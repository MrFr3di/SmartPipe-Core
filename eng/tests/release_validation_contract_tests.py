"""SP220-18 contracts: one package producer, strict release gates, Windows replay."""
from __future__ import annotations

import copy
import unittest

from workflow_contract_tests import ROOT, load_workflows, named_step, require

REPLAY = "inputs.package-artifact-id != ''"
PRODUCER = "inputs.package-artifact-id == ''"
RELEASE = "inputs.validation-mode == 'release'"
CI_RELEASE_MODE = "${{ inputs.release-validation && 'release' || 'current' }}"
DOWNLOAD = "actions/download-artifact@70fc10c6e5e1ce46ad2ea6f2b72d43f7d47b13c3"


def assert_producer_contract(documents: dict) -> None:
    workflow = documents['reusable-release-validation.yml']
    inputs = workflow['on']['workflow_call']['inputs']
    require(inputs.get('validation-mode', {}).get('default') == 'current', 'validation-mode must default to current')
    require(inputs.get('package-artifact-id', {}).get('default') == '', 'replay must be optional')
    steps = workflow['jobs']['build-test-pack']['steps']
    guard = named_step(steps, 'Validate package workflow inputs')
    run = guard.get('run', '')
    require("@('current', 'release')" in run and "^[1-9][0-9]*$" in run
            and 'throw' in run, 'invalid mode/artifact ID must fail before work')
    require(steps.index(guard) < steps.index(named_step(steps, 'Restore locked')), 'input validation must precede restore')
    pack = named_step(steps, 'Pack packages from graph')
    require(pack.get('if') == PRODUCER and '--mode "$env:VALIDATION_MODE"' in pack['run'], 'only producer may pack in selected mode')
    artifact_tests = named_step(steps, 'Test package artifact validation')
    require('validate-package-artifact.Tests.ps1' in artifact_tests.get('run', '')
            and 'compare-nuget-package-payload.Tests.ps1' in artifact_tests.get('run', '')
            and not artifact_tests.get('continue-on-error'), 'package and published-payload fixtures must both be mandatory')
    upload = named_step(steps, 'Upload immutable packages and reports')
    require(PRODUCER in upload.get('if', ''), 'replay must not upload another package artifact')
    for command in ('graph', 'metadata', 'ownership'):
        step = named_step(steps, 'Verify package ' + command + ' release')
        require(step.get('if') == RELEASE and '--mode release' in step['run']
                and not step.get('continue-on-error'), 'release ' + command + ' gate must be mandatory')
    version = named_step(steps, 'Verify release versions release')
    require(version.get('if') == RELEASE and '--mode release' in version['run'], 'release version gate must be mandatory')
    consumers = named_step(steps, 'Run current consumers')
    require(steps.index(named_step(steps, 'Verify package metadata release')) < steps.index(consumers), 'release metadata must precede consumers')


def assert_replay_contract(documents: dict) -> None:
    steps = documents['reusable-release-validation.yml']['jobs']['build-test-pack']['steps']
    download = named_step(steps, 'Download producer package artifact')
    require(download.get('if') == REPLAY and download.get('uses') == DOWNLOAD
            and download.get('with') == {'artifact-ids': '${{ inputs.package-artifact-id }}', 'path': 'downloaded', 'merge-multiple': True}, 'replay must download exact producer ID into isolated root')
    integrity = named_step(steps, 'Validate producer package artifact')
    require(integrity.get('if') == REPLAY and '-ArtifactRoot downloaded' in integrity['run']
            and '-ExpectedMode "$env:VALIDATION_MODE"' in integrity['run']
            and '-ExpectedCommit (git rev-parse HEAD)' in integrity['run']
            and not integrity.get('continue-on-error'), 'replay must validate mode, hashes, version and source commit')
    directory = named_step(steps, 'Set package directory')
    require('downloaded/artifacts/packages' in directory['run'] and 'PACKAGE_DIRECTORY' in directory['run'], 'replay must select downloaded feed')
    consumers = named_step(steps, 'Run current consumers')
    require('--package-directory "$env:PACKAGE_DIRECTORY"' in consumers['run'], 'consumers must use selected immutable feed')
    require(steps.index(download) < steps.index(integrity) < steps.index(consumers), 'replay integrity must precede consumers')
    reports = named_step(steps, 'Upload replay reports')
    paths = reports['with']['path']
    require(REPLAY in reports.get('if', '') and 'artifacts/consumers/**/result.json' in paths
            and 'artifacts/packages' not in paths and 'downloaded' not in paths, 'replay upload must contain reports only')


