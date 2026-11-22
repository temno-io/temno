using System.Text.Json;
using System.Text.Json.Serialization;

namespace Temno.Core.Models;

/// <summary>Mac: Models/Timeline.swift Transform</summary>
public sealed class Transform
{
    [JsonPropertyName("centerX")]
    public double CenterX { get; set; } = 0.5;

    [JsonPropertyName("centerY")]
    public double CenterY { get; set; } = 0.5;

    [JsonPropertyName("width")]
    public double Width { get; set; } = 1;

    [JsonPropertyName("height")]
    public double Height { get; set; } = 1;

    /// <summary>Degrees, positive = clockwise (Mac).</summary>
    [JsonPropertyName("rotation")]
    public double Rotation { get; set; }

    [JsonPropertyName("flipHorizontal")]
    public bool FlipHorizontal { get; set; }

    [JsonPropertyName("flipVertical")]
    public bool FlipVertical { get; set; }

    [JsonIgnore]
    public (double X, double Y) TopLeft => (CenterX - Width / 2, CenterY - Height / 2);

    [JsonIgnore]
    public (double X, double Y) Center => (CenterX, CenterY);

    public static Transform FromTopLeft(double x, double y, double width, double height) => new()
    {
        CenterX = x + width / 2,
        CenterY = y + height / 2,
        Width = width,
        Height = height,
    };

    /// <summary>Accept legacy x/y keys (Mac decoder).</summary>
    public static Transform FromJsonElement(JsonElement el)
    {
        var w = el.TryGetProperty("width", out var wEl) && wEl.TryGetDouble(out var wv) ? wv : 1;
        var h = el.TryGetProperty("height", out var hEl) && hEl.TryGetDouble(out var hv) ? hv : 1;
        double cx = 0.5, cy = 0.5;
        if (el.TryGetProperty("centerX", out var cxEl) && cxEl.TryGetDouble(out var cxv))
            cx = cxv;
        else if (el.TryGetProperty("x", out var xEl) && xEl.TryGetDouble(out var xv))
            cx = xv + w - 0.5;
        if (el.TryGetProperty("centerY", out var cyEl) && cyEl.TryGetDouble(out var cyv))
            cy = cyv;
        else if (el.TryGetProperty("y", out var yEl) && yEl.TryGetDouble(out var yv))
            cy = yv + h - 0.5;

        return new Transform
        {
            CenterX = cx,
            CenterY = cy,
            Width = w,
            Height = h,
            Rotation = el.TryGetProperty("rotation", out var rEl) && rEl.TryGetDouble(out var rv) ? rv : 0,
            FlipHorizontal = el.TryGetProperty("flipHorizontal", out var fh) && fh.ValueKind == JsonValueKind.True,
            FlipVertical = el.TryGetProperty("flipVertical", out var fv) && fv.ValueKind == JsonValueKind.True,
        };
    }

    public Transform Clone() => new()
    {
        CenterX = CenterX,
        CenterY = CenterY,
        Width = Width,
        Height = Height,
        Rotation = Rotation,
        FlipHorizontal = FlipHorizontal,
        FlipVertical = FlipVertical,
    };
}

public sealed class Crop
{
    [JsonPropertyName("left")]
    public double Left { get; set; }

    [JsonPropertyName("top")]
    public double Top { get; set; }

    [JsonPropertyName("right")]
    public double Right { get; set; }

    [JsonPropertyName("bottom")]
    public double Bottom { get; set; }

    [JsonIgnore]
    public bool IsIdentity => Left == 0 && Top == 0 && Right == 0 && Bottom == 0;

    public Crop Clone() => new()
    {
        Left = Left,
        Top = Top,
        Right = Right,
        Bottom = Bottom,
    };
}
