using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Dapper.Tests;

public sealed class DapperContractTests
{
    private static readonly string[] EntryPointTypeNames =
    [
        nameof(DapperPipelineComponents),
        nameof(DapperPipelineDefinitionBuilder),
        nameof(DapperPipelineDefinitionBuilderExtensions),
    ];

    [Fact]
    public void LeafOptions_ExposeTheFinalContractDefaults()
    {
        Assert.Equal(
            [nameof(DapperCommandCacheMode.Default), nameof(DapperCommandCacheMode.NoCache)],
            Enum.GetNames<DapperCommandCacheMode>());
        Assert.Equal([0, 1], Enum.GetValues<DapperCommandCacheMode>().Select(value => (int)value));
        Assert.Equal(
            [nameof(DapperBatchTransactionMode.None), nameof(DapperBatchTransactionMode.PerBatch)],
            Enum.GetNames<DapperBatchTransactionMode>());
        Assert.Equal([0, 1], Enum.GetValues<DapperBatchTransactionMode>().Select(value => (int)value));

        var query = new DapperQueryOptions();
        Assert.Equal("query", query.OperationName);
        Assert.Equal(30, query.CommandTimeoutSeconds);
        Assert.Equal(CommandType.Text, query.CommandType);
        Assert.Equal(DapperCommandCacheMode.Default, query.CacheMode);

        AssertRecordShape(
            typeof(DapperQueryOptions),
            ("OperationName", typeof(string), isRequired: false),
            ("CommandTimeoutSeconds", typeof(int?), isRequired: false),
            ("CommandType", typeof(CommandType), isRequired: false),
            ("CacheMode", typeof(DapperCommandCacheMode), isRequired: false));
        AssertRecordShape(
            typeof(DapperSinkOptions),
            ("OperationName", typeof(string), isRequired: true),
            ("CommandTimeoutSeconds", typeof(int?), isRequired: false),
            ("CommandType", typeof(CommandType), isRequired: false),
            ("CacheMode", typeof(DapperCommandCacheMode), isRequired: false));
        AssertRecordShape(
            typeof(DapperBatchSinkOptions),
            ("OperationName", typeof(string), isRequired: true),
            ("CommandTimeoutSeconds", typeof(int?), isRequired: false),
            ("CommandType", typeof(CommandType), isRequired: false),
            ("CacheMode", typeof(DapperCommandCacheMode), isRequired: false),
            ("TransactionMode", typeof(DapperBatchTransactionMode), isRequired: true),
            ("MaxBatchItems", typeof(int), isRequired: false),
            ("IsolationLevel", typeof(IsolationLevel?), isRequired: false));

        var defaults = new DapperBatchSinkOptions { OperationName = "batch", TransactionMode = DapperBatchTransactionMode.None };
        Assert.Equal(1_000, defaults.MaxBatchItems);
        Assert.Null(defaults.IsolationLevel);
        Assert.Equal(30, defaults.CommandTimeoutSeconds);
        Assert.Equal(CommandType.Text, defaults.CommandType);
        Assert.Equal(DapperCommandCacheMode.Default, defaults.CacheMode);
    }

    [Fact]
    public void LeafEntryPoints_ExposeBothAcquisitionForms()
    {
        AssertEntryPoints(
            typeof(DapperPipelineComponents),
            ("QuerySource", 2, 6),
            ("CommandSink", 2, 5),
            ("BatchCommandSink", 2, 5));
        AssertEntryPoints(
            typeof(DapperPipelineDefinitionBuilder),
            ("FromQuery", 2, 7));
        AssertEntryPoints(
            typeof(DapperPipelineDefinitionBuilderExtensions),
            ("ToCommand", 4, 6),
            ("ToBatchCommand", 4, 6));

        foreach (var method in PublicMethods(typeof(DapperPipelineComponents)))
        {
            var parameters = method.GetParameters();
            var acquisition = parameters[0].ParameterType;
            var isDataSource = acquisition == typeof(DbDataSource);
            var isConnectionFactory = acquisition
                == typeof(Func<PipelineActivationContext, CancellationToken, ValueTask<DbConnection>>);
            Assert.True(isDataSource || isConnectionFactory, $"{method.Name} exposes an unexpected acquisition form.");
            Assert.Equal(typeof(string), parameters[1].ParameterType);
            Assert.False(parameters[2].IsOptional, $"{method.Name} requires its options parameter.");
            for (var index = 3; index < parameters.Length; index++)
                Assert.True(parameters[index].IsOptional, $"{method.Name} parameter {index} must be optional.");
        }
    }

