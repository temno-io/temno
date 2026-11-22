using System.Text;
using System.Text.Json;
using Temno.Core.Account;
using Temno.Core.Editing;
using Temno.Core.Models;
using Temno.Core.Session;
using Temno.Core.Transcription;
using Xunit;

namespace Temno.Core.Tests;

public class TranscriptionTests
{
    // ---- Transcript model JSON ----

    [Fact]
    public void TranscriptionResult_Decodes_MacShape_And_RoundTrips()
    {
        var json = """
        {
          "text": "hello um world",
          "language": "en-US",
          "words": [
            { "text": "hello", "start": 0.0, "end": 0.5, "speaker": "S1" },
            { "text": "um", "start": 1.0, "end": 1.5 },
            { "text": "world" }
          ],
          "segments": [
            { "text": "hello um world", "start": 0.0, "end": 2.5, "speaker": "S1" }
          ]
        }
        """;

        var result = JsonSerializer.Deserialize<TranscriptionResult>(json, JsonOptions.ForProject);
        Assert.NotNull(result);
        Assert.Equal("hello um world", result!.Text);
        Assert.Equal("en-US", result.Language);
        Assert.Equal(3, result.Words.Count);
        Assert.Equal("S1", result.Words[0].Speaker);
        Assert.Null(result.Words[1].Speaker);
        Assert.Null(result.Words[2].Start);
        Assert.Null(result.Words[2].End);
        Assert.Single(result.Segments);
        Assert.Equal(2.5, result.Segments[0].End);

        var encoded = JsonSerializer.Serialize(result, JsonOptions.ForProject);
        // Exact Mac field names; nulls omitted.
        Assert.Contains("\"start\"", encoded);
        Assert.Contains("\"segments\"", encoded);
        Assert.DoesNotContain("\"Speaker\"", encoded);
        Assert.DoesNotContain("\"speaker\": null", encoded);

        var again = JsonSerializer.Deserialize<TranscriptionResult>(encoded, JsonOptions.ForProject);
        Assert.NotNull(again);
        Assert.Equal(result.Words.Count, again!.Words.Count);
        Assert.Equal(result.Words[1].Start, again.Words[1].Start);
        Assert.Null(again.Words[2].Start);
    }

    [Fact]
    public void Offsetting_Shifts_All_Timestamps_And_Preserves_Nulls()
    {
        var result = new TranscriptionResult
        {
            Text = "a b",
            Words =
            [
                new TranscriptionWord { Text = "a", Start = 1.0, End = 1.5 },
                new TranscriptionWord { Text = "b" },
            ],
            Segments = [new TranscriptionSegment { Text = "a b", Start = 1.0, End = 2.0 }],
        };

        var shifted = result.Offsetting(10.0);
        Assert.Equal(11.0, shifted.Words[0].Start);
        Assert.Equal(11.5, shifted.Words[0].End);
        Assert.Null(shifted.Words[1].Start);
        Assert.Equal(11.0, shifted.Segments[0].Start);
        Assert.Equal(12.0, shifted.Segments[0].End);

        Assert.Same(result, result.Offsetting(0.0));
    }

    // ---- Providers ----

    [Fact]
    public async Task FakeProvider_Is_Deterministic_And_Records_Calls()
    {
        var provider = new FakeTranscriptionProvider();
        Assert.Equal(TranscriptionProvider.local, provider.Provider);

        var a = await provider.TranscribeAsync("/tmp/a.mp4", "fr-FR");
        var b = await provider.TranscribeAsync("/tmp/a.mp4", "fr-FR");

        Assert.Equal(2, provider.TranscribeCallCount);
        Assert.Equal("/tmp/a.mp4", provider.LastMediaPath);
        Assert.Equal("fr-FR", provider.LastLanguage);
        Assert.Equal("fr-FR", a.Language);
        Assert.Equal(a.Text, b.Text);
        Assert.Equal(a.Words.Count, b.Words.Count);
        for (var i = 0; i < a.Words.Count; i++)
        {
            Assert.Equal(a.Words[i].Start, b.Words[i].Start);
            Assert.Equal(a.Words[i].End, b.Words[i].End);
        }
    }

