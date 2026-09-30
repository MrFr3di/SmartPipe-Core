# Checkpoint F Improvements Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans or superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Закрыть PostgreSQL release gate и сократить лишние сборки, копирование NDJSON и стоимость проверки каналов без изменения публичного API.

**Architecture:** Общая PostgreSQL validation получает уже собранный immutable package artifact; publishing зависит от её результата. Shared JSON framer сохраняет формат и ownership записей, но делает одну копию нормальной записи и не копирует содержимое oversized записи. Проверка PostgreSQL channel duplicates использует ordinal HashSet.

**Tech Stack:** .NET SDK 10.0.303, C# / net10.0, Npgsql 10.0.3, PostgreSQL 18.6 и 17.11, xUnit v3, BenchmarkDotNet, GitHub Actions, PowerShell, Python / ruamel.yaml 0.18.16.

**Spec:** Раздел «Решения и ограничения» этого документа; исходные контракты — [master architecture plan](../2.2.0-extension-architecture.md), [branch/review policy](../../governance/2.2.0-branch-and-review-policy.md), SP220-15 и SP220-18.

**Status:** Исправления повторного ревью и реализация всех четырёх улучшений включены в PR #111, вместе с bounded consumer concurrency и targeted restores. Локальные проверки и контролируемые измерения описаны в [implementation evidence](checkpoint-f-implementation-evidence.md). Ниже отмечены выполненные этапы; remote CI, полная серия performance runs и финальная release validation остаются отдельными evidence gates.

## Global Constraints

- SDK строго `10.0.303`; locked restore; production dependency versions не менять.
- Существующие публичные сигнатуры и package boundaries сохранять; новых production dependencies не добавлять.
- PostgreSQL primary `18.6`, compatibility `17.11`, provider `Npgsql` `10.0.3`.
- Серверные проверки не должны пропускаться при недоступном PostgreSQL: `SMARTPIPE_POSTGRES_OPTIONAL=0` и non-empty test gates обязательны.
- Borrowed NpgsqlDataSource/HttpClient не мутировать и не освобождать.
- Сохранять same-repository PR guards, `contents: read`, `persist-credentials: false`, полные SHA action refs и текущий Trusted Publishing flow.
- Публикуемые nupkg должны быть теми же файлами, которые прошли consumers и integrity checks. Повторная упаковка после проверки недопустима.
- Не публиковать release tag, пакеты или изменения branch protection при выполнении этого плана без отдельного соответствующего задания.

## Review Focus

1. Пропавший/изменённый package artifact либо неверная версия должны блокировать consumers и publication, а не вызывать repack или fallback.
2. Untrusted fork PR не должен получить доверенную artifact/consumer lane или publishing credentials.
3. BOM, CRLF, whitespace, UTF-8 split и oversize followed by valid record должны иметь прежние semantics и ownership.
4. Потребитель вправе удержать Bytes предыдущей NDJSON-записи после MoveNext: следующая запись не должна её менять.
5. PostgreSQL case-sensitive channel names, входная collection mutation и cancellation должны сохранять прежнее поведение.

## Решения и ограничения

### Порядок и приоритет

| Очерёдность | Работа | Причина / gate | Зависимость | Ответственная роль |
|---|---|---|---|---|
| 1 | PostgreSQL release validation | Обязательно до публикации 2.2.0 / SP220-18 | Проверенный release artifact уже существует | CI/release + PostgreSQL maintainer |
| 2 | Общий artifact для PR consumers | Убирает второй solution build/pack; проверяет те же пакеты | Работа 1 | CI/release maintainer |
| 3 | NDJSON allocations | Наибольший потенциальный memory gain; нужен benchmark | Независима от 1–2 | JSON/transport maintainer |
| 4 | Channel duplicates HashSet | Небольшая локальная оптимизация для большого n | Независима | PostgreSQL maintainer |

По заданию пользователя изменения собраны в существующем PR #111 отдельными reviewable commits. Release publication остаётся отдельным действием.

### Выбранные подходы

