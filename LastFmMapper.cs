using System.Text.Json;
using Chronicle.Plugins.Models;
using J = Chronicle.Plugin.LastFM.LastFmJson;

namespace Chronicle.Plugin.LastFM;

/// <summary>
/// Maps Last.fm payloads onto <see cref="MediaMetadata"/>. Everything Last.fm supplies that has no
/// first-class field (stats, similar artists, track listings, full wiki text, MBIDs) goes into
/// <see cref="MediaMetadata.ExtendedData"/> so nothing is dropped.
/// </summary>
internal static class LastFmMapper
{
    public const string Source = "lastfm";

    // ── Full lookups (artist/album/track.getInfo) ─────────────────────────────

    public static MediaMetadata MapArtist(JsonElement artist)
    {
        var name = J.Str(artist, "name") ?? string.Empty;
        var bio  = J.Obj(artist, "bio");

        var similar = J.List(J.Obj(artist, "similar"), "artist")
            .Select(a => new { name = J.Str(a, "name"), url = J.Str(a, "url") })
            .Where(a => a.name is not null)
            .ToList();

        return new MediaMetadata
        {
            ExternalId = LastFmIds.Artist(name),
            Source     = Source,
            Title      = name,
            Overview   = J.CleanWiki(J.Str(bio, "summary")),
            // Last.fm no longer serves real artist pictures (every one is the grey-star placeholder);
            // BestImage filters it, so this is only ever set if that changes.
            PosterUrl  = J.BestImage(artist),
            Tags       = J.TagNames(J.Obj(artist, "tags")),
            ExtendedData = Extended(new Dictionary<string, object?>
            {
                ["name"]          = name,
                ["url"]           = J.Str(artist, "url"),
                ["mbid"]          = J.Str(artist, "mbid"),
                ["listeners"]     = J.Long(J.Obj(artist, "stats"), "listeners"),
                ["playcount"]     = J.Long(J.Obj(artist, "stats"), "playcount"),
                ["on_tour"]       = J.Str(artist, "ontour") == "1",
                ["similar_artists"] = similar,
                ["bio_published"] = J.Str(bio, "published"),
                ["bio_content"]   = J.CleanWiki(J.Str(bio, "content")),
            }),
        };
    }

    public static MediaMetadata MapAlbum(JsonElement album)
    {
        var title  = J.Str(album, "name") ?? string.Empty;
        var artist = ArtistName(album) ?? string.Empty;
        var wiki   = J.Obj(album, "wiki");

        var tracks = J.List(J.Obj(album, "tracks"), "track")
            .Select(t => new
            {
                rank             = J.Long(J.Obj(t, "@attr"), "rank"),
                name             = J.Str(t, "name"),
                duration_seconds = J.Long(t, "duration"),
                url              = J.Str(t, "url"),
                artist           = ArtistName(t),
            })
            .ToList();

        return new MediaMetadata
        {
            ExternalId = LastFmIds.Album(artist, title),
            Source     = Source,
            Title      = title,
            Overview   = J.CleanWiki(J.Str(wiki, "summary")),
            PosterUrl  = J.BestImage(album),
            Tags       = J.TagNames(J.Obj(album, "tags")),
            ExtendedData = Extended(new Dictionary<string, object?>
            {
                ["name"]           = title,
                ["artist"]         = artist,
                ["url"]            = J.Str(album, "url"),
                ["mbid"]           = J.Str(album, "mbid"),
                ["listeners"]      = J.Long(album, "listeners"),
                ["playcount"]      = J.Long(album, "playcount"),
                ["tracks"]         = tracks,
                ["wiki_published"] = J.Str(wiki, "published"),
                ["wiki_content"]   = J.CleanWiki(J.Str(wiki, "content")),
            }),
        };
    }

