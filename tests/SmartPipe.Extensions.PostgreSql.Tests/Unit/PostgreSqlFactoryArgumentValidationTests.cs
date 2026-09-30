namespace SmartPipe.Extensions.PostgreSql.Tests.Unit;

/// <summary>Validates every public factory, builder and terminal overload before any provider work can start.</summary>
public sealed class PostgreSqlFactoryArgumentValidationTests
{
    [Fact]
    public void BinaryCopySource_NullDataSource_IsRejected()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            PostgreSqlPipelineComponents.BinaryCopySource<int>(
                null!,
                PostgreSqlUnitTestSupport.CopyToCommand,
                PostgreSqlUnitTestSupport.RowReader,
                new PostgreSqlBinaryCopySourceOptions()));

        Assert.Equal("dataSource", exception.ParamName);
    }

    [Fact]
    public void BinaryCopySource_Multiplexing_IsRejectedBeforeProviderWork()
    {
        using var dataSource = Npgsql.NpgsqlDataSource.Create(
            PostgreSqlUnitTestSupport.UnreachableConnectionString + ";Multiplexing=true");
        var originalSettings = dataSource.ConnectionString;

        var error = Assert.Throws<ArgumentException>(() =>
            PostgreSqlPipelineComponents.BinaryCopySource<int>(
                dataSource,
                PostgreSqlUnitTestSupport.CopyToCommand,
                PostgreSqlUnitTestSupport.RowReader,
                new PostgreSqlBinaryCopySourceOptions()));

        Assert.Equal("dataSource", error.ParamName);
        Assert.Contains("COPY", error.Message, StringComparison.Ordinal);
        Assert.Contains("multiplexing", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(originalSettings, dataSource.ConnectionString);
        using var connection = dataSource.CreateConnection();
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
    }

    [Fact]
    public void BinaryCopySource_NullCopyToCommand_IsRejected()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            PostgreSqlPipelineComponents.BinaryCopySource<int>(
                dataSource,
                null!,
                PostgreSqlUnitTestSupport.RowReader,
                new PostgreSqlBinaryCopySourceOptions()));

        Assert.Equal("copyToCommand", exception.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\r\n")]
    public void BinaryCopySource_BlankCopyToCommand_IsRejected(string copyToCommand)
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();

        var exception = Assert.Throws<ArgumentException>(() =>
            PostgreSqlPipelineComponents.BinaryCopySource<int>(
                dataSource,
                copyToCommand,
                PostgreSqlUnitTestSupport.RowReader,
                new PostgreSqlBinaryCopySourceOptions()));

        Assert.StartsWith(Internal.PostgreSqlErrorMessages.CopyCommandBlank, exception.Message, StringComparison.Ordinal);
        Assert.Equal("copyToCommand", exception.ParamName);
    }

    [Fact]
    public void BinaryCopySource_NullRowReader_IsRejected()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            PostgreSqlPipelineComponents.BinaryCopySource<int>(
                dataSource,
                PostgreSqlUnitTestSupport.CopyToCommand,
                null!,
                new PostgreSqlBinaryCopySourceOptions()));

        Assert.Equal("rowReader", exception.ParamName);
    }

    [Fact]
    public void BinaryCopySource_NullOptions_IsRejected()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            PostgreSqlPipelineComponents.BinaryCopySource<int>(
                dataSource,
                PostgreSqlUnitTestSupport.CopyToCommand,
                PostgreSqlUnitTestSupport.RowReader,
                null!));

        Assert.Equal("options", exception.ParamName);
    }

    [Fact]
    public void BinaryCopyBatchSink_NullDataSource_IsRejected()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            PostgreSqlPipelineComponents.BinaryCopyBatchSink<int>(
                null!,
                PostgreSqlUnitTestSupport.CopyFromCommand,
                PostgreSqlUnitTestSupport.RowWriter,
                new PostgreSqlBinaryCopySinkOptions()));

        Assert.Equal("dataSource", exception.ParamName);
    }

    [Fact]
    public void BinaryCopyBatchSink_Multiplexing_IsRejectedBeforeProviderWork()
    {
        using var dataSource = Npgsql.NpgsqlDataSource.Create(
            PostgreSqlUnitTestSupport.UnreachableConnectionString + ";Multiplexing=true");
        var originalSettings = dataSource.ConnectionString;

        var error = Assert.Throws<ArgumentException>(() =>
            PostgreSqlPipelineComponents.BinaryCopyBatchSink<int>(
                dataSource,
                PostgreSqlUnitTestSupport.CopyFromCommand,
                PostgreSqlUnitTestSupport.RowWriter,
                new PostgreSqlBinaryCopySinkOptions()));

        Assert.Equal("dataSource", error.ParamName);
        Assert.Contains("COPY", error.Message, StringComparison.Ordinal);
        Assert.Contains("multiplexing", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(originalSettings, dataSource.ConnectionString);
        using var connection = dataSource.CreateConnection();
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
    }

    [Fact]
    public void BinaryCopyBatchSink_NullCopyFromCommand_IsRejected()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            PostgreSqlPipelineComponents.BinaryCopyBatchSink<int>(
                dataSource,
                null!,
                PostgreSqlUnitTestSupport.RowWriter,
                new PostgreSqlBinaryCopySinkOptions()));

        Assert.Equal("copyFromCommand", exception.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\r\n")]
    public void BinaryCopyBatchSink_BlankCopyFromCommand_IsRejected(string copyFromCommand)
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();

        var exception = Assert.Throws<ArgumentException>(() =>
            PostgreSqlPipelineComponents.BinaryCopyBatchSink<int>(
                dataSource,
                copyFromCommand,
                PostgreSqlUnitTestSupport.RowWriter,
                new PostgreSqlBinaryCopySinkOptions()));

        Assert.StartsWith(Internal.PostgreSqlErrorMessages.CopyCommandBlank, exception.Message, StringComparison.Ordinal);
        Assert.Equal("copyFromCommand", exception.ParamName);
    }

    [Fact]
    public void BinaryCopyBatchSink_NullRowWriter_IsRejected()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            PostgreSqlPipelineComponents.BinaryCopyBatchSink<int>(
                dataSource,
                PostgreSqlUnitTestSupport.CopyFromCommand,
                null!,
                new PostgreSqlBinaryCopySinkOptions()));

        Assert.Equal("rowWriter", exception.ParamName);
    }

    [Fact]
    public void BinaryCopyBatchSink_NullOptions_IsRejected()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            PostgreSqlPipelineComponents.BinaryCopyBatchSink<int>(
                dataSource,
                PostgreSqlUnitTestSupport.CopyFromCommand,
                PostgreSqlUnitTestSupport.RowWriter,
                null!));

        Assert.Equal("options", exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CopyFactories_DisposedMultiplexingDataSource_IsRejectedBeforeAnyOpen(bool sink)
    {
        var dataSource = Npgsql.NpgsqlDataSource.Create(
            PostgreSqlUnitTestSupport.UnreachableConnectionString + ";Multiplexing=true");
        dataSource.Dispose();

        // A disposed data source traps a connection open; composition must instead report the configuration error.
        var error = Assert.Throws<ArgumentException>(() => ComposeCopy(dataSource, sink));

        Assert.Equal("dataSource", error.ParamName);
        Assert.Contains("multiplexing", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CopyFactories_NonMultiplexingDataSource_IsAcceptedWithoutChangingBorrowedSettings(bool sink)
    {
        using var dataSource = Npgsql.NpgsqlDataSource.Create(
            PostgreSqlUnitTestSupport.UnreachableConnectionString + ";Multiplexing=false");
        var originalSettings = dataSource.ConnectionString;

        ComposeCopy(dataSource, sink);

        Assert.Equal(originalSettings, dataSource.ConnectionString);
        using var connection = dataSource.CreateConnection();
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
    }

    private static void ComposeCopy(Npgsql.NpgsqlDataSource dataSource, bool sink)
    {
        if (sink)
            PostgreSqlPipelineComponents.BinaryCopyBatchSink<int>(
                dataSource,
                PostgreSqlUnitTestSupport.CopyFromCommand,
                PostgreSqlUnitTestSupport.RowWriter,
                new PostgreSqlBinaryCopySinkOptions());
        else
            PostgreSqlPipelineComponents.BinaryCopySource<int>(
                dataSource,
                PostgreSqlUnitTestSupport.CopyToCommand,
                PostgreSqlUnitTestSupport.RowReader,
                new PostgreSqlBinaryCopySourceOptions());
    }

    [Fact]
    public void NotificationSource_NullDataSource_IsRejected()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            PostgreSqlPipelineComponents.NotificationSource(
                null!,
                new[] { "orders" },
                new PostgreSqlNotificationSourceOptions()));

        Assert.Equal("dataSource", exception.ParamName);
    }

    [Fact]
    public void NotificationSource_Multiplexing_IsRejectedBeforeProviderWork()
    {
        using var dataSource = Npgsql.NpgsqlDataSource.Create(
            PostgreSqlUnitTestSupport.UnreachableConnectionString + ";Multiplexing=true");
        var originalSettings = dataSource.ConnectionString;

        var error = Assert.Throws<ArgumentException>(() =>
            PostgreSqlPipelineComponents.NotificationSource(dataSource, ["events"], new PostgreSqlNotificationSourceOptions()));

        Assert.Equal("dataSource", error.ParamName);
        Assert.Contains("multiplexing", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(originalSettings, dataSource.ConnectionString);
        using var connection = dataSource.CreateConnection();
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
    }

    [Fact]
    public void NotificationSource_NullChannels_IsRejected()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            PostgreSqlPipelineComponents.NotificationSource(
                dataSource,
                null!,
                new PostgreSqlNotificationSourceOptions()));

        Assert.Equal("channels", exception.ParamName);
    }

    [Fact]
    public void NotificationSource_EmptyChannels_AreRejected()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();

        var exception = Assert.Throws<ArgumentException>(() =>
            PostgreSqlPipelineComponents.NotificationSource(
                dataSource,
                Array.Empty<string>(),
                new PostgreSqlNotificationSourceOptions()));

        Assert.StartsWith(Internal.PostgreSqlErrorMessages.ChannelsEmpty, exception.Message, StringComparison.Ordinal);
        Assert.Equal("channels", exception.ParamName);
    }

    [Fact]
    public void NotificationSource_DuplicateChannels_AreRejected()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();

        var exception = Assert.Throws<ArgumentException>(() =>
            PostgreSqlPipelineComponents.NotificationSource(
                dataSource,
                new[] { "orders", "orders" },
                new PostgreSqlNotificationSourceOptions()));

        Assert.StartsWith(Internal.PostgreSqlErrorMessages.ChannelDuplicate, exception.Message, StringComparison.Ordinal);
        Assert.Equal("channels", exception.ParamName);
    }

    [Fact]
    public void NotificationSource_CaseSensitiveChannels_AreAccepted()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();

        var descriptor = PostgreSqlPipelineComponents.NotificationSource(
            dataSource, ["events", "Events"], new PostgreSqlNotificationSourceOptions());

        Assert.Equal(Core.PipelineComponentOwnership.RuntimeOwned, descriptor.Ownership);
    }

    [Fact]
    public void NotificationSource_DuplicateBeforeBlank_ReportsBlankIndex()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();

        var error = Assert.Throws<ArgumentException>(() =>
            PostgreSqlPipelineComponents.NotificationSource(
                dataSource, ["events", "events", " "], new PostgreSqlNotificationSourceOptions()));

        Assert.StartsWith(Internal.PostgreSqlErrorMessages.ChannelEntryBlank, error.Message, StringComparison.Ordinal);
        Assert.Equal("channels[2]", error.ParamName);
    }

    [Fact]
    public void NotificationSource_NullOptions_IsRejected()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            PostgreSqlPipelineComponents.NotificationSource(
                dataSource,
                new[] { "orders" },
                null!));

        Assert.Equal("options", exception.ParamName);
    }

    [Fact]
    public void FromBinaryCopy_UninitializedPipelineKey_IsRejected()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();

        var exception = Assert.Throws<ArgumentException>(() =>
            PostgreSqlPipelineDefinitionBuilder.FromBinaryCopy<int>(
                default,
                dataSource,
                PostgreSqlUnitTestSupport.CopyToCommand,
                PostgreSqlUnitTestSupport.RowReader,
                new PostgreSqlBinaryCopySourceOptions()));

        Assert.Equal("pipelineKey", exception.ParamName);
    }

    [Fact]
    public void FromBinaryCopy_NullDataSource_IsRejected()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            PostgreSqlPipelineDefinitionBuilder.FromBinaryCopy<int>(
                new Core.PipelineKey("postgresql-from-copy"),
                null!,
                PostgreSqlUnitTestSupport.CopyToCommand,
                PostgreSqlUnitTestSupport.RowReader,
                new PostgreSqlBinaryCopySourceOptions()));

        Assert.Equal("dataSource", exception.ParamName);
    }

    [Fact]
    public void FromBinaryCopy_BlankCopyToCommand_IsRejected()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();

        var exception = Assert.Throws<ArgumentException>(() =>
            PostgreSqlPipelineDefinitionBuilder.FromBinaryCopy<int>(
                new Core.PipelineKey("postgresql-from-copy"),
                dataSource,
                "   ",
                PostgreSqlUnitTestSupport.RowReader,
                new PostgreSqlBinaryCopySourceOptions()));

        Assert.StartsWith(Internal.PostgreSqlErrorMessages.CopyCommandBlank, exception.Message, StringComparison.Ordinal);
        Assert.Equal("copyToCommand", exception.ParamName);
    }

    [Fact]
    public void FromBinaryCopy_NullRowReader_IsRejected()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            PostgreSqlPipelineDefinitionBuilder.FromBinaryCopy<int>(
                new Core.PipelineKey("postgresql-from-copy"),
                dataSource,
                PostgreSqlUnitTestSupport.CopyToCommand,
                null!,
                new PostgreSqlBinaryCopySourceOptions()));

        Assert.Equal("rowReader", exception.ParamName);
    }

    [Fact]
    public void FromBinaryCopy_NullOptions_IsRejected()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            PostgreSqlPipelineDefinitionBuilder.FromBinaryCopy<int>(
                new Core.PipelineKey("postgresql-from-copy"),
                dataSource,
                PostgreSqlUnitTestSupport.CopyToCommand,
                PostgreSqlUnitTestSupport.RowReader,
                null!));

        Assert.Equal("options", exception.ParamName);
    }

    [Fact]
    public void FromNotifications_UninitializedPipelineKey_IsRejected()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();

        var exception = Assert.Throws<ArgumentException>(() =>
            PostgreSqlPipelineDefinitionBuilder.FromNotifications(
                default,
                dataSource,
                new[] { "orders" },
                new PostgreSqlNotificationSourceOptions()));

        Assert.Equal("pipelineKey", exception.ParamName);
    }

    [Fact]
    public void FromNotifications_NullChannels_IsRejected()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            PostgreSqlPipelineDefinitionBuilder.FromNotifications(
                new Core.PipelineKey("postgresql-from-notifications"),
                dataSource,
                null!,
                new PostgreSqlNotificationSourceOptions()));

        Assert.Equal("channels", exception.ParamName);
    }

    [Fact]
    public void ToPostgreSqlBinaryCopy_NullBuilder_IsRejected()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            PostgreSqlPipelineDefinitionBuilderExtensions.ToPostgreSqlBinaryCopy<int>(
                null!,
                dataSource,
                PostgreSqlUnitTestSupport.CopyFromCommand,
                PostgreSqlUnitTestSupport.RowWriter,
                new PostgreSqlBinaryCopySinkOptions()));

        Assert.Equal("builder", exception.ParamName);
    }

    [Fact]
    public void ToPostgreSqlBinaryCopy_NullDataSource_IsRejected()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();
        var builder = PostgreSqlUnitTestBuilders.CreateBatchBuilder(dataSource, "postgresql-terminal-null-source");

        var exception = Assert.Throws<ArgumentNullException>(() =>
            builder.ToPostgreSqlBinaryCopy(
                null!,
                PostgreSqlUnitTestSupport.CopyFromCommand,
                PostgreSqlUnitTestSupport.RowWriter,
                new PostgreSqlBinaryCopySinkOptions()));

        Assert.Equal("dataSource", exception.ParamName);
    }

    [Fact]
    public void ToPostgreSqlBinaryCopy_BlankCopyFromCommand_IsRejected()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();
        var builder = PostgreSqlUnitTestBuilders.CreateBatchBuilder(dataSource, "postgresql-terminal-blank");

        var exception = Assert.Throws<ArgumentException>(() =>
            builder.ToPostgreSqlBinaryCopy(
                dataSource,
                " ",
                PostgreSqlUnitTestSupport.RowWriter,
                new PostgreSqlBinaryCopySinkOptions()));

        Assert.StartsWith(Internal.PostgreSqlErrorMessages.CopyCommandBlank, exception.Message, StringComparison.Ordinal);
        Assert.Equal("copyFromCommand", exception.ParamName);
    }

    [Fact]
    public void ToPostgreSqlBinaryCopy_NullRowWriter_IsRejected()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();
        var builder = PostgreSqlUnitTestBuilders.CreateBatchBuilder(dataSource, "postgresql-terminal-null-writer");

        var exception = Assert.Throws<ArgumentNullException>(() =>
            builder.ToPostgreSqlBinaryCopy(
                dataSource,
                PostgreSqlUnitTestSupport.CopyFromCommand,
                null!,
                new PostgreSqlBinaryCopySinkOptions()));

        Assert.Equal("rowWriter", exception.ParamName);
    }

    [Fact]
    public void ToPostgreSqlBinaryCopy_NullOptions_IsRejected()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();
        var builder = PostgreSqlUnitTestBuilders.CreateBatchBuilder(dataSource, "postgresql-terminal-null-options");

        var exception = Assert.Throws<ArgumentNullException>(() =>
            builder.ToPostgreSqlBinaryCopy(
                dataSource,
                PostgreSqlUnitTestSupport.CopyFromCommand,
                PostgreSqlUnitTestSupport.RowWriter,
                null!));

        Assert.Equal("options", exception.ParamName);
    }

    [Fact]
    public void ToPostgreSqlBinaryCopyStaged_NullBuilder_IsRejected()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            PostgreSqlPipelineDefinitionBuilderExtensions.ToPostgreSqlBinaryCopy<IReadOnlyList<int>, int>(
                null!,
                dataSource,
                PostgreSqlUnitTestSupport.CopyFromCommand,
                PostgreSqlUnitTestSupport.RowWriter,
                new PostgreSqlBinaryCopySinkOptions()));

        Assert.Equal("builder", exception.ParamName);
    }

    [Fact]
    public void ToPostgreSqlBinaryCopyStaged_NullDataSource_IsRejected()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();
        var builder = PostgreSqlUnitTestBuilders.CreateStagedBatchBuilder(dataSource, "postgresql-staged-null-source");

        var exception = Assert.Throws<ArgumentNullException>(() =>
            builder.ToPostgreSqlBinaryCopy<IReadOnlyList<int>, int>(
                null!,
                PostgreSqlUnitTestSupport.CopyFromCommand,
                PostgreSqlUnitTestSupport.RowWriter,
                new PostgreSqlBinaryCopySinkOptions()));

        Assert.Equal("dataSource", exception.ParamName);
    }

    [Fact]
    public void ToPostgreSqlBinaryCopyStaged_BlankCopyFromCommand_IsRejected()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();
        var builder = PostgreSqlUnitTestBuilders.CreateStagedBatchBuilder(dataSource, "postgresql-staged-blank");

        var exception = Assert.Throws<ArgumentException>(() =>
            builder.ToPostgreSqlBinaryCopy<IReadOnlyList<int>, int>(
                dataSource,
                "\t",
                PostgreSqlUnitTestSupport.RowWriter,
                new PostgreSqlBinaryCopySinkOptions()));

        Assert.StartsWith(Internal.PostgreSqlErrorMessages.CopyCommandBlank, exception.Message, StringComparison.Ordinal);
        Assert.Equal("copyFromCommand", exception.ParamName);
    }

    [Fact]
    public void ToPostgreSqlBinaryCopyStaged_NullRowWriter_IsRejected()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();
        var builder = PostgreSqlUnitTestBuilders.CreateStagedBatchBuilder(dataSource, "postgresql-staged-null-writer");

        var exception = Assert.Throws<ArgumentNullException>(() =>
            builder.ToPostgreSqlBinaryCopy<IReadOnlyList<int>, int>(
                dataSource,
                PostgreSqlUnitTestSupport.CopyFromCommand,
                null!,
                new PostgreSqlBinaryCopySinkOptions()));

        Assert.Equal("rowWriter", exception.ParamName);
    }

    [Fact]
    public void ToPostgreSqlBinaryCopyStaged_NullOptions_IsRejected()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();
        var builder = PostgreSqlUnitTestBuilders.CreateStagedBatchBuilder(dataSource, "postgresql-staged-null-options");

        var exception = Assert.Throws<ArgumentNullException>(() =>
            builder.ToPostgreSqlBinaryCopy<IReadOnlyList<int>, int>(
                dataSource,
                PostgreSqlUnitTestSupport.CopyFromCommand,
                PostgreSqlUnitTestSupport.RowWriter,
                null!));

        Assert.Equal("options", exception.ParamName);
    }
}