- **Release gate:** reusable PostgreSQL workflow вместо копирования CI lane в publishing. Это сохраняет одну реализацию matrix и уменьшает риск расхождения branch/tag проверок.
- **Artifact reuse:** получать immutable artifact текущего workflow run. Не восстанавливать packages из cache и не repack при отсутствии artifact. Unit/integration matrix остаётся параллельной; consumer phase ждёт artifact.
- **NDJSON:** оставить `byte[] Bytes` и текущий iterator API; использовать buffer span и единственную итоговую копию. Не возвращать pooled/shared memory наружу: это изменило бы ownership.
- **Channels:** `HashSet<string>(StringComparer.Ordinal)` после полного defensive copy и blank-validation pass. Результирующий массив сохраняет порядок; trimming/case folding не вводятся.

## Task 1: PostgreSQL release gate на публикуемом artifact

**Files:**
- Create: `.github/workflows/reusable-postgresql-validation.yml`.
- Create: `eng/validate-package-artifact.ps1` и `eng/tests/validate-package-artifact.Tests.ps1`.
- Modify: `.github/workflows/publish-nuget.yml`, `eng/tests/workflow_contract_tests.py`.

**Interfaces:**
- Новый reusable workflow принимает `package-version: string`, `artifact-id: string` как обязательные `workflow_call` inputs.
- Integrity script: `param([string]$ArtifactRoot, [string]$ExpectedVersion, [string]$GraphPath)`. Manifest и archive paths относительно корня artifact, а не текущего package feed directory. Exit 0 только при проверенных files/hashes/version; иначе exit 1 без раскрытия credentials.
- Использовать текущую manifest schema 1: `version`, `packages[].id`, `version`, `nupkgPath`, `nupkgSha256`, `snupkgPath`, `snupkgSha256`, `publishOrder`.
- Производит successful reusable-workflow check; publication требует этот check через `needs`.

- [x] **Step 1 — Написать RED contract/mutation tests.** Tag publish без PostgreSQL dependency, убранная версия 17.11, `SMARTPIPE_POSTGRES_OPTIONAL=1`, пустой consumer selector и consumer repack вместо artifact download должны отвергаться. Для integrity script: missing file, изменённый байт, несовпадающая version, path escape (`../outside.nupkg`) и duplicate package ID → exit 1; валидный fixture → exit 0.
- [x] **Step 2 — Проверить RED.** `python eng/tests/workflow_contract_tests.py` и `pwsh -NoProfile -File eng/tests/validate-package-artifact.Tests.ps1` должны обнаружить отсутствие release gate/validator. Отделять assertion failure от syntax/import errors.
- [x] **Step 3 — Добавить workflow и integrity validator.** Matrix service-container tests на Ubuntu для 18.6/17.11; locked restore/build только PostgreSQL test project. На primary 18.6 download указанного artifact текущего run, выполнить validator, restore/build RepositoryChecks и `run-consumers --set current --category postgresql` над `artifacts/packages`. Семь consumers, включая trim/NativeAOT и compositions, должны выполниться. Не выполнять solution repack.
- [x] **Step 4 — Подключить publishing.** В `publish-nuget.yml` добавить `postgresql-validation`, зависящий от `version` и `validation`, передать тот же version/artifact ID. `publish.needs` → `[version, validation, postgresql-validation]`. Не менять NuGet login и OIDC/environment permissions. Сохранить approved required-check names либо согласовать их миграцию с maintainer до merge.
- [ ] **Step 5 — Проверить GREEN и fail-closed.** Workflow contracts и integrity fixtures зелёные. В доверенном validation-only CI run реальные PostgreSQL lanes успешны. Искусственно повреждённый fixture artifact блокирует consumers; тест dependency graph подтверждает блокировку publish. Не создавать новый release tag и не запускать реальную публикацию для проверки.
- [ ] **Step 6 — Зафиксировать evidence и commit.** Candidate/build SHA, workflow run URL/ID, artifact name/ID и hash manifest, обе server versions, семь consumer results. Commit: `ci: gate NuGet publication on PostgreSQL artifact validation`.

**Acceptance:** PostgreSQL check обязателен в release DAG; failures/missing artifact блокируют публикацию; consumers тестируют те же package bytes, которые скачает publisher. Не считать branch CI прошлой revision достаточным evidence для release tag.