def assert_publication_contract(documents: dict) -> None:
    jobs = documents['publish-nuget.yml']['jobs']
    version_steps = jobs['version']['steps']
    accepted = named_step(version_steps, 'Require accepted release commit')
    accepted_run = accepted.get('run', '')
    require('refs/heads/main:refs/remotes/origin/main' in accepted_run
            and 'refs/heads/release/2.2.0:refs/remotes/origin/release/2.2.0' in accepted_run
            and 'merge-base --is-ancestor "$GITHUB_SHA" refs/remotes/origin/main' in accepted_run
            and 'merge-base --is-ancestor refs/remotes/origin/release/2.2.0 "$GITHUB_SHA"' in accepted_run
            and not accepted.get('continue-on-error'),
            'release tag must be bound to accepted main history containing the current release branch head')
    require(jobs['validation']['with'].get('validation-mode') == 'release', 'publication producer must select release mode')
    windows = jobs.get('windows-validation')
    require(isinstance(windows, dict) and windows.get('needs') == ['version', 'validation']
            and windows.get('uses') == './.github/workflows/reusable-release-validation.yml', 'release must require Windows replay')
    require(windows['with'].get('runner-labels') == '["windows-latest"]'
            and windows['with'].get('validation-mode') == 'release'
            and windows['with'].get('package-artifact-id') == '${{ needs.validation.outputs.artifact-id }}'
            and not windows.get('if') and not windows.get('continue-on-error'), 'Windows release validation must consume producer ID and cannot be optional')
    require(jobs['postgresql-validation']['with'].get('validation-mode') == 'release', 'PostgreSQL must validate release artifact mode')
    require(set(jobs['publish']['needs']) == {'version', 'validation', 'windows-validation', 'postgresql-validation'}, 'publication must await both Windows and PostgreSQL')
    publish_steps = jobs['publish']['steps']
    integrity = named_step(publish_steps, 'Validate downloaded package artifact')
    require('-ExpectedMode release' in integrity['run'] and '-ExpectedCommit (git rev-parse HEAD)' in integrity['run'], 'publisher must verify release mode and exact source commit')
    recovery = named_step(publish_steps, 'Preflight recoverable published packages')
    recovery_run = recovery.get('run', '')
    require("inputs.recoverable-rerun" in recovery.get('if', '')
            and 'v3-flatcontainer' in recovery_run
            and '/api/v2/symbolpackage/' in recovery_run
            and '-OutFile $published -SkipHttpErrorCheck -PassThru' in recovery_run
            and '-OutFile $publishedSymbol -SkipHttpErrorCheck -PassThru' in recovery_run
            and '-Method Head' not in recovery_run
            and 'compare-nuget-package-payload.ps1' in recovery_run
            and 'nupkgPath' in recovery_run
            and 'snupkgPath' in recovery_run
            and not recovery.get('continue-on-error'),
            'recoverable publication must reject pre-existing primary or symbol packages that differ from the immutable producer payload before login')
    push = named_step(publish_steps, 'Publish packages in dependency order')
    push_run = push.get('run', '')
    push_env = push.get('env', {})
    require(push_env.get('NUGET_API_KEY') == '${{ steps.nuget-login.outputs.NUGET_API_KEY }}'
            and push_env.get('NUGET_SYMBOL_API_KEY') == '${{ steps.nuget-login.outputs.NUGET_API_KEY }}'
            and '--api-key' not in push_run and '--symbol-api-key' not in push_run,
            'NuGet credentials must stay in supported environment variables rather than command-line arguments')
    require('recovery-state.json' in push_run
            and '--skip-duplicate' not in push_run
            and '--no-symbols' in push_run
            and '.snupkgPath' in push_run
            and '.snupkgSha256' in push_run
            and 'actual_symbol_hash' in push_run
            and 'primaryPublished' in push_run
            and 'symbolsPublished' in push_run
            and 'dotnet nuget push "$symbol_package"' in push_run,
            'recoverable publication must use verified preflight state, avoid duplicate suppression races, and push missing primary/symbol packages explicitly')
    require(publish_steps.index(recovery) < publish_steps.index(named_step(publish_steps, 'NuGet login')) < publish_steps.index(push),
            'recoverable duplicate provenance must be checked before obtaining publication credentials')
    published = named_step(publish_steps, 'Verify published package payloads')
    published_run = published.get('run', '')
    require('[DateTimeOffset]::UtcNow.AddMinutes(15)' in published_run
            and 'while ($pending.Count -gt 0 -and [DateTimeOffset]::UtcNow -lt $deadline)' in published_run
            and 'Start-Sleep -Seconds 10' in published_run
            and 'v3-flatcontainer' in published_run
            and '/api/v2/symbolpackage/' in published_run
            and 'compare-nuget-package-payload.ps1' in published_run
            and 'nupkgPath' in published_run
            and 'snupkgPath' in published_run
            and 'did not become verifiable within the 15-minute propagation window' in published_run
            and publish_steps.index(push) < publish_steps.index(published)
            and not published.get('continue-on-error'),
            'published primary and symbol packages must share one bounded NuGet propagation window and match the immutable producer payload after push')
    require(jobs['publish'].get('environment') == 'nuget-production'
            and jobs['publish'].get('permissions') == {'contents': 'read', 'id-token': 'write'}, 'publication retains protected OIDC environment')
    for name, job in jobs.items():
        if name != 'publish':require(job.get('permissions', {'contents': 'read'}) == {'contents': 'read'}, 'validation cannot request OIDC')


