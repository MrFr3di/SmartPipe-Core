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
        "reusable-release-validation.yml",
        "publish-nuget.yml",
    )
}
SHA_REF = re.compile(r"^[^@\s]+@[0-9a-f]{40}$")


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


def assert_baseline_lane(job: dict, label: str, *, require_scope: bool) -> None:
    job_steps = steps(job, label)
    repository_tests = named_step(job_steps, "Repository baseline contract tests")
    test_run = " ".join(str(repository_tests.get("run", "")).split())
    expected_test = ("dotnet test --project "
                     "tests/SmartPipe.RepositoryChecks.Tests/SmartPipe.RepositoryChecks.Tests.csproj "
                     "--configuration Release --no-build --minimum-expected-tests 1")
    require(test_run == expected_test,
            f"{label} repository tests must set --minimum-expected-tests 1.")

    if require_scope:
        checkouts = [step for step in job_steps
                     if str(step.get("uses", "")).startswith("actions/checkout")]
        require(len(checkouts) == 1
                and checkouts[0].get("with", {}).get("fetch-depth") == 0,
                f"{label} scope verification checkout must fetch full Git history.")
        scope = named_step(job_steps, "Verify SP220-00 scope")
        require(scope.get("if") == "github.event_name == 'pull_request'",
                f"{label} scope verification must be PR-only.")
        scope_run = " ".join(str(scope.get("run", "")).split())
        expected_scope = ("dotnet run --project eng/SmartPipe.RepositoryChecks/SmartPipe.RepositoryChecks.csproj "
                          "--configuration Release --no-build -- verify-sp220-scope --repo-root . "
                          "--base-commit ${{ github.event.pull_request.base.sha }}")
        require(scope_run == expected_scope,
                f"{label} scope verification must use the pull request base SHA.")

    online = named_step(job_steps, "Provision and verify 2.1.2 baseline")
    offline = named_step(job_steps, "Verify 2.1.2 baseline offline")
    baseline_steps = [step for step in job_steps
                      if "verify-baseline" in str(step.get("run", ""))]
    require(len(baseline_steps) == 2,
            f"{label} must run exactly one online and one offline baseline verification.")
    online_run = " ".join(str(online.get("run", "")).split())
    offline_run = " ".join(str(offline.get("run", "")).split())
    expected_online = ("dotnet run --project eng/SmartPipe.RepositoryChecks/SmartPipe.RepositoryChecks.csproj "
                       "--configuration Release --no-build -- verify-baseline --repo-root . "
                       "--manifest eng/baselines/2.1.2/manifest.json "
                       "--packages-dir artifacts/baselines/2.1.2 --mode integrity")
    forbidden = ("curl", "wget", "Invoke-WebRequest", "http://", "https://")
    require(not any(token.lower() in offline_run.lower() for token in forbidden),
            f"{label} offline baseline step must not be network-capable.")
    require(online_run == expected_online,
            f"{label} online baseline command must provision and verify the exact manifest.")
    require(offline_run == expected_online + " --offline",
            f"{label} offline baseline command must verify the exact manifest offline.")
    require(job_steps.index(online) < job_steps.index(offline),
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
    }
    require(len(current) == 7 and {scenario["id"] for scenario in current} == expected,
            "Current consumer set must contain the exact seven scenarios.")
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
    publish = documents["publish-nuget.yml"]

    for event in ("push", "pull_request"):
        branches = ci.get("on", {}).get(event, {}).get("branches", [])
        require("release/2.2.0" in branches,
                f"CI {event} must include release/2.2.0.")

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
    require(reusable_steps.index(build_step) < reusable_steps.index(repository_test_step),
            "Reusable repository baseline tests must run after Build.")
    for name in ("Verify central package management", "Verify package projects"):
        require(reusable_steps.index(build_step) < reusable_steps.index(named_step(reusable_steps, name)),
                f"Reusable {name} must run after Build because it uses --no-build.")

    assert_baseline_lane(reusable_job, "Reusable Linux baseline lane", require_scope=True)

    required_steps = (
        "Verify central package management", "Verify package projects",
        "Format verify", "Build", "Repository baseline contract tests",
        "Verify SP220-00 scope", "Core tests with coverage", "Core stress tests",
        "Extensions tests", "JSON Extensions tests", "Core correctness regressions",
        "Core concurrency regressions", "Extensions correctness regressions",
        "PR concurrency regression repeat", "Test and benchmark warning gate",
        "Pack packages from graph", "Provision and verify 2.1.2 baseline",
        "Verify package graph current", "Verify package metadata current",
        "Verify package ownership current", "Verify release versions current",
        "Run current consumers", "Vulnerable package scan", "Verify direct production audit policy", "Deprecated package scan",
        "Outdated package report", "Docs link check",
        "Upload immutable packages and reports",
    )
    for name in required_steps:
        named_step(reusable_steps, name)
    gate_order = [
        "Restore locked", "Build", "Verify central package management", "Verify package projects",
        "Test and benchmark warning gate", "Pack packages from graph",
        "Provision and verify 2.1.2 baseline", "Verify package graph current",
        "Verify package metadata current", "Verify package ownership current",
        "Verify release versions current", "Run current consumers",
        "Vulnerable package scan", "Verify direct production audit policy",
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
            and "artifacts/audit" in upload_path,
            "Reusable validation must upload package artifacts, consumer reports, and audit reports together.")

    validation = ci["jobs"].get("validation")
    require(validation == {
        "uses": "./.github/workflows/reusable-release-validation.yml",
        "permissions": {"contents": "read"},
    }, "CI validation must be the exact reusable workflow caller with read-only contents permission.")
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
    require("dotnet restore SmartPipe.Core.slnx --locked-mode" in baseline_runs,
            "Windows baseline contract lane must perform locked restore.")
    build = named_step(baseline_windows_steps, "Build repository checks")
    require("-warnaserror" in str(build.get("run", "")),
            "Windows baseline contract build must treat warnings as errors.")
    assert_baseline_lane(baseline_windows, "Windows baseline contract lane", require_scope=True)

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

    all_runs = windows_runs + reusable_runs
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


def _move_graph_before_integrity(documents: dict[str, dict]) -> None:
    job_steps = documents["reusable-release-validation.yml"]["jobs"]["build-test-pack"]["steps"]
    graph = named_step(job_steps, "Verify package graph current")
    job_steps.remove(graph)
    pack_index = job_steps.index(named_step(job_steps, "Pack packages from graph"))
    job_steps.insert(pack_index + 1, graph)


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
    assert_mutation_rejected(documents, _remove_linux_offline_verification, "Verify 2.1.2 baseline offline")
    assert_mutation_rejected(documents, _make_windows_offline_network_capable, "must not be network-capable")
    assert_mutation_rejected(documents, _remove_repository_test_minimum, "--minimum-expected-tests 1")
    assert_mutation_rejected(documents, _duplicate_required_job_name, "must be unique")
    assert_mutation_rejected(
        documents,
        _make_linux_baseline_checkout_shallow,
        "Reusable Linux baseline lane scope verification checkout must fetch full Git history.",
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
        _hardcode_publish_package,
        "must not hard-code package IDs",
    )
    print("Workflow contract tests passed (YAML 1.2 structure, SDK pinning, graph, artifact flow, "
          "ordering, immutable refs, RED mutations).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
