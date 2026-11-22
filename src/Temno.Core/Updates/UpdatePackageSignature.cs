using System.Formats.Asn1;
using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Temno.Core.Updates;

/// <summary>The installed app an update must match, as Windows records it: never a constant, so the release
/// publisher (WP-266) is expected from the first build that carries it.</summary>
public sealed record UpdateExpectation(string Name, string Publisher, Version InstalledVersion);

public enum UpdateRefusal
{
    None,
    NotAPackage,
    WrongApp,
    WrongPublisher,
    VersionMismatch,
    NotNewer,
    Unsigned,
    Tampered,
    Untrusted,
}

/// <summary>What the checks decided, and the detail a development build shows.</summary>
public sealed record UpdatePackageCheck(UpdateRefusal Refusal, string Detail = "")
{
    public bool Accepted => Refusal == UpdateRefusal.None;
}

/// <summary>
/// An MSIX signature lives in the package zip as <c>AppxSignature.p7x</c>: the bytes <c>PKCX</c>, then a
/// PKCS #7 SignedData. <c>Get-AuthenticodeSignature</c> reports that file as unsigned, so it cannot be the check.
/// <see cref="Verify"/> accepts a package only when it is the installed app, signed by the installed app's
/// publisher, at the version the feed announced, and Windows trusts the signature. The cheap checks run first;
/// Windows' verdict hashes the whole package, so it runs last.
/// </summary>
public static class UpdatePackageSignature
{
    public const string EntryName = "AppxSignature.p7x";

    private const string SignedDataOid = "1.2.840.113549.1.7.2";
    private const int MaxSignatureBytes = 1024 * 1024;

    private static ReadOnlySpan<byte> Prefix => "PKCX"u8;

