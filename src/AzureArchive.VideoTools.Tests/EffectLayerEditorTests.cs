using AzureArchive.VideoTools.Core.Commands;

namespace AzureArchive.VideoTools.Tests;

/// <summary>
/// The layer editing policy every layer surface shares: when it re-reads, what a press sends, and what a write does
/// with the answer. The three surfaces used to state this three times over; this is the one place it is decided, and
/// the game-side calls are faked so it can be decided here rather than in front of a screen.
/// </summary>
internal static class EffectLayerEditorTests
{
    private const string FirstCamera = "#aavt;camera;set;x=0;y=0;zoom=1.2;duration=300;easing=easeOut";
    private const string SecondCamera = "#aavt;camera;set;x=200;y=0;zoom=1.6;duration=400;easing=linear";

    private sealed class Fake
    {
        internal CameraLayerReadResult Read = CameraLayerReadResult.Ok();
        internal EffectLayerApply Apply = EffectLayerApply.Failed("未设置写入结果。");
        internal string? Draft = FirstCamera;
        internal long Now = 1_000_000_000;
        internal int Reads;
        internal readonly List<(string Revision, string Token, IReadOnlyList<EffectLayerEdit> Layers)> Writes = new();

        internal sealed record CameraLayerReadResult(bool Success, EffectLayerRead Value)
        {
            internal static CameraLayerReadResult Ok(params string[] directives) => new(
                true,
                EffectLayerRead.From(
                    CameraLayers.All,
                    string.Join("\n", directives),
                    new string('A', 64),
                    "token-1",
                    "台词 #1"));
        }

        internal EffectLayerEditor Editor() => new(
            CameraLayers.All,
            layers => { Reads++; return Read.Value; },
            (revision, token, layers) => { Writes.Add((revision, token, layers)); return Apply; },
            () => Draft,
            "镜头段",
            () => Now);
    }

    internal static void AReadHappensAtMostFourTimesASecond()
    {
        Fake fake = new();
        EffectLayerEditor editor = fake.Editor();

        AssertEx.True(editor.Refresh(), "The first refresh reads.");
        AssertEx.False(editor.Refresh(), "A second one in the same tick does not.");
        fake.Now += EffectLayerEditor.ReadInterval - 1;
        AssertEx.False(editor.Refresh(), "Nor does one just before the interval is up.");
        fake.Now += 2;
        AssertEx.True(editor.Refresh(), "One after the interval does.");
        AssertEx.Equal(2, fake.Reads, "Two reads in total.");
        AssertEx.True(editor.Refresh(force: true), "A surface that is shown again reads immediately.");
        AssertEx.Equal(3, fake.Reads, "Which is a third read.");
    }

    internal static void PendingWorkIsNotReReadOver()
    {
        Fake fake = new();
        EffectLayerEditor editor = fake.Editor();
        editor.Refresh();
        editor.Add();
        AssertEx.True(editor.HasPendingChanges, "The draft is now pending.");

        fake.Now += EffectLayerEditor.ReadInterval * 10;
        AssertEx.False(editor.Refresh(), "A refresh past the interval still does not read over it.");
        AssertEx.Equal(1, fake.Reads, "One read from the beginning.");
        AssertEx.Equal(1, editor.CountOf(EffectLayerCatalog.Camera), "And the draft is still there.");

        AssertEx.True(editor.Refresh(force: true), "An explicit refresh is allowed to throw the draft away.");
        AssertEx.False(editor.HasPendingChanges, "Which it does.");
    }

    internal static void AddUsesTheComposedDraftAndSaysWhenThereIsNone()
    {
        Fake fake = new();
        EffectLayerEditor editor = fake.Editor();
        editor.Refresh();

        AssertEx.True(editor.Add(), "The composed draft is added.");
        AssertEx.Equal(1, editor.CountOf(EffectLayerCatalog.Camera), "To its own layer.");
        AssertEx.True(editor.SegmentLabel.EndsWith("1/1", StringComparison.Ordinal), "The selector follows it.");
        AssertEx.True(editor.DeleteLabel.Contains('1'), "And the delete button names it.");

        fake.Draft = null;
        AssertEx.False(editor.Add(), "With nothing composed there is nothing to add.");
        AssertEx.True(editor.Status.Contains("草稿", StringComparison.Ordinal), "And the reason says so.");
        AssertEx.Equal(1, editor.CountOf(EffectLayerCatalog.Camera), "The layer did not change.");
    }

