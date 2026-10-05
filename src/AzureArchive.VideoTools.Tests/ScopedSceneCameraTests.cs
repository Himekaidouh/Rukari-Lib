using AzureArchive.VideoTools.Core.Cameras;
using AzureArchive.VideoTools.Core.Characters;
using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Core.Results;
using AzureArchive.VideoTools.Core.VisualEditor;

namespace AzureArchive.VideoTools.Tests;

internal static class ScopedSceneCameraTests
{
    private const string Node = "aaaaaaaa-0000-0000-0000-000000000001";

    public static void LegacyScopeAndConstructorRemainCompatible()
    {
        var legacy = new SceneCameraCommand(SceneCameraOperation.Set, null, null, null, null,
            1.5f, null, 200, CharacterTransformEasing.Linear);
        AssertEx.Equal(SceneCameraScope.Overall, legacy.Scope);
        AssertEx.True(typeof(SceneCameraCommand).GetConstructors().Any(constructor =>
            constructor.GetParameters().Length == 9 && constructor.GetParameters().All(parameter => !parameter.IsOptional)));
        CanonicalTimelineCommand canonical = AssertEx.NotNull(new SceneCameraCommandFamilyCompiler()
            .Canonicalize("#camera;set;scope=overall;zoom=1.5;duration=200;easing=linear").Value);
        AssertEx.Equal(0, canonical.PublicSlot);
        AssertEx.Equal("#camera;set;zoom=1.5;duration=200;easing=linear", canonical.Directive);
    }

    public static void BackgroundScopeCanonicalizesToItsOwnResource()
    {
        var compiler = new SceneCameraCommandFamilyCompiler();
        CanonicalTimelineCommand canonical = AssertEx.NotNull(compiler.Canonicalize(
            "#camera;set;zoom=1.5;scope=BACKGROUND;duration=250;easing=easeOut").Value);
        AssertEx.Equal(SceneCameraCommandFamilyCompiler.BackgroundResourceSlot, canonical.PublicSlot);
        AssertEx.Equal("#camera;set;scope=background;zoom=1.5;duration=250;easing=easeOut", canonical.Directive);
        AssertEx.Equal(SceneCameraScope.Background, Parse(canonical.Directive).Scope);
        AssertEx.True(SceneCameraCommandFamilyCompiler.IsCameraResourceSlot(0));
        AssertEx.True(SceneCameraCommandFamilyCompiler.IsCameraResourceSlot(6));
        AssertEx.False(SceneCameraCommandFamilyCompiler.IsCameraResourceSlot(1));
    }

    public static void RejectsUnknownAndDuplicateScopes()
    {
        var parser = new SceneCameraDirectiveParser();
        AssertEx.False(parser.Parse("#camera;set;scope=all;zoom=2").Success);
        AssertEx.False(parser.Parse("#camera;set;scope=background;scope=overall;zoom=2").Success);
        AssertEx.False(SceneCameraCommandValidator.Validate(Parse("#camera;set;zoom=2")
            with { Scope = (SceneCameraScope)42 }).Success);
    }

    public static void ComposesOverallOutsideBackgroundAndKeepsLegacyFormula()
    {
        SceneCameraComposedLayers composed = AssertEx.NotNull(SceneCameraCompositionPlanner.Compose(new(
            new(100f, -20f, 2f), new(30f, 10f, 1.5f))).Value);
        AssertEx.Equal(new SceneCameraLayerComposition(290f, -10f, 3f), composed.Back);
        AssertEx.Equal(new SceneCameraLayerComposition(200f, -40f, 2f), composed.Spine);

        SceneCameraComposedLayers legacy = AssertEx.NotNull(SceneCameraCompositionPlanner.Compose(new(
            new(100f, -20f, 2f), SceneCameraState.Default)).Value);
        AssertEx.Equal(legacy.Spine, legacy.Back);
        AssertEx.Equal(new SceneCameraLayerComposition(200f, -40f, 2f), legacy.Back);
    }

