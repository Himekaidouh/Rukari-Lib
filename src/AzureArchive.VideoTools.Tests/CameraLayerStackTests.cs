using AzureArchive.VideoTools.Core.Commands;

namespace AzureArchive.VideoTools.Tests;

/// <summary>
/// The camera stack on its own, without a surface: what is appended where, what a move does, what a write would send.
/// The picture editor draws this stack with its own widgets, so its behaviour has to be checkable here rather than by
/// looking at that page.
/// </summary>
internal static class CameraLayerStackTests
{
    private const string FirstCamera = "#aavt;camera;set;x=0;y=0;zoom=1.2;duration=300;easing=easeOut";
    private const string SecondCamera = "#aavt;camera;set;x=200;y=0;zoom=1.6;duration=400;easing=linear";
    private const string Background = "#aavt;camera;set;scope=background;zoom=1.5;duration=250;easing=easeOut";

    private static CameraLayerRead Row(params string[] directives) => CameraLayerRead.From(
        string.Join("\n", directives),
        new string('A', 64),
        "token-1",
        "台词 #1");

    private static CameraDraft Draft(string publicDirective, string canonical, string layerKey) =>
        new(layerKey, publicDirective, canonical, CameraLayerLabel.Of(canonical), string.Empty);

    private static CameraDraft CameraDraftOf(string publicDirective, string canonical) =>
        Draft(publicDirective, canonical, EffectLayerCatalog.Camera.ResourceKey);

    private static CameraDraft BackgroundDraftOf(string publicDirective, string canonical) =>
        Draft(publicDirective, canonical, EffectLayerCatalog.BackgroundCamera.ResourceKey);

    internal static void AReadBecomesTheStackAndNamesItself()
    {
        var stack = new CameraLayerStack();
        stack.Adopt(Row(FirstCamera, Background, SecondCamera));

        AssertEx.Equal(2, stack.CountOf(EffectLayerCatalog.Camera), "Two camera directives are two effects.");
        AssertEx.Equal(1, stack.CountOf(EffectLayerCatalog.BackgroundCamera), "The background is its own layer.");
        AssertEx.False(stack.HasPendingChanges, "Reading a row is not a change.");
        AssertEx.Equal("台词 #1", stack.RowLabel, "The stack keeps the row's own name.");
        AssertEx.True(stack.CanWrite, "A read row can be written back.");
        AssertEx.True(stack.Status.Contains("2 段镜头", StringComparison.Ordinal), "The status counts both layers.");
        AssertEx.Equal(FirstCamera, stack.DirectivesOf(EffectLayerCatalog.Camera)[0], "Effects keep authored order.");
        AssertEx.Equal(SecondCamera, stack.DirectivesOf(EffectLayerCatalog.Camera)[1], "Effects keep authored order.");
    }

    internal static void AFailedReadClearsTheStackInsteadOfShowingTheOldRow()
    {
        var stack = new CameraLayerStack();
        stack.Adopt(Row(FirstCamera));
        stack.Adopt(CameraLayerRead.Failed("当前没有选中的台词。"));

        AssertEx.Equal(0, stack.CountOf(EffectLayerCatalog.Camera), "An unreadable row has nothing to show.");
        AssertEx.False(stack.CanWrite, "An unreadable row cannot be written to.");
        AssertEx.True(stack.Status.Contains("当前没有选中的台词", StringComparison.Ordinal), "The reason is kept.");
    }

    internal static void ADraftLandsInItsOwnLayerAndSelectsItself()
    {
        var stack = new CameraLayerStack();
        stack.Adopt(Row(FirstCamera));

        AssertEx.True(stack.Add(BackgroundDraftOf(Background, "#camera;set;scope=background;zoom=1.5;duration=250;easing=easeOut")),
            "A background draft is accepted.");

        AssertEx.Equal(1, stack.CountOf(EffectLayerCatalog.BackgroundCamera), "It landed in the background layer.");
        AssertEx.Equal(1, stack.CountOf(EffectLayerCatalog.Camera), "The other layer did not move.");
        AssertEx.True(stack.HasPendingChanges, "The stack now differs from the row.");
        AssertEx.Equal(EffectLayerCatalog.BackgroundCamera.ResourceKey, stack.SelectedLayer.ResourceKey,
            "The effect just added is the selected one.");
        AssertEx.Equal(0, stack.SelectedIndex, "And it is the first of its own layer.");
        AssertEx.True(stack.Selected?.Label.Contains("缩放=1.5", StringComparison.Ordinal) == true,
            "The selection is the row that was added.");
    }

