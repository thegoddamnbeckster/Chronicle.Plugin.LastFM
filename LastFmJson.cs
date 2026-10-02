using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Chronicle.Plugin.LastFM;

/// <summary>
/// Tolerant readers for Last.fm's JSON, which is a direct XML-to-JSON conversion and so has
/// quirks: a list with exactly one entry collapses to a bare object, text lives under "#text",
/// attributes under "@attr", and every number is a string.
/// </summary>
internal static class LastFmJson
{
    /// <summary>Last.fm's grey-star placeholder, returned for every artist image since 2019
    /// (and for any entity with no art). It is never a real picture.</summary>
    private const string PlaceholderImageHash = "2a96cbd8b46e442fc41c2b86b821562f";

    public static JsonElement? Obj(JsonElement? parent, string name) =>
        parent is { ValueKind: JsonValueKind.Object } p
        && p.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.Object
            ? v
            : null;

    public static string? Str(JsonElement? parent, string name)
    {
        if (parent is not { ValueKind: JsonValueKind.Object } p || !p.TryGetProperty(name, out var v))
            return null;
        var s = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            // {"#text": "...", "name": ...} -- e.g. an <artist> that carries both text and attributes
            JsonValueKind.Object when v.TryGetProperty("#text", out var t) => t.GetString(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    public static long? Long(JsonElement? parent, string name) =>
        long.TryParse(Str(parent, name), out var n) ? n : null;

    /// <summary>The elements of <c>parent[name]</c>, whether Last.fm sent an array or collapsed a
    /// single entry to a bare object. Empty when absent.</summary>
    public static IReadOnlyList<JsonElement> List(JsonElement? parent, string name)
    {
        if (parent is not { ValueKind: JsonValueKind.Object } p || !p.TryGetProperty(name, out var v))
            return [];
        return v.ValueKind switch
        {
            JsonValueKind.Array  => v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).ToList(),
            JsonValueKind.Object => [v],
            _                    => [],
        };
    }

    /// <summary>Tag names from a <c>{"tag": [...]}</c> container, in Last.fm's own order.</summary>
    public static List<string> TagNames(JsonElement? container) =>
        List(container, "tag")
            .Select(t => Str(t, "name"))
            .Where(n => n is not null)
            .Select(n => n!)
            .ToList();

    /// <summary>
    /// The best real image from a Last.fm <c>image</c> array (<c>[{"#text": url, "size": ...}]</c>),
    /// largest first, or null when there is none or only the placeholder.
    /// </summary>
    public static string? BestImage(JsonElement? parent)
    {
        string[] order = ["mega", "extralarge", "large", "medium", "small"];
        var images = List(parent, "image");
        foreach (var size in order)
        {
            foreach (var img in images)
            {
                if (!string.Equals(Str(img, "size"), size, StringComparison.OrdinalIgnoreCase)) continue;
                var url = Str(img, "#text");
                if (IsRealImage(url)) return url;
            }
        }
        return null;
    }

    public static bool IsRealImage(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && !url.Contains(PlaceholderImageHash, StringComparison.OrdinalIgnoreCase);

    private static readonly Regex AnchorTail =
        new(@"\s*<a\s+href=""[^""]*"">\s*Read more on Last\.fm\s*</a>\.?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LicenceTail =
        new(@"\s*User-contributed text is available under the Creative Commons By-SA License[^\n]*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HtmlTag = new(@"<[^>]+>", RegexOptions.Compiled);

    /// <summary>
    /// Turns a Last.fm wiki/bio fragment into plain text: drops the trailing "Read more on
    /// Last.fm" link and the Creative Commons notice, converts &lt;br&gt; to newlines, strips any
    /// remaining tags, and decodes entities. Returns null when nothing is left.
    /// </summary>
    public static string? CleanWiki(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        // Licence first: in the full "content" field it trails the "Read more" link, which is
        // only at the very end once the licence line is gone.
        var s = LicenceTail.Replace(html, string.Empty);
        s = AnchorTail.Replace(s, string.Empty);
        s = Regex.Replace(s, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        s = HtmlTag.Replace(s, string.Empty);
        s = WebUtility.HtmlDecode(s).Trim();
        return s.Length == 0 ? null : s;
    }
}
