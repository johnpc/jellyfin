#nullable disable

#pragma warning disable CA1721, CA1826, CS1591

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Querying;
using MetadataProvider = MediaBrowser.Model.Entities.MetadataProvider;

namespace MediaBrowser.Controller.Entities.Audio
{
    /// <summary>
    /// Class MusicAlbum.
    /// </summary>
    [Common.RequiresSourceSerialisation]
    public class MusicAlbum : Folder, IHasAlbumArtist, IHasArtist, IHasMusicGenres, IHasLookupInfo<AlbumInfo>, IMetadataContainer
    {
        public MusicAlbum()
        {
            Artists = Array.Empty<string>();
            AlbumArtists = Array.Empty<string>();
        }

        /// <inheritdoc />
        public IReadOnlyList<string> AlbumArtists { get; set; }

        /// <inheritdoc />
        public IReadOnlyList<string> Artists { get; set; }

        [JsonIgnore]
        public override bool SupportsAddingToPlaylist => true;

        [JsonIgnore]
        public override bool SupportsInheritedParentImages => true;

        [JsonIgnore]
        public MusicArtist MusicArtist => GetMusicArtist(new DtoOptions(true));

        [JsonIgnore]
        public override bool SupportsPlayedStatus => false;

        [JsonIgnore]
        public override bool SupportsCumulativeRunTimeTicks => true;

        [JsonIgnore]
        public string AlbumArtist => AlbumArtists.FirstOrDefault();

        [JsonIgnore]
        public override bool SupportsPeople => true;

        /// <summary>
        /// Gets the tracks.
        /// </summary>
        /// <value>The tracks.</value>
        [JsonIgnore]
        public IEnumerable<Audio> Tracks => GetRecursiveChildren(i => i is Audio).Cast<Audio>();

        /// <summary>
        /// Gets a value indicating whether local metadata applies. An album grouped by tags shares its directory
        /// with other albums, so nfo files and folder images there cannot be attributed to it.
        /// </summary>
        [JsonIgnore]
        public override bool SupportsLocalMetadata => !IsInMixedFolder && base.SupportsLocalMetadata;

        /// <summary>
        /// Gets a value indicating whether this album was grouped from the tags of files in a shared directory.
        /// </summary>
        [JsonIgnore]
        private bool IsGroupedByTags => IsInMixedFolder && LibraryManager.GetLibraryOptions(this).EnableTagBasedAlbumGrouping;

        /// <inheritdoc />
        public override bool IsSaveLocalMetadataEnabled()
        {
            return !IsInMixedFolder && base.IsSaveLocalMetadataEnabled();
        }

        /// <inheritdoc />
        protected override FileSystemMetadata[] GetFileSystemChildren(IDirectoryService directoryService)
        {
            var children = base.GetFileSystemChildren(directoryService);

            // The sub-directories of a shared directory belong to the parent folder, not to the albums grouped from its files.
            return IsInMixedFolder ? children.Where(i => !i.IsDirectory).ToArray() : children;
        }

        /// <inheritdoc />
        protected override IEnumerable<BaseItem> GetNonCachedChildren(IDirectoryService directoryService)
        {
            var children = base.GetNonCachedChildren(directoryService);

            // An album grouped by tags only ever owns its tracks; the AudioResolver keeps the other albums' tracks out.
            return IsInMixedFolder ? children.Where(i => i is Audio) : children;
        }

        public MusicArtist GetMusicArtist(DtoOptions options)
        {
            var parents = GetParents();
            foreach (var parent in parents)
            {
                if (parent is MusicArtist artist)
                {
                    return artist;
                }
            }

            var name = AlbumArtist;
            if (!string.IsNullOrEmpty(name))
            {
                return LibraryManager.GetArtist(name, options);
            }

            return null;
        }

        protected override IEnumerable<BaseItem> GetEligibleChildrenForRecursiveChildren(User user)
        {
            return Tracks;
        }

        /// <summary>
        /// When albums are grouped by tags, the halves of an album spread over several directories (disc folders,
        /// several library roots) share a presentation key so that listings collapse them into one album and the
        /// album's track queries span all of them, mirroring <c>EnableAutomaticSeriesGrouping</c>.
        /// </summary>
        /// <returns>The presentation key.</returns>
        public override string CreatePresentationUniqueKey()
        {
            if (!IsGroupedByTags)
            {
                return base.CreatePresentationUniqueKey();
            }

            // Same identity order as GetUserDataKeys: MusicBrainz release group, MusicBrainz release, album artist + title.
            var key = this.GetProviderId(MetadataProvider.MusicBrainzReleaseGroup) is { Length: > 0 } releaseGroup
                ? "musicbrainz-releasegroup-" + releaseGroup
                : this.GetProviderId(MetadataProvider.MusicBrainzAlbum) is { Length: > 0 } release
                    ? "musicbrainz-release-" + release
                    : (AlbumArtist ?? string.Empty) + "-" + Name;
            key = "album-" + key.ToLowerInvariant();

            var folders = LibraryManager.GetCollectionFolders(this)
                .Select(i => i.Id.ToString("N", CultureInfo.InvariantCulture))
                .Order(StringComparer.Ordinal)
                .ToArray();

            return folders.Length == 0 ? key : key + "-" + string.Join('-', folders);
        }

