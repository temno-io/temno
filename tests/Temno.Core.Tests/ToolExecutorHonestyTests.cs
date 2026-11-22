using System.Text.Json.Nodes;
using Temno.Core.Account;
using Temno.Core.IO;
using Temno.Core.Models;
using Temno.Core.Session;
using Temno.Core.Transcription;
using Temno.Generation;
using Temno.Mcp;
using Xunit;

namespace Temno.Core.Tests;

[Collection("EnvRegistry")]
public class ToolExecutorHonestyTests : IDisposable
{
    private readonly string _prefsFile;
    private readonly string? _prevFake;
    private readonly string? _prevKey;
    private readonly string? _prevUrl;

    public ToolExecutorHonestyTests()
    {
        _prefsFile = Path.Combine(Path.GetTempPath(), "temno-honesty-prefs-" + Guid.NewGuid().ToString("N") + ".json");
        UserPreferences.SetPathForTests(_prefsFile);
        // WP-193: clear both spellings; EnvCompat reads TEMNO_* first, TEMNO_* as fallback.
        _prevFake = Environment.GetEnvironmentVariable("TEMNO_USE_FAKE_GENERATION");
        _prevKey = Environment.GetEnvironmentVariable("TEMNO_API_KEY");
        _prevUrl = Environment.GetEnvironmentVariable("TEMNO_API_BASE_URL");
        Environment.SetEnvironmentVariable("TEMNO_USE_FAKE_GENERATION", null);
        Environment.SetEnvironmentVariable("TEMNO_API_KEY", null);
        Environment.SetEnvironmentVariable("TEMNO_API_BASE_URL", null);
        Environment.SetEnvironmentVariable("TEMNO_USE_FAKE_GENERATION", null);
        Environment.SetEnvironmentVariable("TEMNO_API_KEY", null);
        Environment.SetEnvironmentVariable("TEMNO_API_BASE_URL", null);
        Environment.SetEnvironmentVariable("TEMNO_GATEWAY_URL", null);
        HostedRuntime.Session = null;
        ToolExecutor.GenerationServiceOverride = null;
        ToolExecutor.ResetGenerationService();
    }

    public void Dispose()
    {
        ToolExecutor.TranscriptionProviderOverride = null;
        ToolExecutor.GenerationServiceOverride = null;
        ToolExecutor.ResetGenerationService();
        ProjectHub.ActiveSession = null;
        TranscriptDiskCache.SetRootForTests(null);
        Environment.SetEnvironmentVariable("TEMNO_USE_FAKE_GENERATION", _prevFake);
        Environment.SetEnvironmentVariable("TEMNO_API_KEY", _prevKey);
        Environment.SetEnvironmentVariable("TEMNO_API_BASE_URL", _prevUrl);
        Environment.SetEnvironmentVariable("TEMNO_GATEWAY_URL", null);
        HostedRuntime.Session = null;
        UserPreferences.SetPathForTests(null);
        try { File.Delete(_prefsFile); } catch { /* ignore */ }
    }

    [Fact]
    public void ImplementedTools_ContainsRedo()
    {
        Assert.Contains("redo", ToolExecutor.ImplementedTools);
        Assert.Contains("undo", ToolExecutor.ImplementedTools);
    }

    [Fact]
    public void Redo_RestoresUndoneEdit()
    {
        var session = ProjectSession.CreateNew();
        ProjectHub.ActiveSession = session;
        Assert.True(EditCommands.AddClip(session, 0, new Clip
        {
            Id = "C1",
            MediaRef = "m",
            StartFrame = 0,
            DurationFrames = 10,
            MediaType = ClipType.video,
        }).Ok);

        var undo = ToolExecutor.Execute("undo", []);
        Assert.False(undo.IsError);
        Assert.Empty(session.ActiveTimeline.Tracks[0].Clips);

        var redo = ToolExecutor.Execute("redo", []);
        Assert.False(redo.IsError, redo.Content[0].Text);
        Assert.Single(session.ActiveTimeline.Tracks[0].Clips);
        Assert.Equal("C1", session.ActiveTimeline.Tracks[0].Clips[0].Id);
    }

