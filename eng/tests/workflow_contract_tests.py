"""Structural contracts for release-critical GitHub Actions workflows."""

from __future__ import annotations

import copy
import json
import re
import sys
from pathlib import Path

try:
    from ruamel.yaml import YAML
except ImportError as error:
    raise SystemExit(
        "ruamel.yaml 0.18.16 is required; install with "
        "'python -m pip install ruamel.yaml==0.18.16'."
    ) from error


ROOT = Path(__file__).resolve().parents[2]
WORKFLOWS = ROOT / ".github" / "workflows"
LYCHEE = ROOT / "lychee.toml"
FILES = {
    name: WORKFLOWS / name
    for name in (
        "ci.yml",
        "codeql.yml",
        "dependency-review.yml",
        "reusable-release-validation.yml",
        "publish-nuget.yml",
    )
}
SHA_REF = re.compile(r"^[^@\s]+@[0-9a-f]{40}$")
SELF_HOSTED_WINDOWS = ["self-hosted", "Windows", "X64"]
SELF_HOSTED_WINDOWS_JSON = '["self-hosted","Windows","X64"]'
SAME_REPOSITORY_PR_GUARD = (
    "github.event_name != 'pull_request' || "
    "github.event.pull_request.head.repo.full_name == github.repository"
)
PULL_REQUEST_SAME_REPOSITORY_GUARD = "github.event.pull_request.head.repo.full_name == github.repository"
CLEANUP_SAME_REPOSITORY_GUARD = (
    "always() && (github.event_name != 'pull_request' || "
    "github.event.pull_request.head.repo.full_name == github.repository)"
)
CLEANUP_PULL_REQUEST_GUARD = (
    "always() && github.event_name == 'pull_request' && "
    "github.event.pull_request.head.repo.full_name == github.repository"
)
CI_VALIDATION_RUNNER_INPUT = (
    "${{ github.event_name == 'pull_request' && "
    "'[\"self-hosted\",\"Windows\",\"X64\"]' || "
    "'[\"ubuntu-latest\"]' }}"
)
CI_WINDOWS_RUNNER = (
    "${{ github.event_name == 'pull_request' && "
    "fromJSON('[\"self-hosted\",\"Windows\",\"X64\"]') || "
    "'windows-latest' }}"
)
CODEQL_RUNNER = (
    "${{ github.event_name == 'pull_request' && "
    "fromJSON('[\"self-hosted\",\"Windows\",\"X64\"]') || "
    "'ubuntu-latest' }}"
)
HOSTING_NAME = "${{ matrix.os == 'self-hosted' && 'Windows' || matrix.os }}"
HOSTING_RUNNER = (
    "${{ matrix.os == 'self-hosted' && "
    "fromJSON('[\"self-hosted\",\"Windows\",\"X64\"]') || matrix.os }}"
)
HOSTING_MATRIX = (
    "${{ fromJSON(github.event_name == 'pull_request' && "
    "'{\"os\":[\"self-hosted\"]}' || "
    "'{\"os\":[\"ubuntu-latest\",\"windows-latest\"]}') }}"
)
LYCHEE_URL = (
    "https://github.com/lycheeverse/lychee/releases/download/"
    "lychee-v0.21.0/lychee-x86_64-windows.exe"
)
LYCHEE_SHA256 = "a1784c32c63ba46dccef0698ddf6be82a83a7d0455b0fd772423d601e3c70ab4"
NATIVE_FAIL_FAST_GUARD = "if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }"
REPOSITORY_CHECKS_PROFILE_COMMAND = (
    "dotnet run --project eng/SmartPipe.RepositoryChecks/SmartPipe.RepositoryChecks.csproj "
    "--configuration Release --no-build -- verify --profile sp220-05 "
    "--format github --failures-only"
)


def require(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)


def require_self_hosted_windows(job: dict, label: str) -> None:
    require(job.get("runs-on") == SELF_HOSTED_WINDOWS,
            f"{label} must target the self-hosted Windows X64 runner labels.")


def require_parameterized_runner(job: dict, label: str) -> None:
    require(job.get("runs-on") == "${{ fromJSON(inputs.runner-labels) }}",
            f"{label} must use the runner-labels workflow input.")


def require_runner_expression(job: dict, expected: str, label: str) -> None:
    require(job.get("runs-on") == expected,
            f"{label} must use the event-aware runner expression.")


def require_same_repository_pr_guard(job: dict, label: str, allow_non_pr: bool = True) -> None:
    expected = SAME_REPOSITORY_PR_GUARD if allow_non_pr else PULL_REQUEST_SAME_REPOSITORY_GUARD
    require(job.get("if") == expected,
            f"{label} must use the same-repository pull_request guard.")


def assert_cleanup_job(
    workflow: dict,
    workflow_name: str,
    expected_needs: list[str],
    expected_guard: str,
) -> None:
    job = workflow["jobs"].get("cleanup-self-hosted")
    require(isinstance(job, dict),
            f"{workflow_name} must define cleanup-self-hosted.")
    require(job.get("name") == "Cleanup self-hosted workspace",
            f"{workflow_name} cleanup must preserve its check name.")
    require(job.get("needs") == expected_needs,
            f"{workflow_name} cleanup must wait for every workflow job.")
    require_self_hosted_windows(job, f"{workflow_name} cleanup")
    require(job.get("if") == expected_guard,
            f"{workflow_name} cleanup must always run only for trusted repository work.")
    cleanup_steps = steps(job, f"{workflow_name} cleanup")
    require(len(cleanup_steps) == 1,
            f"{workflow_name} cleanup must contain exactly one cleanup step.")
    cleanup = named_step(cleanup_steps, "Cleanup generated outputs")
    require(cleanup.get("shell") == "pwsh",
            f"{workflow_name} cleanup must use PowerShell on Windows.")
    script = str(cleanup.get("run", ""))
    for token in (
        "$env:GITHUB_WORKSPACE", "[IO.Path]::GetFullPath", "StartsWith",
        "[StringComparison]::OrdinalIgnoreCase", "[IO.FileAttributes]::ReparsePoint",
        "Join-Path $workspace 'artifacts'",
        "Join-Path $workspace 'BenchmarkDotNet.Artifacts'",
        "$directory.Name -in 'bin', 'obj'",
        "Remove-Item -LiteralPath $fullPath -Recurse -Force",
    ):
        require(token in script,
                f"{workflow_name} cleanup must enforce safe workspace-bound deletion ({token}).")
    require("git clean" not in script.lower(),
            f"{workflow_name} cleanup must not use git clean.")
    require(re.search(r"Remove-Item\s+-LiteralPath\s+\$workspace(?:\s|$)", script) is None,
            f"{workflow_name} cleanup must not delete the workspace root.")
    direct_reparse_guard = (
        "if ((Get-Item -LiteralPath $fullPath -Force).Attributes -band "
        "[IO.FileAttributes]::ReparsePoint)"
    )
    require(direct_reparse_guard in script,
            f"{workflow_name} cleanup must reject direct target reparse points before recursive deletion.")
    require(script.index(direct_reparse_guard) < script.index(
        "Get-ChildItem -LiteralPath $fullPath -Force -Recurse"),
            f"{workflow_name} cleanup must check direct target reparse points before recursion.")


def assert_reusable_windows_shell_contract(reusable_steps: list[dict]) -> None:
    release_version = named_step(reusable_steps, "Test release version validation")
    release_run = str(release_version.get("run", ""))
    require(release_version.get("shell") == "pwsh"
            and r"C:\Program Files\Git\bin\bash.exe" in release_run
            and "Test-Path" in release_run
            and "$IsWindows" in release_run
            and "-lc 'eng/tests/validate-release-version.Tests.sh'" in release_run
            and "bash eng/tests/validate-release-version.Tests.sh" in release_run,
            "Release version validation must use verified Git Bash on Windows and bash on hosted Linux.")
    repeat = named_step(reusable_steps, "PR concurrency regression repeat")
    repeat_run = str(repeat.get("run", ""))
    require(repeat.get("shell") == "pwsh" and "foreach ($pass in 1..10)" in repeat_run,
            "PR concurrency repeat must use the Windows PowerShell loop.")
    require("tests/SmartPipe.Extensions.Channels.Tests/SmartPipe.Extensions.Channels.Tests.csproj" in repeat_run,
            "PR concurrency repeat must exercise the extracted Channels package.")

    leaf_tests = named_step(reusable_steps, "SP220-07 leaf tests")
    leaf_run = str(leaf_tests.get("run", ""))
    for project in ("Channels", "Transforms", "Logging", "DataAnnotations"):
        project_path = f"tests/SmartPipe.Extensions.{project}.Tests/SmartPipe.Extensions.{project}.Tests.csproj"
        require(project_path in leaf_run,
                f"SP220-07 leaf test step must execute {project_path}.")
    require(leaf_tests.get("shell") == "pwsh"
            and "$projects = @(" in leaf_run
            and "foreach ($project in $projects)" in leaf_run
            and "if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }" in leaf_run,
            "SP220-07 leaf tests must fail immediately after every failed project test.")
    package_version = named_step(reusable_steps, "Set package version")
    package_run = str(package_version.get("run", ""))
    require(package_version.get("shell") == "pwsh"
            and "$env:REQUESTED_PACKAGE_VERSION" in package_run
            and "$env:GITHUB_ENV" in package_run,
            "Package version setup must use PowerShell environment handling.")
    for name in ("Vulnerable package scan", "Deprecated package scan", "Outdated package report", "Docs link check (Windows)"):
        require(named_step(reusable_steps, name).get("shell") == "pwsh",
                f"{name} must use PowerShell on Windows.")
    require(not any(step.get("shell") == "bash" for step in reusable_steps),
            "Reusable Windows validation must not depend on an implicit Bash shell.")
    package_command_steps = [step for step in reusable_steps
                             if "--package-version" in str(step.get("run", ""))
                             or "--tag \"v" in str(step.get("run", ""))]
    package_commands = [str(step.get("run", "")) for step in package_command_steps]
    require(package_commands and all("$env:PACKAGE_VERSION" in command for command in package_commands),
            "Reusable package commands must read PACKAGE_VERSION from the PowerShell environment.")
    require(all(step.get("shell") == "pwsh" for step in package_command_steps),
            "Reusable package commands must use PowerShell on hosted Linux and Windows.")


