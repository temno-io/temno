using Temno.Core.Editing;

namespace Temno.Core.Session;

public sealed class ColorArgs
{
    public bool? Reset { get; set; }
    public double? Exposure { get; set; }
    public double? Contrast { get; set; }
    public double? Saturation { get; set; }
    public double? Vibrance { get; set; }
    public double? Temperature { get; set; }
    public double? Tint { get; set; }
    public double? Highlights { get; set; }
    public double? Shadows { get; set; }
    public double? Blacks { get; set; }
    public double? Whites { get; set; }
    public double? ShadowsHue { get; set; }
    public double? ShadowsAmount { get; set; }
    public double? ShadowsLum { get; set; }
    public double? MidsHue { get; set; }
    public double? MidsAmount { get; set; }
    public double? MidsGamma { get; set; }
    public double? HighsHue { get; set; }
    public double? HighsAmount { get; set; }
    public double? HighsGain { get; set; }
    public List<List<double>>? MasterCurve { get; set; }
    public List<List<double>>? RedCurve { get; set; }
    public List<List<double>>? GreenCurve { get; set; }
    public List<List<double>>? BlueCurve { get; set; }
    public List<HueTargetInput>? HueCurves { get; set; }
    public LutInput? Lut { get; set; }
    public Dictionary<string, object>? Color { get; set; }
}
