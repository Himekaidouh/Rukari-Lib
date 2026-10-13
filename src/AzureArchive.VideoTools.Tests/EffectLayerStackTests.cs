using AzureArchive.VideoTools.Core.Commands;

namespace AzureArchive.VideoTools.Tests;

/// <summary>
/// The camera stack on its own, without a surface: what is appended where, what a move does, what a write would send.
/// The picture editor draws this stack with its own widgets, so its behaviour has to be checkable here rather than by
/// looking at that page.
/// </summary>
internal static class EffectLayerStackTests
{
    private const string FirstCamera = "#aavt;camera;set;x=0;y=0;zoom=1.2;duration=300;easing=easeOut";
    private const string SecondCamera = "#aavt;camera;set;x=200;y=0;zoom=1.6;duration=400;easing=linear";
    private const string Background = "#aavt;camera;set;scope=background;zoom=1.5;duration=250;easing=easeOut";

    private static EffectLayerRead Row(params string[] directives) => EffectLayerRead.From(CameraLayers.All, 
        string.Join("\n", directives),
        new string('A', 64),
        "token-1",
        "台词 #1");

    private static EffectDraft Draft(string publicDirective, string canonical, string layerKey) =>
        new(layerKey, publicDirective, canonical, EffectDirectiveLabel.Of(canonical), string.Empty);

    private static EffectDraft EffectDraftOf(string publicDirective, string canonical) =>
        Draft(publicDirective, canonical, EffectLayerCatalog.Camera.ResourceKey);

    private static EffectDraft BackgroundDraftOf(string publicDirective, string canonical) =>
        Draft(publicDirective, canonical, EffectLayerCatalog.BackgroundCamera.ResourceKey);

    internal static void AReadBecomesTheStackAndNamesItself()
    {
        var stack = new EffectLayerStack(CameraLayers.All);
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
        var stack = new EffectLayerStack(CameraLayers.All);
        stack.Adopt(Row(FirstCamera));
        stack.Adopt(EffectLayerRead.Failed("当前没有选中的台词。"));

        AssertEx.Equal(0, stack.CountOf(EffectLayerCatalog.Camera), "An unreadable row has nothing to show.");
        AssertEx.False(stack.CanWrite, "An unreadable row cannot be written to.");
        AssertEx.True(stack.Status.Contains("当前没有选中的台词", StringComparison.Ordinal), "The reason is kept.");
    }

    internal static void ADraftLandsInItsOwnLayerAndSelectsItself()
    {
        var stack = new EffectLayerStack(CameraLayers.All);
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
        var stack = new EffectLayerStack(CameraLayers.All);
        stack.Adopt(Row(FirstCamera));

        AssertEx.False(stack.Add(EffectDraft.None("还没有镜头草稿：先在画面编辑器里调好镜头。")),
            "A missing draft is refused.");

        AssertEx.Equal(1, stack.CountOf(EffectLayerCatalog.Camera), "Nothing was added.");
        AssertEx.False(stack.HasPendingChanges, "And nothing became pending.");
        AssertEx.True(stack.Status.Contains("还没有镜头草稿", StringComparison.Ordinal), "The reason is shown.");
    }

    internal static void MovingAndDeletingStayInsideTheirOwnLayer()
    {
        var stack = new EffectLayerStack(CameraLayers.All);
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
        var stack = new EffectLayerStack(CameraLayers.All);
        stack.Adopt(Row(FirstCamera, SecondCamera));

        AssertEx.False(stack.Move(EffectLayerCatalog.Camera, 0, -1), "The first effect cannot move up.");
        AssertEx.False(stack.HasPendingChanges, "A move that cannot happen is not a change.");
        AssertEx.True(stack.Status.Contains("已经在最前面", StringComparison.Ordinal), "The stack says why.");
        AssertEx.Equal(FirstCamera, stack.DirectivesOf(EffectLayerCatalog.Camera)[0], "Nothing moved.");
    }

    internal static void AWritesPlanCarriesBothLayersInOrder()
    {
        var stack = new EffectLayerStack(CameraLayers.All);
        stack.Adopt(Row(FirstCamera));
        stack.Add(BackgroundDraftOf(Background, "#camera;set;scope=background;zoom=1.5;duration=250;easing=easeOut"));
        stack.Add(EffectDraftOf(SecondCamera, "#camera;set;x=200;y=0;zoom=1.6;duration=400;easing=linear"));

        IReadOnlyList<EffectLayerEdit> edits = stack.BuildEdits();

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
        var stack = new EffectLayerStack(CameraLayers.All);
        stack.Adopt(Row(FirstCamera, Background));
        stack.Delete(EffectLayerCatalog.Camera, 0);

        IReadOnlyList<EffectLayerEdit> edits = stack.BuildEdits();

        AssertEx.Equal(0, edits[0].Directives.Count, "An empty layer is written as an empty list: that removes its line.");
        AssertEx.Equal(1, edits[1].Directives.Count, "The other layer keeps its effect.");
    }