    internal static void AnUnavailableDraftSaysWhyAndChangesNothing()
    {
        var stack = new CameraLayerStack();
        stack.Adopt(Row(FirstCamera));

        AssertEx.False(stack.Add(CameraDraft.None("还没有镜头草稿：先在画面编辑器里调好镜头。")),
            "A missing draft is refused.");

        AssertEx.Equal(1, stack.CountOf(EffectLayerCatalog.Camera), "Nothing was added.");
        AssertEx.False(stack.HasPendingChanges, "And nothing became pending.");
        AssertEx.True(stack.Status.Contains("还没有镜头草稿", StringComparison.Ordinal), "The reason is shown.");
    }

    internal static void MovingAndDeletingStayInsideTheirOwnLayer()
    {
        var stack = new CameraLayerStack();
        stack.Adopt(Row(FirstCamera, Background, SecondCamera));

        AssertEx.True(stack.Move(EffectLayerCatalog.Camera, 1, -1), "The second camera effect moves up.");
        AssertEx.Equal(SecondCamera, stack.DirectivesOf(EffectLayerCatalog.Camera)[0], "It is now first.");
        AssertEx.Equal(FirstCamera, stack.DirectivesOf(EffectLayerCatalog.Camera)[1], "The one it passed is second.");
        AssertEx.Equal(Background, stack.DirectivesOf(EffectLayerCatalog.BackgroundCamera)[0],
            "The background layer's own order did not move.");
        AssertEx.Equal(0, stack.SelectedIndex, "The moved effect stays selected.");

        AssertEx.True(stack.Delete(EffectLayerCatalog.Camera, 0), "The first camera effect can be deleted.");
        AssertEx.Equal(1, stack.CountOf(EffectLayerCatalog.Camera), "One camera effect is left.");
        AssertEx.Equal(FirstCamera, stack.DirectivesOf(EffectLayerCatalog.Camera)[0], "And it is the other one.");
        AssertEx.Equal(1, stack.CountOf(EffectLayerCatalog.BackgroundCamera), "The background layer is untouched.");
    }

    internal static void MovingPastTheEndSaysSoAndIsNotAChange()
    {
        var stack = new CameraLayerStack();
        stack.Adopt(Row(FirstCamera, SecondCamera));

        AssertEx.False(stack.Move(EffectLayerCatalog.Camera, 0, -1), "The first effect cannot move up.");
        AssertEx.False(stack.HasPendingChanges, "A move that cannot happen is not a change.");
        AssertEx.True(stack.Status.Contains("已经在最前面", StringComparison.Ordinal), "The stack says why.");
        AssertEx.Equal(FirstCamera, stack.DirectivesOf(EffectLayerCatalog.Camera)[0], "Nothing moved.");
    }

    internal static void AWritesPlanCarriesBothLayersInOrder()
    {
        var stack = new CameraLayerStack();
        stack.Adopt(Row(FirstCamera));
        stack.Add(BackgroundDraftOf(Background, "#camera;set;scope=background;zoom=1.5;duration=250;easing=easeOut"));
        stack.Add(CameraDraftOf(SecondCamera, "#camera;set;x=200;y=0;zoom=1.6;duration=400;easing=linear"));

        IReadOnlyList<CameraLayerEdit> edits = stack.BuildEdits();

        AssertEx.Equal(2, edits.Count, "Both layers are written.");
        AssertEx.Equal(EffectLayerCatalog.Camera.ResourceKey, edits[0].LayerKey, "The camera layer comes first.");
        AssertEx.Equal(2, edits[0].Directives.Count, "It carries both of its effects.");
        AssertEx.Equal(FirstCamera, edits[0].Directives[0], "In the order they play.");
        AssertEx.Equal(SecondCamera, edits[0].Directives[1], "In the order they play.");
        AssertEx.Equal(EffectLayerCatalog.BackgroundCamera.ResourceKey, edits[1].LayerKey,
            "The background layer comes second.");
        AssertEx.Equal(1, edits[1].Directives.Count, "With its own one effect.");
    }

    internal static void AnEmptiedLayerIsStillWritten()
    {
        var stack = new CameraLayerStack();
        stack.Adopt(Row(FirstCamera, Background));
        stack.Delete(EffectLayerCatalog.Camera, 0);

        IReadOnlyList<CameraLayerEdit> edits = stack.BuildEdits();

        AssertEx.Equal(0, edits[0].Directives.Count, "An empty layer is written as an empty list: that removes its line.");
        AssertEx.Equal(1, edits[1].Directives.Count, "The other layer keeps its effect.");
    }

