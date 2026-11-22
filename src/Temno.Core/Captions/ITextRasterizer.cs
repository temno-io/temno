using Temno.Core.Models;

namespace Temno.Core.Captions;

/// <summary>
/// Contract for rendering styled text into a pixel buffer. Temno.Core ships
/// no implementation: the real one lives in Temno.Media (DirectWrite on Windows,
/// GDI+ then stick-font fallbacks). Mac: Compositing/TextFrameRenderer.swift.
/// </summary>
public interface ITextRasterizer
{
    /// <summary>
    /// Rasterizes text with the given style onto a transparent canvas of
    /// canvasWidth x canvasHeight pixels. Effective font pixel size =
    /// FontSize * FontScale * (canvasHeight / 1080.0), mirroring Mac
    /// TextLayout's referenceCanvasHeight of 1080.
    /// </summary>
    TextRasterization Rasterize(string text, TextStyle style, int canvasWidth, int canvasHeight);
}

/// <summary>
/// One word of a per-word animated caption: a character range in the rasterized string plus the
/// state it should be drawn with. Mac: TextFrameRenderer.renderPerWord draws each word as its own
/// line with these values applied.
/// </summary>
/// <param name="Start">Index of the word's first character in the rasterized string.</param>
/// <param name="Length">Number of characters in the word.</param>
/// <param name="Opacity">0–1 alpha for this word alone.</param>
/// <param name="Scale">Uniform scale about the word's centre.</param>
/// <param name="OffsetY">Vertical offset in multiples of the font size, positive = down.</param>
/// <param name="Color">Fill colour for this word.</param>
/// <param name="Background">Optional block filled behind the word before it is drawn.</param>
public readonly record struct TextWordRun(
    int Start,
    int Length,
    double Opacity,
    double Scale,
    double OffsetY,
    TextStyle.RGBA Color,
    TextStyle.RGBA? Background);

/// <summary>
/// Top-down 32-bit BGRA buffer with premultiplied alpha; stride = Width * 4.
/// </summary>
public sealed class TextRasterization
{
    public int Width { get; }

    public int Height { get; }

    public byte[] Bgra { get; }

    public TextRasterization(int width, int height, byte[] bgra)
    {
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "width must be positive");
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height), "height must be positive");
        ArgumentNullException.ThrowIfNull(bgra);
        if (bgra.Length != checked(width * height * 4))
            throw new ArgumentException("Buffer length must be width * height * 4.", nameof(bgra));
        Width = width;
        Height = height;
        Bgra = bgra;
    }
}
