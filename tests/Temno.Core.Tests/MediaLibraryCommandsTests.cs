using System.IO.Compression;
using Temno.Core.Models;
using Temno.Core.Session;
using Xunit;

namespace Temno.Core.Tests;

public sealed class MediaLibraryCommandsTests
{
    [Fact]
    public void FolderMoves_PreservePathAndRejectDescendantDestination()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(MediaLibraryCommands.CreateFolder(session, "B-roll", null, out var broll).Ok);
        Assert.True(MediaLibraryCommands.CreateFolder(session, "Sunset", broll, out var sunset).Ok);
        session.Manifest.Entries.Add(new MediaManifestEntry { Id = "asset", Name = "Asset", Type = ClipType.image, Source = MediaSource.Project("media/a.png") });

        Assert.True(MediaLibraryCommands.MoveAssets(session, ["asset"], sunset).Ok);
        Assert.Equal(sunset, session.Manifest.Entries.Single().FolderId);
        Assert.Equal(["B-roll", "Sunset"], MediaLibraryCommands.FolderPath(session.Manifest, sunset).Select(f => f.Name));

        var invalid = MediaLibraryCommands.MoveFolders(session, [broll!], sunset);
        Assert.False(invalid.Ok);
        Assert.Contains("descendants", invalid.Error!);
        Assert.Null(session.Manifest.Folders.Single(f => f.Id == broll).ParentFolderId);

