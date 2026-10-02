using Xunit;

namespace Chronicle.Plugin.LastFM.Tests;

public class LastFmIdsTests
{
    [Theory]
    [InlineData("AC/DC")]
    [InlineData("Panic! At The Disco")]
    [InlineData("Sigur Rós")]
    [InlineData("+/-")]
    public void Artist_RoundTrips(string name)
    {
        var parsed = LastFmIds.Parse(LastFmIds.Artist(name));
        Assert.Equal(new LastFmId(LastFmEntity.Artist, name, null, null), parsed);
    }

    [Fact]
    public void TypedArtistId_WithAnUnencodedSlash_IsTheWholeName()
    {
        Assert.Equal(new LastFmId(LastFmEntity.Artist, "AC/DC", null, null), LastFmIds.Parse("artist:AC/DC"));
    }

    [Fact]
    public void Album_RoundTrips_WithSlashesInBothNames()
    {
        var parsed = LastFmIds.Parse(LastFmIds.Album("AC/DC", "Back In Black / Live"));
        Assert.Equal(new LastFmId(LastFmEntity.Album, "AC/DC", "Back In Black / Live", null), parsed);
    }

    [Fact]
    public void Track_WithAndWithoutAlbum_RoundTrips()
    {
        Assert.Equal(new LastFmId(LastFmEntity.Track, "Radiohead", null, "Karma Police"),
            LastFmIds.Parse(LastFmIds.Track("Radiohead", null, "Karma Police")));
        Assert.Equal(new LastFmId(LastFmEntity.Track, "Radiohead", "OK Computer", "Karma Police"),
            LastFmIds.Parse(LastFmIds.Track("Radiohead", "OK Computer", "Karma Police")));
    }

    [Fact]
    public void SameTrackTitle_OnDifferentAlbums_GetDistinctIds()
    {
        Assert.NotEqual(
            LastFmIds.Track("Band", "Album One", "Intro"),
            LastFmIds.Track("Band", "Album Two", "Intro"));
    }

    [Theory]
    [InlineData("https://www.last.fm/music/Pink+Floyd", "Artist", "Pink Floyd", null, null)]
    [InlineData("https://www.last.fm/music/Pink+Floyd/The+Wall", "Album", "Pink Floyd", "The Wall", null)]
    [InlineData("https://www.last.fm/music/Pink+Floyd/_/Money", "Track", "Pink Floyd", null, "Money")]
    [InlineData("https://www.last.fm/de/music/Pink+Floyd", "Artist", "Pink Floyd", null, null)]
    [InlineData("https://www.last.fm/music/AC%2FDC", "Artist", "AC/DC", null, null)]
    [InlineData("https://www.last.fm/music/Beach+Boys%2B", "Artist", "Beach Boys+", null, null)]
    public void Parse_LastFmUrl(string url, string kind, string artist, string? album, string? track)
    {
        Assert.Equal(new LastFmId(Enum.Parse<LastFmEntity>(kind), artist, album, track), LastFmIds.Parse(url));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Radiohead")]
    [InlineData("artist:")]
    [InlineData("album:OnlyOne")]
    [InlineData("movie:550")]
    [InlineData("https://musicbrainz.org/artist/abc")]
    [InlineData("https://www.last.fm/user/someone")]
    public void Parse_RejectsGarbage(string input) => Assert.Null(LastFmIds.Parse(input));

    [Theory]
    [InlineData("artist:a74b1b7f-71a5-4011-9441-d0b5e4122711", "artist", "a74b1b7f-71a5-4011-9441-d0b5e4122711")]
    [InlineData("a74b1b7f-71a5-4011-9441-d0b5e4122711", "artist", "a74b1b7f-71a5-4011-9441-d0b5e4122711")]
    [InlineData("release-group:a74b1b7f-71a5-4011-9441-d0b5e4122711", "artist", null)]
    [InlineData("recording:a74b1b7f-71a5-4011-9441-d0b5e4122711", "artist", null)]
    [InlineData(null, "artist", null)]
    public void ExtractMbid_RespectsEntityType(string? raw, string prefix, string? expected) =>
        Assert.Equal(expected, LastFmIds.ExtractMbid(raw, prefix));
}
