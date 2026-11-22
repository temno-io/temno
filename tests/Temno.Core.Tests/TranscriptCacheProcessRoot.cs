using System.Runtime.CompilerServices;
using Temno.Core.Transcription;

namespace Temno.Core.Tests;

/// <summary>
/// Points <see cref="TranscriptDiskCache"/> at a per-process scratch directory before any test runs,
/// so a test that forgets <see cref="TranscriptDiskCache.SetRootForTests"/> still cannot write into
/// the user's LocalAppData transcript cache (WP-194). Per-test roots layer on top of this one.
/// </summary>
internal static class TranscriptCacheProcessRoot
{
    public static string Root { get; } = Path.Combine(
        Path.GetTempPath(),
        "temno-tests-transcripts-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N"));

    [ModuleInitializer]
    internal static void Install()
    {
        TranscriptDiskCache.SetDefaultRootForTests(Root);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(Root, recursive: true); } catch { }
        };
    }
}
