using Temno.Media.Transcription;
using Xunit;

namespace Temno.Core.Tests;

[Collection("EnvRegistry")]
public sealed class WhisperModelStoreTests : IDisposable
{
    private readonly string? _prevModel;

    public WhisperModelStoreTests()
    {
        _prevModel = Environment.GetEnvironmentVariable("TEMNO_WHISPER_MODEL");
    }

    public void Dispose()
    {
        WhisperModelStore.SetPathsForTests(null, null);
        WhisperModelStore.SetDownloadForTests(null);
        Environment.SetEnvironmentVariable("TEMNO_WHISPER_MODEL", _prevModel);
    }

    [Fact]
    public void TryResolvePath_PrefersEnvThenBundledThenUser()
    {
        var root = Path.Combine(Path.GetTempPath(), "temno-whisper-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var envFile = Path.Combine(root, "env.bin");
            var bundled = Path.Combine(root, "bundled.bin");
            var user = Path.Combine(root, "user.bin");
            File.WriteAllBytes(envFile, [1]);
            File.WriteAllBytes(bundled, [2]);
            File.WriteAllBytes(user, [3]);

            WhisperModelStore.SetPathsForTests(bundled, user);
            Environment.SetEnvironmentVariable("TEMNO_WHISPER_MODEL", envFile);
            Assert.Equal(envFile, WhisperModelStore.TryResolvePath());

            Environment.SetEnvironmentVariable("TEMNO_WHISPER_MODEL", null);
            Assert.Equal(bundled, WhisperModelStore.TryResolvePath());

            WhisperModelStore.SetPathsForTests(Path.Combine(root, "missing-bundled.bin"), user);
            Assert.Equal(user, WhisperModelStore.TryResolvePath());

            WhisperModelStore.SetPathsForTests(
                Path.Combine(root, "missing-bundled.bin"),
                Path.Combine(root, "missing-user.bin"));
            Assert.Null(WhisperModelStore.TryResolvePath());
            Assert.False(WhisperModelStore.IsReady);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    // WP-402: the download comes from one fixed revision, the one ensure-whisper-model.ps1 fetches.
    [Fact]
    public void The_download_is_pinned_to_the_revision_the_script_fetches()
    {
        Assert.Matches("^[0-9a-f]{40}$", WhisperModelStore.PinnedModelRevision);
        Assert.Equal(
            "https://huggingface.co/ggerganov/whisper.cpp/resolve/" + WhisperModelStore.PinnedModelRevision + "/ggml-base.en.bin",
            WhisperModelStore.PinnedModelUrl);
        var script = File.ReadAllText(Path.Combine(BuildProvenanceTests.RepoRoot(), "scripts", "ensure-whisper-model.ps1"));
        Assert.Contains("$revision = \"" + WhisperModelStore.PinnedModelRevision + "\"", script);
        Assert.Contains("$pinnedBytes = " + WhisperModelStore.PinnedModelBytes, script);
        Assert.Contains("$pinnedSha256 = \"" + WhisperModelStore.PinnedModelSha256 + "\"", script);
    }

    // WP-401's T2 lead, taken by WP-402: Temno's own copy at the wrong size is fetched again instead of failing every
    // transcription; a pinned-size copy is used as it is, and a TEMNO_WHISPER_MODEL file is never replaced.
    [Fact]
    public async Task Temno_s_own_download_is_replaced_only_when_it_is_not_the_pinned_size()
    {
        var root = Path.Combine(Path.GetTempPath(), "temno-whisper-refetch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Environment.SetEnvironmentVariable("TEMNO_WHISPER_MODEL", null);
            var user = Path.Combine(root, "whisper", "ggml-base.en.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(user)!);
            WhisperModelStore.SetPathsForTests(Path.Combine(root, "missing-bundled.bin"), user);
            var downloads = 0;
            WhisperModelStore.SetDownloadForTests(_ =>
            {
                downloads++;
                return Task.FromResult<Stream>(new MemoryStream([1, 2, 3]));
            });

            // An older model: fetched again. The fake bytes are not the pinned model, so it fails closed and the
            // old file stays for the next attempt.
            File.WriteAllBytes(user, new byte[1000]);
            await Assert.ThrowsAsync<InvalidOperationException>(() => WhisperModelStore.EnsureDefaultAsync());
            Assert.Equal(1, downloads);
            Assert.Equal(1000, new FileInfo(user).Length);
            Assert.False(File.Exists(user + ".tmp"));

            // The pinned size: used as it is (the loader's VerifyPinned still checks every byte).
            using (var sized = File.Create(user))
                sized.SetLength(WhisperModelStore.PinnedModelBytes);
            Assert.Equal(user, await WhisperModelStore.EnsureDefaultAsync());
            Assert.Equal(1, downloads);

            // A file the user named is never replaced, whatever its size.
            var named = Path.Combine(root, "mine.bin");
            File.WriteAllBytes(named, [9]);
            Environment.SetEnvironmentVariable("TEMNO_WHISPER_MODEL", named);
            Assert.Equal(named, await WhisperModelStore.EnsureDefaultAsync());
            Assert.Equal(1, downloads);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEMNO_WHISPER_MODEL", null);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void VerifyPinned_AcceptsOnlyTheExactBytes()
    {
        var path = Path.Combine(Path.GetTempPath(), "temno-whisper-pin-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            byte[] model = [.. Enumerable.Range(0, 4096).Select(i => (byte)(i * 7))];
            File.WriteAllBytes(path, model);
            var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(model));

            WhisperModelStore.VerifyPinned(path, model.Length, sha);
            WhisperModelStore.VerifyPinned(path, model.Length, sha.ToLowerInvariant());

            var size = Assert.Throws<InvalidDataException>(() => WhisperModelStore.VerifyPinned(path, model.Length + 1, sha));
            Assert.Contains("loads only the pinned model", size.Message, StringComparison.Ordinal);

            model[^1] ^= 0xFF;
            File.WriteAllBytes(path, model);
            var hash = Assert.Throws<InvalidDataException>(() => WhisperModelStore.VerifyPinned(path, model.Length, sha));
            Assert.Contains("SHA-256", hash.Message, StringComparison.Ordinal);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void VerifyPinned_RefusesAGgmlHeaderWithAnOutOfRangeTensorType()
    {
        // The shape of CVE-2026-110810's input: a well-formed ggml magic and hyperparameters, then a
        // tensor header whose ttype indexes past the type table. It must never reach whisper_model_load.
        var path = Path.Combine(Path.GetTempPath(), "temno-whisper-hostile-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                writer.Write(0x67676d6c);
                foreach (var value in new[] { 51864, 1500, 512, 8, 6, 448, 512, 8, 6, 80, 1 })
                    writer.Write(value);
                writer.Write(2);
                writer.Write(4);
                writer.Write(int.MaxValue);
            }

            Assert.Throws<InvalidDataException>(() => WhisperModelStore.VerifyPinned(path));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void ThePinMatchesTheModelTheMsixShips()
    {
        var repo = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(repo, "TemnoWindows.sln")))
            repo = Path.GetDirectoryName(repo) ?? throw new InvalidOperationException("Repository root not found.");
        // The pin build-msix.ps1 and build-zip.ps1 both check on the finished package (WP-451).
        var script = File.ReadAllText(Path.Combine(repo, "scripts", "lib", "app-payload.ps1"));

        Assert.Contains($"$whisperModelBytes = {WhisperModelStore.PinnedModelBytes}", script, StringComparison.Ordinal);
        Assert.Contains($"$whisperModelSha256 = '{WhisperModelStore.PinnedModelSha256}'", script, StringComparison.Ordinal);
    }
}
