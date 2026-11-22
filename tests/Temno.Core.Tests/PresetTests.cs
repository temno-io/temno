using System;
using System.IO;
using System.Linq;
using Temno.Core.IO;
using Temno.Core.Models;
using Xunit;

namespace Temno.Core.Tests;

public class PresetTests
{
    [Fact]
    public void PresetStore_DefaultPathResolution_ShouldResolveToUserProfile()
    {
        var store = new PresetStore();
        var expectedBase = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".temno", "presets");
        Assert.Equal(expectedBase, store.PresetDirectory);
    }

    [Fact]
    public void PresetStore_RoundTrip_ShouldLoadAndSaveCorrectly()
    {
        // 1. Create a isolated temp directory for test run
        var tempBaseDir = Path.Combine(Path.GetTempPath(), $"temno-test-{Guid.NewGuid():N}");
        
        try
        {
            var store1 = new PresetStore(tempBaseDir);

            // Verify clean state
            Assert.Empty(store1.TextStyles);
            Assert.Empty(store1.ExportPresets);

            // 2. Perform CRUD on Text Styles
            var textStyle = new TextStyle
            {
                FontName = "Outfit-Bold",
                FontSize = 100,
                IsBold = true,
                Alignment = TextAlignment.Left,
                Color = new TextStyle.RGBA { R = 0.5, G = 0.8, B = 1.0, A = 1.0 }
            };

            var textPreset = store1.SaveTextStyle("My Title Look", textStyle);
            Assert.NotNull(textPreset);
            Assert.Equal("My Title Look", textPreset.Name);
            Assert.Equal("Outfit-Bold", textPreset.Style.FontName);
            Assert.Equal(100, textPreset.Style.FontSize);

            // 3. Perform CRUD on Export Presets
            var exportPreset = store1.SaveExportPreset(
                name: "Web High Quality",
                codec: VideoCodec.H265,
                resolution: ExportResolution.R1080p,
                bitrateMbps: 15.0,
                maxRenderQuality: true,
                maxBitDepth: true
            );
            Assert.NotNull(exportPreset);
            Assert.Equal("Web High Quality", exportPreset.Name);
            Assert.Equal(VideoCodec.H265, exportPreset.Codec);
            Assert.Equal(ExportResolution.R1080p, exportPreset.Resolution);
            Assert.True(exportPreset.MaxRenderQuality);
            Assert.True(exportPreset.MaxBitDepth);

            // 4. Reload from disk using a fresh PresetStore instance
            var store2 = new PresetStore(tempBaseDir);
            Assert.Single(store2.TextStyles);
            Assert.Single(store2.ExportPresets);

            var loadedText = store2.GetTextStyle("My Title Look");
            Assert.NotNull(loadedText);
            Assert.Equal("Outfit-Bold", loadedText.Style.FontName);
            Assert.Equal(100, loadedText.Style.FontSize);
            Assert.Equal(TextAlignment.Left, loadedText.Style.Alignment);
            Assert.Equal(0.5, loadedText.Style.Color.R);

            var loadedExport = store2.GetExportPreset("Web High Quality");
            Assert.NotNull(loadedExport);
            Assert.Equal(VideoCodec.H265, loadedExport.Codec);
            Assert.Equal(ExportResolution.R1080p, loadedExport.Resolution);
            Assert.Equal(15.0, loadedExport.BitrateMbps);
            Assert.True(loadedExport.MaxRenderQuality);
            Assert.True(loadedExport.MaxBitDepth);

            // 5. Delete presets
            var removedText = store2.RemoveTextStyle("My Title Look");
            Assert.True(removedText);
            var removedExport = store2.RemoveExportPreset(loadedExport.Id);
            Assert.True(removedExport);

            // Reload again to check removal persisted
            var store3 = new PresetStore(tempBaseDir);
            Assert.Empty(store3.TextStyles);
            Assert.Empty(store3.ExportPresets);
        }
        finally
        {
            if (Directory.Exists(tempBaseDir))
            {
                try
                {
                    Directory.Delete(tempBaseDir, true);
                }
                catch
                {
                    // ignore cleanup failure
                }
            }
        }
    }

    [Fact]
    public void PresetStore_CorruptFile_ReportsInvalidData()
    {
        var tempBaseDir = Path.Combine(Path.GetTempPath(), $"temno-test-{Guid.NewGuid():N}");
        var presetDirectory = Path.Combine(tempBaseDir, "presets");
        Directory.CreateDirectory(presetDirectory);
        File.WriteAllText(Path.Combine(presetDirectory, "text-styles.json"), "{not-json");
        try
        {
            Assert.Throws<InvalidDataException>(() => new PresetStore(tempBaseDir));
        }
        finally
        {
            Directory.Delete(tempBaseDir, recursive: true);
        }
    }

    [Fact]
    public void PresetStore_FailedSave_DoesNotMutateInMemoryCollection()
    {
        var tempBaseDir = Path.Combine(Path.GetTempPath(), $"temno-test-{Guid.NewGuid():N}");
        try
        {
            var store = new PresetStore(tempBaseDir);
            var original = store.SaveTextStyle("Title", new TextStyle { FontName = "Original" });
            var path = Path.Combine(store.PresetDirectory, "text-styles.json");
            File.Delete(path);
            Directory.CreateDirectory(path);

            var error = Record.Exception(() =>
                store.SaveTextStyle("Title", new TextStyle { FontName = "Replacement" }));
            Assert.True(error is IOException or UnauthorizedAccessException);

            var current = Assert.Single(store.TextStyles);
            Assert.Same(original, current);
            Assert.Equal("Original", current.Style.FontName);
        }
        finally
        {
            Directory.Delete(tempBaseDir, recursive: true);
        }
    }

    [Fact]
    public void PresetStore_FailedReload_DoesNotPartiallyReplaceCollections()
    {
        var tempBaseDir = Path.Combine(Path.GetTempPath(), $"temno-test-{Guid.NewGuid():N}");
        try
        {
            var store = new PresetStore(tempBaseDir);
            var text = store.SaveTextStyle("Title", new TextStyle { FontName = "Original" });
            var export = store.SaveExportPreset("Web", VideoCodec.H264, ExportResolution.R1080p, 8, false);
            File.WriteAllText(Path.Combine(store.PresetDirectory, "text-styles.json"), "[]");
            File.WriteAllText(Path.Combine(store.PresetDirectory, "export-presets.json"), "{not-json");

            Assert.Throws<InvalidDataException>(() => store.LoadAll());

            Assert.Same(text, Assert.Single(store.TextStyles));
            Assert.Same(export, Assert.Single(store.ExportPresets));
        }
        finally
        {
            Directory.Delete(tempBaseDir, recursive: true);
        }
    }
}
