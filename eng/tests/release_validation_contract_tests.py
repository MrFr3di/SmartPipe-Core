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
    require(inputs.get('prepare-release-assets', {}).get('type') == 'boolean'
            and inputs.get('prepare-release-assets', {}).get('default') is False,
            'release assets must be opt-in so checkpoint validation does not require a final dated changelog')
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
    notes = named_step(steps, 'Prepare release notes')
    checksums = named_step(steps, 'Write release checksums')
    asset_guard = "inputs.prepare-release-assets && inputs.package-artifact-id == ''"
    require(notes.get('if') == asset_guard
            and 'prepare-release-notes' in notes.get('run', '')
            and '--version "$env:PACKAGE_VERSION"' in notes.get('run', '')
            and 'artifacts/packages/RELEASE_NOTES.md' in notes.get('run', ''),
            'publication producer must derive release notes from the exact dated changelog section')
    require(checksums.get('if') == asset_guard
            and "'.nupkg', '.snupkg'" in checksums.get('run', '')
            and 'SHA256SUMS' in checksums.get('run', '')
            and 'SHA256' in checksums.get('run', ''),
            'publication producer must record SHA-256 for every nupkg/snupkg')
    upload = named_step(steps, 'Upload immutable packages and reports')
    require(PRODUCER in upload.get('if', ''), 'replay must not upload another package artifact')
    require(steps.index(pack) < steps.index(notes) < steps.index(checksums) < steps.index(upload),
            'release notes and checksums must be part of the original producer artifact')
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
    workflow = documents['publish-nuget.yml']
    require(set(workflow['on']) == {'workflow_dispatch'}, 'tag pushes must never trigger package publication')
    dispatch_inputs = workflow['on']['workflow_dispatch']['inputs']
    require(set(dispatch_inputs) == {'version', 'publish_nuget', 'recoverable-rerun'},
            'release dispatch must explicitly choose version, publication, and recovery mode')

    jobs = workflow['jobs']
    version_steps = jobs['version']['steps']
    main_guard = named_step(version_steps, 'Require main')
    require('refs/heads/main' in main_guard.get('run', '') and not main_guard.get('continue-on-error'),
            'release workflow must be dispatched from main')
    request = named_step(version_steps, 'Validate requested release')
    request_run = request.get('run', '')
    require('prepare-release-notes' in request_run
            and 'CHANGELOG.md' in request_run
            and 'releaseVersion' in request_run
            and 'stable_core="${VERSION%%-*}"' in request_run
            and 'refs/tags/v$VERSION' in request_run
            and 'recoverable-rerun requires publish_nuget=true' in request_run,
            'release request must validate canonical version, dated changelog, stable-core graph parity, tag reuse, and recovery mode')

    require(jobs['validation']['with'].get('validation-mode') == 'release'
            and jobs['validation']['with'].get('prepare-release-assets') is True,
            'publication producer must select release mode and prepare immutable release assets')
    windows = jobs.get('windows-validation')
    require(isinstance(windows, dict) and windows.get('needs') == ['version', 'validation']
            and windows.get('uses') == './.github/workflows/reusable-release-validation.yml', 'release must require Windows replay')
    require(windows['with'].get('runner-labels') == '["windows-latest"]'
            and windows['with'].get('validation-mode') == 'release'
            and windows['with'].get('package-artifact-id') == '${{ needs.validation.outputs.artifact-id }}'
            and 'prepare-release-assets' not in windows['with']
            and not windows.get('if') and not windows.get('continue-on-error'),
            'Windows release validation must consume producer ID, cannot regenerate release assets, and cannot be optional')
    require(jobs['postgresql-validation']['with'].get('validation-mode') == 'release', 'PostgreSQL must validate release artifact mode')

    publish = jobs['publish']
    require(publish.get('if') == '${{ inputs.publish_nuget }}', 'dry run must skip all publication credentials and side effects')
    require(set(publish['needs']) == {'version', 'validation', 'windows-validation', 'postgresql-validation'}, 'publication must await both Windows and PostgreSQL')
    require(publish.get('environment') == 'nuget-production'
            and publish.get('permissions') == {
                'contents': 'read',
                'id-token': 'write',
                'attestations': 'write',
                'artifact-metadata': 'write',
            }, 'publication retains protected least-privilege OIDC and attestation permissions')

    publish_steps = publish['steps']
    integrity = named_step(publish_steps, 'Validate downloaded package artifact')
    require('-ExpectedMode release' in integrity['run'] and '-ExpectedCommit (git rev-parse HEAD)' in integrity['run'], 'publisher must verify release mode and exact source commit')
    checksum = named_step(publish_steps, 'Verify release asset checksums')
    require(checksum.get('working-directory') == 'artifacts/packages'
            and checksum.get('run') == 'sha256sum --check --strict SHA256SUMS',
            'publisher must verify producer SHA256SUMS before credentials')
    recovery = named_step(publish_steps, 'Preflight recoverable published packages')
    recovery_run = recovery.get('run', '')
    require(recovery.get('if') == 'inputs.recoverable-rerun'
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
    attest = named_step(publish_steps, 'Attest release package provenance')
    require(str(attest.get('uses', '')).startswith('actions/attest@')
            and 'artifacts/packages/*.nupkg' in attest.get('with', {}).get('subject-path', '')
            and 'artifacts/packages/*.snupkg' in attest.get('with', {}).get('subject-path', ''),
            'release package archives must be attested before Trusted Publishing login')
    push = named_step(publish_steps, 'Publish packages in dependency order')
    push_run = push.get('run', '')
    push_env = push.get('env', {})
    require(push_env.get('NUGET_API_KEY') == '${{ steps.nuget-login.outputs.NUGET_API_KEY }}'
            and push_env.get('NUGET_SYMBOL_API_KEY') == '${{ steps.nuget-login.outputs.NUGET_API_KEY }}'
            and push_env.get('RECOVERABLE_RERUN') == '${{ inputs.recoverable-rerun }}'
            and '--api-key' not in push_run and '--symbol-api-key' not in push_run,
            'NuGet credentials must stay in supported environment variables and recovery must be explicit')
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
    login = named_step(publish_steps, 'NuGet login')
    require(publish_steps.index(checksum) < publish_steps.index(recovery) < publish_steps.index(attest) < publish_steps.index(login) < publish_steps.index(push),
            'artifact integrity, recovery provenance and attestation must complete before publication credentials and pushes')
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

    release = jobs.get('github-release')
    require(isinstance(release, dict)
            and release.get('if') == '${{ inputs.publish_nuget }}'
            and release.get('needs') == ['version', 'validation', 'publish']
            and release.get('permissions') == {'contents': 'write'},
            'GitHub Release must run only after successful NuGet publication with contents write only')
    release_steps = release['steps']
    release_download = named_step(release_steps, 'Download validated packages')
    require(release_download.get('with', {}).get('artifact-ids') == '${{ needs.validation.outputs.artifact-id }}',
            'GitHub Release must reuse the exact producer artifact')
    release_checksum = named_step(release_steps, 'Verify release asset checksums')
    create = named_step(release_steps, 'Create release tag and publish GitHub Release')
    create_run = create.get('run', '')
    require(release_checksum.get('run') == 'sha256sum --check --strict SHA256SUMS'
            and 'git/matching-refs/tags/${tag}' in create_run
            and '-f sha="${GITHUB_SHA}"' in create_run
            and 'gh release create' in create_run
            and '--draft' in create_run
            and '--notes-file artifacts/packages/RELEASE_NOTES.md' in create_run
            and 'artifacts/packages/SHA256SUMS' in create_run
            and 'gh release edit "${tag}" --repo "${GITHUB_REPOSITORY}" --draft=false' in create_run,
            'final job must create an exact immutable tag and publish a changelog-backed draft release only after NuGet')

    for name, job in jobs.items():
        if name == 'publish':
            continue
        if name == 'github-release':
            require(job.get('permissions') == {'contents': 'write'}, 'GitHub Release may hold contents write only')
        else:
            require(job.get('permissions', {'contents': 'read'}) == {'contents': 'read'}, 'validation cannot request write/OIDC permissions')


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
            lambda d: named_step(d['publish-nuget.yml']['jobs']['version']['steps'], 'Require main').update({'run': 'echo unchecked'}),
            lambda d: d['publish-nuget.yml']['on'].update({'push': {'tags': ['v*']}}),
            lambda d: d['publish-nuget.yml']['jobs']['publish'].pop('if'),
            lambda d: d['publish-nuget.yml']['jobs']['github-release'].update({'needs': ['version', 'validation']}),
            lambda d: named_step(d['reusable-release-validation.yml']['jobs']['build-test-pack']['steps'], 'Prepare release notes').update({'if': PRODUCER}),
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
