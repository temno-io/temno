using Temno.Core.Editing;
using Temno.Core.Models;
using Temno.Core.Session;
using Xunit;

namespace Temno.Core.Tests;

public sealed class EffectStackCommandsTests
{
    [Fact]
    public void AddEffect_AcceptsEveryCatalogEffectAndUsesDescriptorDefaults()
    {
        var session = CreateSessionWithVideoClip();

        foreach (var type in EffectRegistry.CanonicalOrder)
        {
            var result = EffectStackCommands.AddEffect(session, "clip1", type);
            Assert.True(result.Ok, result.Error);
        }

        var effects = GetClip(session).Effects;
        Assert.NotNull(effects);
        Assert.Equal(EffectRegistry.CanonicalOrder, effects!.Select(effect => effect.Type));
        Assert.All(effects, effect => Assert.True(effect.Enabled));
        Assert.All(effects, effect => Assert.False(string.IsNullOrWhiteSpace(effect.Id)));

        foreach (var effect in effects)
        {
            var descriptor = Assert.IsType<EffectDescriptor>(EffectRegistry.GetDescriptor(effect.Type));
            foreach (var spec in descriptor.Params)
            {
                Assert.True(effect.Params.TryGetValue(spec.Key, out var value));
                Assert.Equal(spec.DefaultValue, value!.Value);
            }
        }
    }

    [Fact]
    public void StackOperations_TargetEffectIds_ClampValues_AndPreserveUnknownEffects()
    {
        var session = CreateSessionWithVideoClip();
        var clip = GetClip(session);
        var unknown = new Effect
        {
            Id = "mac-unknown-effect",
            Type = "vendor.filmLook",
            Enabled = true,
            Params = new Dictionary<string, EffectParam>
            {
                ["opaque"] = new EffectParam { Value = 0.25, String = "retain-me" },
            },
        };
        clip.Effects = [unknown];

        Assert.True(EffectStackCommands.AddEffect(session, clip.Id, "blur.gaussian").Ok);
        Assert.True(EffectStackCommands.AddEffect(session, clip.Id, "blur.gaussian").Ok);
        var gaussianEffects = clip.Effects!.Where(effect => effect.Type == "blur.gaussian").ToList();
        Assert.Equal(2, gaussianEffects.Count);

        var firstGaussianId = gaussianEffects[0].Id;
        var secondGaussianId = gaussianEffects[1].Id;
        Assert.True(EffectStackCommands.SetEffectNumericParam(session, clip.Id, secondGaussianId, "radius", 123.4567).Ok);
        Assert.Equal(8.0, clip.Effects.Single(effect => effect.Id == firstGaussianId).Params["radius"].Value);
        Assert.Equal(100.0, clip.Effects.Single(effect => effect.Id == secondGaussianId).Params["radius"].Value);

        Assert.True(EffectStackCommands.SetEffectEnabled(session, clip.Id, unknown.Id, false).Ok);
        Assert.True(EffectStackCommands.MoveEffect(session, clip.Id, unknown.Id, 2).Ok);
        Assert.Equal(unknown.Id, clip.Effects[2].Id);
        Assert.False(clip.Effects[2].Enabled);
        Assert.Equal("retain-me", clip.Effects[2].Params["opaque"].String);
        Assert.Equal(0.25, clip.Effects[2].Params["opaque"].Value);

        var unsupportedEdit = EffectStackCommands.SetEffectNumericParam(session, clip.Id, unknown.Id, "opaque", 1);
        Assert.False(unsupportedEdit.Ok);
        Assert.Equal("retain-me", GetClip(session).Effects![2].Params["opaque"].String);

        Assert.True(EffectStackCommands.RemoveEffect(session, clip.Id, firstGaussianId).Ok);
        var remainingGaussian = Assert.Single(GetClip(session).Effects!, effect => effect.Type == "blur.gaussian");
        Assert.Equal(secondGaussianId, remainingGaussian.Id);

        var reloaded = ProjectFile.Decode(session.ProjectFile.Encode());
        var reloadedUnknown = reloaded.Timelines.Single().Tracks[0].Clips.Single().Effects!
            .Single(effect => effect.Id == unknown.Id);
        Assert.Equal("vendor.filmLook", reloadedUnknown.Type);
        Assert.False(reloadedUnknown.Enabled);
        Assert.Equal("retain-me", reloadedUnknown.Params["opaque"].String);
        Assert.Equal(0.25, reloadedUnknown.Params["opaque"].Value);

        session.Undo();
        Assert.Equal(2, GetClip(session).Effects!.Count(effect => effect.Type == "blur.gaussian"));
        session.Undo();
        Assert.Equal(0, GetClip(session).Effects!.FindIndex(effect => effect.Id == unknown.Id));
        session.Undo();
        Assert.True(GetClip(session).Effects!.Single(effect => effect.Id == unknown.Id).Enabled);
        session.Redo();
        Assert.False(GetClip(session).Effects!.Single(effect => effect.Id == unknown.Id).Enabled);
    }