    [Fact]
    public void LeafEntryPoints_AdvertiseTrimAndDynamicCodeBoundaries()
    {
        var annotations = 0;
        foreach (var typeName in EntryPointTypeNames)
        {
            var type = typeof(DapperQueryOptions).Assembly.GetType($"SmartPipe.Extensions.Dapper.{typeName}");
            Assert.NotNull(type);
            var methods = PublicMethods(type!).ToArray();
            Assert.NotEmpty(methods);
            foreach (var method in methods)
            {
                var unreferenced = method.GetCustomAttribute<RequiresUnreferencedCodeAttribute>();
                Assert.NotNull(unreferenced);
                Assert.Contains("reflection", unreferenced!.Message, StringComparison.OrdinalIgnoreCase);
                var dynamicCode = method.GetCustomAttribute<RequiresDynamicCodeAttribute>();
                Assert.NotNull(dynamicCode);
                Assert.Contains("runtime code generation", dynamicCode!.Message, StringComparison.OrdinalIgnoreCase);
                annotations += 2;
            }
        }

        Assert.Equal(32, annotations);
    }

    [Fact]
    public async Task LeafComposition_PerformsNoIoAndSnapshotsOptionValues()
    {
        var journal = new DapperTestJournal();
        var createdConnections = new List<RecordingDbConnection>();
        var factoryCalls = 0;
        var dataSource = new RecordingDbDataSource(() =>
        {
            var connection = new RecordingDbConnection(journal);
            createdConnections.Add(connection);
            return connection;
        });

        ValueTask<DbConnection> ConnectionFactory(PipelineActivationContext _, CancellationToken __)
        {
            factoryCalls++;
            var connection = new RecordingDbConnection(journal);
            createdConnections.Add(connection);
            return ValueTask.FromResult<DbConnection>(connection);
        }

        var options = new DapperQueryOptions { CommandTimeoutSeconds = 30, CommandType = CommandType.Text };
        var descriptor = DapperPipelineComponents.QuerySource<int>(
            ConnectionFactory,
            "select 1",
            options,
            rowMapper: reader => reader.GetInt32(0));
        Assert.Equal(PipelineComponentOwnership.RuntimeOwned, descriptor.Ownership);
        Assert.True(descriptor.Initialize);
        Assert.Equal(0, factoryCalls);
        Assert.Empty(createdConnections);
        Assert.Equal(0, dataSource.CreateConnectionCount);

        // The descriptor froze the option values it validated at composition time.
        SetOption(options, nameof(DapperQueryOptions.CommandTimeoutSeconds), 999);
        SetOption(options, nameof(DapperQueryOptions.OperationName), "mutated");

        var source = await DapperTestActivation.ActivateAsync(
            descriptor,
            DapperTestActivation.CreateContext());
        Assert.Equal(0, factoryCalls);
        Assert.Empty(createdConnections);
        Assert.Equal(0, dataSource.CreateConnectionCount);

        var reader = new RecordingDbDataReader(journal, ["Id"], [1]);
        await source.InitializeAsync(TestContext.Current.CancellationToken);
        var connection = Assert.Single(createdConnections);
        connection.Reader = reader;
        Assert.Equal(1, factoryCalls);

        var envelopes = await DapperTestActivation.ReadAllAsync(source, TestContext.Current.CancellationToken);
        var command = connection.SingleCommand;
        Assert.Equal(30, command.CommandTimeout);
        Assert.Equal(CommandType.Text, command.CommandType);
        Assert.Equal("select 1", command.CommandText);
        Assert.Equal(1, Assert.Single(envelopes).Payload);

        await source.DisposeAsync();
        Assert.Equal(1, connection.DisposeCount);
    }

