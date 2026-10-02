using System.Text.Json;
using Chronicle.Plugins.Models;
using Xunit;

namespace Chronicle.Plugin.LastFM.Tests;

public class LastFmProviderTests
{
    private const string PlaceholderUrl =
        "https://lastfm.freetls.fastly.net/i/u/300x300/2a96cbd8b46e442fc41c2b86b821562f.png";

    private const string ArtistInfo = """
        {"artist":{
          "name":"Radiohead","mbid":"a74b1b7f-71a5-4011-9441-d0b5e4122711",
          "url":"https://www.last.fm/music/Radiohead","ontour":"0",
          "image":[{"#text":"https://lastfm.freetls.fastly.net/i/u/300x300/2a96cbd8b46e442fc41c2b86b821562f.png","size":"extralarge"}],
          "stats":{"listeners":"6000000","playcount":"400000000"},
          "similar":{"artist":[{"name":"Thom Yorke","url":"https://www.last.fm/music/Thom+Yorke"}]},
          "tags":{"tag":[{"name":"alternative"},{"name":"rock"}]},
          "bio":{"published":"10 Feb 2006","summary":"Radiohead are an English rock band. <a href=\"https://www.last.fm/music/Radiohead\">Read more on Last.fm</a>","content":"Full text."}}}
        """;

    private const string AlbumInfo = """
        {"album":{
          "name":"OK Computer","artist":"Radiohead","mbid":"",
          "url":"https://www.last.fm/music/Radiohead/OK+Computer",
          "image":[{"#text":"https://x/ok-large.png","size":"extralarge"}],
          "listeners":"3000000","playcount":"90000000",
          "tracks":{"track":{"name":"Airbag","duration":"284","url":"https://x/airbag",
                              "@attr":{"rank":"1"},"artist":{"name":"Radiohead"}}},
          "tags":{"tag":[{"name":"90s"}]},
          "wiki":{"published":"01 Jan 2007","summary":"Third album.","content":"Third album, longer."}}}
        """;

    private const string TrackInfo = """
        {"track":{
          "name":"Karma Police","mbid":"","url":"https://x/karma","duration":"264000",
          "listeners":"1000","playcount":"5000",
          "artist":{"name":"Radiohead","mbid":"a74b1b7f-71a5-4011-9441-d0b5e4122711"},
          "album":{"title":"OK Computer","artist":"Radiohead","image":[{"#text":"https://x/ok.png","size":"large"}],"@attr":{"position":"6"}},
          "toptags":{"tag":[{"name":"rock"}]},
          "wiki":{"summary":"A song."}}}
        """;

    private static MediaSearchContext Artist(string name) =>
        new(name, MediaTypeName: "music", HierarchyLevel: 0, IsRealHierarchyPosition: true);

    // ── GetByIdAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetById_Artist_MapsEverything_AndDropsThePlaceholderImage()
    {
        var handler = new StubHandler(_ => StubHandler.Json(ArtistInfo));
        var meta = await TestClient.Provider(handler).GetByIdAsync("artist:Radiohead");

        Assert.Equal("artist:Radiohead", meta.ExternalId);
        Assert.Equal("lastfm", meta.Source);
        Assert.Equal("Radiohead", meta.Title);
        Assert.Equal("Radiohead are an English rock band.", meta.Overview);   // "Read more" link stripped
        Assert.Equal(["alternative", "rock"], meta.Tags);
        Assert.Null(meta.PosterUrl);                                          // grey-star placeholder

        var ext = meta.ExtendedData!.Value;
        Assert.Equal(6000000, ext.GetProperty("listeners").GetInt64());
        Assert.Equal("a74b1b7f-71a5-4011-9441-d0b5e4122711", ext.GetProperty("mbid").GetString());
        Assert.Equal("Thom Yorke", ext.GetProperty("similar_artists")[0].GetProperty("name").GetString());
        Assert.Equal("Full text.", ext.GetProperty("bio_content").GetString());
    }

