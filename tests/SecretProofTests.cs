using Xunit;
using System.Net;

namespace Chronicle.Plugin.LastFM.Tests;

/// <summary>The checklist's K7: a call that fails on purpose must not carry the API key out in its error text.</summary>
public class SecretProofTests
{
    private const string Key = "SECRET-KEY-1234567890";

    internal sealed class ProofHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(answer(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public async Task A_rejected_key_is_an_auth_error_that_does_not_echo_it()
    {
        var client = new Chronicle.Plugin.LastFM.LastFmClient(new HttpClient(new ProofHandler(_ => Json("{\"error\":10,\"message\":\"Invalid API key\"}"))), Key, TimeSpan.Zero);
        var ex = await Record.ExceptionAsync(() => client.GetAsync("artist.getinfo", [new("artist", "x")]));
        Assert.IsType<Chronicle.Plugins.PluginAuthException>(ex);
        Assert.DoesNotContain(Key, ex!.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_refusal_that_is_not_retried_does_not_echo_the_key(HttpStatusCode status)
    {
        var client = new Chronicle.Plugin.LastFM.LastFmClient(new HttpClient(new ProofHandler(_ => Json("not json", status))), Key, TimeSpan.Zero);
        var ex = await Record.ExceptionAsync(() => client.GetAsync("artist.getinfo", [new("artist", "x")]));
        Assert.NotNull(ex);
        Assert.DoesNotContain(Key, ex!.ToString());
    }

    [Fact]
    public async Task A_rate_limit_that_never_clears_is_reported_as_one_with_its_status()
    {
        var h = new ProofHandler(_ => { var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests); r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1)); return r; });
        var client = new Chronicle.Plugin.LastFM.LastFmClient(new HttpClient(h), Key, TimeSpan.Zero);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("artist.getinfo", [new("artist", "x")]));
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.DoesNotContain(Key, ex.ToString());
        Assert.True(h.Calls <= 6, $"asked {h.Calls} times");
    }
}
