using SmartPipe.Extensions.PostgreSql.Internal;

namespace SmartPipe.Extensions.PostgreSql.Tests.Unit;

/// <summary>Validates <see cref="PostgreSqlBinaryCopySourceOptions"/> and its snapshot.</summary>
public sealed class PostgreSqlBinaryCopySourceOptionsTests
{
    private const string OperationNameParameter = "options";
    private const string ExpectedColumnCountParameter = "options";

    [Fact]
    public void Defaults_AreTheFrozenPublicDefaults()
    {
        var options = new PostgreSqlBinaryCopySourceOptions();

        Assert.Equal("postgresql-copy-out", options.OperationName);
        Assert.Null(options.ExpectedColumnCount);

        var snapshot = PostgreSqlBinaryCopySourceOptionsSnapshot.Create(options);

        Assert.Equal("postgresql-copy-out", snapshot.OperationName);
        Assert.Null(snapshot.ExpectedColumnCount);
    }

    [Fact]
    public void Create_NullOptions_Throws()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => PostgreSqlBinaryCopySourceOptionsSnapshot.Create(null!));

        Assert.Equal("options", exception.ParamName);
    }

    [Fact]
    public void Create_NullOperationName_Throws()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            PostgreSqlBinaryCopySourceOptionsSnapshot.Create(
                new PostgreSqlBinaryCopySourceOptions { OperationName = null! }));

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
            PostgreSqlBinaryCopySourceOptionsSnapshot.Create(
                new PostgreSqlBinaryCopySourceOptions { OperationName = operationName }));

        Assert.StartsWith(PostgreSqlErrorMessages.OperationNameBlank, exception.Message, StringComparison.Ordinal);
        Assert.Equal(OperationNameParameter, exception.ParamName);
    }

    [Fact]
    public void Create_OperationNameOfExactly64Characters_IsAccepted()
    {
        var operationName = new string('x', 64);

        var snapshot = PostgreSqlBinaryCopySourceOptionsSnapshot.Create(
            new PostgreSqlBinaryCopySourceOptions { OperationName = operationName });

        Assert.Equal(operationName, snapshot.OperationName);
        Assert.Equal(64, snapshot.OperationName.Length);
    }

    [Fact]
    public void Create_OperationNameOf65Characters_IsRejected()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            PostgreSqlBinaryCopySourceOptionsSnapshot.Create(
                new PostgreSqlBinaryCopySourceOptions { OperationName = new string('x', 65) }));

        Assert.StartsWith(PostgreSqlErrorMessages.OperationNameTooLong, exception.Message, StringComparison.Ordinal);
        Assert.Equal(OperationNameParameter, exception.ParamName);
    }

    [Fact]
    public void Create_OperationNameIsPreservedVerbatim()
    {
        var snapshot = PostgreSqlBinaryCopySourceOptionsSnapshot.Create(
            new PostgreSqlBinaryCopySourceOptions { OperationName = " Copy-Out " });

        Assert.Equal(" Copy-Out ", snapshot.OperationName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Create_NonPositiveExpectedColumnCount_IsRejected(int expectedColumnCount)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            PostgreSqlBinaryCopySourceOptionsSnapshot.Create(
                new PostgreSqlBinaryCopySourceOptions { ExpectedColumnCount = expectedColumnCount }));

        Assert.Equal(ExpectedColumnCountParameter, exception.ParamName);
        Assert.StartsWith(PostgreSqlErrorMessages.ExpectedColumnCountPositive, exception.Message, StringComparison.Ordinal);
        Assert.Equal(expectedColumnCount, Assert.IsType<int>(exception.ActualValue));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(int.MaxValue)]
    public void Create_PositiveExpectedColumnCount_IsAccepted(int expectedColumnCount)
    {
        var snapshot = PostgreSqlBinaryCopySourceOptionsSnapshot.Create(
            new PostgreSqlBinaryCopySourceOptions { ExpectedColumnCount = expectedColumnCount });

        Assert.Equal(expectedColumnCount, snapshot.ExpectedColumnCount);
    }

    [Fact]
    public void Create_NullExpectedColumnCount_IsAccepted()
    {
        var snapshot = PostgreSqlBinaryCopySourceOptionsSnapshot.Create(
            new PostgreSqlBinaryCopySourceOptions { ExpectedColumnCount = null });

        Assert.Null(snapshot.ExpectedColumnCount);
    }

    [Fact]
    public void Create_ValidOptions_AreSnapshottedWithoutAliasing()
    {
        var options = new PostgreSqlBinaryCopySourceOptions
        {
            OperationName = "copy-out-snapshot",
            ExpectedColumnCount = 3,
        };

        var snapshot = PostgreSqlBinaryCopySourceOptionsSnapshot.Create(options);
        var derived = options with { OperationName = "derived", ExpectedColumnCount = 9 };

        Assert.Equal("copy-out-snapshot", snapshot.OperationName);
        Assert.Equal(3, snapshot.ExpectedColumnCount);
        Assert.Equal("derived", derived.OperationName);
        Assert.Equal(9, derived.ExpectedColumnCount);
    }
}