    [Fact]
    public async Task FakeProvider_Uses_Injected_Result_And_Guards_Path()
    {
        var canned = FakeTranscriptionProvider.MakeTranscript(["hi"], start: 2.0);
        var provider = new FakeTranscriptionProvider(canned, TranscriptionProvider.cloud);

        var result = await provider.TranscribeAsync("/tmp/x.mov");
        Assert.Same(canned, result);
        Assert.Equal(TranscriptionProvider.cloud, provider.Provider);
        Assert.Equal(2.0, result.Words[0].Start);

        await Assert.ThrowsAsync<ArgumentException>(() => provider.TranscribeAsync(""));
    }

    [Fact]
    public async Task CloudProvider_RequiresSignInAndGateway()
    {
        var provider = new CloudTranscriptionProvider(
            new StubWav(),
            () => new HostedTranscriptionClient("https://gateway.example.test", () => null));
        Assert.Equal(TranscriptionProvider.cloud, provider.Provider);
        var ex = await Assert.ThrowsAsync<HostedApiException>(() => provider.TranscribeAsync("/tmp/a.mp4"));
        Assert.True(
            string.Equals(ex.Code, "transcription_not_ready", StringComparison.Ordinal)
            || string.Equals(ex.Code, "authentication_required", StringComparison.Ordinal));
    }

    private sealed class StubWav : IPcmWavPreparer
    {
        public Task<PreparedPcmWav> PrepareAsync(string mediaPath, CancellationToken cancellationToken = default)
            => Task.FromResult(Pcm16Wav.FromMonoFloats([0.1f, -0.1f], 16_000));
    }

    [Fact]
    public void TranscriptionProvider_Serializes_As_Lowercase_RawValue()
    {
        Assert.Equal("\"cloud\"", JsonSerializer.Serialize(TranscriptionProvider.cloud, JsonOptions.ForProject));
        Assert.Equal("\"tight\"", JsonSerializer.Serialize(CutAggressiveness.tight, JsonOptions.ForProject));
    }

    // ---- WordCutPlanner.CutRanges (Mac WordCutPlanner.swift semantics) ----

    [Fact]
    public void KeepGapFrames_Rounds_Midpoint_Away_From_Zero()
    {
        // balanced = 150 ms -> 4.5 frames at 30 fps -> 5 (Swift .rounded()).
        Assert.Equal(5, WordCutPlanner.KeepGapFrames(CutAggressiveness.balanced, 30));
        Assert.Equal(2, WordCutPlanner.KeepGapFrames(CutAggressiveness.tight, 30));
        Assert.Equal(10, WordCutPlanner.KeepGapFrames(CutAggressiveness.loose, 30));
    }

    [Fact]
    public void CutRanges_Middle_Word_Keeps_Half_Gap_Each_Side()
    {
        List<WordCutPlanner.Word> words =
        [
            new(0, 15, false),
            new(30, 45, true),
            new(60, 75, false),
        ];
        var ranges = WordCutPlanner.CutRanges(words, 0, 300, keepGapFrames: 4);
        Assert.Equal([new FrameRange(17, 58)], ranges);
    }

    [Fact]
    public void CutRanges_First_Word_Extends_To_ClipStart()
    {
        List<WordCutPlanner.Word> words =
        [
            new(0, 15, true),
            new(30, 45, false),
        ];
        var ranges = WordCutPlanner.CutRanges(words, 0, 300, keepGapFrames: 4);
        Assert.Equal([new FrameRange(0, 28)], ranges);
    }

    [Fact]
    public void CutRanges_Last_Word_Extends_To_ClipEnd()
    {
        List<WordCutPlanner.Word> words =
        [
            new(0, 15, false),
            new(280, 295, true),
        ];
        var ranges = WordCutPlanner.CutRanges(words, 0, 300, keepGapFrames: 4);
        // Cut runs from previous word's end (+half gap) through the tail, keeping half gap at end.
        Assert.Equal([new FrameRange(17, 298)], ranges);
    }

    [Fact]
    public void CutRanges_Adjacent_Selected_Words_Merge_Into_One_Cut()
    {
        List<WordCutPlanner.Word> words =
        [
            new(0, 10, false),
            new(20, 30, true),
            new(50, 60, true), // 20-frame gap to previous, still one run -> gap removed too
            new(80, 90, false),
        ];
        var ranges = WordCutPlanner.CutRanges(words, 0, 300, keepGapFrames: 4);
        Assert.Equal([new FrameRange(12, 78)], ranges);
    }