    /// <param name="albumForId">The album the Chronicle item sits on, when known. It is baked into
    /// the external id (see <see cref="LastFmIds"/>) so same-titled tracks on different albums stay
    /// distinct. Falls back to the album Last.fm itself reports for the track.</param>
    public static MediaMetadata MapTrack(JsonElement track, string? albumForId = null)
    {
        var title    = J.Str(track, "name") ?? string.Empty;
        var artistEl = J.Obj(track, "artist");
        var artist   = J.Str(artistEl, "name") ?? J.Str(track, "artist") ?? string.Empty;
        var album    = J.Obj(track, "album");
        var wiki     = J.Obj(track, "wiki");

        var durationMs = J.Long(track, "duration");

        return new MediaMetadata
        {
            ExternalId     = LastFmIds.Track(artist, albumForId ?? J.Str(album, "title"), title),
            Source         = Source,
            Title          = title,
            Overview       = J.CleanWiki(J.Str(wiki, "summary")),
            PosterUrl      = J.BestImage(album),
            // At least 1: a sub-30-second interlude must not round to a 0-minute runtime.
            RuntimeMinutes = durationMs is > 0 ? Math.Max(1, (int)Math.Round(durationMs.Value / 60000.0)) : null,
            Tags           = J.TagNames(J.Obj(track, "toptags")),
            ExtendedData   = Extended(new Dictionary<string, object?>
            {
                ["name"]             = title,
                ["artist"]           = artist,
                ["artist_mbid"]      = J.Str(artistEl, "mbid"),
                ["url"]              = J.Str(track, "url"),
                ["mbid"]             = J.Str(track, "mbid"),
                ["duration_seconds"] = durationMs is > 0 ? durationMs.Value / 1000 : null,
                ["listeners"]        = J.Long(track, "listeners"),
                ["playcount"]        = J.Long(track, "playcount"),
                ["album_title"]      = J.Str(album, "title"),
                ["album_mbid"]       = J.Str(album, "mbid"),
                ["album_position"]   = J.Long(J.Obj(album, "@attr"), "position"),
                ["wiki_published"]   = J.Str(wiki, "published"),
                ["wiki_content"]     = J.CleanWiki(J.Str(wiki, "content")),
            }),
        };
    }

    // ── Search results (artist/album/track.search): thin stubs ────────────────

    public static MediaMetadata MapArtistStub(JsonElement hit)
    {
        var name = J.Str(hit, "name") ?? string.Empty;
        return new MediaMetadata
        {
            ExternalId   = LastFmIds.Artist(name),
            Source       = Source,
            Title        = name,
            PosterUrl    = J.BestImage(hit),
            ExtendedData = Extended(new Dictionary<string, object?>
            {
                ["name"] = name, ["url"] = J.Str(hit, "url"), ["mbid"] = J.Str(hit, "mbid"),
                ["listeners"] = J.Long(hit, "listeners"),
            }),
        };
    }

    public static MediaMetadata MapAlbumStub(JsonElement hit)
    {
        var title  = J.Str(hit, "name") ?? string.Empty;
        var artist = ArtistName(hit) ?? string.Empty;
        return new MediaMetadata
        {
            ExternalId   = LastFmIds.Album(artist, title),
            Source       = Source,
            Title        = title,
            PosterUrl    = J.BestImage(hit),
            ExtendedData = Extended(new Dictionary<string, object?>
            {
                ["name"] = title, ["artist"] = artist, ["url"] = J.Str(hit, "url"), ["mbid"] = J.Str(hit, "mbid"),
            }),
        };
    }

    public static MediaMetadata MapTrackStub(JsonElement hit, string? albumForId)
    {
        var title  = J.Str(hit, "name") ?? string.Empty;
        var artist = ArtistName(hit) ?? string.Empty;
        return new MediaMetadata
        {
            ExternalId   = LastFmIds.Track(artist, albumForId, title),
            Source       = Source,
            Title        = title,
            PosterUrl    = J.BestImage(hit),
            ExtendedData = Extended(new Dictionary<string, object?>
            {
                ["name"] = title, ["artist"] = artist, ["url"] = J.Str(hit, "url"), ["mbid"] = J.Str(hit, "mbid"),
                ["listeners"] = J.Long(hit, "listeners"),
            }),
        };
    }

    /// <summary>The artist credited on an album/track node, which Last.fm gives either as a plain
    /// string (search hits, album.getInfo) or as an object with a "name" (track.getInfo, tracklists).</summary>
    public static string? ArtistName(JsonElement node)
    {
        if (!node.TryGetProperty("artist", out var a)) return null;
        return a.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(a.GetString()) ? null : a.GetString()!.Trim(),
            JsonValueKind.Object => J.Str(a, "name"),
            _ => null,
        };
    }

    private static JsonElement Extended(Dictionary<string, object?> values) =>
        JsonSerializer.SerializeToElement(values);
}
