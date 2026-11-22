using Temno.Core.Models;
using Temno.Core.Ui;
using Xunit;

namespace Temno.Core.Tests;

public sealed class ClipPresentationTests
{
    [Fact]
    public void Imported_selection_resolves_its_manifest_name()
    {
        var id = Guid.NewGuid().ToString();
        var clip = new Clip { MediaRef = id, MediaType = ClipType.video };
        var manifest = new MediaManifest { Entries = [new MediaManifestEntry { Id = id, Name = "scene_capture.mov" }] };
        Assert.Equal("scene_capture.mov", ClipPresentation.Title(clip, manifest));
    }

    [Fact]
    public void Missing_entry_has_a_human_fallback_and_preserves_the_identifier()
    {
        var id = Guid.NewGuid().ToString();
        var clip = new Clip { MediaRef = id, MediaType = ClipType.audio };
        Assert.Equal("Audio clip", ClipPresentation.Title(clip, new MediaManifest()));
        Assert.Equal(id, clip.MediaRef);
    }

    [Theory]
    [InlineData("D:\\Footage\\take1.mov")]
    [InlineData("/Footage/take1.mov")]
    public void Legacy_path_references_display_only_the_filename(string path)
        => Assert.Equal("take1.mov", ClipPresentation.Title(new Clip { MediaRef = path }, null));

    [Fact]
    public void Caption_header_is_a_single_line_without_changing_the_caption()
    {
        var clip = new Clip { MediaType = ClipType.subtitle, TextContent = "First line\nSecond line" };
        Assert.Equal("First line Second line", ClipPresentation.Title(clip, null));
        Assert.Contains('\n', clip.TextContent);
    }
}
