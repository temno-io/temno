using System.Drawing.Text;
using System.Runtime.Versioning;

namespace Temno.Media.Text;

/// <summary>Installed Windows font family names for the inspector picker.</summary>
public static class SystemFontCatalog
{
    private static readonly object Gate = new();
    private static IReadOnlyList<string>? _cached;

    public static IReadOnlyList<string> FamilyNames()
    {
        lock (Gate)
        {
            if (_cached is not null)
                return _cached;

            var names = DirectWriteTextRasterizer.ListFamilyNames();
            if (names.Count == 0 && OperatingSystem.IsWindows())
                names = GdiFamilyNames();

            var list = new List<string>(names.Count + 1);
            if (!names.Contains("Helvetica-Bold", StringComparer.OrdinalIgnoreCase))
                list.Add("Helvetica-Bold");
            list.AddRange(names);
            _cached = list;
            return _cached;
        }
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<string> GdiFamilyNames()
    {
        try
        {
            using var installed = new InstalledFontCollection();
            return installed.Families
                .Select(f => f.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }
}
