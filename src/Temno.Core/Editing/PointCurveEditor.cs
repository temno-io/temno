namespace Temno.Core.Editing;

/// <summary>Pure helpers for multi-point curve editing (master/RGB and hue curves).</summary>
public static class PointCurveEditor
{
    private const double MinPointSpacing = 0.005;

    public static List<CurvePoint> Clone(IReadOnlyList<CurvePoint>? points)
        => points?.Select(p => new CurvePoint(p.X, p.Y)).ToList() ?? [];

    public static List<CurvePoint> EnsureSorted(List<CurvePoint> points)
    {
        points.Sort((a, b) => a.X.CompareTo(b.X));
        return points;
    }

    /// <summary>Insert or update a point; merges near-x hits (epsilon). Returns new list sorted.</summary>
    public static List<CurvePoint> Upsert(IReadOnlyList<CurvePoint>? points, double x, double y, double xEpsilon = 0.02)
    {
        x = Math.Clamp(x, 0, 1);
        y = Math.Clamp(y, 0, 1);
        var list = Clone(points);
        var i = list.FindIndex(p => Math.Abs(p.X - x) <= xEpsilon);
        if (i >= 0)
            list[i] = new CurvePoint(list[i].X, y);
        else
            list.Add(new CurvePoint(x, y));
        return EnsureSorted(list);
    }

    /// <summary>Move point at index to new x/y while preserving ordered boundary anchors.</summary>
    public static List<CurvePoint> Move(IReadOnlyList<CurvePoint> points, int index, double x, double y)
    {
        if (points.Count == 0 || index < 0 || index >= points.Count)
            return Clone(points);
        var list = EnsureSorted(Clone(points));

        // Curves need anchors at the domain edges. Interior points cannot cross because that
        // produces duplicate x coordinates and makes a drag select an arbitrary point next time.
        var minX = index == 0 ? 0 : list[index - 1].X + MinPointSpacing;
        var maxX = index == list.Count - 1 ? 1 : list[index + 1].X - MinPointSpacing;
        if (minX > maxX)
        {
            minX = Math.Max(0, list[index - 1].X);
            maxX = Math.Min(1, list[index + 1].X);
        }

        list[index] = new CurvePoint(Math.Clamp(x, minX, maxX), Math.Clamp(y, 0, 1));
        return list;
    }

    /// <summary>Remove point at index if more than 2 remain.</summary>
    public static List<CurvePoint> RemoveAt(IReadOnlyList<CurvePoint> points, int index)
    {
        var list = Clone(points);
        if (list.Count <= 2 || index <= 0 || index >= list.Count - 1)
            return list;
        list.RemoveAt(index);
        return list;
    }

    public static List<CurvePoint> Identity => [new(0, 0), new(1, 1)];

    public static List<CurvePoint> NeutralHue =>
    [
        new(0, HueCurves.NeutralY),
        new(0.5, HueCurves.NeutralY),
        new(1, HueCurves.NeutralY),
    ];

    public static int HitTest(IReadOnlyList<CurvePoint> points, double x, double y, double radius = 0.04)
    {
        var best = -1;
        var bestD = radius;
        for (var i = 0; i < points.Count; i++)
        {
            var dx = points[i].X - x;
            var dy = points[i].Y - y;
            var d = Math.Sqrt(dx * dx + dy * dy);
            if (d <= bestD)
            {
                bestD = d;
                best = i;
            }
        }
        return best;
    }
}
