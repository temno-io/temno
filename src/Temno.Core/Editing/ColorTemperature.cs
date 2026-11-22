namespace Temno.Core.Editing;

/// <summary>
/// Rec.709 / sRGB D65 white is 6500 K (Mac <c>CITemperatureAndTint</c> target).
/// Windows uses 6000 K as the viewer default so ungraded SDR preview matches a
/// color-managed reference on this display. That default is identity in the
/// inspector and is not written into the project JSON.
/// </summary>
public static class ColorTemperature
{
    public const double D65Kelvin = 6500;
    public const double WindowsViewerKelvin = 6000;

    public static double NeutralKelvin =>
        OperatingSystem.IsWindows() ? WindowsViewerKelvin : D65Kelvin;

    public static bool AppliesWindowsViewerDefault => OperatingSystem.IsWindows();

    public static void ApplyRgb(
        ref double red,
        ref double green,
        ref double blue,
        double kelvin,
        double tint = 0,
        double identityKelvin = D65Kelvin)
    {
        var t = (kelvin - identityKelvin) / 4500.0;
        red = Clamp01(red + t * 0.12);
        blue = Clamp01(blue - t * 0.12);
        var tn = tint / 100.0;
        green = Clamp01(green + tn * 0.08);
        red = Clamp01(red - tn * 0.04);
        blue = Clamp01(blue - tn * 0.04);
    }

    /// <summary>
    /// Preview-only Windows viewer white on a BGRA buffer. Safe off the UI thread.
    /// Does not write a clip grade into project JSON.
    /// </summary>
    public static void ApplyWindowsViewerToBgra(byte[] bgra)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (!AppliesWindowsViewerDefault)
            return;

        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            var blue = bgra[i] / 255.0;
            var green = bgra[i + 1] / 255.0;
            var red = bgra[i + 2] / 255.0;
            ApplyRgb(ref red, ref green, ref blue, WindowsViewerKelvin);
            bgra[i] = ToByte(blue);
            bgra[i + 1] = ToByte(green);
            bgra[i + 2] = ToByte(red);
        }
    }

    private static byte ToByte(double value)
        => (byte)Math.Clamp(
            (int)Math.Round(Math.Clamp(value, 0, 1) * 255.0, MidpointRounding.AwayFromZero),
            byte.MinValue,
            byte.MaxValue);

    private static double Clamp01(double value) => Math.Clamp(value, 0, 1);
}
