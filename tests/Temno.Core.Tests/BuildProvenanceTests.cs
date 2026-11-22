using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Temno.Environments;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>WP-402: the official-build stamp, the source commit and the release version a build carries.</summary>
public sealed class BuildProvenanceTests
{
    [Fact]
    public void This_build_carries_its_repository_s_stamp_commit_and_release_version()
    {
        var current = BuildProvenance.Current;
        var root = RepoRoot();
        var publicTree = File.Exists(Path.Combine(root, "Temno.Public.props"));

        // Temno's own repository builds official; the public repository's Temno.Public.props marks its builds as built from source.
        Assert.Equal(!publicTree, current.IsOfficial);
        var stamp = typeof(BuildProvenance).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == BuildProvenance.OfficialMetadataKey);
        Assert.Equal(publicTree ? "false" : "true", stamp.Value);

        // The release version: a release commit's Temno.Release.props, else the source manifest's Identity version,
        // so a build folder compares the version it is.
        var release = Path.Combine(root, "Temno.Release.props");
        string expected;
        if (File.Exists(release))
        {
            expected = XDocument.Load(release).Descendants().Single(e => e.Name.LocalName == "TemnoAppVersion").Value;
        }
        else
        {
            var manifest = XDocument.Load(Path.Combine(root, "src", "Temno.App", "Package.appxmanifest"));
            expected = manifest.Descendants().Single(e => e.Name.LocalName == "Identity").Attribute("Version")!.Value;
        }
        Assert.Equal(expected, current.AppVersion);

