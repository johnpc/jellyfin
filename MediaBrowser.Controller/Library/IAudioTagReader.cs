using MediaBrowser.Model.Configuration;

namespace MediaBrowser.Controller.Library;

/// <summary>
/// Reads the embedded tags that place an audio file into an album.
/// </summary>
public interface IAudioTagReader
{
    /// <summary>
    /// Reads the album and album artist of an audio file, applying the artist tag options of its library.
    /// </summary>
    /// <param name="path">The path of the audio file.</param>
    /// <param name="libraryOptions">The options of the library the file belongs to.</param>
    /// <returns>The album tags, or <c>null</c> when the file has no album tag or its tags cannot be read.</returns>
    AudioAlbumTags? ReadAlbumTags(string path, LibraryOptions libraryOptions);
}
