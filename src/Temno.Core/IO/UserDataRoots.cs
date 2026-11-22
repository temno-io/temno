using Temno.Core.Account;
using Temno.Environments;

namespace Temno.Core.IO;

/// <summary>New (Temno) and legacy (Palmier Pro) user-data roots (WP-193).</summary>
public static class UserDataRoots
{
    public const string ProductDirectoryName = "Temno";
    public const string LegacyProductDirectoryName = "Palmier Pro";

    /// <summary>
    /// WP-334: a development build's LocalAppData directory. Temno-Dev and every development build keep their
    /// preferences, layout, MCP token, sign-in and caches apart from the production app's, which keeps <c>Temno</c>.
    /// </summary>
    public const string DevelopmentDirectoryName = "Temno-Dev";
    public const string HomeDirectoryName = ".temno";
    public const string LegacyHomeDirectoryName = ".palmier";

    public static string DocumentsRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            ProductDirectoryName);

    public static string LegacyDocumentsRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            LegacyProductDirectoryName);

    /// <summary>This build's LocalAppData root: <c>Temno</c> for production, <c>Temno-Dev</c> for development (WP-334).</summary>
    public static string LocalAppDataRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            HostedEndpoints.BuildEnvironment == TemnoEnvironment.Production ? ProductDirectoryName : DevelopmentDirectoryName);

    /// <summary>The root every build shared before WP-334, and the production app's root since.</summary>
    public static string SharedLocalAppDataRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ProductDirectoryName);

    public static string LegacyLocalAppDataRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            LegacyProductDirectoryName);

    public static string HomeRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            HomeDirectoryName);

    public static string LegacyHomeRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            LegacyHomeDirectoryName);
}
