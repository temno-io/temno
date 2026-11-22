using System.Formats.Asn1;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Temno.Core.Updates;
using Temno.Environments;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-356. The fixtures are real MSIX packages from docs/verification/wp-356/make-fixtures.ps1, signed with
/// throwaway self-signed certificates that no store trusts. Tests that need a trusted signature inject the
/// verdict; the ones that ask Windows itself run only on Windows.
/// </summary>
public sealed class UpdatePackageVerificationTests
{
    private const string Signed = "fixture-1.2.0.0-signed.msix";
    private const string Unsigned = "fixture-1.2.0.0-unsigned.msix";
    private const string Tampered = "fixture-1.2.0.0-tampered.msix";
    private const string OtherPublisher = "other-publisher-1.2.0.0-signed.msix";

    private static readonly UpdateExpectation FixtureApp =
        new("io.temno.updatefixture", "CN=Temno Update Fixture", new Version(1, 1, 0, 0));

    [Fact]
    public void CorrectlySignedPackage_IsAccepted()
    {
        var check = UpdatePackageSignature.Verify(Fixture(Signed), FixtureApp, "1.2.0", Trusted);
        Assert.True(check.Accepted, check.Detail);
    }

    [Fact]
    public void UnsignedPackage_IsRefused_BeforeWindowsIsAsked()
    {
        var check = UpdatePackageSignature.Verify(Fixture(Unsigned), FixtureApp, "1.2.0", NeverAsked);
        Assert.Equal(UpdateRefusal.Unsigned, check.Refusal);
    }