    [Fact]
    public void LeafComposition_RejectsInvalidQueryOptionsBeforeAnyIo()
    {
        var journal = new DapperTestJournal();
        var factoryCalls = 0;
        var dataSourceOpens = 0;
        ValueTask<DbConnection> Factory(PipelineActivationContext _, CancellationToken __)
        {
            factoryCalls++;
            return ValueTask.FromResult<DbConnection>(new RecordingDbConnection(journal));
        }

        var dataSource = new RecordingDbDataSource(() =>
        {
            dataSourceOpens++;
            return new RecordingDbConnection(journal);
        });
        var cases = new List<(string, Action, Type)>();
        AddCommonCases(
            cases,
            "query",
            Factory,
            (factory, sql, options) => DapperPipelineComponents.QuerySource<int>(factory, sql, options));
        cases.Add((
            "query data source sql",
            () => DapperPipelineComponents.QuerySource<int>(dataSource, "", new DapperQueryOptions()),
            typeof(ArgumentException)));
        cases.Add((
            "query data source options",
            () => DapperPipelineComponents.QuerySource<int>(
                dataSource,
                "select 1",
                new DapperQueryOptions { CommandTimeoutSeconds = -5 }),
            typeof(ArgumentOutOfRangeException)));

        AssertRejections(cases);
        Assert.Equal(0, factoryCalls);
        Assert.Equal(0, dataSourceOpens);
        Assert.Equal(0, dataSource.CreateConnectionCount);
    }

    [Fact]
    public void LeafComposition_RejectsInvalidSinkOptionsBeforeAnyIo()
    {
        var journal = new DapperTestJournal();
        var factoryCalls = 0;
        var dataSourceOpens = 0;
        ValueTask<DbConnection> Factory(PipelineActivationContext _, CancellationToken __)
        {
            factoryCalls++;
            return ValueTask.FromResult<DbConnection>(new RecordingDbConnection(journal));
        }

        var dataSource = new RecordingDbDataSource(() =>
        {
            dataSourceOpens++;
            return new RecordingDbConnection(journal);
        });
        var cases = new List<(string, Action, Type)>();
        AddCommonCases(
            cases,
            "sink",
            Factory,
            (factory, sql, options) => DapperPipelineComponents.CommandSink<int>(
                factory,
                sql,
                options is null
                    ? null!
                    : new DapperSinkOptions
                    {
                        OperationName = options.OperationName,
                        CommandTimeoutSeconds = options.CommandTimeoutSeconds,
                        CommandType = options.CommandType,
                        CacheMode = options.CacheMode,
                    }));
        cases.Add((
            "sink null data source",
            () => DapperPipelineComponents.CommandSink<int>(
                (DbDataSource)null!,
                "update t set x = @x",
                new DapperSinkOptions { OperationName = "sink" }),
            typeof(ArgumentNullException)));
        cases.Add((
            "sink data source sql",
            () => DapperPipelineComponents.CommandSink<int>(
                dataSource,
                "   ",
                new DapperSinkOptions { OperationName = "sink" }),
            typeof(ArgumentException)));

        AssertRejections(cases);
        Assert.Equal(0, factoryCalls);
        Assert.Equal(0, dataSourceOpens);
        Assert.Equal(0, dataSource.CreateConnectionCount);
    }

