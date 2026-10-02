using System.Text.RegularExpressions;

namespace Chronicle.Plugin.LastFM;

internal enum LastFmEntity { Artist, Album, Track }

/// <summary>A parsed Last.fm external id. <see cref="Album"/> on a track is only the album segment
/// the id was minted with; Last.fm itself looks tracks up by artist + title alone.</summary>
internal sealed record LastFmId(LastFmEntity Kind, string Artist, string? Album, string? Track);

/// <summary>
/// Last.fm has no ids of its own: an artist is its name, an album is artist + title, a track is
/// artist + title. External ids are therefore built from percent-encoded names:
/// <c>artist:{name}</c>, <c>album:{artist}/{album}</c>, <c>track:{artist}/{track}</c> or
/// <c>track:{artist}/{album}/{track}</c>.
///
/// A track id carries the album it was matched under whenever that is known. Last.fm resolves
/// "Intro" by an artist to ONE canonical track no matter which album it sits on, so without the
/// album segment two same-named tracks on different albums would mint the identical id and
/// Chronicle would treat them as one item (the same collision class as the Fanart.tv album-id bug).
/// </summary>
internal static class LastFmIds
{
    public static string Artist(string name) => $"artist:{Enc(name)}";

    public static string Album(string artist, string album) => $"album:{Enc(artist)}/{Enc(album)}";

    public static string Track(string artist, string? album, string track) =>
        string.IsNullOrWhiteSpace(album)
            ? $"track:{Enc(artist)}/{Enc(track)}"
            : $"track:{Enc(artist)}/{Enc(album)}/{Enc(track)}";

    /// <summary>
    /// Parses a typed id (<c>artist:…</c>, <c>album:…</c>, <c>track:…</c>) or a last.fm URL
    /// (<c>/music/Artist</c>, <c>/music/Artist/Album</c>, <c>/music/Artist/_/Track</c>).
    /// Returns null when the text is neither.
    /// </summary>
    public static LastFmId? Parse(string externalId)
    {
        if (string.IsNullOrWhiteSpace(externalId)) return null;
        externalId = externalId.Trim();
        return externalId.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? ParseUrl(externalId)
            : ParseTyped(externalId);
    }

    private static LastFmId? ParseTyped(string id)
    {
        var sep = id.IndexOf(':');
        if (sep < 0) return null;
        var kind  = id[..sep].ToLowerInvariant();
        var rest  = id[(sep + 1)..];

        // An artist id is one segment, so an unencoded slash can only be part of the name
        // ("artist:AC/DC", as a user would type it in Fix Match).
        if (kind == "artist")
            return string.IsNullOrWhiteSpace(rest)
                ? null
                : new LastFmId(LastFmEntity.Artist, Uri.UnescapeDataString(rest), null, null);

        var parts = rest.Split('/').Select(Uri.UnescapeDataString).ToArray();
        if (parts.Any(string.IsNullOrWhiteSpace)) return null;

        return kind switch
        {
            "album"  when parts.Length == 2 => new LastFmId(LastFmEntity.Album, parts[0], parts[1], null),
            "track"  when parts.Length == 2 => new LastFmId(LastFmEntity.Track, parts[0], null, parts[1]),
            "track"  when parts.Length == 3 => new LastFmId(LastFmEntity.Track, parts[0], parts[1], parts[2]),
            _ => null,
        };
    }

    private static LastFmId? ParseUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (!uri.Host.Equals("last.fm", StringComparison.OrdinalIgnoreCase)
            && !uri.Host.EndsWith(".last.fm", StringComparison.OrdinalIgnoreCase))
            return null;

        // /music/Pink+Floyd, /music/Pink+Floyd/The+Wall, /music/Pink+Floyd/_/Money
        // (a regional prefix such as /de/music/... is tolerated)
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var at = Array.FindIndex(segments, s => s.Equals("music", StringComparison.OrdinalIgnoreCase));
        if (at < 0) return null;
        var rest = segments.Skip(at + 1).Select(UrlDecode).ToArray();

        return rest.Length switch
        {
            1 => new LastFmId(LastFmEntity.Artist, rest[0], null, null),
            2 => new LastFmId(LastFmEntity.Album, rest[0], rest[1], null),
            3 when rest[1] == "_" => new LastFmId(LastFmEntity.Track, rest[0], null, rest[2]),
            _ => null,
        };
    }

    // last.fm URLs encode a space as '+', and a literal '+' as %2B.
    private static string UrlDecode(string segment) =>
        Uri.UnescapeDataString(segment.Replace("+", " "));

    private static string Enc(string s) => Uri.EscapeDataString(s);

    private static readonly Regex MbidRe =
        new(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The MusicBrainz UUID inside a stored id ("artist:{uuid}", "recording:{uuid}" or bare),
    /// provided its type prefix (if any) is <paramref name="requiredPrefix"/>; otherwise null.</summary>
    public static string? ExtractMbid(string? raw, string requiredPrefix)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (raw.Contains(':') && !raw.StartsWith(requiredPrefix + ":", StringComparison.OrdinalIgnoreCase))
            return null;
        var m = MbidRe.Match(raw);
        return m.Success ? m.Value : null;
    }
}
