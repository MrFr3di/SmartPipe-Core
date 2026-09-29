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
SCENARIO_ID_PATTERN = r"^[a-z0-9-]+(?:\.[a-z0-9-]+)*$"
SCENARIO_TEMPLATE_PATTERN = (
    r"^tests/Consumers/Scenarios/[a-z0-9-]+(?:\.[a-z0-9-]+)*/[^/]+\.(cs|csproj)$"
)
HOSTED_WINDOWS = "windows-latest"
HOSTED_WINDOWS_JSON = '["windows-latest"]'
CODEQL_ACTION_REF = (
    "github/codeql-action/init@99df26d4f13ea111d4ec1a7dddef6063f76b97e9"
)
CODEQL_ANALYZE_ACTION_REF = (
    "github/codeql-action/analyze@99df26d4f13ea111d4ec1a7dddef6063f76b97e9"
)
DEPENDENCY_REVIEW_ACTION_REF = (
    "actions/dependency-review-action@a1d282b36b6f3519aa1f3fc636f609c47dddb294"
)
SAME_REPOSITORY_PR_GUARD = (
    "github.event_name != 'pull_request' || "
    "github.event.pull_request.head.repo.full_name == github.repository"
)
PULL_REQUEST_SAME_REPOSITORY_GUARD = "github.event.pull_request.head.repo.full_name == github.repository"
DIAGNOSTIC_INPUTS_EMPTY_GUARD = (
    "(github.event_name != 'workflow_dispatch' || "
    "(inputs.diagnostic-sha == '' && inputs.diagnostic-scenario == '' && "
    "inputs.diagnostic-repeat == ''))"
)
CI_NORMAL_GUARD = f"({SAME_REPOSITORY_PR_GUARD}) && {DIAGNOSTIC_INPUTS_EMPTY_GUARD}"
DIAGNOSTIC_GUARD = (
    "github.event_name == 'workflow_dispatch' && "
    "(inputs.diagnostic-sha != '' || inputs.diagnostic-scenario != '' || "
    "inputs.diagnostic-repeat != '')"
)
CI_VALIDATION_RUNNER_INPUT = (
    "${{ github.event_name == 'pull_request' && "
    "'[\"windows-latest\"]' || "
    "'[\"ubuntu-latest\"]' }}"
)
CI_WINDOWS_RUNNER = HOSTED_WINDOWS
NUGET_PACKAGES_PATH = "${{ github.workspace }}/.nuget/packages"
HOSTING_NAME = "${{ matrix.os == 'windows-latest' && 'Windows' || matrix.os }}"
HOSTING_RUNNER = "${{ matrix.os }}"
HOSTING_MATRIX = (
    "${{ fromJSON(github.event_name == 'pull_request' && "
    "'{\"os\":[\"windows-latest\"]}' || "
    "'{\"os\":[\"ubuntu-latest\",\"windows-latest\"]}') }}"
)
CSV_INTEGRATION_NAME = "CSV file integration (${{ matrix.os == 'windows-latest' && 'Windows' || matrix.os }})"
CSV_INTEGRATION_MATRIX = (
    "${{ fromJSON('{\"os\":[\"ubuntu-latest\",\"windows-latest\"]}') }}"
)
CSV_TEST_PROJECT = (
    "tests/SmartPipe.Extensions.Csv.Tests/SmartPipe.Extensions.Csv.Tests.csproj"
)
DAPPER_TEST_PROJECT = (
    "tests/SmartPipe.Extensions.Dapper.Tests/SmartPipe.Extensions.Dapper.Tests.csproj"
)
EFCORE_TEST_PROJECT = (
    "tests/SmartPipe.Extensions.EntityFrameworkCore.Tests/"
    "SmartPipe.Extensions.EntityFrameworkCore.Tests.csproj"
)
MAPSTER_TEST_PROJECT = (
    "tests/SmartPipe.Extensions.Mapster.Tests/"
    "SmartPipe.Extensions.Mapster.Tests.csproj"
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


def require_hosted_windows(job: dict, label: str) -> None:
    require(job.get("runs-on") == HOSTED_WINDOWS,
            f"{label} must target hosted Windows (`windows-latest`).")


def require_parameterized_runner(job: dict, label: str) -> None:
    require(job.get("runs-on") == "${{ fromJSON(inputs.runner-labels) }}",
            f"{label} must use the runner-labels workflow input.")


def require_runner_expression(job: dict, expected: str, label: str) -> None:
    require(job.get("runs-on") == expected,
            f"{label} must use the event-aware runner expression.")


def assert_codeql_contract(workflow: dict) -> None:
    require(workflow.get("name") == "CodeQL",
            "CodeQL workflow must retain the official public check name.")
    require(workflow.get("permissions") == {
        "contents": "read",
        "security-events": "write",
    }, "CodeQL must request only contents read and security-events write permissions.")
    jobs = workflow.get("jobs", {})
    require(set(jobs) == {"analyze"},
            "CodeQL workflow must define only the analyze job.")
    job = jobs["analyze"]
    require(job.get("if") == SAME_REPOSITORY_PR_GUARD,
            "CodeQL must skip untrusted fork pull requests.")
    require(job.get("runs-on") == "ubuntu-latest",
            "CodeQL must use hosted Linux.")
    codeql_steps = steps(job, "CodeQL analyze")
    checkout = next(
        step for step in codeql_steps
        if str(step.get("uses", "")).startswith("actions/checkout")
    )
    require(checkout.get("with", {}).get("persist-credentials") is False,
            "CodeQL checkout must disable persisted credentials.")
    setup = named_step(codeql_steps, "Setup .NET")
    require(setup.get("with", {}).get("global-json-file") == "global.json",
            "CodeQL must use the pinned SDK from global.json.")
    require(setup.get("with", {}).get("cache") is True
            and setup.get("with", {}).get("cache-dependency-path") == "**/packages.lock.json",
            "CodeQL setup-dotnet must cache only the lock-file keyed NuGet packages.")
    init = named_step(codeql_steps, "Initialize CodeQL")
    require(init.get("uses") == CODEQL_ACTION_REF
            and init.get("with", {}).get("languages") == "csharp",
            "CodeQL must initialize the official pinned C# action.")
    build = named_step(codeql_steps, "Build")
    require(build.get("run") == "dotnet build SmartPipe.Core.slnx -c Release",
            "CodeQL must build the solution before analysis.")
    analyze = named_step(codeql_steps, "Perform CodeQL Analysis")
    require(analyze.get("uses") == CODEQL_ANALYZE_ACTION_REF,
            "CodeQL must run the official pinned analysis action.")


def assert_nuget_isolation_contract(workflow: dict, workflow_name: str) -> None:
    environment = workflow.get("env")
    require(isinstance(environment, dict)
            and environment.get("NUGET_PACKAGES") == NUGET_PACKAGES_PATH,
            f"{workflow_name} must isolate NuGet packages inside GITHUB_WORKSPACE.")


def assert_setup_dotnet_cache_contract(workflow: dict, workflow_name: str) -> None:
    setup_steps = [
        step
        for job in workflow["jobs"].values()
        for step in job.get("steps", [])
        if str(step.get("uses", "")).startswith("actions/setup-dotnet")
    ]
    require(bool(setup_steps), f"{workflow_name} must contain setup-dotnet steps.")
    for step in setup_steps:
        with_block = step.get("with", {})
        require(with_block.get("cache") is True
                and with_block.get("cache-dependency-path") == "**/packages.lock.json",
                f"{workflow_name} restore-heavy setup-dotnet must use lock-file keyed caching.")


def assert_hosted_restore_source_contract(documents: dict[str, dict]) -> None:
    for workflow_name in ("ci.yml", "reusable-release-validation.yml"):
        workflow = documents[workflow_name]
        for job_name, job in workflow["jobs"].items():
            for command in runs(job.get("steps", [])):
                if "dotnet restore " in command:
                    require("-p:DisableImplicitLibraryPacksFolder=true" in command,
                            f"{workflow_name}:{job_name} hosted restore must disable the SDK library-packs source.")


def assert_diagnostic_contract(ci: dict) -> None:
    dispatch = ci.get("on", {}).get("workflow_dispatch", {})
    inputs = dispatch.get("inputs", {}) if isinstance(dispatch, dict) else {}
    require(set(inputs) == {"diagnostic-sha", "diagnostic-scenario", "diagnostic-repeat"},
            "CI diagnostic dispatch must expose exactly SHA, scenario, and repeat inputs.")
    for name in inputs:
        definition = inputs[name]
        require(definition.get("required") is False
                and definition.get("type") == "string"
                and definition.get("default") == "",
                f"CI diagnostic input {name} must be an optional empty string.")

    job = ci["jobs"].get("diagnostic-consumer")
    require(isinstance(job, dict), "CI must define the optional diagnostic-consumer job.")
    require(job.get("if") == DIAGNOSTIC_GUARD,
            "Diagnostic consumer must run only for a workflow dispatch with diagnostic input.")
    require_hosted_windows(job, "Diagnostic consumer")
    diagnostic_steps = steps(job, "diagnostic-consumer")
    validation = named_step(diagnostic_steps, "Validate diagnostic inputs")
    validation_script = str(validation.get("run", ""))
    for token in ("^[0-9a-f]{40}$", SCENARIO_ID_PATTERN, "^[1-5]$"):
        require(token in validation_script,
                "Diagnostic input validation must enforce the safe dotted ID grammar."
                if token == SCENARIO_ID_PATTERN
                else f"Diagnostic input validation must enforce {token}.")
    checkout = next(
        step for step in diagnostic_steps
        if str(step.get("uses", "")).startswith("actions/checkout")
    )
    require(checkout.get("with", {}).get("ref") == "${{ inputs.diagnostic-sha }}"
            and checkout.get("with", {}).get("persist-credentials") is False,
            "Diagnostic consumer must checkout the exact requested SHA without credentials.")
    verify = named_step(diagnostic_steps, "Verify exact diagnostic checkout")
    require("git rev-parse HEAD" in str(verify.get("run", ""))
            and "DIAGNOSTIC_SHA" in str(verify.get("run", "")),
            "Diagnostic consumer must verify the checked out commit SHA.")
    restore = named_step(diagnostic_steps, "Restore locked")
    require(str(restore.get("run", "")).strip() ==
            "dotnet restore SmartPipe.Core.slnx --locked-mode -p:DisableImplicitLibraryPacksFolder=true",
            "Diagnostic consumer must perform one locked solution restore.")
    build = named_step(diagnostic_steps, "Build")
    require("--no-restore" in str(build.get("run", ""))
            and "dotnet build SmartPipe.Core.slnx" in str(build.get("run", "")),
            "Diagnostic consumer must build once after restore.")
    pack = named_step(diagnostic_steps, "Pack packages from graph")
    pack_run = str(pack.get("run", ""))
    require("pack-packages" in pack_run
            and "--output artifacts/packages" in pack_run
            and "--manifest artifacts/packages/manifest.json" in pack_run,
            "Diagnostic consumer must pack once from the package graph.")
    run = named_step(diagnostic_steps, "Run diagnostic consumer")
    run_script = str(run.get("run", ""))
    require("--scenario $env:DIAGNOSTIC_SCENARIO" in run_script
            and "DIAGNOSTIC_REPEAT" in run_script
            and "for ($pass = 1;" in run_script
            and "$pass -le [int]$env:DIAGNOSTIC_REPEAT" in run_script,
            "Diagnostic consumer must invoke exactly the selected scenario one to five times.")
    require("GITHUB_STEP_SUMMARY" in run_script
            and "8192" in run_script
            and "upload-artifact" not in "\n".join(
                str(step) for step in diagnostic_steps
            ),
            "Diagnostic consumer must write only a bounded summary and no artifact upload.")
    commands = [command for command in runs(diagnostic_steps)
                if "dotnet restore SmartPipe.Core.slnx" in command
                or "dotnet build SmartPipe.Core.slnx" in command
                or "pack-packages" in command]
    require(sum("dotnet restore SmartPipe.Core.slnx" in command for command in commands) == 1
            and sum("dotnet build SmartPipe.Core.slnx" in command for command in commands) == 1
            and sum("pack-packages" in command for command in commands) == 1,
            "Diagnostic consumer must restore, build, and pack exactly once.")


def require_same_repository_pr_guard(job: dict, label: str, allow_non_pr: bool = True) -> None:
    expected = SAME_REPOSITORY_PR_GUARD if allow_non_pr else PULL_REQUEST_SAME_REPOSITORY_GUARD
    require(job.get("if") == expected,
            f"{label} must use the same-repository pull_request guard.")


def require_ci_normal_job_guard(job: dict, label: str) -> None:
    require(job.get("if") == CI_NORMAL_GUARD,
            f"{label} must retain the same-repository guard and skip only diagnostic dispatches.")


def assert_dependency_review_contract(workflow: dict) -> None:
    require(workflow.get("name") == "Dependency Review",
            "Dependency Review workflow must retain the official public check name.")
    require(workflow.get("permissions") == {
        "contents": "read",
        "pull-requests": "read",
    }, "Dependency Review must request only contents and pull-requests read permissions.")
    jobs = workflow.get("jobs", {})
    require(set(jobs) == {"dependency-review"},
            "Dependency Review workflow must define only the dependency-review job.")
    job = jobs["dependency-review"]
    require(job.get("runs-on") == "ubuntu-latest",
            "Dependency Review must use hosted Linux.")
    require("if" not in job,
            "Dependency Review must run for public fork pull requests as well as same-repository requests.")
    job_steps = steps(job, "Dependency Review")
    checkouts = [step for step in job_steps
                 if str(step.get("uses", "")).startswith("actions/checkout")]
    require(len(checkouts) == 1
            and checkouts[0].get("with", {}).get("persist-credentials") is False,
            "Dependency Review checkout must be pinned and credential-free.")
    review = named_step(job_steps, "Dependency review")
    require(review.get("uses") == DEPENDENCY_REVIEW_ACTION_REF,
            "Dependency Review must run the official pinned public action.")


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


def assert_private_repository_docs_links_are_local() -> None:
    private_repository_prefix = "https://github.com/MrFr3di/SmartPipe-Core/"
    sources = (
        ROOT / "README.md",
        ROOT / "docs" / "plans" / "2.2.0-extension-architecture.md",
        ROOT / "docs" / "plans" / "2.2.0" / "SP220-00-governance-and-baseline.md",
    )
    for source in sources:
        require(private_repository_prefix not in source.read_text(encoding="utf-8"),
                f"{source.relative_to(ROOT)} must use local links for private repository references.")


def assert_scenario_id_grammar() -> None:
    valid = ("csv-direct", "csv.facade-1", "a.b.c")
    invalid = (".leading", "trailing.", "double..dot", "UPPER")
    for value in valid:
        require(re.fullmatch(SCENARIO_ID_PATTERN, value) is not None,
                f"Scenario ID grammar must accept '{value}'.")
    for value in invalid:
        require(re.fullmatch(SCENARIO_ID_PATTERN, value) is None,
                f"Scenario ID grammar must reject '{value}'.")


def assert_consumer_schema_contract(schema: dict | None = None) -> None:
    if schema is None:
        schema_path = ROOT / "eng" / "consumer-scenarios.schema.json"
        schema = json.loads(schema_path.read_text(encoding="utf-8"))
    properties = schema.get("properties", {})
    scenarios = properties.get("scenarios", {})
    require(scenarios.get("minItems") == 53 and scenarios.get("maxItems") == 53,
            "Consumer scenario schema must require exactly fifty-three scenarios.")
    required_pattern = properties.get("requiredAtRelease", {}).get("items", {}).get("pattern")
    require(required_pattern == SCENARIO_ID_PATTERN,
            "Consumer scenario schema must use the safe dotted ID grammar.")
    scenario_properties = schema.get("$defs", {}).get("scenario", {}).get("properties", {})
    for property_name in ("id", "category"):
        require(scenario_properties.get(property_name, {}).get("pattern") == SCENARIO_ID_PATTERN,
                f"Consumer scenario schema {property_name} must use the safe dotted ID grammar.")
    require(scenario_properties.get("templatePath", {}).get("pattern") == SCENARIO_TEMPLATE_PATTERN,
            "Consumer scenario schema template paths must use the safe dotted ID grammar.")
    assert_scenario_id_grammar()


def assert_consumer_contract(document: dict | None = None) -> None:
    if document is None:
        manifest_path = ROOT / "eng" / "consumer-scenarios.json"
        document = json.loads(manifest_path.read_text(encoding="utf-8"))
    current = [scenario for scenario in document["scenarios"] if scenario["set"] == "current"]
    expected = {
        "csv-direct", "csv-di-composition", "csv-facade-source",
        "csv-facade-binary-2.1.2", "csv-trim-diagnostic",
        "dapper-direct", "dapper-di-composition", "dapper-facade-source",
        "dapper-facade-binary-2.1.2", "dapper-trim-diagnostic",
        "entity-framework-core-direct", "entity-framework-core-di-composition",
        "entity-framework-core-facade-source", "entity-framework-core-facade-binary-2.1.2",
        "entity-framework-core-trim-diagnostic",
        "mapster-direct", "mapster-facade-binary-2.1.2", "mapster-trim-diagnostic",
        "core-direct", "json-direct", "extensions-meta", "legacy-binary-2.1.2",
        "core-trim", "core-nativeaot", "json-nativeaot", "json-trim",
        "json-dependency-injection-direct",
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
    require(len(current) == 53 and {scenario["id"] for scenario in current} == expected,
            "Current consumer set must contain the exact fifty-three scenarios.")
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



def assert_csv_integration_contract(ci: dict, reusable: dict) -> None:
    job = ci["jobs"].get("csv-file-integration")
    require(isinstance(job, dict), "CI must define the CSV file integration matrix job.")
    require(job.get("name") == CSV_INTEGRATION_NAME,
            "CSV file integration must use the stable matrix check name.")
    require_ci_normal_job_guard(job, "CSV file integration")
    require_runner_expression(job, HOSTING_RUNNER, "CSV file integration")
    strategy = job.get("strategy")
    require(isinstance(strategy, dict)
            and strategy.get("fail-fast") is False
            and strategy.get("matrix") == CSV_INTEGRATION_MATRIX
            and "ubuntu-latest" in str(strategy.get("matrix"))
            and "windows-latest" in str(strategy.get("matrix")),
            "CSV file integration must use the Windows/Linux hosted matrix.")

    csv_steps = steps(job, "csv-file-integration")
    checkout = next(
        step for step in csv_steps
        if str(step.get("uses", "")).startswith("actions/checkout")
    )
    require(checkout.get("with", {}).get("persist-credentials") is False,
            "CSV file integration checkout must disable persisted credentials.")
    setup = next(
        step for step in csv_steps
        if str(step.get("uses", "")).startswith("actions/setup-dotnet")
    )
    require(setup.get("with", {}).get("global-json-file") == "global.json"
            and setup.get("with", {}).get("cache") is True
            and setup.get("with", {}).get("cache-dependency-path") == "**/packages.lock.json",
            "CSV file integration setup-dotnet must use the standard lock-file cache.")
    restore = named_step(csv_steps, "Restore locked")
    require(str(restore.get("run", "")).strip() ==
            f"dotnet restore {CSV_TEST_PROJECT} --locked-mode -p:DisableImplicitLibraryPacksFolder=true",
            "CSV file integration must perform one locked CSV test-project restore.")
    build = named_step(csv_steps, "Build CSV test project")
    require(" ".join(str(build.get("run", "")).split()) ==
            f"dotnet build {CSV_TEST_PROJECT} --configuration Release --no-restore -warnaserror",
            "CSV file integration must build the CSV test project in Release.")
    expected_tests = {
        "CSV strict source tests": "SmartPipe.Extensions.Csv.Tests.CsvStrictSourceTests",
        "CSV strict sink tests": "SmartPipe.Extensions.Csv.Tests.CsvStrictSinkTests",
    }
    for step_name, test_class in expected_tests.items():
        test_run = " ".join(str(named_step(csv_steps, step_name).get("run", "")).split())
        require("--minimum-expected-tests 1" in test_run,
                f"{step_name} must set --minimum-expected-tests 1.")
        require(test_run == (
            f"dotnet test --project {CSV_TEST_PROJECT} --configuration Release --no-build "
            f"--filter-class {test_class} --minimum-expected-tests 1"
        ), f"CSV file integration must run the {test_class} filter with a non-empty gate.")
    require(csv_steps.index(restore) < csv_steps.index(build)
            < csv_steps.index(named_step(csv_steps, "CSV strict source tests"))
            < csv_steps.index(named_step(csv_steps, "CSV strict sink tests")),
            "CSV file integration must restore, build, then run source and sink tests.")

    reusable_job = reusable["jobs"].get("build-test-pack")
    require(isinstance(reusable_job, dict),
            "Reusable validation must define build-test-pack for CSV tests.")
    reusable_steps = steps(reusable_job, "reusable build-test-pack")
    csv_step = named_step(reusable_steps, "CSV Extensions tests")
    require(" ".join(str(csv_step.get("run", "")).split()) == (
        f"dotnet test --project {CSV_TEST_PROJECT} --configuration Release --no-build "
        "--minimum-expected-tests 1"
    ), "Reusable validation must run the complete CSV test project with a non-empty gate.")
    dapper_step = named_step(reusable_steps, "Dapper Extensions tests")
    require(" ".join(str(dapper_step.get("run", "")).split()) == (
        f"dotnet test --project {DAPPER_TEST_PROJECT} --configuration Release --no-build "
        "--minimum-expected-tests 1"
    ), "Reusable validation must run the complete Dapper test project with a non-empty gate.")
    efcore_step = named_step(reusable_steps, "EntityFrameworkCore Extensions tests")
    require(" ".join(str(efcore_step.get("run", "")).split()) == (
        f"dotnet test --project {EFCORE_TEST_PROJECT} --configuration Release --no-build "
        "--minimum-expected-tests 1"
    ), "Reusable validation must run the complete EntityFrameworkCore test project with a non-empty gate.")
    mapster_step = named_step(reusable_steps, "Mapster Extensions tests")
    require(" ".join(str(mapster_step.get("run", "")).split()) == (
        f"dotnet test --project {MAPSTER_TEST_PROJECT} --configuration Release --no-build "
        "--minimum-expected-tests 1"
    ), "Reusable validation must run the complete Mapster test project with a non-empty gate.")


def validate(documents: dict[str, dict]) -> None:
    reusable = documents["reusable-release-validation.yml"]
    ci = documents["ci.yml"]
    static_analysis = documents["codeql.yml"]
    dependency_review = documents["dependency-review.yml"]
    publish = documents["publish-nuget.yml"]
    assert_consumer_schema_contract()

    for workflow_name, workflow in (
        ("ci.yml", ci),
        ("codeql.yml", static_analysis),
        ("dependency-review.yml", dependency_review),
    ):
        branches = workflow.get("on", {}).get("pull_request", {}).get("branches", [])
        for checkpoint in ("c", "d", "e"):
            require(f"sp220/checkpoint-{checkpoint}" in branches,
                    f"{workflow_name} pull_request must include sp220/checkpoint-{checkpoint}.")

    for event in ("push", "pull_request"):
        branches = ci.get("on", {}).get(event, {}).get("branches", [])
        require("release/2.2.0" in branches,
                f"CI {event} must include release/2.2.0.")
    for workflow_name in ("ci.yml", "codeql.yml"):
        branches = documents[workflow_name].get("on", {}).get("push", {}).get("branches", [])
        require("sp220/checkpoint-e" in branches,
                f"{workflow_name} push must include sp220/checkpoint-e.")
    assert_diagnostic_contract(ci)

    expected_triggers = {
        "ci.yml": {
            "workflow_dispatch": {
                "inputs": {
                    "diagnostic-sha": {
                        "description": "Exact 40-character commit SHA for a single-consumer diagnostic",
                        "required": False,
                        "type": "string",
                        "default": "",
                    },
                    "diagnostic-scenario": {
                        "description": "Exact consumer scenario ID for a single-consumer diagnostic",
                        "required": False,
                        "type": "string",
                        "default": "",
                    },
                    "diagnostic-repeat": {
                        "description": "Number of diagnostic runs (1-5)",
                        "required": False,
                        "type": "string",
                        "default": "",
                    },
                },
            },
            "push": {"branches": ["main", "upd", "release/2.2.0", "sp220/checkpoint-e"]},
            "pull_request": {
                "branches": ["main", "upd", "release/2.2.0", "sp220/checkpoint-c", "sp220/checkpoint-d", "sp220/checkpoint-e"]
            },
        },
        "codeql.yml": {
            "push": {"branches": ["main", "upd", "release/2.2.0", "sp220/checkpoint-e"]},
            "pull_request": {"branches": ["main", "release/2.2.0", "sp220/checkpoint-c", "sp220/checkpoint-d", "sp220/checkpoint-e"]},
            "schedule": [{"cron": "27 3 * * 1"}],
        },
        "dependency-review.yml": {
            "pull_request": {"branches": ["main", "release/2.2.0", "sp220/checkpoint-c", "sp220/checkpoint-d", "sp220/checkpoint-e"]},
        },
    }
    for workflow_name, expected in expected_triggers.items():
        require(documents[workflow_name].get("on") == expected,
                f"{workflow_name} trigger contract changed.")
    assert_hosted_restore_source_contract(documents)

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
    assert_nuget_isolation_contract(reusable, "reusable-release-validation.yml")
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
    require(restores == [
        "dotnet restore SmartPipe.Core.slnx --locked-mode -p:DisableImplicitLibraryPacksFolder=true",
    ],
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
        "CSV Extensions tests",
        "Pack packages from graph", "Provision 2.1.2 baseline packages",
        "Verify package graph current", "Verify package metadata current",
        "Verify package ownership current", "Verify release versions current",
        "Run current consumers", "Vulnerable package scan",
        "Verify direct production audit policy", "Deprecated package scan",
        "Outdated package report", "Docs link check", "Docs link check (Windows)",
        "Upload immutable packages and reports",
    )
    for name in required_steps:
        named_step(reusable_steps, name)
    gate_order = [
        "Restore locked", "Build", "Verify RepositoryChecks profile",
        "Pack packages from graph",
        "Provision 2.1.2 baseline packages", "Verify package graph current",
        "Verify package metadata current", "Verify package ownership current",
        "Verify release versions current", "Run current consumers",
        "Test and benchmark warning gate", "Vulnerable package scan", "Verify direct production audit policy",
        "Deprecated package scan", "Outdated package report",
        "Upload immutable packages and reports",
    ]
    gate_indexes = [reusable_steps.index(named_step(reusable_steps, name)) for name in gate_order]
    require(gate_indexes == sorted(gate_indexes),
            "Reusable package gates must follow the required order.")
    wide_tests_index = reusable_steps.index(named_step(reusable_steps, "Core correctness regressions"))
    for name in (
        "Pack packages from graph", "Verify package graph current",
        "Verify package metadata current", "Verify package ownership current",
        "Verify release versions current", "Run current consumers",
    ):
        require(reusable_steps.index(named_step(reusable_steps, name)) < wide_tests_index,
                f"{name} must run before wide tests.")
    reusable_text = "\n".join(reusable_runs)
    pack_run = str(named_step(reusable_steps, "Pack packages from graph").get("run", ""))
    for token in ("pack-packages", "--mode current", "--configuration Release",
                  "--package-version", "--output artifacts/packages",
                  "--manifest artifacts/packages/manifest.json"):
        require(token in pack_run, f"Graph-driven pack step must contain '{token}'.")
    require(reusable_text.count("pack-packages") == 1,
            "Reusable validation must invoke pack-packages exactly once.")
    current_consumers = [
        str(step.get("run", ""))
        for step in reusable_steps
        if "run-consumers" in str(step.get("run", ""))
        and "--set current" in str(step.get("run", ""))
    ]
    require(len(current_consumers) == 1
            and "--category" not in current_consumers[0]
            and "--scenario" not in current_consumers[0],
            "Reusable validation must execute exactly one full current consumer run.")
    concurrency_job = reusable["jobs"].get("health-checks-concurrency")
    require(isinstance(concurrency_job, dict),
            "Reusable validation must define the HealthChecks concurrency OS matrix.")
    require_parameterized_runner(concurrency_job, "HealthChecks concurrency")
    require_same_repository_pr_guard(concurrency_job, "HealthChecks concurrency")
    require("strategy" not in concurrency_job,
            "HealthChecks concurrency must use one hosted runner lane.")
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
    require(upload.get("if") == "github.event_name != 'pull_request'",
            "Reusable validation artifact upload must skip only pull_request events and remain required for non-PR events.")
    require(upload.get("with", {}).get("name") == "${{ inputs.artifact-name }}",
            "Reusable validation must upload the caller-selected artifact name.")
    require(upload.get("with", {}).get("retention-days") ==
            "${{ inputs.artifact-name == 'packages' && 7 || 90 }}",
            "Reusable validation must retain generic CI packages for seven days and versioned artifacts for the existing policy.")
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
        "if": CI_NORMAL_GUARD,
        "with": {"runner-labels": CI_VALIDATION_RUNNER_INPUT},
    }, "CI validation must be the exact reusable workflow caller with read-only contents permission.")
    pull_request = ci.get("on", {}).get("pull_request", {})
    require("paths-ignore" not in pull_request,
            "CI pull requests must not exclude Hosting package, tests, or docs paths.")
    hosting_integration = ci["jobs"].get("hosting-integration")
    require(isinstance(hosting_integration, dict)
            and hosting_integration.get("name") == f"Hosting integration ({HOSTING_NAME})",
            "CI must preserve the Hosting integration check name across event routes.")
    require_ci_normal_job_guard(hosting_integration, "Hosting integration")
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
    require_ci_normal_job_guard(windows, "Windows JSON lane")
    windows_steps = steps(windows, "json-file-windows")
    windows_runs = runs(windows_steps)
    windows_restores = [command for command in windows_runs if "dotnet restore SmartPipe.Core.slnx" in command]
    require(windows_restores == [
        "dotnet restore SmartPipe.Core.slnx --locked-mode -p:DisableImplicitLibraryPacksFolder=true",
    ],
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
    require_ci_normal_job_guard(baseline_windows, "Windows baseline contract lane")
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
    require(not duplicates, f"Required job/check names must be unique: {sorted(duplicates)}")

    windows_text = "\n".join(windows_runs)
    require("SmartPipe.Extensions.Json.Tests.Sinks.JsonFileSinkLifecycleTests" in windows_text,
            "Windows lifecycle filter must use the correct "
            "SmartPipe.Extensions.Json.Tests.Sinks namespace.")
    require("SmartPipe.Extensions.Tests.Sinks.JsonFileSinkLifecycleTests" not in windows_text,
            "Windows lifecycle filter must not use the obsolete "
            "SmartPipe.Extensions.Tests.Sinks namespace.")

    for workflow_name, document in documents.items():
        require("cleanup-self-hosted" not in document["jobs"],
                f"{workflow_name} must not define the obsolete cleanup-self-hosted job.")
    assert_dependency_review_contract(dependency_review)
    assert_nuget_isolation_contract(ci, "ci.yml")
    assert_nuget_isolation_contract(static_analysis, "codeql.yml")
    assert_codeql_contract(static_analysis)
    for workflow_name, document in (
        ("ci.yml", ci),
        ("codeql.yml", static_analysis),
        ("reusable-release-validation.yml", reusable),
    ):
        assert_setup_dotnet_cache_contract(document, workflow_name)
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
    assert_csv_integration_contract(ci, reusable)
    assert_link_check_exclusion_scoped()
    assert_private_repository_docs_links_are_local()
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


def _remove_csv_scenario(document: dict) -> None:
    document["scenarios"] = [
        scenario for scenario in document["scenarios"] if scenario["id"] != "csv-direct"
    ]


def _relax_schema_scenario_count(schema: dict) -> None:
    schema["properties"]["scenarios"]["maxItems"] = 49


def _relax_schema_scenario_id_pattern(schema: dict) -> None:
    schema["properties"]["requiredAtRelease"]["items"]["pattern"] = "^[a-z0-9-]+$"


def _relax_ci_scenario_id_validation(documents: dict[str, dict]) -> None:
    step = named_step(
        documents["ci.yml"]["jobs"]["diagnostic-consumer"]["steps"],
        "Validate diagnostic inputs",
    )
    step["run"] = str(step["run"]).replace(SCENARIO_ID_PATTERN, "^[a-z0-9-]+$")


def _remove_ci_checkpoint_e_push_branch(documents: dict[str, dict]) -> None:
    documents["ci.yml"]["on"]["push"]["branches"].remove("sp220/checkpoint-e")


def _remove_ci_checkpoint_e_branch(documents: dict[str, dict]) -> None:
    documents["ci.yml"]["on"]["pull_request"]["branches"].remove("sp220/checkpoint-e")


def _remove_codeql_checkpoint_e_push_branch(documents: dict[str, dict]) -> None:
    documents["codeql.yml"]["on"]["push"]["branches"].remove("sp220/checkpoint-e")


def _remove_codeql_checkpoint_e_branch(documents: dict[str, dict]) -> None:
    documents["codeql.yml"]["on"]["pull_request"]["branches"].remove("sp220/checkpoint-e")


def _remove_dependency_review_checkpoint_e_branch(documents: dict[str, dict]) -> None:
    documents["dependency-review.yml"]["on"]["pull_request"]["branches"].remove("sp220/checkpoint-e")


def _remove_csv_integration_job(documents: dict[str, dict]) -> None:
    del documents["ci.yml"]["jobs"]["csv-file-integration"]


def _change_csv_integration_name(documents: dict[str, dict]) -> None:
    documents["ci.yml"]["jobs"]["csv-file-integration"]["name"] = "CSV integration"


def _change_csv_integration_matrix(documents: dict[str, dict]) -> None:
    documents["ci.yml"]["jobs"]["csv-file-integration"]["strategy"]["matrix"] = (
        "${{ fromJSON('{\"os\":[\"windows-latest\"]}') }}"
    )


def _remove_csv_integration_cache(documents: dict[str, dict]) -> None:
    for step in documents["ci.yml"]["jobs"]["csv-file-integration"]["steps"]:
        if str(step.get("uses", "")).startswith("actions/setup-dotnet"):
            step["with"].pop("cache", None)
            return


def _change_csv_integration_restore(documents: dict[str, dict]) -> None:
    step = named_step(
        documents["ci.yml"]["jobs"]["csv-file-integration"]["steps"],
        "Restore locked",
    )
    step["run"] = str(step["run"]).replace("--locked-mode", "")


def _change_csv_integration_build(documents: dict[str, dict]) -> None:
    step = named_step(
        documents["ci.yml"]["jobs"]["csv-file-integration"]["steps"],
        "Build CSV test project",
    )
    step["run"] = str(step["run"]).replace("--configuration Release", "--configuration Debug")


def _change_csv_source_filter(documents: dict[str, dict]) -> None:
    step = named_step(
        documents["ci.yml"]["jobs"]["csv-file-integration"]["steps"],
        "CSV strict source tests",
    )
    step["run"] = str(step["run"]).replace(
        "SmartPipe.Extensions.Csv.Tests.CsvStrictSourceTests",
        "SmartPipe.Extensions.Csv.Tests.CsvStrictSinkTests",
    )


def _strip_csv_test_minimum(documents: dict[str, dict]) -> None:
    step = named_step(
        documents["ci.yml"]["jobs"]["csv-file-integration"]["steps"],
        "CSV strict source tests",
    )
    step["run"] = str(step["run"]).replace(" --minimum-expected-tests 1", "")


def _remove_csv_reusable_step(documents: dict[str, dict]) -> None:
    job = documents["reusable-release-validation.yml"]["jobs"]["build-test-pack"]
    job["steps"] = [step for step in job["steps"] if step.get("name") != "CSV Extensions tests"]


def _strip_csv_reusable_minimum(documents: dict[str, dict]) -> None:
    step = named_step(
        documents["reusable-release-validation.yml"]["jobs"]["build-test-pack"]["steps"],
        "CSV Extensions tests",
    )
    step["run"] = str(step["run"]).replace(" --minimum-expected-tests 1", "")


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


def _make_hosting_matrix_hosted_only(documents: dict[str, dict]) -> None:
    job = documents["ci.yml"]["jobs"]["hosting-integration"]
    job["strategy"]["matrix"] = (
        "${{ fromJSON('{\"os\":[\"ubuntu-latest\"]}') }}"
    )


def _make_hosting_static_runner(documents: dict[str, dict]) -> None:
    documents["ci.yml"]["jobs"]["hosting-integration"]["runs-on"] = HOSTED_WINDOWS


def _make_codeql_substitute_name(documents: dict[str, dict]) -> None:
    documents["codeql.yml"]["name"] = "Private static analysis"


def _make_codeql_non_official_action(documents: dict[str, dict]) -> None:
    init = named_step(documents["codeql.yml"]["jobs"]["analyze"]["steps"], "Initialize CodeQL")
    init["uses"] = "github/codeql-action/init@0000000000000000000000000000000000000000"


def _make_dependency_review_non_official_action(documents: dict[str, dict]) -> None:
    review = named_step(
        documents["dependency-review.yml"]["jobs"]["dependency-review"]["steps"],
        "Dependency review",
    )
    review["uses"] = "actions/dependency-review-action@0000000000000000000000000000000000000000"


def _remove_nuget_isolation(documents: dict[str, dict], workflow_name: str) -> None:
    documents[workflow_name]["env"].pop("NUGET_PACKAGES", None)


def _remove_setup_dotnet_cache(documents: dict[str, dict], workflow_name: str) -> None:
    for job in documents[workflow_name]["jobs"].values():
        for step in job.get("steps", []):
            if str(step.get("uses", "")).startswith("actions/setup-dotnet"):
                step["with"].pop("cache", None)
                step["with"].pop("cache-dependency-path", None)
                return


def _change_artifact_retention(documents: dict[str, dict]) -> None:
    upload = named_step(
        documents["reusable-release-validation.yml"]["jobs"]["build-test-pack"]["steps"],
        "Upload immutable packages and reports",
    )
    upload["with"]["retention-days"] = 7


def _add_ci_cleanup_job(documents: dict[str, dict]) -> None:
    documents["ci.yml"]["jobs"]["cleanup-self-hosted"] = {}


def _remove_ci_runner_override(documents: dict[str, dict]) -> None:
    del documents["ci.yml"]["jobs"]["validation"]["with"]["runner-labels"]


def _remove_diagnostic_input(documents: dict[str, dict]) -> None:
    del documents["ci.yml"]["on"]["workflow_dispatch"]["inputs"]["diagnostic-sha"]


def _make_diagnostic_hosted(documents: dict[str, dict]) -> None:
    documents["ci.yml"]["jobs"]["diagnostic-consumer"]["runs-on"] = "ubuntu-latest"


def _make_ci_normal_job_diagnostic_capable(documents: dict[str, dict]) -> None:
    documents["ci.yml"]["jobs"]["json-file-windows"]["if"] = SAME_REPOSITORY_PR_GUARD


def _remove_diagnostic_sha_validation(documents: dict[str, dict]) -> None:
    step = named_step(
        documents["ci.yml"]["jobs"]["diagnostic-consumer"]["steps"],
        "Validate diagnostic inputs",
    )
    step["run"] = str(step["run"]).replace("^[0-9a-f]{40}", "^[0-9a-f]+")


def _remove_diagnostic_exact_checkout(documents: dict[str, dict]) -> None:
    checkout = next(
        step for step in documents["ci.yml"]["jobs"]["diagnostic-consumer"]["steps"]
        if str(step.get("uses", "")).startswith("actions/checkout")
    )
    checkout["with"]["ref"] = "main"


def _remove_diagnostic_repeat_bound(documents: dict[str, dict]) -> None:
    step = named_step(
        documents["ci.yml"]["jobs"]["diagnostic-consumer"]["steps"],
        "Validate diagnostic inputs",
    )
    step["run"] = str(step["run"]).replace("^[1-5]$", "^[0-9]+$")


def _duplicate_current_consumer_run(documents: dict[str, dict]) -> None:
    job = documents["reusable-release-validation.yml"]["jobs"]["build-test-pack"]
    current = named_step(job["steps"], "Run current consumers")
    job["steps"].append(copy.deepcopy(current))


def _move_current_consumer_after_wide_tests(documents: dict[str, dict]) -> None:
    job = documents["reusable-release-validation.yml"]["jobs"]["build-test-pack"]
    current = named_step(job["steps"], "Run current consumers")
    job["steps"].remove(current)
    wide = job["steps"].index(named_step(job["steps"], "Core correctness regressions"))
    job["steps"].insert(wide + 1, current)


def _change_runner_default(documents: dict[str, dict]) -> None:
    documents["reusable-release-validation.yml"]["on"]["workflow_call"]["inputs"]["runner-labels"][
        "default"
    ] = '["windows-latest"]'


def _override_publish_runner(documents: dict[str, dict]) -> None:
    documents["publish-nuget.yml"]["jobs"]["validation"].setdefault("with", {})[
        "runner-labels"
    ] = HOSTED_WINDOWS_JSON


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


def _remove_upload_event_guard(documents: dict[str, dict]) -> None:
    upload = named_step(
        documents["reusable-release-validation.yml"]["jobs"]["build-test-pack"]["steps"],
        "Upload immutable packages and reports",
    )
    upload.pop("if", None)


def _restrict_upload_to_push(documents: dict[str, dict]) -> None:
    upload = named_step(
        documents["reusable-release-validation.yml"]["jobs"]["build-test-pack"]["steps"],
        "Upload immutable packages and reports",
    )
    upload["if"] = "github.event_name == 'push'"


def _hardcode_publish_package(documents: dict[str, dict]) -> None:
    publish_steps = documents["publish-nuget.yml"]["jobs"]["publish"]["steps"]
    push = named_step(publish_steps, "Publish packages in dependency order")
    push["run"] = str(push["run"]) + "\ndotnet nuget push artifacts/packages/SmartPipe.Core.2.2.0.nupkg"


def assert_document_mutation_rejected(document: dict, mutate, validator, expected: str) -> None:
    mutated = copy.deepcopy(document)
    mutate(mutated)
    try:
        validator(mutated)
    except AssertionError as error:
        require(expected in str(error), f"Mutation failed for the wrong reason: {error}")
        return
    raise AssertionError(f"RED mutation was accepted: {expected}")


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
    manifest = json.loads((ROOT / "eng" / "consumer-scenarios.json").read_text(encoding="utf-8"))
    schema = json.loads((ROOT / "eng" / "consumer-scenarios.schema.json").read_text(encoding="utf-8"))
    assert_document_mutation_rejected(
        manifest, _remove_csv_scenario, assert_consumer_contract, "exact fifty-three scenarios"
    )
    assert_document_mutation_rejected(
        schema, _relax_schema_scenario_count, assert_consumer_schema_contract,
        "exactly fifty-three scenarios",
    )
    assert_document_mutation_rejected(
        schema, _relax_schema_scenario_id_pattern, assert_consumer_schema_contract,
        "safe dotted ID grammar",
    )
    assert_mutation_rejected(
        documents, _relax_ci_scenario_id_validation, "safe dotted ID grammar"
    )
    for mutate, expected in (
        (_remove_ci_checkpoint_e_push_branch, "ci.yml push must include sp220/checkpoint-e"),
        (_remove_ci_checkpoint_e_branch, "ci.yml pull_request must include sp220/checkpoint-e"),
        (_remove_codeql_checkpoint_e_push_branch, "codeql.yml push must include sp220/checkpoint-e"),
        (_remove_codeql_checkpoint_e_branch, "codeql.yml pull_request must include sp220/checkpoint-e"),
        (_remove_dependency_review_checkpoint_e_branch,
         "dependency-review.yml pull_request must include sp220/checkpoint-e"),
    ):
        assert_mutation_rejected(documents, mutate, expected)
    for mutate, expected in (
        (_remove_csv_integration_job, "define the CSV file integration matrix job"),
        (_change_csv_integration_name, "stable matrix check name"),
        (_change_csv_integration_matrix, "Windows/Linux hosted matrix"),
        (_remove_csv_integration_cache, "ci.yml restore-heavy setup-dotnet"),
        (_change_csv_integration_restore, "locked CSV test-project restore"),
        (_change_csv_integration_build, "build the CSV test project in Release"),
        (_change_csv_source_filter, "CsvStrictSourceTests filter"),
        (_strip_csv_test_minimum, "CSV strict source tests must set --minimum-expected-tests 1"),
        (_remove_csv_reusable_step, "exactly one step named 'CSV Extensions tests'"),
        (_strip_csv_reusable_minimum, "complete CSV test project with a non-empty gate"),
    ):
        assert_mutation_rejected(documents, mutate, expected)
    assert_mutation_rejected(
        documents,
        _remove_diagnostic_input,
        "exactly SHA, scenario, and repeat inputs",
    )
    assert_mutation_rejected(
        documents,
        _make_diagnostic_hosted,
        "must target hosted Windows",
    )
    assert_mutation_rejected(
        documents,
        _make_ci_normal_job_diagnostic_capable,
        "skip only diagnostic dispatches",
    )
    assert_mutation_rejected(
        documents,
        _remove_diagnostic_sha_validation,
        "^[0-9a-f]{40}$",
    )
    assert_mutation_rejected(
        documents,
        _remove_diagnostic_exact_checkout,
        "exact requested SHA",
    )
    assert_mutation_rejected(
        documents,
        _remove_diagnostic_repeat_bound,
        "^[1-5]$",
    )
    assert_mutation_rejected(
        documents,
        _duplicate_current_consumer_run,
        "Expected exactly one step named 'Run current consumers'",
    )
    assert_mutation_rejected(
        documents,
        _move_current_consumer_after_wide_tests,
        "must run before wide tests",
    )
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
        "global.json",
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
        _make_hosting_matrix_hosted_only,
        "original hosted non-PR matrix",
    )
    assert_mutation_rejected(
        documents,
        _make_hosting_static_runner,
        "event-aware runner expression",
    )
    assert_mutation_rejected(
        documents,
        lambda docs: docs["ci.yml"]["jobs"]["json-file-windows"].__setitem__(
            "runs-on", "ubuntu-latest"),
        "Windows JSON lane must use the event-aware runner expression",
    )
    assert_mutation_rejected(
        documents,
        lambda docs: docs["ci.yml"]["jobs"]["baseline-contract-windows"].__setitem__(
            "runs-on", "ubuntu-latest"),
        "Windows baseline contract lane must use the event-aware runner expression",
    )
    assert_mutation_rejected(
        documents,
        _make_codeql_substitute_name,
        "official public check name",
    )
    assert_mutation_rejected(
        documents,
        _make_codeql_non_official_action,
        "official pinned C# action",
    )
    assert_mutation_rejected(
        documents,
        _make_dependency_review_non_official_action,
        "official pinned public action",
    )
    for workflow_name in ("ci.yml", "reusable-release-validation.yml", "codeql.yml"):
        assert_mutation_rejected(
            documents,
            lambda docs, name=workflow_name: _remove_nuget_isolation(docs, name),
            f"{workflow_name} must isolate NuGet packages",
        )
        assert_mutation_rejected(
            documents,
            lambda docs, name=workflow_name: _remove_setup_dotnet_cache(docs, name),
            "cache only the lock-file keyed"
            if workflow_name == "codeql.yml"
            else f"{workflow_name} restore-heavy setup-dotnet",
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
        _change_artifact_retention,
        "retain generic CI packages for seven days",
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
        _add_ci_cleanup_job,
        "must not define the obsolete cleanup-self-hosted job",
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
        _duplicate_upload,
        "exactly one step named 'Upload immutable packages and reports'",
    )
    assert_mutation_rejected(
        documents,
        _remove_upload_event_guard,
        "skip only pull_request events and remain required for non-PR events",
    )
    assert_mutation_rejected(
        documents,
        _restrict_upload_to_push,
        "skip only pull_request events and remain required for non-PR events",
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