    [Fact]
    public void SetEffectNumericParams_ClampsEachValueAndKeepsAdditionalData()
    {
        var session = CreateSessionWithVideoClip();
        var clip = GetClip(session);
        Assert.True(EffectStackCommands.AddEffect(session, clip.Id, "color.temperature").Ok);
        var effect = clip.Effects!.Single();
        effect.Params["temperature"].String = "Mac metadata";
        effect.Params["resourcePath"] = new EffectParam { String = "media/look.cube" };

        var result = EffectStackCommands.SetEffectNumericParams(
            session,
            clip.Id,
            effect.Id,
            new Dictionary<string, double>
            {
                ["temperature"] = 99999,
                ["tint"] = -999,
            });

        Assert.True(result.Ok, result.Error);
        Assert.Equal(11000.0, effect.Params["temperature"].Value);
        Assert.Equal("Mac metadata", effect.Params["temperature"].String);
        Assert.Equal(-100.0, effect.Params["tint"].Value);
        Assert.Equal("media/look.cube", effect.Params["resourcePath"].String);

        var invalid = EffectStackCommands.SetEffectNumericParam(session, clip.Id, effect.Id, "notAParameter", 1);
        Assert.False(invalid.Ok);
        Assert.Equal(11000.0, effect.Params["temperature"].Value);
    }

    [Fact]
    public void SetEffectNumericParamsInPlace_ClampsWithoutRecordingUndo()
    {
        var session = CreateSessionWithVideoClip();
        Assert.True(EffectStackCommands.AddEffect(session, "clip1", "color.exposure").Ok);
        var effect = GetClip(session).Effects!.Single();

        var result = EffectStackCommands.SetEffectNumericParamsInPlace(
            session,
            "clip1",
            effect.Id,
            new Dictionary<string, double> { ["ev"] = 99 });
        Assert.True(result.Ok, result.Error);
        Assert.Equal(3.0, effect.Params["ev"].Value);

        session.Undo();
        Assert.True(GetClip(session).Effects is not { Count: > 0 });
    }

    [Fact]
    public void ApplyNumericEffectInPlace_SkipsIdentityAndRemovesExisting()
    {
        var session = CreateSessionWithVideoClip();
        Assert.True(session.CanUndo());

        var skip = EffectStackCommands.ApplyNumericEffectInPlace(
            session, "clip1", "color.exposure", new Dictionary<string, double> { ["ev"] = 0 }, out var changed);
        Assert.True(skip.Ok, skip.Error);
        Assert.False(changed);
        Assert.Null(GetClip(session).Effects);

        var add = EffectStackCommands.ApplyNumericEffectInPlace(
            session, "clip1", "color.exposure", new Dictionary<string, double> { ["ev"] = 0.5 }, out changed);
        Assert.True(add.Ok, add.Error);
        Assert.True(changed);
        Assert.Equal(0.5, GetClip(session).Effects!.Single(e => e.Type == "color.exposure").Params["ev"].Value);

        var same = EffectStackCommands.ApplyNumericEffectInPlace(
            session, "clip1", "color.exposure", new Dictionary<string, double> { ["ev"] = 0.5 }, out changed);
        Assert.True(same.Ok, same.Error);
        Assert.False(changed);

        session.Undo();
        Assert.Empty(session.ActiveTimeline.Tracks[0].Clips);

        session.Redo();
        Assert.Equal(0.5, GetClip(session).Effects!.Single(e => e.Type == "color.exposure").Params["ev"].Value);

        var remove = EffectStackCommands.ApplyNumericEffectInPlace(
            session, "clip1", "color.exposure", new Dictionary<string, double> { ["ev"] = 0 }, out changed);
        Assert.True(remove.Ok, remove.Error);
        Assert.True(changed);
        Assert.Null(GetClip(session).Effects);
    }

    [Fact]
    public void EffectStackCommands_RejectNonVisualClipAndInvalidMove()
    {
        var session = ProjectSession.CreateNew();
        var audio = new Clip
        {
            Id = "audio1",
            MediaRef = "A1",
            MediaType = ClipType.audio,
            DurationFrames = 30,
        };
        Assert.True(EditCommands.AddClip(session, 2, audio).Ok);

        var audioResult = EffectStackCommands.AddEffect(session, audio.Id, "color.exposure");
        Assert.False(audioResult.Ok);
        Assert.Null(audio.Effects);

        var clip = CreateSessionWithVideoClip();
        Assert.True(EffectStackCommands.AddEffect(clip, "clip1", "color.exposure").Ok);
        var effectId = GetClip(clip).Effects!.Single().Id;
        var move = EffectStackCommands.MoveEffect(clip, "clip1", effectId, 4);
        Assert.False(move.Ok);
        Assert.Single(GetClip(clip).Effects!);
    }

    private static ProjectSession CreateSessionWithVideoClip()
    {
        var session = ProjectSession.CreateNew();
        var clip = new Clip
        {
            Id = "clip1",
            MediaRef = "M1",
            MediaType = ClipType.video,
            DurationFrames = 120,
        };
        Assert.True(EditCommands.AddClip(session, 0, clip).Ok);
        return session;
    }

    private static Clip GetClip(ProjectSession session)
        => session.ActiveTimeline.Tracks[0].Clips.Single(clip => clip.Id == "clip1");
}
