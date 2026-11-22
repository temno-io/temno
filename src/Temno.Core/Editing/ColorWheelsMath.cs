namespace Temno.Core.Editing;

/// <summary>
/// Lift / Gamma / Gain color wheels (Mac: Compositing/ColorWheels.swift).
/// Formula per channel: ((in·(1−L) + L) · Gain) ^ (1/Gamma).
/// </summary>
public static class ColorWheelsMath
{
    private const double ChromaLift = 0.2;
    private const double ChromaGain = 0.35;
    private const double ChromaGamma = 0.35;

    public readonly record struct Coefficients(
        double LiftR, double LiftG, double LiftB,
        double GainR, double GainG, double GainB,
        double InvGammaR, double InvGammaG, double InvGammaB);

    public static bool IsNeutral(
        double liftX, double liftY, double liftM,
        double gammaX, double gammaY, double gammaM,
        double gainX, double gainY, double gainM)
        => liftX == 0 && liftY == 0 && liftM == 0
           && gammaX == 0 && gammaY == 0 && Math.Abs(gammaM - 1) < 1e-9
           && gainX == 0 && gainY == 0 && Math.Abs(gainM - 1) < 1e-9;

    public static Coefficients FromParams(
        double liftX, double liftY, double liftM,
        double gammaX, double gammaY, double gammaM,
        double gainX, double gainY, double gainM)
    {
        var lift = ChromaOffset(liftX, liftY);
        var gamma = ChromaOffset(gammaX, gammaY);
        var gain = ChromaOffset(gainX, gainY);
        double L(double c) => liftM + c * ChromaLift;
        double G(double c) => gainM * (1 + c * ChromaGain);
        double Ig(double c) => 1.0 / Math.Max(0.01, gammaM * (1 + c * ChromaGamma));
        return new Coefficients(
            L(lift.R), L(lift.G), L(lift.B),
            G(gain.R), G(gain.G), G(gain.B),
            Ig(gamma.R), Ig(gamma.G), Ig(gamma.B));
    }

    public static void Apply(ref double red, ref double green, ref double blue, in Coefficients c)
    {
        red = ApplyChannel(red, c.LiftR, c.GainR, c.InvGammaR);
        green = ApplyChannel(green, c.LiftG, c.GainG, c.InvGammaG);
        blue = ApplyChannel(blue, c.LiftB, c.GainB, c.InvGammaB);
    }

    private static double ApplyChannel(double input, double lift, double gain, double invGamma)
    {
        // ((in·(1−L) + L) · Gain) ^ invGamma  — lift here is absolute addend (Mac lift_m + chroma)
        var lifted = input * (1.0 - lift) + lift;
        // When lift is used as Mac's "L" additive in shadows sense, values can be outside [0,1].
        // Clamp intermediate for stability.
        lifted = Math.Max(0, lifted);
        var gained = Math.Max(0, lifted * gain);
        var outv = Math.Pow(gained, invGamma);
        return Math.Clamp(outv, 0.0, 1.0);
    }

    /// <summary>Fully-saturated hue at h ∈ [0,1).</summary>
    public static (double R, double G, double B) HueRgb(double h)
    {
        var x = (h - Math.Floor(h)) * 6;
        var f = x - Math.Floor(x);
        return ((int)x % 6) switch
        {
            0 => (1, f, 0),
            1 => (1 - f, 1, 0),
            2 => (0, 1, f),
            3 => (0, 1 - f, 1),
            4 => (f, 0, 1),
            _ => (1, 0, 1 - f),
        };
    }

    public static (double R, double G, double B) ChromaOffset(double x, double y)
    {
        var r = Math.Min(1, Math.Sqrt(x * x + y * y));
        if (r <= 1e-6) return (0, 0, 0);
        var (cr, cg, cb) = HueRgb(Math.Atan2(y, x) / (2 * Math.PI));
        var mean = (cr + cg + cb) / 3;
        return ((cr - mean) * r, (cg - mean) * r, (cb - mean) * r);
    }
}
