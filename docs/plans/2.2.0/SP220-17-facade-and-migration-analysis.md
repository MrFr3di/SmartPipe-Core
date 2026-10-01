# SmartPipe.Core 2.2.0: Checkpoint G / SP220-17 — анализ и план

Дата проверки: 30 сентября 2026. Статус документа: предложение для реализации, не свидетельство завершения SP220-17.

Обновление 1 октября 2026: подготовка G и workflow filters завершены merge PR #113,
checkpoint G base `4083f4e2`. Исторические G.0 и раздел 4.1 ниже не требуется
повторять. Текущая реализация и проверяемые шаги ведутся в
[implementation plan](SP220-17-implementation.md) и
[evidence](../../implementation/2.2.0/sp220-17-evidence.md); прежние результаты
базы не подменяют проверки нового candidate.

## 1. Проверенная база

Репозиторий: https://github.com/MrFr3di/SmartPipe-Core

| Параметр | Факт |
|---|---|
| Рабочая ветка релиза | `release/2.2.0` |
| Проверенный SHA | `cf6f16d4b34db9fc4d090e94c26e364cae5192da` |
| Последнее изменение | PR #112: runtime hardening после продвижения F |
| Продвижение F | PR #111, commit `90753d10e64928f26a3786e29dc7d9f06884de93` |
| main | `8e79902d22de714f493582946f7c260462b0895e`; не использовать как базу G |
| G | Создана `sp220/checkpoint-g` от проверенной release-базы |
| PR при начале проверки | #93–104, Dependabot к main; их изменения консолидируются отдельным PR к G |
| CI проверенного SHA | success: https://github.com/MrFr3di/SmartPipe-Core/actions/runs/36735414392 |
| CodeQL проверенного SHA | success: https://github.com/MrFr3di/SmartPipe-Core/actions/runs/36735413923 |
| SDK проекта | `10.0.303`, rollForward disabled |
| Целевая платформа | `net10.0` |
| Пакеты / consumers | 20 узлов package graph / 70 consumer scenarios |
| Локальная проверка | SDK 10.0.303; locked restore и Release build с warnings as errors прошли; ограничения тестов ниже |

У CI подтверждены успешные build-test-pack, baseline Windows, JSON Windows, Hosting Linux/Windows, CSV Linux/Windows, PostgreSQL integration 18.6/17.11 и package consumers 18.6. Это доказательство текущей базы, а не будущих изменений G и не финальной release validation SP220-18.

`AGENTS.md` в проверенном checkout не обнаружен. Действуют ADR и `docs/governance/2.2.0-branch-and-review-policy.md`.

## 2. Что изменилось относительно приложенных планов

Приложенный общий план имеет старую нумерацию: SP220-16 — facade, SP220-17 — release validation. Репозиторий уже добавил PostgreSQL и использует SP220-17 — facade/migration, SP220-18 — release validation. Актуальный master plan и ADR репозитория имеют приоритет для этой задачи.

Особенно нельзя механически исполнять старые указания о переносе DI-фабрики, Hosting/Health и forwarding старого Polly API:

- ADR-0002 сохраняет физически связанный legacy DI/Hosting/Health кластер в `SmartPipe.Extensions`.
- ADR-0004 удаляет четыре HTTP generic-типа и `HttpSelectorStreamingMode` без wrappers/forwarders.
- Поправка ADR-0004 удаляет `PollyResilienceTransform<T>`: старый callback не исполнял inner operation.
- G включает SP220-17–18, F уже включал SP220-13–16.

Сейчас master plan всё ещё формулирует acceptance как «old binary запускается». Её надо уточнить: запускаются binaries, использующие сохраняемый API; удалённые HTTP/Polly identities требуют миграции и перекомпиляции. Нельзя подписывать полную бинарную совместимость 2.1.2 → 2.2.0.

## 3. Реальная готовность фасада