    internal static void TheSelectionSurvivesARemovalAndTheLastOneClamps()
    {
        var stack = new EffectLayerStack(CameraLayers.All);
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
        var stack = new EffectLayerStack(CameraLayers.All);
        stack.Adopt(Row(FirstCamera));

        EffectLayerSummary before = stack.SummaryOf(EffectLayerCatalog.Camera);
        stack.Add(EffectDraftOf(SecondCamera, "#camera;set;x=200;y=0;zoom=1.6;duration=400;easing=linear"));
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
        EffectDraft fromPage = EffectDraft.FromDirective(published);
        AssertEx.True(fromPage.Available, "The form the picture editor publishes is a usable draft.");
        AssertEx.Equal(EffectLayerCatalog.Camera.ResourceKey, fromPage.LayerKey, "Its scope decides its layer.");
        AssertEx.Equal(published, fromPage.PublicDirective, "It is written back in the form the document holds.");
        AssertEx.True(fromPage.CanonicalDirective.StartsWith("#camera;", StringComparison.Ordinal),
            "And it carries the canonical form the planner reads.");
        AssertEx.True(fromPage.Label.Contains("设置", StringComparison.Ordinal), "With a readable label.");

        EffectDraft fromPlan = EffectDraft.FromDirective(
            "#camera;set;x=0;y=0;zoom=1.2;duration=300;easing=easeOut");
        AssertEx.True(fromPlan.Available, "The canonical form is a usable draft too.");
        AssertEx.True(fromPlan.PublicDirective.StartsWith("#aavt;camera;", StringComparison.Ordinal),
            "And it is converted rather than left canonical.");
        AssertEx.Equal(EffectLayerCatalog.Camera.ResourceKey, fromPlan.LayerKey, "With the same layer.");

        EffectDraft background = EffectDraft.FromDirective(
            "#aavt;camera;set;scope=background;zoom=1.5;duration=250;easing=easeOut");
        AssertEx.Equal(EffectLayerCatalog.BackgroundCamera.ResourceKey, background.LayerKey,
            "A background camera is a background draft.");

        AssertEx.False(EffectDraft.FromDirective(string.Empty).Available, "No draft is not a draft.");
        AssertEx.True(
            EffectDraft.FromDirective(string.Empty).Error.Contains("草稿", StringComparison.Ordinal),
            "And the refusal says what to do first.");

        // A draft of a family this build knows is readable as a draft; which window may stack it is the stack's own
        // decision, not the draft's, so the refusal happens where the layer set is known.
        EffectDraft character = EffectDraft.FromDirective("#char;3;move;dx=10;duration=0");
        AssertEx.True(character.Available, "A character effect is a draft of the character layer.");
        AssertEx.Equal(EffectLayerCatalog.Character(3).ResourceKey, character.LayerKey, "Which is its own layer.");
        var cameraWindow = new EffectLayerStack(CameraLayers.All);
        AssertEx.False(cameraWindow.Add(character), "The camera window refuses it.");
        AssertEx.True(cameraWindow.Status.Contains("角色 3", StringComparison.Ordinal),
            "And says which layer the window does own.");

        AssertEx.False(EffectDraft.FromDirective("这不是指令").Available, "Text that is not a directive is refused.");
    }

    internal static void ASecondEffectOfOneLayerIsCalledOutAsTheReasonToStopEditingElsewhere()
    {
        var stack = new EffectLayerStack(CameraLayers.All);
        stack.Adopt(Row(FirstCamera));
        AssertEx.True(stack.Consequence().Contains("播放这一句", StringComparison.Ordinal),
            "One effect per layer needs no warning.");

        stack.Add(EffectDraftOf(SecondCamera, "#camera;set;x=200;y=0;zoom=1.6;duration=400;easing=linear"));

        AssertEx.True(stack.Consequence().Contains("2 段", StringComparison.Ordinal),
            "Two effects say how many there are.");
        AssertEx.True(stack.Consequence().Contains("一次一条指令", StringComparison.Ordinal),
            "And which editors no longer fit this layer.");
    }

