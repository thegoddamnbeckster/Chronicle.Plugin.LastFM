using System.Net;
using Chronicle.Plugins;
using Xunit;

namespace Chronicle.Plugin.LastFM.Tests;

public class LastFmClientTests
{
    [Fact]
    public async Task Error6_NotFound_ReturnsNull()
    {
        var handler = new StubHandler(_ => StubHandler.Json("""{"error":6,"message":"Artist not found"}"""));
        var result = await TestClient.Make(handler).GetAsync("artist.getInfo", [new("artist", "x")]);
        Assert.Null(result);
        Assert.Equal(1, handler.CallCount);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(26)]
    public async Task InvalidOrSuspendedKey_ThrowsPluginAuthException(int code)
    {
        var handler = new StubHandler(_ =>
            StubHandler.Json("{\"error\":" + code + ",\"message\":\"Invalid API key\"}", HttpStatusCode.Forbidden));
        var ex = await Assert.ThrowsAsync<PluginAuthException>(
            () => TestClient.Make(handler).GetAsync("artist.getInfo", [new("artist", "x")]));
        Assert.Equal("chronicle.plugin.lastfm", ex.PluginId);
    }

    [Fact]
    public async Task RateLimit29_IsRetried_ThenSucceeds()
    {
        int call = 0;
        var handler = new StubHandler(_ => ++call == 1
            ? StubHandler.Json("""{"error":29,"message":"Rate limit exceeded"}""")
            : StubHandler.Json("""{"artist":{"name":"Radiohead"}}"""));

        var result = await TestClient.Make(handler).GetAsync("artist.getInfo", [new("artist", "Radiohead")]);

        Assert.NotNull(result);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task OtherError_Throws()
    {
        var handler = new StubHandler(_ => StubHandler.Json("""{"error":2,"message":"Invalid service"}"""));
        await Assert.ThrowsAsync<HttpRequestException>(
            () => TestClient.Make(handler).GetAsync("nope", []));
    }

    [Fact]
    public async Task NonJsonBody_Throws()
    {
        var handler = new StubHandler(_ => StubHandler.Json("<html>bad request</html>", HttpStatusCode.BadRequest));
        await Assert.ThrowsAsync<HttpRequestException>(
            () => TestClient.Make(handler).GetAsync("artist.getInfo", []));
    }

    [Fact]
    public void BuildUrl_OmitsBlankValues_AndEscapes()
    {
        var url = TestClient.Make(new StubHandler(_ => StubHandler.Json("{}")))
            .BuildUrl("album.getInfo", [new("artist", "AC/DC"), new("album", "  "), new("mbid", null), new("lang", "en")]);

        Assert.Contains("method=album.getInfo", url);
        Assert.Contains("api_key=test-key", url);
        Assert.Contains("format=json", url);
        Assert.Contains("artist=AC%2FDC", url);
        Assert.Contains("lang=en", url);
        Assert.DoesNotContain("album=", url);
        Assert.DoesNotContain("mbid=", url);
    }
}