Baseline `eng/baselines/2.1.2/package-assets.json` фиксирует:

- Core: 108 собственных public types;
- Extensions: 35 собственных public types и 7 JSON-forwarders;
- Json: 14 собственных public types.

У фасада исходно 42 экспонируемых identity. Существующая ownership policy задаёт для них следующий итог:

| Категория | Количество | Итог |
|---|---:|---|
| Перенос из Extensions в leaves | 16 | Type forwarding |
| Уже существовавшие JSON-forwarders | 7 | Сохранить forwarding в Json |
| Legacy DI/Hosting/Health и service extensions | 13 | Реализация остаётся в Extensions |
| HTTP + старый Polly no-op | 6 | Удаление по ADR-0004 |
| Всего | 42 | 23 forwarded + 13 retained + 6 removed |

В исходниках уже присутствуют 23 `TypeForwardedTo` в шести файлах: `Sp22007TypeForwarders.cs`, `JsonTypeForwarders.cs`, `CsvTypeForwarders.cs`, `DapperTypeForwarders.cs`, `EntityFrameworkCoreTypeForwarders.cs`, `MapsterTypeForwarders.cs`.

### Forwarding matrix

| Типы; существующие namespaces сохраняются | Assembly реализации |
|---|---|
| `ChannelMerge` | SmartPipe.Extensions.Channels |
| `CompositeTransform<T>`, `CompressionAlgorithm`, `CompressionTransform`, `ConditionalTransform<T>`, `FilterTransform<T>` | SmartPipe.Extensions.Transforms |
| `FilterValidationExtensions`, `ValidationTransform<T>` | SmartPipe.Extensions.DataAnnotations |
| `LoggerSink<T>` | SmartPipe.Extensions.Logging |
| `CsvFileSource<T>`, `CsvFileSink<T>`, `CsvTransform<TIn,TOut>` | SmartPipe.Extensions.Csv |
| `DapperSelector<T>`, `DbSink<T>` | SmartPipe.Extensions.Dapper |
| `EfCoreSelector<T>` | SmartPipe.Extensions.EntityFrameworkCore |
| `MapsterTransform<TIn,TOut>` | SmartPipe.Extensions.Mapster |
| `DeadLetterSource<T>`, `JsonFileSource<T>`, `DeadLetterSink<T>`, `DeadLetterWriteException`, `DeadLetterWriteFailureMode`, `JsonFileSink<T>`, `JsonTransform<TIn,TOut>` | SmartPipe.Extensions.Json |

### Retained cluster

`ISmartPipeDefinition<,>`, `ISmartPipeFactory<,>`, `ISmartPipeRunHealthMonitor<,>`, `SmartPipeDefinitionBuilder<,>`, `SmartPipeDefinition<,>`, `SmartPipeFactory<,>`, `SmartPipeHealthCheckOptions`, `SmartPipeHealthSnapshot`, `SmartPipeHostedFailureBehavior`, `SmartPipeHostedServiceOptions`, `SmartPipeHostedService<,>`, `SmartPipeRunHealthMonitor<,>`, `SmartPipeServiceCollectionExtensions`.

`SmartPipeHealthCheck<,>` — внутренний тип; его отсутствие в public baseline корректно. Не превращать его в новый public API ради матрицы.

Название стратегии `obsolete-wrapper` не означает, что все 13 типов уже отмечены `[Obsolete]`. В текущем кластере obsolete помечен synchronous `Start` в интерфейсе и реализации. Добавление атрибутов всем типам изменит предупреждения у source consumers с TreatWarningsAsErrors. Для SP220-17 сохранять существующую diagnostic policy; не добавлять массово obsolete без отдельного принятого решения.

## 4. Основные пробелы

### 4.1. CI не поддерживает новую ветку

`.github/workflows/ci.yml`, `codeql.yml`, `dependency-review.yml` не включают `sp220/checkpoint-g`. Без исправления task PR к G не получает штатные проверки. Это первая необходимая работа, до изменения package graph.