    [Fact]
    public void LeafComposition_RejectsInvalidBatchSinkOptionsBeforeAnyIo()
    {
        var journal = new DapperTestJournal();
        var factoryCalls = 0;
        var dataSourceOpens = 0;
        ValueTask<DbConnection> Factory(PipelineActivationContext _, CancellationToken __)
        {
            factoryCalls++;
            return ValueTask.FromResult<DbConnection>(new RecordingDbConnection(journal));
        }

        var dataSource = new RecordingDbDataSource(() =>
        {
            dataSourceOpens++;
            return new RecordingDbConnection(journal);
        });
        var cases = new List<(string, Action, Type)>();
        AddCommonCases(
            cases,
            "batch",
            Factory,
            (factory, sql, options) => DapperPipelineComponents.BatchCommandSink<int>(
                factory,
                sql,
                options is null
                    ? null!
                    : new DapperBatchSinkOptions
                    {
                        OperationName = options.OperationName,
                        CommandTimeoutSeconds = options.CommandTimeoutSeconds,
                        CommandType = options.CommandType,
                        CacheMode = options.CacheMode,
                        TransactionMode = DapperBatchTransactionMode.PerBatch,
                    }));

        foreach (var maxBatchItems in new[] { 0, -1, 1_000_001 })
        {
            var invalid = maxBatchItems;
            cases.Add((
                $"batch max batch items {invalid}",
                () => DapperPipelineComponents.BatchCommandSink<int>(
                    Factory,
                    "insert into t values (@x)",
                    new DapperBatchSinkOptions
                    {
                        OperationName = "batch",
                        TransactionMode = DapperBatchTransactionMode.PerBatch,
                        MaxBatchItems = invalid,
                    }),
                typeof(ArgumentOutOfRangeException)));
        }

        cases.Add((
            "batch isolation level without a transaction",
            () => DapperPipelineComponents.BatchCommandSink<int>(
                Factory,
                "insert into t values (@x)",
                new DapperBatchSinkOptions
                {
                    OperationName = "batch",
                    TransactionMode = DapperBatchTransactionMode.None,
                    IsolationLevel = IsolationLevel.Serializable,
                }),
            typeof(ArgumentException)));
        cases.Add((
            "batch undefined transaction mode",
            () => DapperPipelineComponents.BatchCommandSink<int>(
                Factory,
                "insert into t values (@x)",
                new DapperBatchSinkOptions
                {
                    OperationName = "batch",
                    TransactionMode = (DapperBatchTransactionMode)9,
                }),
            typeof(ArgumentOutOfRangeException)));
        cases.Add((
            "batch undefined isolation level",
            () => DapperPipelineComponents.BatchCommandSink<int>(
                Factory,
                "insert into t values (@x)",
                new DapperBatchSinkOptions
                {
                    OperationName = "batch",
                    TransactionMode = DapperBatchTransactionMode.PerBatch,
                    IsolationLevel = (IsolationLevel)1234,
                }),
            typeof(ArgumentOutOfRangeException)));
        cases.Add((
            "batch null data source",
            () => DapperPipelineComponents.BatchCommandSink<int>(
                (DbDataSource)null!,
                "insert into t values (@x)",
                new DapperBatchSinkOptions
                {
                    OperationName = "batch",
                    TransactionMode = DapperBatchTransactionMode.None,
                }),
            typeof(ArgumentNullException)));
        cases.Add((
            "batch data source sql",
            () => DapperPipelineComponents.BatchCommandSink<int>(
                dataSource,
                "",
                new DapperBatchSinkOptions
                {
                    OperationName = "batch",
                    TransactionMode = DapperBatchTransactionMode.None,
                }),
            typeof(ArgumentException)));

        AssertRejections(cases);
        Assert.Equal(0, factoryCalls);
        Assert.Equal(0, dataSourceOpens);
        Assert.Equal(0, dataSource.CreateConnectionCount);
    }

    [Fact]
    public void LeafComposition_AcceptsEveryBoundaryValue()
    {
        var journal = new DapperTestJournal();
        ValueTask<DbConnection> Factory(PipelineActivationContext _, CancellationToken __) =>
            ValueTask.FromResult<DbConnection>(new RecordingDbConnection(journal));

        foreach (var timeout in new int?[] { null, 0, 86_400 })
        {
            Assert.NotNull(DapperPipelineComponents.QuerySource<int>(
                Factory,
                "select 1",
                new DapperQueryOptions { CommandTimeoutSeconds = timeout }));
            Assert.NotNull(DapperPipelineComponents.CommandSink<int>(
                Factory,
                "update t set x = @x",
                new DapperSinkOptions { OperationName = "sink", CommandTimeoutSeconds = timeout }));
        }

        foreach (var maxBatchItems in new[] { 1, 1_000_000 })
        {
            Assert.NotNull(DapperPipelineComponents.BatchCommandSink<int>(
                Factory,
                "insert into t values (@x)",
                new DapperBatchSinkOptions
                {
                    OperationName = "batch",
                    TransactionMode = DapperBatchTransactionMode.PerBatch,
                    MaxBatchItems = maxBatchItems,
                    IsolationLevel = IsolationLevel.Serializable,
                }));
        }

        Assert.NotNull(DapperPipelineComponents.CommandSink<int>(
            new RecordingDbDataSource(() => new RecordingDbConnection(journal)),
            "update t set x = @x",
            new DapperSinkOptions
            {
                OperationName = new string('a', 64),
                CommandType = CommandType.StoredProcedure,
                CacheMode = DapperCommandCacheMode.NoCache,
            }));
        Assert.Empty(journal.Snapshot());
    }

