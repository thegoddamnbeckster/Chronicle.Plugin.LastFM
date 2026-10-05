using System.Text.Json;
using System.Text.RegularExpressions;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using J = Chronicle.Plugin.LastFM.LastFmJson;

namespace Chronicle.Plugin.LastFM;

/// <summary>
/// Chronicle metadata provider for Last.fm (music: Artist → Album → Track).
///
/// Last.fm is a supplementary source: MusicBrainz stays the primary identity for music, and Last.fm
/// adds artist biographies, community tags, listener/play counts, similar artists and (for albums)
/// cover art. Resolution order per item:
///   1. a MusicBrainz id Chronicle already knows (artist → artist mbid, track → recording mbid)
///   2. an exact artist/album/track lookup by name, using the parent names from the search context
///   3. Last.fm's own search, returned as scored stubs
/// Albums are never looked up by MusicBrainz id: Last.fm's album mbid is a RELEASE id, while
/// Chronicle stores release-GROUP ids, so passing one would simply miss.
/// </summary>
public sealed class LastFmMetadataProvider : IMetadataProvider, IDisposable
{
    // ── IMetadataProvider identity ────────────────────────────────────────────

    public string PluginId => LastFmClient.PluginId;
    public string Name     => "Last.fm";
    public string Version  => "1.1.0";
    public string Author   => "Chronicle Contributors";

    // ── Settings keys ─────────────────────────────────────────────────────────

    private const string KeyApiKey   = "api_key";
    private const string KeyLanguage = "language";

    private const string UserAgent = "Chronicle/1.0 (https://github.com/thegoddamnbeckster/Chronicle)";

    // ── Live state ────────────────────────────────────────────────────────────

    private LastFmClient? _client;
    private string _language = "en";

    // Short-lived cache: the enrichment service calls GetByIdAsync right after SearchAsync for the
    // same id, and an exact getInfo hit in SearchAsync already holds the full payload.
    private static readonly long CacheTtlTicks = 30L * TimeSpan.TicksPerSecond;
    private sealed record CacheEntry(string Id, MediaMetadata Result, long ExpiresAtTicks);
    private volatile CacheEntry? _cache;

    /// <summary>Chronicle instantiates plugins via <c>Activator.CreateInstance</c>, so a public
    /// parameterless constructor is required. Configuration arrives via <see cref="Configure"/>.</summary>
    public LastFmMetadataProvider() { }

    /// <summary>Test-only: inject a pre-built client instead of going through Configure().</summary>
    internal LastFmMetadataProvider(LastFmClient client, string language = "en")
    {
        _client   = client;
        _language = language;
    }

    // ── IMetadataProvider: static declarations ────────────────────────────────

    public MediaTypeSupport[] GetSupportedMediaTypes() =>
    [
        new MediaTypeSupport
        {
            MediaTypeName   = "music",
            DisplayName     = "Music",
            HierarchyLevels = 3,
            HierarchyLabels = ["Artist", "Album", "Track"],
            InteractionVerb = "listened",
            // Supplements MusicBrainz (priority 10), which keeps titles and identity.
            DefaultPriority = 20,
            SupportedFields = ["overview", "tags"],
            LevelFields = new Dictionary<int, List<string>>
            {
                [1] = ["overview", "poster_url", "tags"],
                [2] = ["overview", "runtime_minutes", "poster_url", "tags"],
            },
        },
    ];

    public PluginSettingsSchema GetSettingsSchema() => new()
    {
        Settings =
        [
            new SettingDefinition
            {
                Key         = KeyApiKey,
                Label       = "Last.fm API Key",
                Description = "Your API key from https://www.last.fm/api/account/create (free). Stored encrypted.",
                Type        = SettingType.Password,
                Required    = true,
            },
            new SettingDefinition
            {
                Key          = KeyLanguage,
                Label        = "Biography Language",
                Description  = "ISO 639-1 code (e.g. en, de, fr) for artist and album write-ups. " +
                               "Falls back to English where Last.fm has no translation.",
                Type         = SettingType.Text,
                Required     = false,
                DefaultValue = "en",
            },
        ],
    };

    // ── IMetadataProvider: configuration ─────────────────────────────────────