### 4.2. Bundle ещё не совпадает с releaseDependencies

В csproj и currentDependencies отсутствуют `SmartPipe.Extensions.HealthChecks` и `SmartPipe.Extensions.OpenTelemetry`, уже обязательные в releaseDependencies. Сейчас фасад напрямую ссылается на 15 SmartPipe пакетов; итог требует 17: Core + 16 runtime leaves.

Это число прямых обязательных edges, а не полный размер closure: вместе с самим фасадом будет 18 SmartPipe packages. Всего в репозитории 20, потому что `SmartPipe.Testing` и `SmartPipe.Extensions.PostgreSql` остаются вне bundle. PostgreSql — осознанно optional; Testing — только для тестовых проектов.

OpenTelemetry consumer сейчас напрямую ссылается и на фасад, и на OTel leaf. Поэтому он не доказывает доступность OTel через bundle.

### 4.3. Ownership gate не проверяет адресата forwarder

`ManagedAssemblyInspector` сохраняет full names forwarders, но не destination AssemblyRef. `TypeForwarderReader` сворачивает их в type → packages, а `OwnershipValidator` проверяет наличие forwarder у facade и реализации у target.

Следствие по исходному коду: неверный destination forwarder может пройти ownership gate, если нужный full name реализован в ожидаемом пакете. Runtime consumer может поймать такой дефект, но metadata gate должен диагностировать его непосредственно.

Кроме того, текущий snapshot объединяет TFM/asset-family. При будущем multi-targeting дефект одной assembly способен быть скрыт другой. Сейчас baseline одноцелевой net10.0; проверку следует выполнять по конкретным assets, без заявления о существующей multi-TFM поддержке.

### 4.4. Проверка завершённости идёт в основном от baseline

Validator проходит baseline types и ловит повторные implementations. Однако он не запрещает новый unique public type в фасаде, не проверяет лишние forwarders и не обеспечивает ожидаемый точный retained set. Wildcard `SmartPipeDefinition*` удобен для группировки, но не является freeze-контрактом public surface.

`ReadPackagesAsync` молча пропускает отсутствующие nupkg. Package graph gate частично компенсирует это; ownership command тоже должен явно падать при отсутствии required active/release package.

### 4.5. Source/runtime tests уже существуют, но не дают complete matrix

Имеются source и old-binary consumers для DI, Hosting, CSV, Dapper, EF; Mapster old-binary; общий `legacy-binary-2.1.2`. `extensions-meta` проверяет reflection forwarding только для пяти типов SP220-07.

Нужно довести metadata coverage до всего сохраняемого API и добавить точечные runtime probes там, где имеющиеся consumers не упражняют сигнатуры. Не создавать 23 тяжёлых однотипных executable scenarios.

### 4.6. README и описание пакета устарели

README фасада по-прежнему содержит installation 2.1.2, requirements Core 2.1.2, старые представления Hosting/Health и неполное объяснение bundle. Description csproj представляет его как обычный integration package, акцентирует только JSON split.

Основной миграционный документ уже существует: `docs/migration/2.2.0-integration-packages.md`. Расширять его как единственную точку входа; не создавать ещё один конкурирующий документ с тем же назначением.

## 5. Выбранный подход

Рекомендация: завершить существующий compatibility facade, сохранив принятый retained cluster, и усилить существующий RepositoryChecks.

Альтернативы:

| Подход | Оценка |
|---|---|
| Завершить текущий facade + точная verification matrix | Рекомендуется: минимальный архитектурный риск, использует уже принятые ADR и consumers |
| Перенести DI/Hosting/Health в leaves и forward всё | Противоречит ADR-0002; сцепленные конструкторы и Start требуют другого решения, не SP220-17 |
| Сделать Extensions чистым metapackage без DLL | Ломает сохранённые identities и старые binaries; задача для будущего major при отдельном контракте |