def assert_multiline_native_fail_fast_contract(reusable_steps: list[dict]) -> None:
    for name in (
        "Extensions correctness regressions",
        "PR concurrency regression repeat",
        "SP220-07 leaf tests",
        "Test and benchmark warning gate",
    ):
        step = named_step(reusable_steps, name)
        require(step.get("shell") == "pwsh",
                f"{name} must use PowerShell for native command failure handling.")
        lines = [line.strip() for line in str(step.get("run", "")).splitlines()
                 if line.strip()]
        dotnet_indexes = [index for index, line in enumerate(lines)
                          if line.startswith("dotnet ")]
        require(dotnet_indexes,
                f"{name} must contain native dotnet commands.")
        for index in dotnet_indexes:
                    require(index + 1 < len(lines) and lines[index + 1] == NATIVE_FAIL_FAST_GUARD,
                    f"Every dotnet command in {name} must immediately fail on nonzero LASTEXITCODE.")


def assert_all_multiline_native_fail_fast_contract(documents: dict[str, dict]) -> None:
    for workflow_name, workflow in documents.items():
        if workflow_name not in {
            "ci.yml",
            "codeql.yml",
            "dependency-review.yml",
            "reusable-release-validation.yml",
        }:
            continue
        for job_name, job in workflow.get("jobs", {}).items():
            job_steps = job.get("steps")
            if not isinstance(job_steps, list):
                continue
            for step in job_steps:
                lines = [line.strip() for line in str(step.get("run", "")).splitlines()
                         if line.strip()]
                if len(lines) < 2:
                    continue
                dotnet_indexes = [index for index, line in enumerate(lines)
                                  if line.startswith("dotnet ")]
                if not dotnet_indexes:
                    continue
                label = f"{workflow_name}:{job_name}:{step.get('name', '<unnamed>')}"
                require(step.get("shell") == "pwsh",
                        f"Multiline native block {label} must use explicit PowerShell.")
                for index in dotnet_indexes:
                    require(index + 1 < len(lines) and lines[index + 1] == NATIVE_FAIL_FAST_GUARD,
                            f"Every dotnet command in multiline {label} must immediately fail on nonzero LASTEXITCODE.")


def assert_lychee_contract(reusable_steps: list[dict]) -> None:
    linux = named_step(reusable_steps, "Docs link check")
    require(linux.get("if") == "runner.os != 'Windows'"
            and linux.get("uses") == "lycheeverse/lychee-action@a8c4c7cb88f0c7386610c35eb25108e448569cb0",
            "Linux Docs link check must retain the pinned Lychee action.")
    windows = named_step(reusable_steps, "Docs link check (Windows)")
    run = str(windows.get("run", ""))
    require(windows.get("if") == "runner.os == 'Windows'"
            and windows.get("shell") == "pwsh"
            and LYCHEE_URL in run and LYCHEE_SHA256 in run
            and "Get-FileHash" in run and "SHA256" in run,
            "Windows Docs link check must download the pinned Lychee binary and verify SHA256.")
    lychee_text = json.dumps({"linux": linux, "windows": windows})
    require("GITHUB_TOKEN" not in lychee_text and "github-token" not in lychee_text.lower(),
            "Docs link check must not expose or require GITHUB_TOKEN.")
    require("lycheeverse/lychee-action" not in run,
            "Windows Docs link check must not use the hosted Lychee action.")


def load_workflows() -> dict[str, dict]:
    yaml = YAML(typ="safe", pure=True)
    yaml.version = (1, 2)
    documents = {}
    for name, path in FILES.items():
        with path.open(encoding="utf-8") as stream:
            document = yaml.load(stream)
        require(isinstance(document, dict), f"{name} must contain a YAML mapping.")
        require(isinstance(document.get("jobs"), dict), f"{name} must define a jobs mapping.")
        documents[name] = document
    return documents


def steps(job: dict, job_name: str) -> list[dict]:
    value = job.get("steps")
    require(isinstance(value, list), f"{job_name} must define a steps list.")
    require(all(isinstance(step, dict) for step in value), f"{job_name} steps must be mappings.")
    return value


def named_step(job_steps: list[dict], name: str) -> dict:
    matches = [step for step in job_steps if step.get("name") == name]
    require(len(matches) == 1, f"Expected exactly one step named '{name}'.")
    return matches[0]


def runs(job_steps: list[dict]) -> list[str]:
    return [str(step["run"]) for step in job_steps if "run" in step]


def command_index(commands: list[str], token: str) -> int:
    matches = [index for index, command in enumerate(commands) if token in command]
    require(len(matches) == 1, f"Expected exactly one command containing '{token}'.")
    return matches[0]


def assert_repository_checks_profile(
    reusable_steps: list[dict],
    build_step: dict,
    repository_test_step: dict,
) -> None:
    profile_steps = [step for step in reusable_steps
                     if step.get("name") == "Verify RepositoryChecks profile"]
    require(len(profile_steps) == 1,
            "Reusable validation must define exactly one step named "
            "'Verify RepositoryChecks profile'.")
    profile_step = profile_steps[0]
    profile_run = " ".join(str(profile_step.get("run", "")).split())
    profile_invocations = [
        " ".join(str(step.get("run", "")).split())
        for step in reusable_steps
        if "-- verify --profile " in str(step.get("run", ""))
    ]
    require(len(profile_invocations) == 1,
            "Reusable validation must invoke exactly one RepositoryChecks profile.")
    require(profile_run == REPOSITORY_CHECKS_PROFILE_COMMAND,
            "Reusable validation must use the exact sp220-05 profile command.")
    require(profile_invocations[0] == REPOSITORY_CHECKS_PROFILE_COMMAND,
            "Reusable validation must invoke only the exact sp220-05 profile command.")

    profile_index = reusable_steps.index(profile_step)
    require(reusable_steps.index(build_step) < profile_index,
            "RepositoryChecks profile must run after Build because it uses --no-build.")
    require(profile_index < reusable_steps.index(repository_test_step),
            "RepositoryChecks profile must run before Repository baseline contract tests.")

    release_gate_names = (
        "Test and benchmark warning gate", "Pack packages from graph",
        "Provision 2.1.2 baseline packages", "Verify package graph current",
        "Verify package metadata current", "Verify package ownership current",
        "Verify release versions current",         "Run current consumers",
        "Run Hosting consumers", "Run HealthChecks consumers",
        "Run OpenTelemetry consumers",
        "Vulnerable package scan", "Verify direct production audit policy",
        "Deprecated package scan", "Outdated package report",
        "Upload immutable packages and reports",
    )
    for name in release_gate_names:
        require(profile_index < reusable_steps.index(named_step(reusable_steps, name)),
                f"RepositoryChecks profile must run before {name}.")

    reusable_runs = runs(reusable_steps)
    require(not any("verify-central-packages" in command or
                    "verify-package-projects" in command
                    for command in reusable_runs),
            "RepositoryChecks profile must replace the duplicate central/package project steps.")