    /// <summary>
    /// The same stack over one preset layer: the second family to get a layer editor, and the proof that the stack is
    /// not a camera thing. A window owns its layer even when it is the only one it has, and a draft for somebody
    /// else's layer is refused.
    /// </summary>
    internal static void APresetLayerStacksItsOwnEffectsWithItsOwnLabels()
    {
        EffectLayer preset = EffectLayerCatalog.Preset(3);
        var stack = new EffectLayerStack(new[] { preset });
        stack.Adopt(EffectLayerRead.From(
            new[] { preset },
            "#aavt;char;3;move;dx=10;duration=200\n#aavt;fx;3;squash;frequency=4",
            new string('A', 64),
            "token-1",
            "台词 #1"));

        AssertEx.Equal(1, stack.CountOf(preset), "Only the preset layer's own effect is counted.");
        AssertEx.Equal(0, stack.CountOf(EffectLayerCatalog.Camera), "A layer this window does not own holds nothing.");
        AssertEx.True(stack.Rows[0].Label.Contains("压扁", StringComparison.Ordinal),
            "The row says which preset it is rather than printing the directive at the author.");
        AssertEx.True(stack.Rows[0].Label.Contains("频率=4", StringComparison.Ordinal), "And how strong it is.");

        AssertEx.False(
            stack.Add(EffectDraftOf(FirstCamera, "#camera;set;x=0;y=0;zoom=1.2;duration=300;easing=easeOut")),
            "A camera draft does not go into the preset window.");
        AssertEx.False(stack.HasPendingChanges, "And nothing became pending.");
        AssertEx.True(stack.Status.Contains("预设 3", StringComparison.Ordinal),
            "The refusal names the layer this window does own.");

        AssertEx.True(
            stack.Add(Draft(
                "#aavt;fx;3;sway;frequency=2",
                "#fx;3;sway;frequency=2",
                preset.ResourceKey)),
            "A draft for its own layer does.");
        AssertEx.Equal(2, stack.CountOf(preset), "It is the layer's second effect.");

        IReadOnlyList<EffectLayerEdit> edits = stack.BuildEdits();
        AssertEx.Equal(1, edits.Count, "One window writes one layer.");
        AssertEx.Equal(preset.ResourceKey, edits[0].LayerKey, "Its own.");
        AssertEx.Equal(2, edits[0].Directives.Count, "Both effects are written, in the order they play.");
        AssertEx.True(edits[0].Directives[0].StartsWith("#aavt;fx;3;squash;", StringComparison.Ordinal),
            "The effect that was already there is written first, in the compiler's canonical public form.");
    }

    /// <summary>
    /// Editing one effect of a stack instead of deleting and re-adding it (2026-10-14). The whole point is that the
    /// effect keeps its place: an author who tunes the second of four camera moves must not end up with it last.
    /// </summary>
    internal static void AReplacedEffectKeepsItsPlaceAndItsSelection()
    {
        var stack = new EffectLayerStack(CameraLayers.All);
        stack.Adopt(Row(FirstCamera, SecondCamera, Background));
        stack.Select(EffectLayerCatalog.Camera, 0);

        const string retuned = "#aavt;camera;set;x=40;y=0;zoom=1.9;duration=300;easing=easeOut";
        AssertEx.True(
            stack.Replace(EffectLayerCatalog.Camera, 0, EffectDraftOf(retuned, "#camera;set;x=40;y=0;zoom=1.9;duration=300;easing=easeOut")),
            "Replacing the first effect is accepted.");

        AssertEx.Equal(2, stack.CountOf(EffectLayerCatalog.Camera), "Replace does not add an effect.");
        AssertEx.Equal(1, stack.CountOf(EffectLayerCatalog.BackgroundCamera), "and does not touch the other layer.");
        AssertEx.True(stack.HasPendingChanges, "The stack now differs from the row.");
        AssertEx.Equal(retuned, stack.DirectivesOf(EffectLayerCatalog.Camera)[0], "The retuned effect is in its old place.");
        AssertEx.Equal(SecondCamera, stack.DirectivesOf(EffectLayerCatalog.Camera)[1], "The one behind it did not move.");
        AssertEx.Equal(0, stack.SelectedIndex, "The effect that was replaced is still the selected one.");
        AssertEx.True(stack.Status.Contains("第 1 段", StringComparison.Ordinal),
            "The status names the segment that changed: " + stack.Status);
    }

    internal static void AReplaceRefusesAnotherLayersDraftAndAnEmptyOne()
    {
        var stack = new EffectLayerStack(CameraLayers.All);
        stack.Adopt(Row(FirstCamera, SecondCamera));

        AssertEx.False(
            stack.Replace(
                EffectLayerCatalog.Camera,
                0,
                BackgroundDraftOf(Background, "#camera;set;scope=background;zoom=1.5;duration=250")),
            "A background effect does not replace a whole-picture one: moving between layers is a different act.");
        AssertEx.True(stack.Status.Contains("背景镜头", StringComparison.Ordinal),
            "The refusal names the layer the draft belongs to: " + stack.Status);
        AssertEx.Equal(FirstCamera, stack.DirectivesOf(EffectLayerCatalog.Camera)[0], "and changes nothing.");

        AssertEx.False(
            stack.Replace(EffectLayerCatalog.Camera, 0, EffectDraft.None("还没有可加入的效果草稿")),
            "An empty draft replaces nothing.");
        AssertEx.False(
            stack.Replace(
                EffectLayerCatalog.Camera,
                9,
                EffectDraftOf(FirstCamera, "#camera;set;x=0;y=0;zoom=1.2;duration=300;easing=easeOut")),
            "A segment that does not exist cannot be replaced.");
        AssertEx.False(stack.HasPendingChanges, "None of the refusals changed the stack.");
    }
}
