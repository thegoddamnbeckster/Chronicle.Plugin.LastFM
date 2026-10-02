using System.Net;
using Chronicle.Plugin.LastFM;

namespace Chronicle.Plugin.LastFM.Tests;

/// <summary>Scripted HTTP handler that records every request and answers via a callback.</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];
    public int CallCount => Requests.Count;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request.RequestUri!);
        return Task.FromResult(respond(request));
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body) };

    public static string MethodOf(HttpRequestMessage r) =>
        System.Web.HttpUtility.ParseQueryString(r.RequestUri!.Query)["method"] ?? "";

    public static string ParamOf(HttpRequestMessage r, string name) =>
        System.Web.HttpUtility.ParseQueryString(r.RequestUri!.Query)[name] ?? "";
}

internal static class TestClient
{
    public static LastFmClient Make(StubHandler handler) =>
        new(new HttpClient(handler), "test-key", TimeSpan.FromMilliseconds(1));

    public static LastFmMetadataProvider Provider(StubHandler handler) => new(Make(handler));
}
