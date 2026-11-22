using Temno.Core.IO;
using Temno.Core.Transcription;
using Xunit;

namespace Temno.Core.Tests;

[Collection("EnvRegistry")]
public sealed class TranscriptDiskCacheRootTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "temno-cache-root-" + Guid.NewGuid().ToString("N"));

    public TranscriptDiskCacheRootTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        TranscriptDiskCache.SetRootForTests(null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void RootDirectory_PerTestRootWins_ThenProcessScratch_NeverLocalAppData()
    {
        var perTest = Path.Combine(_tempDir, "cache");
        TranscriptDiskCache.SetRootForTests(perTest);
        Assert.Equal(perTest, TranscriptDiskCache.RootDirectory);

        TranscriptDiskCache.SetRootForTests(null);
        Assert.Equal(TranscriptCacheProcessRoot.Root, TranscriptDiskCache.RootDirectory);
        Assert.False(TranscriptDiskCache.RootDirectory.StartsWith(
            UserDataRoots.LocalAppDataRoot, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Store_WithoutPerTestRoot_WritesUnderProcessScratch()
    {
        var media = Path.Combine(_tempDir, "speech.wav");
        File.WriteAllText(media, "not audio");
        TranscriptDiskCache.SetRootForTests(null);

        TranscriptDiskCache.Store(media, FakeTranscriptionProvider.MakeTranscript(["guard"]));

        var written = TranscriptDiskCache.PathForKey(TranscriptDiskCache.KeyForPath(media));
        Assert.StartsWith(TranscriptCacheProcessRoot.Root, written, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(written));
    }
}