## Task 2: Reuse validated artifact в branch/PR consumer lane

**Files:**
- Modify: `.github/workflows/ci.yml`, `.github/workflows/reusable-release-validation.yml`, `.github/workflows/reusable-postgresql-validation.yml`, `eng/tests/workflow_contract_tests.py`.
- Reuse: validator и fixture tests из Task 1.

**Interfaces:**
- Consumes: reusable inputs и integrity script Task 1.
- Reusable PostgreSQL workflow получает `run-integration: boolean` с default true; consumers выполняются всегда: позволяет сохранить раннюю независимую test matrix и отдельно ждать artifact для consumers.
- Immutable artifact ID передаётся producer output через caller; producer и consumer используют один текущий run. Не выбирать «последний успешный run» другой revision.

- [ ] **Step 1 — Снять baseline.** Для трёх сопоставимых trusted PR runs записать суммарные runner minutes, restore/build/pack duration, consumer duration и время критического пути. SDK/cache/package graph фиксировать; сравнение с unrelated revisions не использовать.
- [x] **Step 2 — Написать RED workflow mutations.** Trusted PR без upload, fork PR с разрешённым upload/consumers, consumer, который repack'ит solution, другой artifact ID/run или integrity check после consumers → отказ. Test matrix не должна зависеть от длинной generic validation.
- [x] **Step 3 — Проверить RED**, затем включить immutable package upload для доверенных PRs. Предлагаемый retention: 1 день для PR, сохранить 7 дней для generic branch artifacts и 90 дней для release artifacts. Не включать credentials, connection strings или provider logs в artifact.
- [x] **Step 4 — Разделить matrix и consumers.** Сохранить PostgreSQL integration checks 18.6/17.11 параллельными. Linux consumer job после `validation` download'ит artifact, проверяет manifest/hashes, builds только RepositoryChecks и вызывает `run-consumers`. Удалить повторный full solution restore/build/pack из primary PostgreSQL lane. Использовать checkout того же build commit; записывать `git rev-parse HEAD` в evidence (для PR это может быть synthetic merge commit).
- [ ] **Step 5 — Проверить GREEN.** Workflow mutation suite, trusted PR и fork-event fixtures; реальные семь consumer scenarios проходят над downloaded feed. Симулированные missing/changed artifacts дают failure без fallback/repack. Проверить stable check-name migration и service credentials scope.
- [ ] **Step 6 — Измерить после изменения.** Ещё три сопоставимых runs; сравнить runner minutes и critical path. Acceptance: исчезла вторая solution build/pack, hashes producer/consumer совпадают. Не обещать сокращение wall time, если dependency wait его увеличивает; сохранить измерения и объяснить trade-off.
- [x] **Step 7 — Commit:** `ci: reuse validated packages for PostgreSQL consumers`.

**Rollback:** вернуть предыдущую отдельную consumer build/pack lane, сохранив обязательный release gate Task 1. Artifact correctness не ослаблять ради быстродействия.

## Task 3: Сократить NDJSON copying без изменения ownership

**Files:**
- Modify: `src/Shared/JsonFraming/Utf8LineRecordReader.cs`.
- Test: `tests/SmartPipe.Extensions.Json.Tests/Utf8LineRecordReaderTests.cs` (create), existing JSON file/dead-letter tests и `tests/SmartPipe.Extensions.Http.Json.Tests/HttpJsonResponseReadersTests.cs`.
- Create: `benchmarks/SmartPipe.Benchmarks/JsonFramingBenchmarks.cs`.
- Modify: benchmark csproj только для source-link существующего framer; production dependency graph не расширять.

**Interfaces:**
- Сохранять `Utf8LineRecord(byte[] Bytes, bool TooLarge)` и `ReadAsync(Stream, int, CancellationToken)`.
- Для oversized nonblank record выдавать `Bytes = Array.Empty<byte>()`, `TooLarge = true`; consumers уже проверяют TooLarge до чтения Bytes. Для blank/whitespace oversized input сохранять существующее правило пропуска.
- Для валидной записи Bytes — самостоятельный массив, пригодный к удержанию после следующего MoveNext.
- Benchmark: `[MemoryDiagnoser]` и `Task<long> ReadRecords()`; inputs 128 B, 64 KiB, 1 MiB, default-limit 16 MiB и 16 MiB+1, варианты BOM/trim/oversize. Stream/data готовить вне measured allocation setup.