def assert_baseline_lane(job: dict, label: str) -> None:
    job_steps = steps(job, label)
    repository_tests = named_step(job_steps, "Repository baseline contract tests")
    test_run = " ".join(str(repository_tests.get("run", "")).split())
    expected_test = ("dotnet test --project "
                     "tests/SmartPipe.RepositoryChecks.Tests/SmartPipe.RepositoryChecks.Tests.csproj "
                     "--configuration Release --no-build --minimum-expected-tests 1")
    require(test_run == expected_test,
            f"{label} repository tests must set --minimum-expected-tests 1.")

    checkouts = [step for step in job_steps
                 if str(step.get("uses", "")).startswith("actions/checkout")]
    require(len(checkouts) == 1
            and checkouts[0].get("with", {}).get("fetch-depth") == 0,
            f"{label} baseline verification checkout must fetch full Git history.")
    require(not any(step.get("name") == "Verify SP220-00 scope" for step in job_steps),
            f"{label} must not run the completed SP220-00 production freeze.")

    provision = named_step(job_steps, "Provision 2.1.2 baseline packages")
    offline = named_step(job_steps, "Verify 2.1.2 baseline offline")
    provision_steps = [step for step in job_steps
                       if "provision-baseline" in str(step.get("run", ""))]
    verify_steps = [step for step in job_steps
                    if "verify-baseline" in str(step.get("run", ""))]
    require(len(provision_steps) == 1 and len(verify_steps) == 1,
            f"{label} must run exactly one baseline provisioning and one offline verification.")
    provision_run = " ".join(str(provision.get("run", "")).split())
    offline_run = " ".join(str(offline.get("run", "")).split())
    expected_provision = ("dotnet run --project eng/SmartPipe.RepositoryChecks/SmartPipe.RepositoryChecks.csproj "
                          "--configuration Release --no-build -- provision-baseline --repo-root . "
                          "--manifest eng/baselines/2.1.2/manifest.json "
                          "--packages-dir artifacts/baselines/2.1.2")
    expected_verify = ("dotnet run --project eng/SmartPipe.RepositoryChecks/SmartPipe.RepositoryChecks.csproj "
                       "--configuration Release --no-build -- verify-baseline --repo-root . "
                       "--manifest eng/baselines/2.1.2/manifest.json "
                       "--packages-dir artifacts/baselines/2.1.2 --offline --mode integrity")
    forbidden = ("curl", "wget", "Invoke-WebRequest", "http://", "https://")
    require(not any(token.lower() in offline_run.lower() for token in forbidden),
            f"{label} offline baseline step must not be network-capable.")
    require(provision_run == expected_provision,
            f"{label} provisioning command must acquire the exact manifest packages.")
    require(offline_run == expected_verify,
            f"{label} offline baseline command must verify the exact manifest offline.")
    require(job_steps.index(provision) < job_steps.index(offline),
            f"{label} offline baseline verification must run after online provisioning.")


def assert_immutable_action_refs(documents: dict[str, dict]) -> None:
    for file_name, document in documents.items():
        for job_name, job in document["jobs"].items():
            if "uses" in job and not str(job["uses"]).startswith("./"):
                require(SHA_REF.fullmatch(str(job["uses"])) is not None,
                        f"{file_name}:{job_name} must pin third-party reusable actions to a full SHA.")
            for step in job.get("steps", []):
                action = step.get("uses")
                if action and not str(action).startswith("./"):
                    require(SHA_REF.fullmatch(str(action)) is not None,
                            f"{file_name}:{job_name} action '{action}' must use a full commit SHA.")


def assert_persist_credentials_disabled(documents: dict[str, dict]) -> None:
    for file_name, document in documents.items():
        for job_name, job in document["jobs"].items():
            for step in job.get("steps", []):
                action = str(step.get("uses", ""))
                if action.startswith("actions/checkout"):
                    with_block = step.get("with")
                    require(isinstance(with_block, dict)
                            and with_block.get("persist-credentials") is False,
                            f"{file_name}:{job_name} read-only checkout must set "
                            "persist-credentials: false.")


def assert_setup_dotnet_uses_global_json(documents: dict[str, dict]) -> None:
    setup_steps = []
    for file_name, document in documents.items():
        for job_name, job in document["jobs"].items():
            for step in job.get("steps", []):
                if str(step.get("uses", "")).startswith("actions/setup-dotnet"):
                    setup_steps.append((file_name, job_name, step))

    require(bool(setup_steps), "Release workflows must contain setup-dotnet steps.")
    for file_name, job_name, step in setup_steps:
        with_block = step.get("with")
        require(isinstance(with_block, dict)
                and with_block.get("global-json-file") == "global.json",
                f"{file_name}:{job_name} setup-dotnet must use global.json as the SDK source.")
        require("dotnet-version" not in with_block,
                f"{file_name}:{job_name} setup-dotnet must not duplicate the SDK version.")


def assert_link_check_exclusion_scoped() -> None:
    require(LYCHEE.is_file(), "lychee.toml must exist to scope the docs link check exclusion.")
    try:
        import tomllib
    except ModuleNotFoundError:  # pragma: no cover - Python < 3.11
        import tomli as tomllib  # type: ignore
    with LYCHEE.open("rb") as stream:
        config = tomllib.load(stream)
    require(isinstance(config, dict), "lychee.toml must contain a TOML mapping.")
    exclude = config.get("exclude")
    require(isinstance(exclude, list) and len(exclude) == 1,
            "lychee.toml must exclude exactly one URL.")
    target = r"^https://www\.nuget\.org/packages/SmartPipe\.Extensions\.Json/?$"
    require(exclude[0] == target,
            "lychee.toml exclusion must be scoped to the single pre-release "
            "SmartPipe.Extensions.Json URL.")
    require(not any("nuget.org" in pattern and pattern != target for pattern in exclude),
            "lychee.toml must not contain a broad nuget.org exclusion.")


def assert_consumer_contract() -> None:
    manifest_path = ROOT / "eng" / "consumer-scenarios.json"
    document = json.loads(manifest_path.read_text(encoding="utf-8"))
    current = [scenario for scenario in document["scenarios"] if scenario["set"] == "current"]
    expected = {
        "core-direct", "json-direct", "extensions-meta", "legacy-binary-2.1.2",
        "core-trim", "core-nativeaot", "json-nativeaot",
        "dependency-injection-direct", "dependency-injection-keyed",
        "dependency-injection-from-keyed-services", "dependency-injection-facade-source",
        "dependency-injection-facade-binary-2.1.2", "dependency-injection-trim",
        "dependency-injection-nativeaot",
        "hosting-direct", "hosting-facade-source", "hosting-facade-binary-2.1.2",
        "hosting-trim", "hosting-nativeaot",
        "health-checks-direct", "health-checks-aspnet", "health-checks-trim",
        "health-checks-nativeaot",
        "opentelemetry-direct", "opentelemetry-otlp", "opentelemetry-facade",
        "opentelemetry-trim", "opentelemetry-nativeaot",
        "channels-direct", "transforms-direct", "logging-direct", "data-annotations-direct",
        "data-annotations-runtime",
    }
    require(len(current) == 33 and {scenario["id"] for scenario in current} == expected,
            "Current consumer set must contain the exact thirty-three scenarios.")
    hosting = [scenario for scenario in current if scenario.get("category") == "hosting"]
    require({scenario["id"] for scenario in hosting} == {
        "hosting-direct", "hosting-facade-source", "hosting-facade-binary-2.1.2",
        "hosting-trim", "hosting-nativeaot",
    }, "Hosting consumer category must contain the exact five Hosting scenarios.")
    health_checks = [scenario for scenario in current if scenario.get("category") == "health-checks"]
    require({scenario["id"] for scenario in health_checks} == {
        "health-checks-direct", "health-checks-aspnet", "health-checks-trim",
        "health-checks-nativeaot",
    }, "HealthChecks consumer category must contain the exact four HealthChecks scenarios.")
    opentelemetry = [scenario for scenario in current if scenario.get("category") == "opentelemetry"]
    require({scenario["id"] for scenario in opentelemetry} == {
        "opentelemetry-direct", "opentelemetry-otlp", "opentelemetry-facade",
        "opentelemetry-trim", "opentelemetry-nativeaot",
    }, "OpenTelemetry consumer category must contain the exact five OpenTelemetry scenarios.")
    meta = next(scenario for scenario in current if scenario["id"] == "extensions-meta")
    require(meta["packageIds"] == ["SmartPipe.Extensions"],
            "extensions-meta must directly reference only the facade package.")
    source = (ROOT / "tests" / "Consumers" / "Scenarios"
              / "extensions-meta" / "Program.cs").read_text(encoding="utf-8")
    require("new MapsterTransform<DefaultSource, DefaultDestination>()" in source
            and "new TypeAdapterConfig()" in source
            and "ConfiguredDestination" in source,
            "extensions-meta must assert default and configured Mapster mappings.")



