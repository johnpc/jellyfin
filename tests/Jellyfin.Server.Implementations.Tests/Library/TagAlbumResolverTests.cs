using System;
using System.Collections.Generic;
using System.Linq;
using Emby.Naming.Common;
using Emby.Server.Implementations.Library.Resolvers.Audio;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.IO;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library;

public class TagAlbumResolverTests
{
    private const string Dir = "/music/spotdl/Chill Mix";
    private static readonly NamingOptions _namingOptions = new();

    private static readonly Dictionary<string, AudioAlbumTags?> _tags = new(StringComparer.Ordinal)
    {
        [$"{Dir}/Adele - Easy On Me.mp3"] = new AudioAlbumTags("30", "Adele"),
        [$"{Dir}/Adele - Oh My God.mp3"] = new AudioAlbumTags("30", "Adele"),
        [$"{Dir}/Milky Chance - Stolen Dance.mp3"] = new AudioAlbumTags("Sadnecessary", "Milky Chance"),
        [$"{Dir}/Unknown - Demo.mp3"] = null,
        ["/music/other/Adele - Easy On Me.mp3"] = new AudioAlbumTags("30", "Adele"),
    };

    private static ILibraryManager CreateLibraryManager(bool groupByTags)
    {
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(l => l.GetLibraryOptions(It.IsAny<BaseItem>()))
            .Returns(new LibraryOptions { EnableTagBasedAlbumGrouping = groupByTags });
        libraryManager.Setup(l => l.GetNewItemId(It.IsAny<string>(), It.IsAny<Type>()))
            .Returns((string key, Type type) => (type.FullName + key.ToLowerInvariant()).GetMD5());
        return libraryManager.Object;
    }

    private static IAudioTagReader CreateTagReader()
    {
        var reader = new Mock<IAudioTagReader>();
        reader.Setup(r => r.ReadAlbumTags(It.IsAny<string>(), It.IsAny<LibraryOptions>()))
            .Returns((string path, LibraryOptions _) => _tags.GetValueOrDefault(path));
        return reader.Object;
    }

    private static TagAlbumResolver CreateResolver(bool groupByTags = true)
        => new(CreateLibraryManager(groupByTags), CreateTagReader(), _namingOptions);

    private static List<FileSystemMetadata> MixedFolderFiles()
    {
        var files = _tags.Keys.Where(p => p.StartsWith(Dir, StringComparison.Ordinal))
            .Select(p => new FileSystemMetadata { FullName = p, Name = p[(Dir.Length + 1)..] })
            .ToList();
        files.Add(new FileSystemMetadata { FullName = $"{Dir}/cover.jpg", Name = "cover.jpg" });
        files.Add(new FileSystemMetadata { FullName = $"{Dir}/Bonus", Name = "Bonus", IsDirectory = true });
        return files;
    }

    [Fact]
    public void ResolveMultiple_MixedFolder_OneAlbumPerAlbumTag()
    {
        var result = CreateResolver().ResolveMultiple(new Folder { Path = Dir }, MixedFolderFiles(), CollectionType.music, Mock.Of<IDirectoryService>());

        Assert.NotNull(result);
        var albums = result.Items.Cast<MusicAlbum>().OrderBy(a => a.Name, StringComparer.Ordinal).ToList();
        Assert.Collection(
            albums,
            a =>
            {
                Assert.Equal("30", a.Name);
                Assert.Equal(["Adele"], a.AlbumArtists);
            },
            a =>
            {
                Assert.Equal("Sadnecessary", a.Name);
                Assert.Equal(["Milky Chance"], a.AlbumArtists);
            });
        Assert.All(albums, a =>
        {
            Assert.Equal(Dir, a.Path);
            Assert.True(a.IsInMixedFolder);
            Assert.NotEqual(Guid.Empty, a.Id);
        });
        Assert.NotEqual(albums[0].Id, albums[1].Id);
    }

