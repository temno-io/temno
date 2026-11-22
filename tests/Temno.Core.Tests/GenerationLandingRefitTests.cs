using System.Net;
using System.Net.Http.Headers;
using Temno.Core.Models;
using Temno.Core.Project;
using Temno.Core.Session;
using Temno.Generation;
using Temno.Mcp;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-405, end to end through <c>LandFromUrlAsync</c>: a clip placed on a generating placeholder has no
/// size to fit, so it gets the full-canvas box. When the result lands, the entry takes the landed file's
/// own size and the clip takes its real fit; a clip the user has scaled meanwhile keeps its box.
/// </summary>
[Collection("EnvRegistry")]
public sealed class GenerationLandingRefitTests : IDisposable
{
    private readonly string _folder;
    private readonly ProjectSession _session;

    // The 24 header bytes ImageDimensions reads: PNG signature, then an IHDR chunk of 1152 x 2048.
    private static readonly byte[] PortraitPng =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x04, 0x80, 0x00, 0x00, 0x08, 0x00,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    ];

    private sealed class PngDownload : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PortraitPng) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return Task.FromResult(response);
        }
    }

    public GenerationLandingRefitTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "temno-wp405-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_folder, "media"));
        _session = ProjectSession.CreateNew();
        _session.Save(_folder);
        ProjectHub.ActiveSession = _session;
        ToolExecutor.ResetGenerationService();
        ToolExecutor.GenerationServiceOverride = new GenerationService(
            new FakeGenerationClient(), new HttpClient(new PngDownload()),
            hostResolver: (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));
    }

    public void Dispose()
    {
        ToolExecutor.GenerationServiceOverride = null;
        ToolExecutor.ResetGenerationService();
        ProjectHub.ActiveSession = null;
        try { Directory.Delete(_folder, true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task LandedStill_RefitsTheClipPlacedWhileItGenerated()
    {
        var entry = new MediaManifestEntry
        {
            Id = Guid.NewGuid().ToString().ToUpperInvariant(),
            Name = "presenter",
            Type = ClipType.image,
            Source = MediaSource.Project("media/gen-presenter.png"),
            GenerationStatus = "failed: network lost",
            GenerationInput = new GenerationInput
            {
                Prompt = "a presenter",
                Model = "legacy-image",
                ResultUrls = ["https://cdn.example.test/out.png"],
                CreatedAt = DateTimeOffset.UtcNow,
            },
        };
        _session.Manifest.Entries.Add(entry);
        var placed = new Clip { MediaRef = entry.Id, MediaType = ClipType.image, SourceClipType = ClipType.image, DurationFrames = 30 };
        var scaled = new Clip
        {
            MediaRef = entry.Id, MediaType = ClipType.image, SourceClipType = ClipType.image,
            StartFrame = 30, DurationFrames = 30, Transform = new Transform { Width = 0.5, Height = 0.5 },
        };
        var track = _session.ActiveTimeline.Tracks.First(t => t.Type == ClipType.video);
        track.Clips.Add(placed);
        track.Clips.Add(scaled);
        Assert.Equal((1920, 1080), (_session.ActiveTimeline.Width, _session.ActiveTimeline.Height));

        Assert.True(ToolExecutor.RetryGeneration(entry.Id));
        for (var i = 0; i < 750 && entry.GenerationStatus is not null; i++)
            await Task.Delay(20);

        Assert.Null(entry.GenerationStatus);
        Assert.Equal((1152, 2048), (entry.SourceWidth, entry.SourceHeight));
        Assert.Equal(0.31640625, placed.Transform.Width, 10);
        Assert.Equal(1.0, placed.Transform.Height, 10);
        Assert.Equal(0.5, scaled.Transform.Width, 10);
        Assert.Equal(0.5, scaled.Transform.Height, 10);
    }
}
