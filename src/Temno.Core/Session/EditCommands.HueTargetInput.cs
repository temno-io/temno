namespace Temno.Core.Session;

public sealed class HueTargetInput
{
    public double TargetHue { get; set; }
    public double? HueShift { get; set; }
    public double? SatScale { get; set; }
    public double? LumShift { get; set; }
}