    [Fact]
    public void LeafDefinitions_DelegateToCoreDefinitionTypesWithoutIo()
    {
        var journal = new DapperTestJournal();
        var factoryCalls = 0;
        ValueTask<DbConnection> ConnectionFactory(PipelineActivationContext _, CancellationToken __)
        {
            factoryCalls++;
            return ValueTask.FromResult<DbConnection>(new RecordingDbConnection(journal));
        }

        var queryOptions = new DapperQueryOptions();
        var sinkOptions = new DapperSinkOptions { OperationName = "sink" };
        var batchOptions = new DapperBatchSinkOptions
        {
            OperationName = "batch",
            TransactionMode = DapperBatchTransactionMode.None,
        };

        var builder = DapperPipelineDefinitionBuilder.FromQuery<int>(
            new PipelineKey("definition"),
            ConnectionFactory,
            "select 1",
            queryOptions);
        Assert.IsType<PipelineDefinitionBuilder<int>>(builder);

        var commandDefinition = builder.ToCommand(ConnectionFactory, "update t set x = @x", sinkOptions);
        Assert.IsType<PipelineDefinition<int, int>>(commandDefinition);

        var dataSourceBuilder = DapperPipelineDefinitionBuilder.FromQuery<int>(
            new PipelineKey("definition-data-source"),
            new RecordingDbDataSource(() => new RecordingDbConnection(journal)),
            "select 1",
            queryOptions);
        var dataSourceDefinition = dataSourceBuilder.ToCommand(
            new RecordingDbDataSource(() => new RecordingDbConnection(journal)),
            "update t set x = @x",
            sinkOptions);
        Assert.IsType<PipelineDefinition<int, int>>(dataSourceDefinition);

        var batchBuilder = SmartPipe.Core.PipelineDefinitionBuilder.From(
            new PipelineKey("batch-definition"),
            DapperPipelineComponents.QuerySource<IReadOnlyList<int>>(
                ConnectionFactory,
                "select 1",
                queryOptions,
                rowMapper: _ => new List<int>()));
        var batchDefinition = batchBuilder.ToBatchCommand(
            ConnectionFactory,
            "insert into t values (@x)",
            batchOptions);
        Assert.IsType<PipelineDefinition<IReadOnlyList<int>, IReadOnlyList<int>>>(batchDefinition);

        var listStaged = builder.Transform(
            new PipelineStageKey("to-list"),
            PipelineComponent.Borrowed<IPipelineTransformer<int, IReadOnlyList<int>>>(new ListTransformer()));
        var stagedBatchDefinition = listStaged.ToBatchCommand<int, int>(
            ConnectionFactory,
            "insert into t values (@x)",
            batchOptions);
        Assert.IsType<PipelineDefinition<int, IReadOnlyList<int>>>(stagedBatchDefinition);

        var textStaged = builder.Transform(
            new PipelineStageKey("to-text"),
            PipelineComponent.Borrowed<IPipelineTransformer<int, string>>(new TextTransformer()));
        var stagedCommandDefinition = textStaged.ToCommand(ConnectionFactory, "update t set x = @x", sinkOptions);
        Assert.IsType<PipelineDefinition<int, string>>(stagedCommandDefinition);

        Assert.Equal(0, factoryCalls);
        Assert.Empty(journal.Snapshot());
    }