Фасад сохраняет DLL. Type forwarding — механизм переноса типа между assemblies, а не способ изменить тип, namespace, generic arity или отдельные методы. Сохранять точные constructor/method signatures, generic constraints, optional defaults и имена параметров для named arguments.

Важное различие: C#-код, перекомпилированный с `using SmartPipe.Extensions`, не доказывает старый AssemblyRef. Отдельный consumer, однажды собранный против 2.1.2, обязателен.

## 6. Границы изменения dependencies

Обязательно добавить два SmartPipe ProjectReference, синхронизировать current/release graph, expectedSmartPipeDependencies consumer manifests и lock files.

Уже вынесенные CsvHelper, Dapper, Mapster и EF Core могут поступать через leaves. Их прямые ссылки из фасада — кандидаты на удаление после проверки compilation, nuspec и восстановленного closure. Logging тоже проверять по фактическим retained signatures, а не по старому forwarded LoggerSink.

Не удалять без анализа dependency closure. Microsoft прямо относит package dependencies к контракту: исчезновение dependency может сломать downstream consumers. Удаление прямого edge безопаснее, когда библиотека остаётся транзитивно доступной с совместимой версией. Отдельно задокументировать уже принятую замену Microsoft.Extensions.Resilience на Polly.Core; это не автоматическая совместимость чужого API.

Сохранить dependencies, которые нужны retained cluster: Options, Hosting.Abstractions, Diagnostics.HealthChecks. Не скрывать runtime dependencies через PrivateAssets. Internal SmartPipe versions — minimum 2.2.0; не вводить exact/upper ranges. Не менять SDK и версии сторонних библиотек в этой задаче.

## 7. Последовательность реализации

### G.0 — старт checkpoint и CI

1. Fetch; повторно проверить head release, историю F/#111/#112 и чистоту checkout.
2. Создать integration branch `sp220/checkpoint-g` от актуального release HEAD, содержащего принятое F и subsequent hardening. Не стартовать от более старого `origin/sp220/checkpoint-f`.
3. Изолированная task branch `build/sp220-g-governance`, PR base — G.
4. Добавить G в push/PR filters CI и CodeQL, PR filter Dependency Review.
5. Уточнить branch policy: baseline G включает post-F hardening. Обновить двусмысленный acceptance master plan.
6. Поскольку первые checks ещё не настроены на base G, bootstrap выполнять через разрешённый workflow_dispatch на конкретном ref с проверкой returned head SHA и review; не считать отсутствие checks зелёным результатом. При необходимости отдельного bypass следовать audit policy, не обходить молча.
7. После проверки и review интегрировать governance в G.

Ветки и PR в ходе данного анализа не создавались.

### SP220-17.1 — exact compatibility inventory

Файлы: `eng/package-ownership.json`, `docs/package-ownership.md`, новый отчёт `docs/implementation/2.2.0/sp220-17-compatibility-matrix.md`, тесты Ownership.

Источники: immutable package-assets baseline, существующий ownership manifest, metadata текущих packed assemblies, PublicAPI. Таблица — производное представление существующих источников, а не второй authoritative manifest.

Для каждого baseline full name записать origin exposure assembly, original implementation owner, strategy, expected current owner, forwarder destination, source/binary status, consumer evidence, ADR удаления, replacement. Для JSON различать baseline exposure из Extensions и физический owner Json.

Успех: все baseline types классифицированы; facade rows дают 23/13/6; current facade public implementations ровно retained set; отсутствие unclassified entries. Новые leaf API не ошибочно объявляются baseline API.

### SP220-17.2 — metadata/ownership enforcement

Изменять: `NuGet/ManagedAssemblyInspector.cs`, `NuGet/PackageAssetSnapshot.cs`, `Ownership/TypeForwarderReader.cs`, `OwnershipModels.cs`, `OwnershipValidator.cs`, `Commands/VerifyPackageOwnershipCommand.cs`; соответствующие tests.

