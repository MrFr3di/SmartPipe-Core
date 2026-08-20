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
REPOSITORY_CHECKS_PROFILE_COMMAND = (
    "dotnet run --project eng/SmartPipe.RepositoryChecks/SmartPipe.RepositoryChecks.csproj "
    "--configuration Release --no-build -- verify --profile sp220-05 "
    "--format github --failures-only"
)


def require(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)


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
    }
    require(len(current) == 28 and {scenario["id"] for scenario in current} == expected,
            "Current consumer set must contain the exact twenty-eight scenarios.")
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

    for event in ("push", "pull_request"):
        branches = ci.get("on", {}).get(event, {}).get("branches", [])
        require("release/2.2.0" in branches,
                f"CI {event} must include release/2.2.0.")

    expected_triggers = {
        "ci.yml": {
            "workflow_dispatch": None,
            "push": {"branches": ["main", "upd", "release/2.2.0"]},
            "pull_request": {
                "branches": ["main", "upd", "release/2.2.0", "sp220/checkpoint-c"]
            },
        },
        "codeql.yml": {
            "push": {"branches": ["main", "upd", "release/2.2.0"]},
            "pull_request": {"branches": ["main", "release/2.2.0", "sp220/checkpoint-c"]},
            "schedule": [{"cron": "27 3 * * 1"}],
        },
        "dependency-review.yml": {
            "pull_request": {"branches": ["main", "release/2.2.0", "sp220/checkpoint-c"]},
        },
    }
    for workflow_name, expected in expected_triggers.items():
        require(documents[workflow_name].get("on") == expected,
                f"{workflow_name} trigger contract changed.")

    workflow_call = reusable.get("on", {}).get("workflow_call")
    require(isinstance(workflow_call, dict), "Reusable validation must declare on.workflow_call.")
    reusable_job = reusable["jobs"].get("build-test-pack")
    require(isinstance(reusable_job, dict), "Reusable validation must define build-test-pack.")
    reusable_steps = steps(reusable_job, "reusable build-test-pack")
    reusable_runs = runs(reusable_steps)
    require(named_step(reusable_steps, "Test workflow contracts").get("run") ==
            "./eng/tests/workflow-contract.Tests.ps1",
            "Reusable validation must execute the workflow contract test.")
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

    assert_baseline_lane(reusable_job, "Reusable Linux baseline lane")

    required_steps = (
        "Verify RepositoryChecks profile",
        "Format verify", "Build", "Repository baseline contract tests",
        "Core tests with coverage", "Core stress tests",
        "Extensions tests", "Dependency Injection tests", "HealthChecks tests", "Hosting lifecycle regressions", "Hosting tests",
        "JSON Extensions tests", "Core correctness regressions",
        "Core concurrency regressions", "Extensions correctness regressions",
        "PR concurrency regression repeat", "Test and benchmark warning gate",
        "Pack packages from graph", "Provision 2.1.2 baseline packages",
        "Verify package graph current", "Verify package metadata current",
        "Verify package ownership current", "Verify release versions current",
        "Run current consumers", "Run Hosting consumers", "Run HealthChecks consumers",
        "Run OpenTelemetry consumers", "Vulnerable package scan",
        "Verify direct production audit policy", "Deprecated package scan",
        "Outdated package report", "Docs link check",
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
    require(concurrency_job.get("strategy", {}).get("matrix", {}).get("os") ==
            ["ubuntu-latest", "windows-latest"],
            "HealthChecks concurrency matrix must run on Linux and Windows.")
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
    }, "CI validation must be the exact reusable workflow caller with read-only contents permission.")
    pull_request = ci.get("on", {}).get("pull_request", {})
    require("paths-ignore" not in pull_request,
            "CI pull requests must not exclude Hosting package, tests, or docs paths.")
    hosting_integration = ci["jobs"].get("hosting-integration")
    require(isinstance(hosting_integration, dict)
            and hosting_integration.get("name") == "Hosting integration (${{ matrix.os }})"
            and hosting_integration.get("runs-on") == "${{ matrix.os }}",
            "CI must define the Hosting integration OS matrix.")
    hosting_matrix = hosting_integration.get("strategy", {}).get("matrix", {}).get("os")
    require(hosting_matrix == ["ubuntu-latest", "windows-latest"],
            "Hosting integration must run on Linux and Windows.")
    hosting_steps = steps(hosting_integration, "hosting-integration")
    hosting_runs = runs(hosting_steps)
    integration_run = str(named_step(
        hosting_steps, "Generic Host ordering and cancellation tests").get("run", ""))
    require("--filter-class SmartPipe.Extensions.Hosting.Tests.Integration.GenericHostIntegrationTests"
            in integration_run,
            "Hosting OS matrix must run the real Generic Host integration tests.")
    windows = ci["jobs"].get("json-file-windows")
    require(isinstance(windows, dict) and windows.get("runs-on") == "windows-latest",
            "CI must define the Windows JSON lane on windows-latest.")
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
            and baseline_windows.get("name") == "Baseline contract (Windows)"
            and baseline_windows.get("runs-on") == "windows-latest",
            "CI must define the uniquely named Windows baseline contract job.")
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


def _remove_codeql_checkpoint_branch(documents: dict[str, dict]) -> None:
    branches = documents["codeql.yml"]["on"]["pull_request"]["branches"]
    branches.remove("sp220/checkpoint-c")


def _remove_dependency_review_checkpoint_branch(documents: dict[str, dict]) -> None:
    branches = documents["dependency-review.yml"]["on"]["pull_request"]["branches"]
    branches.remove("sp220/checkpoint-c")


def _remove_linux_offline_verification(documents: dict[str, dict]) -> None:
    job = documents["reusable-release-validation.yml"]["jobs"]["build-test-pack"]
    job["steps"] = [step for step in job["steps"]
                    if step.get("name") != "Verify 2.1.2 baseline offline"]


def _make_windows_offline_network_capable(documents: dict[str, dict]) -> None:
    job = documents["ci.yml"]["jobs"]["baseline-contract-windows"]
    named_step(job["steps"], "Verify 2.1.2 baseline offline")["run"] += "\nInvoke-WebRequest https://example.test"


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


def _remove_hosting_matrix(documents: dict[str, dict]) -> None:
    del documents["ci.yml"]["jobs"]["hosting-integration"]


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
        _remove_codeql_checkpoint_branch,
        "codeql.yml pull_request must include sp220/checkpoint-c",
    )
    assert_mutation_rejected(
        documents,
        _remove_dependency_review_checkpoint_branch,
        "dependency-review.yml pull_request must include sp220/checkpoint-c",
    )
    assert_mutation_rejected(documents, _remove_linux_offline_verification, "Verify 2.1.2 baseline offline")
    assert_mutation_rejected(documents, _make_windows_offline_network_capable, "must not be network-capable")
    assert_mutation_rejected(documents, _remove_repository_test_minimum, "--minimum-expected-tests 1")
    assert_mutation_rejected(documents, _duplicate_required_job_name, "must be unique")
    assert_mutation_rejected(
        documents,
        _make_linux_baseline_checkout_shallow,
        "Reusable Linux baseline lane baseline verification checkout must fetch full Git history.",
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
        _remove_hosting_matrix,
        "Hosting integration OS matrix",
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
