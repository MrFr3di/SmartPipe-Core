using System.Reflection;
using Microsoft.Extensions.Logging;
using SmartPipe.Core;
using SmartPipe.Extensions.Sinks;

namespace SmartPipe.Extensions.Logging.Tests;

public sealed class LoggerSinkContractTests
{
    [Fact]
    public async Task LegacyConstructorPreservesRawPayloadStructuredContractAndIsNotObsolete()
    {
        var constructor = typeof(LoggerSink<string>).GetConstructor(
            [typeof(ILogger<LoggerSink<string>>)]);
        Assert.NotNull(constructor);
        Assert.Null(constructor!.GetCustomAttribute<ObsoleteAttribute>());

        var logger = new CapturingLogger<LoggerSink<string>>();
        var sink = new LoggerSink<string>(logger);
        await sink.WriteAsync(ProcessingEnvelope<string>.Create("raw payload", "pipeline", "run", 42), TestContext.Current.CancellationToken);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal(0, entry.EventId.Id);
        Assert.Null(entry.EventId.Name);
        Assert.Equal("raw payload", entry.Properties["@Value"]);
        Assert.Equal((ulong)42, entry.Properties["TraceId"]);
        Assert.Equal(
            "Processed item [TraceId: {TraceId}] successfully. Value: {@Value}",
            entry.Properties["{OriginalFormat}"]);
        Assert.Contains("raw payload", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SafeDefaultDoesNotCaptureRawPayloadAndPreservesTraceIdEventContract()
    {
        var logger = new CapturingLogger<LoggerSink<string>>();
        var sink = new LoggerSink<string>(logger, new LoggerSinkOptions<string>());
        await sink.WriteAsync(ProcessingEnvelope<string>.Create("secret payload", "pipeline", "run", 42), TestContext.Current.CancellationToken);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal(1000, entry.EventId.Id);
        Assert.Equal("SmartPipeItem", entry.EventId.Name);
        Assert.Equal((ulong)42, entry.Properties["TraceId"]);
        Assert.DoesNotContain("Value", entry.Properties.Keys);
        Assert.DoesNotContain("@Value", entry.Properties.Keys);
        Assert.DoesNotContain("secret payload", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FormattedPayloadIsBoundedBeforeItIsLogged()
    {
        var formatterCalls = 0;
        var logger = new CapturingLogger<LoggerSink<string>>();
        var sink = new LoggerSink<string>(
            logger,
            new LoggerSinkOptions<string>
            {
                PayloadMode = LoggerSinkPayloadMode.Formatted,
                Formatter = payload =>
                {
                    formatterCalls++;
                    return payload;
                },
                MaximumFormattedPayloadLength = 5,
            });

        await sink.WriteAsync(ProcessingEnvelope<string>.Create("secret payload", "pipeline", "run", 42), TestContext.Current.CancellationToken);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(1, formatterCalls);
        Assert.Equal("secre", entry.Properties["FormattedPayload"]);
        Assert.DoesNotContain("Value", entry.Properties.Keys);
        Assert.DoesNotContain("@Value", entry.Properties.Keys);
        Assert.DoesNotContain("secret payload", entry.Message, StringComparison.Ordinal);
        Assert.Equal((ulong)42, entry.Properties["TraceId"]);
    }

    [Fact]
    public async Task FormattedSafeModeDoesNotExposeRawPayloadThroughStateOrMessage()
    {
        const string rawPayload = "secret payload";
        var logger = new CapturingLogger<LoggerSink<string>>();
        var sink = new LoggerSink<string>(
            logger,
            new LoggerSinkOptions<string>
            {
                PayloadMode = LoggerSinkPayloadMode.Formatted,
                Formatter = _ => "redacted",
            });

        await sink.WriteAsync(
            ProcessingEnvelope<string>.Create(rawPayload, "pipeline", "run", 42),
            TestContext.Current.CancellationToken);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal("redacted", entry.Properties["FormattedPayload"]);
        Assert.DoesNotContain("Value", entry.Properties.Keys);
        Assert.DoesNotContain("@Value", entry.Properties.Keys);
        Assert.DoesNotContain(rawPayload, entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            rawPayload,
            string.Join('|', entry.Properties.Values.Select(static value => value?.ToString() ?? string.Empty)),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task FormattedSafeModeWithoutTraceIdOmitsTraceMetadataAndPreservesEventMetadata()
    {
        var logger = new CapturingLogger<LoggerSink<string>>();
        var sink = new LoggerSink<string>(
            logger,
            new LoggerSinkOptions<string>
            {
                PayloadMode = LoggerSinkPayloadMode.Formatted,
                IncludeTraceId = false,
                Formatter = _ => "redacted",
            });

        await sink.WriteAsync(
            ProcessingEnvelope<string>.Create("secret payload", "pipeline", "run", 42),
            TestContext.Current.CancellationToken);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal(1000, entry.EventId.Id);
        Assert.Equal("SmartPipeItemFormattedWithoutTrace", entry.EventId.Name);
        Assert.DoesNotContain("TraceId", entry.Properties.Keys);
        Assert.Equal("redacted", entry.Properties["FormattedPayload"]);
        Assert.DoesNotContain("Value", entry.Properties.Keys);
        Assert.DoesNotContain("@Value", entry.Properties.Keys);
    }

    [Fact]
    public async Task FormatterIsNotInvokedWhenInformationIsDisabled()
    {
        var formatterCalls = 0;
        var logger = new CapturingLogger<LoggerSink<string>>(LogLevel.Warning);
        var sink = new LoggerSink<string>(
            logger,
            new LoggerSinkOptions<string>
            {
                PayloadMode = LoggerSinkPayloadMode.Formatted,
                Formatter = _ =>
                {
                    formatterCalls++;
                    return "formatted";
                },
            });

        await sink.WriteAsync(ProcessingEnvelope<string>.Create("secret payload"), TestContext.Current.CancellationToken);

        Assert.Equal(0, formatterCalls);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task RawPayloadRequiresExplicitUnsafeMode()
    {
        var logger = new CapturingLogger<LoggerSink<string>>();
        var sink = new LoggerSink<string>(
            logger,
            new LoggerSinkOptions<string> { PayloadMode = LoggerSinkPayloadMode.UnsafeRaw });

        await sink.WriteAsync(ProcessingEnvelope<string>.Create("explicit raw payload"), TestContext.Current.CancellationToken);

        var entry = Assert.Single(logger.Entries);
        Assert.True(
            entry.Properties.TryGetValue("@Value", out var value),
            $"Captured keys: {string.Join(", ", entry.Properties.Keys)}");
        Assert.Equal("explicit raw payload", value);
    }

    [Fact]
    public void ConstructorsRejectNullAndInvalidOptionsAtTheBoundary()
    {
        var logger = new CapturingLogger<LoggerSink<string>>();

        Assert.Throws<ArgumentNullException>(() => new LoggerSink<string>(null!));
        Assert.Throws<ArgumentNullException>(() => new LoggerSink<string>(logger, null!));
        Assert.Throws<ArgumentException>(() => new LoggerSink<string>(
            logger,
            new LoggerSinkOptions<string> { PayloadMode = LoggerSinkPayloadMode.Formatted }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LoggerSink<string>(
            logger,
            new LoggerSinkOptions<string> { MaximumFormattedPayloadLength = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LoggerSink<string>(
            logger,
            new LoggerSinkOptions<string> { PayloadMode = (LoggerSinkPayloadMode)99 }));
    }

    [Fact]
    public void FormattedPayloadLengthAcceptsRevisedUpperBoundAndRejectsValuesAboveIt()
    {
        const int revisedMaximum = 64 * 1024;
        var logger = new CapturingLogger<LoggerSink<string>>();

        var sink = new LoggerSink<string>(
            logger,
            new LoggerSinkOptions<string>
            {
                PayloadMode = LoggerSinkPayloadMode.Formatted,
                Formatter = _ => "redacted",
                MaximumFormattedPayloadLength = revisedMaximum,
            });

        Assert.NotNull(sink);
        Assert.Throws<ArgumentOutOfRangeException>(() => new LoggerSink<string>(
            logger,
            new LoggerSinkOptions<string>
            {
                PayloadMode = LoggerSinkPayloadMode.Formatted,
                Formatter = _ => "redacted",
                MaximumFormattedPayloadLength = revisedMaximum + 1,
            }));
    }

    private sealed class CapturingLogger<T>(LogLevel minimumLevel = LogLevel.Trace) : ILogger<T>
    {
        internal List<Entry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimumLevel;

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

            Entries.Add(new Entry(
                logLevel,
                eventId,
                formatter(state, exception),
                properties));
        }

        internal sealed record Entry(
            LogLevel Level,
            EventId EventId,
            string Message,
            IReadOnlyDictionary<string, object?> Properties);
    }
}
