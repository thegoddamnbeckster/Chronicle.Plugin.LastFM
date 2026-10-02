using System.Text.Json;
using Xunit;

namespace Chronicle.Plugin.LastFM.Tests;

public class LastFmJsonTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void List_NormalisesSingleObjectAndArray()
    {
        var single = Parse("""{"tag":{"name":"rock"}}""");
        var many   = Parse("""{"tag":[{"name":"rock"},{"name":"indie"}]}""");
        Assert.Equal(["rock"],          LastFmJson.TagNames(single));
        Assert.Equal(["rock", "indie"], LastFmJson.TagNames(many));
        Assert.Empty(LastFmJson.TagNames(Parse("""{"tag":""}""")));   // Last.fm sends "" when empty
        Assert.Empty(LastFmJson.TagNames(null));
    }

    [Fact]
    public void Str_ReadsNumbersAndTextNodes_AndBlanksBecomeNull()
    {
        var el = Parse("""{"n":123,"a":{"#text":"hello"},"blank":"  ","s":" x "}""");
        Assert.Equal("123", LastFmJson.Str(el, "n"));
        Assert.Equal("hello", LastFmJson.Str(el, "a"));
        Assert.Null(LastFmJson.Str(el, "blank"));
        Assert.Equal("x", LastFmJson.Str(el, "s"));
        Assert.Equal(123L, LastFmJson.Long(el, "n"));
    }

    [Fact]
    public void BestImage_PrefersLargest_AndRejectsThePlaceholder()
    {
        var album = Parse("""
            {"image":[
              {"#text":"https://x/small.png","size":"small"},
              {"#text":"https://x/extra.png","size":"extralarge"},
              {"#text":"https://x/mega.png","size":"mega"}]}
            """);
        Assert.Equal("https://x/mega.png", LastFmJson.BestImage(album));

        var placeholder = Parse("""
            {"image":[{"#text":"https://lastfm.freetls.fastly.net/i/u/300x300/2a96cbd8b46e442fc41c2b86b821562f.png","size":"extralarge"}]}
            """);
        Assert.Null(LastFmJson.BestImage(placeholder));
        Assert.Null(LastFmJson.BestImage(Parse("""{"image":[{"#text":"","size":"large"}]}""")));
    }

    [Fact]
    public void CleanWiki_StripsReadMoreLink_Licence_Tags_AndDecodesEntities()
    {
        var html = "Radiohead are an English rock band &amp; more.<br />Second line. " +
                   "<a href=\"https://www.last.fm/music/Radiohead\">Read more on Last.fm</a>";
        Assert.Equal("Radiohead are an English rock band & more.\nSecond line.", LastFmJson.CleanWiki(html));

        Assert.Equal("Body text.", LastFmJson.CleanWiki(
            "Body text. <a href=\"https://www.last.fm/music/X\">Read more on Last.fm</a>. " +
            "User-contributed text is available under the Creative Commons By-SA License; additional terms may apply."));

        Assert.Null(LastFmJson.CleanWiki("   "));
        Assert.Null(LastFmJson.CleanWiki("<a href=\"u\">Read more on Last.fm</a>"));
    }
}