    public static void RejectsCombinedZoomAndPositionOverflow()
    {
        AssertEx.False(SceneCameraCompositionPlanner.Compose(new(new(0, 0, 4), new(0, 0, 4))).Success);
        AssertEx.False(SceneCameraCompositionPlanner.Compose(new(new(0, 0, 0.1f), new(0, 0, 0.1f))).Success);
        AssertEx.False(SceneCameraCompositionPlanner.Compose(new(new(float.MaxValue, 0, 2), SceneCameraState.Default)).Success);
        AssertEx.True(SceneCameraCompositionPlanner.Compose(new(new(0, 0, 2), new(0, 0, 4))).Success);
        AssertEx.True(SceneCameraCompositionPlanner.Compose(new(new(0, 0, 1), new(0, 0, 0.1f))).Success);
    }

    public static void BackgroundOwnershipNeverRestoresOrWritesSpine()
    {
        AssertEx.Equal(new SceneCameraPhysicalWrites(true, true, false, false),
            SceneCameraCompositionPlanner.WritesFor(SceneCameraMotionFields.BackgroundPosition | SceneCameraMotionFields.BackgroundZoom));
        AssertEx.Equal(new SceneCameraPhysicalWrites(true, false, false, false),
            SceneCameraCompositionPlanner.WritesFor(SceneCameraMotionFields.BackgroundPosition));
        AssertEx.Equal(new SceneCameraPhysicalWrites(true, true, true, true),
            SceneCameraCompositionPlanner.WritesFor(SceneCameraMotionFields.OverallZoom));
        AssertEx.Equal(new SceneCameraPhysicalWrites(false, false, false, false),
            SceneCameraCompositionPlanner.WritesFor(SceneCameraMotionFields.None));
    }

    public static void RoundedCombinedBoundaryUsesThePhysicalFloatRange()
    {
        var motion = new SceneCameraMotion();
        Apply(motion, "#camera;set;zoom=1.6;duration=0", 0);
        Apply(motion, "#camera;set;scope=background;zoom=5;duration=0", 0);
        AssertEx.Equal(8f, AssertEx.NotNull(SceneCameraCompositionPlanner.Compose(Sample(motion, 0)).Value).Back.Zoom);
    }

    public static void IndependentDurationsComposeWithoutRestartingTheOtherScope()
    {
        var motion = new SceneCameraMotion();
        Apply(motion, "#camera;set;zoom=2;duration=2000;easing=linear", 0);
        Apply(motion, "#camera;set;scope=background;zoom=1.5;duration=1000;easing=linear", 0);
        SceneCameraComposition halfwayBackground = Sample(motion, 0.5);
        Near(1.25f, halfwayBackground.Overall.Zoom);
        Near(1.25f, halfwayBackground.Background.Zoom);
        Near(1.5625f, AssertEx.NotNull(SceneCameraCompositionPlanner.Compose(halfwayBackground).Value).Back.Zoom);
        Near(1.5f, Sample(motion, 1).Overall.Zoom);
        Near(1.5f, Sample(motion, 1).Background.Zoom);
        AssertEx.Equal(new SceneCameraComposition(new(0, 0, 2), new(0, 0, 1.5f)), Sample(motion, 2));
        AssertEx.Equal(SceneCameraMotionFields.None, motion.ActiveFields(2));
    }

    public static void OrdinaryScopeCommandOrderHasTheSameComposition()
    {
        var first = new SceneCameraMotion();
        Apply(first, "#camera;set;x=100;zoom=2;duration=1000", 0);
        Apply(first, "#camera;set;scope=background;x=30;zoom=1.5;duration=500", 0);
        var second = new SceneCameraMotion();
        Apply(second, "#camera;set;scope=background;x=30;zoom=1.5;duration=500", 0);
        Apply(second, "#camera;set;x=100;zoom=2;duration=1000", 0);
        AssertEx.Equal(Sample(first, 0.25), Sample(second, 0.25));
        AssertEx.Equal(first.Target, second.Target);
    }