    [Fact]
    public void OtherPublisher_IsRefused_EvenWhenWindowsTrustsIt()
    {
        var check = UpdatePackageSignature.Verify(Fixture(OtherPublisher), FixtureApp, "1.2.0", NeverAsked);
        Assert.Equal(UpdateRefusal.WrongPublisher, check.Refusal);
        Assert.Contains("CN=Not Temno", check.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void OtherApp_IsRefused()
    {
        var temno = FixtureApp with { Name = "io.temno.windows" };
        var check = UpdatePackageSignature.Verify(Fixture(Signed), temno, "1.2.0", NeverAsked);
        Assert.Equal(UpdateRefusal.WrongApp, check.Refusal);
    }

    [Fact]
    public void PublisherIsCompared_Exactly()
    {
        // Windows derives the app's identity from the exact string: a space makes it another app.
        var spaced = FixtureApp with { Publisher = "CN = Temno Update Fixture" };
        var check = UpdatePackageSignature.Verify(Fixture(Signed), spaced, "1.2.0", NeverAsked);
        Assert.Equal(UpdateRefusal.WrongPublisher, check.Refusal);
    }

    [Theory]
    [InlineData("1.3.0")]
    [InlineData("1.2.0.1")]
    [InlineData("")]
    [InlineData(null)]
    public void VersionTheFeedDidNotAnnounce_IsRefused(string? feedVersion)
    {
        var check = UpdatePackageSignature.Verify(Fixture(Signed), FixtureApp, feedVersion, NeverAsked);
        Assert.Equal(UpdateRefusal.VersionMismatch, check.Refusal);
    }

    [Theory]
    [InlineData("1.2.0")]
    [InlineData("v1.2.0.0")]
    public void FeedVersion_MayOmitTheRevision(string feedVersion)
    {
        var check = UpdatePackageSignature.Verify(Fixture(Signed), FixtureApp, feedVersion, Trusted);
        Assert.True(check.Accepted, check.Detail);
    }

    [Theory]
    [InlineData(1, 2, 0, 0)]
    [InlineData(2, 0, 0, 0)]
    public void PackageNotNewerThanTheInstalledApp_IsRefused(int major, int minor, int build, int revision)
    {
        var installed = FixtureApp with { InstalledVersion = new Version(major, minor, build, revision) };
        var check = UpdatePackageSignature.Verify(Fixture(Signed), installed, "1.2.0", NeverAsked);
        Assert.Equal(UpdateRefusal.NotNewer, check.Refusal);
    }

    [Fact]
    public void SignatureFromAnotherPublisher_IsRefused()
    {
        // The fixture's manifest with the other publisher's signature: the signer is not the package's publisher.
        var path = TempPackage();
        try
        {
            File.Copy(Fixture(Signed), path, overwrite: true);
            ReplaceEntry(path, UpdatePackageSignature.EntryName, ReadEntry(Fixture(OtherPublisher), UpdatePackageSignature.EntryName));
            var check = UpdatePackageSignature.Verify(path, FixtureApp, "1.2.0", NeverAsked);
            Assert.Equal(UpdateRefusal.WrongPublisher, check.Refusal);
            Assert.Contains("signed by CN=Not Temno", check.Detail, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("PKCX")]
    [InlineData("PKCXnot a signature")]
    [InlineData("a signature without the prefix")]
    public void UnreadableSignature_IsRefused(string contents)
    {
        var path = TempPackage();
        try
        {
            File.Copy(Fixture(Signed), path, overwrite: true);
            ReplaceEntry(path, UpdatePackageSignature.EntryName, Encoding.ASCII.GetBytes(contents));
            var check = UpdatePackageSignature.Verify(path, FixtureApp, "1.2.0", NeverAsked);
            Assert.Equal(UpdateRefusal.Tampered, check.Refusal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TwoManifests_AreNotAPackage()
    {
        var path = TempPackage();
        try
        {
            File.Copy(Fixture(Signed), path, overwrite: true);
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
            {
                var second = archive.CreateEntry("/AppxManifest.xml");
                using var writer = new StreamWriter(second.Open());
                writer.Write(Manifest("io.temno.updatefixture", "CN=Temno Update Fixture", "9.0.0.0"));
            }
            var check = UpdatePackageSignature.Verify(path, FixtureApp, "1.2.0", NeverAsked);
            Assert.Equal(UpdateRefusal.NotAPackage, check.Refusal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ManifestWithADocumentType_IsNotRead()
    {
        var path = TempPackage();
        try
        {
            WriteZip(path, "<!DOCTYPE Package [<!ENTITY x SYSTEM \"file:///C:/Windows/win.ini\">]>"
                + Manifest("io.temno.updatefixture", "CN=Temno Update Fixture", "1.2.0.0"));
            var check = UpdatePackageSignature.Verify(path, FixtureApp, "1.2.0", NeverAsked);
            Assert.Equal(UpdateRefusal.NotAPackage, check.Refusal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ManifestInAnotherNamespace_IsNotRead()
    {
        var path = TempPackage();
        try
        {
            WriteZip(path, Manifest("io.temno.updatefixture", "CN=Temno Update Fixture", "1.2.0.0")
                .Replace("foundation/windows10", "foundation/elsewhere", StringComparison.Ordinal));
            var check = UpdatePackageSignature.Verify(path, FixtureApp, "1.2.0", NeverAsked);
            Assert.Equal(UpdateRefusal.NotAPackage, check.Refusal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FileThatIsNotAZip_IsNotAPackage()
    {
        var path = TempPackage();
        try
        {
            File.WriteAllBytes(path, Encoding.ASCII.GetBytes("MZ this is not a package"));
            var check = UpdatePackageSignature.Verify(path, FixtureApp, "1.2.0", NeverAsked);
            Assert.Equal(UpdateRefusal.NotAPackage, check.Refusal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadSigner_FindsTheCertificateTheSignerInfoNames()
    {
        using var signer = UpdatePackageSignature.ReadSigner(ReadEntry(Fixture(Signed), UpdatePackageSignature.EntryName));
        Assert.NotNull(signer);
        Assert.Equal("CN=Temno Update Fixture", signer!.Subject);
    }

    [Fact]
    public void SignatureNamingTwoMatchingCertificates_IsNotRead()
    {
        // Windows could verify with one certificate while this reads the other, so neither is believed.
        var p7x = ReadEntry(Fixture(Signed), UpdatePackageSignature.EntryName);
        using (var once = UpdatePackageSignature.ReadSigner(p7x))
            Assert.NotNull(once);
        Assert.Null(UpdatePackageSignature.ReadSigner(WithCertificatesTwice(p7x)));

        var path = TempPackage();
        try
        {
            File.Copy(Fixture(Signed), path, overwrite: true);
            ReplaceEntry(path, UpdatePackageSignature.EntryName, WithCertificatesTwice(p7x));
            var check = UpdatePackageSignature.Verify(path, FixtureApp, "1.2.0", NeverAsked);
            Assert.Equal(UpdateRefusal.Tampered, check.Refusal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SignatureCarryingTheSignerAsPkcs12_IsNotRead()
    {
        // WP-404: the certificates come from a downloaded package, so each is loaded as one X.509 certificate only.
        // The constructor .NET 10 marks obsolete (SYSLIB0057) also loads a PKCS#12 blob, which would match the signer.
        var p7x = ReadEntry(Fixture(Signed), UpdatePackageSignature.EntryName);
        using var signer = UpdatePackageSignature.ReadSigner(p7x);
        Assert.NotNull(signer);
        var pkcs12 = signer!.Export(X509ContentType.Pkcs12);

        Assert.Null(UpdatePackageSignature.ReadSigner(
            WithCertificates(p7x, certificates => certificates.Select(c => c.Span.SequenceEqual(signer.RawData) ? pkcs12 : c))));
    }

    [Fact]
    public void PublisherNames_CompareAttributeByAttribute()
    {
        const string publisher = "CN=\"Wright Solutions Development, LLC\", O=\"Wright Solutions Development, LLC\", L=Austin, S=Texas, C=US";
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(new X500DistinguishedName(publisher), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        Assert.True(UpdatePackageSignature.PublisherNamesMatch(certificate.SubjectName, publisher));
        Assert.True(UpdatePackageSignature.PublisherNamesMatch(certificate.SubjectName, certificate.Subject));
        Assert.False(UpdatePackageSignature.PublisherNamesMatch(certificate.SubjectName, "CN=\"Wright Solutions Development, LLC\""));
        Assert.False(UpdatePackageSignature.PublisherNamesMatch(certificate.SubjectName, publisher.Replace("Austin", "Dallas", StringComparison.Ordinal)));
        Assert.False(UpdatePackageSignature.PublisherNamesMatch(certificate.SubjectName, "C=US, S=Texas, L=Austin, O=\"Wright Solutions Development, LLC\", CN=\"Wright Solutions Development, LLC\""));
        Assert.False(UpdatePackageSignature.PublisherNamesMatch(certificate.SubjectName, "not a name"));
    }

    [Fact]
    public void NoSignatureCheckOnThisPlatform_RefusesEveryPackage()
    {
        var check = UpdatePackageSignature.Verify(
            Fixture(Signed), FixtureApp, "1.2.0", _ => new UpdateTrustResult(UpdateTrustVerdict.Unavailable, 0));
        Assert.Equal(UpdateRefusal.Untrusted, check.Refusal);
    }

    [Fact]
    public void Windows_SaysTheTamperedPackageWasChanged()
    {
        if (!OperatingSystem.IsWindows())
            return;
        Assert.Equal(UpdateTrustVerdict.Tampered, UpdatePackageTrust.Verify(Fixture(Tampered)).Verdict);
        var check = UpdatePackageSignature.Verify(Fixture(Tampered), FixtureApp, "1.2.0");
        Assert.Equal(UpdateRefusal.Tampered, check.Refusal);
    }

    [Fact]
    public void Windows_DoesNotTrustASelfSignedPackage()
    {
        if (!OperatingSystem.IsWindows())
            return;
        Assert.Equal(UpdateTrustVerdict.Untrusted, UpdatePackageTrust.Verify(Fixture(Signed)).Verdict);
        var check = UpdatePackageSignature.Verify(Fixture(Signed), FixtureApp, "1.2.0");
        Assert.Equal(UpdateRefusal.Untrusted, check.Refusal);
    }

    [Fact]
    public void Windows_SaysTheUnsignedPackageIsUnsigned()
    {
        if (!OperatingSystem.IsWindows())
            return;
        Assert.Equal(UpdateTrustVerdict.Unsigned, UpdatePackageTrust.Verify(Fixture(Unsigned)).Verdict);
    }

    [Fact]
    public void ProductionRefusals_SayWhy_WithoutSigningWords()
    {
        foreach (var refusal in Enum.GetValues<UpdateRefusal>().Where(r => r != UpdateRefusal.None))
        {
            var production = UpdateChannelCopy.Refused(refusal, TemnoEnvironment.Production, "WinVerifyTrust 0x800B0109");
            Assert.False(string.IsNullOrWhiteSpace(production));
            Assert.Contains("Temno", production, StringComparison.Ordinal);
            foreach (var word in new[] { "sign", "certificate", "SmartScreen", "CDN", "LLC", "0x" })
                Assert.DoesNotContain(word, production, StringComparison.OrdinalIgnoreCase);

            var development = UpdateChannelCopy.Refused(refusal, TemnoEnvironment.Development, "WinVerifyTrust 0x800B0109");
            Assert.StartsWith(production, development, StringComparison.Ordinal);
            Assert.Contains("0x800B0109", development, StringComparison.Ordinal);
        }
    }

    private static UpdateTrustResult Trusted(string path) => new(UpdateTrustVerdict.Trusted, 0);

    private static UpdateTrustResult NeverAsked(string path)
        => throw new InvalidOperationException("Windows was asked about a package the earlier checks should refuse.");

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Updates", name);

    private static string TempPackage()
        => Path.Combine(Path.GetTempPath(), "temno-wp356-" + Guid.NewGuid().ToString("N") + ".msix");

    private static string Manifest(string name, string publisher, string version)
        => "<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\">"
           + $"<Identity Name=\"{name}\" Publisher=\"{publisher}\" Version=\"{version}\" /></Package>";

    private static void WriteZip(string path, string manifest)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(archive.CreateEntry(UpdatePackageManifest.EntryName).Open());
        writer.Write(manifest);
    }

    private static byte[] ReadEntry(string path, string name)
    {
        using var archive = ZipFile.OpenRead(path);
        using var stream = archive.GetEntry(name)!.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>The same SignedData with its certificate set listed twice.</summary>
    private static byte[] WithCertificatesTwice(byte[] p7x) =>
        WithCertificates(p7x, certificates => certificates.Concat(certificates));

    private static byte[] WithCertificates(
        byte[] p7x, Func<List<ReadOnlyMemory<byte>>, IEnumerable<ReadOnlyMemory<byte>>> replace)
    {
        var contentInfo = new AsnReader(p7x.AsMemory(4), AsnEncodingRules.BER).ReadSequence();
        var contentType = contentInfo.ReadObjectIdentifier();
        var signedData = contentInfo.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence();
        var head = new List<ReadOnlyMemory<byte>>
        {
            signedData.ReadEncodedValue(), signedData.ReadEncodedValue(), signedData.ReadEncodedValue(),
        };
        var set = signedData.ReadSetOf(skipSortOrderValidation: true, new Asn1Tag(TagClass.ContextSpecific, 0));
        var certificates = new List<ReadOnlyMemory<byte>>();
        while (set.HasData)
            certificates.Add(set.ReadEncodedValue());
        var tail = new List<ReadOnlyMemory<byte>>();
        while (signedData.HasData)
            tail.Add(signedData.ReadEncodedValue());

        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            writer.WriteObjectIdentifier(contentType);
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true)))
            using (writer.PushSequence())
            {
                foreach (var part in head)
                    writer.WriteEncodedValue(part.Span);
                using (writer.PushSetOf(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true)))
                {
                    foreach (var certificate in replace(certificates))
                        writer.WriteEncodedValue(certificate.Span);
                }
                foreach (var part in tail)
                    writer.WriteEncodedValue(part.Span);
            }
        }
        return [.. "PKCX"u8, .. writer.Encode()];
    }

    private static void ReplaceEntry(string path, string name, byte[] contents)
    {
        // Another process (a virus scanner) can hold a package copied a moment ago: two of three full runs on
        // 2026-10-24 failed here with a sharing violation (WP-375). Open it once it lets go.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
                archive.GetEntry(name)?.Delete();
                using var stream = archive.CreateEntry(name).Open();
                stream.Write(contents);
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(100);
            }
        }
    }
}