def validate(documents: dict[str, dict]) -> None:
    reusable = documents["reusable-release-validation.yml"]
    ci = documents["ci.yml"]
    codeql = documents["codeql.yml"]
    dependency_review = documents["dependency-review.yml"]
    publish = documents["publish-nuget.yml"]

    for workflow_name, workflow in (
        ("ci.yml", ci),
        ("codeql.yml", codeql),
        ("dependency-review.yml", dependency_review),
    ):
        branches = workflow.get("on", {}).get("pull_request", {}).get("branches", [])
        require("sp220/checkpoint-c" in branches,
                f"{workflow_name} pull_request must include sp220/checkpoint-c.")
        require("sp220/checkpoint-d" in branches,
                f"{workflow_name} pull_request must include sp220/checkpoint-d.")

    for event in ("push", "pull_request"):
        branches = ci.get("on", {}).get(event, {}).get("branches", [])
        require("release/2.2.0" in branches,
                f"CI {event} must include release/2.2.0.")

    expected_triggers = {
        "ci.yml": {
            "workflow_dispatch": None,
            "push": {"branches": ["main", "upd", "release/2.2.0"]},
            "pull_request": {
                "branches": ["main", "upd", "release/2.2.0", "sp220/checkpoint-c", "sp220/checkpoint-d"]
            },
        },
        "codeql.yml": {
            "push": {"branches": ["main", "upd", "release/2.2.0"]},
            "pull_request": {"branches": ["main", "release/2.2.0", "sp220/checkpoint-c", "sp220/checkpoint-d"]},
            "schedule": [{"cron": "27 3 * * 1"}],
        },
        "dependency-review.yml": {
            "pull_request": {"branches": ["main", "release/2.2.0", "sp220/checkpoint-c", "sp220/checkpoint-d"]},
        },
    }
    for workflow_name, expected in expected_triggers.items():
        require(documents[workflow_name].get("on") == expected,
                f"{workflow_name} trigger contract changed.")

    workflow_call = reusable.get("on", {}).get("workflow_call")
    require(isinstance(workflow_call, dict), "Reusable validation must declare on.workflow_call.")
    runner_input = workflow_call.get("inputs", {}).get("runner-labels")
    require(runner_input == {
        "description": "Runner labels as a JSON array",
        "required": False,
        "type": "string",
        "default": '["ubuntu-latest"]',
    }, "Reusable validation must default runner-labels to hosted Linux.")
    reusable_job = reusable["jobs"].get("build-test-pack")
    require(isinstance(reusable_job, dict), "Reusable validation must define build-test-pack.")
    require_parameterized_runner(reusable_job, "Reusable build-test-pack")
    require_same_repository_pr_guard(reusable_job, "Reusable build-test-pack")
    reusable_steps = steps(reusable_job, "reusable build-test-pack")
    reusable_runs = runs(reusable_steps)
    require(named_step(reusable_steps, "Test workflow contracts").get("run") ==
            "./eng/tests/workflow-contract.Tests.ps1",
            "Reusable validation must execute the workflow contract test.")
    assert_reusable_windows_shell_contract(reusable_steps)
    assert_multiline_native_fail_fast_contract(reusable_steps)
    assert_all_multiline_native_fail_fast_contract(documents)
    assert_lychee_contract(reusable_steps)
    require(any("ruamel.yaml==0.18.16" in command for command in reusable_runs),
            "Reusable validation must install the pinned YAML 1.2 parser.")
    restores = [command for command in reusable_runs if "dotnet restore SmartPipe.Core.slnx" in command]
    require(restores == ["dotnet restore SmartPipe.Core.slnx --locked-mode"],
            "Reusable validation must perform exactly one locked-mode solution restore.")
    build_step = named_step(reusable_steps, "Build")
    repository_test_step = named_step(reusable_steps, "Repository baseline contract tests")
    di_test_step = named_step(reusable_steps, "Dependency Injection tests")
    di_test_run = " ".join(str(di_test_step.get("run", "")).split())
    expected_di_test = ("dotnet test --project "
                       "tests/SmartPipe.Extensions.DependencyInjection.Tests/"
                       "SmartPipe.Extensions.DependencyInjection.Tests.csproj "
                       "--configuration Release --no-build --minimum-expected-tests 1")
    require(di_test_run == expected_di_test,
            "Reusable validation must run the exact DI test project command.")
    hosting_test = " ".join(str(named_step(reusable_steps, "Hosting tests").get("run", "")).split())
    require(hosting_test == (
        "dotnet test --project tests/SmartPipe.Extensions.Hosting.Tests/"
        "SmartPipe.Extensions.Hosting.Tests.csproj --configuration Release --no-build "
        "--minimum-expected-tests 1"),
        "Reusable validation must run the complete Hosting test project.")
    health_checks_test = " ".join(str(named_step(reusable_steps, "HealthChecks tests").get("run", "")).split())
    require(health_checks_test == (
        "dotnet test --project tests/SmartPipe.Extensions.HealthChecks.Tests/"
        "SmartPipe.Extensions.HealthChecks.Tests.csproj --configuration Release --no-build "
        "--minimum-expected-tests 1"),
        "Reusable validation must run the complete HealthChecks test project with the MTP non-empty gate.")
    hosting_regressions = str(named_step(reusable_steps, "Hosting lifecycle regressions").get("run", ""))
    require("--filter-query /[Category=HostingLifecycle]" in hosting_regressions,
            "Reusable validation must run the Hosting lifecycle regression category.")
    require(reusable_steps.index(build_step) < reusable_steps.index(repository_test_step),
            "Reusable repository baseline tests must run after Build.")
    require(reusable_steps.index(build_step) < reusable_steps.index(di_test_step),
            "Reusable DI tests must run after Build.")
    assert_repository_checks_profile(reusable_steps, build_step, repository_test_step)

    assert_baseline_lane(reusable_job, "Reusable Windows baseline lane")

    required_steps = (
        "Verify RepositoryChecks profile",
        "Format verify", "Build", "Repository baseline contract tests",
        "Core tests with coverage", "Core stress tests",
        "Extensions tests", "SP220-07 leaf tests", "Dependency Injection tests", "HealthChecks tests", "Hosting lifecycle regressions", "Hosting tests",
        "JSON Extensions tests", "Core correctness regressions",
        "Core concurrency regressions", "Extensions correctness regressions",
        "PR concurrency regression repeat", "Test and benchmark warning gate",
        "Pack packages from graph", "Provision 2.1.2 baseline packages",
        "Verify package graph current", "Verify package metadata current",
        "Verify package ownership current", "Verify release versions current",
        "Run current consumers", "Run Hosting consumers", "Run HealthChecks consumers",
        "Run OpenTelemetry consumers", "Vulnerable package scan",
        "Verify direct production audit policy", "Deprecated package scan",
        "Outdated package report", "Docs link check", "Docs link check (Windows)",
        "Upload immutable packages and reports",
    )
    for name in required_steps:
        named_step(reusable_steps, name)
    gate_order = [
        "Restore locked", "Build", "Verify RepositoryChecks profile",
        "Test and benchmark warning gate", "Pack packages from graph",
        "Provision 2.1.2 baseline packages", "Verify package graph current",
        "Verify package metadata current", "Verify package ownership current",
        "Verify release versions current", "Run current consumers", "Run HealthChecks consumers",
        "Run OpenTelemetry consumers", "Vulnerable package scan", "Verify direct production audit policy",
        "Deprecated package scan", "Outdated package report",
        "Upload immutable packages and reports",
    ]
    gate_indexes = [reusable_steps.index(named_step(reusable_steps, name)) for name in gate_order]
    require(gate_indexes == sorted(gate_indexes),
            "Reusable package gates must follow the required order.")
    reusable_text = "\n".join(reusable_runs)
    pack_run = str(named_step(reusable_steps, "Pack packages from graph").get("run", ""))
    for token in ("pack-packages", "--mode current", "--configuration Release",
                  "--package-version", "--output artifacts/packages",
                  "--manifest artifacts/packages/manifest.json"):
        require(token in pack_run, f"Graph-driven pack step must contain '{token}'.")
    require(reusable_text.count("pack-packages") == 1,
            "Reusable validation must invoke pack-packages exactly once.")
    hosting_consumers = str(named_step(reusable_steps, "Run Hosting consumers").get("run", ""))
    require("run-consumers" in hosting_consumers and "--category hosting" in hosting_consumers,
            "Reusable validation must execute the Hosting consumer category.")
    health_checks_consumers = str(named_step(reusable_steps, "Run HealthChecks consumers").get("run", ""))
    require("run-consumers" in health_checks_consumers and "--category health-checks" in health_checks_consumers,
            "Reusable validation must execute the HealthChecks consumer category.")
    opentelemetry_consumers = str(named_step(reusable_steps, "Run OpenTelemetry consumers").get("run", ""))
    require("run-consumers" in opentelemetry_consumers and "--category opentelemetry" in opentelemetry_consumers,
            "Reusable validation must execute the OpenTelemetry consumer category.")
    concurrency_job = reusable["jobs"].get("health-checks-concurrency")
    require(isinstance(concurrency_job, dict),
            "Reusable validation must define the HealthChecks concurrency OS matrix.")
    require_parameterized_runner(concurrency_job, "HealthChecks concurrency")
    require_same_repository_pr_guard(concurrency_job, "HealthChecks concurrency")
    require("strategy" not in concurrency_job,
            "HealthChecks concurrency must use one self-hosted Windows lane while Linux hosted minutes are unavailable.")
    concurrency_steps = steps(concurrency_job, "reusable health-checks-concurrency")
    for step_name in ("Run bounded observation concurrency", "Run concurrent health evaluation"):
        command = str(named_step(concurrency_steps, step_name).get("run", ""))
        require("--minimum-expected-tests 1" in command,
                f"{step_name} must fail when its MTP filter selects zero tests.")
    require(re.search(r"\\bdotnet\\s+pack\\b", reusable_text) is None,
            "Reusable validation must not hard-code dotnet pack commands.")
    require("validate-json-package-split.ps1" not in reusable_text,
            "Reusable validation must not call the legacy package split validator.")
    for forbidden in ("dotnet new", "Program.cs", "<<'CS'", "<<CS", "<<'XML'", "<<XML"):
        require(forbidden not in reusable_text,
                f"Reusable validation must not generate inline projects ({forbidden}).")
    ownership_run = str(named_step(reusable_steps, "Verify package ownership current").get("run", ""))
    require("--baseline eng/baselines/2.1.2" in ownership_run,
            "Ownership verification must use snapshot metadata from eng/baselines/2.1.2.")
    audit_reports = {
        "Vulnerable package scan": "artifacts/audit/vulnerable.json",
        "Deprecated package scan": "artifacts/audit/deprecated.json",
        "Outdated package report": "artifacts/audit/outdated.json",
    }
    for name, report in audit_reports.items():
        command = str(named_step(reusable_steps, name).get("run", ""))
        require("dotnet package list --project SmartPipe.Core.slnx" in command
                and "--format json" in command and "--output-version 1" in command
                and report in command,
                f"{name} must save a machine-readable JSON report.")
    audit_policy_run = str(named_step(reusable_steps, "Verify direct production audit policy").get("run", ""))
    require("verify-nuget-audit" in audit_policy_run
            and "--report artifacts/audit/vulnerable.json" in audit_policy_run,
            "Reusable validation must enforce the direct production audit policy from the vulnerable JSON report.")
    upload = named_step(reusable_steps, "Upload immutable packages and reports")
    require(upload.get("with", {}).get("name") == "${{ inputs.artifact-name }}",
            "Reusable validation must upload the caller-selected artifact name.")
    upload_path = str(upload.get("with", {}).get("path", ""))
    require("artifacts/packages" in upload_path
            and "artifacts/consumers/**/result.json" in upload_path
            and "artifacts/audit" in upload_path
            and "eng/package-graph.json" in upload_path,
            "Reusable validation must upload package graph, packages, consumer reports, and audit reports together.")
    consumer_paths = [line.strip() for line in upload_path.splitlines()
                      if line.strip().startswith("artifacts/consumers/")]
    require(consumer_paths == ["artifacts/consumers/**/result.json"],
            "Reusable validation must keep consumer artifact upload result-only; logs stay local.")

    validation = ci["jobs"].get("validation")
    require(validation == {
        "uses": "./.github/workflows/reusable-release-validation.yml",
        "permissions": {"contents": "read"},
        "if": SAME_REPOSITORY_PR_GUARD,
        "with": {"runner-labels": CI_VALIDATION_RUNNER_INPUT},
    }, "CI validation must be the exact reusable workflow caller with read-only contents permission.")
    pull_request = ci.get("on", {}).get("pull_request", {})
    require("paths-ignore" not in pull_request,
            "CI pull requests must not exclude Hosting package, tests, or docs paths.")
    hosting_integration = ci["jobs"].get("hosting-integration")
    require(isinstance(hosting_integration, dict)
            and hosting_integration.get("name") == f"Hosting integration ({HOSTING_NAME})",
            "CI must preserve the Hosting integration check name across event routes.")
    require_same_repository_pr_guard(hosting_integration, "Hosting integration")
    require_runner_expression(hosting_integration, HOSTING_RUNNER, "Hosting integration")
    hosting_strategy = hosting_integration.get("strategy")
    require(isinstance(hosting_strategy, dict)
            and hosting_strategy.get("fail-fast") is False
            and hosting_strategy.get("matrix") == HOSTING_MATRIX,
            "Hosting integration must use one Windows PR leg and the original hosted non-PR matrix.")
    hosting_steps = steps(hosting_integration, "hosting-integration")
    hosting_runs = runs(hosting_steps)
    integration_run = str(named_step(
        hosting_steps, "Generic Host ordering and cancellation tests").get("run", ""))
    require("--filter-class SmartPipe.Extensions.Hosting.Tests.Integration.GenericHostIntegrationTests"
            in integration_run,
            "Hosting OS matrix must run the real Generic Host integration tests.")
    windows = ci["jobs"].get("json-file-windows")
    require(isinstance(windows, dict), "CI must define the Windows JSON lane.")
    require_runner_expression(windows, CI_WINDOWS_RUNNER, "Windows JSON lane")
    require_same_repository_pr_guard(windows, "Windows JSON lane")
    windows_steps = steps(windows, "json-file-windows")
    windows_runs = runs(windows_steps)
    windows_restores = [command for command in windows_runs if "dotnet restore SmartPipe.Core.slnx" in command]
    require(windows_restores == ["dotnet restore SmartPipe.Core.slnx --locked-mode"],
            "Windows JSON lane must perform exactly one locked-mode solution restore.")
    require(not any("Category=Stress" in command for command in windows_runs),
            "Windows JSON lane must not execute the stress suite.")
    for name in ("Build JSON test project", "JSON file source, path, open, and share tests",
                 "JSON file sink and dispose tests", "Dead-letter source and sink tests",
                 ):
        named_step(windows_steps, name)
    require(not any("dotnet pack" in command or "validate-json-package-split" in command
                    for command in windows_runs),
            "Windows JSON lane must remain package-count agnostic.")

    baseline_windows = ci["jobs"].get("baseline-contract-windows")
    require(isinstance(baseline_windows, dict)
            and baseline_windows.get("name") == "Baseline contract (Windows)",
            "CI must define the uniquely named Windows baseline contract job.")
    require_runner_expression(baseline_windows, CI_WINDOWS_RUNNER, "Windows baseline contract lane")
    require_same_repository_pr_guard(baseline_windows, "Windows baseline contract lane")
    baseline_windows_steps = steps(baseline_windows, "Windows baseline contract lane")
    checkout = baseline_windows_steps[0]
    require(str(checkout.get("uses", "")).startswith("actions/checkout")
            and checkout.get("with", {}).get("persist-credentials") is False,
            "Windows baseline contract checkout must pin SHA and disable credentials.")
    baseline_runs = runs(baseline_windows_steps)
    require("dotnet restore SmartPipe.Core.slnx --locked-mode -p:DisableImplicitLibraryPacksFolder=true" in baseline_runs,
            "Windows baseline contract lane must disable the SDK library-packs source during locked restore.")
    build = named_step(baseline_windows_steps, "Build repository checks")
    require("-warnaserror" in str(build.get("run", "")),
            "Windows baseline contract build must treat warnings as errors.")
    assert_baseline_lane(baseline_windows, "Windows baseline contract lane")

    explicit_names = []
    for file_name, document in documents.items():
        for job_id, job in document["jobs"].items():
            if "name" in job:
                explicit_names.append((str(job["name"]), file_name, job_id))
    duplicates = {name for name, _, _ in explicit_names
                  if sum(item[0] == name for item in explicit_names) > 1}
    duplicates.discard("Cleanup self-hosted workspace")
    require(not duplicates, f"Required job/check names must be unique: {sorted(duplicates)}")

    windows_text = "\n".join(windows_runs)
    require("SmartPipe.Extensions.Json.Tests.Sinks.JsonFileSinkLifecycleTests" in windows_text,
            "Windows lifecycle filter must use the correct "
            "SmartPipe.Extensions.Json.Tests.Sinks namespace.")
    require("SmartPipe.Extensions.Tests.Sinks.JsonFileSinkLifecycleTests" not in windows_text,
            "Windows lifecycle filter must not use the obsolete "
            "SmartPipe.Extensions.Tests.Sinks namespace.")

    assert_cleanup_job(
        ci,
        "ci.yml",
        ["validation", "hosting-integration", "json-file-windows", "baseline-contract-windows"],
        CLEANUP_PULL_REQUEST_GUARD,
    )
    assert_cleanup_job(codeql, "codeql.yml", ["analyze"], CLEANUP_PULL_REQUEST_GUARD)
    assert_cleanup_job(
        dependency_review,
        "dependency-review.yml",
        ["dependency-review"],
        CLEANUP_PULL_REQUEST_GUARD,
    )

    codeql_job = codeql["jobs"].get("analyze")
    require(isinstance(codeql_job, dict), "CodeQL must define the analyze job.")
    require_runner_expression(codeql_job, CODEQL_RUNNER, "CodeQL analyze")
    require_same_repository_pr_guard(codeql_job, "CodeQL analyze")
    dependency_review_job = dependency_review["jobs"].get("dependency-review")
    require(isinstance(dependency_review_job, dict),
            "Dependency Review must define the dependency-review job.")
    require_self_hosted_windows(dependency_review_job, "Dependency Review")
    require_same_repository_pr_guard(dependency_review_job, "Dependency Review", allow_non_pr=False)

    all_runs = windows_runs + hosting_runs + reusable_runs
    filtered = [command for command in all_runs
                if "--filter-class" in command or "--filter-query" in command]
    require(bool(filtered),
            "At least one filtered test command must be present for the contract to be meaningful.")
    for command in filtered:
        require("--minimum-expected-tests 1" in command,
                f"Every filtered test command must set --minimum-expected-tests 1: {command}")

    assert_persist_credentials_disabled(documents)
    assert_setup_dotnet_uses_global_json(documents)
    assert_link_check_exclusion_scoped()
    assert_consumer_contract()

    version = publish["jobs"].get("version")
    validation = publish["jobs"].get("validation")
    publication = publish["jobs"].get("publish")
    require(isinstance(version, dict) and isinstance(validation, dict) and isinstance(publication, dict),
            "Publish workflow must define version, validation, and publish jobs.")
    require(validation.get("needs") == "version", "Publish validation must depend exactly on version.")
    require(validation.get("uses") == "./.github/workflows/reusable-release-validation.yml",
            "Publish validation must call the local reusable workflow.")
    require("runner-labels" not in validation.get("with", {}),
            "Publish validation must use reusable hosted Linux runner default.")
    require(validation.get("with") == {
        "package-version": "${{ needs.version.outputs.package-version }}",
        "artifact-name": "${{ needs.version.outputs.artifact-name }}",
    }, "Publish validation must pass version outputs as the reusable workflow inputs.")
    require(publication.get("needs") == ["version", "validation"],
            "Publish job must depend exactly on version and validation.")
    require(publication.get("environment") == "nuget-production",
            "Publish job must use nuget-production.")
    publish_steps = steps(publication, "publish")
    download = named_step(publish_steps, "Download validated packages")
    require(download.get("with", {}).get("name") == "${{ needs.version.outputs.artifact-name }}",
            "Publish must download the same artifact name produced by validation.")
    publish_runs = runs(publish_steps)
    require(not any("dotnet pack" in command for command in publish_runs), "Publish job must never repack.")
    version_runs = runs(steps(version, "publish version"))
    hard_coded_ids = ("SmartPipe.Core", "SmartPipe.Extensions.Json", "SmartPipe.Extensions")
    require(not any(package_id in command for command in version_runs + publish_runs
                    for package_id in hard_coded_ids),
            "Publish version, push, and availability logic must not hard-code package IDs.")
    require(download.get("with", {}).get("path") == "artifacts",
            "Publish download layout must preserve manifest repository-relative paths.")
    push = named_step(publish_steps, "Publish packages in dependency order")
    push_run = str(push.get("run", ""))
    require("artifacts/packages/manifest.json" in push_run
            and "sort_by(.publishOrder)" in push_run
            and ".nupkgPath" in push_run
            and ".nupkgSha256" in push_run,
            "Publish must read ordered paths and hashes from manifest.json.")
    require("awk '{print tolower($1)}'" in push_run
            and "toupper($1)" not in push_run,
            "Publish must normalize sha256sum output to lowercase before comparing it with the manifest hash.")
    require(push_run.count("dotnet nuget push") == 1,
            "Publish must use one manifest-driven push loop.")
    require("skip_duplicate=(--skip-duplicate)" in push_run,
            "Recoverable rerun must be the only source of --skip-duplicate.")
    availability_run = str(named_step(
        publish_steps, "Verify published package versions are available").get("run", ""))
    require("sort_by(.publishOrder)" in availability_run
            and "[.id, .version]" in availability_run,
            "Availability checks must derive package IDs and versions from manifest.json.")

    assert_immutable_action_refs(documents)


