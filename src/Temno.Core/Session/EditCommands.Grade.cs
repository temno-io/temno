using Temno.Core.Models;
using Temno.Core.Editing;
using System.Text.Json;

namespace Temno.Core.Session;

public static partial class EditCommands
{
    private sealed class GradeState
    {
        public double? Exposure { get; set; }
        public double? Temperature { get; set; }
        public double? Tint { get; set; }
        public double? Contrast { get; set; }
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
        public double? Vibrance { get; set; }
        public double? Saturation { get; set; }
        public GradeCurve? Curve { get; set; }
        public HueCurves? HueCurves { get; set; }
        public string? LutPath { get; set; }
        public double? LutIntensity { get; set; }

        public GradeState(List<Effect>? effects)
        {
            if (effects == null) return;
            foreach (var e in effects)
            {
                var p = e.Params;
                switch (e.Type)
                {
                    case "color.exposure":
                        if (p.TryGetValue("ev", out var ev)) Exposure = ev.Value;
                        break;
                    case "color.temperature":
                        if (p.TryGetValue("temperature", out var temp)) Temperature = temp.Value;
                        if (p.TryGetValue("tint", out var t)) Tint = t.Value;
                        break;
                    case "color.contrast":
                        if (p.TryGetValue("amount", out var amt)) Contrast = amt.Value;
                        break;
                    case "color.highlightsShadows":
                        if (p.TryGetValue("highlights", out var hl)) Highlights = hl.Value;
                        if (p.TryGetValue("shadows", out var sh)) Shadows = sh.Value;
                        break;
                    case "color.blacksWhites":
                        if (p.TryGetValue("blacks", out var bl)) Blacks = bl.Value;
                        if (p.TryGetValue("whites", out var wh)) Whites = wh.Value;
                        break;
                    case "color.vibrance":
                        if (p.TryGetValue("amount", out var vib)) Vibrance = vib.Value;
                        break;
                    case "color.saturation":
                        if (p.TryGetValue("amount", out var sat)) Saturation = sat.Value;
                        break;
                    case "color.curves":
                        if (p.TryGetValue("curve", out var cv) && cv.String != null) Curve = GradeCurve.Decode(cv.String);
                        break;
                    case "color.hueCurves":
                        if (p.TryGetValue("curves", out var hc) && hc.String != null) HueCurves = HueCurves.Decode(hc.String);
                        break;
                    case "color.lut":
                        if (p.TryGetValue("path", out var path)) LutPath = path.String;
                        if (p.TryGetValue("intensity", out var intensity)) LutIntensity = intensity.Value;
                        break;
                    case "color.wheels":
                        double lx = 0, ly = 0, gx = 0, gy = 0, hx = 0, hy = 0;
                        if (p.TryGetValue("lift_x", out var lift_x) && lift_x.Value.HasValue) lx = lift_x.Value.Value;
                        if (p.TryGetValue("lift_y", out var lift_y) && lift_y.Value.HasValue) ly = lift_y.Value.Value;
                        if (p.TryGetValue("lift_m", out var lift_m)) ShadowsLum = lift_m.Value;
                        (ShadowsHue, ShadowsAmount) = HueAmount(lx, ly);

                        if (p.TryGetValue("gamma_x", out var gamma_x) && gamma_x.Value.HasValue) gx = gamma_x.Value.Value;
                        if (p.TryGetValue("gamma_y", out var gamma_y) && gamma_y.Value.HasValue) gy = gamma_y.Value.Value;
                        if (p.TryGetValue("gamma_m", out var gamma_m)) MidsGamma = gamma_m.Value;
                        (MidsHue, MidsAmount) = HueAmount(gx, gy);

                        if (p.TryGetValue("gain_x", out var gain_x) && gain_x.Value.HasValue) hx = gain_x.Value.Value;
                        if (p.TryGetValue("gain_y", out var gain_y) && gain_y.Value.HasValue) hy = gain_y.Value.Value;
                        if (p.TryGetValue("gain_m", out var gain_m)) HighsGain = gain_m.Value;
                        (HighsHue, HighsAmount) = HueAmount(hx, hy);
                        break;
                }
            }
        }

