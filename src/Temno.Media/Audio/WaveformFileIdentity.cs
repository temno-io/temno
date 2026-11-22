namespace Temno.Media.Audio;

/// <summary>
/// Source-file identity for waveform cache validation (WP-213): normalized full path, byte
/// length and last-write ticks. A relink to a different file invalidates even when length and
/// timestamps agree. Missing or inaccessible files capture as <see cref="Missing"/>, which
/// never matches, so absence can never validate a stale envelope.
/// </summary>
public readonly record struct WaveformFileIdentity(string FullPath, long Length, long MtimeTicks)
{
    public static WaveformFileIdentity Missing => new("", 0, 0);

    public static WaveformFileIdentity Capture(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists
                ? new(Path.GetFullPath(path), info.Length, info.LastWriteTimeUtc.Ticks)
                : Missing;
        }
        catch
        {
            return Missing;
        }
    }

    /// <summary>True when <paramref name="path"/> still names this exact file identity.</summary>
    public bool Matches(string path)
    {
        var live = Capture(path);
        return live != Missing
            && PathEquals(live.FullPath, FullPath)
            && live.Length == Length
            && live.MtimeTicks == MtimeTicks;
    }

    private static bool PathEquals(string a, string b)
        => OperatingSystem.IsWindows()
            ? string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
            : string.Equals(a, b, StringComparison.Ordinal);
}