def _revert_windows_lifecycle_namespace(documents: dict[str, dict]) -> None:
    wrong = "SmartPipe.Extensions.Tests.Sinks.JsonFileSinkLifecycleTests"
    correct = "SmartPipe.Extensions.Json.Tests.Sinks.JsonFileSinkLifecycleTests"
    for step in documents["ci.yml"]["jobs"]["json-file-windows"]["steps"]:
        if "run" in step:
            step["run"] = str(step["run"]).replace(correct, wrong)


def _strip_minimum_expected_from_windows(documents: dict[str, dict]) -> None:
    for step in documents["ci.yml"]["jobs"]["json-file-windows"]["steps"]:
        if "run" in step:
            step["run"] = str(step["run"]).replace("--minimum-expected-tests 1", "")


def _restore_floating_sdk_selection(documents: dict[str, dict]) -> None:
    for document in documents.values():
        for job in document["jobs"].values():
            for step in job.get("steps", []):
                if str(step.get("uses", "")).startswith("actions/setup-dotnet"):
                    with_block = step.setdefault("with", {})
                    with_block.pop("global-json-file", None)
                    with_block["dotnet-version"] = "10.0.x"


def _remove_release_branch(documents: dict[str, dict]) -> None:
    branches = documents["ci.yml"]["on"]["pull_request"]["branches"]
    branches.remove("release/2.2.0")