    public static bool HasAppxSignature(string packagePath)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        return SingleEntry(archive, EntryName) is not null;
    }

    public static UpdatePackageCheck Verify(
        string packagePath,
        UpdateExpectation expected,
        string? feedVersion,
        Func<string, UpdateTrustResult>? trust = null)
    {
        UpdatePackageIdentity? identity;
        byte[]? signature;
        try
        {
            using var archive = ZipFile.OpenRead(packagePath);
            if (CountEntries(archive, UpdatePackageManifest.EntryName) != 1 || CountEntries(archive, EntryName) > 1)
                return new UpdatePackageCheck(UpdateRefusal.NotAPackage, "a package has one manifest and at most one signature");
            identity = UpdatePackageManifest.TryRead(archive);
            signature = ReadEntry(SingleEntry(archive, EntryName));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return new UpdatePackageCheck(UpdateRefusal.NotAPackage, ex.Message);
        }

        if (identity is null)
            return new UpdatePackageCheck(UpdateRefusal.NotAPackage, "no readable package identity");
        if (!string.Equals(identity.Name, expected.Name, StringComparison.OrdinalIgnoreCase))
            return new UpdatePackageCheck(UpdateRefusal.WrongApp, $"the package is {identity.Name}; this app is {expected.Name}");
        // Windows derives the app's family from the exact publisher string, so this comparison is exact too.
        if (!string.Equals(identity.Publisher, expected.Publisher, StringComparison.Ordinal))
            return new UpdatePackageCheck(UpdateRefusal.WrongPublisher, $"the package's publisher is {identity.Publisher}; this app's is {expected.Publisher}");
        if (!UpdateChecker.TryParseNumericVersion(feedVersion ?? "", out var announced)
            || UpdateChecker.Canonical(announced) != UpdateChecker.Canonical(identity.Version))
            return new UpdatePackageCheck(UpdateRefusal.VersionMismatch, $"the package is {identity.Version}; the feed announced {feedVersion}");
        if (UpdateChecker.Canonical(identity.Version) <= UpdateChecker.Canonical(expected.InstalledVersion))
            return new UpdatePackageCheck(UpdateRefusal.NotNewer, $"the package is {identity.Version}; this app is {expected.InstalledVersion}");
        if (signature is null)
            return new UpdatePackageCheck(UpdateRefusal.Unsigned, "the package has no " + EntryName);

        using (var signer = ReadSigner(signature))
        {
            if (signer is null)
                return new UpdatePackageCheck(UpdateRefusal.Tampered, EntryName + " is not a readable signature");
            if (!PublisherNamesMatch(signer.SubjectName, identity.Publisher))
                return new UpdatePackageCheck(UpdateRefusal.WrongPublisher, $"signed by {signer.Subject}; the package says {identity.Publisher}");
        }

        var verdict = (trust ?? UpdatePackageTrust.Verify)(packagePath);
        return verdict.Verdict switch
        {
            UpdateTrustVerdict.Trusted => new UpdatePackageCheck(UpdateRefusal.None),
            UpdateTrustVerdict.Unsigned => new UpdatePackageCheck(UpdateRefusal.Unsigned, "WinVerifyTrust " + verdict.CodeText),
            UpdateTrustVerdict.Tampered => new UpdatePackageCheck(UpdateRefusal.Tampered, "WinVerifyTrust " + verdict.CodeText),
            UpdateTrustVerdict.Unavailable => new UpdatePackageCheck(UpdateRefusal.Untrusted, "this platform cannot check a package signature"),
            _ => new UpdatePackageCheck(UpdateRefusal.Untrusted, "WinVerifyTrust " + verdict.CodeText),
        };
    }

    /// <summary>
    /// The certificate the one SignerInfo names, matched by issuer and serial number or by key identifier.
    /// Null for anything else: no prefix, not SignedData, several signers, or no single matching certificate.
    /// This reads the signature; <see cref="UpdatePackageTrust"/> is what proves it.
    /// </summary>
    public static X509Certificate2? ReadSigner(byte[] p7x)
    {
        if (p7x.Length <= Prefix.Length || !p7x.AsSpan(0, Prefix.Length).SequenceEqual(Prefix))
            return null;
        try
        {
            var contentInfo = new AsnReader(p7x.AsMemory(Prefix.Length), AsnEncodingRules.BER).ReadSequence();
            if (contentInfo.ReadObjectIdentifier() != SignedDataOid)
                return null;
            var signedData = contentInfo.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence();
            signedData.ReadInteger();
            signedData.ReadEncodedValue();
            signedData.ReadEncodedValue();

            var certificates = new List<ReadOnlyMemory<byte>>();
            if (signedData.HasData && signedData.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0)))
            {
                var set = signedData.ReadSetOf(skipSortOrderValidation: true, new Asn1Tag(TagClass.ContextSpecific, 0));
                while (set.HasData)
                {
                    var choice = set.PeekTag();
                    var encoded = set.ReadEncodedValue();
                    if (choice.HasSameClassAndValue(Asn1Tag.Sequence))
                        certificates.Add(encoded);
                }
            }
            if (signedData.HasData && signedData.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 1)))
                signedData.ReadEncodedValue();

            var signerInfos = signedData.ReadSetOf(skipSortOrderValidation: true);
            var signerInfo = signerInfos.ReadSequence();
            if (signerInfos.HasData)
                return null;
            signerInfo.ReadInteger();

            Func<X509Certificate2, bool> isSigner;
            var sid = signerInfo.PeekTag();
            if (sid.HasSameClassAndValue(Asn1Tag.Sequence))
            {
                var issuerAndSerial = signerInfo.ReadSequence();
                var issuer = issuerAndSerial.ReadEncodedValue().ToArray();
                var serial = issuerAndSerial.ReadInteger();
                isSigner = c => c.IssuerName.RawData.AsSpan().SequenceEqual(issuer)
                    && new BigInteger(c.SerialNumberBytes.Span, isUnsigned: false, isBigEndian: true) == serial;
            }
            else if (sid.HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0)))
            {
                var keyId = signerInfo.ReadOctetString(new Asn1Tag(TagClass.ContextSpecific, 0));
                isSigner = c => c.Extensions.OfType<X509SubjectKeyIdentifierExtension>()
                    .Any(e => e.SubjectKeyIdentifierBytes.Span.SequenceEqual(keyId));
            }
            else
            {
                return null;
            }

            // Two certificates that both match would let Windows verify with one while this reads the other.
            X509Certificate2? signer = null;
            var matches = 0;
            foreach (var encoded in certificates)
            {
                // The package is untrusted: each entry loads as one X.509 certificate, never as PKCS#12 as the constructor would.
                var certificate = X509CertificateLoader.LoadCertificate(encoded.Span);
                if (isSigner(certificate) && ++matches == 1)
                    signer = certificate;
                else
                    certificate.Dispose();
            }
            if (matches == 1)
                return signer;
            signer?.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is AsnContentException or CryptographicException)
        {
            return null;
        }
    }

    /// <summary>
    /// The certificate's subject against a manifest's <c>Publisher</c>, attribute by attribute, as Windows requires
    /// them to agree. A publisher string that does not parse matches nothing.
    /// </summary>
    public static bool PublisherNamesMatch(X500DistinguishedName subject, string publisher)
    {
        X500DistinguishedName declared;
        try
        {
            declared = new X500DistinguishedName(publisher);
        }
        catch (CryptographicException)
        {
            return false;
        }

        var left = subject.EnumerateRelativeDistinguishedNames().ToList();
        var right = declared.EnumerateRelativeDistinguishedNames().ToList();
        if (left.Count == 0 || left.Count != right.Count)
            return false;
        for (var i = 0; i < left.Count; i++)
        {
            var a = left[i];
            var b = right[i];
            if (a.HasMultipleElements || b.HasMultipleElements)
            {
                if (!a.RawData.Span.SequenceEqual(b.RawData.Span))
                    return false;
                continue;
            }
            if (a.GetSingleElementType().Value != b.GetSingleElementType().Value
                || !string.Equals(a.GetSingleElementValue(), b.GetSingleElementValue(), StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    /// <summary>The one entry with this name, or null when it is absent or repeated.</summary>
    internal static ZipArchiveEntry? SingleEntry(ZipArchive archive, string name)
    {
        ZipArchiveEntry? found = null;
        foreach (var entry in archive.Entries)
        {
            if (!IsNamed(entry, name))
                continue;
            if (found is not null)
                return null;
            found = entry;
        }
        return found;
    }

    private static int CountEntries(ZipArchive archive, string name)
        => archive.Entries.Count(entry => IsNamed(entry, name));

    private static bool IsNamed(ZipArchiveEntry entry, string name)
        => entry.FullName.Replace('\\', '/').TrimStart('/').Equals(name, StringComparison.OrdinalIgnoreCase);

    private static byte[]? ReadEntry(ZipArchiveEntry? entry)
    {
        if (entry is null)
            return null;
        if (entry.Length > MaxSignatureBytes)
            return [];
        // The declared length is the archive's claim; the cap holds against what actually decompresses.
        using var stream = entry.Open();
        var buffer = new byte[MaxSignatureBytes + 1];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
            total += read;
        return total > MaxSignatureBytes ? [] : buffer[..total];
    }
}