        public GradeState(Dictionary<string, object> d)
        {
            Exposure = GetDouble(d, "exposure");
            Contrast = GetDouble(d, "contrast");
            Saturation = GetDouble(d, "saturation");
            Vibrance = GetDouble(d, "vibrance");
            Temperature = GetDouble(d, "temperature");
            Tint = GetDouble(d, "tint");
            Highlights = GetDouble(d, "highlights");
            Shadows = GetDouble(d, "shadows");
            Blacks = GetDouble(d, "blacks");
            Whites = GetDouble(d, "whites");
            ShadowsHue = GetDouble(d, "shadowsHue");
            ShadowsAmount = GetDouble(d, "shadowsAmount");
            ShadowsLum = GetDouble(d, "shadowsLum");
            MidsHue = GetDouble(d, "midsHue");
            MidsAmount = GetDouble(d, "midsAmount");
            MidsGamma = GetDouble(d, "midsGamma");
            HighsHue = GetDouble(d, "highsHue");
            HighsAmount = GetDouble(d, "highsAmount");
            HighsGain = GetDouble(d, "highsGain");

            var master = GetPoints(d, "masterCurve");
            var red = GetPoints(d, "redCurve");
            var green = GetPoints(d, "greenCurve");
            var blue = GetPoints(d, "blueCurve");
            if (master.Count > 0 || red.Count > 0 || green.Count > 0 || blue.Count > 0)
            {
                Curve = new GradeCurve { Master = master, Red = red, Green = green, Blue = blue };
            }

            if (d.TryGetValue("hueCurvesRaw", out var rawHcObj))
            {
                Dictionary<string, object>? rawHc = null;
                if (rawHcObj is Dictionary<string, object> dict) rawHc = dict;
                else if (rawHcObj is System.Text.Json.JsonElement element && element.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    try { rawHc = JsonSerializer.Deserialize<Dictionary<string, object>>(element.GetRawText()); } catch {}
                }

                if (rawHc != null)
                {
                    var hc = new HueCurves
                    {
                        HueVsHue = GetPoints(rawHc, "hueVsHue"),
                        HueVsSat = GetPoints(rawHc, "hueVsSat"),
                        HueVsLum = GetPoints(rawHc, "hueVsLum")
                    };
                    if (!hc.IsIdentity) HueCurves = hc;
                }
            }

            if (d.TryGetValue("lut", out var lutObj))
            {
                Dictionary<string, object>? lut = null;
                if (lutObj is Dictionary<string, object> dict) lut = dict;
                else if (lutObj is System.Text.Json.JsonElement element && element.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    try { lut = JsonSerializer.Deserialize<Dictionary<string, object>>(element.GetRawText()); } catch {}
                }

                if (lut != null)
                {
                    LutPath = lut.TryGetValue("path", out var path) ? path?.ToString() : null;
                    LutIntensity = GetDouble(lut, "strength");
                }
            }
        }

        private static double? GetDouble(Dictionary<string, object> d, string key)
        {
            if (d.TryGetValue(key, out var val) && val != null)
            {
                if (val is System.Text.Json.JsonElement elem)
                {
                    if (elem.ValueKind == System.Text.Json.JsonValueKind.Number) return elem.GetDouble();
                    return null;
                }
                return Convert.ToDouble(val);
            }
            return null;
        }

        private static List<CurvePoint> GetPoints(Dictionary<string, object> d, string key)
        {
            var pts = new List<CurvePoint>();
            if (d.TryGetValue(key, out var val) && val != null)
            {
                System.Collections.IEnumerable? list = null;
                if (val is System.Text.Json.JsonElement element && element.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    var rawList = new List<List<double>>();
                    foreach (var rowElem in element.EnumerateArray())
                    {
                        if (rowElem.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            var sub = new List<double>();
                            foreach (var itemElem in rowElem.EnumerateArray())
                            {
                                if (itemElem.ValueKind == System.Text.Json.JsonValueKind.Number)
                                {
                                    sub.Add(itemElem.GetDouble());
                                }
                            }
                            rawList.Add(sub);
                        }
                    }
                    list = rawList;
                }
                else if (val is System.Collections.IEnumerable enumerable)
                {
                    list = enumerable;
                }

                if (list != null)
                {
                    foreach (var row in list)
                    {
                        if (row is System.Collections.IEnumerable rowList)
                        {
                            var enumerator = rowList.GetEnumerator();
                            if (enumerator.MoveNext())
                            {
                                var xObj = enumerator.Current;
                                double x = 0;
                                if (xObj is System.Text.Json.JsonElement xElem) x = xElem.GetDouble();
                                else x = Convert.ToDouble(xObj);

                                if (enumerator.MoveNext())
                                {
                                    var yObj = enumerator.Current;
                                    double y = 0;
                                    if (yObj is System.Text.Json.JsonElement yElem) y = yElem.GetDouble();
                                    else y = Convert.ToDouble(yObj);

                                    pts.Add(new CurvePoint(x, y));
                                }
                            }
                        }
                    }
                }
            }
            return pts;
        }

