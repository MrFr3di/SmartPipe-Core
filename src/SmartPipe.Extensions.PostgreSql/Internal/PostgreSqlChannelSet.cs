namespace SmartPipe.Extensions.PostgreSql.Internal;

/// <summary>
/// Validated, defensively copied and ordinal-deduplicated LISTEN channel set. Identifiers are handed to PostgreSQL
/// exactly as supplied: no trimming, no case folding and no other normalization, because PostgreSQL owns identifier
/// interpretation.
/// </summary>
internal sealed class PostgreSqlChannelSet
{
    private readonly System.Collections.ObjectModel.ReadOnlyCollection<string> _channels;

    private PostgreSqlChannelSet(string[] channels) => _channels = Array.AsReadOnly(channels);

    internal IReadOnlyList<string> Channels => _channels;

    internal static PostgreSqlChannelSet Create(IReadOnlyCollection<string>? channels)
    {
        ArgumentNullException.ThrowIfNull(channels, nameof(channels));
        if (channels.Count == 0)
            throw new ArgumentException(PostgreSqlErrorMessages.ChannelsEmpty, nameof(channels));

        var copy = new string[channels.Count];
        var index = 0;
        foreach (var channel in channels)
        {
            if (string.IsNullOrWhiteSpace(channel))
            {
                throw new ArgumentException(
                    PostgreSqlErrorMessages.ChannelEntryBlank,
                    $"{nameof(channels)}[{index}]");
            }

            copy[index] = channel;
            index++;
        }

        for (var i = 0; i < copy.Length; i++)
        {
            for (var j = i + 1; j < copy.Length; j++)
            {
                if (string.Equals(copy[i], copy[j], StringComparison.Ordinal))
                    throw new ArgumentException(PostgreSqlErrorMessages.ChannelDuplicate, nameof(channels));
            }
        }

        return new PostgreSqlChannelSet(copy);
    }
}
