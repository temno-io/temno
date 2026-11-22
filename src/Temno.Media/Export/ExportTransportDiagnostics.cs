using System.Diagnostics;
using System.Text;
using Temno.Core.IO;

namespace Temno.Media.Export;

/// <summary>
/// WP-216. Which transport carried each export decoder's frames, and why.
/// <para>
/// The measured gap between the two transports at 4K is <b>84.9 ms/frame against 16.0</b>
/// (<c>docs/verification/wp-216/DECISION.md</c> §1), so a fallback that happens quietly turns a
/// 1.5x-realtime export back into a 0.37x one and leaves nothing behind that says so. WP-215 learned
/// the same lesson about the compositor and this follows its shape deliberately: counters per
/// process, one log line per <i>transition</i> rather than per frame, and a fallback reason that is
/// never cleared once seen.
/// </para>
/// </summary>
public static class ExportTransportDiagnostics
{
    private const long MaxLogBytes = 256 * 1024;

    private static readonly object Gate = new();
    private static long _namedPipeDecoders;
    private static long _anonymousPipeDecoders;
    private static string? _lastTransition;
    private static bool _logDisabled;

    /// <summary>Transport of the most recently started export decoder, or null before the first.</summary>
    public static FrameTransport? LastTransport { get; private set; }

    /// <summary>Buffer size when the sized transport was obtained, or why it was not.</summary>
    public static string? LastDetail { get; private set; }

    /// <summary>
    /// Set the first time a decoder asked for the sized transport and did not get it, with the
    /// reason. Never cleared: a fallback that happened once is worth reporting after the path
    /// recovers, because it is the difference between an export that made realtime and one that
    /// did not.
    /// </summary>
    public static string? LastFallbackReason { get; private set; }

    /// <summary>Decoder pipes started on each transport this process. Pipes, not frames — one pipe carries many.</summary>
    public static long NamedPipeDecoders { get { lock (Gate) return _namedPipeDecoders; } }

    /// <inheritdoc cref="NamedPipeDecoders"/>
    public static long AnonymousPipeDecoders { get { lock (Gate) return _anonymousPipeDecoders; } }

    /// <summary>Raised on a transition only, with the same text written to the log.</summary>
    public static event Action<string>? Transition;

    /// <summary>Append-only transition log, shared with other processes and attributed per line.</summary>
    public static string LogPath { get; } =
        Path.Combine(UserDataRoots.LocalAppDataRoot, "logs", "export-transport.log");

    /// <summary>
    /// What the next export decoder will <b>ask for</b>, and why — answerable before a frame has
    /// been decoded, which <see cref="Summary"/> is not. Mirrors
    /// <c>CompositorFactory.DescribeSelection()</c>, deliberately: the two rows sit beside each
    /// other in Settings and a reader should not have to learn two shapes.
    /// <para>
    /// It is a request, never an observation. Nothing here probes the pipe, so this row cannot say
    /// what the transport <i>is</i>; <see cref="Summary"/> says what it <i>was</i>, and only a real
    /// fault arms <see cref="LastFallbackReason"/>.
    /// </para>
    /// </summary>
    public static string DescribeSelection()
    {
        if (!OperatingSystem.IsWindows())
            return "Standard pipe — the sized transport is Windows-only";

        if (!ExportFrameSource.UseSizedTransport())
            return $"Standard pipe — forced by {ExportFrameSource.TransportOverrideVariable}=stdout";

        // Stated as a *request*, which is the only thing true in every state, and deliberately not
        // as an observation.
        //
        // Two reviews pushed this line in opposite directions and both were right. The
        // machine-specificity audit objected that a bare "Sized pipe (1 MiB buffer)" asserts a
        // capability nothing here has probed — unlike the compositor row above, which earns its
        // certainty by creating a D3D11 device first. T2 round 2 then objected that answering with
        // the last *observed* transport is worse, because a decoder takes stdout for benign reasons
        // too — an override since cleared, or a stream with no frames at that position — and the
        // row would go on claiming a capability failure the machine never had, while Summary
        // correctly stayed quiet. Both objections are satisfied by describing the request: it is
        // exactly what UseSizedTransport() will do, it never over-claims, and what actually
        // happened is Summary's job on the line below.
        return $"Sized pipe ({DescribeBuffer()}) requested";
    }

    /// <summary>
    /// The buffer size as the decoder is actually built, not as a literal that quietly stops
    /// matching. The kernel treats the figure as advisory, which is why the row reports it as what
    /// was asked for.
    /// </summary>
    private static string DescribeBuffer()
    {
        const int Mib = 1024 * 1024;
        return FfmpegPlaybackDecoder.NamedPipeBufferBytes % Mib == 0
            ? $"{FfmpegPlaybackDecoder.NamedPipeBufferBytes / Mib} MiB buffer"
            : $"{FfmpegPlaybackDecoder.NamedPipeBufferBytes / 1024} KiB buffer";
    }

    /// <summary>One line for a status row: which transport is carrying export frames, and what it cost.</summary>
    public static string Summary
    {
        get
        {
            lock (Gate)
            {
                if (LastTransport is null)
                    return "No export decoder started yet.";

                var sb = new StringBuilder();
                sb.Append(_namedPipeDecoders)
                  .Append(_namedPipeDecoders == 1 ? " decode pipe" : " decode pipes")
                  .Append(" on the sized transport, ").Append(_anonymousPipeDecoders)
                  .Append(" on the stdout pipe.");
                if (!string.IsNullOrEmpty(LastFallbackReason))
                    sb.Append(" Fell back at least once: ").Append(LastFallbackReason);
                return sb.ToString();
            }
        }
    }

    internal static void RecordDecoder(FrameTransport transport, string? detail, bool fellBack)
    {
        string? transition = null;
        lock (Gate)
        {
            if (transport == FrameTransport.NamedPipe) _namedPipeDecoders++; else _anonymousPipeDecoders++;

            // Only a real fault arms the alarm. A stream with no frames at the requested position
            // also takes the stdout path and also carries a detail, but it is not a fallback and
            // must not leave a healthy machine reporting one for the rest of the process.
            if (fellBack && detail is { Length: > 0 })
                LastFallbackReason = detail;

            var key = transport + "|" + detail;
            LastTransport = transport;
            LastDetail = detail;
            if (!string.Equals(key, _lastTransition, StringComparison.Ordinal))
            {
                _lastTransition = key;
                transition = Describe(transport, detail);
            }
        }

        if (transition is null) return;
        Write(transition);
        Transition?.Invoke(transition);
    }

    private static string Describe(FrameTransport transport, string? detail)
    {
        var sb = new StringBuilder();
        sb.Append(transport == FrameTransport.NamedPipe
            ? "export-transport=named-pipe"
            : "export-transport=stdout");
        if (!string.IsNullOrEmpty(detail)) sb.Append("; detail=").Append(detail);
        return sb.ToString();
    }

    private static void Write(string message)
    {
        if (_logDisabled) return;
        try
        {
            var directory = Path.GetDirectoryName(LogPath)!;
            Directory.CreateDirectory(directory);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxLogBytes)
                File.Delete(LogPath);

            using var process = Process.GetCurrentProcess();
            File.AppendAllText(
                LogPath,
                $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} {process.ProcessName}({process.Id}) {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never be able to fail an export. One failure disables the sink for
            // the process rather than retrying a locked path on every transition.
            _logDisabled = true;
        }
    }
}