- [ ] **Step 1 — Зафиксировать baseline benchmark.** Выполнить `dotnet run --project benchmarks/SmartPipe.Benchmarks -c Release -- --filter '*JsonFramingBenchmarks*'`; сохранить runtime, input sizes, Allocated B/op, Gen2 и throughput. Benchmark должен дренировать iterator и использовать Bytes/TooLarge в checksum.
- [x] **Step 2 — Написать RED regression test.** Oversized запись отдаёт marker без Bytes; следующая валидная запись читается целиком. Добавить положительные тесты удержания первого массива, BOM из отдельных байтов, CRLF split, blank lines, exact limit / limit+1, cancellation во время oversize discard и incomplete BOM. Только marker test должен падать на старой реализации; compatibility tests фиксируют прежнее поведение.
- [x] **Step 3 — Проверить RED**, затем изменить `CompleteRecord`. Работать с buffer span (`TryGetBuffer`), посчитать BOM/trim offsets до копирования. Oversized nonblank record → empty marker, valid record → единственная ToArray от итогового span. Не выдавать GetBuffer/pooled array потребителю. Сохранять firstRecord и semanticHasNonWhitespace semantics, включая oversized whitespace.
- [ ] **Step 4 — Проверить GREEN.** Полные Json и Http.Json suites; JSON file recovery и dead-letter paths; trim/NativeAOT consumers. Записи, parser exceptions, order, limits и cancellation не изменились.
- [ ] **Step 5 — Повторить benchmark.** Acceptance: у oversized record нет полноразмерных output copies; у normal record одна итоговая копия. На input 16 MiB ожидать устранение примерно двух 16 MiB копий в oversized path; retained MemoryStream buffer остаётся. Объяснить фактическое отличие от оценки и сверить throughput; не объединять с pooling/rewrite framer.
- [x] **Step 6 — Commit:** `perf(json): avoid copying discarded framed records`.

**Rollback:** локальный revert framer change; внешний API и формат не менялись, миграция consumers не требуется.

## Task 4: Ordinal duplicate detection за ожидаемое O(n)

**Files:**
- Modify: `src/SmartPipe.Extensions.PostgreSql/Internal/PostgreSqlChannelSet.cs`.
- Test: `tests/SmartPipe.Extensions.PostgreSql.Tests/Unit/PostgreSqlFactoryArgumentValidationTests.cs`, existing descriptor snapshot tests.

**Interfaces:** Сохранять `PostgreSqlChannelSet.Create(IReadOnlyCollection<string>)` и resulting channel order; comparisons строго `StringComparer.Ordinal`.

- [x] **Step 1 — Зафиксировать поведение и baseline.** Inputs 1/10/100/1000/10000 names; duplicate в конце; измерять composition только, без PostgreSQL/network. Добавить тесты `events`/`Events` как разные имена, exact duplicate → существующий ArgumentException, order и defensive-copy после mutation входного массива.
- [x] **Step 2 — Реализовать HashSet во время defensive copy.** После blank validation проверять `seen.Add(channel)`, при false использовать прежний duplicate error/parameter. Сохранить validation-order contract: если в том же наборе есть и duplicate, и blank, определить и закрепить ожидаемый порядок до замены циклов; при необходимости выполнить duplicate pass после полного blank-validation pass, но с HashSet вместо вложенных циклов.
- [ ] **Step 3 — Проверить GREEN.** Полный PostgreSQL Unit suite и descriptor snapshot tests; real LISTEN case-sensitive scenarios в CI. Для simultaneous blank+duplicate включить тест именно выбранного validation precedence.
- [x] **Step 4 — Сравнить large-n измерения.** Acceptance: ожидаемое линейное масштабирование duplicate scan и неизменные результаты/порядок. Отметить дополнительную память HashSet; при типичном малом n не обещать заметного выигрыша.
- [x] **Step 5 — Commit:** `perf(postgresql): validate channel duplicates with ordinal set`.