    public static void PositionEditPreservesAnOngoingZoom()
    {
        var motion = new SceneCameraMotion();
        Apply(motion, "#camera;set;zoom=2;duration=1000", 0);
        Apply(motion, "#camera;set;x=100;duration=500", 0.25);
        SceneCameraComposition sampled = Sample(motion, 0.5);
        Near(1.5f, sampled.Overall.Zoom);
        Near(100f, AssertEx.NotNull(SceneCameraCompositionPlanner.Compose(sampled).Value).Spine.OffsetX);
        AssertEx.Equal(SceneCameraMotionFields.OverallPosition | SceneCameraMotionFields.OverallZoom,
            motion.ActiveFields(0.5));
    }

    public static void PublishedNguiDefaultEasingSamplesRemainCompatible()
    {
        foreach (var example in new[]
        {
            ("linear", 1.25f), ("easeIn", 1.0761205f),
            ("easeOut", 1.3826834f), ("easeInOut", 1.0908451f)
        })
        {
            var motion = new SceneCameraMotion();
            Apply(motion, $"#camera;set;zoom=2;duration=1000;easing={example.Item1}", 0);
            Near(example.Item2, Sample(motion, 0.25).Overall.Zoom);
            AssertEx.Equal(2f, Sample(motion, 1).Overall.Zoom);
        }
    }

    public static void RejectsUnsafeIntermediateProductWithoutChangingMotion()
    {
        var motion = new SceneCameraMotion(new(new(0, 0, 8), SceneCameraState.Default));
        Apply(motion, "#camera;set;zoom=1;duration=1000", 0);
        SceneCameraComposition before = motion.Target;
        SceneCameraCommand background = Parse("#camera;set;scope=background;zoom=8;duration=100");
        AssertEx.False(motion.Begin(background, Plan(motion, background), 0).Success);
        AssertEx.Equal(before, motion.Target);
        Apply(motion, "#camera;set;scope=background;zoom=8;duration=100", 1);
        AssertEx.Equal(8f, Sample(motion, 1.1).Background.Zoom);

        var small = new SceneCameraMotion(new(new(0, 0, 0.1f), SceneCameraState.Default));
        Apply(small, "#camera;set;zoom=1;duration=1000", 0);
        SceneCameraCommand shrink = Parse("#camera;set;scope=background;zoom=0.1;duration=100");
        AssertEx.False(small.Begin(shrink, Plan(small, shrink), 0).Success);
    }

    public static void BackgroundResetAndPartialSeedPreserveOverall()
    {
        var motion = new SceneCameraMotion(new(new(100, 20, 2), new(30, 10, 1.5f)));
        Apply(motion, "#camera;reset;scope=background;duration=0", 0);
        AssertEx.Equal(new SceneCameraState(100, 20, 2), motion.Target.Overall);
        AssertEx.Equal(SceneCameraState.Default, motion.Target.Background);
        AssertEx.True(motion.Seed(new(new(float.NaN, 0, 0), new(5, 6, 1.25f)), false, true, 1).Success);
        AssertEx.Equal(new SceneCameraState(100, 20, 2), motion.Target.Overall);
        AssertEx.Equal(new SceneCameraState(5, 6, 1.25f), motion.Target.Background);
        AssertEx.True(motion.Seed(new(new(10, 15, 1.5f), new(float.NaN, 0, 0)), true, false, 2).Success);
        AssertEx.Equal(new SceneCameraState(5, 6, 1.25f), motion.Target.Background);
    }

    public static void PartialSeedPreservesTheOtherScopeMotion()
    {
        var motion = new SceneCameraMotion();
        Apply(motion, "#camera;set;zoom=2;duration=2000", 0);
        Apply(motion, "#camera;set;scope=background;zoom=1.5;duration=2000", 0);
        AssertEx.True(motion.Seed(new(SceneCameraState.Default, new(0, 0, 1.25f)), false, true, 0.5).Success);
        Near(1.5f, Sample(motion, 1).Overall.Zoom);
        Near(1.25f, Sample(motion, 1).Background.Zoom);
        Apply(motion, "#camera;set;scope=background;zoom=1.5;duration=1000", 1);
        AssertEx.True(motion.Seed(new(new(0, 0, 1.5f), SceneCameraState.Default), true, false, 1.25).Success);
        Near(1.5f, Sample(motion, 1.5).Overall.Zoom);
        Near(1.375f, Sample(motion, 1.5).Background.Zoom);
    }