def _remove_ci_checkpoint_branch(documents: dict[str, dict]) -> None:
    branches = documents["ci.yml"]["on"]["pull_request"]["branches"]
    branches.remove("sp220/checkpoint-c")


def _remove_ci_checkpoint_d_branch(documents: dict[str, dict]) -> None:
    branches = documents["ci.yml"]["on"]["pull_request"]["branches"]
    branches.remove("sp220/checkpoint-d")


def _remove_codeql_checkpoint_branch(documents: dict[str, dict]) -> None:
    branches = documents["codeql.yml"]["on"]["pull_request"]["branches"]
    branches.remove("sp220/checkpoint-c")


def _remove_codeql_checkpoint_d_branch(documents: dict[str, dict]) -> None:
    branches = documents["codeql.yml"]["on"]["pull_request"]["branches"]
    branches.remove("sp220/checkpoint-d")


def _remove_dependency_review_checkpoint_branch(documents: dict[str, dict]) -> None:
    branches = documents["dependency-review.yml"]["on"]["pull_request"]["branches"]
    branches.remove("sp220/checkpoint-c")


def _remove_dependency_review_checkpoint_d_branch(documents: dict[str, dict]) -> None:
    branches = documents["dependency-review.yml"]["on"]["pull_request"]["branches"]
    branches.remove("sp220/checkpoint-d")


def _remove_linux_offline_verification(documents: dict[str, dict]) -> None:
    job = documents["reusable-release-validation.yml"]["jobs"]["build-test-pack"]
    job["steps"] = [step for step in job["steps"]
                    if step.get("name") != "Verify 2.1.2 baseline offline"]


def _make_windows_offline_network_capable(documents: dict[str, dict]) -> None:
    job = documents["ci.yml"]["jobs"]["baseline-contract-windows"]
    step = named_step(job["steps"], "Verify 2.1.2 baseline offline")
    step["shell"] = "pwsh"
    step["run"] += f"\n{NATIVE_FAIL_FAST_GUARD}\nInvoke-WebRequest https://example.test"


def _remove_repository_test_minimum(documents: dict[str, dict]) -> None:
    job = documents["reusable-release-validation.yml"]["jobs"]["build-test-pack"]
    step = named_step(job["steps"], "Repository baseline contract tests")
    step["run"] = str(step["run"]).replace("--minimum-expected-tests 1", "")


def _duplicate_required_job_name(documents: dict[str, dict]) -> None:
    documents["ci.yml"]["jobs"]["json-file-windows"]["name"] = "Baseline contract (Windows)"


def _make_linux_baseline_checkout_shallow(documents: dict[str, dict]) -> None:
    job = documents["reusable-release-validation.yml"]["jobs"]["build-test-pack"]
    checkout = next(step for step in job["steps"]
                    if str(step.get("uses", "")).startswith("actions/checkout"))
    checkout["with"]["fetch-depth"] = 1


def _remove_reusable_step(documents: dict[str, dict], name: str) -> None:
    job = documents["reusable-release-validation.yml"]["jobs"]["build-test-pack"]
    job["steps"] = [step for step in job["steps"] if step.get("name") != name]


def _wrong_repository_checks_profile(documents: dict[str, dict]) -> None:
    job = documents["reusable-release-validation.yml"]["jobs"]["build-test-pack"]
    profile = next(step for step in job["steps"]
                   if step.get("name") == "Verify RepositoryChecks profile")
    profile["run"] = str(profile["run"]).replace("sp220-05", "repository-checks-fast")


def _duplicate_repository_checks_invocation(documents: dict[str, dict]) -> None:
    job = documents["reusable-release-validation.yml"]["jobs"]["build-test-pack"]
    profile = next(step for step in job["steps"]
                   if step.get("name") == "Verify RepositoryChecks profile")
    duplicate = copy.deepcopy(profile)
    duplicate["name"] = "Duplicate RepositoryChecks profile"
    job["steps"].append(duplicate)


def _add_consumer_logs_to_upload(documents: dict[str, dict]) -> None:
    job = documents["reusable-release-validation.yml"]["jobs"]["build-test-pack"]
    upload = named_step(job["steps"], "Upload immutable packages and reports")
    upload["with"]["path"] += "\nartifacts/consumers/**/logs/**"


def _use_hosted_runner_for_required_lanes(documents: dict[str, dict]) -> None:
    lanes = (
        ("ci.yml", "hosting-integration"),
        ("ci.yml", "json-file-windows"),
        ("ci.yml", "baseline-contract-windows"),
        ("reusable-release-validation.yml", "build-test-pack"),
        ("reusable-release-validation.yml", "health-checks-concurrency"),
        ("codeql.yml", "analyze"),
        ("dependency-review.yml", "dependency-review"),
    )
    for workflow_name, job_name in lanes:
        documents[workflow_name]["jobs"][job_name]["runs-on"] = "windows-latest"


def _make_ci_validation_always_self_hosted(documents: dict[str, dict]) -> None:
    documents["ci.yml"]["jobs"]["validation"]["with"]["runner-labels"] = SELF_HOSTED_WINDOWS_JSON


