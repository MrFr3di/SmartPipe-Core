# Анализ runtime-ревью и план исправлений — 2026-10-08

## Проверенная база и актуальность приложений

Репозиторий: MrFr3di/SmartPipe-Core. Main:
`44368f1014ca9ce801b0e3d9443ea5aaf6272809`.
Baseline CI: [37808671315](https://github.com/MrFr3di/SmartPipe-Core/actions/runs/37808671315), success.
На входе нет открытых PR/issues. `eng/SmartPipe.Versions.props`: 2.2.1,
предыдущая стабильная версия 2.2.0; SDK в global.json: 10.0.401.

Приложенное ревью совпадает с кодом main, но само является статическим анализом,
не доказательством выполненных тестов. Прилагаемые планы SP220-00–14 описывают
предыдущий архитектурный переход; они не являются текущей очередью работ.
В репозитории есть уже реализованные пакеты, facade/ownership/consumer проверки,
SP220-17/18, миграция и servicing-документация. Не переигрывать checkpoint G и не
возвращать старые SDK/dependency versions из вложений. Текущие ADR и код важнее
исторических инструкций; например, часть HTTP/Polly API была намеренно удалена,
а не сохранена forwarders. Остальные интеграции требуют отдельного аудита.

## Разбор замечаний

| ID | Статическое подтверждение и риск | Решение и критерий |
|---|---|---|
| F1 P1 | Producer exception перескакивает Task.WhenAll; normal workers не попадают в late registry; компоненты могут освобождаться пока transformer/sink активен | Первое исправление: локальная остановка workers, unconditional join, исходная ошибка первой; barrier + backpressure + callback/fault проверки |
| F2 P1 | Caller cancellation выходит из WaitAsync мимо timeout-handler; Dispose linked CTS не означает окончание execution | Следующий slice: ownership transfer на каждом выходе после старта, в том числе исключения Cancel callback; тесты Cancel/Abort/Dispose, до timeout/в grace/после detach |
| F3 P1 | Flush(None) ждёт buffered callback, callback ждёт dispatcher token, отменяемый лишь после flush | Отдельный shutdown slice: сигнал stop до flush, graceful drain сохраняется; reliable/best-effort × Cancel/Abort/Dispose и terminal event contract |
| F4 P2 | action=null означает и success, и EmitFailureResult/DeadLetter; adaptive failed=false неверен | Internal readonly outcome отделяет Failed от control action; FakeTimeProvider и outcome matrix, Skip/Filtered не считать failure |
| F5 P2 | InfiniteTimeSpan отрицателен и попадает в grace<=0 | Исправить sentinel вместе с F2; infinite grace ждёт cooperative завершения, caller cancellation прерывает ожидание с сохранением ownership |
| F6 P2 | Snapshot Validate не проверяет TimeoutPolicy; неверные enum/durations доходят до runtime | До activation единая внутренняя валидация; null/zero/infinity/-2/max/max+1; StageTimeout общий deadline отделить от одного timer-wait |
| F7 P2 | Enumerator DisposeAsync в голом finally заменяет ошибку тела | Включён в первый slice: ExceptionDispatchInfo + RuntimeCleanup; read/cleanup/both/cancel+cleanup, concurrency 1/2 |

Все семь замечаний имеют конкретное основание в текущих ветвях управления.
Приёмка каждого требует исполнения регрессий: наличие старого зелёного CI не
опровергает сценарий, который старые тесты не покрывают.

## Архитектура: что делать сейчас и что отложить

I1 supervisor: сначала закрепить ownership-инварианты локальными исправлениями.
Полная перепись TypedPipelineRuntime до регрессий увеличит риск и затруднит
локализацию. У ordinary workers и detached attempts разные границы lifetime;
нельзя объявить timeout registry общим supervisor без проверки sink, source и
scope ownership. Future outcome struct полезен независимо от нового registry.

Дополнение к F2: LateStageAttemptRegistry сейчас намеренно поглощает поздние
faults, поскольку timeout уже представлен результатом. Для caller cancellation
это не автоматически правильный контракт. Следующий slice должен различать
причину detach и определить наблюдение/агрегацию cancellation-origin faults;
простой Register на новый путь без анализа ошибок недостаточен. F1 join обычных
workers не закрывает этот timed-attempt дефект.

I2 retry budget/jitter/late cap: отдельное opt-in изменение. Concurrency envelopes
не ограничивает detached operations. Лимит нельзя реализовать забыв задачу или
освободив её ресурсы. Нужны явный admission failure, отменяемое ожидание, injectable
random, fault-injection и измерение retry amplification; новый public API требует
отдельной совместимости. Default retry policy не менять этим bugfix.

O1 allocations: после исправлений снять baseline, затем gate до создания events
и success output. Не выключать Meter/ActivitySource вместе с observers; sink
записывается до success. Матрица 1/4/16 stages, observers 0/1, sink 0/1,
concurrency 1/8/32, sync/async ValueTask; B/item, Gen0, throughput, p99.
Число потенциальных объектов из ревью — модель, не измеренная экономия GC.

O2 breaker sampling: точное окно и агрегированные buckets имеют разную семантику.
Сначала профиль retained memory/lock contention и batch cleanup точного окна.
Bucket mode только opt-in после oracle comparison на burst/idle/boundaries.
Не менять hybrid EWMA молча. O(R×W) памяти — реальный риск масштаба, но не
доказательство, что именно он сейчас ограничивает продукт.

## Последовательность и stop conditions

1. F1/F7: владение ordinary workers и приоритет source errors; этот PR.
2. F2/F5/F6: timed attempt ownership, sentinel semantics, fail-fast validation.
3. F3: cooperative buffered observer stop с явным delivery contract.
4. F4: terminal outcome для adaptive и согласованных метрик.
5. Full Core/Hosting/DI lifecycle evidence; затем O1 benchmarks и оптимизация.
6. I1/I2/O2 только после отдельного дизайна и измеримой потребности.

Не расширять текущий PR на integrations, новые policies или public API.
Не обещать принудительно остановить пользовательский код, игнорирующий token.
Не снимать ownership ради responsiveness. Не публиковать релиз по одному bugfix.

## Первоисточники Microsoft

- [Отмена ожидания и underlying operations](https://learn.microsoft.com/en-us/dotnet/standard/asynchronous-programming-patterns/cancel-non-cancelable-async-operations): WaitAsync может отменить ожидание, а исходная операция продолжает работать; её fault/lifetime нужно наблюдать.
- [Task.WaitAsync, .NET 10](https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.task.waitasync?view=net-10.0): separate wait task, timeout/cancellation semantics.
- [CTS.Cancel, .NET 10](https://learn.microsoft.com/en-us/dotnet/api/system.threading.cancellationtokensource.cancel?view=net-10.0): callbacks могут бросать AggregateException; нельзя помещать join после незащищённого Cancel.
- [CTS.CancelAsync, .NET 10](https://learn.microsoft.com/en-us/dotnet/api/system.threading.cancellationtokensource.cancelasync?view=net-10.0): ждёт callbacks, а не завершение всех user operations.
- [Task.WhenAll, .NET 10](https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.task.whenall?view=net-10.0): агрегат task.Exception нужен для всех независимых failures, одного catch exception недостаточно.

Дизайн: [F1/F7](../../superpowers/specs/2026-10-08-runtime-ownership-design.md).
Исполнение: [план](../../superpowers/plans/2026-10-08-runtime-ownership.md).

## Evidence

Первоначально: нет локального dotnet; тесты ещё не выполнялись. Tests-first PR
и exact-head GitHub CI являются следующей проверкой. До GREEN эта работа —
кандидат исправления, а не принятый milestone. Исторические baseline snapshots
не изменяются. Результаты и остаточные риски фиксируются здесь по факту.

### Исполнение первого slice

- Tests-first `fd8b680efcd13f5cf4d009ad171675aeb37bb9ae`, [CI 37813949746](https://github.com/MrFr3di/SmartPipe-Core/actions/runs/37813949746): format/build success; regressions 15 total, 10 failed, 5 passed. Первое падение компиляции в 56db813 было исправлением fixture, не RED evidence.
- Runtime candidate `b63dabd2f2273d6229a6ad081e108ed9654a2dd0`, [CI 37814792263](https://github.com/MrFr3di/SmartPipe-Core/actions/runs/37814792263): ранний ownership regression step success. Полная приёмка определяется всеми checks последнего HEAD PR, не этим частичным результатом.
- Read-only review не нашёл concrete runtime defect, но потребовал укрепить backpressure и multiple-worker coverage. Добавлены gate фактически заполненного output, отдельный emitter test с наблюдаемым pending WriteAsync и два independently failing workers. Итоговый класс — 17 cases; ранний шаг CI перед package validation не позволяет скрыть их отсутствие нулевым прогоном.
- [PR #148](https://github.com/MrFr3di/SmartPipe-Core/pull/148), ветка `fix/runtime-ownership-review-2026-10-08`. Проверять актуальный head SHA/checks там; записи выше — evidence конкретных исторических commits, не rolling status.

Backpressure проверяется двумя уровнями: runtime fault при полной output queue и
отдельный cancellation test действительно pending emitter write. Публичный runtime
не предоставляет hook «writer уже вошёл в WriteAsync»; новый production hook ради
теста не добавлен. Утверждение, что runtime-test сам доказывает точный момент
входа в pending write, не делается. Возможность indefinite shutdown при
non-cooperative user code остаётся явно документированным ограничением.


## F1/F7 acceptance and F2/F5/F6 continuation

PR #148 final head `8951a4db1312bda2e21417cfe6c2745f22b736e5` passed all required checks,
including full [CI 37815529580](https://github.com/MrFr3di/SmartPipe-Core/actions/runs/37815529580).
Ready for review; not merged.

The next branch `fix/timed-attempt-ownership-2026-10-08`, PR #149, depends on #148.
Clean tests-first RED: `1475fd8d217a1d3c4a3d47aa3048ae14d76447c9`,
[CI 37819739713](https://github.com/MrFr3di/SmartPipe-Core/actions/runs/37819739713),
format/build succeeded, 19 tests with 14 failures and 5 passes.
The logs also expose lost cancellation delivery when premature CTS disposal
removes its linked registration before cancellation reaches the transformer.
Candidate retains abandoned executions/CTS, preserves unexpected late faults
and structured results, handles infinite grace, and rejects invalid timeout
snapshots before activation. Acceptance pending candidate CI and review.

Finite StageTimeout is intentionally subject to the same supported wait maximum:
the runtime can use its remaining budget as a single attempt timer. Supporting
larger deadlines would require provider-backed chunked waits and separate tests;
this servicing change fails fast instead of silently timing out early.

See [timed ownership plan](../../superpowers/plans/2026-10-08-timed-attempt-ownership.md).
F3/F4 and I1/I2/O1/O2 remain outside this slice.


Review follow-up: caller cancellation during grace is classified by caller token,
including completion racing the cancelled wait. A custom timer completes execution
while the cancellation promise cleans up, covering both task and structured faults.
Disposal collects cancellation-callback errors and continues through run join and
deferred cleanup; source-origin and stage-origin throwing callbacks are covered.
Candidate now contains 47 timed integration/policy/race cases and 5 registry cases.

Candidate CI `37820874428` stopped before build because the generic workflow
contract accepted the literal substring `--minimum-expected-tests 1`, inadvertently
accepting 19 but rejecting 41. It now checks a positive integer, with zero,
negative and malformed-value rejection mutations. Exact-head GREEN still pending.


Hosted CI `37876568260`, candidate `c8b8079`: workflow contracts, format and
build succeeded; timed regressions 47 total, 45 passed, 2 source-callback cases
failed. Root cause: `PipelineStartOperation.DisposeAsync` requested cancellation
before entering executor cleanup and only handled ObjectDisposedException.
It now retains cancellation errors, waits Completion, runs executor disposal and
activation-CTS cleanup even when callbacks throw, then aggregates failures.
The same existing source-callback regressions verify this outer ownership boundary.

## F3 candidate — 2026-10-09

Separate branch `fix/buffered-observer-shutdown-2026-10-09` builds on verified
PR #149 head `78615165560f278337158a59dd51ba75fddc9462` (full CI
`37876995823` success). Callback cancellation is separated from queue-worker
shutdown; immediate stop is linked before backpressure can block finalization.
Processing faults signal callback stop before flush. Drain remains graceful.
Dispatcher Dispose cancels before awaiting concurrent Complete and joins even
if cancellation callbacks throw. The 22-case acceptance matrix and delivery
contract are in `docs/superpowers/plans/2026-10-09-buffered-observer-shutdown.md`.
Candidate full hosted CI is pending; F4 remains outside this slice.
