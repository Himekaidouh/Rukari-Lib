using AzureArchive.VideoTools.Core.Commands;

namespace AzureArchive.VideoTools.Tests;

/// <summary>
/// The one line a window shows about its own layer. These tests exist because that line is now the whole of what an
/// author sees about the stack: if it counts another window's effects, or adds up durations that the player will not
/// spend, there is nothing else on the screen that would say so.
/// </summary>
internal static class EffectLayerSummaryTests
{
    private const string CameraMove = "#aavt;camera;set;zoom=1.2;duration=800;easing=linear";
    private const string BackgroundMove = "#aavt;camera;set;scope=background;zoom=1.4;duration=300;easing=easeOut";
    private const string CharacterMove = "#aavt;char;3;move;dx=100;duration=600";
    private const string CharacterSecond = "#aavt;char;3;move;dy=50;duration=300";
    private const string Preset = "#aavt;fx;3;squash;frequency=4";

    internal static void AnEmptyRowLeavesEveryLayerEmpty()
    {
        EffectLayerSummary summary = EffectLayerSummary.Read(EffectLayerCatalog.Preset(1), string.Empty);
        AssertEx.Equal(0, summary.SegmentCount, "An empty row contains nothing.");
        AssertEx.Equal(0d, summary.SpanMs, "An empty row lasts no time.");
        AssertEx.False(summary.HasSegments, "An empty layer has no segments.");
        AssertEx.Equal("预设 1 · 本层暂无效果", summary.Line, "The line says the layer is empty rather than showing 0 段.");
    }

    internal static void ALayerCountsOnlyItsOwnDirectives()
    {
        string row = string.Join("\n", new[] { CharacterMove, CameraMove, CharacterSecond });
        EffectLayerSummary character = EffectLayerSummary.Read(EffectLayerCatalog.Character(3), row);
        EffectLayerSummary other = EffectLayerSummary.Read(EffectLayerCatalog.Character(4), row);
        EffectLayerSummary camera = EffectLayerSummary.Read(EffectLayerCatalog.Camera, row);

        AssertEx.Equal(2, character.SegmentCount, "Both of one character's moves are that character's layer.");
        AssertEx.Equal(900d, character.SpanMs, "Two appended moves occupy their sum.");
        AssertEx.Equal(0, other.SegmentCount, "A character the row does not touch has an empty layer.");
        AssertEx.Equal(1, camera.SegmentCount, "The camera directive belongs to the camera layer only.");
        AssertEx.Equal(800d, camera.SpanMs, "The camera layer reports its own span, not the row's.");
    }

    internal static void AnAuthoredOffsetMovesTheSpanInsteadOfExtendingIt()
    {
        // A second directive carrying at= does not queue behind the first: it runs at its own offset on the same
        // layer, so the layer's span is the end of the later segment and not a sum of durations.
        string appended = string.Join("\n", new[] { CharacterMove, CharacterSecond });
        string deferred = string.Join("\n", new[] { CharacterMove, CharacterSecond + ";at=300" });

        EffectLayerSummary queued = EffectLayerSummary.Read(EffectLayerCatalog.Character(3), appended);
        EffectLayerSummary offset = EffectLayerSummary.Read(EffectLayerCatalog.Character(3), deferred);

        AssertEx.Equal(900d, queued.SpanMs, "Without an offset the second move starts where the first ends.");
        AssertEx.Equal(600d, offset.SpanMs, "With at=300 the second move ends 600 ms into the layer.");
        AssertEx.Equal(2, offset.SegmentCount, "An offset places a segment; it does not remove one.");
    }

    internal static void TheTwoCameraScopesAreTwoLayersOfOneWindow()
    {
        string row = CameraMove + "\n" + BackgroundMove;
        EffectLayerSummary overall = EffectLayerSummary.Read(EffectLayerCatalog.Camera, row);
        EffectLayerSummary background = EffectLayerSummary.Read(EffectLayerCatalog.BackgroundCamera, row);

        AssertEx.Equal(1, overall.SegmentCount, "The whole-picture camera is one layer.");
        AssertEx.Equal(800d, overall.SpanMs, "The whole-picture camera keeps its own duration.");
        AssertEx.Equal(1, background.SegmentCount, "The background camera is a second, separate layer.");
        // The two scopes really do share one screen track and queue behind each other; the background's own extent is
        // still its own 300 ms, because that is the question a window can answer about its own layer.
        SlotActionQueuePlan plan = AssertEx.NotNull(SlotActionQueuePlanner.PlanDirectives(new[]
        {
            "#camera;set;zoom=1.2;duration=800;easing=linear",
            "#camera;set;scope=background;zoom=1.4;duration=300;easing=easeOut"
        }).Value);
        AssertEx.Equal(1100d, plan.TotalMs, "Both scopes queue on one track and take 1100 ms together.");
        AssertEx.Equal(300d, background.SpanMs, "The background camera reports its own extent, not its track position.");
        // One window owns both: they are written from the same place even though the runtime plays them apart.
        AssertEx.Equal(EffectLayerCatalog.CameraWindow, overall.Layer.Window, "Both camera scopes name one owner.");
        AssertEx.Equal(overall.Layer.Window, background.Layer.Window, "The two scopes are not two windows.");
    }