def _make_hosting_always_self_hosted(documents: dict[str, dict]) -> None:
    job = documents["ci.yml"]["jobs"]["hosting-integration"]
    job["strategy"]["matrix"] = (
        "${{ fromJSON('{\"os\":[\"self-hosted\"]}') }}"
    )


def _make_hosting_static_runner(documents: dict[str, dict]) -> None:
    documents["ci.yml"]["jobs"]["hosting-integration"]["runs-on"] = SELF_HOSTED_WINDOWS


def _make_ci_json_always_self_hosted(documents: dict[str, dict]) -> None:
    documents["ci.yml"]["jobs"]["json-file-windows"]["runs-on"] = SELF_HOSTED_WINDOWS


def _make_ci_baseline_always_self_hosted(documents: dict[str, dict]) -> None:
    documents["ci.yml"]["jobs"]["baseline-contract-windows"]["runs-on"] = SELF_HOSTED_WINDOWS


def _make_codeql_always_self_hosted(documents: dict[str, dict]) -> None:
    documents["codeql.yml"]["jobs"]["analyze"]["runs-on"] = SELF_HOSTED_WINDOWS


def _make_cleanup_non_pr_capable(documents: dict[str, dict], workflow_name: str) -> None:
    documents[workflow_name]["jobs"]["cleanup-self-hosted"]["if"] = CLEANUP_SAME_REPOSITORY_GUARD


def _remove_ci_runner_override(documents: dict[str, dict]) -> None:
    del documents["ci.yml"]["jobs"]["validation"]["with"]["runner-labels"]


def _change_runner_default(documents: dict[str, dict]) -> None:
    documents["reusable-release-validation.yml"]["on"]["workflow_call"]["inputs"]["runner-labels"][
        "default"
    ] = '["windows-latest"]'


def _override_publish_runner(documents: dict[str, dict]) -> None:
    documents["publish-nuget.yml"]["jobs"]["validation"].setdefault("with", {})[
        "runner-labels"
    ] = SELF_HOSTED_WINDOWS_JSON


def _remove_leaf_exit_guard(documents: dict[str, dict]) -> None:
    leaf = named_step(
        documents["reusable-release-validation.yml"]["jobs"]["build-test-pack"]["steps"],
        "SP220-07 leaf tests",
    )
    leaf["run"] = "\n".join(
        line for line in str(leaf.get("run", "")).splitlines()
        if NATIVE_FAIL_FAST_GUARD not in line
    )


def _remove_native_fail_fast_guard(documents: dict[str, dict], step_name: str) -> None:
    job = documents["reusable-release-validation.yml"]["jobs"]["build-test-pack"]
    step = named_step(job["steps"], step_name)
    step["run"] = "\n".join(
        line for line in str(step.get("run", "")).splitlines()
        if NATIVE_FAIL_FAST_GUARD not in line
    )


def _remove_multiline_native_fail_fast_guard(
    documents: dict[str, dict],
    workflow_name: str,
    job_name: str,
    step_name: str,
) -> None:
    job = documents[workflow_name]["jobs"][job_name]
    step = named_step(job["steps"], step_name)
    step["run"] = "\n".join(
        line for line in str(step.get("run", "")).splitlines()
        if NATIVE_FAIL_FAST_GUARD not in line
    )


def _remove_linux_release_fallback(documents: dict[str, dict]) -> None:
    release = named_step(
        documents["reusable-release-validation.yml"]["jobs"]["build-test-pack"]["steps"],
        "Test release version validation",
    )
    release["run"] = str(release.get("run", "")).replace(
        "bash eng/tests/validate-release-version.Tests.sh",
        "Write-Output 'Linux fallback removed'",
    )


def _remove_package_command_shell(documents: dict[str, dict]) -> None:
    job = documents["reusable-release-validation.yml"]["jobs"]["build-test-pack"]
    step = named_step(job["steps"], "Pack packages from graph")
    step.pop("shell", None)


def _remove_windows_lychee_step(documents: dict[str, dict]) -> None:
    job = documents["reusable-release-validation.yml"]["jobs"]["build-test-pack"]
    job["steps"] = [step for step in job["steps"] if step.get("name") != "Docs link check (Windows)"]


def _add_lychee_token(documents: dict[str, dict]) -> None:
    linux = named_step(
        documents["reusable-release-validation.yml"]["jobs"]["build-test-pack"]["steps"],
        "Docs link check",
    )
    linux["env"] = {"GITHUB_TOKEN": "${{ secrets.GITHUB_TOKEN }}"}


def _remove_reusable_pr_guard(documents: dict[str, dict]) -> None:
    documents["reusable-release-validation.yml"]["jobs"]["build-test-pack"].pop("if", None)


def _remove_ci_cleanup_job(documents: dict[str, dict]) -> None:
    del documents["ci.yml"]["jobs"]["cleanup-self-hosted"]


def _make_ci_cleanup_delete_workspace_root(documents: dict[str, dict]) -> None:
    cleanup = named_step(
        documents["ci.yml"]["jobs"]["cleanup-self-hosted"]["steps"],
        "Cleanup generated outputs",
    )
    cleanup["run"] = str(cleanup["run"]) + "\nRemove-Item -LiteralPath $workspace -Recurse -Force"


def _remove_cleanup_direct_target_guard(documents: dict[str, dict], workflow_name: str) -> None:
    cleanup = named_step(
        documents[workflow_name]["jobs"]["cleanup-self-hosted"]["steps"],
        "Cleanup generated outputs",
    )
    direct_reparse_guard = (
        "if ((Get-Item -LiteralPath $fullPath -Force).Attributes -band "
        "[IO.FileAttributes]::ReparsePoint)"
    )
    cleanup["run"] = "\n".join(
        line for line in str(cleanup.get("run", "")).splitlines()
        if direct_reparse_guard not in line
    )


def _restore_lychee_action(documents: dict[str, dict]) -> None:
    cleanup = named_step(
        documents["reusable-release-validation.yml"]["jobs"]["build-test-pack"]["steps"],
        "Docs link check",
    )
    cleanup["uses"] = "lycheeverse/lychee-action@v2"


def _move_graph_before_integrity(documents: dict[str, dict]) -> None:
    job_steps = documents["reusable-release-validation.yml"]["jobs"]["build-test-pack"]["steps"]
    graph = named_step(job_steps, "Verify package graph current")
    job_steps.remove(graph)
    pack_index = job_steps.index(named_step(job_steps, "Pack packages from graph"))
    job_steps.insert(pack_index + 1, graph)


def _move_opentelemetry_consumers_before_pack(documents: dict[str, dict]) -> None:
    job_steps = documents["reusable-release-validation.yml"]["jobs"]["build-test-pack"]["steps"]
    consumers = named_step(job_steps, "Run OpenTelemetry consumers")
    job_steps.remove(consumers)
    pack_index = job_steps.index(named_step(job_steps, "Pack packages from graph"))
    job_steps.insert(pack_index, consumers)


def _duplicate_upload(documents: dict[str, dict]) -> None:
    job_steps = documents["reusable-release-validation.yml"]["jobs"]["build-test-pack"]["steps"]
    job_steps.append(copy.deepcopy(named_step(job_steps, "Upload immutable packages and reports")))


def _hardcode_publish_package(documents: dict[str, dict]) -> None:
    publish_steps = documents["publish-nuget.yml"]["jobs"]["publish"]["steps"]
    push = named_step(publish_steps, "Publish packages in dependency order")
    push["run"] = str(push["run"]) + "\ndotnet nuget push artifacts/packages/SmartPipe.Core.2.2.0.nupkg"


def assert_mutation_rejected(documents: dict[str, dict], mutate, expected: str) -> None:
    mutated = copy.deepcopy(documents)
    mutate(mutated)
    try:
        validate(mutated)
    except AssertionError as error:
        require(expected in str(error), f"Mutation failed for the wrong reason: {error}")
        return
    raise AssertionError(f"RED mutation was accepted: {expected}")


