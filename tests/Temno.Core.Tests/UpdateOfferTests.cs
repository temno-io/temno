using Temno.Core.Updates;
using Temno.Environments;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-402: a packaged official build offers Install; a build from source names the release and how to merge it.
/// WP-451: an official build that runs from a folder names the release and where to download it.
/// </summary>
public sealed class UpdateOfferTests
{
    private const UpdateDelivery Packaged = UpdateDelivery.InstallsPackage;
    private const UpdateDelivery Folder = UpdateDelivery.DownloadsRelease;
    private const UpdateDelivery Source = UpdateDelivery.MergesSource;

    private static UpdateCheckResult Available(string? url = "https://download.example.test/windows/Temno_0.1.0.4_x64.msix")
        => new()
        {
            Kind = UpdateCheckKind.UpdateAvailable,
            FeedVersion = "0.1.0.4",
            AppInstallerUrl = url,
            Message = "Update available: 0.1.0.4" + (url is null ? "" : "\n" + url),
        };

    [Fact]
    public void A_build_from_source_merges_and_an_official_one_installs_only_from_a_package()
    {
        Assert.Equal(Packaged, UpdateOffer.DeliveryFor(officialBuild: true, installedFromPackage: true));
        Assert.Equal(Folder, UpdateOffer.DeliveryFor(officialBuild: true, installedFromPackage: false));
        // A fork's own package is still a build from source: it never takes Temno's package.
        Assert.Equal(Source, UpdateOffer.DeliveryFor(officialBuild: false, installedFromPackage: true));
        Assert.Equal(Source, UpdateOffer.DeliveryFor(officialBuild: false, installedFromPackage: false));
    }

    [Fact]
    public void A_packaged_official_build_offers_install_as_before()
    {
        var result = Available();
        Assert.True(UpdateOffer.ShowsInstall(result, Packaged));
        Assert.False(UpdateOffer.ShowsDownload(result, Packaged));
        Assert.Equal(result.Message, UpdateOffer.Status(result, Packaged));
        Assert.Equal("Install update 0.1.0.4", UpdateOffer.Name(result, Packaged));
    }

    [Fact]
    public void A_packaged_official_build_without_a_package_url_says_so_and_offers_nothing()
    {
        var result = Available(url: null);
        Assert.False(UpdateOffer.ShowsInstall(result, Packaged));
        Assert.EndsWith(UpdateChannelCopy.NoPackageUrl, UpdateOffer.Status(result, Packaged));
    }

    [Fact]
    public void An_official_build_in_a_folder_names_the_release_and_where_to_download_it_and_no_install()
    {
        var result = Available();
        Assert.False(UpdateOffer.ShowsInstall(result, Folder));
        Assert.True(UpdateOffer.ShowsDownload(result, Folder));
        Assert.Equal(
            "Temno 0.1.0.4 is out. This copy runs from a folder, so it doesn't update itself: download the new release at temno.io and use it instead. Your projects and settings stay where they are.",
            UpdateOffer.Status(result, Folder));
        Assert.Equal("Temno 0.1.0.4 is out", UpdateOffer.Name(result, Folder));
        Assert.Equal("https://temno.io", UpdateChannelCopy.DownloadPageUrl);
        Assert.Equal("Open temno.io", UpdateChannelCopy.DownloadPageLabel);
    }

    [Fact]
    public void An_official_build_in_a_folder_needs_no_package_address_to_say_where_the_release_is()
    {
        // The page has the release whatever form it takes, so a feed that names only a version is enough.
        var result = Available(url: null);
        Assert.True(UpdateOffer.ShowsDownload(result, Folder));
        Assert.Equal(UpdateOffer.Status(Available(), Folder), UpdateOffer.Status(result, Folder));
        Assert.DoesNotContain(UpdateChannelCopy.NoPackageUrl, UpdateOffer.Status(result, Folder));

        var unnamed = new UpdateCheckResult { Kind = UpdateCheckKind.UpdateAvailable, Message = "Update available" };
        Assert.StartsWith("A new Temno release is out. This copy runs from a folder", UpdateOffer.Status(unnamed, Folder));
        Assert.Equal("A new Temno release is out", UpdateOffer.Name(unnamed, Folder));
    }

