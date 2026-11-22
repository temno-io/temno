using System.Globalization;

namespace Temno.Core.Export;

public readonly struct SourceTimecode : IEquatable<SourceTimecode>
{
    public int Frame { get; }
    public int Quanta { get; }
    public bool DropFrame { get; }
    public double? FrameDuration { get; }

    public SourceTimecode(int frame, int quanta, bool dropFrame, double? frameDuration = null)
    {
        Frame = frame;
        Quanta = quanta;
        DropFrame = dropFrame;
        FrameDuration = frameDuration;
    }

    public int Frames(int fps)
    {
        if (Quanta <= 0) return 0;
        return (int)Math.Round((double)Frame / Quanta * fps);
    }

    public double Seconds => Frame * (FrameDuration ?? (Quanta > 0 ? 1.0 / Quanta : 0.0));

    /// <summary>
    /// Parse SMPTE strings like "01:02:03:04" (non-drop) or "01:02:03;04" (drop-frame).
    /// Frame count is encoded at <paramref name="quanta"/> ticks/sec (typically source fps).
    /// Drop-frame numbering is flagged but not remapped (matches Mac SourceTimingReader for tagged TC).
    /// </summary>
    public static bool TryParseSmpte(string? text, int quanta, out SourceTimecode tc)
    {
        tc = default;
        if (string.IsNullOrWhiteSpace(text) || quanta <= 0) return false;
        var s = text.Trim();
        var drop = s.Contains(';');
        s = s.Replace(';', ':');
        var parts = s.Split(':');
        if (parts.Length != 4) return false;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hh)) return false;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mm)) return false;
        if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ss)) return false;
        if (!int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ff)) return false;
        if (hh < 0 || mm is < 0 or > 59 || ss is < 0 or > 59 || ff < 0) return false;
        var frame = ((hh * 3600) + (mm * 60) + ss) * quanta + ff;
        tc = new SourceTimecode(frame, quanta, drop);
        return true;
    }

    public bool Equals(SourceTimecode other)
    {
        return Frame == other.Frame &&
               Quanta == other.Quanta &&
               DropFrame == other.DropFrame &&
               Nullable.Equals(FrameDuration, other.FrameDuration);
    }

    public override bool Equals(object? obj) => obj is SourceTimecode other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Frame, Quanta, DropFrame, FrameDuration);

    public static bool operator ==(SourceTimecode left, SourceTimecode right) => left.Equals(right);

    public static bool operator !=(SourceTimecode left, SourceTimecode right) => !left.Equals(right);
}