        // A test build made from a git checkout carries its commit; a source zip has none.
        if (Directory.Exists(Path.Combine(RepoRoot(), ".git")) || File.Exists(Path.Combine(RepoRoot(), ".git")))
            Assert.Matches("^[0-9a-f]{40}$", current.SourceCommit!);
    }

    [Theory]
    [InlineData("1.0.0+64e319933f0b636a2b33058afb7a5b5e39c71739", "64e319933f0b636a2b33058afb7a5b5e39c71739")]
    [InlineData("0.1.0.4+ABCDEF1", "abcdef1")]
    [InlineData("0.1.0.4+64e319933f0b636a2b33058afb7a5b5e39c71739.build42", "64e319933f0b636a2b33058afb7a5b5e39c71739")]
    [InlineData("0.1.0-windows", null)]
    [InlineData("1.0.0+abc", null)]
    [InlineData("1.0.0+notacommit", null)]
    [InlineData("1.0.0+64e319933f0b636a2b33058afb7a5b5e39c71739ff", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void The_commit_is_the_hex_build_metadata_of_the_informational_version(string? informational, string? commit)
        => Assert.Equal(commit, BuildProvenance.CommitFrom(informational));

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData(" true ", true)]
    [InlineData("false", false)]
    [InlineData("", false)]
    [InlineData("yes", false)]
    [InlineData("1", false)]
    public void Only_the_value_true_stamps_an_official_build(string value, bool official)
        => Assert.Equal(official, BuildProvenance.Read(AssemblyWith(value)).IsOfficial);

    [Fact]
    public void No_stamp_at_all_is_a_custom_build()
        => Assert.False(BuildProvenance.Read(typeof(Assert).Assembly).IsOfficial);

    [Fact]
    public void An_official_build_reads_as_before_and_a_custom_build_says_so_with_its_commit()
    {
        const string sha = "64e319933f0b636a2b33058afb7a5b5e39c71739";
        Assert.Equal("Version 0.1.0.4", new BuildProvenance(true, sha, "0.1.0.4").VersionLine("0.1.0.4"));
        Assert.Equal(
            "Version 0.1.0.4 · Built from source, commit 64e319933f0b",
            new BuildProvenance(false, sha, "0.1.0.4").VersionLine("0.1.0.4"));
        Assert.Equal("Version 0.1.0.4 · Built from source", new BuildProvenance(false, null, null).VersionLine("0.1.0.4"));
    }

    // WP-402 T1: a fork's package keeps the committed manifest's version (0.1.0.0); the release it merged is 0.1.0.4.
    [Fact]
    public void A_custom_build_compares_the_release_its_source_names_even_when_packaged()
    {
        var custom = new BuildProvenance(false, null, "0.1.0.4");
        Assert.Equal("0.1.0.4", custom.VersionToCompare("0.1.0.0", "0.1.0-windows"));
        Assert.Equal("0.1.0.4", custom.VersionToCompare(null, "0.1.0-windows"));
        Assert.Equal("0.1.0.0", new BuildProvenance(false, null, null).VersionToCompare("0.1.0.0", "0.1.0-windows"));
        Assert.Equal("0.1.0-windows", new BuildProvenance(false, null, null).VersionToCompare(null, "0.1.0-windows"));

        // An official build compares what Windows installed, as before; a build folder, the version its source names.
        var official = new BuildProvenance(true, null, "0.1.0.0");
        Assert.Equal("0.1.0.3", official.VersionToCompare("0.1.0.3", "0.1.0.3"));
        Assert.Equal("0.1.0.0", official.VersionToCompare(null, "0.1.0-windows"));
        Assert.Equal("0.1.0-windows", new BuildProvenance(true, null, null).VersionToCompare(null, "0.1.0-windows"));
    }

    [Fact]
    public void The_private_props_default_development_and_official_and_the_public_file_production_and_custom()
    {
        var root = RepoRoot();
        var props = File.ReadAllText(Path.Combine(root, "Directory.Build.props"));
        Assert.Contains("<TemnoEnvironment Condition=\"'$(TemnoEnvironment)' == ''\">development</TemnoEnvironment>", props);
        Assert.Contains("<TemnoOfficialBuild Condition=\"'$(TemnoOfficialBuild)' == ''\">true</TemnoOfficialBuild>", props);

        // The import comes before the development default, or the public file's default would never apply.
        var import = props.IndexOf("Temno.Public.props\" Condition=\"Exists(", StringComparison.Ordinal);
        Assert.True(import > 0 && import < props.IndexOf(">development</TemnoEnvironment>", StringComparison.Ordinal));
        // This repository keeps the file under scripts/public-export/public; only the export places it at the root.
        // The tests ship in the export too, where the root copy is the one to check.
        var source = Path.Combine(root, "scripts", "public-export", "public", "Temno.Public.props");
        var exported = Path.Combine(root, "Temno.Public.props");
        if (File.Exists(source))
            Assert.False(File.Exists(exported), "Only the public export has Temno.Public.props at its root.");
        var publicProps = File.ReadAllText(File.Exists(source) ? source : exported);
        Assert.Contains("<TemnoEnvironment Condition=\"'$(TemnoEnvironment)' == ''\">production</TemnoEnvironment>", publicProps);
        Assert.Contains("<TemnoOfficialBuild Condition=\"'$(TemnoOfficialBuild)' == ''\">false</TemnoOfficialBuild>", publicProps);
        Assert.Contains("Temno.Release.props\" Condition=\"Exists(", publicProps);
    }

    [Fact]
    public void The_whisper_fetch_script_pins_a_fixed_revision_and_build_msix_s_size_and_hash()
    {
        var root = RepoRoot();
        var script = File.ReadAllText(Path.Combine(root, "scripts", "ensure-whisper-model.ps1"));
        // The packages' own pin, which build-msix.ps1 and build-zip.ps1 both check (WP-451).
        var msix = File.ReadAllText(Path.Combine(root, "scripts", "lib", "app-payload.ps1"));

        Assert.DoesNotContain("resolve/main", script);
        Assert.Matches(new Regex("\\$revision = \"[0-9a-f]{40}\""), script);
        Assert.Contains("/resolve/$revision/$fileName", script);

        var bytes = Regex.Match(msix, "\\$whisperModelBytes = (\\d+)").Groups[1].Value;
        var sha = Regex.Match(msix, "\\$whisperModelSha256 = '([0-9A-F]{64})'").Groups[1].Value;
        Assert.NotEmpty(bytes);
        Assert.NotEmpty(sha);
        Assert.Contains("$pinnedBytes = " + bytes, script);
        Assert.Contains("$pinnedSha256 = \"" + sha + "\"", script);
    }

    internal static string RepoRoot()
    {
        var repo = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(repo, "TemnoWindows.sln")))
            repo = Path.GetDirectoryName(repo) ?? throw new InvalidOperationException("Repository root not found.");
        return repo;
    }

    private static Assembly AssemblyWith(string official)
    {
        var builder = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("Wp402Stamp" + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.Run);
        var ctor = typeof(AssemblyMetadataAttribute).GetConstructor(new[] { typeof(string), typeof(string) })!;
        builder.SetCustomAttribute(new CustomAttributeBuilder(ctor, new object[] { BuildProvenance.OfficialMetadataKey, official }));
        return builder;
    }
}