    [Fact]
    public async Task GetById_Album_HandlesSingleTrackCollapsedToObject()
    {
        var handler = new StubHandler(_ => StubHandler.Json(AlbumInfo));
        var meta = await TestClient.Provider(handler).GetByIdAsync("album:Radiohead/OK%20Computer");

        Assert.Equal("album:Radiohead/OK%20Computer", meta.ExternalId);
        Assert.Equal("OK Computer", meta.Title);
        Assert.Equal("https://x/ok-large.png", meta.PosterUrl);
        Assert.Equal(["90s"], meta.Tags);

        var tracks = meta.ExtendedData!.Value.GetProperty("tracks");
        Assert.Equal(1, tracks.GetArrayLength());
        Assert.Equal("Airbag", tracks[0].GetProperty("name").GetString());
        Assert.Equal(284, tracks[0].GetProperty("duration_seconds").GetInt64());
    }

    [Fact]
    public async Task GetById_Track_MapsRuntimeAndAlbum_AndKeepsRequestedId()
    {
        var handler = new StubHandler(_ => StubHandler.Json(TrackInfo));
        var meta = await TestClient.Provider(handler)
            .GetByIdAsync(LastFmIds.Track("Radiohead", "Some Compilation", "Karma Police"));

        // The id the caller asked for (album segment included) must survive, not be replaced by
        // whatever album Last.fm itself reports.
        Assert.Equal(LastFmIds.Track("Radiohead", "Some Compilation", "Karma Police"), meta.ExternalId);
        Assert.Equal(4, meta.RuntimeMinutes);                       // 264 s ≈ 4 min
        Assert.Equal("https://x/ok.png", meta.PosterUrl);
        Assert.Equal("OK Computer", meta.ExtendedData!.Value.GetProperty("album_title").GetString());
    }

    [Fact]
    public async Task GetById_NotFound_ReturnsEmptyResultWithId_NotAnException()
    {
        var handler = new StubHandler(_ => StubHandler.Json("""{"error":6,"message":"Artist not found"}"""));
        var meta = await TestClient.Provider(handler).GetByIdAsync("artist:Nobody%20Real");

        Assert.Equal("artist:Nobody%20Real", meta.ExternalId);
        Assert.Equal(string.Empty, meta.Title);
    }

    [Fact]
    public async Task GetById_AcceptsLastFmUrl_AndRejectsGarbage()
    {
        var handler = new StubHandler(_ => StubHandler.Json(ArtistInfo));
        var provider = TestClient.Provider(handler);

        var meta = await provider.GetByIdAsync("https://www.last.fm/music/Radiohead");
        Assert.Equal("artist:Radiohead", meta.ExternalId);
        Assert.Equal("Radiohead", StubHandler.ParamOf(
            new HttpRequestMessage(HttpMethod.Get, handler.Requests[0]), "artist"));

        await Assert.ThrowsAsync<ArgumentException>(() => provider.GetByIdAsync("not an id"));
    }

    // ── SearchAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_Artist_ExactHit_ScoresAndCachesForGetById()
    {
        var handler = new StubHandler(_ => StubHandler.Json(ArtistInfo));
        var provider = TestClient.Provider(handler);

        var results = await provider.SearchAsync(Artist("radiohead"));

        var hit = Assert.Single(results);
        Assert.Equal("artist:Radiohead", hit.Metadata.ExternalId);
        Assert.Equal(60, hit.Score);
        Assert.Contains("title exact", hit.ScoreReason);

        // The enrichment service follows a search with GetById for the same id: no second request.
        await provider.GetByIdAsync("artist:Radiohead");
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Search_Artist_KnownMusicBrainzId_LooksUpByMbid_AndScores100()
    {
        var handler = new StubHandler(_ => StubHandler.Json(ArtistInfo));
        var ctx = Artist("anything") with
        {
            KnownExternalIds = new Dictionary<string, string>
            {
                ["musicbrainz"] = "artist:a74b1b7f-71a5-4011-9441-d0b5e4122711",
            },
        };

        var results = await TestClient.Provider(handler).SearchAsync(ctx);

        Assert.Equal(100, Assert.Single(results).Score);
        Assert.Equal("a74b1b7f-71a5-4011-9441-d0b5e4122711",
            StubHandler.ParamOf(new HttpRequestMessage(HttpMethod.Get, handler.Requests[0]), "mbid"));
    }

    [Fact]
    public async Task Search_Artist_IgnoresAReleaseGroupIdInTheArtistSlot()
    {
        var handler = new StubHandler(r => StubHandler.MethodOf(r) == "artist.getInfo"
            ? StubHandler.Json(ArtistInfo)
            : StubHandler.Json("{}"));
        var ctx = Artist("radiohead") with
        {
            KnownExternalIds = new Dictionary<string, string>
            {
                ["musicbrainz"] = "release-group:a74b1b7f-71a5-4011-9441-d0b5e4122711",
            },
        };

        await TestClient.Provider(handler).SearchAsync(ctx);

        var first = new HttpRequestMessage(HttpMethod.Get, handler.Requests[0]);
        Assert.Equal("", StubHandler.ParamOf(first, "mbid"));
        Assert.Equal("radiohead", StubHandler.ParamOf(first, "artist"));
    }

    [Fact]
    public async Task Search_Artist_FallsBackToSearchStubs_WhenNameLookupMisses()
    {
        var handler = new StubHandler(r => StubHandler.MethodOf(r) switch
        {
            "artist.getInfo"  => StubHandler.Json("""{"error":6,"message":"Artist not found"}"""),
            "artist.search"   => StubHandler.Json("""
                {"results":{"artistmatches":{"artist":[
                  {"name":"Radiohead","listeners":"6000000","mbid":"","url":"https://x/r","image":[]},
                  {"name":"Radiohead Tribute","listeners":"5","mbid":"","url":"https://x/t","image":[]}]}}}
                """),
            _ => StubHandler.Json("{}"),
        });

        var results = await TestClient.Provider(handler).SearchAsync(Artist("radiohead"));

        Assert.Equal(["artist:Radiohead", "artist:Radiohead%20Tribute"],
            results.Select(r => r.Metadata.ExternalId));
        Assert.True(results[0].Score > results[1].Score);
    }

    [Fact]
    public async Task Search_Album_UsesParentAsArtist_AndPenalisesAnotherArtistsAlbum()
    {
        var handler = new StubHandler(r => StubHandler.MethodOf(r) switch
        {
            "album.getInfo" => StubHandler.Json("""{"error":6,"message":"Album not found"}"""),
            "album.search"  => StubHandler.Json("""
                {"results":{"albummatches":{"album":[
                  {"name":"Greatest Hits","artist":"Queen","url":"https://x/q","mbid":"","image":[]},
                  {"name":"Greatest Hits","artist":"Abba","url":"https://x/a","mbid":"","image":[]}]}}}
                """),
            _ => StubHandler.Json("{}"),
        });
        var ctx = new MediaSearchContext("greatest hits", MediaTypeName: "music",
            HierarchyLevel: 1, ParentName: "Queen", IsRealHierarchyPosition: true);

        var results = await TestClient.Provider(handler).SearchAsync(ctx);

        Assert.Equal("album:Queen/Greatest%20Hits", results[0].Metadata.ExternalId);
        Assert.Equal(90, results[0].Score);          // title exact 60 + artist exact 30
        Assert.Equal(20, results[1].Score);          // title exact 60 - artist mismatch 40
        Assert.Contains("artist mismatch", results[1].ScoreReason);
    }

    [Fact]
    public async Task Search_Track_BakesTheChronicleAlbumIntoTheId()
    {
        var handler = new StubHandler(_ => StubHandler.Json(TrackInfo));
        var ctx = new MediaSearchContext("karma police", MediaTypeName: "music", HierarchyLevel: 2,
            ParentName: "Some Compilation", GrandparentName: "Radiohead", IsRealHierarchyPosition: true);

        var results = await TestClient.Provider(handler).SearchAsync(ctx);

        var hit = Assert.Single(results);
        Assert.Equal(LastFmIds.Track("Radiohead", "Some Compilation", "Karma Police"), hit.Metadata.ExternalId);
        Assert.Equal(90, hit.Score);
    }

    [Fact]
    public async Task Search_OpenAddMediaSearch_AlsoReturnsAlbums()
    {
        var handler = new StubHandler(r => StubHandler.MethodOf(r) switch
        {
            "artist.getInfo" => StubHandler.Json(ArtistInfo),
            "album.search"   => StubHandler.Json("""
                {"results":{"albummatches":{"album":{"name":"Radiohead","artist":"Other Band","url":"u","mbid":"","image":[]}}}}
                """),
            _ => StubHandler.Json("{}"),
        });
        var ctx = new MediaSearchContext("radiohead", MediaTypeName: "music");   // not a real hierarchy position

        var ids = (await TestClient.Provider(handler).SearchAsync(ctx)).Select(r => r.Metadata.ExternalId).ToList();

        Assert.Contains("artist:Radiohead", ids);
        Assert.Contains("album:Other%20Band/Radiohead", ids);
    }

    [Fact]
    public async Task Search_NonMusicType_ReturnsNothing_WithoutCallingLastFm()
    {
        var handler = new StubHandler(_ => StubHandler.Json("{}"));
        var ctx = new MediaSearchContext("Fight Club", MediaTypeName: "movies");

        Assert.Empty(await TestClient.Provider(handler).SearchAsync(ctx));
        Assert.Equal(0, handler.CallCount);
    }

    // ── Declarations / health ─────────────────────────────────────────────────

    [Fact]
    public void Declarations_AreMusicOnly_AndApiKeyIsRequiredPassword()
    {
        var provider = new LastFmMetadataProvider();
        var music = Assert.Single(provider.GetSupportedMediaTypes());
        Assert.Equal("music", music.MediaTypeName);
        Assert.Equal(3, music.HierarchyLevels);

        var key = provider.GetSettingsSchema().Settings.Single(s => s.Key == "api_key");
        Assert.True(key.Required);
        Assert.Equal(SettingType.Password, key.Type);
    }

    [Fact]
    public async Task Unconfigured_ThrowsPluginAuthException_SoTheHostRecordsAuthFailedNotSkipped()
    {
        var provider = new LastFmMetadataProvider();
        await Assert.ThrowsAsync<Chronicle.Plugins.PluginAuthException>(() => provider.SearchAsync(Artist("x")));
        await Assert.ThrowsAsync<Chronicle.Plugins.PluginAuthException>(() => provider.GetByIdAsync("artist:x"));
    }

    [Fact]
    public async Task GetById_TrackUnder30Seconds_StillHasAtLeastOneMinuteRuntime()
    {
        var handler = new StubHandler(_ => StubHandler.Json(
            "{\"track\":{\"name\":\"Interlude\",\"duration\":\"20000\",\"artist\":{\"name\":\"Band\"}}}"));
        var meta = await TestClient.Provider(handler).GetByIdAsync("track:Band/Interlude");
        Assert.Equal(1, meta.RuntimeMinutes);
    }

    [Fact]
    public void Configure_WithoutApiKey_Throws() =>
        Assert.Throws<InvalidOperationException>(() =>
            new LastFmMetadataProvider().Configure(new Dictionary<string, string>()));

    [Fact]
    public async Task HealthCheck_TrueWhenArtistComesBack_FalseOnBadKey()
    {
        Assert.True(await TestClient.Provider(new StubHandler(_ => StubHandler.Json(ArtistInfo))).HealthCheckAsync());

        var bad = new StubHandler(_ => StubHandler.Json("""{"error":10,"message":"Invalid API key"}"""));
        Assert.False(await TestClient.Provider(bad).HealthCheckAsync());
    }

    [Fact]
    public async Task HealthCheck_FalseWhenUnconfigured() =>
        Assert.False(await new LastFmMetadataProvider().HealthCheckAsync());
}