Расширять текущие metadata модели additive способом; baseline files/schema/hashes не перегенерировать. Destination information текущих assemblies сохранять вместе с source assembly, TFM и asset family; old baseline остаётся читабельным без нового поля. Отразить новую модель в source-generated JSON context, если сериализуется.

Проверять:

- destination assembly forwarder совпадает с ожидаемым implementation assembly;
- target type действительно существует, implementation единственная;
- facade не содержит implementation forwarded type;
- каждый разрешённый retained type остаётся в facade и не forwarded;
- removed type отсутствует во всём current graph;
- неизвестный public implementation/forwarder в facade запрещён;
- required package отсутствует → failure;
- циклы/неразрешимые forwarding chains диагностируются, если модель поддерживает chain; для текущего facade ожидается прямой target;
- nested exported entries корректно следуют parent ExportedType до AssemblyRef, а не теряются из-за проверки одного Forwarder-bit.

RED→GREEN fixtures: wrong destination; missing destination assembly; missing type; new facade public type; duplicate implementation; missing package; resurrected HTTP/Polly type; nested/generic case. Проверять ошибки валидатора, а не только строку source атрибута.

Не писать собственный универсальный ApiCompat: member-level compatibility остаётся за Microsoft Package Validation.

### SP220-17.3 — facade graph и package metadata

Файлы: `src/SmartPipe.Extensions/SmartPipe.Extensions.csproj`, `packages.lock.json`, `eng/package-graph.json`, `eng/consumer-scenarios.json`, facade README; dependent lock files при необходимости.

Добавить HealthChecks/OTel; сохранить все 23 forwarders; удалить только доказанные лишние direct external references; точный graph current согласовать с release. Testing/PostgreSql не добавлять в bundle.

Description: compatibility facade/convenience bundle; предпочтение specific packages для нового кода; retained API и intentional breaks. Не писать «full backward compatible» и «AOT-compatible» для broad bundle.

Успех: 17 прямых обязательных SmartPipe dependencies; facade closure содержит 18 SmartPipe IDs; thin leaf consumers не содержат `SmartPipe.Extensions`; graph/nuspec/assets совпадают; removed identities не возвращаются.

### SP220-17.4 — matrix consumers

Расширить `extensions-meta` до exact forwarding set и expected owner assertions для всех 23. Assembly facade получить через сохранённый facade type; `typeof(forwardedType).Assembly` возвращает leaf, а не facade.

Сохранить существующие old-binary scenarios. В общий старый consumer добавить отсутствующие representative вызовы Compression/Conditional и JSON identities, если точный baseline API это допускает. В source consumer добавить constructor calls с null/default и named arguments для перегруженных legacy API. Добавить source counterpart Mapster при отсутствии достаточного покрытия общего meta consumer. Frozen constructor graph DI/Hosting/Health должен по-прежнему выполняться без sync-over-async bridge.

Для bundle OTel проверки убрать прямую SmartPipe OTel leaf reference из отдельного bundle consumer и использовать его transitively; OTel SDK/Microsoft Hosting consumer references допустимы. Дополнительно проверить наличие и API HealthChecks через bundle. Canonical DI/Hosting APIs не подменять legacy overloads; примеры использовать явные namespaces при конфликтующих extension methods.

Binary procedure уже реализован в `ConsumerScenarioRunner`:

1. Проверить SHA baseline nupkg.
2. Восстановить baseline dependency graph и собрать consumer один раз против 2.1.2.
3. Обновить deployment metadata для текущих пакетов; убедиться, что consumer DLL SHA неизменен.
4. Заменить SmartPipe runtime assemblies из текущих nupkg.
5. Запустить без новой сборки.

Сохранить этот протокол и event evidence. Проверить версии и происхождение загруженных SmartPipe assemblies, исключить случайное использование 2.1.2 DLL. Проверка `.deps.json` после обновления тоже должна подтверждать текущий closure.