def assert_ci_release_contract(documents: dict) -> None:
    ci = documents['ci.yml']
    flag = ci['on']['workflow_dispatch']['inputs'].get('release-validation')
    require(isinstance(flag, dict) and flag.get('type') == 'boolean' and flag.get('default') is False, 'release dispatch must be explicit and default off')
    require(ci['jobs']['validation']['with'].get('validation-mode') == CI_RELEASE_MODE, 'CI producer must select requested mode')
    windows = ci['jobs'].get('release-windows-validation')
    require(isinstance(windows, dict) and windows.get('needs') == 'validation'
            and windows.get('with', {}).get('package-artifact-id') == '${{ needs.validation.outputs.artifact-id }}'
            and windows['with'].get('runner-labels') == '["windows-latest"]'
            and windows['with'].get('validation-mode') == 'release', 'release candidate dispatch requires full Windows replay')
    require('inputs.release-validation' in windows.get('if', '')
            and "inputs.diagnostic-sha == ''" in windows['if']
            and not windows.get('continue-on-error'), 'release replay cannot be triggered by diagnostic mode or optional failure')
    require(ci['jobs']['postgresql-consumers']['with'].get('validation-mode') == CI_RELEASE_MODE, 'CI PostgreSQL must use producer mode')


def assert_telemetry_test_contract(documents: dict) -> None:
    step = named_step(documents['reusable-release-validation.yml']['jobs']['build-test-pack']['steps'], 'OpenTelemetry tests')
    require(step.get('run') == 'dotnet test --project tests/SmartPipe.Extensions.OpenTelemetry.Tests/SmartPipe.Extensions.OpenTelemetry.Tests.csproj --configuration Release --no-build --minimum-expected-tests 1'
            and not step.get('if') and not step.get('continue-on-error'), 'OpenTelemetry unit suite must run on both producer and Windows replay')


def assert_fixture_runner_contract() -> None:
    wrapper = (ROOT / 'eng/tests/workflow-contract.Tests.ps1').read_text()
    require("$releaseTestScript = Join-Path $PSScriptRoot 'release_validation_contract_tests.py'" in wrapper
            and 'python $releaseTestScript' in wrapper and wrapper.count('if ($LASTEXITCODE -ne 0)') == 2,
            'CI wrapper must execute both workflow suites and enforce both exit codes')


def assert_release_contract(documents: dict) -> None:
    assert_producer_contract(documents)
    assert_replay_contract(documents)
    assert_publication_contract(documents)
    assert_ci_release_contract(documents)
    assert_telemetry_test_contract(documents)
    assert_fixture_runner_contract()


