using System.Text;
using Temno.Core.Models;
using Xunit;

namespace Temno.Core.Tests;

public sealed class HostedMediaManifestTests
{
    [Fact]
    public void RolesAndRepeatedSource_RoundTripInTheirQuotedOrder()
    {
        var manifest = new MediaManifest { Entries = [new MediaManifestEntry
        {
            Id = "result", GenerationInput = new GenerationInput
            {
                Operation = "image_to_video", SourceAssetIds = ["source", "source"],
                InputRoles = ["first_frame", "last_frame"]
            }
        }] };
        var restored = MediaManifest.Decode(manifest.Encode()).Entries.Single().GenerationInput!;
        Assert.Equal(new[] { "source", "source" }, restored.SourceAssetIds);
        Assert.Equal(new[] { "first_frame", "last_frame" }, restored.InputRoles);
        Assert.Contains("\"inputRoles\"", Encoding.UTF8.GetString(manifest.Encode()));
    }

    [Fact]
    public void OlderImageProvenanceNeedsNoRoleField()
    {
        var manifest = MediaManifest.Decode(Encoding.UTF8.GetBytes("""
            {"version":2,"entries":[{"id":"old","generationInput":{"operation":"edit_image","sourceAssetIds":["input"]}}]}
            """));
        Assert.Null(manifest.Entries.Single().GenerationInput!.InputRoles);
        Assert.DoesNotContain("inputRoles", Encoding.UTF8.GetString(manifest.Encode()));
    }
}