        public void Apply(ColorArgs i)
        {
            if (i.Exposure.HasValue) Exposure = i.Exposure.Value;
            if (i.Temperature.HasValue) Temperature = i.Temperature.Value;
            if (i.Tint.HasValue) Tint = i.Tint.Value;
            if (i.Contrast.HasValue) Contrast = i.Contrast.Value;
            if (i.Highlights.HasValue) Highlights = i.Highlights.Value;
            if (i.Shadows.HasValue) Shadows = i.Shadows.Value;
            if (i.Blacks.HasValue) Blacks = i.Blacks.Value;
            if (i.Whites.HasValue) Whites = i.Whites.Value;
            if (i.ShadowsHue.HasValue) ShadowsHue = i.ShadowsHue.Value;
            if (i.ShadowsAmount.HasValue) ShadowsAmount = i.ShadowsAmount.Value;
            if (i.ShadowsLum.HasValue) ShadowsLum = i.ShadowsLum.Value;
            if (i.MidsHue.HasValue) MidsHue = i.MidsHue.Value;
            if (i.MidsAmount.HasValue) MidsAmount = i.MidsAmount.Value;
            if (i.MidsGamma.HasValue) MidsGamma = i.MidsGamma.Value;
            if (i.HighsHue.HasValue) HighsHue = i.HighsHue.Value;
            if (i.HighsAmount.HasValue) HighsAmount = i.HighsAmount.Value;
            if (i.HighsGain.HasValue) HighsGain = i.HighsGain.Value;
            if (i.Vibrance.HasValue) Vibrance = i.Vibrance.Value;
            if (i.Saturation.HasValue) Saturation = i.Saturation.Value;

            if (i.MasterCurve != null || i.RedCurve != null || i.GreenCurve != null || i.BlueCurve != null)
            {
                var c = Curve ?? new GradeCurve();
                if (i.MasterCurve != null) c.Master = i.MasterCurve.Select(p => new CurvePoint(Clamp3(p[0]), Clamp3(p[1]))).ToList();
                if (i.RedCurve != null) c.Red = i.RedCurve.Select(p => new CurvePoint(Clamp3(p[0]), Clamp3(p[1]))).ToList();
                if (i.GreenCurve != null) c.Green = i.GreenCurve.Select(p => new CurvePoint(Clamp3(p[0]), Clamp3(p[1]))).ToList();
                if (i.BlueCurve != null) c.Blue = i.BlueCurve.Select(p => new CurvePoint(Clamp3(p[0]), Clamp3(p[1]))).ToList();
                Curve = c;
            }

            if (i.HueCurves != null && i.HueCurves.Count > 0)
            {
                HueCurves = CompileHueCurves(i.HueCurves);
            }

            if (i.Lut != null)
            {
                if (!string.IsNullOrEmpty(i.Lut.Path)) LutPath = i.Lut.Path;
                if (i.Lut.Strength.HasValue) LutIntensity = Clamp3(Math.Min(1, Math.Max(0, i.Lut.Strength.Value)));
            }
        }

        public List<Effect> BuildStack()
        {
            var stack = new List<Effect>();
            if (Exposure.HasValue) stack.Add(Make("color.exposure", new() { { "ev", Clamp3(Exposure.Value) } }));
            if (Contrast.HasValue) stack.Add(Make("color.contrast", new() { { "amount", Clamp3(Contrast.Value) } }));
            if (Highlights.HasValue || Shadows.HasValue)
            {
                stack.Add(Make("color.highlightsShadows", new() { { "highlights", Clamp3(Highlights ?? 0) }, { "shadows", Clamp3(Shadows ?? 0) } }));
            }
            if (Blacks.HasValue || Whites.HasValue)
            {
                stack.Add(Make("color.blacksWhites", new() { { "blacks", Clamp3(Blacks ?? 0) }, { "whites", Clamp3(Whites ?? 0) } }));
            }
            if (Temperature.HasValue || Tint.HasValue)
            {
                stack.Add(Make("color.temperature", new() { { "temperature", Clamp3(Temperature ?? ColorTemperature.NeutralKelvin) }, { "tint", Clamp3(Tint ?? 0) } }));
            }
            if (Vibrance.HasValue) stack.Add(Make("color.vibrance", new() { { "amount", Clamp3(Vibrance.Value) } }));
            if (Saturation.HasValue) stack.Add(Make("color.saturation", new() { { "amount", Clamp3(Saturation.Value) } }));

            var wheelFields = new[] { ShadowsHue, ShadowsAmount, ShadowsLum, MidsHue, MidsAmount, MidsGamma, HighsHue, HighsAmount, HighsGain };
            if (wheelFields.Any(x => x.HasValue))
            {
                var (lx, ly) = Xy(ShadowsHue, ShadowsAmount);
                var (gx, gy) = Xy(MidsHue, MidsAmount);
                var (hx, hy) = Xy(HighsHue, HighsAmount);
                stack.Add(Make("color.wheels", new()
                {
                    { "lift_x", Clamp3(lx) }, { "lift_y", Clamp3(ly) }, { "lift_m", Clamp3(ShadowsLum ?? 0) },
                    { "gamma_x", Clamp3(gx) }, { "gamma_y", Clamp3(gy) }, { "gamma_m", Clamp3(MidsGamma ?? 1) },
                    { "gain_x", Clamp3(hx) }, { "gain_y", Clamp3(hy) }, { "gain_m", Clamp3(HighsGain ?? 1) }
                }));
            }

            if (Curve != null && !Curve.IsIdentity)
            {
                var json = Curve.Encoded();
                if (json != null)
                {
                    var e = new Effect { Type = "color.curves" };
                    e.Params["curve"] = new EffectParam { String = json };
                    stack.Add(e);
                }
            }

            if (HueCurves != null && !HueCurves.IsIdentity)
            {
                var json = HueCurves.Encoded();
                if (json != null)
                {
                    var e = new Effect { Type = "color.hueCurves" };
                    e.Params["curves"] = new EffectParam { String = json };
                    stack.Add(e);
                }
            }

            if (LutPath != null)
            {
                stack.Add(new Effect
                {
                    Type = "color.lut",
                    Params = new()
                    {
                        { "path", new EffectParam { String = LutPath } },
                        { "intensity", new EffectParam { Value = Clamp3(LutIntensity ?? 1) } }
                    }
                });
            }

            return stack;
        }