        /// <inheritdoc />
        public override IReadOnlyList<BaseItem> GetChildren(User user, bool includeLinkedChildren, out int totalItemCount, InternalItemsQuery query = null)
        {
            if (!IsGroupedByTags)
            {
                return base.GetChildren(user, includeLinkedChildren, out totalItemCount, query);
            }

            var tracks = LibraryManager.GetItemList(GetGroupedTracksQuery(new InternalItemsQuery(user)
            {
                DtoOptions = query?.DtoOptions ?? new DtoOptions(true)
            }));
            totalItemCount = tracks.Count;
            return tracks;
        }

        /// <inheritdoc />
        protected override QueryResult<BaseItem> GetItemsInternal(InternalItemsQuery query)
        {
            if (!IsGroupedByTags || SourceType == SourceType.Channel)
            {
                return base.GetItemsInternal(query);
            }

            return LibraryManager.GetItemsResult(GetGroupedTracksQuery(query));
        }

        private InternalItemsQuery GetGroupedTracksQuery(InternalItemsQuery query)
        {
            query.Parent = null;
            query.ParentId = Guid.Empty;
            query.AncestorWithPresentationUniqueKey = GetPresentationUniqueKey();
            if (query.IncludeItemTypes.Length == 0)
            {
                query.IncludeItemTypes = [BaseItemKind.Audio];
            }

            if (query.OrderBy.Count == 0)
            {
                query.OrderBy = [(ItemSortBy.ParentIndexNumber, SortOrder.Ascending), (ItemSortBy.IndexNumber, SortOrder.Ascending), (ItemSortBy.SortName, SortOrder.Ascending)];
            }

            return query;
        }

        public override double GetDefaultPrimaryImageAspectRatio()
        {
            return 1;
        }

        public override List<string> GetUserDataKeys()
        {
            var list = base.GetUserDataKeys();

            var albumArtist = AlbumArtist;
            if (!string.IsNullOrEmpty(albumArtist))
            {
                list.Insert(0, albumArtist + "-" + Name);
            }

            var id = this.GetProviderId(MetadataProvider.MusicBrainzAlbum);

            if (!string.IsNullOrEmpty(id))
            {
                list.Insert(0, "MusicAlbum-Musicbrainz-" + id);
            }

            id = this.GetProviderId(MetadataProvider.MusicBrainzReleaseGroup);

            if (!string.IsNullOrEmpty(id))
            {
                list.Insert(0, "MusicAlbum-MusicBrainzReleaseGroup-" + id);
            }

            return list;
        }

        protected override bool GetBlockUnratedValue(User user)
        {
            return user.GetPreferenceValues<UnratedItem>(PreferenceKind.BlockUnratedItems).Contains(UnratedItem.Music);
        }

        public override UnratedItem GetBlockUnratedType()
        {
            return UnratedItem.Music;
        }

        public AlbumInfo GetLookupInfo()
        {
            var id = GetItemLookupInfo<AlbumInfo>();

            id.AlbumArtists = AlbumArtists;

            var artist = GetMusicArtist(new DtoOptions(false));

            if (artist is not null)
            {
                id.ArtistProviderIds = artist.ProviderIds;
            }

            id.SongInfos = GetRecursiveChildren(i => i is Audio)
                .Cast<Audio>()
                .Select(i => i.GetLookupInfo())
                .ToList();

            var album = id.SongInfos
                .Select(i => i.Album)
                .FirstOrDefault(i => !string.IsNullOrEmpty(i));

            if (!string.IsNullOrEmpty(album))
            {
                id.Name = album;
            }

            return id;
        }

        public async Task RefreshAllMetadata(MetadataRefreshOptions refreshOptions, IProgress<double> progress, CancellationToken cancellationToken)
        {
            try
            {
                await RefreshAllMetadataInternal(refreshOptions, progress, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ReleaseCachedChildren();
            }
        }

        private async Task RefreshAllMetadataInternal(MetadataRefreshOptions refreshOptions, IProgress<double> progress, CancellationToken cancellationToken)
        {
            var items = GetRecursiveChildren();

            var totalItems = items.Count;
            var numComplete = 0;

            var childUpdateType = ItemUpdateType.None;

            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var updateType = await item.RefreshMetadata(refreshOptions, cancellationToken).ConfigureAwait(false);
                childUpdateType = childUpdateType | updateType;

                numComplete++;
                double percent = numComplete;
                percent /= totalItems;
                progress.Report(percent * 95);
            }

            var parentRefreshOptions = refreshOptions;
            if (childUpdateType > ItemUpdateType.None)
            {
                parentRefreshOptions = new MetadataRefreshOptions(refreshOptions)
                {
                    MetadataRefreshMode = MetadataRefreshMode.FullRefresh
                };
            }

            // Refresh current item
            await RefreshMetadata(parentRefreshOptions, cancellationToken).ConfigureAwait(false);

            if (!refreshOptions.IsAutomated)
            {
                await RefreshArtists(refreshOptions, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task RefreshArtists(MetadataRefreshOptions refreshOptions, CancellationToken cancellationToken)
        {
            foreach (var i in this.GetAllArtists())
            {
                // This should not be necessary but we're seeing some cases of it
                if (string.IsNullOrEmpty(i))
                {
                    continue;
                }

                var artist = LibraryManager.GetArtist(i);

                if (!artist.IsAccessedByName)
                {
                    continue;
                }

                await artist.RefreshMetadata(refreshOptions, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
