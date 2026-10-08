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
