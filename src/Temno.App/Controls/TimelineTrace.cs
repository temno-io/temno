using System.Diagnostics;
using System.Globalization;
using System.Text;
using Temno.Core.IO;

namespace Temno.App.Controls;

/// <summary>
/// WP-320 trim-drag trace. Env-gated by TEMNO_TIMELINE_TRACE=&lt;path&gt;, the same kind of
/// switch as TEMNO_PLAYBACK_TRACE. Events go to preallocated arrays and are written only by
/// <see cref="Flush"/>, so tracing costs no I/O on the UI thread mid-gesture.
/// </summary>
internal static class TimelineTrace
{
    internal static readonly string? TracePath = EnvCompat.Get("TEMNO_TIMELINE_TRACE");
    internal static bool On => TracePath is not null;

    private const int Capacity = 1 << 19;
    private static readonly long[] Ts = On ? new long[Capacity] : [];
    private static readonly string[] Ev = On ? new string[Capacity] : [];
    private static readonly long[] A = On ? new long[Capacity] : [];
    private static readonly long[] B = On ? new long[Capacity] : [];
    private static int _n;
    private static readonly object Gate = new();

    public static void Mark(string evt, long a = 0, long b = 0)
    {
        if (!On) return;
        var t = Stopwatch.GetTimestamp();
        lock (Gate)
        {
            if (_n >= Capacity) return;
            Ts[_n] = t;
            Ev[_n] = evt;
            A[_n] = a;
            B[_n] = b;
            _n++;
        }
    }

    public static void Flush(string path)
    {
        if (!On) return;
        var sb = new StringBuilder();
        lock (Gate)
        {
            sb.Append("freq ").Append(Stopwatch.Frequency.ToString(CultureInfo.InvariantCulture)).Append('\n');
            for (var i = 0; i < _n; i++)
            {
                sb.Append(Ts[i].ToString(CultureInfo.InvariantCulture)).Append(' ')
                  .Append(Ev[i]).Append(' ')
                  .Append(A[i].ToString(CultureInfo.InvariantCulture)).Append(' ')
                  .Append(B[i].ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
            _n = 0;
        }
        File.WriteAllText(path, sb.ToString());
    }
}
