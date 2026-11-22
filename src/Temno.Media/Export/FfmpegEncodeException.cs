namespace Temno.Media.Export;

/// <summary>
/// An ffmpeg encode/mux pass that failed, carrying the diagnostics needed to act on it.
/// <para>
/// Before this existed, an argv ffmpeg refused (wrong container for the codec, a pix_fmt the
/// encoder cannot take, a missing hardware device) killed the process during header write, and the
/// very next frame written to its stdin surfaced as <c>IOException: The pipe has been ended.</c> —
/// identical for every possible cause, with ffmpeg's own explanation discarded unread.
/// </para>
/// </summary>
public sealed class FfmpegEncodeException : Exception
{
    /// <summary>ffmpeg messages that mean "this encoder never opened", not "the render went wrong".</summary>
    private static readonly string[] InitFailureMarkers =
    [
        "Error while opening encoder",
        "Could not open encoder",
        "Cannot load nvcuda",
        "Cannot load libnvidia-encode",
        "OpenEncodeSessionEx failed",
        "InitializeEncoder failed",
        "No capable devices found",
        "No NVENC capable devices found",
        "Error creating a MFX session",
        "not supported, try next mfx implementation",
        "Provided device doesn't support",
        "DeviceCreation failed",
        "AMF failed",
        "Failed to initialise",
        "incorrect codec parameters",
        "Could not find tag for codec",
        "Invalid pixel format",
        "Specified pixel format",
        "Width and height must be",
        "dimensions not supported",
        "Generic error in an external library",
    ];

    private FfmpegEncodeException(
        string message, string stage, string encoder, string arguments,
        int exitCode, string stderr, int framesWritten, long bytesWritten)
        : base(message)
    {
        Stage = stage;
        Encoder = encoder;
        Arguments = arguments;
        ExitCode = exitCode;
        Stderr = stderr;
        FramesWritten = framesWritten;
        BytesWritten = bytesWritten;
    }

    /// <summary>Which pass failed ("video encode" / "audio mux").</summary>
    public string Stage { get; }

    /// <summary>FFmpeg encoder that was selected (e.g. hevc_nvenc, libx265, prores_ks).</summary>
    public string Encoder { get; }

    /// <summary>Full argv handed to ffmpeg, for a reproducible bug report.</summary>
    public string Arguments { get; }

    public int ExitCode { get; }

    /// <summary>Captured ffmpeg stderr — the actual reason the pass failed.</summary>
    public string Stderr { get; }

    /// <summary>Frames accepted before the failure.</summary>
    public int FramesWritten { get; }

    /// <summary>Bytes accepted before the failure.</summary>
    public long BytesWritten { get; }

    /// <summary>Roughly one pipe buffer's worth: writes below this may have gone into a dead pipe.</summary>
    private const long PipeBufferSlackBytes = 1L << 20;

    /// <summary>
    /// True when ffmpeg died before it could take real work, i.e. the command line or the encoder
    /// device was rejected. Only these are worth retrying on a different encoder; a failure 40 000
    /// frames in is a disk-full or driver fault, and re-rendering from frame 0 just burns another hour.
    /// <para>
    /// Frame count alone is not enough: a small canvas fits several frames inside the OS pipe buffer,
    /// so writes keep succeeding after ffmpeg is already gone. Bytes and ffmpeg's own wording decide.
    /// </para>
    /// </summary>
    public bool IsInitializationFailure
        => FramesWritten <= 2 || BytesWritten <= PipeBufferSlackBytes || MentionsInitFailure(Stderr);

    public static bool MentionsInitFailure(string? stderr)
    {
        if (string.IsNullOrEmpty(stderr)) return false;
        foreach (var marker in InitFailureMarkers)
        {
            if (stderr.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static FfmpegEncodeException Create(
        string stage, string encoder, string arguments, int exitCode, string stderr,
        int framesWritten, long bytesWritten = 0)
    {
        var reason = SummarizeStderr(stderr);
        var message = $"FFmpeg {stage} failed using '{encoder}'"
            + (exitCode != 0 ? $" (exit {exitCode})" : " (process ended early)")
            + (reason.Length > 0 ? $": {reason}" : ".");
        return new FfmpegEncodeException(
            message, stage, encoder, arguments, exitCode, stderr ?? "", framesWritten, bytesWritten);
    }

    /// <summary>
    /// Pulls the lines that explain the failure out of ffmpeg's stderr. ffmpeg prints the useful
    /// line first and then a wall of thread-teardown noise, so a naive tail shows nothing useful.
    /// </summary>
    public static string SummarizeStderr(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return "";

        var interesting = new List<string>();
        foreach (var raw in stderr.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("frame=", StringComparison.Ordinal)) continue;
            if (line.Contains("Terminating thread", StringComparison.Ordinal)) continue;
            if (line.Contains("Task finished with error code", StringComparison.Ordinal)) continue;
            if (line.Contains("Conversion failed", StringComparison.Ordinal)) continue;
            interesting.Add(line);
            if (interesting.Count == 4) break;
        }

        if (interesting.Count == 0)
            interesting.Add(stderr.Trim());

        var joined = string.Join(" | ", interesting);
        return joined.Length > 600 ? joined[..600] + "…" : joined;
    }
}
