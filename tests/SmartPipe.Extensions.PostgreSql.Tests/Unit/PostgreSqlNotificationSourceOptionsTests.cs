using SmartPipe.Extensions.PostgreSql.Internal;

namespace SmartPipe.Extensions.PostgreSql.Tests.Unit;

/// <summary>Validates <see cref="PostgreSqlNotificationSourceOptions"/> and its snapshot.</summary>
public sealed class PostgreSqlNotificationSourceOptionsTests
{
    private const string OperationNameParameter = "options";
    private const string BufferCapacityParameter = "options";

    [Fact]
    public void Defaults_AreTheFrozenPublicDefaults()
    {
        var options = new PostgreSqlNotificationSourceOptions();

        Assert.Equal("postgresql-listen", options.OperationName);
        Assert.Equal(256, options.BufferCapacity);

        var snapshot = PostgreSqlNotificationSourceOptionsSnapshot.Create(options);

        Assert.Equal("postgresql-listen", snapshot.OperationName);
        Assert.Equal(256, snapshot.BufferCapacity);
    }

    [Fact]
    public void Create_NullOptions_Throws()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => PostgreSqlNotificationSourceOptionsSnapshot.Create(null!));

        Assert.Equal("options", exception.ParamName);
    }

    [Fact]
    public void Create_NullOperationName_Throws()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            PostgreSqlNotificationSourceOptionsSnapshot.Create(
                new PostgreSqlNotificationSourceOptions { OperationName = null! }));

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
            PostgreSqlNotificationSourceOptionsSnapshot.Create(
                new PostgreSqlNotificationSourceOptions { OperationName = operationName }));

        Assert.StartsWith(PostgreSqlErrorMessages.OperationNameBlank, exception.Message, StringComparison.Ordinal);
        Assert.Equal(OperationNameParameter, exception.ParamName);
    }

    [Fact]
    public void Create_OperationNameOfExactly64Characters_IsAccepted()
    {
        var operationName = new string('z', 64);

        var snapshot = PostgreSqlNotificationSourceOptionsSnapshot.Create(
            new PostgreSqlNotificationSourceOptions { OperationName = operationName });

        Assert.Equal(operationName, snapshot.OperationName);
    }

    [Fact]
    public void Create_OperationNameOf65Characters_IsRejected()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            PostgreSqlNotificationSourceOptionsSnapshot.Create(
                new PostgreSqlNotificationSourceOptions { OperationName = new string('z', 65) }));

        Assert.StartsWith(PostgreSqlErrorMessages.OperationNameTooLong, exception.Message, StringComparison.Ordinal);
        Assert.Equal(OperationNameParameter, exception.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Create_NonPositiveBufferCapacity_IsRejected(int bufferCapacity)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            PostgreSqlNotificationSourceOptionsSnapshot.Create(
                new PostgreSqlNotificationSourceOptions { BufferCapacity = bufferCapacity }));

        Assert.Equal(BufferCapacityParameter, exception.ParamName);
        Assert.StartsWith(PostgreSqlErrorMessages.BufferCapacityPositive, exception.Message, StringComparison.Ordinal);
        Assert.Equal(bufferCapacity, Assert.IsType<int>(exception.ActualValue));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(int.MaxValue)]
    public void Create_PositiveBufferCapacity_IsAccepted(int bufferCapacity)
    {
        var snapshot = PostgreSqlNotificationSourceOptionsSnapshot.Create(
            new PostgreSqlNotificationSourceOptions { BufferCapacity = bufferCapacity });

        Assert.Equal(bufferCapacity, snapshot.BufferCapacity);
    }

    [Fact]
    public void Create_ValidOptions_AreSnapshottedWithoutAliasing()
    {
        var options = new PostgreSqlNotificationSourceOptions
        {
            OperationName = "listen-snapshot",
            BufferCapacity = 3,
        };

        var snapshot = PostgreSqlNotificationSourceOptionsSnapshot.Create(options);
        var derived = options with { OperationName = "derived", BufferCapacity = 4 };

        Assert.Equal("listen-snapshot", snapshot.OperationName);
        Assert.Equal(3, snapshot.BufferCapacity);
        Assert.Equal(4, derived.BufferCapacity);
    }
}
