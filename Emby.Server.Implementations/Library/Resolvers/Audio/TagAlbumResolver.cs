#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Emby.Naming.Audio;
using Emby.Naming.Common;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Resolvers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.IO;

namespace Emby.Server.Implementations.Library.Resolvers.Audio
{
    /// <summary>
    /// Groups the audio files of a folder into albums by their embedded tags instead of by directory.
    /// Active only for music libraries with <see cref="LibraryOptions.EnableTagBasedAlbumGrouping"/>.
    /// </summary>
    /// <remarks>
    /// Every album found this way has the directory of its tracks as <see cref="BaseItem.Path"/>, is marked
    /// <see cref="BaseItem.IsInMixedFolder"/> because it shares that directory with the other albums found there,
    /// and gets a stable id derived from the directory, album artist and album title so rescans find it again.
    /// Files without an album tag are left to the other resolvers and stay loose tracks of the folder.
    /// </remarks>
    public class TagAlbumResolver : ItemResolver<MusicAlbum>, IMultiItemResolver
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IAudioTagReader _tagReader;
        private readonly NamingOptions _namingOptions;

        /// <summary>
        /// Initializes a new instance of the <see cref="TagAlbumResolver"/> class.
        /// </summary>
        /// <param name="libraryManager">The library manager.</param>
        /// <param name="tagReader">The audio tag reader.</param>
        /// <param name="namingOptions">The naming options.</param>
        public TagAlbumResolver(ILibraryManager libraryManager, IAudioTagReader tagReader, NamingOptions namingOptions)
        {
            _libraryManager = libraryManager;
            _tagReader = tagReader;
            _namingOptions = namingOptions;
        }

        /// <inheritdoc />
        public override ResolverPriority Priority => ResolverPriority.Third;

        /// <summary>
        /// Gets a value indicating whether albums are grouped by tags for the given library and collection type.
        /// </summary>
        /// <param name="libraryOptions">The library options.</param>
        /// <param name="collectionType">The collection type.</param>
        /// <returns><c>true</c> if tag-based album grouping applies.</returns>
        public static bool IsEnabled(LibraryOptions libraryOptions, CollectionType? collectionType)
        {
            return collectionType == CollectionType.music && libraryOptions.EnableTagBasedAlbumGrouping;
        }

        /// <summary>
        /// Gets the stable id of the tag-grouped album described by <paramref name="tags"/> inside <paramref name="directory"/>.
        /// </summary>
        /// <param name="libraryManager">The library manager.</param>
        /// <param name="directory">The directory holding the album's tracks.</param>
        /// <param name="tags">The album tags of a track.</param>
        /// <returns>The album id.</returns>
        public static Guid GetAlbumId(ILibraryManager libraryManager, string directory, AudioAlbumTags tags)
        {
            // GetNewItemId normalizes the key's case unless case-sensitive ids are enabled.
            var key = string.Join('\n', directory, tags.AlbumArtist, tags.Album);
            return libraryManager.GetNewItemId(key, typeof(MusicAlbum));
        }

        /// <summary>
        /// Gets the id of a track inside a tag-grouped album. It is scoped to the album so that a retagged file
        /// becomes a new item under its new album instead of one item fought over by two albums.
        /// </summary>
        /// <param name="libraryManager">The library manager.</param>
        /// <param name="albumId">The id of the album the track belongs to.</param>
        /// <param name="path">The path of the track.</param>
        /// <returns>The track id.</returns>
        public static Guid GetTrackId(ILibraryManager libraryManager, Guid albumId, string path)
        {
            var key = albumId.ToString("N", CultureInfo.InvariantCulture) + '\n' + path;
            return libraryManager.GetNewItemId(key, typeof(MediaBrowser.Controller.Entities.Audio.Audio));
        }

        /// <summary>
        /// Reads the album tags of a file, or <c>null</c> when it is not an audio file or has no album tag.
        /// </summary>
        /// <param name="tagReader">The audio tag reader.</param>
        /// <param name="namingOptions">The naming options.</param>
        /// <param name="path">The file path.</param>
        /// <param name="libraryOptions">The library options.</param>
        /// <returns>The album tags, or <c>null</c>.</returns>
        public static AudioAlbumTags ReadAlbumTags(IAudioTagReader tagReader, NamingOptions namingOptions, string path, LibraryOptions libraryOptions)
        {
            if (!AudioFileParser.IsAudioFile(path, namingOptions)
                || Path.GetExtension(path.AsSpan()).Equals(".cue", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return tagReader.ReadAlbumTags(path, libraryOptions);
        }

        /// <inheritdoc />
        public MultiItemResolverResult ResolveMultiple(
            Folder parent,
            List<FileSystemMetadata> files,
            CollectionType? collectionType,
            IDirectoryService directoryService)
        {
            // The tracks of an album are resolved one by one by the AudioResolver.
            if (parent is MusicAlbum)
            {
                return null;
            }

            var libraryOptions = _libraryManager.GetLibraryOptions(parent);
            if (!IsEnabled(libraryOptions, collectionType))
            {
                return null;
            }

            var result = new MultiItemResolverResult();
            var albums = new Dictionary<Guid, MusicAlbum>();

            foreach (var file in files)
            {
                var tags = file.IsDirectory ? null : ReadAlbumTags(_tagReader, _namingOptions, file.FullName, libraryOptions);
                if (tags is null)
                {
                    result.ExtraFiles.Add(file);
                    continue;
                }

                var directory = Path.GetDirectoryName(file.FullName);
                var albumId = GetAlbumId(_libraryManager, directory, tags);
                if (!albums.ContainsKey(albumId))
                {
                    albums.Add(albumId, new MusicAlbum
                    {
                        Id = albumId,
                        Name = tags.Album,
                        AlbumArtists = string.IsNullOrEmpty(tags.AlbumArtist) ? [] : [tags.AlbumArtist],
                        Path = directory,
                        IsInMixedFolder = true
                    });
                }
            }

            result.Items.AddRange(albums.Values);
            return result;
        }

        /// <inheritdoc />
        protected override MusicAlbum Resolve(ItemResolveArgs args)
        {
            // Albums are only produced from the files of a folder, see ResolveMultiple.
            return null;
        }
    }
}