def main() -> int:
    documents = load_workflows()
    validate(documents)
    assert_mutation_rejected(
        documents,
        lambda docs: _remove_reusable_step(docs, "Verify RepositoryChecks profile"),
        "exactly one step named 'Verify RepositoryChecks profile'",
    )
    assert_mutation_rejected(
        documents,
        _wrong_repository_checks_profile,
        "exact sp220-05 profile command",
    )
    assert_mutation_rejected(
        documents,
        lambda docs: docs["reusable-release-validation.yml"]["jobs"]["build-test-pack"]["steps"]
        .append(copy.deepcopy(named_step(
            docs["reusable-release-validation.yml"]["jobs"]["build-test-pack"]["steps"],
            "Verify RepositoryChecks profile"))),
        "exactly one step named 'Verify RepositoryChecks profile'",
    )
    assert_mutation_rejected(
        documents,
        _duplicate_repository_checks_invocation,
        "exactly one RepositoryChecks profile",
    )
    assert_mutation_rejected(
        documents,
        lambda docs: _remove_reusable_step(docs, "Pack packages from graph"),
        "Pack packages from graph",
    )
    assert_mutation_rejected(
        documents,
        lambda docs: _remove_reusable_step(docs, "Run HealthChecks consumers"),
        "Run HealthChecks consumers",
    )
    assert_mutation_rejected(
        documents,
        lambda docs: _remove_reusable_step(docs, "Verify direct production audit policy"),
        "Verify direct production audit policy",
    )
    assert_mutation_rejected(
        documents,
        _add_consumer_logs_to_upload,
        "upload result-only; logs stay local",
    )
    assert_mutation_rejected(
        documents,
        lambda docs: docs["publish-nuget.yml"]["jobs"]["publish"].update({"needs": ["version"]}),
        "depend exactly on version and validation",
    )
    assert_mutation_rejected(
        documents,
        lambda docs: docs["publish-nuget.yml"]["jobs"]["publish"]["steps"][0]["with"].update({"name": "wrong"}),
        "same artifact name",
    )
    assert_mutation_rejected(
        documents,
        lambda docs: docs["ci.yml"]["jobs"]["json-file-windows"]["steps"][0].update({"uses": "actions/checkout@v7"}),
        "full commit SHA",
    )
    assert_mutation_rejected(
        documents,
        lambda docs: docs["ci.yml"]["jobs"]["json-file-windows"]["steps"][0]
            .setdefault("with", {}).__setitem__("persist-credentials", True),
        "persist-credentials: false",
    )
    assert_mutation_rejected(
        documents,
        _revert_windows_lifecycle_namespace,
        "SmartPipe.Extensions.Json.Tests.Sinks",
    )
    assert_mutation_rejected(
        documents,
        _strip_minimum_expected_from_windows,
        "--minimum-expected-tests 1",
    )
    assert_mutation_rejected(
        documents,
        _restore_floating_sdk_selection,
        "global.json as the SDK source",
    )
    assert_mutation_rejected(documents, _remove_release_branch, "release/2.2.0")
    assert_mutation_rejected(
        documents,
        _remove_ci_checkpoint_branch,
        "ci.yml pull_request must include sp220/checkpoint-c",
    )
    assert_mutation_rejected(
        documents,
        _remove_ci_checkpoint_d_branch,
        "ci.yml pull_request must include sp220/checkpoint-d",
    )
    assert_mutation_rejected(
        documents,
        _remove_codeql_checkpoint_branch,
        "codeql.yml pull_request must include sp220/checkpoint-c",
    )
    assert_mutation_rejected(
        documents,
        _remove_codeql_checkpoint_d_branch,
        "codeql.yml pull_request must include sp220/checkpoint-d",
    )
    assert_mutation_rejected(
        documents,
        _remove_dependency_review_checkpoint_branch,
        "dependency-review.yml pull_request must include sp220/checkpoint-c",
    )
    assert_mutation_rejected(
        documents,
        _remove_dependency_review_checkpoint_d_branch,
        "dependency-review.yml pull_request must include sp220/checkpoint-d",
    )
    assert_mutation_rejected(documents, _remove_linux_offline_verification, "Verify 2.1.2 baseline offline")
    assert_mutation_rejected(documents, _make_windows_offline_network_capable, "must not be network-capable")
    assert_mutation_rejected(documents, _remove_repository_test_minimum, "--minimum-expected-tests 1")
    assert_mutation_rejected(documents, _duplicate_required_job_name, "must be unique")
    assert_mutation_rejected(
        documents,
        _make_linux_baseline_checkout_shallow,
        "Reusable Windows baseline lane baseline verification checkout must fetch full Git history.",
    )
    assert_mutation_rejected(
        documents,
        lambda docs: _remove_reusable_step(docs, "Format verify"),
        "Format verify",
    )
    assert_mutation_rejected(
        documents,
        lambda docs: _remove_reusable_step(docs, "Core tests with coverage"),
        "Core tests with coverage",
    )
    assert_mutation_rejected(
        documents,
        lambda docs: _remove_reusable_step(docs, "Hosting tests"),
        "Hosting tests",
    )
    assert_mutation_rejected(
        documents,
        lambda docs: _remove_reusable_step(docs, "Run Hosting consumers"),
        "Run Hosting consumers",
    )
    assert_mutation_rejected(
        documents,
        _use_hosted_runner_for_required_lanes,
        "runner-labels workflow input",
    )
    assert_mutation_rejected(
        documents,
        _make_ci_validation_always_self_hosted,
        "exact reusable workflow caller",
    )
    assert_mutation_rejected(
        documents,
        _make_hosting_always_self_hosted,
        "original hosted non-PR matrix",
    )
    assert_mutation_rejected(
        documents,
        _make_hosting_static_runner,
        "event-aware runner expression",
    )
    assert_mutation_rejected(
        documents,
        _make_ci_json_always_self_hosted,
        "event-aware runner expression",
    )
    assert_mutation_rejected(
        documents,
        _make_ci_baseline_always_self_hosted,
        "event-aware runner expression",
    )
    assert_mutation_rejected(
        documents,
        _make_codeql_always_self_hosted,
        "event-aware runner expression",
    )
    assert_mutation_rejected(
        documents,
        _remove_ci_runner_override,
        "exact reusable workflow caller",
    )
    assert_mutation_rejected(
        documents,
        _change_runner_default,
        "default runner-labels to hosted Linux",
    )
    assert_mutation_rejected(
        documents,
        _override_publish_runner,
        "Publish validation must use reusable hosted Linux runner default",
    )
    assert_mutation_rejected(
        documents,
        _remove_leaf_exit_guard,
        "fail immediately after every failed project test",
    )
    for step_name in (
        "Extensions correctness regressions",
        "PR concurrency regression repeat",
        "Test and benchmark warning gate",
    ):
        assert_mutation_rejected(
            documents,
            lambda docs, name=step_name: _remove_native_fail_fast_guard(docs, name),
            f"Every dotnet command in {step_name} must immediately fail",
        )
    for workflow_name, job_name, step_name in (
        ("ci.yml", "json-file-windows", "JSON file source, path, open, and share tests"),
        ("ci.yml", "json-file-windows", "JSON file sink and dispose tests"),
        ("ci.yml", "json-file-windows", "Dead-letter source and sink tests"),
        ("reusable-release-validation.yml", "health-checks-concurrency", "Build concurrency projects"),
        ("reusable-release-validation.yml", "build-test-pack", "Vulnerable package scan"),
    ):
        label = f"{workflow_name}:{job_name}:{step_name}"
        assert_mutation_rejected(
            documents,
            lambda docs, wf=workflow_name, job=job_name, step=step_name:
                _remove_multiline_native_fail_fast_guard(docs, wf, job, step),
            f"Every dotnet command in multiline {label} must immediately fail",
        )
    assert_mutation_rejected(
        documents,
        _remove_linux_release_fallback,
        "verified Git Bash on Windows and bash on hosted Linux",
    )
    assert_mutation_rejected(
        documents,
        _remove_package_command_shell,
        "package commands must use PowerShell on hosted Linux and Windows",
    )
    assert_mutation_rejected(
        documents,
        _remove_reusable_pr_guard,
        "same-repository pull_request guard",
    )
    assert_mutation_rejected(
        documents,
        _remove_ci_cleanup_job,
        "must define cleanup-self-hosted",
    )
    for workflow_name in ("ci.yml", "codeql.yml", "dependency-review.yml"):
        assert_mutation_rejected(
            documents,
            lambda docs, name=workflow_name: _make_cleanup_non_pr_capable(docs, name),
            f"{workflow_name} cleanup must always run only for trusted repository work",
        )
    assert_mutation_rejected(
        documents,
        _make_ci_cleanup_delete_workspace_root,
        "must not delete the workspace root",
    )
    for workflow_name in ("ci.yml", "codeql.yml", "dependency-review.yml"):
        assert_mutation_rejected(
            documents,
            lambda docs, name=workflow_name: _remove_cleanup_direct_target_guard(docs, name),
            f"{workflow_name} cleanup must reject direct target reparse points",
        )
    assert_mutation_rejected(
        documents,
        _restore_lychee_action,
        "Linux Docs link check must retain the pinned Lychee action",
    )
    assert_mutation_rejected(
        documents,
        _remove_windows_lychee_step,
        "Docs link check (Windows)",
    )
    assert_mutation_rejected(
        documents,
        _add_lychee_token,
        "must not expose or require GITHUB_TOKEN",
    )
    assert_mutation_rejected(
        documents,
        _move_graph_before_integrity,
        "required order",
    )
    assert_mutation_rejected(
        documents,
        _move_opentelemetry_consumers_before_pack,
        "required order",
    )
    assert_mutation_rejected(
        documents,
        lambda docs: _remove_reusable_step(docs, "Run OpenTelemetry consumers"),
        "Run OpenTelemetry consumers",
    )
    assert_mutation_rejected(
        documents,
        _duplicate_upload,
        "exactly one step named 'Upload immutable packages and reports'",
    )
    assert_mutation_rejected(
        documents,
        _hardcode_publish_package,
        "must not hard-code package IDs",
    )
    print("Workflow contract tests passed (YAML 1.2 structure, SDK pinning, graph, artifact flow, "
          "ordering, immutable refs, RED mutations).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