    internal static void AWriteSendsEveryOwnedLayerWithTheStackGuards()
    {
        Fake fake = new();
        EffectLayerEditor editor = fake.Editor();
        editor.Refresh();
        editor.Add();
        fake.Draft = SecondCamera;
        editor.Add();
        fake.Apply = EffectLayerApply.Ok(EffectLayerRead.From(
            CameraLayers.All, FirstCamera, new string('B', 64), "token-1", "台词 #1"));

        EffectLayerApply? applied = editor.Apply();

        AssertEx.True(applied.HasValue && applied.Value.Success, "The write reported success.");
        AssertEx.Equal(1, fake.Writes.Count, "One press is one write.");
        (string revision, string token, IReadOnlyList<EffectLayerEdit> layers) = fake.Writes[0];
        AssertEx.Equal(new string('A', 64), revision, "Guarded by the revision the stack was read from.");
        AssertEx.Equal("token-1", token, "And by the token it was read from.");
        AssertEx.Equal(2, layers.Count, "Both layers this surface owns are written.");
        AssertEx.Equal(2, layers[0].Directives.Count, "The one with effects carries both, in order.");
        AssertEx.Equal(0, layers[1].Directives.Count, "The empty one is written empty, which removes its line.");
        AssertEx.False(editor.HasPendingChanges, "A successful write clears the pending state.");
        AssertEx.True(editor.Status.Contains("已写入当前句", StringComparison.Ordinal), "And says so.");
        AssertEx.Equal(1, editor.CountOf(EffectLayerCatalog.Camera),
            "The write's own readback is the new truth, not the stack that was sent.");
    }

    internal static void ANothingToWriteAndAFailedWriteAreDifferentAnswers()
    {
        Fake fake = new();
        EffectLayerEditor editor = fake.Editor();
        editor.Refresh();

        AssertEx.True(editor.Apply() is null, "Nothing pending writes nothing.");
        AssertEx.True(editor.Status.Contains("没有需要写入的改动", StringComparison.Ordinal), "And says that.");
        AssertEx.Equal(0, fake.Writes.Count, "No write was attempted.");

        editor.Add();
        fake.Apply = EffectLayerApply.Failed("Additional-prompt revision changed after the draft was created.");
        EffectLayerApply? failed = editor.Apply();
        AssertEx.True(failed.HasValue && !failed.Value.Success, "A refused write is reported as one.");
        AssertEx.True(editor.HasPendingChanges, "And the work stays exactly where it was.");
        AssertEx.True(editor.Status.Contains("写入失败", StringComparison.Ordinal), "With the reason on screen.");

        // A row that could not be read cannot be written to, and that is not a failed write either.
        editor.Refresh(force: true);
        EffectLayerEditor unreadable = new(
            CameraLayers.All,
            _ => EffectLayerRead.Failed("当前没有选中的台词。"),
            (_, _, _) => EffectLayerApply.Ok(EffectLayerRead.Failed("unused")),
            () => FirstCamera,
            "镜头段",
            () => 0);
        unreadable.Refresh();
        AssertEx.True(unreadable.Apply() is null, "An unreadable row is not written to.");
        AssertEx.True(unreadable.Status.Contains("没有需要写入的改动", StringComparison.Ordinal),
            "There is simply nothing pending on it.");
    }

    internal static void TheButtonsActOnTheLayerTheSurfaceIsShowing()
    {
        Fake fake = new();
        EffectLayerEditor editor = fake.Editor();
        editor.Refresh();
        editor.Add();
        fake.Draft = SecondCamera;
        editor.Add();

        editor.SelectLayer(EffectLayerCatalog.BackgroundCamera);
        AssertEx.Equal(EffectLayerCatalog.BackgroundCamera.ResourceKey, editor.SelectedLayer.ResourceKey,
            "The surface can point its buttons at its other layer.");
        AssertEx.False(editor.CanMove(-1), "Which has no effects of its own.");
        AssertEx.True(editor.SegmentLabel.EndsWith("0/0", StringComparison.Ordinal), "So the selector says so.");

        editor.SelectLayer(EffectLayerCatalog.Camera);
        AssertEx.True(editor.SegmentLabel.EndsWith("1/2", StringComparison.Ordinal),
            "Pointing back shows that layer's own selection.");
        editor.Stack.Select(EffectLayerCatalog.Camera, 0);
        AssertEx.True(editor.CanMove(1), "And moving is possible there.");
        AssertEx.True(editor.Move(1), "Which works inside that layer.");
        AssertEx.Equal(SecondCamera, editor.DirectivesOf(EffectLayerCatalog.Camera)[0],
            "The second effect is now first.");
    }
}
