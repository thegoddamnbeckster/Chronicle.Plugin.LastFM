using System.Text.Json;
using Chronicle.Plugins;

namespace Chronicle.Plugin.LastFM;

/// <summary>
/// Thread-safe Last.fm API 2.0 client with built-in pacing.
///
/// Last.fm reports almost every failure as HTTP 200 (or an HTTP 4xx with a JSON body) carrying
/// <c>{"error":N,"message":"..."}</c>, so the error code is read from the body, not the status:
///   6            -> not found ("Artist not found", "Track not found", ...): returns null
///   10, 26       -> invalid / suspended API key: <see cref="PluginAuthException"/>
///   8, 11, 16, 29 -> transient / rate limited: retried with backoff
///   anything else -> <see cref="HttpRequestException"/>
/// </summary>
internal sealed class LastFmClient : IDisposable
{
    internal const string PluginId = "chronicle.plugin.lastfm";
    private const string BaseUrl = "https://ws.audioscrobbler.com/2.0/";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly SemaphoreSlim _throttle = new(1, 1);
    private readonly TimeSpan _minInterval;
    private DateTime _lastRequest = DateTime.MinValue;

    public LastFmClient(string apiKey, string userAgent)
    {
        _apiKey      = apiKey;
        // Last.fm's terms allow roughly 5 requests/sec; stay comfortably under it.
        _minInterval = TimeSpan.FromMilliseconds(250);
        _http        = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.Add("User-Agent", userAgent);
    }

    /// <summary>Test-only constructor that accepts a pre-built HttpClient and pacing interval.</summary>
    internal LastFmClient(HttpClient http, string apiKey, TimeSpan minInterval)
    {
        _http        = http;
        _apiKey      = apiKey;
        _minInterval = minInterval;
    }

    /// <summary>
    /// Calls a Last.fm method and returns the parsed JSON root, or null when Last.fm says the
    /// requested entity does not exist (error 6). Null/blank parameter values are omitted.
    /// </summary>
    public async Task<JsonElement?> GetAsync(
        string method, IEnumerable<KeyValuePair<string, string?>> parameters, CancellationToken ct = default)
    {
        var url = BuildUrl(method, parameters);
        const int maxRetries = 4;
        var delay = TimeSpan.FromSeconds(2);

        for (int attempt = 0; ; attempt++)
        {
            await ThrottleAsync(ct).ConfigureAwait(false);

            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!TryParse(body, out var parsed))
            {
                // Not JSON at all (gateway page, HTML error). Transient on 5xx, fatal otherwise.
                if ((int)response.StatusCode >= 500 && attempt < maxRetries)
                {
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    delay = NextDelay(delay);
                    continue;
                }
                throw new HttpRequestException(
                    $"Last.fm returned a non-JSON response (HTTP {(int)response.StatusCode}) for {method}.");
            }

            if (!parsed.TryGetProperty("error", out var errEl))
            {
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException(
                        $"Last.fm returned HTTP {(int)response.StatusCode} for {method}.");
                return parsed;
            }

            var code = errEl.ValueKind == JsonValueKind.Number ? errEl.GetInt32() : -1;
            var message = parsed.TryGetProperty("message", out var m) ? m.GetString() : null;

            switch (code)
            {
                case 6:
                    return null;
                case 10:
                case 26:
                    throw new PluginAuthException(PluginId,
                        $"Last.fm rejected the API key (error {code}: {message}). " +
                        "Check the API key in the plugin settings.");
                case 8 or 11 or 16 or 29 when attempt < maxRetries:
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    delay = NextDelay(delay);
                    continue;
                default:
                    throw new HttpRequestException(
                        $"Last.fm error {code} for {method}: {message}");
            }
        }
    }

    /// <summary>Download raw image bytes (paced like any other request).</summary>
    public async Task<byte[]> GetBytesAsync(string url, CancellationToken ct = default)
    {
        await ThrottleAsync(ct).ConfigureAwait(false);
        return await _http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
    }

    internal string BuildUrl(string method, IEnumerable<KeyValuePair<string, string?>> parameters)
    {
        var sb = new System.Text.StringBuilder(BaseUrl);
        sb.Append("?method=").Append(Uri.EscapeDataString(method));
        sb.Append("&api_key=").Append(Uri.EscapeDataString(_apiKey));
        sb.Append("&format=json");
        foreach (var (key, value) in parameters)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            sb.Append('&').Append(Uri.EscapeDataString(key))
              .Append('=').Append(Uri.EscapeDataString(value));
        }
        return sb.ToString();
    }

    private static bool TryParse(string body, out JsonElement root)
    {
        root = default;
        try
        {
            using var doc = JsonDocument.Parse(body);
            root = doc.RootElement.Clone();
            return root.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static TimeSpan NextDelay(TimeSpan d) =>
        TimeSpan.FromSeconds(Math.Min(d.TotalSeconds * 2, 30));

    private async Task ThrottleAsync(CancellationToken ct)
    {
        await _throttle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var elapsed = DateTime.UtcNow - _lastRequest;
            if (elapsed < _minInterval)
                await Task.Delay(_minInterval - elapsed, ct).ConfigureAwait(false);
            _lastRequest = DateTime.UtcNow;
        }
        finally
        {
            _throttle.Release();
        }
    }

    public void Dispose()
    {
        _throttle.Dispose();
        _http.Dispose();
    }
}