Для шести removed identities достаточно строгой metadata absence + шести target-specific ApiCompat suppressions + компилируемых migration examples. Не расширять generic consumer runner новым режимом expected runtime failure ради демонстрации принятого break.

### SP220-17.5 — единый migration guide

Расширить `docs/migration/2.2.0-integration-packages.md`, исправить `src/SmartPipe.Extensions/README.md`, связать root README/getting-started/package ownership.

Обязательные разделы:

1. Обновление всех SmartPipe IDs согласованно до 2.2.0, net10.0 prerequisite.
2. Минимальное обновление с facade для сохраняемого API.
3. Переход на specific leaves; package names и сохранение legacy namespaces.
4. Source/binary/behavioral compatibility — разные гарантии.
5. Явный список шести removed types, replacements и требование rebuild.
6. Legacy synchronous Start и переход к canonical StartAsync; per-run scope и lifecycle.
7. HTTP request/response ownership, JSON JsonTypeInfo, отсутствие implicit retry.
8. Polly inner ownership и множитель retry budgets.
9. DI registration, hosted orchestration, keyed health checks — tested before/after recipes.
10. AOT по конкретным leaves; broad facade без blanket guarantee.
11. Optional PostgreSql и test-only Testing.
12. Troubleshooting: MissingMethod/TypeLoad, mismatched package versions, старые DLL, stale deployment metadata.

Примеры проверять как source consumers из packed feed. Не публиковать пример, который только «выглядит правильно». Исторический guide JSON split 2.1.2 сохранить, не переписывать историю релиза.

### SP220-17.6 — приёмка и evidence

Минимальная лестница:

- focused RepositoryChecks Ownership/NuGet/Consumers tests;
- locked restore + Release build фасада и affected projects с warnings-as-errors;
- pack из текущего graph, сохранить native validation against baseline 2.1.2;
- verify-package-graph и verify-package-ownership в current и release mode для готового G package graph;
- source/binary/meta/direct scenarios, затронутые graph изменения;
- CI на Linux и Windows для compatibility consumers, одинаковый candidate SHA;
- миграционные примеры и ссылки;
- API/package maintainer + docs/consumer reviewer; CI reviewer при workflow changes.

Не применять strict baseline equality, запрещающую новые canonical API; не разрешать глобальные CP0001/NoWarn suppressions. Проверить актуальность ровно шести intentional-removal suppressions; новые suppressions требуют нового решения. Для разрешения forwarded references использовать штатный механизм SDK; при реальной ошибке resolver настроить `PackageValidationReferencePath`, не отключать проверки.

Записать evidence в `docs/implementation/2.2.0/sp220-17-evidence.md`: SHA, SDK, run URL/ID/event, ОС, nupkg hashes, forwarded/retained/removed counts, source/binary scenarios и результаты, consumer DLL before/after SHA, inspected dependency closure. До выполнения это план, не «passed».

После SP220-17 можно приступать к SP220-18 внутри G. G не продвигать на release по одному facade PR: полноценный checkpoint acceptance включает SP220-18.

## 8. Что не входит в SP220-17

- Новая runtime архитектура, повторный Core refactor и новые integrations.
- Пересмотр принятых HTTP/Polly removals или восстановление shims.
- Перенос legacy cluster вопреки ADR-0002.
- Массовое добавление Obsolete attributes и новые legacy overloads.
- Новый source generator type forwarders или новый параллельный ownership manifest.
- Blanket AOT для EF/Mapster/CSV/bundle.
- .NET 11 migration, update всех dependencies, обработка Dependabot PR.
- Release tag, Trusted Publishing, NuGet publication, merge в main.

SP220-18 отдельно подтверждает полный release artifact и release/publication gates. Успешный SP220-17 не даёт права назвать весь 2.2.0 релиз завершённым.

## 9. Практика Microsoft, проверенная 30.09.2026

