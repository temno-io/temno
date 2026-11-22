namespace Temno.Media.Search;

/// <summary>
/// A deterministic, model-free description of an RGBA/BGRA frame. All values
/// are normalized to 0..1 and the distribution contains eight coarse RGB bins.
/// </summary>
public sealed class VisualSignature
{
    private const double RedLuma = 0.2126;
    private const double GreenLuma = 0.7152;
    private const double BlueLuma = 0.0722;

    public double MeanRed { get; set; }
    public double MeanGreen { get; set; }
    public double MeanBlue { get; set; }
    public double MeanLuminance { get; set; }
    public double LuminanceContrast { get; set; }
    public double[] ColorDistribution { get; set; } = new double[8];

    public static VisualSignature FromFrame(VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Width <= 0 || frame.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(frame), "Visual signatures require positive frame dimensions.");

        var requiredLength = checked(frame.Width * frame.Height * 4);
        if (frame.Data.Length < requiredLength)
            throw new ArgumentException("Frame data is smaller than its declared RGBA/BGRA dimensions.", nameof(frame));

        var format = frame.PixelFormat.Trim();
        var isRgba = string.Equals(format, "rgba", StringComparison.OrdinalIgnoreCase);
        var isBgra = string.Equals(format, "bgra", StringComparison.OrdinalIgnoreCase);
        if (!isRgba && !isBgra)
            throw new NotSupportedException($"Visual signatures require RGBA or BGRA frames, not '{frame.PixelFormat}'.");

        var redIndex = isRgba ? 0 : 2;
        var blueIndex = isRgba ? 2 : 0;
        var signature = new VisualSignature();
        var totalWeight = 0.0;

        for (var offset = 0; offset < requiredLength; offset += 4)
        {
            var weight = frame.Data[offset + 3] / 255.0;
            if (weight <= 0) continue;

            var red = frame.Data[offset + redIndex] / 255.0;
            var green = frame.Data[offset + 1] / 255.0;
            var blue = frame.Data[offset + blueIndex] / 255.0;
            var luminance = Luminance(red, green, blue);

            totalWeight += weight;
            signature.MeanRed += red * weight;
            signature.MeanGreen += green * weight;
            signature.MeanBlue += blue * weight;
            signature.MeanLuminance += luminance * weight;
            signature.ColorDistribution[VisualColor.ToDistributionBin(red, green, blue)] += weight;
        }

        if (totalWeight <= 0)
            return signature;

        signature.MeanRed /= totalWeight;
        signature.MeanGreen /= totalWeight;
        signature.MeanBlue /= totalWeight;
        signature.MeanLuminance /= totalWeight;
        for (var i = 0; i < signature.ColorDistribution.Length; i++)
            signature.ColorDistribution[i] /= totalWeight;

        var variance = 0.0;
        for (var offset = 0; offset < requiredLength; offset += 4)
        {
            var weight = frame.Data[offset + 3] / 255.0;
            if (weight <= 0) continue;

            var red = frame.Data[offset + redIndex] / 255.0;
            var green = frame.Data[offset + 1] / 255.0;
            var blue = frame.Data[offset + blueIndex] / 255.0;
            var difference = Luminance(red, green, blue) - signature.MeanLuminance;
            variance += difference * difference * weight;
        }

        signature.LuminanceContrast = Math.Sqrt(variance / totalWeight);
        return signature;
    }

    public double DistanceTo(VisualSignature other)
    {
        ArgumentNullException.ThrowIfNull(other);

        var colorDistance = Math.Sqrt(
            Square(MeanRed - other.MeanRed)
            + Square(MeanGreen - other.MeanGreen)
            + Square(MeanBlue - other.MeanBlue)) / Math.Sqrt(3.0);
        var luminanceDistance = Math.Abs(MeanLuminance - other.MeanLuminance);
        var contrastDistance = Math.Abs(LuminanceContrast - other.LuminanceContrast);
        var distributionDistance = DistributionDistance(ColorDistribution, other.ColorDistribution);

        return colorDistance * 0.45
            + luminanceDistance * 0.15
            + contrastDistance * 0.15
            + distributionDistance * 0.25;
    }

    public double ColorDistance(VisualColor target)
        => Math.Sqrt(
            Square(MeanRed - target.Red)
            + Square(MeanGreen - target.Green)
            + Square(MeanBlue - target.Blue)) / Math.Sqrt(3.0);

    public double DistributionPresence(VisualColor target)
    {
        var distribution = ColorDistribution;
        return distribution is { Length: 8 }
            ? distribution[target.DistributionBin]
            : 0.0;
    }

    public string MeanColorHex
        => $"#{ToByte(MeanRed):X2}{ToByte(MeanGreen):X2}{ToByte(MeanBlue):X2}";

    private static double Luminance(double red, double green, double blue)
        => red * RedLuma + green * GreenLuma + blue * BlueLuma;

    private static double DistributionDistance(double[] left, double[] right)
    {
        if (left is not { Length: 8 } || right is not { Length: 8 }) return 1.0;

        var sum = 0.0;
        for (var i = 0; i < left.Length; i++)
            sum += Math.Abs(left[i] - right[i]);
        return Math.Clamp(sum / 2.0, 0.0, 1.0);
    }

    private static double Square(double value) => value * value;

    private static byte ToByte(double value)
        => (byte)Math.Clamp(
            (int)Math.Round(Math.Clamp(value, 0.0, 1.0) * 255.0, MidpointRounding.AwayFromZero),
            byte.MinValue,
            byte.MaxValue);
}

public readonly record struct VisualColor(double Red, double Green, double Blue)
{
    public int DistributionBin => ToDistributionBin(Red, Green, Blue);

    public static int ToDistributionBin(double red, double green, double blue)
        => (red >= 0.5 ? 4 : 0)
            | (green >= 0.5 ? 2 : 0)
            | (blue >= 0.5 ? 1 : 0);
}