    public static void RejectsIntermediatePositionOverflowWithoutChangingState()
    {
        var motion = new SceneCameraMotion(new(new(float.MaxValue / 2, 0, 1), new(float.MaxValue / 2, 0, 1)));
        SceneCameraCommand command = Parse("#camera;set;x=0;zoom=2;duration=1000");
        SceneCameraComposition before = motion.Target;
        AssertEx.False(motion.Begin(command, Plan(motion, command), 0).Success);
        AssertEx.Equal(before, motion.Target);
        Apply(motion, "#camera;set;x=0;zoom=2;duration=0", 0);
        AssertEx.Equal(new SceneCameraState(0, 0, 2), motion.Target.Overall);
    }

    public static void LegacyCombinedMoveAndZoomInterpolatesPhysicalOffset()
    {
        var motion = new SceneCameraMotion();
        Apply(motion, "#camera;set;x=100;y=-20;zoom=2;duration=1000", 0);
        SceneCameraComposedLayers middle = AssertEx.NotNull(SceneCameraCompositionPlanner.Compose(Sample(motion, 0.5)).Value);
        Near(100, middle.Spine.OffsetX);
        Near(-20, middle.Spine.OffsetY);
        Near(1.5f, middle.Spine.Zoom);
        AssertEx.Equal(middle.Spine, middle.Back);
        AssertEx.Equal(new SceneCameraState(100, -20, 2), Sample(motion, 1).Overall);
    }

    public static void ImmediateReplayValidatesTheFinalCompositionAtomically()
    {
        var motion = new SceneCameraMotion(new(new(0, 0, 8), SceneCameraState.Default));
        var baseline = new SceneCameraBaseline("card", new(0, 0, 4));
        SceneCameraCommand command = Parse("#camera;set;scope=background;zoom=1;duration=0");
        var planner = new SceneCameraPlanner();
        SceneCameraTarget restore = AssertEx.NotNull(planner.PlanReplayRestore(command, motion.Target.Background, baseline).Value);
        SceneCameraTarget target = AssertEx.NotNull(planner.Plan(command, restore.State, baseline).Value);
        AssertEx.True(motion.BeginReplay(command, restore, target, 0).Success);
        AssertEx.Equal(new SceneCameraComposition(new(0, 0, 8), SceneCameraState.Default), motion.Target);

        SceneCameraCommand animated = command with { DurationMilliseconds = 1000 };
        SceneCameraTarget animatedTarget = AssertEx.NotNull(planner.Plan(animated, restore.State, baseline).Value);
        AssertEx.False(motion.BeginReplay(animated, restore, animatedTarget, 1).Success);
        AssertEx.Equal(new SceneCameraComposition(new(0, 0, 8), SceneCameraState.Default), motion.Target);
    }

    public static void BackgroundRelativeReplayRestoresOnlyItsOwnEntry()
    {
        var motion = new SceneCameraMotion(new(new(100, 0, 1.5f), new(10, 0, 1.25f)));
        var baseline = new SceneCameraBaseline("card", motion.Target.Background);
        SceneCameraCommand command = Parse("#camera;move;scope=background;dx=20;dzoom=0.5;duration=0");
        var planner = new SceneCameraPlanner();
        AssertEx.True(motion.Begin(command,
            AssertEx.NotNull(planner.Plan(command, motion.Target.Background, baseline).Value), 0).Success);
        SceneCameraComposition first = motion.Target;
        SceneCameraTarget restore = AssertEx.NotNull(planner.PlanReplayRestore(command, motion.Target.Background, baseline).Value);
        AssertEx.True(motion.Begin(command, restore, 1).Success);
        AssertEx.True(motion.Begin(command,
            AssertEx.NotNull(planner.Plan(command, motion.Target.Background, baseline).Value), 1).Success);
        AssertEx.Equal(first, motion.Target);
        AssertEx.Equal(new SceneCameraState(100, 0, 1.5f), motion.Target.Overall);
    }

