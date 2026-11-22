using Temno.Core.Ui;
using Xunit;

namespace Temno.Core.Tests;

public sealed class ImageGenerationStatusLabelTests
{
    [Theory]
    [InlineData(null, "failed: old", false, "image")]
    [InlineData(null, null, true, "image · linked")]
    [InlineData("generate_image", "preparing", false, "Generating…")]
    [InlineData("edit_image", "generating", false, "Generating…")]
    [InlineData("edit_image", "downloading", false, "Downloading…")]
    [InlineData("generate_image", "failed: Insufficient credits.", false, "Failed: Insufficient credits.")]
    [InlineData("edit_image", null, false, "image")]
    public void Hosted_image_states_are_visible_without_changing_ordinary_media(string? operation, string? status, bool linked, string expected)
        => Assert.Equal(expected, ImageGenerationStatusLabel.Format("image", linked, operation, status));

    // WP-169: the view model passes "import_media" while a queued import's importInput is set.
    [Theory]
    [InlineData("downloading", "Downloading…")]
    [InlineData("failed: Import interrupted", "Failed: Import interrupted")]
    [InlineData(null, "video")]
    public void Queued_import_states_read_like_image_jobs(string? status, string expected)
        => Assert.Equal(expected, ImageGenerationStatusLabel.Format("video", linked: false, "import_media", status));
}
