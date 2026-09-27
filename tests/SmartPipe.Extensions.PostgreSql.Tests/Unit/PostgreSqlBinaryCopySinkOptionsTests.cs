using SmartPipe.Extensions.PostgreSql.Internal;

namespace SmartPipe.Extensions.PostgreSql.Tests.Unit;

/// <summary>Validates <see cref="PostgreSqlBinaryCopySinkOptions"/> and its snapshot.</summary>
public sealed class PostgreSqlBinaryCopySinkOptionsTests
{
    private const string OperationNameParameter = "options.OperationName";
    private const string MaxRowsPerBatchParameter = "options.MaxRowsPerBatch";

    [Fact]
    public void Defaults_AreTheFrozenPublicDefaults()
    {
        var options = new PostgreSqlBinaryCopySinkOptions();

        Assert.Equal("postgresql-copy-in", options.OperationName);
        Assert.Equal(10_000, options.MaxRowsPerBatch);

        var snapshot = PostgreSqlBinaryCopySinkOptionsSnapshot.Create(options);

        Assert.Equal("postgresql-copy-in", snapshot.OperationName);
        Assert.Equal(10_000, snapshot.MaxRowsPerBatch);
    }

    [Fact]
    public void Create_NullOptions_Throws()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => PostgreSqlBinaryCopySinkOptionsSnapshot.Create(null!));

        Assert.Equal("options", exception.ParamName);
    }

    [Fact]
    public void Create_NullOperationName_Throws()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            PostgreSqlBinaryCopySinkOptionsSnapshot.Create(
                new PostgreSqlBinaryCopySinkOptions { OperationName = null! }));

        Assert.Equal(OperationNameParameter, exception.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\r\n")]
    public void Create_BlankOperationName_Throws(string operationName)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            PostgreSqlBinaryCopySinkOptionsSnapshot.Create(
                new PostgreSqlBinaryCopySinkOptions { OperationName = operationName }));

        Assert.StartsWith(PostgreSqlErrorMessages.OperationNameBlank, exception.Message, StringComparison.Ordinal);
        Assert.Equal(OperationNameParameter, exception.ParamName);
    }

    [Fact]
    public void Create_OperationNameOfExactly64Characters_IsAccepted()
    {
        var operationName = new string('y', 64);

        var snapshot = PostgreSqlBinaryCopySinkOptionsSnapshot.Create(
            new PostgreSqlBinaryCopySinkOptions { OperationName = operationName });

        Assert.Equal(operationName, snapshot.OperationName);
    }

    [Fact]
    public void Create_OperationNameOf65Characters_IsRejected()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            PostgreSqlBinaryCopySinkOptionsSnapshot.Create(
                new PostgreSqlBinaryCopySinkOptions { OperationName = new string('y', 65) }));

        Assert.StartsWith(PostgreSqlErrorMessages.OperationNameTooLong, exception.Message, StringComparison.Ordinal);
        Assert.Equal(OperationNameParameter, exception.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Create_NonPositiveMaxRowsPerBatch_IsRejected(int maxRowsPerBatch)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            PostgreSqlBinaryCopySinkOptionsSnapshot.Create(
                new PostgreSqlBinaryCopySinkOptions { MaxRowsPerBatch = maxRowsPerBatch }));

        Assert.Equal(MaxRowsPerBatchParameter, exception.ParamName);
        Assert.StartsWith(PostgreSqlErrorMessages.MaxRowsPerBatchPositive, exception.Message, StringComparison.Ordinal);
        Assert.Equal(maxRowsPerBatch, Assert.IsType<int>(exception.ActualValue));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(int.MaxValue)]
    public void Create_PositiveMaxRowsPerBatch_IsAccepted(int maxRowsPerBatch)
    {
        var snapshot = PostgreSqlBinaryCopySinkOptionsSnapshot.Create(
            new PostgreSqlBinaryCopySinkOptions { MaxRowsPerBatch = maxRowsPerBatch });

        Assert.Equal(maxRowsPerBatch, snapshot.MaxRowsPerBatch);
    }

    [Fact]
    public void Create_ValidOptions_AreSnapshottedWithoutAliasing()
    {
        var options = new PostgreSqlBinaryCopySinkOptions
        {
            OperationName = "copy-in-snapshot",
            MaxRowsPerBatch = 5,
        };

        var snapshot = PostgreSqlBinaryCopySinkOptionsSnapshot.Create(options);
        var derived = options with { OperationName = "derived", MaxRowsPerBatch = 6 };

        Assert.Equal("copy-in-snapshot", snapshot.OperationName);
        Assert.Equal(5, snapshot.MaxRowsPerBatch);
        Assert.Equal(6, derived.MaxRowsPerBatch);
    }
}
