# SmartPipe.Core 2.1.3 follow-up plan

## Цель

Закрыть подтверждённый follow-up после выпуска 2.1.2, не смешивая его с немедленными P1-исправлениями PR #27 и не расширяя публичный API без отдельного решения.

## Correctness и тесты

- [ ] Исправить false-green `BufferedBestEffort_NormalDisposeDoesNotThrow`:
  - сначала полностью заполнить capacity;
  - убедиться явной синхронизацией, что следующий emit действительно заблокирован;
  - только после этого запускать disposal;
  - не использовать `Task.Delay` как доказательство блокировки.
- [ ] Исправить `LegacyTopLevelSequence_TotalAboveLimit_Throws`: заменить root array `[1,2]` на настоящую последовательность top-level JSON values `1 2`.
- [ ] Добавить legacy override `JsonUnframedInputLimitStream.ReadAsync(byte[], int, int, CancellationToken)`, делегирующий в единый limit contract всех read-overload'ов.
- [ ] Добавить регрессии для sync/modern/legacy read-overload'ов: exact limit, limit + 1, cancellation и единый текст ошибки.

## Maintainability без изменения поведения

- [ ] Снизить cyclomatic complexity `Utf8LineRecordReader.ReadAsync` ниже 15 выделением узких helpers.
- [ ] Снизить cyclomatic complexity `JsonFileSource.ReadEnvelopesAsync` ниже 15 выделением узких helpers.
- [ ] Для обоих рефакторингов сохранить без изменений framing, BOM/CRLF, cancellation, buffer ownership, record limits и обработку последней строки без LF.
- [ ] До и после рефакторинга прогнать точечные boundary-тесты и полный `SmartPipe.Extensions.Json.Tests`.

## Release и operator experience

- [ ] После публикации 2.1.2 удалить временное исключение NuGet URL из `lychee.toml` и подтвердить, что ссылка проходит link-check без allowlist.
- [ ] Уточнить recoverable-rerun сообщение: для manual dispatch оператор должен выбрать release tag, а не произвольную ветку или SHA.
- [ ] Добавить operator checklist:
  - NuGet Trusted Publishing policy и разрешённые package IDs;
  - GitHub environment protection, reviewers и branch/tag restrictions;
  - совпадение release tag, package version и опубликованных package artifacts.

## Performance: сначала baseline

- [ ] Добавить BenchmarkDotNet baseline для `Utf8LineRecordReader`: throughput и allocations на LF, CRLF, BOM, коротких и near-limit records.
- [ ] Добавить BenchmarkDotNet baseline для batch serialization `JsonFileSink`: throughput и allocations для типовых batch sizes и payload sizes.
- [ ] Зафиксировать воспроизводимую конфигурацию benchmark и исходные результаты.
- [ ] Планировать оптимизацию только после измерений; не принимать изменения только по статическому предположению о производительности.

## Проверка

- [ ] Новые регрессии запускать отдельно с `--minimum-expected-tests 1`.
- [ ] Прогнать полный `SmartPipe.Extensions.Json.Tests`.
- [ ] Прогнать затронутые workflow/contract tests.
- [ ] Выполнить Release build с warnings-as-errors и `dotnet format --verify-no-changes`.
- [ ] Выполнить `git diff --check`.
- [ ] Зафиксировать benchmark baseline отдельно от функциональных исправлений.

## Явные non-goals

- Не удалять `_windowGate` у `CircuitBreaker`.
- Не объединять публичные source option types.
- Не менять malformed multiple-BOM semantics.
- Не «исправлять» компилирующийся `bytes.CopyTo(buffer)` как production blocker.
- Не заменять `List.RemoveRange` на основании ошибочного утверждения о сдвиге оставшихся элементов.
- Не проводить API redesign `CircuitBreaker` до major-версии.
