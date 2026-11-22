using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Temno.Core.Models;

public enum TextAlignment
{
    Left,
    Center,
    Right
}

/// <summary>
/// Mac raw values are lowercase ("left"/"center"/"right"); [JsonPropertyName]
/// on enum members is ignored by JsonStringEnumConverter, so a camelCase
/// naming policy is required for parity. Reads stay case-insensitive.
/// </summary>
public sealed class TextAlignmentJsonConverter : JsonStringEnumConverter<TextAlignment>
{
    public TextAlignmentJsonConverter() : base(JsonNamingPolicy.CamelCase) { }
}

public sealed class TextStyle
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    [JsonPropertyName("fontName")]
    public string FontName { get; set; } = "Helvetica-Bold";

    [JsonPropertyName("fontSize")]
    public double FontSize { get; set; } = 96;

    [JsonPropertyName("fontScale")]
    public double FontScale { get; set; } = 1.0;

    [JsonPropertyName("isBold")]
    public bool IsBold { get; set; } = true;

    [JsonPropertyName("isItalic")]
    public bool IsItalic { get; set; } = false;

    [JsonPropertyName("color")]
    public RGBA Color { get; set; } = new RGBA();

    [JsonPropertyName("alignment")]
    [JsonConverter(typeof(TextAlignmentJsonConverter))]
    public TextAlignment Alignment { get; set; } = TextAlignment.Center;

    [JsonPropertyName("shadow")]
    public ShadowStyle Shadow { get; set; } = new ShadowStyle();

    [JsonPropertyName("background")]
    public FillStyle Background { get; set; } = new FillStyle { Enabled = false, Color = new RGBA { R = 0, G = 0, B = 0, A = 0.6 } };

    [JsonPropertyName("border")]
    public FillStyle Border { get; set; } = new FillStyle { Enabled = false, Color = new RGBA { R = 0, G = 0, B = 0, A = 1 } };

    public sealed class RGBA
    {
        [JsonPropertyName("r")]
        public double R { get; set; } = 1.0;

        [JsonPropertyName("g")]
        public double G { get; set; } = 1.0;

        [JsonPropertyName("b")]
        public double B { get; set; } = 1.0;

        [JsonPropertyName("a")]
        public double A { get; set; } = 1.0;
    }

    public sealed class ShadowStyle
    {
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        [JsonPropertyName("color")]
        public RGBA Color { get; set; } = new RGBA { R = 0, G = 0, B = 0, A = 0.6 };

        [JsonPropertyName("offsetX")]
        public double OffsetX { get; set; } = 0;

        [JsonPropertyName("offsetY")]
        public double OffsetY { get; set; } = -2;

        [JsonPropertyName("blur")]
        public double Blur { get; set; } = 6;
    }

    public sealed class FillStyle
    {
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = false;

        [JsonPropertyName("color")]
        public RGBA Color { get; set; } = new RGBA();
    }
}

public sealed class TextStylePreset
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("style")]
    public TextStyle Style { get; set; } = new TextStyle();

    public TextStylePreset()
    {
        Id = Guid.NewGuid().ToString();
    }

    public TextStylePreset(string name, TextStyle style)
    {
        Id = Guid.NewGuid().ToString();
        Name = name;
        Style = style;
    }
}
