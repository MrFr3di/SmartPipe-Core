# SP220-17 implementation evidence

Local implementation is ready for integration review. Release acceptance and checkpoint G promotion remain pending the gates below.

- Base: checkpoint G `4083f4e2b881bce0740ebbe29cf2d40d9cdf1c45`.
- Implementation commit / packaging HEAD: `16197c5b1247671ac41b3bed478f2fc7fa0ef1b2` on `feat/sp220-17-facade-and-migration`.
- Verification date: 2026-10-01; Linux x64; .NET SDK 10.0.303; net10.0.
- Feed: `artifacts/sp220-17-final`; package version 2.2.0; 20 nupkg + 20 snupkg.
- Pack uses the successful Release solution outputs (`--no-build --no-restore`) with native Package Validation enabled for every package. No baseline or compatibility suppression was changed. Later changes are test whitespace, a consumer comment and this evidence/plan.
- [Compatibility inventory](sp220-17-compatibility-matrix.md): exactly 42 facade identities, classified 23 forwarded / 13 retained / 6 removed.
- Facade bundle: 17 direct SmartPipe dependencies, closure of 18 including facade; HealthChecks/OpenTelemetry included, Testing/PostgreSql excluded.

## Verification outcomes

| Check | Local result |
|---|---|
| Immutable baseline files vs checkpoint G | No changes |
| Offline baseline integrity, including package/signature/snapshot checks | Passed (`BASELINE VERIFICATION PASSED`) |
| Locked solution restore | Passed |
| Release solution build with warnings as errors | Passed, 0 warnings / 0 errors |
| Focused ownership tests | RED: seven rejecting assertions failed before fixes; GREEN: 21/21 |
| Bundle contract | RED: expected 17, observed 15; GREEN passed |
| RepositoryChecks after final test formatting | 605/605; ProcessRunnerTests excluded, see environment limitation |
| Facade unit tests | 199/199 |
| Full solution format verification | Passed after fixing new test whitespace |
| Relative Markdown file targets | 66 checked, no missing targets |
| Final nupkg/snupkg hashes | All 40 match immutable pack manifest |
| Final packed graph, current / release | Passed, 20 active packages in both modes |
| Final packed ownership, current / release | Passed, 157 baseline types in both modes |
| Final package metadata, current | Passed, 20 packages |
| Final package metadata, release | Facade passes; 19 SPMETA006 violations for other packages, see release boundary |
| Final release version | Passed, 2.2.0 |
| Final consumer scenarios | Passed, 63/63 non-PostgreSQL scenarios, including trimming / NativeAOT / 7 old-binary scenarios |

The ownership RED cases exercised wrong AssemblyRef destination even when the correct implementation exists elsewhere; unknown facade implementation/forwarder; missing requested nupkg; mismatched target TFM; missing forwarder in a second ref asset; nested generic exported type resolution. Existing tests retain rejection of resurrected removed identities. Destination evidence is nonserialized so immutable baseline snapshot serialization remains stable.

Meta consumer asserts the exact 23 forwarded identities and their implementation assemblies, plus the exact 13 retained public identities. The OpenTelemetry facade consumer references only the facade and runs canonical HealthChecks and OpenTelemetry alongside the retained hosted API. Source probes include null/default/named constructor arguments. Native Package Validation supplies member-level coverage beyond representative consumer calls.

## Artifact hashes

Manifest SHA256: `c647f1912071b4f4c55ababba379eea37a5df3e4968b12d9908a7b178671a366`.

The manifest records all nupkg/snupkg hashes and publish order. These nupkg hashes identify the exact locally tested feed; artifacts are local and have not been published.