    public static void DualScopeIndexAndChainKeepIndependentResetAndPresence()
    {
        ProjectSnapshot project = Project(
            "#aavt;camera;set;zoom=2\n#aavt;camera;set;scope=background;zoom=1.5",
            "#aavt;camera;reset;scope=background",
            string.Empty);
        PreviewChainDirectiveIndex index = PreviewChainDirectiveIndex.Build(project, true);
        AssertEx.Equal(2, index.CameraSceneCount);
        AssertEx.Equal(3, index.CameraDirectiveCount);
        AssertEx.True(index.TryGetCamera(Node, 0, out string legacy));
        AssertEx.Equal(SceneCameraScope.Overall, Parse(legacy).Scope);
        AssertEx.True(index.TryGetCamera(Node, 0, SceneCameraScope.Background, out _));
        PreviewCameraChainResolution chain = AssertEx.NotNull(new PreviewCameraChainResolver(project)
            .Resolve(project.Nodes[0].Scenes[2].Key,
                (key, scope) => index.TryGetCamera(key.NodeGuid, key.SceneIndex, scope, out string directive) ? directive : null).Value);
        AssertEx.True(chain.HasOverallInheritedStart);
        AssertEx.True(chain.HasBackgroundInheritedStart);
        AssertEx.Equal(new SceneCameraComposition(new(0, 0, 2), SceneCameraState.Default), chain.InheritedComposition);
        AssertEx.Equal(1, chain.OverallFoldedCommandCount);
        AssertEx.Equal(2, chain.BackgroundFoldedCommandCount);
    }

    public static void BackgroundOnlyChainDoesNotClaimOverallInheritance()
    {
        ProjectSnapshot project = Project("#aavt;camera;set;scope=background;zoom=1.5", string.Empty);
        PreviewChainDirectiveIndex index = PreviewChainDirectiveIndex.Build(project, true);
        PreviewCameraChainResolution chain = AssertEx.NotNull(new PreviewCameraChainResolver(project)
            .Resolve(project.Nodes[0].Scenes[1].Key,
                (key, scope) => index.TryGetCamera(key.NodeGuid, key.SceneIndex, scope, out string directive) ? directive : null).Value);
        AssertEx.False(chain.HasOverallInheritedStart);
        AssertEx.True(chain.HasBackgroundInheritedStart);
        AssertEx.Equal(SceneCameraState.Default, chain.InheritedState);
        AssertEx.Equal(new SceneCameraState(0, 0, 1.5f), chain.BackgroundInheritedState);
    }

    public static void ScopedDraftAndFrameUseTheCorrectComposition()
    {
        var builder = new VisualCameraDraftBuilder();
        string background = AssertEx.NotNull(builder.BuildSet(SceneCameraScope.Background,
            new(30, 10, 1.5f), 250, CharacterTransformEasing.Linear).Value);
        AssertEx.Equal("#aavt;camera;set;scope=background;x=30;y=10;zoom=1.5;duration=250;easing=linear", background);
        AssertEx.Equal("#aavt;camera;reset;scope=background;duration=0;easing=linear",
            AssertEx.NotNull(builder.BuildReset(SceneCameraScope.Background, 0, CharacterTransformEasing.Linear).Value));
        var viewport = new VisualEditorRect(0, 0, 1600, 900);
        var composition = new SceneCameraComposition(new(100, -20, 2), new(30, 10, 1.5f));
        Result<VisualEditorRect> overall = VisualCameraFrameMath.CameraToViewportFrame(composition, SceneCameraScope.Overall, 900, viewport);
        Result<VisualEditorRect> legacy = VisualCameraFrameMath.CameraToViewportFrame(composition.Overall, 900, viewport);
        AssertEx.True(overall.Success && legacy.Success);
        AssertEx.Equal(legacy.Value, overall.Value);
        Result<VisualEditorRect> back = VisualCameraFrameMath.CameraToViewportFrame(composition, SceneCameraScope.Background, 900, viewport);
        AssertEx.True(back.Success);
        Near(2f / 3f, back.Value.Width / overall.Value.Width);
    }