    [Fact]
    public void ResolveMultiple_MixedFolder_LeavesUntaggedAndNonAudioToOtherResolvers()
    {
        var result = CreateResolver().ResolveMultiple(new Folder { Path = Dir }, MixedFolderFiles(), CollectionType.music, Mock.Of<IDirectoryService>());

        Assert.NotNull(result);
        Assert.Equal(
            ["Bonus", "Unknown - Demo.mp3", "cover.jpg"],
            result.ExtraFiles.Select(f => f.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ResolveMultiple_SameAlbumInTwoDirectories_DistinctStableIds()
    {
        var resolver = CreateResolver();
        var here = new List<FileSystemMetadata> { new() { FullName = $"{Dir}/Adele - Easy On Me.mp3", Name = "Adele - Easy On Me.mp3" } };
        var there = new List<FileSystemMetadata> { new() { FullName = "/music/other/Adele - Easy On Me.mp3", Name = "Adele - Easy On Me.mp3" } };

        var first = resolver.ResolveMultiple(new Folder { Path = Dir }, here, CollectionType.music, Mock.Of<IDirectoryService>())!.Items.Single();
        var again = resolver.ResolveMultiple(new Folder { Path = Dir }, here, CollectionType.music, Mock.Of<IDirectoryService>())!.Items.Single();
        var elsewhere = resolver.ResolveMultiple(new Folder { Path = "/music/other" }, there, CollectionType.music, Mock.Of<IDirectoryService>())!.Items.Single();

        Assert.Equal(first.Id, again.Id);
        Assert.NotEqual(first.Id, elsewhere.Id);
    }

    [Fact]
    public void ResolveMultiple_OptionOff_ReturnsNull()
    {
        Assert.Null(CreateResolver(groupByTags: false).ResolveMultiple(new Folder { Path = Dir }, MixedFolderFiles(), CollectionType.music, Mock.Of<IDirectoryService>()));
    }

    [Fact]
    public void ResolveMultiple_NotMusicCollection_ReturnsNull()
    {
        Assert.Null(CreateResolver().ResolveMultiple(new Folder { Path = Dir }, MixedFolderFiles(), CollectionType.books, Mock.Of<IDirectoryService>()));
    }

    [Fact]
    public void ResolveMultiple_InsideAlbum_ReturnsNull()
    {
        Assert.Null(CreateResolver().ResolveMultiple(new MusicAlbum { Path = Dir, IsInMixedFolder = true }, MixedFolderFiles(), CollectionType.music, Mock.Of<IDirectoryService>()));
    }

    [Fact]
    public void ResolvePath_Directory_ReturnsNull()
    {
        var args = new ItemResolveArgs(Mock.Of<IServerApplicationPaths>(), CreateLibraryManager(true))
        {
            Parent = new Folder { Path = "/music" },
            FileInfo = new FileSystemMetadata { FullName = Dir, Name = "Chill Mix", IsDirectory = true },
            CollectionType = CollectionType.music
        };

        Assert.Null(CreateResolver().ResolvePath(args));
    }

    [Fact]
    public void GetTrackId_ScopedToAlbum()
    {
        var libraryManager = CreateLibraryManager(true);
        var path = $"{Dir}/Adele - Easy On Me.mp3";
        var album = TagAlbumResolver.GetAlbumId(libraryManager, Dir, new AudioAlbumTags("30", "Adele"));
        var otherAlbum = TagAlbumResolver.GetAlbumId(libraryManager, Dir, new AudioAlbumTags("Sadnecessary", "Milky Chance"));

        Assert.Equal(TagAlbumResolver.GetTrackId(libraryManager, album, path), TagAlbumResolver.GetTrackId(libraryManager, album, path));
        Assert.NotEqual(TagAlbumResolver.GetTrackId(libraryManager, album, path), TagAlbumResolver.GetTrackId(libraryManager, otherAlbum, path));
        Assert.NotEqual(TagAlbumResolver.GetTrackId(libraryManager, album, path), libraryManager.GetNewItemId(path, typeof(Audio)));
    }

    [Fact]
    public void AudioResolver_InsideTagAlbum_ClaimsOnlyItsOwnTracks()
    {
        var libraryManager = CreateLibraryManager(true);
        var album = new MusicAlbum
        {
            Path = Dir,
            IsInMixedFolder = true,
            Id = TagAlbumResolver.GetAlbumId(libraryManager, Dir, new AudioAlbumTags("30", "Adele"))
        };
        var resolver = new AudioResolver(_namingOptions, libraryManager, CreateTagReader());

        var own = resolver.ResolvePath(Args(libraryManager, album, $"{Dir}/Adele - Easy On Me.mp3"));
        Assert.NotNull(own);
        Assert.Equal(TagAlbumResolver.GetTrackId(libraryManager, album.Id, $"{Dir}/Adele - Easy On Me.mp3"), own.Id);

        Assert.Null(resolver.ResolvePath(Args(libraryManager, album, $"{Dir}/Milky Chance - Stolen Dance.mp3")));
        Assert.Null(resolver.ResolvePath(Args(libraryManager, album, $"{Dir}/Unknown - Demo.mp3")));
    }

    [Fact]
    public void AudioResolver_InsideRegularFolder_LeavesIdToResolverHelper()
    {
        var libraryManager = CreateLibraryManager(true);
        var resolver = new AudioResolver(_namingOptions, libraryManager, CreateTagReader());

        var track = resolver.ResolvePath(Args(libraryManager, new Folder { Path = Dir }, $"{Dir}/Unknown - Demo.mp3"));

        Assert.NotNull(track);
        Assert.Equal(Guid.Empty, track.Id);
    }

    private static ItemResolveArgs Args(ILibraryManager libraryManager, Folder parent, string path)
    {
        return new ItemResolveArgs(Mock.Of<IServerApplicationPaths>(), libraryManager)
        {
            Parent = parent,
            FileInfo = new FileSystemMetadata { FullName = path, Name = System.IO.Path.GetFileName(path) },
            CollectionType = CollectionType.music
        };
    }
}