    public void Configure(IReadOnlyDictionary<string, string> settings)
    {
        settings.TryGetValue(KeyApiKey,   out var apiKey);
        settings.TryGetValue(KeyLanguage, out var language);

        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Last.fm plugin requires 'api_key' to be configured.");

        _client?.Dispose();
        _client   = new LastFmClient(apiKey.Trim(), UserAgent);
        _language = string.IsNullOrWhiteSpace(language) ? "en" : language.Trim();
        _cache    = null;
    }

    // ── IMetadataProvider: search ─────────────────────────────────────────────

    private sealed record Candidate(MediaMetadata Meta, string? Artist, string? Album, int Bonus = 0, string? BonusReason = null);

    public async Task<IReadOnlyList<ScoredCandidate>> SearchAsync(
        MediaSearchContext context, CancellationToken ct = default)
    {
        EnsureConfigured();

        // Last.fm only carries music. A null type means the caller didn't say; treat it as music
        // (this plugin declares nothing else, so nothing else can be routed here legitimately).
        if (context.MediaTypeName is not null
            && !string.Equals(context.MediaTypeName, "music", StringComparison.OrdinalIgnoreCase))
            return [];

        var titles = (context.AltTitles?.Count > 0 ? context.AltTitles : [context.Name])
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (titles.Count == 0) return [];

        var candidates = context.HierarchyLevel switch
        {
            0 => await FindArtistsAsync(context, titles, ct),
            1 => await FindAlbumsAsync(context, titles, ct),
            _ => await FindTracksAsync(context, titles, ct),
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return candidates
            .Where(c => !string.IsNullOrEmpty(c.Meta.ExternalId) && !string.IsNullOrEmpty(c.Meta.Title))
            .Select(c => Score(context, c))
            .Where(c => seen.Add(c.Metadata.ExternalId))
            .OrderByDescending(c => c.Score)
            .Take(10)
            .ToList();
    }

    private async Task<List<Candidate>> FindArtistsAsync(
        MediaSearchContext ctx, List<string> titles, CancellationToken ct)
    {
        var results = new List<Candidate>();

        var mbid = LastFmIds.ExtractMbid(ctx.KnownExternalIds?.GetValueOrDefault("musicbrainz"), "artist");
        if (mbid is not null && await GetArtistInfoAsync(null, mbid, ct) is { } byMbid)
        {
            var meta = LastFmMapper.MapArtist(byMbid);
            Remember(meta);
            return [new Candidate(meta, null, null, 100, "musicbrainz id match")];
        }

        foreach (var title in titles)
        {
            if (await GetArtistInfoAsync(title, null, ct) is not { } hit) continue;
            var meta = LastFmMapper.MapArtist(hit);
            Remember(meta);
            results.Add(new Candidate(meta, null, null));
            break;
        }

        if (results.Count == 0)
        {
            var root = await _client!.GetAsync("artist.search",
                P(("artist", titles[0]), ("limit", "10")), ct);
            var hits = J.List(J.Obj(J.Obj(root, "results"), "artistmatches"), "artist");
            results.AddRange(hits.Select(h => new Candidate(LastFmMapper.MapArtistStub(h), null, null)));
        }

        // An open "Add Media" search (no hierarchy position) also surfaces albums, like MusicBrainz does.
        if (!ctx.IsRealHierarchyPosition && ctx.ParentName is null)
        {
            var root = await _client!.GetAsync("album.search",
                P(("album", titles[0]), ("limit", "10")), ct);
            var hits = J.List(J.Obj(J.Obj(root, "results"), "albummatches"), "album");
            results.AddRange(hits.Select(h =>
                new Candidate(LastFmMapper.MapAlbumStub(h), LastFmMapper.ArtistName(h), null)));
        }

        return results;
    }

    private async Task<List<Candidate>> FindAlbumsAsync(
        MediaSearchContext ctx, List<string> titles, CancellationToken ct)
    {
        var artist = ctx.ParentName;

        if (!string.IsNullOrWhiteSpace(artist))
        {
            foreach (var title in titles)
            {
                var root = await _client!.GetAsync("album.getInfo",
                    P(("artist", artist), ("album", title), ("autocorrect", "1"), ("lang", _language)), ct);
                if (J.Obj(root, "album") is not { } album) continue;
                var meta = LastFmMapper.MapAlbum(album);
                Remember(meta);
                return [new Candidate(meta, LastFmMapper.ArtistName(album), null)];
            }
        }

        var search = await _client!.GetAsync("album.search", P(("album", titles[0]), ("limit", "10")), ct);
        return J.List(J.Obj(J.Obj(search, "results"), "albummatches"), "album")
            .Select(h => new Candidate(LastFmMapper.MapAlbumStub(h), LastFmMapper.ArtistName(h), null))
            .ToList();
    }

    private async Task<List<Candidate>> FindTracksAsync(
        MediaSearchContext ctx, List<string> titles, CancellationToken ct)
    {
        var artist = ctx.GrandparentName;
        var album  = ctx.ParentName;   // the Chronicle album this track sits on; keeps ids unique

        var mbid = LastFmIds.ExtractMbid(ctx.KnownExternalIds?.GetValueOrDefault("musicbrainz"), "recording");
        if (mbid is not null)
        {
            var root = await _client!.GetAsync("track.getInfo",
                P(("mbid", mbid), ("autocorrect", "1")), ct);
            if (J.Obj(root, "track") is { } byMbid)
            {
                var meta = LastFmMapper.MapTrack(byMbid, album);
                Remember(meta);
                return [new Candidate(meta, LastFmMapper.ArtistName(byMbid), album, 100, "musicbrainz id match")];
            }
        }

        if (!string.IsNullOrWhiteSpace(artist))
        {
            foreach (var title in titles)
            {
                var root = await _client!.GetAsync("track.getInfo",
                    P(("artist", artist), ("track", title), ("autocorrect", "1")), ct);
                if (J.Obj(root, "track") is not { } track) continue;
                var meta = LastFmMapper.MapTrack(track, album);
                Remember(meta);
                return [new Candidate(meta, LastFmMapper.ArtistName(track), album)];
            }
        }

        var search = await _client!.GetAsync("track.search",
            P(("track", titles[0]), ("artist", artist), ("limit", "10")), ct);
        return J.List(J.Obj(J.Obj(search, "results"), "trackmatches"), "track")
            .Select(h => new Candidate(LastFmMapper.MapTrackStub(h, album), LastFmMapper.ArtistName(h), album))
            .ToList();
    }

    // ── Scoring ───────────────────────────────────────────────────────────────

    private static ScoredCandidate Score(MediaSearchContext ctx, Candidate c)
    {
        if (c.Bonus >= 100)
            return new ScoredCandidate(c.Meta, 100, c.BonusReason);

        int score = 0;
        var reasons = new List<string>();

        var candidate = Normalize(c.Meta.Title);
        var query     = Normalize(ctx.PreciseName ?? ctx.Name);
        var alt       = (ctx.AltTitles ?? []).Select(Normalize);

        if (candidate == query || alt.Contains(candidate))
        {
            score += 60;
            reasons.Add("title exact");
        }
        else if (candidate.Length > 0 && query.Length > 0
                 && (candidate.Contains(query, StringComparison.Ordinal) || query.Contains(candidate, StringComparison.Ordinal)))
        {
            score += 30;
            reasons.Add("title contains");
        }

        // The artist the item should belong to: the parent for an album, the grandparent for a track.
        var wantArtist = ctx.HierarchyLevel switch { 1 => ctx.ParentName, 2 => ctx.GrandparentName, _ => null };
        if (!string.IsNullOrWhiteSpace(wantArtist) && !string.IsNullOrWhiteSpace(c.Artist))
        {
            if (Normalize(wantArtist) == Normalize(c.Artist))
            {
                score += 30;
                reasons.Add("artist exact");
            }
            else
            {
                // Same title by a different artist is a different record, not a weak match.
                score -= 40;
                reasons.Add("artist mismatch");
            }
        }

        return new ScoredCandidate(c.Meta, score, reasons.Count > 0 ? string.Join(", ", reasons) : "no signals");
    }

    private static string Normalize(string s) =>
        Regex.Replace(s.Trim(), @"[:\-,\.'’!?&]", " ")
            .Replace("  ", " ").Replace("  ", " ").Trim().ToLowerInvariant();

    // ── IMetadataProvider: get by ID ──────────────────────────────────────────

    /// <summary>
    /// Accepts <c>artist:{name}</c>, <c>album:{artist}/{album}</c>, <c>track:{artist}/{track}</c>,
    /// <c>track:{artist}/{album}/{track}</c> (every segment percent-encoded), or a last.fm URL.
    /// </summary>
    public async Task<MediaMetadata> GetByIdAsync(string externalId, CancellationToken ct = default)
    {
        EnsureConfigured();

        var id = LastFmIds.Parse(externalId)
            ?? throw new ArgumentException(
                $"Invalid Last.fm ID format: '{externalId}'. Expected artist:Name, album:Artist/Album, " +
                "track:Artist/Track, or a last.fm URL.");

        // Re-render canonically so a URL and the equivalent typed id share one cache slot.
        var canonical = id.Kind switch
        {
            LastFmEntity.Artist => LastFmIds.Artist(id.Artist),
            LastFmEntity.Album  => LastFmIds.Album(id.Artist, id.Album!),
            _                   => LastFmIds.Track(id.Artist, id.Album, id.Track!),
        };
        if (TryGetCached(canonical) is { } cached) return cached;

        MediaMetadata? meta = null;
        switch (id.Kind)
        {
            case LastFmEntity.Artist:
                if (await GetArtistInfoAsync(id.Artist, null, ct) is { } artist)
                    meta = LastFmMapper.MapArtist(artist);
                break;

            case LastFmEntity.Album:
                var albumRoot = await _client!.GetAsync("album.getInfo",
                    P(("artist", id.Artist), ("album", id.Album), ("autocorrect", "1"), ("lang", _language)), ct);
                if (J.Obj(albumRoot, "album") is { } album)
                    meta = LastFmMapper.MapAlbum(album);
                break;

            default:
                var trackRoot = await _client!.GetAsync("track.getInfo",
                    P(("artist", id.Artist), ("track", id.Track), ("autocorrect", "1")), ct);
                if (J.Obj(trackRoot, "track") is { } track)
                    meta = LastFmMapper.MapTrack(track, id.Album);
                break;
        }

        // Not on Last.fm (or removed since): an empty result, not an error -- the enrichment
        // service records the row as having nothing from this provider.
        if (meta is null)
            return new MediaMetadata { ExternalId = canonical, Source = LastFmMapper.Source };

        // Keep the id the caller asked for: autocorrect may have changed the spelling Last.fm
        // reports, and the stored id must stay stable across refreshes.
        meta.ExternalId = canonical;
        Remember(meta);
        return meta;
    }

    // ── IMetadataProvider: image / health ─────────────────────────────────────

    public async Task<byte[]> GetImageAsync(string url, CancellationToken ct = default)
    {
        EnsureConfigured();
        return await _client!.GetBytesAsync(url, ct).ConfigureAwait(false);
    }

    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        if (_client is null) return false;
        try
        {
            var artist = await GetArtistInfoAsync("Radiohead", null, ct).ConfigureAwait(false);
            return artist is not null && J.Str(artist, "name") is not null;
        }
        catch
        {
            return false;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<JsonElement?> GetArtistInfoAsync(string? name, string? mbid, CancellationToken ct)
    {
        var root = await _client!.GetAsync("artist.getInfo",
            P(("artist", name), ("mbid", mbid), ("autocorrect", "1"), ("lang", _language)), ct)
            .ConfigureAwait(false);
        return J.Obj(root, "artist");
    }

    private static KeyValuePair<string, string?>[] P(params (string Key, string? Value)[] pairs) =>
        pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)).ToArray();

    private void Remember(MediaMetadata meta) =>
        _cache = new CacheEntry(meta.ExternalId, meta, DateTime.UtcNow.Ticks + CacheTtlTicks);

    private MediaMetadata? TryGetCached(string id)
    {
        var entry = _cache;
        return entry is not null && entry.Id == id && entry.ExpiresAtTicks > DateTime.UtcNow.Ticks
            ? entry.Result
            : null;
    }

    private void EnsureConfigured()
    {
        // PluginAuthException, not InvalidOperationException: the host turns the former into an
        // AuthFailed row (surfaced as a "plugin needs credentials" alert, bulk-resettable once the key
        // is entered) but the latter into a silent, permanent Skipped row for every music item.
        if (_client is null)
            throw new PluginAuthException(PluginId,
                "Last.fm plugin is not configured -- set an API key in Settings → Plugins → Last.fm.");
    }

    public void Dispose()
    {
        _client?.Dispose();
        _client = null;
    }
}