    [Fact]
    public void An_official_build_in_a_folder_never_says_install_or_merge()
    {
        foreach (var result in new[] { Available(), Available(url: null) })
        {
            var status = UpdateOffer.Status(result, Folder);
            Assert.DoesNotContain("Install", status, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("build folder", status, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("merge", status, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("built from source", status, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Install", UpdateOffer.Name(result, Folder), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void A_custom_build_shows_the_release_its_tag_and_how_to_merge_it_and_no_install()
    {
        var result = Available();
        Assert.False(UpdateOffer.ShowsInstall(result, Source));
        Assert.False(UpdateOffer.ShowsDownload(result, Source));

        var status = UpdateOffer.Status(result, Source);
        Assert.Equal(
            "Temno 0.1.0.4 is out, tagged v0.1.0.4. This copy was built from source, so it doesn't install Temno's package: merge the release into your copy and rebuild.",
            status);
        Assert.Equal("Temno 0.1.0.4 is out", UpdateOffer.Name(result, Source));
        Assert.Equal(
            "git fetch https://github.com/temno-io/temno tag v0.1.0.4\ngit merge v0.1.0.4",
            UpdateChannelCopy.SourceBuildMergeCommands(result.FeedVersion));
    }

    [Fact]
    public void A_build_that_installs_nothing_never_shows_the_wrong_publisher_refusal()
    {
        var refusal = UpdateChannelCopy.Refused(UpdateRefusal.WrongPublisher, TemnoEnvironment.Production);
        Assert.Equal("This update isn't from Temno, so it wasn't installed.", refusal);
        foreach (var delivery in new[] { Source, Folder })
        {
            foreach (var result in new[] { Available(), Available(url: null) })
            {
                Assert.DoesNotContain(refusal, UpdateOffer.Status(result, delivery));
                Assert.DoesNotContain("isn't from Temno", UpdateOffer.Status(result, delivery));
                Assert.DoesNotContain("isn't from Temno", UpdateOffer.Name(result, delivery));
            }
        }

        Assert.DoesNotContain("isn't from Temno", UpdateChannelCopy.SourceBuildRelease("0.1.0.4"));
        Assert.DoesNotContain("isn't from Temno", UpdateChannelCopy.SourceBuildRelease(null));
        Assert.DoesNotContain("isn't from Temno", UpdateChannelCopy.FolderBuildRelease("0.1.0.4"));
        Assert.DoesNotContain("isn't from Temno", UpdateChannelCopy.FolderBuildRelease(null));
    }

    [Fact]
    public void Other_results_read_the_same_for_every_build()
    {
        var upToDate = new UpdateCheckResult { Kind = UpdateCheckKind.UpToDate, Message = "You're up to date." };
        var unconfigured = new UpdateCheckResult { Kind = UpdateCheckKind.Unconfigured, Message = "" };
        var error = new UpdateCheckResult { Kind = UpdateCheckKind.Error, Message = UpdateChecker.UnreachableMessage };
        foreach (var result in new[] { upToDate, unconfigured, error })
        {
            Assert.Equal(UpdateOffer.Status(result, Packaged), UpdateOffer.Status(result, Source));
            Assert.Equal(UpdateOffer.Status(result, Packaged), UpdateOffer.Status(result, Folder));
            foreach (var delivery in new[] { Packaged, Folder, Source })
            {
                Assert.False(UpdateOffer.ShowsInstall(result, delivery));
                Assert.False(UpdateOffer.ShowsDownload(result, delivery));
            }
        }

        Assert.Equal(UpdateChecker.UnconfiguredMessage, UpdateOffer.Status(unconfigured, Source));
        Assert.False(UpdateOffer.ShowsInstall(null, Packaged));
        Assert.False(UpdateOffer.ShowsDownload(null, Folder));
    }

    [Fact]
    public void A_custom_build_without_a_feed_version_still_says_how_to_update()
    {
        var result = new UpdateCheckResult { Kind = UpdateCheckKind.UpdateAvailable, Message = "Update available" };
        Assert.Contains("merge the release into your copy and rebuild", UpdateOffer.Status(result, Source));
        Assert.Equal("A new Temno release is out", UpdateOffer.Name(result, Source));
        Assert.Equal("v0.1.0.4", UpdateChannelCopy.ReleaseTag(" 0.1.0.4 "));
    }
}
