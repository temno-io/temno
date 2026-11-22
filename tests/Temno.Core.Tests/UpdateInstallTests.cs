using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using Temno.Core.Updates;
using Temno.Environments;
using Xunit;

namespace Temno.Core.Tests;

public sealed class UpdateInstallTests
{
    [Fact]
    public void EmptyUrl_IsNotAnInstall()
    {
        Assert.False(UpdateInstallPlan.TryCreate("  ", feedIsLocal: false, out var plan, out var error));
        Assert.Null(plan);
        Assert.Equal(UpdateChannelCopy.NoPackageUrl, error);
    }

    [Fact]
    public void HttpsMsix_IsARemotePackage()
    {
        Assert.True(UpdateInstallPlan.TryCreate(
            "https://example.test/temno/Temno.App.msix?sig=1",
            feedIsLocal: false,
            out var plan,
            out var error));
        Assert.Null(error);
        Assert.NotNull(plan);
        Assert.False(plan!.IsLocalFile);
        Assert.Null(plan.LocalPath);
    }

    [Theory]
    [InlineData("https://example.test/Temno.App.appinstaller")]
    [InlineData("https://example.test/Temno.App.msixbundle")]
    public void AppInstaller_AndBundle_AreRefused(string url)
    {
        Assert.False(UpdateInstallPlan.TryCreate(url, feedIsLocal: false, out var plan, out var error));
        Assert.Null(plan);
        Assert.Equal(UpdateChannelCopy.MsixOnly, error);
    }

    [Theory]
    [InlineData("http://example.test/Temno.App.msix")]
    [InlineData("HTTP://example.test/Temno.App.msix")]
    public void HttpPackage_IsRefused(string url)
    {
        Assert.False(UpdateInstallPlan.TryCreate(url, feedIsLocal: true, out var plan, out var error));
        Assert.Null(plan);
        Assert.Equal(UpdateChannelCopy.HttpsOnly, error);
    }

    [Fact]
    public void RootedPath_FromALocalFeed_IsALocalPackage()
    {
        var path = Path.Combine(Path.GetTempPath(), "Temno.App.msix");
        Assert.True(UpdateInstallPlan.TryCreate(path, feedIsLocal: true, out var plan, out _));
        Assert.True(plan!.IsLocalFile);
        Assert.Equal(path, plan.LocalPath);
    }

    [Fact]
    public void RootedPath_FromAnOnlineFeed_IsRefused()
    {
        var path = Path.Combine(Path.GetTempPath(), "Temno.App.msix");
        Assert.False(UpdateInstallPlan.TryCreate(path, feedIsLocal: false, out var plan, out var error));
        Assert.Null(plan);
        Assert.Equal(UpdateChannelCopy.OnlineFeedLocalPackage, error);
        Assert.False(UpdateInstallPlan.TryCreate(new Uri(path).AbsoluteUri, feedIsLocal: false, out _, out error));
        Assert.Equal(UpdateChannelCopy.OnlineFeedLocalPackage, error);
    }

    [Theory]
    [InlineData(@"\\attacker.example\share\Temno.App.msix")]
    [InlineData("//attacker.example/share/Temno.App.msix")]
    [InlineData("file://attacker.example/share/Temno.App.msix")]
    [InlineData("file:///%2F%2Fattacker.example/share/Temno.App.msix")]
    [InlineData(@"\\?\C:\updates\Temno.App.msix")]
    public void NetworkShare_IsRefused_EvenFromALocalFeed(string url)
    {
        Assert.False(UpdateInstallPlan.TryCreate(url, feedIsLocal: true, out var plan, out var error));
        Assert.Null(plan);
        Assert.Equal(UpdateChannelCopy.NetworkShare, error);
    }

    [Fact]
    public void FileUri_IsDecodedOnce()
    {
        // Decoding twice turned %252F into a second slash, and so a share path (WP-356 review).
        Assert.True(UpdateInstallPlan.TryCreate(
            "file:///%252Fattacker.example/share/Temno.App.msix", feedIsLocal: true, out var odd, out _));
        Assert.Equal(Path.GetFullPath("/%2Fattacker.example/share/Temno.App.msix"), odd!.LocalPath);
        Assert.False(odd.LocalPath!.StartsWith(@"\\", StringComparison.Ordinal));

        var named = Path.Combine(Path.GetTempPath(), "Temno a%20b.msix");
        Assert.True(UpdateInstallPlan.TryCreate(new Uri(named).AbsoluteUri, feedIsLocal: true, out var plan, out _));
        Assert.Equal(named, plan!.LocalPath);
    }

