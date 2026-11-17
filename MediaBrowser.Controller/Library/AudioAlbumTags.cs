namespace MediaBrowser.Controller.Library;

/// <summary>
/// The embedded tags that identify the album an audio file belongs to.
/// </summary>
/// <param name="Album">The album title.</param>
/// <param name="AlbumArtist">The first album artist, falling back to the first track artist; empty when neither is tagged.</param>
public record AudioAlbumTags(string Album, string AlbumArtist);