    [Fact]
    public void CutRanges_Ignores_ZeroLength_Words_And_Empty_Input()
    {
        Assert.Empty(WordCutPlanner.CutRanges([], 0, 100, 4));
        Assert.Empty(WordCutPlanner.CutRanges([new WordCutPlanner.Word(10, 10, true)], 0, 100, 4));
        // Degenerate clip span.
        Assert.Empty(WordCutPlanner.CutRanges([new WordCutPlanner.Word(10, 20, true)], 50, 50, 4));
    }

    // ---- Source seconds -> timeline frames mapping ----

    [Fact]
    public void MapToTimeline_Applies_Trim_And_Placement()
    {
        var clip = new Clip
        {
            Id = "C",
            MediaRef = "M",
            StartFrame = 90,
            DurationFrames = 150,
            TrimStartFrame = 60, // visible source frames [60, 210) = seconds [2.0, 7.0) at 30fps
        };
        List<TranscriptionWord> words =
        [
            new() { Text = "a", Start = 3.0, End = 3.4 },
            new() { Text = "early", Start = 0.5, End = 1.0 }, // before visible span -> dropped
            new() { Text = "late", Start = 8.0, End = 8.5 },  // after visible span -> dropped
            new() { Text = "untimed" },                       // no timestamps -> dropped
        ];

        var mapped = WordCutPlanner.MapToTimeline(clip, 30, words);
        var word = Assert.Single(mapped);
        Assert.Equal("a", word.Text);
        Assert.Equal(120, word.StartFrame); // 90 + (3.0*30 - 60)
        Assert.Equal(132, word.EndFrame);   // 90 + (3.4*30 - 60)
        Assert.Equal(3.0, word.SourceStart);
        Assert.Equal(3.4, word.SourceEnd);
    }

    [Fact]
    public void MapToTimeline_Applies_Speed()
    {
        var clip = new Clip
        {
            Id = "C",
            MediaRef = "M",
            StartFrame = 0,
            DurationFrames = 100,
            Speed = 2.0, // visible source frames [0, 200)
        };
        List<TranscriptionWord> words =
        [
            new() { Text = "w", Start = 2.0, End = 3.0 },   // source frames 60-90 -> timeline 30-45
            new() { Text = "out", Start = 7.0, End = 7.5 }, // source mid 217.5 outside [0,200)
        ];

        var mapped = WordCutPlanner.MapToTimeline(clip, 30, words);
        var word = Assert.Single(mapped);
        Assert.Equal(30, word.StartFrame);
        Assert.Equal(45, word.EndFrame);
    }

    // ---- PlanRemoval ----

    [Fact]
    public void PlanRemoval_Computes_Ranges_And_Interior_Splits()
    {
        var clip = new Clip
        {
            Id = "C",
            MediaRef = "M",
            StartFrame = 90,
            DurationFrames = 150,
            TrimStartFrame = 60,
        };
        List<TranscriptionWord> words =
        [
            new() { Text = "a", Start = 2.5, End = 2.9 },  // timeline 105-117
            new() { Text = "um", Start = 3.0, End = 3.4 }, // timeline 120-132
            new() { Text = "b", Start = 4.0, End = 4.5 },  // timeline 150-165
        ];

        var plan = WordCutPlanner.PlanRemoval(clip, 2, 30, words, [(2.95, 3.45)]);
        Assert.Equal(2, plan.TrackIndex);
        Assert.Equal([new FrameRange(119, 148)], plan.Ranges);
        Assert.Equal([119, 148], plan.SplitFrames);
        Assert.Equal(29, plan.RemovedFrames);
        Assert.False(plan.IsEmpty);
    }

    [Fact]
    public void PlanRemoval_Range_Touching_Clip_Edges_Needs_No_Split_There()
    {
        var clip = new Clip { Id = "C", MediaRef = "M", StartFrame = 0, DurationFrames = 300 };
        List<TranscriptionWord> words =
        [
            new() { Text = "first", Start = 0.0, End = 0.5 }, // timeline 0-15
            new() { Text = "next", Start = 1.0, End = 1.5 },  // timeline 30-45
        ];

        var plan = WordCutPlanner.PlanRemoval(clip, 0, 30, words, [(0.0, 0.5)]);
        Assert.Equal([new FrameRange(0, 28)], plan.Ranges);
        Assert.Equal([28], plan.SplitFrames); // start touches clip start, only end needs a split
    }

