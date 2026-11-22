using System.Text;
using Temno.Core.Models;
using Xunit;

namespace Temno.Core.Tests;

public class ProjectFileTests
{
    [Fact]
    public void Decode_ModernProjectFile_ReadsTimelines()
    {
        var json = """
            {
              "timelines": [
                {
                  "id": "T1",
                  "name": "Main",
                  "fps": 24,
                  "width": 1920,
                  "height": 1080,
                  "tracks": []
                }
              ],
              "activeTimelineId": "T1"
            }
            """;

        var file = ProjectFile.Decode(Encoding.UTF8.GetBytes(json));
        Assert.Single(file.Timelines);
        Assert.Equal("T1", file.Timelines[0].Id);
        Assert.Equal(24, file.Timelines[0].Fps);
        Assert.Equal("T1", file.ActiveTimelineId);
    }

    [Fact]
    public void Decode_LegacyBareTimeline_Wraps()
    {
        var json = """
            {
              "id": "LEGACY",
              "name": "Old",
              "fps": 30,
              "width": 1280,
              "height": 720,
              "tracks": []
            }
            """;

        var file = ProjectFile.Decode(Encoding.UTF8.GetBytes(json));
        Assert.Single(file.Timelines);
        Assert.Equal("LEGACY", file.Timelines[0].Id);
        Assert.Equal("LEGACY", file.ActiveTimelineId);
        Assert.NotNull(file.OpenTimelineIds);
        Assert.Contains("LEGACY", file.OpenTimelineIds!);
    }

    [Fact]
    public void Encode_RoundTrip_PreservesClipPlacement()
    {
        var file = new ProjectFile
        {
            ActiveTimelineId = "T1",
            Timelines =
            [
                new Timeline
                {
                    Id = "T1",
                    Fps = 30,
                    Tracks =
                    [
                        new Track
                        {
                            Type = ClipType.video,
                            Clips =
                            [
                                new Clip
                                {
                                    Id = "C1",
                                    MediaRef = "M1",
                                    StartFrame = 10,
                                    DurationFrames = 90,
                                }
                            ]
                        }
                    ]
                }
            ]
        };

        var bytes = file.Encode();
        var again = ProjectFile.Decode(bytes);
        var clip = again.Timelines[0].Tracks[0].Clips[0];
        Assert.Equal(10, clip.StartFrame);
        Assert.Equal(90, clip.DurationFrames);
        Assert.Equal(100, clip.EndFrame);
    }
}