1. [Type forwarding](https://learn.microsoft.com/en-us/dotnet/standard/assembly/type-forwarding): перенос класса между assemblies с сохранением запуска ранее собранного consumer, при наличии old facade DLL и target assembly.
2. [Change rules](https://learn.microsoft.com/en-us/dotnet/core/compatibility/library-change-rules): namespace/name/signature changes рассматриваются отдельно от допустимого переноса assembly.
3. [Package Validation](https://learn.microsoft.com/en-us/dotnet/fundamentals/apicompat/package-validation/overview): baseline, framework/runtime API checks после pack. Дополняет runtime probes, не заменяет их.
4. [Baseline validator](https://learn.microsoft.com/en-us/dotnet/fundamentals/apicompat/package-validation/baseline-version-validator): сравнение с опубликованным стабильным пакетом.
5. [NuGet package compatibility rules](https://learn.microsoft.com/en-us/dotnet/standard/library-guidance/nuget-package-compatibility-rules), обновлено 08.04.2026: не понижать assembly version; dependency availability — часть контракта; осторожность с удалениями.
6. [Dependencies](https://learn.microsoft.com/en-us/dotnet/standard/library-guidance/dependencies): минимизировать ненужные edges, избегать exact/upper ranges без причины.
7. [SDK MSBuild properties](https://learn.microsoft.com/en-us/dotnet/core/project-sdk/msbuild-props): PackageValidationReferencePath, ApiCompat rules/suppressions, strict modes.

Это применимые официальные правила на дату анализа. Возраст механизма TypeForwardedTo не делает его устаревшим; замена на reflection/AssemblyResolve здесь не даёт преимуществ и ухудшает проверяемость.

## 10. Текущий статус

| Поле | Значение |
|---|---|
| Last verified HEAD | release/2.2.0 @ cf6f16d4b34db9fc4d090e94c26e364cae5192da |
| Current milestone | SmartPipe.Core 2.2.0; подготовка Checkpoint G |
| Current slice | SP220-17 design + консолидация dependency PR; изменения фасада ещё не реализованы |
| Open PR | Общий PR к G публикуется; #93–104 заменяются им |
| Last CI | CI 36735414392 success; CodeQL 36735413923 success на этом SHA |
| Acceptance | База проверена через repository CI; SP220-17 не принят |
| Known blockers | Публикация разрешена пользователем; полный candidate CI ещё не подтверждён; compatibility/package gaps выше |
| Next allowed action | Отправить общий commit, открыть PR к G, продолжить фиксы по CI; затем SP220-17.1–6 |
| Last updated from repository | 2026-09-30; исходники и GitHub проверены в этом сеансе |

## 11. Результат подготовки G и объединения PR

Создана `sp220/checkpoint-g` от `cf6f16d4`. Подготовлена ветка `build/sp220-g-consolidated-dependencies`: предложения #93–104 перенесены в central package management принятой release-базы. #101 не применяется: NSubstitute в release больше не используется. Исправлены несовместимый атрибут xUnit 4, nullable-matchers Moq и наблюдение test cancellation token в timeout-тестах.

SDK 10.0.303 установлен для проверки. Locked restore и Release build с `-warnaserror` проходят. Core: 1283/1283. Прямой запуск тестовых сборок: 17 suites проходят; OpenTelemetry имеет четыре одинаковых падения на кандидате и исходной release-базе. RepositoryChecks блокируется запретом сокетов; PostgreSQL требует БД. Facade suite превысил локальный лимит 180 секунд. Полная приёмка кандидата остаётся за CI; SP220-17 и SP220-18 не завершены.

Трассировка предложений и исправлений: `docs/implementation/2.2.0/checkpoint-g-dependency-consolidation.md`.

Пользователь явно разрешил публикацию общей ветки и закрытие заменённых PR 30.09.2026. Проверки кандидата и SP220-17 остаются отдельными критериями приёмки.
