using Temno.Core.Project;
using Xunit;

namespace Temno.Core.Tests;

public class ProjectConstantsTests
{
    [Fact]
    public void PackageFilenames_MatchMac()
    {
        // WP-193: this app writes .temno and reads .palmier forever; the in-package
        // filenames themselves still match the Mac contract.
        Assert.Equal("temno", ProjectConstants.FileExtension);
        Assert.Equal("palmier", ProjectConstants.LegacyFileExtension);
        Assert.Equal("project.json", ProjectConstants.TimelineFilename);
        Assert.Equal("media.json", ProjectConstants.ManifestFilename);
        Assert.Equal("media", ProjectConstants.MediaDirectoryName);
        Assert.Equal("generation-log.json", ProjectConstants.GenerationLogFilename);
        Assert.Equal("thumbnail.jpg", ProjectConstants.ThumbnailFilename);
    }

    [Fact]
    public void AllocateUniquePackagePath_EmitsTemnoAndUniquifies()
    {
        var parent = Path.Combine(Path.GetTempPath(), "temno-alloc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        try
        {
            // Bare name gains .temno; legacy suffix is rewritten (strict .temno on write).
            Assert.Equal(
                Path.Combine(parent, "Show.temno"),
                ProjectConstants.AllocateUniquePackagePath(parent, "Show"));
            Assert.Equal(
                Path.Combine(parent, "Show.temno"),
                ProjectConstants.AllocateUniquePackagePath(parent, "Show.palmier"));

            // Collision appends " 2", leaving the taken path alone.
            Directory.CreateDirectory(Path.Combine(parent, "Show.temno"));
            Assert.Equal(
                Path.Combine(parent, "Show 2.temno"),
                ProjectConstants.AllocateUniquePackagePath(parent, "Show"));
        }
        finally
        {
            try { Directory.Delete(parent, recursive: true); } catch { /* ignore */ }
        }
    }
}