class ReleaseValidationContractTests(unittest.TestCase):
    def setUp(self):
        self.documents = load_workflows()

    def test_producer_enforces_release_gates(self):
        assert_producer_contract(self.documents)

    def test_replay_uses_verified_producer_feed_without_repacking(self):
        assert_replay_contract(self.documents)

    def test_publication_requires_windows_and_release_artifact(self):
        assert_publication_contract(self.documents)

    def test_candidate_dispatch_covers_same_artifact_on_windows(self):
        assert_ci_release_contract(self.documents)

    def test_telemetry_unit_suite_is_mandatory(self):
        assert_telemetry_test_contract(self.documents)
        for optional in ({'if': "inputs.validation-mode == 'release'"}, {'continue-on-error': True}):
            modified = copy.deepcopy(self.documents)
            named_step(modified['reusable-release-validation.yml']['jobs']['build-test-pack']['steps'], 'OpenTelemetry tests').update(optional)
            with self.assertRaises(AssertionError):assert_telemetry_test_contract(modified)
        modified = copy.deepcopy(self.documents)
        modified['reusable-release-validation.yml']['jobs']['build-test-pack']['steps'] = [step for step in modified['reusable-release-validation.yml']['jobs']['build-test-pack']['steps'] if step.get('name') != 'OpenTelemetry tests']
        with self.assertRaises(AssertionError):assert_telemetry_test_contract(modified)

    def test_ci_executes_new_mutation_suite(self):
        assert_fixture_runner_contract()

    def test_mutations_cannot_relax_release_gates(self):
        assert_release_contract(self.documents)
        mutations = [
            lambda d: named_step(d['publish-nuget.yml']['jobs']['version']['steps'], 'Require accepted release commit').update({'run': 'echo unchecked'}),
            lambda d: d['publish-nuget.yml']['jobs']['validation']['with'].update({'validation-mode': 'current'}),
            lambda d: d['publish-nuget.yml']['jobs']['publish']['needs'].remove('windows-validation'),
            lambda d: d['publish-nuget.yml']['jobs']['publish']['needs'].remove('postgresql-validation'),
            lambda d: d['publish-nuget.yml']['jobs']['windows-validation'].update({'continue-on-error': True}),
            lambda d: d['publish-nuget.yml']['jobs']['windows-validation']['with'].update({'package-artifact-id': 'old-artifact'}),
            lambda d: named_step(d['reusable-release-validation.yml']['jobs']['build-test-pack']['steps'], 'Pack packages from graph').pop('if'),
            lambda d: named_step(d['reusable-release-validation.yml']['jobs']['build-test-pack']['steps'], 'Verify package metadata release').update({'continue-on-error': True}),
            lambda d: named_step(d['reusable-release-validation.yml']['jobs']['build-test-pack']['steps'], 'Upload replay reports')['with'].update({'path': 'downloaded/artifacts/packages'}),
            lambda d: named_step(d['reusable-release-validation.yml']['jobs']['build-test-pack']['steps'], 'Validate producer package artifact').update({'run': 'echo unchecked'}),
            lambda d: named_step(d['reusable-release-validation.yml']['jobs']['build-test-pack']['steps'], 'Test package artifact validation').update({'run': './eng/tests/validate-package-artifact.Tests.ps1'}),
            lambda d: named_step(d['publish-nuget.yml']['jobs']['publish']['steps'], 'Preflight recoverable published packages').update({'run': 'echo unchecked'}),
            lambda d: named_step(d['publish-nuget.yml']['jobs']['publish']['steps'], 'Publish packages in dependency order').update({'run': 'dotnet nuget push package.nupkg --skip-duplicate'}),
            lambda d: named_step(d['publish-nuget.yml']['jobs']['publish']['steps'], 'Verify published package payloads').update({'run': 'echo available'}),
        ]
        for mutate in mutations:
            with self.subTest(mutation=mutate):
                modified = copy.deepcopy(self.documents)
                mutate(modified)
                with self.assertRaises(AssertionError):assert_release_contract(modified)


if __name__ == '__main__':
    unittest.main()
