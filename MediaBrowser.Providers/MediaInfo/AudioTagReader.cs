using System;
using System.Collections.Generic;
using System.Linq;
using ATL;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Extensions;
using Microsoft.Extensions.Logging;

namespace MediaBrowser.Providers.MediaInfo;

/// <summary>
/// Reads audio tags with ATL, applying the artist tag rules shared with <see cref="AudioFileProber"/>.
/// </summary>
public class AudioTagReader : IAudioTagReader
{
    /// <summary>
    /// The separator ATL joins multi-value tags with.
    /// </summary>
    public const char InternalValueSeparator = '\u001F';

    private readonly ILogger<AudioTagReader> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AudioTagReader"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public AudioTagReader(ILogger<AudioTagReader> logger)
    {
        _logger = logger;
        ConfigureAtl();
    }

    /// <summary>
    /// Applies the global ATL settings Jellyfin relies on when reading tags.
    /// </summary>
    public static void ConfigureAtl()
    {
        Settings.DisplayValueSeparator = InternalValueSeparator;
        Settings.UseFileNameWhenNoTitle = false;
        Settings.ID3v2_separatev2v3Values = false;
    }

    /// <inheritdoc />
    public AudioAlbumTags? ReadAlbumTags(string path, LibraryOptions libraryOptions)
    {
        Track track;
        try
        {
            track = new Track(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the tags of {Path}", path);
            return null;
        }

        var album = track.Album?.TruncateAtNull().Trim();
        if (string.IsNullOrEmpty(album))
        {
            return null;
        }

        var albumArtists = GetArtistValues(track, "ALBUMARTISTS", track.AlbumArtist, libraryOptions);
        if (albumArtists.Length == 0)
        {
            albumArtists = GetArtistValues(track, "ARTISTS", track.Artist, libraryOptions);
        }

        return new AudioAlbumTags(album, albumArtists.Length == 0 ? string.Empty : albumArtists[0].Trim());
    }

    /// <summary>
    /// Gets the values of an artist tag: the non-standard multi-value field when the library prefers it and the
    /// file has it, otherwise the standard tag, both split on ATL's separator and the library's custom delimiters.
    /// </summary>
    /// <param name="track">The track whose tags are read.</param>
    /// <param name="nonStandardField">The name of the non-standard multi-value field, e.g. <c>ALBUMARTISTS</c>.</param>
    /// <param name="standardValue">The value of the standard tag.</param>
    /// <param name="libraryOptions">The library options.</param>
    /// <returns>The artist values; empty when the tag is absent.</returns>
    public static string[] GetArtistValues(Track track, string nonStandardField, string? standardValue, LibraryOptions libraryOptions)
    {
        string[]? values = null;
        if (libraryOptions.PreferNonstandardArtistsTag
            && TryGetAdditionalField(track, nonStandardField, out var nonStandardValue)
            && !string.IsNullOrEmpty(nonStandardValue))
        {
            values = nonStandardValue.TruncateAtNull().Split(InternalValueSeparator);
        }

        if (values is null || values.Length == 0)
        {
            var sanitized = standardValue?.TruncateAtNull();
            values = string.IsNullOrEmpty(sanitized) ? [] : sanitized.Split(InternalValueSeparator);
        }

        if (libraryOptions.UseCustomTagDelimiters)
        {
            values = values.SelectMany(v => SplitWithCustomDelimiter(v, libraryOptions.GetCustomTagDelimiters(), libraryOptions.DelimiterWhitelist)).ToArray();
        }

        return values;
    }

    /// <summary>
    /// Splits a tag value on the custom delimiters, keeping allowlisted names that contain a delimiter whole.
    /// </summary>
    /// <param name="val">The tag value.</param>
    /// <param name="tagDelimiters">The delimiters to split on.</param>
    /// <param name="whitelist">The names that must not be split.</param>
    /// <returns>The split values.</returns>
    public static IReadOnlyList<string> SplitWithCustomDelimiter(string val, char[] tagDelimiters, string[] whitelist)
    {
        var items = new List<string>();
        var temp = val;
        foreach (var whitelistItem in whitelist)
        {
            if (string.IsNullOrWhiteSpace(whitelistItem))
            {
                continue;
            }

            var originalTemp = temp;
            temp = temp.Replace(whitelistItem, string.Empty, StringComparison.OrdinalIgnoreCase);

            if (!string.Equals(temp, originalTemp, StringComparison.OrdinalIgnoreCase))
            {
                items.Add(whitelistItem);
            }
        }

        var items2 = temp.Split(tagDelimiters, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).DistinctNames();
        items.AddRange(items2);

        return items;
    }

    /// <summary>
    /// Gets an additional (non-standard) field, trying the key as-is first and then the mka-style
    /// <c>track.</c>-prefixed lower-case key (e.g. <c>ARTISTS</c> → <c>track.artists</c>).
    /// </summary>
    /// <param name="track">The track whose tags are read.</param>
    /// <param name="key">The field name.</param>
    /// <param name="value">The field value when found.</param>
    /// <returns><c>true</c> if the field exists under either key.</returns>
    public static bool TryGetAdditionalField(Track track, string key, out string? value)
    {
        var fields = track.AdditionalFields;
        if (fields is null)
        {
            value = null;
            return false;
        }

        if (fields.TryGetValue(key, out value))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(key))
        {
            var fallbackKey = "track." + key.Trim().Replace(' ', '_').ToLowerInvariant();
            if (fields.TryGetValue(fallbackKey, out value))
            {
                return true;
            }
        }

        value = null;
        return false;
    }
}