    [Fact]
    public void InstallerFailure_InProduction_NamesNoCertificate()
    {
        const string windows = "Deployment failed: the certificate is not trusted (0x800B0109).";
        var production = UpdateChannelCopy.InstallFailedFor(TemnoEnvironment.Production, windows);
        Assert.Equal(UpdateChannelCopy.InstallFailed, production);
        foreach (var word in new[] { "sign", "certificate", "SmartScreen", "0x" })
            Assert.DoesNotContain(word, production, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0x800B0109", UpdateChannelCopy.InstallFailedFor(TemnoEnvironment.Development, windows), StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_FromACheckResult_UsesTheFeedsLocality()
    {
        var path = Path.Combine(Path.GetTempPath(), "Temno.App.msix");
        var local = new UpdateCheckResult { Kind = UpdateCheckKind.UpdateAvailable, AppInstallerUrl = path, FeedIsLocal = true };
        var online = new UpdateCheckResult { Kind = UpdateCheckKind.UpdateAvailable, AppInstallerUrl = path };
        Assert.True(UpdateInstallPlan.TryCreate(local, out _, out _));
        Assert.False(UpdateInstallPlan.TryCreate(online, out _, out _));
    }

    [Theory]
    [InlineData("https://example.test/notes.html")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.test/Temno.App.msix")]
    public void NonPackageUrl_IsRejected(string url)
    {
        Assert.False(UpdateInstallPlan.TryCreate(url, feedIsLocal: false, out var plan, out var error));
        Assert.Null(plan);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void PowerShell_ReadsThePathFromTheEnvironment_NeverFromTheScript()
    {
        Assert.Equal("Add-AppxPackage -Path $env:TEMNO_UPDATE_PACKAGE", UpdateInstallPlan.PowerShell(false));
        Assert.Equal(
            "Add-AppxPackage -Path $env:TEMNO_UPDATE_PACKAGE -ForceApplicationShutdown",
            UpdateInstallPlan.PowerShell(true));
    }

    [Fact]
    public void Signature_RequiresTheAppxEntry()
    {
        var path = Path.Combine(Path.GetTempPath(), "temno-pkg-" + Guid.NewGuid().ToString("N") + ".msix");
        try
        {
            WriteZip(path, includeSignature: false);
            Assert.False(UpdatePackageSignature.HasAppxSignature(path));
            WriteZip(path, includeSignature: true);
            Assert.True(UpdatePackageSignature.HasAppxSignature(path));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void HonestyCopy_NamesUnsignedSmartScreenAndNoPurchasedCertificate()
    {
        Assert.Contains("unsigned", UpdateChannelCopy.SideloadHonesty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SmartScreen", UpdateChannelCopy.SideloadHonesty, StringComparison.Ordinal);
        Assert.Contains("no purchased certificate", UpdateChannelCopy.SideloadHonesty, StringComparison.Ordinal);
        Assert.Contains("unsigned", UpdateChannelCopy.UnsignedBlocked, StringComparison.OrdinalIgnoreCase);
        // WP-375: development builds update from the development download host, so the copy no longer says there is no host.
        Assert.DoesNotContain("CDN", UpdateChannelCopy.SideloadHonesty, StringComparison.Ordinal);
        Assert.DoesNotContain("CDN", UpdateChannelCopy.UnsignedBlocked, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionHonesty_OmitsCertificateAndCdn()
    {
        var sideload = UpdateChannelCopy.SideloadHonestyFor(TemnoEnvironment.Production);
        var blocked = UpdateChannelCopy.UnsignedBlockedFor(TemnoEnvironment.Production);
        Assert.Equal("", sideload);
        Assert.DoesNotContain("SmartScreen", blocked, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sign", blocked, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("certificate", blocked, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CDN", blocked, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LocalPackage_DoesNotCallHttp()
    {
        var path = Path.Combine(Path.GetTempPath(), "temno-pkg-" + Guid.NewGuid().ToString("N") + ".msix");
        var handler = new CountingHandler();
        try
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            Assert.True(UpdateInstallPlan.TryCreate(path, feedIsLocal: true, out var plan, out _));
            var fetcher = new UpdatePackageFetcher(handler);
            var materialized = await fetcher.MaterializeAsync(plan!, Path.GetTempPath());
            Assert.Equal(path, materialized);
            Assert.Equal(0, handler.Calls);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task RemotePackage_IsSaved()
    {
        var dir = Path.Combine(Path.GetTempPath(), "temno-upd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var handler = new BodyHandler([9, 8, 7]);
        try
        {
            Assert.True(UpdateInstallPlan.TryCreate("https://example.test/Temno.App.msix", feedIsLocal: false, out var plan, out _));
            var fetcher = new UpdatePackageFetcher(handler);
            var materialized = await fetcher.MaterializeAsync(plan!, dir);
            Assert.Equal(new byte[] { 9, 8, 7 }, await File.ReadAllBytesAsync(materialized));
            Assert.Equal(1, handler.Calls);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task RemotePackage_OverTheCap_LeavesNoFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "temno-upd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.True(UpdateInstallPlan.TryCreate("https://example.test/Temno.App.msix", feedIsLocal: false, out var plan, out _));
            var fetcher = new UpdatePackageFetcher(new BodyHandler([1, 2, 3, 4, 5]), maxBytes: 4);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fetcher.MaterializeAsync(plan!, dir));
            Assert.Contains("4 GB", error.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task RemotePackage_RedirectedToHttp_IsRefused_AndLeavesNoFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "temno-upd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.True(UpdateInstallPlan.TryCreate("https://example.test/Temno.App.msix", feedIsLocal: false, out var plan, out _));
            var fetcher = new UpdatePackageFetcher(new BodyHandler([1, 2, 3], new Uri("http://example.test/Temno.App.msix")));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fetcher.MaterializeAsync(plan!, dir));
            Assert.Equal(UpdateChannelCopy.HttpsOnly, error.Message);
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task RemotePackage_IsSavedUnderAnAppChosenName_NotTheUrls()
    {
        var dir = Path.Combine(Path.GetTempPath(), "temno-upd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // U+2019 and U+2018 are quotes to PowerShell; a URL may carry them in its file name.
            const string url = "https://example.test/Temno%E2%80%99%24(Write-Output%20x)%E2%80%98.msix";
            Assert.True(UpdateInstallPlan.TryCreate(url, feedIsLocal: false, out var plan, out _));
            var materialized = await new UpdatePackageFetcher(new BodyHandler([7])).MaterializeAsync(plan!, dir);
            Assert.Matches("^[0-9a-f]{32}\\.msix$", Path.GetFileName(materialized));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task HttpPlan_IsNeverFetched()
    {
        var handler = new CountingHandler();
        var plan = new UpdateInstallPlan { Source = new Uri("http://example.test/Temno.App.msix") };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new UpdatePackageFetcher(handler).MaterializeAsync(plan, Path.GetTempPath()));
        Assert.Equal(UpdateChannelCopy.HttpsOnly, error.Message);
        Assert.Equal(0, handler.Calls);
    }

    private static void WriteZip(string path, bool includeSignature)
    {
        if (File.Exists(path))
            File.Delete(path);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var manifest = archive.CreateEntry("AppxManifest.xml");
        using (var writer = new StreamWriter(manifest.Open()))
            writer.Write("<Package />");
        if (includeSignature)
        {
            var signature = archive.CreateEntry(UpdatePackageSignature.EntryName);
            using var writer = new StreamWriter(signature.Open());
            writer.Write("signed");
        }
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("HTTP should not be called for a local package.");
        }
    }

    private sealed class BodyHandler : HttpMessageHandler
    {
        private readonly byte[] _body;
        private readonly Uri? _finalUri;

        public BodyHandler(byte[] body, Uri? finalUri = null)
        {
            _body = body;
            _finalUri = finalUri;
        }

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_body),
                RequestMessage = _finalUri is null ? request : new HttpRequestMessage(HttpMethod.Get, _finalUri),
            });
        }
    }
}