        private static double Clamp3(double v) => Math.Round(v * 1000.0) / 1000.0;

        private static Effect Make(string type, Dictionary<string, double> p)
        {
            var e = new Effect { Type = type };
            foreach (var pair in p)
            {
                e.Params[pair.Key] = new EffectParam { Value = pair.Value };
            }
            return e;
        }

        private static (double?, double?) HueAmount(double x, double y)
        {
            var amt = Math.Sqrt(x * x + y * y);
            if (amt > 1e-6)
            {
                var deg = Math.Atan2(y, x) * 180.0 / Math.PI;
                if (deg < 0) deg += 360;
                return (deg, amt);
            }
            return (null, null);
        }

        private static (double, double) Xy(double? hue, double? amount)
        {
            var h = (hue ?? 0) * Math.PI / 180.0;
            var r = amount ?? 0;
            return (r * Math.Cos(h), r * Math.Sin(h));
        }

        private static HueCurves CompileHueCurves(List<HueTargetInput> targets)
        {
            const double band = 0.06;
            static double Wrap01(double v) { var m = v % 1.0; return m < 0 ? m + 1 : m; }
            static void Bump(List<CurvePoint> pts, double center, double y)
            {
                pts.Add(new CurvePoint(Wrap01(center), y));
                pts.Add(new CurvePoint(Wrap01(center - band), HueCurves.NeutralY));
                pts.Add(new CurvePoint(Wrap01(center + band), HueCurves.NeutralY));
            }
            static List<CurvePoint> FinalizePoints(List<CurvePoint> pts)
            {
                if (pts.Count == 0) return new();
                var sorted = pts.OrderBy(p => Math.Abs(p.Y - HueCurves.NeutralY)).ToList();
                var byX = new Dictionary<double, CurvePoint>();
                foreach (var p in sorted)
                {
                    byX[Clamp3(p.X)] = new CurvePoint(Clamp3(p.X), Clamp3(p.Y));
                }
                return byX.Values.OrderBy(p => p.X).ToList();
            }

            var hue = new List<CurvePoint>();
            var sat = new List<CurvePoint>();
            var lum = new List<CurvePoint>();

            foreach (var t in targets)
            {
                var center = Wrap01(t.TargetHue / 360.0);
                if (t.HueShift.HasValue && Math.Abs(t.HueShift.Value) > 1e-6)
                {
                    Bump(hue, center, HueCurves.NeutralY + Math.Max(-30, Math.Min(30, t.HueShift.Value)) / 60.0);
                }
                if (t.SatScale.HasValue && Math.Abs(t.SatScale.Value - 1) > 1e-6)
                {
                    Bump(sat, center, HueCurves.NeutralY + (Math.Max(0, Math.Min(2, t.SatScale.Value)) - 1) / 2.0);
                }
                if (t.LumShift.HasValue && Math.Abs(t.LumShift.Value) > 1e-6)
                {
                    Bump(lum, center, HueCurves.NeutralY + Math.Max(-0.5, Math.Min(0.5, t.LumShift.Value)));
                }
            }

            return new HueCurves
            {
                HueVsHue = FinalizePoints(hue),
                HueVsSat = FinalizePoints(sat),
                HueVsLum = FinalizePoints(lum)
            };
        }
    }
}
