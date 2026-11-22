using System.Text.Json;
using System.Text.Json.Nodes;
using Temno.Core.Models;
using Temno.Mcp;
using Temno.Media.Export;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// Local MCP clients read tool schemas from <see cref="McpToolRegistry"/>; the hosted agent reads the
/// Gateway's copy, <c>src/Temno.Gateway/Agent/approved-tools.json</c>, which ships only with a Gateway
/// deploy. WP-329 makes that copy an export of the registry: this test fails when the two drift, and
/// regenerates the file when <c>TEMNO_UPDATE_HOSTED_TOOLS=1</c> is set. WP-275's export flag tests stay,
/// since they pin the registry text to what the exporter does.
/// </summary>
public sealed class HostedToolSchemaParityTests
{
    private const string HostedToolsRelativePath = "src/Temno.Gateway/Agent/approved-tools.json";
    private const string UpdateVariable = "TEMNO_UPDATE_HOSTED_TOOLS";

    /// <summary>
    /// The registry's implemented tools in registry order, as the OpenAI-style function list the Gateway
    /// loads. The default encoder keeps the file's existing escaping (' and friends); the file is
    /// written with LF, and compared with line endings normalized.
    /// </summary>
    public static string ExportHostedTools()
    {
        var tools = new JsonArray();
        foreach (var tool in McpToolRegistry.Tools.Where(t => ToolExecutor.ImplementedTools.Contains(t.Name)))
        {
            tools.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = tool.InputSchema.DeepClone(),
                },
            });
        }
        return tools.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\n");
    }

    [Fact]
    public void ApprovedTools_IsTheRegistryExport()
    {
        var expected = ExportHostedTools();
        var path = FindRepoFile(HostedToolsRelativePath);
        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            File.WriteAllText(path, expected);
            return;
        }

        Assert.True(
            expected == File.ReadAllText(path).ReplaceLineEndings("\n"),
            $"{HostedToolsRelativePath} differs from the registry. Run this test once with {UpdateVariable}=1 to regenerate it.");
    }

    [Fact]
    public void ApprovedTools_OfferTheQuoteTools()
    {
        var names = JsonNode.Parse(ExportHostedTools())!.AsArray()
            .Select(t => t!["function"]!["name"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("quote_image", names);
        Assert.Contains("edit_image", names);
        Assert.Contains("quote_media", names);
    }

    [Fact]
    public void EveryArraySchema_DeclaresItsItems()
    {
        var missing = new List<string>();
        foreach (var tool in McpToolRegistry.Tools)
            Walk(tool.InputSchema, tool.Name);
        Assert.Empty(missing);

        void Walk(JsonNode? node, string path)
        {
            if (node is not JsonObject schema) return;
            if (schema["type"] is JsonValue type && type.TryGetValue<string>(out var name) && name == "array"
                && schema["items"] is not JsonObject)
                missing.Add(path);
            if (schema["properties"] is JsonObject properties)
                foreach (var (key, property) in properties)
                    Walk(property, path + "." + key);
            Walk(schema["items"], path + "[]");
            foreach (var keyword in new[] { "anyOf", "oneOf", "allOf" })
                if (schema[keyword] is JsonArray branches)
                    for (var i = 0; i < branches.Count; i++)
                        Walk(branches[i], $"{path}.{keyword}[{i}]");
        }
    }

    [Theory]
    [InlineData("generate_video")]
    [InlineData("generate_image")]
    [InlineData("generate_audio")]
    [InlineData("upscale_media")]
    [InlineData("quote_media")]
    public void GenerationSchemas_DescribeNoHostedLegacyRoute(string tool)
    {
        var text = McpToolRegistry.Tools.Single(t => t.Name == tool).Description
            + McpToolRegistry.Tools.Single(t => t.Name == tool).InputSchema.ToJsonString();
        Assert.DoesNotContain("legacy model", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("legacy path", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("legacy arguments", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expanded", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("H3", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("export_project")]
    [InlineData("save_export_preset")]
    public void MaxRenderQuality_SaysItIsASoftwareCrfTarget(string tool)
    {
        var text = RegistryDescription(tool, "maxRenderQuality");
        Assert.DoesNotContain("Lanczos", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CRF 18 instead of 23", text);
        Assert.Contains("(x264, x265)", text);
        Assert.Contains("No effect on hardware encoders", text);
        Assert.Contains("ProRes", text);
    }

    [Theory]
    [InlineData("export_project")]
    [InlineData("save_export_preset")]
    public void MaxBitDepth_SaysTheRenderStaysEightBit(string tool)
    {
        var text = RegistryDescription(tool, "maxBitDepth");
        Assert.Contains("HEVC 10-bit tagged HLG", text);
        Assert.Contains("rendered in 8-bit SDR and converted", text);
        Assert.DoesNotContain("Main10", text);
    }

    // The behaviour the maxRenderQuality text states: the CRF reaches only x264 and x265, and only
    // when no bitrate was set.
    [Theory]
    [InlineData("libx264", VideoCodec.H264, false, true)]
    [InlineData("libx265", VideoCodec.H265, false, true)]
    [InlineData("libx264", VideoCodec.H264, true, false)]
    [InlineData("libopenh264", VideoCodec.H264, false, false)]
    [InlineData("h264_nvenc", VideoCodec.H264, false, false)]
    [InlineData("hevc_nvenc", VideoCodec.H265, false, false)]
    [InlineData("h264_qsv", VideoCodec.H264, false, false)]
    [InlineData("hevc_amf", VideoCodec.H265, false, false)]
    [InlineData("prores_ks", VideoCodec.ProRes, false, false)]
    public void MaxRenderQuality_ChangesOnlyTheSoftwareCrf(string encoder, VideoCodec codec, bool explicitBitrate, bool changes)
    {
        string Args(int crf) => ExportEncodeArguments.BuildVideoPassArguments(
            encoder, 1920, 1080, 30, codec, 20, crf, explicitBitrate, writeFaststart: true, "out.mov");

        Assert.Equal(changes, Args(18) != Args(23));
        if (changes) Assert.Contains("-crf 18", Args(18));
    }

    // The behaviour the maxBitDepth text states: the frames piped to ffmpeg are 8-bit RGBA, and the
    // 10-bit HLG picture is a conversion of them.
    [Fact]
    public void MaxBitDepth_EncodesAConversionOfTheEightBitRender()
    {
        var args = ExportEncodeArguments.BuildVideoPassArguments(
            "libx265", 1920, 1080, 30, VideoCodec.HevcHdr, 20, 23, explicitBitrate: false, writeFaststart: true, "out.mov");

        Assert.Contains("-f rawvideo -pix_fmt rgba", args);
        Assert.Contains("zscale=t=arib-std-b67", args);
        Assert.Contains("-pix_fmt yuv420p10le", args);
    }

    private static string RegistryDescription(string tool, string property) =>
        McpToolRegistry.Tools.Single(t => t.Name == tool)
            .InputSchema["properties"]![property]!["description"]!.GetValue<string>();

    private static string FindRepoFile(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"{relativePath} was not found above {AppContext.BaseDirectory}.");
    }
}
