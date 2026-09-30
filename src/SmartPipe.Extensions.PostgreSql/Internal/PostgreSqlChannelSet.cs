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

        // Validate every blank entry first to preserve error precedence over duplicates.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var channel in copy)
        {
            if (!seen.Add(channel))
                throw new ArgumentException(PostgreSqlErrorMessages.ChannelDuplicate, nameof(channels));
        }

        return new PostgreSqlChannelSet(copy);
    }
}