| Package | nupkg SHA256 |
|---|---|
| `SmartPipe.Core` | `570d247ddaa1463164d90e05c34b41fc639a0f41392d177feb057a38ed489555` |
| `SmartPipe.Extensions` | `b31720f143ec711d06430c2949dfe128903742d14905cf7ab6fe52dd268e4511` |
| `SmartPipe.Extensions.Channels` | `b1cf0066f83cd26f946ab7e93e8ecd255d94a61506d4c99341c703cea3fd8a03` |
| `SmartPipe.Extensions.Csv` | `8207d19b7e6af1028fcb1961be1a696fc7637fbbd0b2c265bf6d2989fc7d1902` |
| `SmartPipe.Extensions.Dapper` | `adce3b5e006a18e056c9c272a98dadcb0d677c8f6dca69ad6c7976fac9d6e985` |
| `SmartPipe.Extensions.DataAnnotations` | `82d3c3c2a4a13d03142f1926720decfbed9f6d965cb760861292ab45026bae29` |
| `SmartPipe.Extensions.DependencyInjection` | `0dd49a4ae1a5adb369736bcfc5bd0fa6dbaacba9ae725dffdf933b91743418ea` |
| `SmartPipe.Extensions.EntityFrameworkCore` | `a2fe7e5c60bf435137ee14d1fb7f7cb273c62a2779c86702fd8fe7aa2b62b836` |
| `SmartPipe.Extensions.HealthChecks` | `f270ee3b83088105ddcd29124ce56ca4711379fb3605d8311a977a97ccc93f8c` |
| `SmartPipe.Extensions.Hosting` | `980f385b0f9db6759c76b4045e5d96eb97d56aa988f9d37ec184a7f2dd744af3` |
| `SmartPipe.Extensions.Http` | `0a66024b8e94b6b62fc58f8100eee5e73740bfef6cbce903cffefa118d141bda` |
| `SmartPipe.Extensions.Http.Json` | `a243372b4cd972f9eef30292109ea45bd7967a10ed1caac9e4ad5d052c1b4f2f` |
| `SmartPipe.Extensions.Json` | `c7070633cf83b68343977809702e5f1db78b726be72d70b0c03aa55986754c90` |
| `SmartPipe.Extensions.Logging` | `0d6bdb35fb0b529b8cb7dd4aab345832e329241faf9b35b7956c222696051cff` |
| `SmartPipe.Extensions.Mapster` | `19d61aec345ad6a8257457041bff9dc72115347773763a67684299587dc7a5a5` |
| `SmartPipe.Extensions.OpenTelemetry` | `2f1c03c68dfa6b7486614813d75296b5b3ec7a0edd758afce6472b7966fb9f30` |
| `SmartPipe.Extensions.Polly` | `b49cdddda09b19875e5f00729e5d4cabfe5443a75c8fc4587067bab8c8d3ee4f` |
| `SmartPipe.Extensions.PostgreSql` | `8349a70f9c4782ec7c0b38982655e749e537a8d3a5d7ee5ce9e5c067ea61164a` |
| `SmartPipe.Extensions.Transforms` | `544e8219c3f11072804a548f19bfd74d4a4954ed985b879349a11ed782e6f863` |
| `SmartPipe.Testing` | `4d7b0530327d0ba083f3b06018388357ffcefcc70f97532f2f86641d1872c2ae` |

## Binary replacement evidence

All seven binary-compatibility scenarios built the consumer once against signed 2.1.2 packages, refreshed deployment metadata, preserved the consumer DLL hash, replaced the current runtime closure and ran successfully. No consumer rebuild occurred during replacement. Each result is recorded under `artifacts/consumers/<scenario>/<run>/result.json`; its `refresh-deps` command records equal before/after hashes. An independent SHA256 check of each final workspace Consumer.dll after execution matched the same recorded baseline hash in all seven cases.

