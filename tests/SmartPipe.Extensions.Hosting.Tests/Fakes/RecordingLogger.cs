using Microsoft.Extensions.Logging;

namespace SmartPipe.Extensions.Hosting.Tests.Fakes;

internal sealed class RecordingLogger<T> : ILogger<T>
{
    internal List<Entry> Entries { get; } = [];

    internal Action<Entry>? EntryObserver { get; init; }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var properties = state is IEnumerable<KeyValuePair<string, object?>> values
            ? values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
            : new Dictionary<string, object?>();
        var entry = new Entry(logLevel, formatter(state, exception), exception, properties);
        Entries.Add(entry);
        EntryObserver?.Invoke(entry);
    }

    internal sealed record Entry(
        LogLevel Level,
        string Message,
        Exception? Exception,
        IReadOnlyDictionary<string, object?> Properties);
}
