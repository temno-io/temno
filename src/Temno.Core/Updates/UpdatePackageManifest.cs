using System.IO.Compression;
using System.Xml;

namespace Temno.Core.Updates;

/// <summary>The identity an MSIX declares. Windows keys an app on <see cref="Name"/> and the exact
/// <see cref="Publisher"/> string, so two packages are the same app only when both match.</summary>
public sealed record UpdatePackageIdentity(string Name, string Publisher, Version Version);

/// <summary>
/// Reads <c>AppxManifest.xml</c> from a downloaded package. The file is untrusted input: no DTD, no
/// resolver, a size cap, and a package with two manifests is not read at all.
/// </summary>
public static class UpdatePackageManifest
{
    public const string EntryName = "AppxManifest.xml";

    private const string FoundationNamespace = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    private const int MaxManifestCharacters = 1024 * 1024;

    /// <summary>Null when the archive has no single readable manifest with a complete identity.</summary>
    public static UpdatePackageIdentity? TryRead(ZipArchive archive)
    {
        var entry = UpdatePackageSignature.SingleEntry(archive, EntryName);
        if (entry is null || entry.Length > MaxManifestCharacters)
            return null;

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxManifestCharacters,
            IgnoreComments = true,
        };
        try
        {
            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, settings);
            if (reader.MoveToContent() != XmlNodeType.Element
                || reader.LocalName != "Package"
                || reader.NamespaceURI != FoundationNamespace)
                return null;

            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element
                    || reader.Depth != 1
                    || reader.LocalName != "Identity"
                    || reader.NamespaceURI != FoundationNamespace)
                    continue;

                var name = reader.GetAttribute("Name");
                var publisher = reader.GetAttribute("Publisher");
                if (string.IsNullOrWhiteSpace(name)
                    || string.IsNullOrWhiteSpace(publisher)
                    || !Version.TryParse(reader.GetAttribute("Version"), out var version)
                    || version.Revision < 0)
                    return null;
                return new UpdatePackageIdentity(name, publisher, version);
            }
        }
        catch (XmlException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }

        return null;
    }
}
