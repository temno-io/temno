using System.Text;
using Temno.Core.Models;
using Xunit;

namespace Temno.Core.Tests;

public class MediaManifestTests
{
    [Fact]
    public void Decode_EmptyObject_Defaults()
    {
        var m = MediaManifest.Decode("{}"u8);
        Assert.Empty(m.Entries);
        Assert.Empty(m.Folders);
        Assert.Equal(1, m.Version); // Mac: decodeIfPresent ?? 1
    }

    [Fact]
    public void RoundTrip_BothSourceKinds()
    {
        var manifest = new MediaManifest
        {
            Version = 2,
            Entries =
            [
                new MediaManifestEntry
                {
                    Id = "ext-1",
                    Name = "External",
                    Type = ClipType.video,
                    Source = MediaSource.External("/abs/path/video.mp4"),
                    Duration = 5.0,
                    SourceWidth = 1920,
                    SourceHeight = 1080,
                    SourceFps = 30,
                    HasAudio = true,
                },
                new MediaManifestEntry
                {
                    Id = "proj-1",
                    Name = "Project-relative",
                    Type = ClipType.image,
                    Source = MediaSource.Project("media/img.png"),
                    Duration = 0,
                    FolderId = "folder-1",
                },
            ],
            Folders =
            [
                new MediaFolder { Id = "folder-1", Name = "Refs" },
            ],
        };

        var again = MediaManifest.Decode(manifest.Encode());
        Assert.Equal(2, again.Entries.Count);
        Assert.IsType<MediaSource.ExternalSource>(again.Entries[0].Source);
        Assert.Equal("/abs/path/video.mp4", ((MediaSource.ExternalSource)again.Entries[0].Source).AbsolutePath);
        Assert.IsType<MediaSource.ProjectSource>(again.Entries[1].Source);
        Assert.Equal("media/img.png", ((MediaSource.ProjectSource)again.Entries[1].Source).RelativePath);
        Assert.Single(again.Folders);
    }

    [Fact]
    public void Decode_SwiftShapedSourceJson()
    {
        var json = """
            {
              "version": 2,
              "entries": [
                {
                  "id": "a",
                  "name": "A",
                  "type": "video",
                  "source": { "external": { "absolutePath": "/tmp/a.mp4" } },
                  "duration": 1.5
                },
                {
                  "id": "b",
                  "name": "B",
                  "type": "image",
                  "source": { "project": { "relativePath": "media/b.png" } },
                  "duration": 0
                }
              ],
              "folders": []
            }
            """;
        var m = MediaManifest.Decode(Encoding.UTF8.GetBytes(json));
        Assert.Equal(2, m.Entries.Count);
        Assert.Equal("/tmp/a.mp4", ((MediaSource.ExternalSource)m.Entries[0].Source).AbsolutePath);
        Assert.Equal("media/b.png", ((MediaSource.ProjectSource)m.Entries[1].Source).RelativePath);
    }
}