## Task 5: Исправления повторного ревью

- [x] HTTP root-array guard пропускает leading comments только при frozen `ReadCommentHandling.Skip`; исходные bytes проверяет System.Text.Json. Split comments/BOM и неверный root покрыты тестами.
- [x] HTTP array и NDJSON считают body budget от начальной позиции тела; file readers сохраняют учёт уже прочитанного prefix и rewind semantics.
- [x] COPY source/sink отклоняют `Multiplexing=true` при композиции descriptor: Npgsql 10.0.3 может игнорировать cancellation при binding в занятом multiplexed pool. Borrowed data source не меняется.
- [x] Документация учитывает `HttpResponseMessage.Content`/EmptyContent: пустой array body ошибочен при enumeration, пустой NDJSON даёт пустой stream.
- [x] HTTP NDJSON Throw завершается при известной oversized nonblank записи без ожидания LF; Skip и file/dead-letter пути продолжают draining. Partial BOM и oversized whitespace сохраняют прежние правила.
- [x] Дополнительные регрессии защищают generated idempotency key при invalid client default и cancellation из Polly hook/predicate от ExceptionMapper.

## Task 6: Bounded package consumer parallelism

- [x] Добавить `--max-parallelism` (default 2, positive invariant integer); значение 1 сохраняет serial execution.
- [x] Использовать bounded workers; NativeAOT cap 1. Каждый сценарий имеет собственные workspace, package cache и logs; результаты остаются в manifest order.
- [x] При ошибке отменять sibling workers, дождаться cleanup и пробросить initiating exception. При caller cancellation не оставлять активные процессы.
- [x] Проверить ordering, default two workers, serial override, NativeAOT cap и failure drain. Контролируемый runner RED/ GREEN подтверждает 1 → 2 обычных процесса.
- [ ] Измерить реальные 70 scenarios / 13 NativeAOT / 15 trimmed на сопоставимых CI runs. Delay-only harness подтверждает scheduler parallelism, но не оценивает compile speed или peak RAM.

## Оставшиеся evidence gates

- [ ] Task 1 Step 5–6: exact-candidate PostgreSQL 18.6/17.11 и семь downloaded-artifact consumers; сохранить run/artifact IDs и hashes. Локальные workflow mutations и integrity fixtures уже проходят.
- [ ] Task 2 Step 1/6: три сопоставимых runs до и после. Есть только один baseline; runner-minute и wall-time gain пока не заявлены.
- [ ] Task 3 Step 1/5: полный устойчивый BenchmarkDotNet before/after. Контролируемые allocation/Gen2/throughput measurements приложены; smoke benchmark не заменяет статистическую серию.
- [ ] Task 3/4 remote consumers/integration: локальные полные Json/Http.Json и PostgreSQL Unit suites проходят; серверные проверки поручены CI.
- [ ] Maintainer подтверждает branch protection/check-name requirements до merge; существующие ранние PostgreSQL integration check names сохранены.

## Итоговая проверка и завершение

- [x] Correctness fixes подтверждены RED/GREEN; allocation и channel-scaling changes имеют контролируемые baseline/after evidence. Полная стабильная benchmark серия остаётся выше открытой.
- [x] Release build `dotnet build SmartPipe.Core.slnx -c Release --no-restore -warnaserror`, format verify, `sp220-05`, workflow contracts и локальные suites/classes проходят; полный состав проверок и ограничения указаны в evidence.
- [ ] Все требуемые проверки прикреплены к exact candidate SHA; branch protection/check names проверены maintainer'ом при CI restructuring.
- [x] Ни один package consumer не repack'ит publishing artifact и не использует feed другой revision.
- [x] Независимое review: lifecycle/ownership, integrity/provenance, fork guards, package graph, release DAG и benchmark methodology.
- [ ] Обновить SP220-18 evidence с run IDs, artifact hashes и consumer results; не отмечать финальную release validation выполненной по одному PR run.
- [x] Handoff: reviewable commits, локальные измерения, implementation evidence и отмеченные checklist items. Exact-head CI evidence добавляется в PR после запуска; публикация 2.2.0 остаётся отдельным действием.