    public static void BackgroundFrameDragAndZoomInvertTheCombinedFrame()
    {
        var viewport = new VisualEditorRect(0, 0, 1600, 900);
        var composition = new SceneCameraComposition(new(100, -20, 2), new(30, 10, 1.5f));
        Result<VisualEditorRect> frame = VisualCameraFrameMath.CameraToViewportFrame(composition,
            SceneCameraScope.Background, 900, viewport);
        AssertEx.True(frame.Success, frame.Error);
        var center = new VisualEditorPoint(frame.Value.X + frame.Value.Width / 2,
            frame.Value.Y + frame.Value.Height / 2);
        Result<SceneCameraState> unchanged = VisualCameraFrameMath.MoveFrameCenter(center, composition,
            SceneCameraScope.Background, 900, viewport);
        AssertEx.True(unchanged.Success, unchanged.Error);
        AssertEx.True(Math.Abs(composition.Background.X - unchanged.Value.X) < 0.001f);
        AssertEx.True(Math.Abs(composition.Background.Y - unchanged.Value.Y) < 0.001f);
        Result<float> zoom = VisualCameraFrameMath.ZoomFromFrameWidth(frame.Value.Width, composition,
            SceneCameraScope.Background, viewport);
        AssertEx.True(zoom.Success, zoom.Error);
        Near(1.5f, zoom.Value);
        var upper = new SceneCameraComposition(new(0, 0, 4), new(0, 0, 2));
        AssertEx.False(VisualCameraFrameMath.ZoomFromFrameWidth(
            VisualCameraFrameMath.FrameWidth(viewport, 5), upper, SceneCameraScope.Overall, viewport).Success);
    }

    private static SceneCameraCommand Parse(string directive) => AssertEx.NotNull(new SceneCameraDirectiveParser().Parse(directive).Value);
    private static SceneCameraTarget Plan(SceneCameraMotion motion, SceneCameraCommand command) =>
        AssertEx.NotNull(new SceneCameraPlanner().Plan(command, motion.Target.ForScope(command.Scope),
            new SceneCameraBaseline("test-card", SceneCameraState.Default)).Value);
    private static void Apply(SceneCameraMotion motion, string directive, double now)
    {
        SceneCameraCommand command = Parse(directive);
        Result result = motion.Begin(command, Plan(motion, command), now);
        AssertEx.True(result.Success, result.Error);
    }
    private static SceneCameraComposition Sample(SceneCameraMotion motion, double now)
    {
        var result = motion.Sample(now);
        AssertEx.True(result.Success, result.Error);
        return result.Value;
    }
    private static void Near(float expected, float actual) => AssertEx.True(Math.Abs(expected - actual) < 0.00001f,
        $"Expected approximately {expected}, got {actual}.");

    private static ProjectSnapshot Project(params string[] prompts)
    {
        SceneSnapshot[] scenes = prompts.Select((prompt, index) => new SceneSnapshot(
            new SceneKey(Node, index, new string((char)('A' + index), 64)), "managed-fixture", $"scene {index}", true,
            string.Empty, 0, 0, prompt, string.Empty, string.Empty, string.Empty, string.Empty, 0, 0, 0)).ToArray();
        var node = new StoryNodeSnapshot(0, StoryNodeKind.Script, "managed-fixture", Node, true,
            "test", Array.Empty<string>(), scenes);
        return new ProjectSnapshot(new("managed-fixture", "key", new string('F', 64), 0, DateTimeOffset.UtcNow),
            "managed-fixture", "test", new(null, string.Empty, string.Empty), new[] { node }, Array.Empty<ProjectDiagnostic>());
    }
}
