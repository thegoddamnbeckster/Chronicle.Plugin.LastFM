using System.Text.Json;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using J = Chronicle.Plugin.LastFM.LastFmJson;

namespace Chronicle.Plugin.LastFM;

/// <summary>
/// Imports the user's own listening history (their scrobbles) from Last.fm via <c>user.getRecentTracks</c>.
///
/// No sign-in flow is involved: a user's scrobbles are public by default, so the API key already saved for
/// metadata plus the Last.fm username is all that is needed. Each scrobble becomes one watch ("listened")
/// event stamped with the exact time it was played, so re-importing the same range never duplicates a play.
///
/// Incremental syncs ask only for scrobbles from shortly before the last sync; the host dedupes by exact
/// timestamp, so the overlap is free insurance for scrobbles that were submitted late (offline players
/// upload plays with their original, earlier times).
/// </summary>
public sealed class LastFmImportProvider : IImportProvider
{
    private const string KeyApiKey   = "api_key";
    private const string KeyUsername = "username";
    private const string UserAgent   = "Chronicle/1.0 (https://github.com/thegoddamnbeckster/Chronicle)";
    private const int PageSize = 200;

    /// <summary>How far before the last sync an incremental run starts, to catch late-uploaded scrobbles.</summary>
    internal static readonly TimeSpan IncrementalOverlap = TimeSpan.FromDays(1);

    private LastFmClient? _client;
    private string? _username;

    public LastFmImportProvider() { }

    /// <summary>Test-only: a provider wired to a prepared client and username.</summary>
    internal LastFmImportProvider(LastFmClient client, string username)
    {
        _client   = client;
        _username = username;
    }

    // ── Identity ──────────────────────────────────────────────────────────────

    public string PluginId    => LastFmClient.PluginId;
    public string Name        => "Last.fm";
    public string Version     => "1.1.0";
    public string Author      => "Chronicle Contributors";
    public string Description => "Imports your Last.fm listening history (scrobbles) as plays of your music library.";

    // ── Settings ──────────────────────────────────────────────────────────────

    // The API key is declared by the metadata provider in the same plugin; the host merges both schemas.
    public PluginSettingsSchema GetSettingsSchema() => new()
    {
        Settings =
        [
            new SettingDefinition
            {
                Key         = KeyUsername,
                Label       = "Last.fm Username",
                Description = "Your Last.fm username. Needed to import your own play history; your scrobbles must be " +
                              "public (the default). Leave empty to use Last.fm for metadata only.",
                Type        = SettingType.Text,
                Required    = false,
            },
        ]
    };

    public void Configure(IReadOnlyDictionary<string, string> settings)
    {
        settings.TryGetValue(KeyApiKey,   out var apiKey);
        settings.TryGetValue(KeyUsername, out var username);

        _client?.Dispose();
        _client   = null;
        _username = string.IsNullOrWhiteSpace(username) ? null : username.Trim();
        if (!string.IsNullOrWhiteSpace(apiKey))
            _client = new LastFmClient(apiKey.Trim(), UserAgent);
    }

    // ── Auth: key + username, no device flow ──────────────────────────────────

    public Task<DeviceAuthStart> StartAuthAsync(CancellationToken ct = default) =>
        throw new NotSupportedException(
            "Last.fm needs no sign-in: save the API key and your Last.fm username in the plugin settings.");

    public Task<DeviceAuthPollResult> PollAuthAsync(string pollCode, CancellationToken ct = default) =>
        throw new NotSupportedException("Last.fm does not use a device flow.");

    public Task<bool> IsAuthenticatedAsync(CancellationToken ct = default) =>
        Task.FromResult(_client is not null && _username is not null);

    public ImportCapabilities GetCapabilities() =>
        new(SupportsHistory: true, SupportsRatings: false, SupportsWatchlist: false, RequiresDeviceAuth: false);

    // ── History ───────────────────────────────────────────────────────────────

    public async Task<List<ImportedWatchEvent>> GetWatchHistoryAsync(
        DateTimeOffset? since = null, CancellationToken ct = default)
    {
        var client = _client ?? throw new InvalidOperationException(
            "Last.fm is not configured -- set the API key and your username in Settings → Plugins → Last.fm.");
        var user = _username ?? throw new InvalidOperationException(
            "No Last.fm username set -- enter it in Settings → Plugins → Last.fm.");

        long? fromUts = since is null ? null : (since.Value - IncrementalOverlap).ToUnixTimeSeconds();
        var events = new List<ImportedWatchEvent>();

        for (var page = 1; ; page++)
        {
            var root = await client.GetAsync("user.getRecentTracks",
            [
                new("user",  user),
                new("limit", PageSize.ToString()),
                new("page",  page.ToString()),
                new("from",  fromUts?.ToString()),
            ], ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Last.fm has no user named '{user}'. Check the username in Settings → Plugins → Last.fm.");

            var recent = J.Obj(root, "recenttracks");
            foreach (var t in J.List(recent, "track"))
                if (ToEvent(t) is { } evt) events.Add(evt);

            var attr       = J.Obj(recent, "@attr");
            var totalPages = (int)(J.Long(attr, "totalPages") ?? 1);
            if (page >= totalPages) break;
        }

        return events;
    }

    /// <summary>One <c>recenttracks.track</c> entry as a watch event; null for the "now playing" row (it has no
    /// play time yet) and for entries missing an artist or title.</summary>
    internal static ImportedWatchEvent? ToEvent(JsonElement track)
    {
        var uts    = J.Long(J.Obj(track, "date"), "uts");
        var title  = J.Str(track, "name");
        var artist = J.Str(track, "artist");
        if (uts is null || title is null || artist is null) return null;

        var album = J.Str(track, "album");
        var ids   = new Dictionary<string, string>();
        if (J.Str(track, "mbid") is { } mbid)
            ids["musicbrainz"] = $"recording:{mbid}";

        return new ImportedWatchEvent(
            ExternalId:      LastFmIds.Track(artist, album, title),
            AdditionalIds:   ids,
            MediaType:       "track",
            Title:           title,
            Year:            null,
            WatchedAt:       DateTimeOffset.FromUnixTimeSeconds(uts.Value),
            ProgressPercent: 100,
            ArtistName:      artist,
            AlbumName:       album);
    }

    // ── Everything else: nothing to import ────────────────────────────────────

    public Task<List<ImportedRating>> GetRatingsAsync(CancellationToken ct = default) =>
        Task.FromResult(new List<ImportedRating>());

    public Task<List<ImportedWatchlistEntry>> GetWatchlistAsync(CancellationToken ct = default) =>
        Task.FromResult(new List<ImportedWatchlistEntry>());

    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        if (_client is null || _username is null) return false;
        try
        {
            var root = await _client.GetAsync("user.getInfo", [new("user", _username)], ct).ConfigureAwait(false);
            return root is not null;
        }
        catch
        {
            return false;
        }
    }
}
