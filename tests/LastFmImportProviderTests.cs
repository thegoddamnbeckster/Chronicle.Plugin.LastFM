using System.Text.Json;
using Xunit;

namespace Chronicle.Plugin.LastFM.Tests;

public class LastFmImportProviderTests
{
    private static string Page(int page, int totalPages, params string[] tracks) =>
        "{\"recenttracks\":{\"track\":[" + string.Join(",", tracks) + "],\"@attr\":{\"user\":\"u\",\"page\":\"" + page
        + "\",\"perPage\":\"200\",\"totalPages\":\"" + totalPages + "\"}}}";

    private static string Scrobble(string artist, string title, string? album, long uts, string mbid = "") =>
        "{\"artist\":{\"mbid\":\"\",\"#text\":\"" + artist + "\"},\"name\":\"" + title + "\",\"mbid\":\"" + mbid
        + "\",\"album\":{\"mbid\":\"\",\"#text\":\"" + album + "\"},\"date\":{\"uts\":\"" + uts + "\",\"#text\":\"x\"}}";

    private const string NowPlaying =
        """{"artist":{"mbid":"","#text":"Live Band"},"name":"Right Now","mbid":"","album":{"mbid":"","#text":"Live"},"@attr":{"nowplaying":"true"}}""";

    private static LastFmImportProvider Provider(StubHandler h, string user = "mbeck") =>
        new(TestClient.Make(h), user);

    [Fact]
    public async Task EachScrobble_BecomesATrackEventStampedWithItsPlayTime()
    {
        var h = new StubHandler(_ => StubHandler.Json(Page(1, 1,
            Scrobble("Radiohead", "Karma Police", "OK Computer", 1700000000, "abc-123"))));

        var events = await Provider(h).GetWatchHistoryAsync();

        var e = Assert.Single(events);
        Assert.Equal("track", e.MediaType);
        Assert.Equal("Karma Police", e.Title);
        Assert.Equal("Radiohead", e.ArtistName);
        Assert.Equal("OK Computer", e.AlbumName);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), e.WatchedAt);
        Assert.False(e.WatchedAtIsApproximate);
        Assert.Equal(100, e.ProgressPercent);
        Assert.Equal("recording:abc-123", e.AdditionalIds["musicbrainz"]);
        Assert.Equal(LastFmIds.Track("Radiohead", "OK Computer", "Karma Police"), e.ExternalId);
    }

    [Fact]
    public async Task NowPlayingRow_IsSkipped_BecauseItHasNoPlayTimeYet()
    {
        var h = new StubHandler(_ => StubHandler.Json(Page(1, 1, NowPlaying,
            Scrobble("A", "T", "L", 1700000000))));

        var events = await Provider(h).GetWatchHistoryAsync();

        Assert.Equal("T", Assert.Single(events).Title);
    }

    [Fact]
    public async Task ScrobbleWithoutAnAlbum_HasNoAlbumName()
    {
        var h = new StubHandler(_ => StubHandler.Json(
            """{"recenttracks":{"track":[{"artist":{"#text":"A"},"name":"T","album":{"#text":""},"date":{"uts":"1700000000"}}],"@attr":{"totalPages":"1"}}}"""));

        var e = Assert.Single(await Provider(h).GetWatchHistoryAsync());

        Assert.Null(e.AlbumName);
        Assert.Equal(LastFmIds.Track("A", null, "T"), e.ExternalId);
    }

    [Fact]
    public async Task EveryPageIsFetched()
    {
        var h = new StubHandler(r => StubHandler.ParamOf(r, "page") switch
        {
            "1" => StubHandler.Json(Page(1, 3, Scrobble("A", "One", "L", 1700000300))),
            "2" => StubHandler.Json(Page(2, 3, Scrobble("A", "Two", "L", 1700000200))),
            _   => StubHandler.Json(Page(3, 3, Scrobble("A", "Three", "L", 1700000100))),
        });

        var events = await Provider(h).GetWatchHistoryAsync();

        Assert.Equal(["One", "Two", "Three"], events.Select(e => e.Title));
        Assert.Equal(3, h.CallCount);
    }

    [Fact]
    public async Task SingleScrobbleSentAsABareObject_IsStillRead()
    {
        var h = new StubHandler(_ => StubHandler.Json(
            """{"recenttracks":{"track":{"artist":{"#text":"A"},"name":"T","album":{"#text":"L"},"date":{"uts":"1700000000"}},"@attr":{"totalPages":"1"}}}"""));

        Assert.Single(await Provider(h).GetWatchHistoryAsync());
    }

    [Fact]
    public async Task FullImport_SendsNoFromParameter_AndAsksForTheConfiguredUser()
    {
        var h = new StubHandler(_ => StubHandler.Json(Page(1, 1)));

        await Provider(h, "mbeck").GetWatchHistoryAsync(since: null);

        var r = h.Requests[0];
        Assert.DoesNotContain("from=", r.Query);
        Assert.Contains("user=mbeck", r.Query);
        Assert.Contains("method=user.getRecentTracks", r.Query);
    }

    [Fact]
    public async Task IncrementalImport_StartsOneDayBeforeTheLastSync_ToCatchLateUploads()
    {
        var h = new StubHandler(_ => StubHandler.Json(Page(1, 1)));
        var since = DateTimeOffset.FromUnixTimeSeconds(1700100000);

        await Provider(h).GetWatchHistoryAsync(since);

        Assert.Contains($"from={1700100000 - 86400}", h.Requests[0].Query);
    }

    [Fact]
    public async Task UnknownUser_ThrowsAnErrorThatNamesTheUser()
    {
        var h = new StubHandler(_ => StubHandler.Json("""{"error":6,"message":"User not found"}"""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(h, "nobody").GetWatchHistoryAsync());

        Assert.Contains("nobody", ex.Message);
    }

    [Fact]
    public async Task IsAuthenticated_NeedsBothTheApiKeyAndAUsername()
    {
        var p = new LastFmImportProvider();

        p.Configure(new Dictionary<string, string> { ["api_key"] = "k" });
        Assert.False(await p.IsAuthenticatedAsync());

        p.Configure(new Dictionary<string, string> { ["username"] = "u" });
        Assert.False(await p.IsAuthenticatedAsync());

        p.Configure(new Dictionary<string, string> { ["api_key"] = "k", ["username"] = " u " });
        Assert.True(await p.IsAuthenticatedAsync());
    }

    [Fact]
    public void Capabilities_AreHistoryOnly_WithNoDeviceAuth()
    {
        var c = new LastFmImportProvider().GetCapabilities();

        Assert.True(c.SupportsHistory);
        Assert.False(c.SupportsRatings);
        Assert.False(c.SupportsWatchlist);
        Assert.False(c.RequiresDeviceAuth);
    }

    [Fact]
    public void PluginId_MatchesTheManifest_SoTheHostFindsTheProvider()
    {
        var manifest = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "manifest.json")));

        Assert.Equal(manifest.RootElement.GetProperty("plugin_id").GetString(), new LastFmImportProvider().PluginId);
    }
}