        Assert.True(MediaLibraryCommands.MoveFolders(session, [sunset!], null).Ok);
        Assert.Null(session.Manifest.Folders.Single(f => f.Id == sunset).ParentFolderId);
    }

    [Fact]
    public void ImportBase64_WritesDistinctPackageOwnedFilesAndRoundTrips()
    {
        using var package = new TemporaryPackage();
        var session = package.CreateSession();
        Assert.True(MediaLibraryCommands.CreateFolder(session, "Assets", null, out var folder).Ok);
        var bytes = Convert.ToBase64String([1, 2, 3, 4]);

        Assert.True(MediaLibraryCommands.ImportBase64(session, bytes, "image/png", "One", folder, out var first).Ok);
        Assert.True(MediaLibraryCommands.ImportBase64(session, bytes, "image/png", "Two", folder, out var second).Ok);
        Assert.NotNull(first);
        Assert.NotNull(second);
        var firstPath = session.Resolver.ResolvePath(first!);
        var secondPath = session.Resolver.ResolvePath(second!);
        Assert.NotEqual(firstPath, secondPath);
        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(firstPath!));
        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(secondPath!));
        Assert.All([firstPath, secondPath], path => Assert.StartsWith(Path.Combine(package.Path, "media"), path!, StringComparison.OrdinalIgnoreCase));

        session.Save();
        var reopened = ProjectSession.Open(package.Path);
        Assert.Equal(2, reopened.Manifest.Entries.Count);
        Assert.All(reopened.Manifest.Entries, entry => Assert.Equal(folder, entry.FolderId));
    }

    [Fact]
    public void ImportBase64_RejectsInvalidMimeAndPayloadWithoutCreatingMedia()
    {
        using var package = new TemporaryPackage();
        var session = package.CreateSession();

        Assert.False(MediaLibraryCommands.ImportBase64(session, "not base64", "image/png", null, null, out _).Ok);
        Assert.False(MediaLibraryCommands.ImportBase64(session, Convert.ToBase64String([1]), "application/octet-stream", null, null, out _).Ok);
        Assert.Empty(session.Manifest.Entries);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(package.Path, "media")));
    }

    [Fact]
    public void ImportFile_SameBasenameCreatesDistinctPackageFilesAndRoundTrips()
    {
        using var package = new TemporaryPackage();
        var session = package.CreateSession();
        var sources = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "temno-import-sources-" + Guid.NewGuid().ToString("N"));
        var firstSource = System.IO.Path.Combine(sources, "first", "camera.mp4");
        var secondSource = System.IO.Path.Combine(sources, "second", "camera.mp4");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(firstSource)!);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(secondSource)!);
        File.WriteAllText(firstSource, "FIRST");
        File.WriteAllText(secondSource, "SECOND");

        try
        {
            Assert.True(MediaLibraryCommands.ImportFile(session, firstSource, null, null, out var first).Ok);
            Assert.True(MediaLibraryCommands.ImportFile(session, secondSource, null, null, out var second).Ok);
            var firstPath = session.Resolver.ResolvePath(first!);
            var secondPath = session.Resolver.ResolvePath(second!);

            Assert.NotEqual(firstPath, secondPath);
            Assert.Equal("FIRST", File.ReadAllText(firstPath!));
            Assert.Equal("SECOND", File.ReadAllText(secondPath!));
            Assert.StartsWith("imported-", System.IO.Path.GetFileName(firstPath), StringComparison.Ordinal);
            Assert.StartsWith("imported-", System.IO.Path.GetFileName(secondPath), StringComparison.Ordinal);

            session.Save();
            var reopened = ProjectSession.Open(package.Path);
            Assert.Equal(2, reopened.Manifest.Entries.Count);
            Assert.Equal(
                ["FIRST", "SECOND"],
                reopened.Manifest.Entries
                    .Select(entry => File.ReadAllText(reopened.Resolver.ResolvePath(entry)!))
                    .ToArray());
        }
        finally
        {
            if (Directory.Exists(sources)) Directory.Delete(sources, recursive: true);
        }
    }

    [Fact]
    public void ImportFile_RejectsUnsupportedExtensionWithoutChangingManifest()
    {
        using var package = new TemporaryPackage();
        var session = package.CreateSession();
        var source = System.IO.Path.Combine(package.Path, "notes.txt");
        File.WriteAllText(source, "not media");

        var result = MediaLibraryCommands.ImportFile(session, source, null, null, out var entry);

        Assert.False(result.Ok);
        Assert.Contains("unsupported file extension", result.Error!);
        Assert.Null(entry);
        Assert.Empty(session.Manifest.Entries);
        Assert.Empty(Directory.EnumerateFiles(System.IO.Path.Combine(package.Path, "media")));
    }

    [Theory]
    [InlineData(".mp4", ClipType.video)]
    [InlineData(".webp", ClipType.image)]
    [InlineData(".flac", ClipType.audio)]
    [InlineData(".lottie", ClipType.lottie)]
    public void SupportedFileExtensions_AndClassifierUseTheSameTypeMap(string extension, ClipType expected)
    {
        Assert.Contains(extension, MediaLibraryCommands.SupportedFileExtensions);
        Assert.True(MediaLibraryCommands.TryGetTypeFromExtension(extension, out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void CreateMatte_WritesEvenSolidRgbaPngForRequestedAspect()
    {
        using var package = new TemporaryPackage(width: 1919, height: 1081);
        var session = package.CreateSession();

        Assert.True(MediaLibraryCommands.CreateMatte(session, "#0A20F0", "16:9", null, null, out var matte).Ok);
        var path = session.Resolver.ResolvePath(matte!);
        Assert.NotNull(path);
        var png = File.ReadAllBytes(path!);
        Assert.Equal((1922, 1080), ReadSize(png));
        Assert.Equal([10, 32, 240, 255], ReadFirstPixel(png));
        Assert.Equal(ClipType.image, matte!.Type);
    }

    [Fact]
    public void RelinkMediaEntry_RejectsIncompatibleKnownMediaType()
    {
        var session = ProjectSession.CreateNew();
        session.Manifest.Entries.Add(new MediaManifestEntry
        {
            Id = "video",
            Name = "video",
            Type = ClipType.video,
            Source = MediaSource.External("C:/old/video.mp4"),
        });

        var result = EditCommands.RelinkMediaEntry(session, "video", "C:/new/still.png");
        Assert.False(result.Ok);
        Assert.Contains("cannot relink", result.Error!);
    }

    private static (int Width, int Height) ReadSize(byte[] png)
        => (ReadUInt32(png, 16), ReadUInt32(png, 20));

    private static byte[] ReadFirstPixel(byte[] png)
    {
        var cursor = 8;
        using var idat = new MemoryStream();
        while (cursor < png.Length)
        {
            var length = ReadUInt32(png, cursor);
            var type = System.Text.Encoding.ASCII.GetString(png, cursor + 4, 4);
            if (type == "IDAT") idat.Write(png, cursor + 8, length);
            cursor += 12 + length;
        }
        idat.Position = 0;
        using var zlib = new ZLibStream(idat, CompressionMode.Decompress);
        var row = new byte[5];
        Assert.Equal(row.Length, zlib.Read(row, 0, row.Length));
        Assert.Equal(0, row[0]);
        return row[1..];
    }

    private static int ReadUInt32(byte[] bytes, int offset)
        => (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];

    private sealed class TemporaryPackage : IDisposable
    {
        public TemporaryPackage(int width = 1920, int height = 1080)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "temno-wp105-tests-" + Guid.NewGuid().ToString("N") + ".palmier");
            Directory.CreateDirectory(Path);
            Width = width;
            Height = height;
        }

        public string Path { get; }
        private int Width { get; }
        private int Height { get; }

        public ProjectSession CreateSession()
        {
            var session = ProjectSession.CreateNew(width: Width, height: Height);
            session.Save(Path);
            return session;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