    internal static void TheSelectionSurvivesARemovalAndTheLastOneClamps()
    {
        var stack = new CameraLayerStack();
        stack.Adopt(Row(FirstCamera, SecondCamera));
        stack.Select(EffectLayerCatalog.Camera, 1);

        AssertEx.True(stack.DeleteSelected(), "The selected effect can be deleted.");
        AssertEx.Equal(0, stack.SelectedIndex, "The selection clamps to what is left.");
        AssertEx.True(stack.Selected is not null, "Something is still selected.");

        AssertEx.True(stack.DeleteSelected(), "The last effect can be deleted too.");
        AssertEx.Equal(0, stack.CountOf(EffectLayerCatalog.Camera), "The layer is empty now.");
        AssertEx.Equal(-1, stack.SelectedIndex, "An empty layer has nothing selected.");
        AssertEx.False(stack.DeleteSelected(), "And deleting again does nothing.");
    }

    internal static void TheHeaderSummaryFollowsTheStackNotTheSavedRow()
    {
        var stack = new CameraLayerStack();
        stack.Adopt(Row(FirstCamera));

        EffectLayerSummary before = stack.SummaryOf(EffectLayerCatalog.Camera);
        stack.Add(CameraDraftOf(SecondCamera, "#camera;set;x=200;y=0;zoom=1.6;duration=400;easing=linear"));
        EffectLayerSummary after = stack.SummaryOf(EffectLayerCatalog.Camera);

        AssertEx.Equal(1, before.SegmentCount, "One effect before.");
        AssertEx.Equal(300d, before.SpanMs, "And its own 300 ms.");
        AssertEx.Equal(2, after.SegmentCount, "Two effects after the draft was added.");
        AssertEx.Equal(700d, after.SpanMs, "Which run one after the other.");
        AssertEx.Equal(0, stack.SummaryOf(EffectLayerCatalog.BackgroundCamera).SegmentCount,
            "The layer nobody touched is still empty.");
    }

    internal static void ADraftComesFromTheCameraDirectiveWhicheverFormItArrivesIn()
    {
        // The page publishes the public form — this exact shape came back from a real session — and a canonical
        // directive is what a plan or a hand-written card carries. Both have to become the same kind of draft.
        const string published = "#aavt;camera;set;x=270;y=0;zoom=1.364;duration=500;easing=easeOut";
        CameraDraft fromPage = CameraDraft.FromDirective(published);
        AssertEx.True(fromPage.Available, "The form the picture editor publishes is a usable draft.");
        AssertEx.Equal(EffectLayerCatalog.Camera.ResourceKey, fromPage.LayerKey, "Its scope decides its layer.");
        AssertEx.Equal(published, fromPage.PublicDirective, "It is written back in the form the document holds.");
        AssertEx.True(fromPage.CanonicalDirective.StartsWith("#camera;", StringComparison.Ordinal),
            "And it carries the canonical form the planner reads.");
        AssertEx.True(fromPage.Label.Contains("设置", StringComparison.Ordinal), "With a readable label.");

        CameraDraft fromPlan = CameraDraft.FromDirective(
            "#camera;set;x=0;y=0;zoom=1.2;duration=300;easing=easeOut");
        AssertEx.True(fromPlan.Available, "The canonical form is a usable draft too.");
        AssertEx.True(fromPlan.PublicDirective.StartsWith("#aavt;camera;", StringComparison.Ordinal),
            "And it is converted rather than left canonical.");
        AssertEx.Equal(EffectLayerCatalog.Camera.ResourceKey, fromPlan.LayerKey, "With the same layer.");

        CameraDraft background = CameraDraft.FromDirective(
            "#aavt;camera;set;scope=background;zoom=1.5;duration=250;easing=easeOut");
        AssertEx.Equal(EffectLayerCatalog.BackgroundCamera.ResourceKey, background.LayerKey,
            "A background camera is a background draft.");

        AssertEx.False(CameraDraft.FromDirective(string.Empty).Available, "No draft is not a draft.");
        AssertEx.False(CameraDraft.FromDirective("#char;3;move;dx=10;duration=0").Available,
            "A character effect cannot be added to a camera layer.");
        AssertEx.True(CameraDraft.FromDirective(string.Empty).Error.Contains("镜头草稿", StringComparison.Ordinal),
            "And the refusal says what to do first.");
    }

    internal static void ASecondEffectOfOneLayerIsCalledOutAsTheReasonToStopEditingElsewhere()
    {
        var stack = new CameraLayerStack();
        stack.Adopt(Row(FirstCamera));
        AssertEx.True(stack.Consequence().Contains("播放这一句", StringComparison.Ordinal),
            "One effect per layer needs no warning.");

        stack.Add(CameraDraftOf(SecondCamera, "#camera;set;x=200;y=0;zoom=1.6;duration=400;easing=linear"));

        AssertEx.True(stack.Consequence().Contains("2 段", StringComparison.Ordinal),
            "Two effects say how many there are.");
        AssertEx.True(stack.Consequence().Contains("一次一条指令", StringComparison.Ordinal),
            "And which editors no longer fit this layer.");
    }
}
