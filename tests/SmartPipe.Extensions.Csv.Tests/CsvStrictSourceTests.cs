using System.Reflection;
using System.Text;
using CsvHelper.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartPipe.Core;
using SmartPipe.Extensions.Csv.Internal;

namespace SmartPipe.Extensions.Csv.Tests;

public sealed class CsvStrictSourceTests
{
    [Fact]
    public async Task FileSource_ProcessesOneHundredThousandRowsStructurally()
    {
        var content = new StringBuilder("Name,Age\r\n", 1_600_000);
        for (var index = 0; index < 100_000; index++)
            content.Append("Person").Append(index).Append(',').Append(index % 100).Append("\r\n");
        var path = CreateTempFile(content.ToString());
        try
        {
            var descriptor = InvokeFileSource(LoadLeafAssembly(), path, CreateOptions(LoadLeafAssembly()), null, null);
            var source = await ActivateSourceAsync(descriptor);
            try
            {
                await source.InitializeAsync();
                var count = 0;
                await foreach (var envelope in source.ReadEnvelopesAsync(TestContext.Current.CancellationToken))
                {
                    Assert.StartsWith("Person", envelope.Payload.Name, StringComparison.Ordinal);
                    count++;
                }
                Assert.Equal(100_000, count);
            }
            finally
            {
                await source.DisposeAsync();
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task FileSource_ThirtyTwoDefinitionsKeepMapFactoriesIsolatedPerActivation()
    {
        var assembly = LoadLeafAssembly();
        var map = CreateFactoryRegistration(assembly);
        var paths = Enumerable.Range(0, 32)
            .Select(index => CreateTempFile($"Name,Age\nPerson{index},{index}\n"))
            .ToArray();
        try
        {
            for (var index = 0; index < paths.Length; index++)
            {
                var source = await ActivateSourceAsync(
                    InvokeFileSource(assembly, paths[index], CreateOptions(assembly), map, null));
                try
                {
                    await source.InitializeAsync();
                    var value = Assert.Single(await ReadValuesAsync(source));
                    Assert.Equal($"Person{index}", value.Name);
                }
                finally
                {
                    await source.DisposeAsync();
                }
            }
            Assert.Equal(32, PersonMap.CreatedCount);
        }
        finally
        {
            foreach (var path in paths)
                File.Delete(path);
        }
    }

    [Fact]
    public async Task FileSource_DetectsUtf16BomAndPreservesMultibyteValues()
    {
        var assembly = LoadLeafAssembly();
        var path = Path.Combine(Path.GetTempPath(), $"smartpipe-csv-source-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(
            path,
            "Name,Age\r\nЕлена漢字,41\r\n",
            new UnicodeEncoding(false, true, true));
        try
        {
            var options = CreateOptions(assembly);
            SetOption(options, "Encoding", new UTF8Encoding(false, true));
            var source = await ActivateSourceAsync(InvokeFileSource(assembly, path, options, null, null));
            try
            {
                await source.InitializeAsync();
                Assert.Equal("Елена漢字", Assert.Single(await ReadValuesAsync(source)).Name);
            }
            finally
            {
                await source.DisposeAsync();
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
    [Fact]
    public async Task FileSource_UsesDefensiveOptionsSnapshotAndFreshMapPerActivation()
    {
        var assembly = LoadLeafAssembly();
        var path = CreateTempFile("Name;Age\nAlice;41\n");
        try
        {
            var options = CreateOptions(assembly);
            SetOption(options, "Delimiter", ';');
            var map = CreateFactoryRegistration(assembly);
            var descriptor = InvokeFileSource(assembly, path, options, map, loggerFactory: null);
            SetOption(options, "Delimiter", ',');

            var first = await ActivateSourceAsync(descriptor);
            var second = await ActivateSourceAsync(descriptor);
            try
            {
                await first.InitializeAsync();
                await second.InitializeAsync();
                var firstValues = await ReadValuesAsync(first);
                var secondValues = await ReadValuesAsync(second);

                Assert.Equal([new Person { Name = "Alice", Age = 41 }], firstValues);
                Assert.Equal([new Person { Name = "Alice", Age = 41 }], secondValues);
                Assert.Equal(2, PersonMap.CreatedCount);
            }
            finally
            {
                await first.DisposeAsync();
                await second.DisposeAsync();
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task FileSource_SkipAndLogSkipsConversionFailureAndContinuesAtNextRecord()
    {
        var assembly = LoadLeafAssembly();
        var path = CreateTempFile("Name,Age\nAlice,41\nBroken,nope\nCarol,43\n");
        try
        {
            var options = CreateOptions(assembly);
            SetOption(options, "InvalidRecordBehavior", ParseEnum(assembly, "CsvInvalidRecordBehavior", "SkipAndLog"));
            var descriptor = InvokeFileSource(
                assembly,
                path,
                options,
                map: null,
                NullLoggerFactory.Instance);

            var source = await ActivateSourceAsync(descriptor);
            try
            {
                await source.InitializeAsync();
                var values = await ReadValuesAsync(source);
                Assert.Equal(
                    [new Person { Name = "Alice", Age = 41 }, new Person { Name = "Carol", Age = 43 }],
                    values);
            }
            finally
            {
                await source.DisposeAsync();
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task FileSource_PartialEnumerationReleasesOwnedFileResources()
    {
        var assembly = LoadLeafAssembly();
        var path = CreateTempFile("Name,Age\nAlice,41\nBob,42\n");
        try
        {
            var descriptor = InvokeFileSource(assembly, path, CreateOptions(assembly), null, null);
            var source = await ActivateSourceAsync(descriptor);
            await source.InitializeAsync();
            await using (var enumerator = source.ReadEnvelopesAsync().GetAsyncEnumerator())
                Assert.True(await enumerator.MoveNextAsync());

            await source.DisposeAsync();
            File.Delete(path);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task FileSource_HeaderFailureIsNeverSkipped()
    {
        var assembly = LoadLeafAssembly();
        var path = CreateTempFile("Wrong,Age\nAlice,41\n");
        try
        {
            var options = CreateOptions(assembly);
            SetOption(options, "InvalidRecordBehavior", ParseEnum(assembly, "CsvInvalidRecordBehavior", "SkipAndLog"));
            var descriptor = InvokeFileSource(assembly, path, options, null, NullLoggerFactory.Instance);
            var source = await ActivateSourceAsync(descriptor);
            try
            {
                await source.InitializeAsync();
                await Assert.ThrowsAsync<InvalidDataException>(async () => await ReadValuesAsync(source));
            }
            finally
            {
                await source.DisposeAsync();
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task FileSource_SkipAndLogDoesNotExposeTheRejectedPayload()
    {
        var assembly = LoadLeafAssembly();
        var path = CreateTempFile("Name,Age\nAlice,41\nSecretName,nope\n");
        var loggerFactory = new CapturingLoggerFactory();
        try
        {
            var options = CreateOptions(assembly);
            SetOption(options, "InvalidRecordBehavior", ParseEnum(assembly, "CsvInvalidRecordBehavior", "SkipAndLog"));
            var descriptor = InvokeFileSource(assembly, path, options, null, loggerFactory);
            var source = await ActivateSourceAsync(descriptor);
            try
            {
                await source.InitializeAsync();
                _ = await ReadValuesAsync(source);
            }
            finally
            {
                await source.DisposeAsync();
            }

            Assert.DoesNotContain(loggerFactory.Messages, message => message.Contains("SecretName", StringComparison.Ordinal));
            Assert.DoesNotContain(loggerFactory.Messages, message => message.Contains("nope", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task FileSource_CreatesOneLoggerAtActivationAndReusesIt()
    {
        var assembly = LoadLeafAssembly();
        var path = CreateTempFile("Name,Age\nFirst,nope\nSecond,still-nope\n");
        var loggerFactory = new CapturingLoggerFactory();
        try
        {
            var options = CreateOptions(assembly);
            SetOption(options, "InvalidRecordBehavior", ParseEnum(assembly, "CsvInvalidRecordBehavior", "SkipAndLog"));
            var descriptor = InvokeFileSource(assembly, path, options, null, loggerFactory);
            var source = await ActivateSourceAsync(descriptor);
            try
            {
                Assert.Equal(1, loggerFactory.CreateLoggerCalls);
                await source.InitializeAsync();
                Assert.Empty(await ReadValuesAsync(source));
                Assert.Equal(1, loggerFactory.CreateLoggerCalls);
                Assert.Equal(2, loggerFactory.Messages.Count);
            }
            finally
            {
                await source.DisposeAsync();
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task FileSource_CancellationRemainsOperationCanceledException()
    {
        var assembly = LoadLeafAssembly();
        var path = CreateTempFile("Name,Age\nAlice,41\n");
        try
        {
            var descriptor = InvokeFileSource(assembly, path, CreateOptions(assembly), null, null);
            var source = await ActivateSourceAsync(descriptor);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try
            {
                await source.InitializeAsync();
                await Assert.ThrowsAsync<OperationCanceledException>(async () => await ReadValuesAsync(source, cancellation.Token));
            }
            finally
            {
                await source.DisposeAsync();
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task FileSource_ActivationCancellationRemainsActiveForDefaultReadToken()
    {
        var assembly = LoadLeafAssembly();
        var path = CreateTempFile("Name,Age\nAlice,41\n");
        using var activationCancellation = new CancellationTokenSource();
        try
        {
            var descriptor = InvokeFileSource(assembly, path, CreateOptions(assembly), null, null);
            var source = await ActivateSourceAsync(descriptor, activationCancellation.Token);
            try
            {
                await source.InitializeAsync();
                activationCancellation.Cancel();

                await Assert.ThrowsAsync<OperationCanceledException>(async () => await ReadValuesAsync(source));
            }
            finally
            {
                await source.DisposeAsync();
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task FileSource_ReadFailurePreservesPrimaryAndCleansOwnedResourcesOnceInOrder()
    {
        var events = new List<string>();
        var reader = new FaultInjectingReader(events) { FailNextRead = true, FailNextDispose = true };
        FaultInjectingBridge? bridge = null;
        var source = new StrictCsvFileSource<Person>(
            "injected.csv",
            CsvSourceOptionsSnapshot.Create(new CsvSourceOptions { HasHeaderRecord = false }, loggerAvailable: false),
            CsvMapRegistration<Person>.Auto,
            logger: null,
            activationCancellationToken: CancellationToken.None,
            readerFactory: (_, _, _) => ValueTask.FromResult<TextReader>(reader),
            bridgeFactory: (framer, cancellationToken) =>
            {
                bridge = new FaultInjectingBridge(framer, cancellationToken, events)
                {
                    FailNextDispose = true,
                };
                return bridge;
            });

        var failure = await Assert.ThrowsAsync<AggregateException>(async () =>
        {
            await foreach (var _ in source.ReadEnvelopesAsync())
            {
            }
        });

        Assert.Collection(
            failure.InnerExceptions,
            primary => Assert.IsType<TestReadException>(primary),
            bridgeCleanup => Assert.IsType<TestDisposeException>(bridgeCleanup),
            readerCleanup => Assert.IsType<TestDisposeException>(readerCleanup));
        Assert.Equal(["read", "bridge-dispose", "reader-dispose"], events);
        Assert.Equal(1, bridge!.DisposeCalls);
        Assert.Equal(1, reader.DisposeCalls);

        await source.DisposeAsync();

        Assert.Equal(1, bridge.DisposeCalls);
        Assert.Equal(1, reader.DisposeCalls);
    }

    [Fact]
    public async Task FileSource_RepeatedDisposeAfterCleanupFailureReturnsSameFailureWithoutObjectDisposed()
    {
        var events = new List<string>();
        var reader = new FaultInjectingReader(events) { FailNextDispose = true };
        FaultInjectingBridge? bridge = null;
        var source = CreateInjectedSource(reader, events, value => bridge = value);

        await source.InitializeAsync();
        bridge!.FailNextDispose = true;

        var firstFailure = await Assert.ThrowsAsync<AggregateException>(() => source.DisposeAsync().AsTask());
        var secondFailure = await Assert.ThrowsAsync<AggregateException>(() => source.DisposeAsync().AsTask());

        Assert.Same(firstFailure, secondFailure);
        Assert.Equal(["bridge-dispose", "reader-dispose"], events);
        Assert.Equal(1, bridge.DisposeCalls);
        Assert.Equal(1, reader.DisposeCalls);
    }

    [Fact]
    public async Task FileSource_ConcurrentDisposeIsSingleFlightAndCleansOnce()
    {
        var events = new List<string>();
        var reader = new FaultInjectingReader(events);
        FaultInjectingBridge? bridge = null;
        var source = CreateInjectedSource(reader, events, value => bridge = value);

        await source.InitializeAsync();
        bridge!.BlockDispose = true;

        var first = Task.Run(() => source.DisposeAsync().AsTask());
        await bridge.DisposeStarted.Task;
        var second = source.DisposeAsync().AsTask();

        bridge.ReleaseDispose();
        await Task.WhenAll(first, second);
        await source.DisposeAsync();

        Assert.Equal(["bridge-dispose", "reader-dispose"], events);
        Assert.Equal(1, bridge.DisposeCalls);
        Assert.Equal(1, reader.DisposeCalls);
    }

    private static StrictCsvFileSource<Person> CreateInjectedSource(
        FaultInjectingReader reader,
        List<string> events,
        Action<FaultInjectingBridge> captureBridge) =>
        new(
            "injected.csv",
            CsvSourceOptionsSnapshot.Create(new CsvSourceOptions { HasHeaderRecord = false }, loggerAvailable: false),
            CsvMapRegistration<Person>.Auto,
            logger: null,
            activationCancellationToken: CancellationToken.None,
            readerFactory: (_, _, _) => ValueTask.FromResult<TextReader>(reader),
            bridgeFactory: (framer, cancellationToken) =>
            {
                var bridge = new FaultInjectingBridge(framer, cancellationToken, events);
                captureBridge(bridge);
                return bridge;
            });

    private static Assembly LoadLeafAssembly() => Assembly.Load("SmartPipe.Extensions.Csv");

    private static object CreateOptions(Assembly assembly) =>
        Activator.CreateInstance(RequireType(assembly, "CsvSourceOptions"))!;

    private static object CreateFactoryRegistration(Assembly assembly)
    {
        PersonMap.CreatedCount = 0;
        var registrationType = RequireType(assembly, "CsvMapRegistration`1").MakeGenericType(typeof(Person));
        var method = registrationType.GetMethod("FromFactory", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        var factoryType = typeof(Func<>).MakeGenericType(typeof(ClassMap<Person>));
        var factory = Delegate.CreateDelegate(
            factoryType,
            typeof(CsvStrictSourceTests).GetMethod(nameof(CreatePersonMap), BindingFlags.NonPublic | BindingFlags.Static)!);
        return method!.Invoke(null, [factory])!;
    }

    private static ClassMap<Person> CreatePersonMap() => new PersonMap();

    private static object InvokeFileSource(
        Assembly assembly,
        string path,
        object options,
        object? map,
        object? loggerFactory)
    {
        var method = RequireType(assembly, "CsvPipelineComponents")
            .GetMethod("FileSource", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        return method!.MakeGenericMethod(typeof(Person)).Invoke(null, [path, options, map, loggerFactory])!;
    }

    private static async Task<IPipelineSource<Person>> ActivateSourceAsync(object descriptor)
        => await ActivateSourceAsync(descriptor, TestContext.Current.CancellationToken);

    private static async Task<IPipelineSource<Person>> ActivateSourceAsync(
        object descriptor,
        CancellationToken activationCancellationToken)
    {
        var activator = descriptor.GetType().GetProperty(
            "Activator",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(descriptor) as Delegate;
        Assert.NotNull(activator);
        var valueTask = activator!.DynamicInvoke(
            new PipelineActivationContext(new PipelineKey("csv-source"), Guid.NewGuid()),
            activationCancellationToken);
        Assert.NotNull(valueTask);
        var task = (Task)valueTask!.GetType().GetMethod("AsTask")!.Invoke(valueTask, null)!;
        await task;
        return Assert.IsAssignableFrom<IPipelineSource<Person>>(task.GetType().GetProperty("Result")!.GetValue(task));
    }

    private static async Task<List<Person>> ReadValuesAsync(
        IPipelineSource<Person> source,
        CancellationToken cancellationToken = default)
    {
        var values = new List<Person>();
        await foreach (var envelope in source.ReadEnvelopesAsync(cancellationToken))
            values.Add(envelope.Payload);
        return values;
    }

    private static string CreateTempFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"smartpipe-csv-source-{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private static Type RequireType(Assembly assembly, string name)
    {
        var type = assembly.GetType($"SmartPipe.Extensions.Csv.{name}");
        Assert.NotNull(type);
        return type!;
    }

    private static void SetOption(object options, string name, object value) =>
        options.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)!.SetValue(options, value);

    private static object ParseEnum(Assembly assembly, string name, string value) =>
        Enum.Parse(RequireType(assembly, name), value);

    private sealed record Person
    {
        public string Name { get; init; } = string.Empty;
        public int Age { get; init; }
    }

    private sealed class PersonMap : ClassMap<Person>
    {
        public static int CreatedCount;

        public PersonMap()
        {
            Interlocked.Increment(ref CreatedCount);
            Map(person => person.Name).Name("Name");
            Map(person => person.Age).Name("Age");
        }
    }

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public List<string> Messages { get; } = [];

        public int CreateLoggerCalls { get; private set; }

        public void AddProvider(ILoggerProvider provider) { }

        public ILogger CreateLogger(string categoryName)
        {
            CreateLoggerCalls++;
            return new CapturingLogger(Messages);
        }

        public void Dispose() { }
    }

    private sealed class CapturingLogger(List<string> messages) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => messages.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose() { }
        }
    }

    private sealed class TestReadException(string message) : IOException(message);
    private sealed class TestDisposeException(string message) : IOException(message);

    private sealed class FaultInjectingReader(List<string> events) : StringReader("Name\r\nAlice\r\n")
    {
        public bool FailNextRead { get; set; }
        public bool FailNextDispose { get; set; }
        public int DisposeCalls { get; private set; }

        public override ValueTask<int> ReadAsync(
            Memory<char> buffer,
            CancellationToken cancellationToken = default)
        {
            if (FailNextRead)
            {
                FailNextRead = false;
                events.Add("read");
                return ValueTask.FromException<int>(new TestReadException("primary-read"));
            }

            return base.ReadAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (!disposing)
            {
                base.Dispose(disposing);
                return;
            }

            DisposeCalls++;
            events.Add("reader-dispose");
            if (FailNextDispose)
            {
                FailNextDispose = false;
                throw new TestDisposeException("reader-dispose");
            }

            base.Dispose(disposing);
        }
    }

    private sealed class FaultInjectingBridge(
        CsvLogicalRecordFramer framer,
        CancellationToken cancellationToken,
        List<string> events) : CsvBoundedRecordTextReader(framer, cancellationToken)
    {
        public bool FailNextDispose { get; set; }
        public bool BlockDispose { get; set; }
        public int DisposeCalls { get; private set; }
        public TaskCompletionSource DisposeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource DisposeRelease { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseDispose() => DisposeRelease.TrySetResult();

        protected override void Dispose(bool disposing)
        {
            if (!disposing)
            {
                base.Dispose(disposing);
                return;
            }

            DisposeCalls++;
            events.Add("bridge-dispose");
            if (BlockDispose)
            {
                DisposeStarted.TrySetResult();
                DisposeRelease.Task.GetAwaiter().GetResult();
            }

            if (FailNextDispose)
            {
                FailNextDispose = false;
                throw new TestDisposeException("bridge-dispose");
            }

            base.Dispose(disposing);
        }
    }
}
