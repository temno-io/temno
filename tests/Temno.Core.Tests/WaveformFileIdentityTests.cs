using Temno.Media.Audio;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>WP-213: file-identity capture/match semantics behind waveform invalidation.</summary>
public sealed class WaveformFileIdentityTests
{
    private static string TouchTemp(byte[]? content = null)
    {
        var path = Path.Combine(Path.GetTempPath(), "wp213-ident-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, content ?? [7, 7, 7]);
        return path;
    }

    [Fact]
    public void StableFile_MatchesItsCapture()
    {
        var path = TouchTemp();
        try
        {
            var identity = WaveformFileIdentity.Capture(path);
            Assert.NotEqual(WaveformFileIdentity.Missing, identity);
            Assert.True(identity.Matches(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void EmptyFile_MatchesItselfButIsNotMissing()
    {
        var path = TouchTemp([]);
        try
        {
            var identity = WaveformFileIdentity.Capture(path);
            Assert.NotEqual(WaveformFileIdentity.Missing, identity);
            Assert.True(identity.Matches(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RewrittenFile_NoLongerMatches()
    {
        var path = TouchTemp([1, 2, 3]);
        try
        {
            var before = WaveformFileIdentity.Capture(path);
            File.WriteAllBytes(path, [1, 2, 3, 4]); // length change
            Assert.False(before.Matches(path));

            var middle = WaveformFileIdentity.Capture(path);
            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(5)); // mtime-only change
            Assert.False(middle.Matches(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RelinkedSameLengthSameMtime_DoesNotMatch()
    {
        // The finding-4 repro: two equal-length files with copied timestamps. A mediaRef
        // relinked from one to the other must invalidate, even though length+mtime agree.
        var first = TouchTemp([1, 1, 1, 1]);
        var second = TouchTemp([2, 2, 2, 2]);
        try
        {
            File.SetLastWriteTimeUtc(second, File.GetLastWriteTimeUtc(first));
            var identity = WaveformFileIdentity.Capture(first);
            Assert.True(identity.Matches(first));
            Assert.False(identity.Matches(second));
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }

    [Fact]
    public void MissingFile_CapturesMissingAndNeverMatches()
    {
        var path = Path.Combine(Path.GetTempPath(), "wp213-ident-gone-" + Guid.NewGuid().ToString("N") + ".bin");
        Assert.Equal(WaveformFileIdentity.Missing, WaveformFileIdentity.Capture(path));
        Assert.False(WaveformFileIdentity.Missing.Matches(path));

        var live = TouchTemp();
        try
        {
            // Absence must not validate anything, even against a stored Missing.
            Assert.False(WaveformFileIdentity.Capture(live).Matches(path));
        }
        finally
        {
            File.Delete(live);
        }
    }
}
