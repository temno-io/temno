using Temno.Core.Models;
using Temno.Media;
using Temno.Media.Compositing;
using Xunit;

namespace Temno.Core.Tests;

public class TransitionTests
{
    private const int W = 8;
    private const int H = 8;

    private static VideoFrame SolidFrame(byte r, byte g, byte b, byte a = 255)
    {
        var data = new byte[W * H * 4];
        for (var i = 0; i < data.Length; i += 4)
        {
            data[i] = r;
            data[i + 1] = g;
            data[i + 2] = b;
            data[i + 3] = a;
        }
        return new VideoFrame(W, H, data, "rgba");
    }

    private static CompositorLayer Layer(VideoFrame frame) => new(frame);

    private static void AssertFramesEqual(VideoFrame expected, VideoFrame actual)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        Assert.Equal(expected.Data.Length, actual.Data.Length);
        Assert.Equal(expected.Data, actual.Data);
    }

    private static void AssertPixel(VideoFrame frame, int x, int y, byte r, byte g, byte b, byte a = 255)
    {
        var i = (y * frame.Width + x) * 4;
        Assert.Equal(r, frame.Data[i]);
        Assert.Equal(g, frame.Data[i + 1]);
        Assert.Equal(b, frame.Data[i + 2]);
        Assert.Equal(a, frame.Data[i + 3]);
    }

    private static void AssertAllPixels(VideoFrame frame, byte r, byte g, byte b, byte a = 255)
    {
        for (var y = 0; y < frame.Height; y++)
        for (var x = 0; x < frame.Width; x++)
            AssertPixel(frame, x, y, r, g, b, a);
    }

    [Fact]
    public void CrossDissolve_Progress0_MatchesA()
    {
        using var a = SolidFrame(255, 0, 0);
        using var b = SolidFrame(0, 0, 255);
        using var result = TransitionRenderer.Render(Layer(a), Layer(b), TransitionType.CrossDissolve, 0);
        AssertFramesEqual(a, result);
    }

    [Fact]
    public void CrossDissolve_Progress1_MatchesB()
    {
        using var a = SolidFrame(255, 0, 0);
        using var b = SolidFrame(0, 0, 255);
        using var result = TransitionRenderer.Render(Layer(a), Layer(b), TransitionType.CrossDissolve, 1);
        AssertFramesEqual(b, result);
    }

    [Fact]
    public void CrossDissolve_ProgressHalf_IsBlended()
    {
        using var a = SolidFrame(255, 0, 0);     // red
        using var b = SolidFrame(0, 0, 255);     // blue
        using var result = TransitionRenderer.Render(Layer(a), Layer(b), TransitionType.CrossDissolve, 0.5);

        // Midpoint lerp: (127.5, 0, 127.5) → rounds to 128 or 127 depending on banker's rounding;
        // accept either adjacent byte for R/B, G stays 0, A stays 255.
        for (var i = 0; i < result.Data.Length; i += 4)
        {
            Assert.InRange(result.Data[i], 127, 128);       // R
            Assert.Equal(0, result.Data[i + 1]);             // G
            Assert.InRange(result.Data[i + 2], 127, 128);    // B
            Assert.Equal(255, result.Data[i + 3]);           // A
            // Must not be pure A or pure B.
            Assert.NotEqual(255, result.Data[i]);
            Assert.NotEqual(255, result.Data[i + 2]);
        }
    }

    [Fact]
    public void FadeToBlack_Progress0_MatchesA()
    {
        using var a = SolidFrame(200, 100, 50);
        using var b = SolidFrame(10, 20, 30);
        using var result = TransitionRenderer.Render(Layer(a), Layer(b), TransitionType.FadeToBlack, 0);
        AssertFramesEqual(a, result);
    }

    [Fact]
    public void FadeToBlack_ProgressHalf_IsBlack()
    {
        using var a = SolidFrame(200, 100, 50);
        using var b = SolidFrame(10, 20, 30);
        using var result = TransitionRenderer.Render(Layer(a), Layer(b), TransitionType.FadeToBlack, 0.5);
        AssertAllPixels(result, 0, 0, 0);
    }

    [Fact]
    public void SlideLeft_Progress0_AFullyVisible()
    {
        using var a = SolidFrame(255, 0, 0);
        using var b = SolidFrame(0, 255, 0);
        using var result = TransitionRenderer.Render(Layer(a), Layer(b), TransitionType.SlideLeft, 0);
        AssertFramesEqual(a, result);
    }

    [Fact]
    public void SlideLeft_Progress1_BFullyVisible()
    {
        using var a = SolidFrame(255, 0, 0);
        using var b = SolidFrame(0, 255, 0);
        using var result = TransitionRenderer.Render(Layer(a), Layer(b), TransitionType.SlideLeft, 1);
        AssertFramesEqual(b, result);
    }

    [Fact]
    public void SlideLeft_ProgressHalf_SplitsLeftARightB()
    {
        // Left half of canvas shows right half of A; right half shows left half of B.
        // With solid colors: left = A red, right = B green.
        using var a = SolidFrame(255, 0, 0);
        using var b = SolidFrame(0, 255, 0);
        using var result = TransitionRenderer.Render(Layer(a), Layer(b), TransitionType.SlideLeft, 0.5);

        AssertPixel(result, 0, 0, 255, 0, 0);       // far left still A
        AssertPixel(result, W - 1, 0, 0, 255, 0);   // far right is B
    }

    [Fact]
    public void FadeToWhite_ProgressHalf_IsWhite()
    {
        using var a = SolidFrame(10, 20, 30);
        using var b = SolidFrame(40, 50, 60);
        using var result = TransitionRenderer.Render(Layer(a), Layer(b), TransitionType.FadeToWhite, 0.5);
        AssertAllPixels(result, 255, 255, 255);
    }

    [Fact]
    public void Wipe_ProgressHalf_LeftBRightA()
    {
        using var a = SolidFrame(255, 0, 0);
        using var b = SolidFrame(0, 0, 255);
        using var result = TransitionRenderer.Render(Layer(a), Layer(b), TransitionType.Wipe, 0.5);

        AssertPixel(result, 0, 0, 0, 0, 255);       // left = B
        AssertPixel(result, W - 1, 0, 255, 0, 0);   // right = A
    }

    [Fact]
    public void TransitionType_Json_RoundTripsCamelCase()
    {
        var transition = new Transition
        {
            Type = TransitionType.FadeToBlack,
            DurationFrames = 12,
            ClipIdA = "A",
            ClipIdB = "B",
        };

        var json = System.Text.Json.JsonSerializer.Serialize(transition, JsonOptions.ForProject);
        Assert.Contains("\"type\": \"fadeToBlack\"", json);

        var back = System.Text.Json.JsonSerializer.Deserialize<Transition>(json, JsonOptions.ForProject);
        Assert.NotNull(back);
        Assert.Equal(TransitionType.FadeToBlack, back.Type);
        Assert.Equal(12, back.DurationFrames);
    }

    [Fact]
    public void TransitionType_Json_AcceptsLegacyCrossDissolve()
    {
        const string json = """{"id":"T1","type":"crossDissolve","durationFrames":8,"clipIdA":"A","clipIdB":"B"}""";
        var t = System.Text.Json.JsonSerializer.Deserialize<Transition>(json, JsonOptions.ForProject);
        Assert.NotNull(t);
        Assert.Equal(TransitionType.CrossDissolve, t.Type);
    }
}
