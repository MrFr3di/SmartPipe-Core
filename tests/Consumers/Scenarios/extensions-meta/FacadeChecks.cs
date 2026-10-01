using SmartPipe.Extensions;

internal static class FacadeChecks
{
    internal static void Verify()
    {
        var facade = typeof(SmartPipeHostedService<,>).Assembly;
        var expected = new Dictionary<Type, string>
        {
            [typeof(SmartPipe.Extensions.ChannelMerge)] = "SmartPipe.Extensions.Channels",
            [typeof(SmartPipe.Extensions.Selectors.CsvFileSource<>)] = "SmartPipe.Extensions.Csv",
            [typeof(SmartPipe.Extensions.Selectors.DapperSelector<>)] = "SmartPipe.Extensions.Dapper",
            [typeof(SmartPipe.Extensions.Selectors.DeadLetterSource<>)] = "SmartPipe.Extensions.Json",
            [typeof(SmartPipe.Extensions.Selectors.EfCoreSelector<>)] = "SmartPipe.Extensions.EntityFrameworkCore",
            [typeof(SmartPipe.Extensions.Selectors.JsonFileSource<>)] = "SmartPipe.Extensions.Json",
            [typeof(SmartPipe.Extensions.Sinks.CsvFileSink<>)] = "SmartPipe.Extensions.Csv",
            [typeof(SmartPipe.Extensions.Sinks.DbSink<>)] = "SmartPipe.Extensions.Dapper",
            [typeof(SmartPipe.Extensions.Sinks.DeadLetterSink<>)] = "SmartPipe.Extensions.Json",
            [typeof(SmartPipe.Extensions.Sinks.DeadLetterWriteException)] = "SmartPipe.Extensions.Json",
            [typeof(SmartPipe.Extensions.Sinks.DeadLetterWriteFailureMode)] = "SmartPipe.Extensions.Json",
            [typeof(SmartPipe.Extensions.Sinks.JsonFileSink<>)] = "SmartPipe.Extensions.Json",
            [typeof(SmartPipe.Extensions.Sinks.LoggerSink<>)] = "SmartPipe.Extensions.Logging",
            [typeof(SmartPipe.Extensions.Transforms.CompositeTransform<>)] = "SmartPipe.Extensions.Transforms",
            [typeof(SmartPipe.Extensions.Transforms.CompressionAlgorithm)] = "SmartPipe.Extensions.Transforms",
            [typeof(SmartPipe.Extensions.Transforms.CompressionTransform)] = "SmartPipe.Extensions.Transforms",
            [typeof(SmartPipe.Extensions.Transforms.ConditionalTransform<>)] = "SmartPipe.Extensions.Transforms",
            [typeof(SmartPipe.Extensions.Transforms.CsvTransform<,>)] = "SmartPipe.Extensions.Csv",
            [typeof(SmartPipe.Extensions.Transforms.FilterTransform<>)] = "SmartPipe.Extensions.Transforms",
            [typeof(SmartPipe.Extensions.Transforms.FilterValidationExtensions)] = "SmartPipe.Extensions.DataAnnotations",
            [typeof(SmartPipe.Extensions.Transforms.JsonTransform<,>)] = "SmartPipe.Extensions.Json",
            [typeof(SmartPipe.Extensions.Transforms.MapsterTransform<,>)] = "SmartPipe.Extensions.Mapster",
            [typeof(SmartPipe.Extensions.Transforms.ValidationTransform<>)] = "SmartPipe.Extensions.DataAnnotations",
        };
        if (!expected.Keys.ToHashSet().SetEquals(facade.GetForwardedTypes()))
            throw new InvalidOperationException("Facade forwarding inventory differs from the 23 retained moves.");
        foreach (var (type, owner) in expected)
            if (type.Assembly.GetName().Name != owner)
                throw new InvalidOperationException($"Wrong implementation owner for {type}: {type.Assembly.FullName}");

        Type[] retained =
        [
            typeof(SmartPipe.Extensions.ISmartPipeDefinition<,>),
            typeof(SmartPipe.Extensions.ISmartPipeFactory<,>),
            typeof(SmartPipe.Extensions.ISmartPipeRunHealthMonitor<,>),
            typeof(SmartPipe.Extensions.SmartPipeDefinitionBuilder<,>),
            typeof(SmartPipe.Extensions.SmartPipeDefinition<,>),
            typeof(SmartPipe.Extensions.SmartPipeFactory<,>),
            typeof(SmartPipe.Extensions.SmartPipeHealthCheckOptions),
            typeof(SmartPipe.Extensions.SmartPipeHealthSnapshot),
            typeof(SmartPipe.Extensions.SmartPipeHostedFailureBehavior),
            typeof(SmartPipe.Extensions.SmartPipeHostedServiceOptions),
            typeof(SmartPipe.Extensions.SmartPipeHostedService<,>),
            typeof(SmartPipe.Extensions.SmartPipeRunHealthMonitor<,>),
            typeof(SmartPipe.Extensions.SmartPipeServiceCollectionExtensions),
        ];
        if (!retained.ToHashSet().SetEquals(facade.GetExportedTypes()))
            throw new InvalidOperationException("Facade public implementation inventory differs from the 13 frozen identities.");
    }
}
