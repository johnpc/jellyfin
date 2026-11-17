using System.Collections.Generic;
using ATL;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Providers.MediaInfo;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Providers.Tests.MediaInfo;

public class AudioTagReaderTests
{
    private const char Sep = AudioTagReader.InternalValueSeparator;

    [Fact]
    public void GetArtistValues_StandardTag_SplitOnAtlSeparator()
    {
        var values = AudioTagReader.GetArtistValues(new Track(), "ALBUMARTISTS", $"Adele{Sep}Chris Stapleton", new LibraryOptions());

        Assert.Equal(["Adele", "Chris Stapleton"], values);
    }

    [Fact]
    public void GetArtistValues_MissingTag_Empty()
    {
        Assert.Empty(AudioTagReader.GetArtistValues(new Track(), "ALBUMARTISTS", null, new LibraryOptions()));
        Assert.Empty(AudioTagReader.GetArtistValues(new Track(), "ALBUMARTISTS", string.Empty, new LibraryOptions()));
    }

    [Fact]
    public void GetArtistValues_PrefersNonStandardTagOnlyWhenAskedAndPresent()
    {
        var track = new Track { AdditionalFields = new Dictionary<string, string> { ["ALBUMARTISTS"] = $"Milky Chance{Sep}Adele" } };

        Assert.Equal(["Standard"], AudioTagReader.GetArtistValues(track, "ALBUMARTISTS", "Standard", new LibraryOptions()));
        Assert.Equal(["Milky Chance", "Adele"], AudioTagReader.GetArtistValues(track, "ALBUMARTISTS", "Standard", new LibraryOptions { PreferNonstandardArtistsTag = true }));
        Assert.Equal(["Standard"], AudioTagReader.GetArtistValues(new Track(), "ALBUMARTISTS", "Standard", new LibraryOptions { PreferNonstandardArtistsTag = true }));
    }

    [Fact]
    public void GetArtistValues_CustomDelimiters_SplitAndKeepAllowlistedNames()
    {
        var options = new LibraryOptions
        {
            UseCustomTagDelimiters = true,
            CustomTagDelimiters = [";", "/"],
            DelimiterWhitelist = ["AC/DC"]
        };

        var values = AudioTagReader.GetArtistValues(new Track(), "ARTISTS", "AC/DC; Adele / Dolly Parton", options);

        Assert.Equal(["AC/DC", "Adele", "Dolly Parton"], values);
    }

    [Fact]
    public void GetArtistValues_NullCharacter_TruncatesLikeTheProber()
    {
        Assert.Equal(["Adele"], AudioTagReader.GetArtistValues(new Track(), "ARTISTS", "Adele\0garbage", new LibraryOptions()));
    }

    [Fact]
    public void TryGetAdditionalField_FallsBackToMkaStyleKey()
    {
        var track = new Track { AdditionalFields = new Dictionary<string, string> { ["track.musicbrainz_artist_id"] = "mbid" } };

        Assert.True(AudioTagReader.TryGetAdditionalField(track, "MusicBrainz Artist Id", out var value));
        Assert.Equal("mbid", value);
        Assert.False(AudioTagReader.TryGetAdditionalField(track, "ARTISTS", out _));
    }

    [Fact]
    public void ReadAlbumTags_UnreadableFile_Null()
    {
        var reader = new AudioTagReader(NullLogger<AudioTagReader>.Instance);

        Assert.Null(reader.ReadAlbumTags("/definitely/not/here.mp3", new LibraryOptions()));
    }
}