    internal static void APresetIsItsOwnLayerNextToTheCharacterItBelongsTo()
    {
        string row = Preset + "\n" + CharacterMove;
        EffectLayerSummary preset = EffectLayerSummary.Read(EffectLayerCatalog.Preset(3), row);
        EffectLayerSummary character = EffectLayerSummary.Read(EffectLayerCatalog.Character(3), row);

        AssertEx.Equal(1, preset.SegmentCount, "A preset on slot 3 is the preset layer of that slot.");
        AssertEx.Equal(1, character.SegmentCount, "The character's own move stays in the character layer.");
        AssertEx.True(preset.SpanMs > 0d, "A preset that plays has a span.");
        AssertEx.False(preset.Line.Contains("角色", StringComparison.Ordinal),
            "The preset layer does not report the character layer's effects.");
    }

    internal static void TheLineAndTheLogNameTheLayerAndItsOwner()
    {
        string row = CameraMove + "\n" + "#aavt;camera;set;zoom=1.5;duration=800;easing=linear";
        EffectLayerSummary summary = EffectLayerSummary.Read(EffectLayerCatalog.Camera, row);

        AssertEx.Equal(2, summary.SegmentCount, "Two camera directives are two segments of one layer.");
        AssertEx.Equal("1.60", summary.Seconds, "Seconds are printed at the precision the line shows.");
        AssertEx.Equal("镜头 · 本层 2 段 · 共 1.60 s", summary.Line, "The line names the layer and its two numbers.");
        AssertEx.True(summary.LogLine.StartsWith("[layer] camera ← 镜头效果 · ", StringComparison.Ordinal),
            "The log line names the resource and the window that owns it.");
    }

    internal static void AReadFailureIsSaidInsteadOfCounted()
    {
        EffectLayerSummary summary = EffectLayerSummary.Unavailable(
            EffectLayerCatalog.Preset(1),
            "当前没有选中的台词。");

        AssertEx.Equal(0, summary.SegmentCount, "A failed read reports no segments.");
        AssertEx.False(summary.HasSegments, "A failed read is not a layer with segments.");
        AssertEx.Equal("预设 1 · 当前没有选中的台词。", summary.Line, "The reason is shown instead of a stale count.");
    }

    internal static void UnparsableTextIsSaidButTheGoodDirectivesStillCount()
    {
        string row = CameraMove + "\n#aavt;camera;set;scope=background;scope=overall;zoom=2";
        EffectLayerSummary summary = EffectLayerSummary.Read(EffectLayerCatalog.Camera, row);

        AssertEx.Equal(1, summary.SegmentCount, "A sibling line that cannot be parsed does not hide the good one.");
        AssertEx.True(summary.Line.Contains("1 处无法解析", StringComparison.Ordinal),
            "The line says the row itself has text it could not read.");
    }
}

/// <summary>
/// The ownership table itself. One resource, one writer is the whole of what makes the overlay model safe, and it is
/// checked against the extractor's own resource keys rather than against a second copy of the same strings.
/// </summary>
internal static class EffectLayerCatalogTests
{
    internal static void EveryLayerNamesTheOneSurfaceThatOwnsIt()
    {
        AssertEx.Equal("camera", EffectLayerCatalog.Camera.ResourceKey, "The whole-picture camera keeps its key.");
        AssertEx.Equal(EffectLayerCatalog.CameraWindow, EffectLayerCatalog.Camera.Window,
            "The camera layer names the window that writes it.");
        AssertEx.Equal("camera:background", EffectLayerCatalog.BackgroundCamera.ResourceKey,
            "The background camera is a resource of its own.");
        AssertEx.Equal("preset:3", EffectLayerCatalog.Preset(3).ResourceKey, "A preset layer is keyed by its slot.");
        AssertEx.Equal("预设 3", EffectLayerCatalog.Preset(3).Label, "A preset layer reads as 预设 <n>.");
        AssertEx.Equal(EffectLayerCatalog.PresetWindow, EffectLayerCatalog.Preset(3).Window,
            "The preset layer names the window that writes it.");
        AssertEx.Equal("character:12", EffectLayerCatalog.Character(12).ResourceKey,
            "A character layer is keyed by its slot.");
        AssertEx.Equal("角色 12", EffectLayerCatalog.Character(12).Label, "A character layer reads as 角色 <n>.");
    }

    internal static void TheCatalogKeysAreTheExtractorKeys()
    {
        // If a directive form is ever renamed, this says so here rather than as a layer that quietly stops counting.
        AssertEx.Equal(EffectLayerCatalog.Camera.ResourceKey, KeyOf("#aavt;camera;set;zoom=1;duration=0"));
        AssertEx.Equal(EffectLayerCatalog.BackgroundCamera.ResourceKey,
            KeyOf("#aavt;camera;set;scope=background;zoom=1;duration=0"));
        AssertEx.Equal(EffectLayerCatalog.Character(3).ResourceKey, KeyOf("#aavt;char;3;move;dx=10;duration=0"));
        AssertEx.Equal(EffectLayerCatalog.Preset(3).ResourceKey, KeyOf("#aavt;fx;3;squash;frequency=4"));
    }

    private static string KeyOf(string publicDirective)
    {
        EmbeddedAavtExtraction extraction = new EmbeddedAavtDirectiveExtractor().Extract(publicDirective);
        AssertEx.Equal(0, extraction.Errors.Count,
            $"'{publicDirective}' must parse: {string.Join(" | ", extraction.Errors)}");
        AssertEx.Equal(1, extraction.Commands.Count, $"'{publicDirective}' must yield exactly one command.");
        return CommandResourceIdentity.KeyFor(extraction.Commands[0]);
    }
}