    [Fact]
    public void GenerateVideo_WhenUnconfigured_DoesNotCreatePlaceholder()
    {
        var prevFake = Environment.GetEnvironmentVariable("TEMNO_USE_FAKE_GENERATION");
        var prevKey = Environment.GetEnvironmentVariable("TEMNO_API_KEY");
        var prevUrl = Environment.GetEnvironmentVariable("TEMNO_API_BASE_URL");
        try
        {
            Environment.SetEnvironmentVariable("TEMNO_USE_FAKE_GENERATION", null);
            Environment.SetEnvironmentVariable("TEMNO_API_KEY", null);
            Environment.SetEnvironmentVariable("TEMNO_API_BASE_URL", null);
            ToolExecutor.GenerationServiceOverride = null;
            ToolExecutor.ResetGenerationService();

            var session = ProjectSession.CreateNew();
            ProjectHub.ActiveSession = session;
            var before = session.Manifest.Entries.Count;

            var result = ToolExecutor.Execute("generate_video", new JsonObject { ["prompt"] = "a cat" });
            Assert.True(result.IsError);
            Assert.Contains("TEMNO_API_KEY", result.Content[0].Text);
            Assert.Equal(before, session.Manifest.Entries.Count);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEMNO_USE_FAKE_GENERATION", prevFake);
            Environment.SetEnvironmentVariable("TEMNO_API_KEY", prevKey);
            Environment.SetEnvironmentVariable("TEMNO_API_BASE_URL", prevUrl);
            ToolExecutor.ResetGenerationService();
        }
    }

    [Fact]
    public void PrefsFileApiKey_SelectsHttpGenerationClient()
    {
        UserPreferences.Save(new UserPreferences
        {
            Generation = { ApiKey = "prefs-only-key", BaseUrl = "https://api.example.test" },
        });
        ToolExecutor.ResetGenerationService();

        var service = ToolExecutor.TryResolveGenerationService();
        Assert.NotNull(service);
        Assert.IsType<HttpGenerationClient>(service.Client);
        Assert.True(ToolExecutor.IsGenerationConfigured());
        Assert.False(ToolExecutor.IsFakeGenerationActive());
    }