    private static void AddCommonCases(
        List<(string, Action, Type)> cases,
        string prefix,
        Func<PipelineActivationContext, CancellationToken, ValueTask<DbConnection>> factory,
        Func<Func<PipelineActivationContext, CancellationToken, ValueTask<DbConnection>>, string, DapperQueryOptions, object> compose)
    {
        foreach (var operationName in new[] { "", "   ", "bad\u0001name", new string('x', 65) })
        {
            var invalid = operationName;
            var expected = invalid.Length > 64 ? typeof(ArgumentOutOfRangeException) : typeof(ArgumentException);
            cases.Add((
                $"{prefix} operation name length {invalid.Length}",
                () => compose(factory, "select 1", new DapperQueryOptions { OperationName = invalid }),
                expected));
        }

        cases.Add((
            $"{prefix} null operation name",
            () => compose(factory, "select 1", new DapperQueryOptions { OperationName = null! }),
            typeof(ArgumentException)));

        foreach (var timeout in new int?[] { -1, 86_401 })
        {
            var invalid = timeout;
            cases.Add((
                $"{prefix} timeout {invalid}",
                () => compose(factory, "select 1", new DapperQueryOptions { CommandTimeoutSeconds = invalid }),
                typeof(ArgumentOutOfRangeException)));
        }

        cases.Add((
            $"{prefix} undefined command type",
            () => compose(factory, "select 1", new DapperQueryOptions { CommandType = (CommandType)42 }),
            typeof(ArgumentOutOfRangeException)));
        cases.Add((
            $"{prefix} undefined cache mode",
            () => compose(factory, "select 1", new DapperQueryOptions { CacheMode = (DapperCommandCacheMode)7 }),
            typeof(ArgumentOutOfRangeException)));

        foreach (var sql in new[] { "", "   ", null })
        {
            var invalid = sql;
            cases.Add((
                $"{prefix} sql '{invalid ?? "null"}'",
                () => compose(factory, invalid!, new DapperQueryOptions()),
                typeof(ArgumentException)));
        }

        cases.Add((
            $"{prefix} null options",
            () => compose(factory, "select 1", null!),
            typeof(ArgumentNullException)));
        cases.Add((
            $"{prefix} null connection factory",
            () => compose(
                (Func<PipelineActivationContext, CancellationToken, ValueTask<DbConnection>>)null!,
                "select 1",
                new DapperQueryOptions()),
            typeof(ArgumentNullException)));
    }

    private static void AssertRejections(List<(string Description, Action Compose, Type Expected)> cases)
    {
        Assert.NotEmpty(cases);
        foreach (var (description, compose, expected) in cases)
        {
            var exception = Capture(compose);
            Assert.True(exception is not null, $"{description} was accepted.");
            Assert.True(
                expected.IsInstanceOfType(exception),
                $"{description} threw {exception!.GetType().Name} instead of {expected.Name}.");
        }
    }

    private static Exception? Capture(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static IEnumerable<MethodInfo> PublicMethods(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);

    private static void AssertEntryPoints(
        Type type,
        params (string Name, int Overloads, int ParameterCount)[] expected)
    {
        foreach (var (name, overloads, parameterCount) in expected)
        {
            var methods = PublicMethods(type).Where(method => method.Name == name).ToArray();
            Assert.Equal(overloads, methods.Length);
            foreach (var method in methods)
            {
                Assert.True(method.IsGenericMethodDefinition, $"{name} must be generic.");
                Assert.Equal(parameterCount, method.GetParameters().Length);
            }
        }

        Assert.Equal(expected.Sum(entry => entry.Overloads), PublicMethods(type).Count());
    }

    private static void AssertRecordShape(
        Type type,
        params (string Name, Type PropertyType, bool isRequired)[] members)
    {
        Assert.True(type.IsClass && type.IsSealed);
        Assert.True(typeof(IEquatable<>).MakeGenericType(type).IsAssignableFrom(type));
        foreach (var (name, propertyType, isRequired) in members)
        {
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(property);
            Assert.Equal(propertyType, property!.PropertyType);
            Assert.NotNull(property.SetMethod);
            Assert.Equal(isRequired, property.GetCustomAttribute<RequiredMemberAttribute>() is not null);
        }

        Assert.Equal(members.Length, type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Length);
    }

    private static void SetOption(object options, string propertyName, object? value) =>
        options.GetType()
            .GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)!
            .SetValue(options, value);

    private sealed class TextTransformer : IPipelineTransformer<int, string>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<StageResult<string>> TransformAsync(
            ProcessingEnvelope<int> envelope,
            CancellationToken ct = default) =>
            ValueTask.FromResult(StageResult<string>.Success(envelope.Payload.ToString()));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ListTransformer : IPipelineTransformer<int, IReadOnlyList<int>>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<StageResult<IReadOnlyList<int>>> TransformAsync(
            ProcessingEnvelope<int> envelope,
            CancellationToken ct = default) =>
            ValueTask.FromResult(StageResult<IReadOnlyList<int>>.Success([envelope.Payload]));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
