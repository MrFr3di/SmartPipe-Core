using SmartPipe.Extensions.PostgreSql.Internal;

namespace SmartPipe.Extensions.PostgreSql.Tests.Unit;

/// <summary>Validates the defensively copied, ordinally deduplicated LISTEN channel set.</summary>
public sealed class PostgreSqlChannelSetTests
{
    [Fact]
    public void Create_SingleChannel_IsAcceptedInOrder()
    {
        var set = PostgreSqlChannelSet.Create(["smoke"]);

        Assert.Equal(new[] { "smoke" }, set.Channels);
    }

    [Fact]
    public void Create_NullCollection_Throws()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => PostgreSqlChannelSet.Create(null));

        Assert.Equal("channels", exception.ParamName);
    }

    [Fact]
    public void Create_EmptyArray_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(() => PostgreSqlChannelSet.Create(Array.Empty<string>()));

        Assert.StartsWith(PostgreSqlErrorMessages.ChannelsEmpty, exception.Message, StringComparison.Ordinal);
        Assert.Equal("channels", exception.ParamName);
    }

    [Fact]
    public void Create_EmptyList_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(() => PostgreSqlChannelSet.Create(new List<string>()));

        Assert.StartsWith(PostgreSqlErrorMessages.ChannelsEmpty, exception.Message, StringComparison.Ordinal);
        Assert.Equal("channels", exception.ParamName);
    }

    [Fact]
    public void Create_NullEntry_NamesTheOffendingIndex()
    {
        var exception = Assert.Throws<ArgumentException>(() => PostgreSqlChannelSet.Create(new string[] { null! }));

        Assert.StartsWith(PostgreSqlErrorMessages.ChannelEntryBlank, exception.Message, StringComparison.Ordinal);
        Assert.Equal("channels[0]", exception.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\r\n")]
    public void Create_BlankEntry_NamesTheOffendingIndex(string entry)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => PostgreSqlChannelSet.Create(new[] { "first", entry, "third" }));

        Assert.StartsWith(PostgreSqlErrorMessages.ChannelEntryBlank, exception.Message, StringComparison.Ordinal);
        Assert.Equal("channels[1]", exception.ParamName);
    }

    [Fact]
    public void Create_BlankEntriesOnly_ReportsTheFirstIndex()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => PostgreSqlChannelSet.Create(new[] { " ", "  " }));

        Assert.StartsWith(PostgreSqlErrorMessages.ChannelEntryBlank, exception.Message, StringComparison.Ordinal);
        Assert.Equal("channels[0]", exception.ParamName);
    }

    [Fact]
    public void Create_ExactOrdinalDuplicate_IsRejected()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => PostgreSqlChannelSet.Create(new[] { "orders", "payments", "orders" }));

        Assert.StartsWith(PostgreSqlErrorMessages.ChannelDuplicate, exception.Message, StringComparison.Ordinal);
        Assert.Equal("channels", exception.ParamName);
    }

    [Fact]
    public void Create_AdjacentExactDuplicate_IsRejected()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => PostgreSqlChannelSet.Create(new[] { "orders", "orders" }));

        Assert.StartsWith(PostgreSqlErrorMessages.ChannelDuplicate, exception.Message, StringComparison.Ordinal);
        Assert.Equal("channels", exception.ParamName);
    }

    [Fact]
    public void Create_CaseVariantIsNotAFoldAndIsAccepted()
    {
        var set = PostgreSqlChannelSet.Create(new[] { "a", "A" });

        // PostgreSQL owns identifier interpretation: no case folding and no collapse happens here.
        Assert.Equal(new[] { "a", "A" }, set.Channels);
    }

    [Fact]
    public void Create_NormalizationVariantsAreNotCollapsed()
    {
        var composed = "\u00e9";
        var decomposed = "e\u0301";

        var set = PostgreSqlChannelSet.Create(new[] { composed, decomposed });

        // Ordinal comparison only: canonically equivalent identifiers stay distinct.
        Assert.Equal(new[] { composed, decomposed }, set.Channels);
    }

    [Fact]
    public void Create_WhitespaceIsNotTrimmed()
    {
        var set = PostgreSqlChannelSet.Create(new[] { " a ", "b" });

        Assert.Equal(new[] { " a ", "b" }, set.Channels);
    }

    [Fact]
    public void Create_NonAsciiIdentifiers_ArePreservedExactly()
    {
        var set = PostgreSqlChannelSet.Create(new[] { "Канал", "orders" });

        Assert.Equal(new[] { "Канал", "orders" }, set.Channels);
    }

    [Fact]
    public void Create_CopiesTheCallersCollectionDefensively()
    {
        var channels = new List<string> { "first", "second" };

        var set = PostgreSqlChannelSet.Create(channels);

        channels[0] = "mutated";
        channels.Add("third");
        channels.Clear();
        channels.Add("replaced");

        var afterMutation = PostgreSqlChannelSet.Create(channels);

        Assert.Equal(new[] { "first", "second" }, set.Channels);
        Assert.Equal(new[] { "replaced" }, afterMutation.Channels);
    }

    [Fact]
    public void Channels_CannotBeMutatedThroughTheReadOnlyContract()
    {
        var set = PostgreSqlChannelSet.Create(new[] { "first", "second" });

        var list = Assert.IsAssignableFrom<IList<string>>(set.Channels);

        Assert.Throws<NotSupportedException>(() => list.Add("third"));
        Assert.Equal(new[] { "first", "second" }, set.Channels);
    }

    [Fact]
    public void Create_PreservesTheCallersOrder()
    {
        var set = PostgreSqlChannelSet.Create(new[] { "delta", "alpha", "charlie", "bravo" });

        Assert.Equal(new[] { "delta", "alpha", "charlie", "bravo" }, set.Channels);
    }
}