    [Fact]
    public void HttpGenerationClient_WithoutEndpoint_Throws()
    {
        // No default host: with no argument and no TEMNO_API_BASE_URL there is nowhere to send the key.
        Assert.Throws<ArgumentException>(() => new HttpGenerationClient(null, "key-without-endpoint"));
        Assert.Throws<ArgumentException>(() => new HttpGenerationClient("   ", "key-without-endpoint"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void PrefsKeyWithoutEndpoint_IsRefusedWithEndpointMessage(string baseUrl)
    {
        UserPreferences.Save(new UserPreferences
        {
            Generation = { ApiKey = "prefs-only-key", BaseUrl = baseUrl },
        });
        ToolExecutor.ResetGenerationService();

        Assert.Null(ToolExecutor.TryResolveGenerationService());
        Assert.False(ToolExecutor.IsGenerationConfigured());
        Assert.True(ToolExecutor.IsGenerationEndpointMissing());
        Assert.Equal(ToolExecutor.GenerationEndpointMissingMessage, ToolExecutor.GenerationUnavailableMessage());

        var session = ProjectSession.CreateNew();
        ProjectHub.ActiveSession = session;
        var before = session.Manifest.Entries.Count;
        var result = ToolExecutor.Execute("generate_video", new JsonObject { ["prompt"] = "a cat" });
        Assert.True(result.IsError);
        Assert.Contains(ToolExecutor.GenerationEndpointMissingMessage, result.Content[0].Text);
        Assert.Equal(before, session.Manifest.Entries.Count);

        var models = JsonNode.Parse(ToolExecutor.Execute("list_models", new JsonObject()).Content[0].Text!)!;
        Assert.False(models["configured"]!.GetValue<bool>());
        Assert.Equal(ToolExecutor.GenerationEndpointMissingMessage, models["note"]!.GetValue<string>());
    }

    [Fact]
    public void EnvKeyWithoutEndpoint_IsRefused()
    {
        Environment.SetEnvironmentVariable("TEMNO_API_KEY", "env-only-key");
        ToolExecutor.ResetGenerationService();

        Assert.Null(ToolExecutor.TryResolveGenerationService());
        Assert.True(ToolExecutor.IsGenerationEndpointMissing());
    }

    [Fact]
    public void KeyWithoutEndpoint_DoesNotFallBackToHostedGeneration()
    {
        UserPreferences.Save(new UserPreferences
        {
            Generation = { ApiKey = "prefs-only-key" },
        });
        Environment.SetEnvironmentVariable("TEMNO_GATEWAY_URL", "https://gateway.example.test");
        HostedRuntime.Session = new TestAccountSession();
        ToolExecutor.ResetGenerationService();

        Assert.Null(ToolExecutor.TryResolveGenerationService());
        Assert.False(ToolExecutor.IsHostedImagesConfigured());
        Assert.False(ToolExecutor.IsHostedMediaConfigured());
        Assert.Null(ToolExecutor.TryGetImageClient());
        Assert.Null(ToolExecutor.TryGetMediaClient());
    }

    [Fact]
    public void EndpointWithoutKey_SelectsHttpGenerationClient()
    {
        UserPreferences.Save(new UserPreferences
        {
            Generation = { BaseUrl = "https://api.example.test" },
        });
        ToolExecutor.ResetGenerationService();

        Assert.IsType<HttpGenerationClient>(ToolExecutor.TryResolveGenerationService()?.Client);
        Assert.False(ToolExecutor.IsGenerationEndpointMissing());
    }

    [Fact]
    public void FakeEnv_TakesPrecedenceOverPrefsKey()
    {
        UserPreferences.Save(new UserPreferences
        {
            Generation = { ApiKey = "prefs-only-key" },
        });
        Environment.SetEnvironmentVariable("TEMNO_USE_FAKE_GENERATION", "true");
        ToolExecutor.ResetGenerationService();

        var service = ToolExecutor.TryResolveGenerationService();
        Assert.NotNull(service);
        Assert.IsType<FakeGenerationClient>(service.Client);
        Assert.True(ToolExecutor.IsFakeGenerationActive());
        Assert.Equal(ToolExecutor.DemoGenerationStatus, ToolExecutor.CompletedGenerationStatus());
    }

    [Fact]
    public void SignedInGateway_SelectsHostedGenerationClient()
    {
        Environment.SetEnvironmentVariable("TEMNO_GATEWAY_URL", "https://gateway.example.test");
        HostedRuntime.Session = new TestAccountSession();
        ToolExecutor.ResetGenerationService();
        try
        {
            var service = ToolExecutor.TryResolveGenerationService();
            Assert.NotNull(service);
            Assert.IsType<HostedGenerationClient>(service.Client);
            Assert.True(ToolExecutor.IsGenerationConfigured());
            Assert.False(ToolExecutor.IsFakeGenerationActive());
        }
        finally
        {
            HostedRuntime.Session = null;
            Environment.SetEnvironmentVariable("TEMNO_GATEWAY_URL", null);
            ToolExecutor.ResetGenerationService();
        }
    }

    [Fact]
    public void ByokPrefs_StillBeatHostedGateway()
    {
        UserPreferences.Save(new UserPreferences
        {
            Generation = { ApiKey = "prefs-only-key", BaseUrl = "https://api.example.test" },
        });
        Environment.SetEnvironmentVariable("TEMNO_GATEWAY_URL", "https://gateway.example.test");
        HostedRuntime.Session = new TestAccountSession();
        ToolExecutor.ResetGenerationService();
        try
        {
            var service = ToolExecutor.TryResolveGenerationService();
            Assert.NotNull(service);
            Assert.IsType<HttpGenerationClient>(service.Client);
        }
        finally
        {
            HostedRuntime.Session = null;
            Environment.SetEnvironmentVariable("TEMNO_GATEWAY_URL", null);
            ToolExecutor.ResetGenerationService();
        }
    }

    [Fact]
    public void ApplyGenerationPlaceholderStatus_PersistsJobIdAndResultUrls()
    {
        var entry = new MediaManifestEntry
        {
            Id = "GEN1",
            GenerationInput = new GenerationInput { Prompt = "cat" },
        };
        ToolExecutor.ApplyGenerationPlaceholderStatus(
            entry,
            "downloading",
            "job-99",
            ["https://example.com/out.mp4"]);
        Assert.Equal("downloading", entry.GenerationStatus);
        Assert.Equal("job-99", entry.GenerationInput!.BackendJobId);
        Assert.Equal("https://example.com/out.mp4", Assert.Single(entry.GenerationInput.ResultUrls!));
    }

    [Fact]
    public void GenerateGoogle_WhenUnconfigured_DoesNotCreatePlaceholder()
    {
        var prevFake = Environment.GetEnvironmentVariable("TEMNO_USE_FAKE_GENERATION");
        var prevKey = Environment.GetEnvironmentVariable("TEMNO_API_KEY");
        var prevUrl = Environment.GetEnvironmentVariable("TEMNO_API_BASE_URL");
        try
        {
            Environment.SetEnvironmentVariable("TEMNO_USE_FAKE_GENERATION", null);
            Environment.SetEnvironmentVariable("TEMNO_API_KEY", null);
            Environment.SetEnvironmentVariable("TEMNO_API_BASE_URL", null);
            ToolExecutor.GenerationServiceOverride = null;
            ToolExecutor.ResetGenerationService();

            var session = ProjectSession.CreateNew();
            ProjectHub.ActiveSession = session;
            var before = session.Manifest.Entries.Count;

            var result = ToolExecutor.Execute("generate_google", new JsonObject
            {
                ["type"] = "image",
                ["prompt"] = "a cat",
            });
            Assert.True(result.IsError);
            Assert.Contains("TEMNO_API_KEY", result.Content[0].Text);
            Assert.Equal(before, session.Manifest.Entries.Count);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEMNO_USE_FAKE_GENERATION", prevFake);
            Environment.SetEnvironmentVariable("TEMNO_API_KEY", prevKey);
            Environment.SetEnvironmentVariable("TEMNO_API_BASE_URL", prevUrl);
            ToolExecutor.ResetGenerationService();
        }
    }

    [Fact]
    public void RemoveWords_LinkedAv_RipplesBothTracks()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "temno-wordcut-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        TranscriptDiskCache.SetRootForTests(Path.Combine(tempDir, "cache"));
        var mediaFile = Path.Combine(tempDir, "speech.wav");
        File.WriteAllText(mediaFile, "fake audio");

        try
        {
            var session = ProjectSession.CreateNew(fps: 30);
            session.Manifest.Entries.Add(new MediaManifestEntry
            {
                Id = "Vmedia",
                Name = "picture",
                Type = ClipType.video,
                HasAudio = false,
                Source = MediaSource.External(mediaFile),
                Duration = 10,
            });
            session.Manifest.Entries.Add(new MediaManifestEntry
            {
                Id = "Amedia",
                Name = "speech",
                Type = ClipType.audio,
                HasAudio = true,
                Source = MediaSource.External(mediaFile),
                Duration = 10,
            });

            Assert.True(EditCommands.AddClips(session,
            [
                (0, new Clip
                {
                    Id = "V",
                    MediaRef = "Vmedia",
                    MediaType = ClipType.video,
                    StartFrame = 0,
                    DurationFrames = 60,
                }),
                (2, new Clip
                {
                    Id = "A",
                    MediaRef = "Amedia",
                    MediaType = ClipType.audio,
                    StartFrame = 0,
                    DurationFrames = 60,
                }),
            ]).Ok);
            Assert.True(EditCommands.LinkClips(session, ["V", "A"]).Ok);

            ProjectHub.ActiveSession = session;
            ToolExecutor.TranscriptionProviderOverride = new FakeTranscriptionProvider(
                FakeTranscriptionProvider.MakeTranscript(["alpha", "bravo", "charlie"]));

            var result = ToolExecutor.Execute("remove_words", new JsonObject
            {
                ["matches"] = new JsonArray("bravo"),
            });
            Assert.False(result.IsError, result.Content[0].Text);

            var videoClips = session.ActiveTimeline.Tracks[0].Clips.OrderBy(c => c.StartFrame).ToList();
            var audioClips = session.ActiveTimeline.Tracks[2].Clips.OrderBy(c => c.StartFrame).ToList();
            Assert.Equal(videoClips.Count, audioClips.Count);
            Assert.Equal(
                videoClips.Select(c => (c.StartFrame, c.DurationFrames)),
                audioClips.Select(c => (c.StartFrame, c.DurationFrames)));
            Assert.True(videoClips.Sum(c => c.DurationFrames) < 60);
        }
        finally
        {
            ToolExecutor.TranscriptionProviderOverride = null;
            TranscriptDiskCache.SetRootForTests(null);
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void OpenProject_WhenDirtySessionOpen_DoesNotSwap()
    {
        var root = Path.Combine(Path.GetTempPath(), "temno-p0-open-" + Guid.NewGuid().ToString("N"));
        var registry = Path.Combine(root, "registry.json");
        var prevReg = Environment.GetEnvironmentVariable("TEMNO_REGISTRY_PATH");
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("TEMNO_REGISTRY_PATH", registry);
        try
        {
            var a = Path.Combine(root, "a.palmier");
            var b = Path.Combine(root, "b.palmier");
            Directory.CreateDirectory(a);
            Directory.CreateDirectory(b);
            ProjectSession.CreateNew().Save(a);
            ProjectSession.CreateNew().Save(b);

            var open = ProjectSession.Open(a);
            ProjectHub.ActiveSession = open;
            Assert.True(EditCommands.AddClip(open, 0, new Clip
            {
                Id = "keep",
                MediaRef = "m",
                StartFrame = 0,
                DurationFrames = 10,
            }).Ok);
            Assert.True(open.IsDirty);

            var result = ToolExecutor.Execute("open_project", new JsonObject { ["path"] = b });
            Assert.True(result.IsError);
            Assert.Contains("Close the current project", result.Content[0].Text);
            Assert.Same(open, ProjectHub.ActiveSession);
            Assert.True(ProjectHub.ActiveSession!.IsDirty);
            Assert.Contains(open.ActiveTimeline.Tracks[0].Clips, c => c.Id == "keep");
        }
        finally
        {
            ProjectHub.ActiveSession = null;
            Environment.SetEnvironmentVariable("TEMNO_REGISTRY_PATH", prevReg);
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void ActiveSessionChanged_FiresWhenSessionSwaps()
    {
        var fires = 0;
        EventHandler handler = (_, _) => fires++;
        ProjectHub.ActiveSessionChanged += handler;
        try
        {
            ProjectHub.ActiveSession = ProjectSession.CreateNew();
            Assert.Equal(1, fires);
            var first = ProjectHub.ActiveSession;
            ProjectHub.ActiveSession = ProjectSession.CreateNew();
            Assert.Equal(2, fires);
            Assert.NotSame(first, ProjectHub.ActiveSession);
        }
        finally
        {
            ProjectHub.ActiveSessionChanged -= handler;
            ProjectHub.ActiveSession = null;
        }
    }

    [Fact]
    public void AddCaptions_CensorProfanity_IsRejectedOnWindowsWhisper()
    {
        ProjectHub.ActiveSession = ProjectSession.CreateNew();
        ToolExecutor.TranscriptionProviderOverride = new FakeTranscriptionProvider();
        var result = ToolExecutor.Execute("add_captions", new JsonObject
        {
            ["censorProfanity"] = true,
        });
        Assert.True(result.IsError);
        Assert.Contains("Whisper", result.Content[0].Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("censorProfanity", result.Content[0].Text, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TestAccountSession : IAccountSessionSource
    {
        public bool IsSignedIn => true;
        public string? UserId => "user-1";
        public string? Email => "t@example.com";
        public string? AccessToken => "access-token";
        public CreditBalance? Balance => new(10, 0, 10);
        public string? BalanceError => null;
        public event EventHandler? Changed { add { } remove { } }
    }
}