| Scenario | Unchanged consumer DLL SHA256 | Result |
|---|---|---|
| `csv-facade-binary-2.1.2` | `ac9f47481b21049e5d1e6c86cb6a301214287bebc483953d3b84f3fae40ff6dc` | Passed |
| `dapper-facade-binary-2.1.2` | `de4d8f69082fc84c58c58a3ce0881140c55466101bd89cb43f142dec761f8958` | Passed |
| `dependency-injection-facade-binary-2.1.2` | `413f607ff73003f159a85710ddcad09319d631f009e424fa929b65adc76f1553` | Passed |
| `entity-framework-core-facade-binary-2.1.2` | `fec589d511801c55dcd3ce87eac580c97a0dd99571df15d444bbe3115c63fa82` | Passed |
| `hosting-facade-binary-2.1.2` | `93147b8659b655d4d3df31261f270f9a38348182c1ecd0f7ae02a86a8cccd669` | Passed |
| `legacy-binary-2.1.2` | `831eca66afd445ccd8f7ee211d17653aa2101547d7cc642f49621b60cfd84715` | Passed |
| `mapster-facade-binary-2.1.2` | `14a948deac97fbdff382d47cb2c35662a491a07277347c2146eed3c4b66ddd5d` | Passed |

## Review and remaining gates

A fresh read-only reviewer inspected the complete implementation, inventory, baseline preservation, bundle policy and consumers. No Critical or Important findings. One minor JSON AssemblyRef comment was corrected to describe identity resolution and implementation ownership accurately. The reviewer did not rerun tests.

- Full RepositoryChecks was stopped after its child-process lifecycle fixtures hung for more than eight minutes. Dead descendants were reparented to the environment's non-reaping PID 1. The 605-test bounded run excludes ProcessRunnerTests explicitly; this is not a full-suite pass. Repeat the full suite in CI. The user-profile path fixture passed after providing a writable temporary fixture directory.
- All seven PostgreSQL consumer scenarios were excluded. PostgreSql is optional and outside the facade bundle; its dedicated integration gate remains for CI.
- Run Linux/Windows CI against the exact proposed final commit, including full suites and required PostgreSQL/profile gates. Local tests do not replace remote workflow evidence or human acceptance.
- Aggregate release metadata reports missing release notes for the other 19 packages (`SPMETA006`), an SP220-18 release-preparation dependency. The facade's notes are already packed; no gate was relaxed.
- SP220-18 acceptance, checkpoint promotion, tagging, publishing and branch integration have not been performed.

## Reproduction

Use SDK pinned in `global.json`. All commands run from the repository root. Provision immutable 2.1.2 baseline packages as required by the existing workflow. Choose a fresh output directory for every pack; existing manifests/artifacts cannot be overwritten.

```sh
dotnet restore SmartPipe.Core.slnx --locked-mode
dotnet build SmartPipe.Core.slnx --configuration Release --no-restore -warnaserror
dotnet format SmartPipe.Core.slnx --verify-no-changes --no-restore
dotnet test --project tests/SmartPipe.RepositoryChecks.Tests/SmartPipe.RepositoryChecks.Tests.csproj --configuration Release --no-build
dotnet test --project tests/SmartPipe.Extensions.Tests/SmartPipe.Extensions.Tests.csproj --configuration Release --no-build

dotnet run --project eng/SmartPipe.RepositoryChecks --configuration Release --no-build -- pack-packages --mode release --configuration Release --package-version 2.2.0 --output artifacts/sp220-17-candidate --manifest artifacts/sp220-17-candidate/manifest.json
```

For both `current` and `release`, run `verify-package-graph --mode MODE --packages artifacts/sp220-17-candidate`, `verify-package-ownership --mode MODE --baseline eng/baselines/2.1.2 --packages artifacts/sp220-17-candidate`, and `verify-package-metadata --mode MODE --packages artifacts/sp220-17-candidate` through the same CLI. Then:

```sh
dotnet run --project eng/SmartPipe.RepositoryChecks --configuration Release --no-build -- verify-release-version --mode release --tag v2.2.0 --package-directory artifacts/sp220-17-candidate
dotnet run --project eng/SmartPipe.RepositoryChecks --configuration Release --no-build -- run-consumers --set current --package-directory artifacts/sp220-17-candidate --package-version 2.2.0 --exclude-category postgresql --max-parallelism 4
```

Local bounded RepositoryChecks additionally used `-- --filter-not-class '*ProcessRunnerTests*' --timeout 3m`; this exclusion is an environment workaround, not a CI policy change.