    [Fact]
    public void PlanRemoval_With_No_Selection_Is_Empty()
    {
        var clip = new Clip { Id = "C", MediaRef = "M", StartFrame = 0, DurationFrames = 300 };
        List<TranscriptionWord> words = [new() { Text = "a", Start = 1.0, End = 1.5 }];
        var plan = WordCutPlanner.PlanRemoval(clip, 0, 30, words, [(5.0, 6.0)]);
        Assert.True(plan.IsEmpty);
        Assert.Equal(0, plan.RemovedFrames);
    }

    // ---- Plan application against an in-memory session ----

    [Fact]
    public void ApplyTo_Splits_Ripple_Deletes_And_Shifts_Downstream()
    {
        var session = ProjectSession.CreateNew();
        var clip = new Clip { Id = "C1", MediaRef = "M1", StartFrame = 0, DurationFrames = 300 };
        Assert.True(EditCommands.AddClip(session, 0, clip).Ok);
        Assert.True(EditCommands.AddClip(session, 0,
            new Clip { Id = "C2", MediaRef = "M2", StartFrame = 300, DurationFrames = 100 }).Ok);

        List<TranscriptionWord> words =
        [
            new() { Text = "hello", Start = 0.0, End = 0.5 }, // timeline 0-15
            new() { Text = "um", Start = 1.0, End = 1.5 },    // timeline 30-45
            new() { Text = "world", Start = 2.0, End = 2.5 }, // timeline 60-75
        ];

        var plan = WordCutPlanner.PlanRemoval(clip, 0, 30, words, [(1.0, 1.5)]);
        Assert.Equal([new FrameRange(17, 58)], plan.Ranges);

        var result = plan.ApplyTo(session);
        Assert.True(result.Ok, result.Error);

        var clips = session.ActiveTimeline.Tracks[0].Clips.OrderBy(c => c.StartFrame).ToList();
        Assert.Equal(3, clips.Count);

        // Left piece keeps head content untouched.
        Assert.Equal(0, clips[0].StartFrame);
        Assert.Equal(17, clips[0].DurationFrames);
        Assert.Equal(0, clips[0].TrimStartFrame);

        // Right piece starts where the cut began and skips the removed source frames.
        Assert.Equal(17, clips[1].StartFrame);
        Assert.Equal(242, clips[1].DurationFrames);
        Assert.Equal(58, clips[1].TrimStartFrame);

        // Downstream clip ripples left by the removed length (41 frames).
        Assert.Equal("C2", clips[2].Id);
        Assert.Equal(259, clips[2].StartFrame);
        Assert.Equal(100, clips[2].DurationFrames);
    }

    [Fact]
    public void ApplyTo_First_Word_Cut_Trims_Clip_Head()
    {
        var session = ProjectSession.CreateNew();
        var clip = new Clip { Id = "C1", MediaRef = "M1", StartFrame = 0, DurationFrames = 300 };
        Assert.True(EditCommands.AddClip(session, 0, clip).Ok);

        List<TranscriptionWord> words =
        [
            new() { Text = "first", Start = 0.0, End = 0.5 },
            new() { Text = "next", Start = 1.0, End = 1.5 },
        ];

        var plan = WordCutPlanner.PlanRemoval(clip, 0, 30, words, [(0.0, 0.5)]);
        var result = plan.ApplyTo(session);
        Assert.True(result.Ok, result.Error);

        var only = Assert.Single(session.ActiveTimeline.Tracks[0].Clips);
        Assert.Equal(0, only.StartFrame);
        Assert.Equal(272, only.DurationFrames);
        Assert.Equal(28, only.TrimStartFrame);
    }

    [Fact]
    public void ApplyTo_Empty_Plan_Is_NoOp_Success()
    {
        var session = ProjectSession.CreateNew();
        var plan = new WordCutPlan(0, [], []);
        Assert.True(plan.ApplyTo(session).Ok);
        Assert.False(session.CanUndo());
    }
}
