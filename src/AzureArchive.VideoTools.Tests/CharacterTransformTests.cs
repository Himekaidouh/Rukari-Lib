using AzureArchive.VideoTools.Core.Characters;
using AzureArchive.VideoTools.Core.Commands;

namespace AzureArchive.VideoTools.Tests;

internal static class CharacterTransformTests
{
    public static void ParsesAbsoluteScreenSpaceCommand()
    {
        var result = new CharacterTransformDirectiveParser().Parse(
            "#char;3;set;x=500;y=-120;rotation=15;flipX=true;duration=800;easing=easeInOut");
        AssertEx.True(result.Success, result.Error);
        CharacterTransformCommand command = AssertEx.NotNull(result.Value);
        AssertEx.Equal(3, command.PublicSlot);
        AssertEx.Equal(CharacterTransformOperation.Set, command.Operation);
        AssertEx.Equal(500f, command.X);
        AssertEx.Equal(-120f, command.Y);
        AssertEx.Equal(15f, command.RotationDegrees);
        AssertEx.Equal(true, command.FlipX);
        AssertEx.Equal(800, command.DurationMilliseconds);
        AssertEx.Equal(CharacterTransformEasing.EaseInOut, command.Easing);
        AssertEx.Equal(2960f, CharacterCoordinateSpace.ScreenWidth);
    }

    public static void ParsesRelativeAndResetCommands()
    {
        var parser = new CharacterTransformDirectiveParser();
        CharacterTransformCommand move = AssertEx.NotNull(parser.Parse(
            "#char;2;move;dx=-200;dy=50;drotation=-10;duration=350;easing=linear").Value);
        AssertEx.Equal(CharacterTransformOperation.Move, move.Operation);
        AssertEx.Equal(-200f, move.DeltaX);
        AssertEx.Equal(50f, move.DeltaY);
        AssertEx.Equal(-10f, move.DeltaRotationDegrees);

        CharacterTransformCommand reset = AssertEx.NotNull(
            parser.Parse("#char;2;reset;duration=500;easing=easeOut").Value);
        AssertEx.Equal(CharacterTransformOperation.Reset, reset.Operation);
        AssertEx.Equal(500, reset.DurationMilliseconds);
        AssertEx.Equal(CharacterTransformEasing.EaseOut, reset.Easing);
    }

    public static void RejectsMalformedAndUnsafeCommands()
    {
        var parser = new CharacterTransformDirectiveParser();
        string[] invalid =
        {
            "#char;0;set;x=1",
            "#char;6;set;x=1",
            "#char;3;set;x=1;x=2",
            "#char;3;set;x=NaN",
            "#char;3;set;dx=1",
            "#char;3;move;flipX=true",
            "#char;3;reset;x=0",
            "#char;3;set",
            "#char;3;move",
            "#char;3;reset;duration=-1",
            "#char;3;set;x=1;easing=bezier"
        };

        foreach (string directive in invalid)
        {
            var result = parser.Parse(directive);
            AssertEx.False(result.Success, $"Expected rejection: {directive}");
        }
    }

    public static void PlansAbsoluteSetAndDeterministicFlip()
    {
        CharacterTransformState baselineState = State(0, 0, -1, 0, 10, 5);
        var baseline = new CharacterTransformBaseline("scene-a", 3, "iori", baselineState);
        CharacterTransformCommand command = AssertEx.NotNull(
            new CharacterTransformDirectiveParser().Parse(
                "#char;3;set;x=500;rotation=15;flipX=true;duration=800;easing=easeInOut").Value);

        CharacterTransformTarget target = AssertEx.NotNull(
            new CharacterTransformPlanner().Plan(command, baselineState, baseline).Value);
        AssertEx.Equal(new CharacterVector3(500, 0, -1), target.State.Position);
        AssertEx.Equal(new CharacterVector3(0, -170, 15), target.State.LocalEulerAngles);
        AssertEx.True(target.PositionChanged);
        AssertEx.True(target.RotationChanged);

        CharacterTransformCommand unflip = AssertEx.NotNull(
            new CharacterTransformDirectiveParser().Parse("#char;3;set;flipX=false").Value);
        CharacterTransformTarget restoredOrientation = AssertEx.NotNull(
            new CharacterTransformPlanner().Plan(unflip, target.State, baseline).Value);
        // The unflip target is 180 degrees from -170; both +10 and -350 are
        // the same heading (mod 360) and the exact representative depends on
        // the half-turn tie-break. Assert the semantic end state instead.
        float unflippedY = restoredOrientation.State.LocalEulerAngles.Y;
        AssertEx.False(CharacterScreenRotation.IsHorizontallyFlipped(unflippedY));
        AssertEx.True(MathF.Abs(CharacterScreenRotation.ToScreenDegrees(
                restoredOrientation.State.LocalEulerAngles.Z,
                unflippedY)
            - 15f) < 0.001f);
    }

    public static void ScreenRotationKeepsItsSignAcrossHorizontalFlip()
    {
        AssertEx.Equal(30f, CharacterScreenRotation.ToScreenDegrees(30f, 0f));
        AssertEx.Equal(30f, CharacterScreenRotation.ToScreenDegrees(330f, 180f));
        AssertEx.Equal(-30f, CharacterScreenRotation.ToScreenDegrees(30f, 180f));

        float physical = CharacterScreenRotation.ToPhysicalDegrees(30f, 180f, 0f);
        AssertEx.Equal(-30f, physical);
        AssertEx.Equal(30f, CharacterScreenRotation.ToScreenDegrees(physical, 180f));

        AssertEx.False(CharacterScreenRotation.IsHorizontallyFlipped(10f));
        AssertEx.True(CharacterScreenRotation.IsHorizontallyFlipped(190f));
        AssertEx.True(CharacterScreenRotation.IsHorizontallyFlipped(-170f));
        float offsetPhysical = CharacterScreenRotation.ToPhysicalDegrees(-25f, 190f, 0f);
        AssertEx.Equal(25f, offsetPhysical);
        AssertEx.Equal(-25f, CharacterScreenRotation.ToScreenDegrees(offsetPhysical, 190f));
    }

    public static void DispatchConversionPreservesCommandedTweenPath()
    {
        // Deliberate multi-turn: the raw magnitude survives the conversion.
        AssertEx.Equal(375f, CharacterScreenRotation.ToPhysicalDegreesFromLive(375f, 0f, 0f));
        AssertEx.Equal(-375f, CharacterScreenRotation.ToPhysicalDegreesFromLive(375f, 180f, 10f));
        AssertEx.Equal(-720f, CharacterScreenRotation.ToPhysicalDegreesFromLive(-720f, 0f, 15f));

        // Ordinary poses take the shortest arc from the LIVE value without
        // normalizing it: -10 -> 25 travels 35 degrees, not 395.
        float ordinary = CharacterScreenRotation.ToPhysicalDegreesFromLive(
            25f,
            0f,
            -10.0000305f);
        AssertEx.Equal(25f, ordinary);

        // Mirrored sign still applies for flipped characters.
        float flipped = CharacterScreenRotation.ToPhysicalDegreesFromLive(
            25f,
            180f,
            190f);
        AssertEx.Equal(335f, flipped);
        AssertEx.Equal(25f, CharacterScreenRotation.ToScreenDegrees(flipped, 180f));
    }

    public static void FlipOnlyAndReplayPreserveScreenRotation()
    {
        CharacterTransformState baselineState = State(0, 0, -1, 0, 0, 30);
        var baseline = new CharacterTransformBaseline("scene-flip", 2, "mika", baselineState);
        CharacterTransformCommand flip = AssertEx.NotNull(
            new CharacterTransformDirectiveParser().Parse(
                "#char;2;set;flipX=true;duration=1000;easing=easeInOut").Value);
        var planner = new CharacterTransformPlanner();

        CharacterTransformTarget flipped = AssertEx.NotNull(
            planner.Plan(flip, baselineState, baseline).Value);
        AssertEx.True(CharacterScreenRotation.IsHorizontallyFlipped(
            flipped.State.LocalEulerAngles.Y));
        AssertEx.Equal(30f, flipped.State.LocalEulerAngles.Z);
        float flippedPhysicalZ = CharacterScreenRotation.ToPhysicalDegrees(
            flipped.State.LocalEulerAngles.Z,
            flipped.State.LocalEulerAngles.Y,
            30f);
        AssertEx.Equal(30f, CharacterScreenRotation.ToScreenDegrees(
            flippedPhysicalZ,
            flipped.State.LocalEulerAngles.Y));

        CharacterTransformTarget restored = AssertEx.NotNull(
            planner.PlanReplayRestore(flip, flipped.State, baseline).Value);
        AssertEx.False(CharacterScreenRotation.IsHorizontallyFlipped(
            restored.State.LocalEulerAngles.Y));
        AssertEx.Equal(30f, restored.State.LocalEulerAngles.Z);
    }

    public static void RelativeRotationUsesScreenDirectionWhileFlipped()
    {
        CharacterTransformState current = State(0, 0, -1, 0, 180, 30);
        var baseline = new CharacterTransformBaseline("scene-relative", 2, "mika", current);
        CharacterTransformCommand rotate = AssertEx.NotNull(
            new CharacterTransformDirectiveParser().Parse(
                "#char;2;move;drotation=-60;duration=800;easing=easeOut").Value);

        CharacterTransformTarget target = AssertEx.NotNull(
            new CharacterTransformPlanner().Plan(rotate, current, baseline).Value);
        AssertEx.Equal(-30f, target.State.LocalEulerAngles.Z);
        float physicalZ = CharacterScreenRotation.ToPhysicalDegrees(
            target.State.LocalEulerAngles.Z,
            target.State.LocalEulerAngles.Y,
            330f);
        AssertEx.Equal(-30f, CharacterScreenRotation.ToScreenDegrees(
            physicalZ,
            target.State.LocalEulerAngles.Y));
    }

    public static void PlansRelativeMovementFromCurrentState()
    {
        CharacterTransformState baselineState = State(0, 0, -1, 0, 0, 0);
        CharacterTransformState current = State(500, 20, -1, 0, 180, 15);
        var baseline = new CharacterTransformBaseline("scene-a", 3, "iori", baselineState);
        CharacterTransformCommand command = AssertEx.NotNull(
            new CharacterTransformDirectiveParser().Parse(
                "#char;3;move;dx=-200;dy=50;drotation=-20;duration=350").Value);

        CharacterTransformTarget target = AssertEx.NotNull(
            new CharacterTransformPlanner().Plan(command, current, baseline).Value);
        AssertEx.Equal(new CharacterVector3(300, 70, -1), target.State.Position);
        AssertEx.Equal(new CharacterVector3(0, 180, -5), target.State.LocalEulerAngles);
    }

    public static void PlansRotationThroughShortestArc()
    {
        CharacterTransformState baselineState = State(0, 0, -1, 0, 0, 0);
        var baseline = new CharacterTransformBaseline("scene-a", 5, "hoshino", baselineState);

        CharacterTransformCommand move = AssertEx.NotNull(
            new CharacterTransformDirectiveParser().Parse(
                "#char;5;move;drotation=-12;duration=600;easing=easeOut").Value);
        CharacterTransformTarget moved = AssertEx.NotNull(
            new CharacterTransformPlanner().Plan(move, State(0, 0, -1, 0, 0, 0), baseline).Value);
        AssertEx.Equal(-12f, moved.State.LocalEulerAngles.Z);

        CharacterTransformCommand absolute = AssertEx.NotNull(
            new CharacterTransformDirectiveParser().Parse(
                "#char;5;set;rotation=10;duration=600;easing=easeOut").Value);
        CharacterTransformTarget wrapped = AssertEx.NotNull(
            new CharacterTransformPlanner().Plan(absolute, State(0, 0, -1, 0, 0, 350), baseline).Value);
        AssertEx.Equal(370f, wrapped.State.LocalEulerAngles.Z);

        CharacterTransformCommand restore = AssertEx.NotNull(
            new CharacterTransformDirectiveParser().Parse("#char;5;reset;duration=400").Value);
        CharacterTransformTarget resetTarget = AssertEx.NotNull(
            new CharacterTransformPlanner().Plan(restore, State(0, 0, -1, 0, 0, 348), baseline).Value);
        AssertEx.Equal(360f, resetTarget.State.LocalEulerAngles.Z);
    }

    public static void SetRotationBeyond360PerformsDeliberateMultiTurn()
    {
        CharacterTransformState baselineState = State(0, 0, -1, 0, 0, 0);
        var baseline = new CharacterTransformBaseline("scene-a", 5, "hoshino", baselineState);
        CharacterTransformCommand spin = AssertEx.NotNull(
            new CharacterTransformDirectiveParser().Parse(
                "#char;5;set;rotation=720;duration=1200;easing=easeInOut").Value);
        var planner = new CharacterTransformPlanner();

        // From zero the tween must traverse both full turns, not collapse.
        CharacterTransformTarget fromZero = AssertEx.NotNull(
            planner.Plan(spin, State(0, 0, -1, 0, 0, 0), baseline).Value);
        AssertEx.Equal(720f, fromZero.State.LocalEulerAngles.Z);

        // From a non-zero live value the commanded end pose is authoritative.
        CharacterTransformTarget fromOffset = AssertEx.NotNull(
            planner.Plan(spin, State(0, 0, -1, 0, 0, 170), baseline).Value);
        AssertEx.Equal(720f, fromOffset.State.LocalEulerAngles.Z);

        // Relative moves beyond a full turn accumulate raw as well.
        CharacterTransformCommand relative = AssertEx.NotNull(
            new CharacterTransformDirectiveParser().Parse(
                "#char;5;move;drotation=720;duration=1200;easing=easeInOut").Value);
        CharacterTransformTarget moved = AssertEx.NotNull(
            planner.Plan(relative, State(0, 0, -1, 0, 0, 0), baseline).Value);
        AssertEx.Equal(720f, moved.State.LocalEulerAngles.Z);
    }

    public static void SetRotationWithin360TakesShortestPathFromLiveValue()
    {
        CharacterTransformState baselineState = State(0, 0, -1, 0, 0, 0);
        var baseline = new CharacterTransformBaseline("scene-a", 2, "mika", baselineState);
        CharacterTransformCommand command = AssertEx.NotNull(
            new CharacterTransformDirectiveParser().Parse(
                "#char;2;set;rotation=25;duration=600;easing=easeInOut").Value);

        // The live value may sit slightly below zero; the tween must take the
        // 35-degree path instead of gaining a spurious extra revolution.
        CharacterTransformTarget target = AssertEx.NotNull(
            new CharacterTransformPlanner().Plan(
                command,
                State(0, 0, -1, 0, 0, -10.0000305f),
                baseline).Value);
        AssertEx.Equal(25f, target.State.LocalEulerAngles.Z);
    }

    public static void ResetAfterMultiTurnKeepsVisualContinuity()
    {
        CharacterTransformState baselineState = State(0, 0, -1, 0, 0, 0);
        var baseline = new CharacterTransformBaseline("scene-a", 5, "hoshino", baselineState);
        CharacterTransformCommand reset = AssertEx.NotNull(
            new CharacterTransformDirectiveParser().Parse("#char;5;reset;duration=600").Value);

        // 720 is visually identical to the official 0: the reset must not
        // spin the character backwards through both turns.
        CharacterTransformTarget target = AssertEx.NotNull(
            new CharacterTransformPlanner().Plan(
                reset,
                State(0, 0, -1, 0, 0, 720),
                baseline).Value);
        AssertEx.Equal(720f, target.State.LocalEulerAngles.Z);
    }

    public static void ValidatorBoundsMultiTurnRotation()
    {
        var parser = new CharacterTransformDirectiveParser();
        AssertEx.True(parser.Parse("#char;3;set;rotation=720").Success);
        AssertEx.True(parser.Parse("#char;3;move;drotation=-1080").Success);
        AssertEx.False(parser.Parse("#char;3;set;rotation=3601").Success);
        AssertEx.False(parser.Parse("#char;3;move;drotation=-7200").Success);
    }

    public static void MultiTurnContinuityAcrossFlipAndReplayRestore()
    {
        var planner = new CharacterTransformPlanner();

        // A flip toggle after eulerY accumulated past ±360 must tween the
        // short way (360 -> 370), not spin backwards 350 degrees to 10.
        CharacterTransformState baselineState = State(0, 0, -1, 0, 10, 0);
        var baseline = new CharacterTransformBaseline("scene-a", 3, "iori", baselineState);
        CharacterTransformCommand unflip = AssertEx.NotNull(
            new CharacterTransformDirectiveParser().Parse("#char;3;set;flipX=false").Value);
        CharacterTransformTarget target = AssertEx.NotNull(
            planner.Plan(unflip, State(0, 0, -1, 0, 360, 0), baseline).Value);
        AssertEx.Equal(370f, target.State.LocalEulerAngles.Y);

        // Scene re-entry after a 720-degree somersault restores to the
        // visually-identical 720 instead of spinning backwards to 0.
        CharacterTransformCommand relative = AssertEx.NotNull(
            new CharacterTransformDirectiveParser().Parse(
                "#char;3;move;drotation=30;duration=2000;easing=easeInOut").Value);
        CharacterTransformTarget restored = AssertEx.NotNull(
            planner.PlanReplayRestore(
                relative,
                State(500, 0, -1, 0, 0, 720),
                baseline).Value);
        AssertEx.Equal(720f, restored.State.LocalEulerAngles.Z);
    }

    public static void ResetRestoresCapturedBaseline()
    {
        CharacterTransformState baselineState = State(-400, 80, -1, 0, 25, 8);
        var baseline = new CharacterTransformBaseline("scene-a", 3, "iori", baselineState);
        CharacterTransformCommand reset = AssertEx.NotNull(
            new CharacterTransformDirectiveParser().Parse(
                "#char;3;reset;duration=500;easing=easeInOut").Value);

        CharacterTransformTarget target = AssertEx.NotNull(
            new CharacterTransformPlanner().Plan(
                reset,
                State(700, -100, -1, 0, 205, 45),
                baseline).Value);
        AssertEx.Equal(baselineState, target.State);
        AssertEx.True(target.PositionChanged);
        AssertEx.True(target.RotationChanged);
        AssertEx.True(target.IsReset);
    }

    public static void BaselineStoreKeepsFirstSnapshotAndInvalidatesOnSceneChange()
    {
        var store = new CharacterTransformBaselineStore();
        CharacterTransformState first = State(0, 0, -1, 0, 0, 0);
        CharacterTransformState changed = State(500, 0, -1, 0, 180, 30);

        CharacterTransformBaseline captured = AssertEx.NotNull(
            store.Capture("scene-a", 3, "iori", first).Value);
        CharacterTransformBaseline recaptured = AssertEx.NotNull(
            store.Capture("scene-a", 3, "iori", changed).Value);
        AssertEx.Equal(captured, recaptured);
        AssertEx.Equal(first, recaptured.State);

        AssertEx.NotNull(store.Capture("scene-b", 3, "iori", changed).Value);
        AssertEx.False(store.Get("scene-a", 3, "iori").Success);
        AssertEx.True(store.Get("scene-b", 3, "iori").Success);
    }

    public static void ReplayRestoreTouchesOnlyCommandAxes()
    {
        CharacterTransformState baselineState = State(0, 20, -1, 5, 10, 15);
        CharacterTransformState current = State(494, 90, -2, 25, 190, 45);
        var baseline = new CharacterTransformBaseline("scene-a", 3, "iori", baselineState);
        CharacterTransformCommand command = AssertEx.NotNull(
            new CharacterTransformDirectiveParser().Parse(
                "#char;3;move;dx=500;drotation=30;duration=2000;easing=easeInOut").Value);

        CharacterTransformTarget restored = AssertEx.NotNull(
            new CharacterTransformPlanner().PlanReplayRestore(
                command,
                current,
                baseline).Value);

        AssertEx.Equal(new CharacterVector3(0, 90, -2), restored.State.Position);
        AssertEx.Equal(new CharacterVector3(25, 190, 15), restored.State.LocalEulerAngles);
        AssertEx.True(restored.PositionChanged);
        AssertEx.True(restored.RotationChanged);
        AssertEx.Equal(0, restored.DurationMilliseconds);
    }

    public static void RepeatedRelativeReplayStartsFromCapturedBaseline()
    {
        CharacterTransformState baselineState = State(0, 0, -1, 0, 0, 0);
        var baseline = new CharacterTransformBaseline("scene-a", 3, "iori", baselineState);
        CharacterTransformCommand command = AssertEx.NotNull(
            new CharacterTransformDirectiveParser().Parse(
                "#char;3;move;dx=500;duration=2000;easing=easeInOut").Value);
        var planner = new CharacterTransformPlanner();

        CharacterTransformTarget first = AssertEx.NotNull(
            planner.Plan(command, baselineState, baseline).Value);
        CharacterTransformTarget restored = AssertEx.NotNull(
            planner.PlanReplayRestore(
                command,
                State(494, 0, -1, 0, 0, 0),
                baseline).Value);
        CharacterTransformTarget replayed = AssertEx.NotNull(
            planner.Plan(command, restored.State, baseline).Value);

        AssertEx.Equal(500f, first.State.Position.X);
        AssertEx.Equal(0f, restored.State.Position.X);
        AssertEx.Equal(500f, replayed.State.Position.X);
    }

    public static void InheritedStartIsIdempotentAcrossRepeatedFlipApplication()
    {
        CharacterTransformState official = State(100, -50, -1, 4, 10, 12);
        var baseline = new CharacterTransformBaseline(
            "scene-inherited-flip", 2, "mika", official);
        var inherited = new PreviewChainSlotState(
            2,
            PreviewChainAxisState.Clear(),
            PreviewChainAxisState.Clear(),
            PreviewChainAxisState.Clear(),
            true,
            1)
        { FlipControlled = true };
        var planner = new CharacterInheritedStartPlanner();

        CharacterInheritedStartTarget first = AssertEx.NotNull(
            planner.Plan(inherited, official, baseline).Value);
        CharacterInheritedStartTarget repeated = AssertEx.NotNull(
            planner.Plan(inherited, first.State, baseline).Value);

        AssertEx.True(CharacterScreenRotation.IsHorizontallyFlipped(
            first.State.LocalEulerAngles.Y));
        AssertEx.Equal(first.State, repeated.State);
        AssertEx.True(first.RotationChanged);
        AssertEx.False(repeated.PositionChanged);
        AssertEx.False(repeated.RotationChanged);
    }

    public static void InheritedRelativeOffsetsAlwaysUseOfficialBaseline()
    {
        CharacterTransformState official = State(100, -50, -1, 4, 15, 30);
        var baseline = new CharacterTransformBaseline(
            "scene-inherited-relative", 2, "mika", official);
        var inherited = new PreviewChainSlotState(
            2,
            PreviewChainAxisState.Relative(300),
            PreviewChainAxisState.Relative(-20),
            PreviewChainAxisState.Relative(-15),
            false,
            3);
        CharacterTransformState alreadyAccumulated = State(
            700, -90, -1, 4, 15, 0);
        var planner = new CharacterInheritedStartPlanner();

        CharacterInheritedStartTarget corrected = AssertEx.NotNull(
            planner.Plan(inherited, alreadyAccumulated, baseline).Value);
        CharacterInheritedStartTarget repeated = AssertEx.NotNull(
            planner.Plan(inherited, corrected.State, baseline).Value);

        AssertEx.Equal(new CharacterVector3(400, -70, -1), corrected.State.Position);
        AssertEx.Equal(15f, corrected.State.LocalEulerAngles.Z);
        AssertEx.Equal(corrected.State, repeated.State);
        AssertEx.False(repeated.PositionChanged);
        AssertEx.False(repeated.RotationChanged);
    }

    public static void InheritedFlipKeepsScreenRotationSign()
    {
        CharacterTransformState official = State(0, 0, -1, 0, 0, 18);
        var baseline = new CharacterTransformBaseline(
            "scene-inherited-screen-rotation", 2, "mika", official);
        var inherited = new PreviewChainSlotState(
            2,
            PreviewChainAxisState.Absolute(200),
            PreviewChainAxisState.Clear(),
            PreviewChainAxisState.Absolute(-25),
            true,
            2)
        { FlipControlled = true };

        CharacterInheritedStartTarget target = AssertEx.NotNull(
            new CharacterInheritedStartPlanner().Plan(
                inherited,
                official,
                baseline).Value);

        AssertEx.Equal(200f, target.State.Position.X);
        AssertEx.Equal(-25f, target.State.LocalEulerAngles.Z);
        float physicalZ = CharacterScreenRotation.ToPhysicalDegrees(
            target.State.LocalEulerAngles.Z,
            target.State.LocalEulerAngles.Y,
            official.LocalEulerAngles.Z);
        AssertEx.Equal(25f, physicalZ);
        AssertEx.Equal(-25f, CharacterScreenRotation.ToScreenDegrees(
            physicalZ,
            target.State.LocalEulerAngles.Y));
    }

    public static void UncontrolledAxesKeepCurrentLiveValues()
    {
        CharacterTransformState official = State(100, -50, -1, 4, 10, 30);
        var baseline = new CharacterTransformBaseline(
            "scene-inherited-uncontrolled", 2, "mika", official);
        var inherited = new PreviewChainSlotState(
            2,
            PreviewChainAxisState.Absolute(600),
            PreviewChainAxisState.Clear(),
            PreviewChainAxisState.Absolute(-25),
            false,
            2);
        // The live character diverges from the official baseline on every
        // axis the chain never touched; none of them may be overwritten.
        CharacterTransformState live = State(55, -80, -1, 4, 40, 12);
        var planner = new CharacterInheritedStartPlanner();

        CharacterInheritedStartTarget planned = AssertEx.NotNull(
            planner.Plan(inherited, live, baseline).Value);

        AssertEx.Equal(600f, planned.State.Position.X);
        AssertEx.Equal(-80f, planned.State.Position.Y);
        AssertEx.Equal(live.Position.Z, planned.State.Position.Z);
        AssertEx.Equal(40f, planned.State.LocalEulerAngles.Y);
        AssertEx.Equal(-25f, planned.State.LocalEulerAngles.Z);
        AssertEx.True(planned.PositionChanged);
        AssertEx.True(planned.RotationChanged);
    }

    public static void FlipOnlyInheritanceKeepsOtherAxesAtLiveValues()
    {
        CharacterTransformState official = State(100, -50, -1, 4, 10, 30);
        var baseline = new CharacterTransformBaseline(
            "scene-inherited-flip-only", 2, "mika", official);
        var inherited = new PreviewChainSlotState(
            2,
            PreviewChainAxisState.Clear(),
            PreviewChainAxisState.Clear(),
            PreviewChainAxisState.Clear(),
            true,
            1)
        { FlipControlled = true };
        CharacterTransformState live = State(55, -80, -1, 4, 40, 12);
        var planner = new CharacterInheritedStartPlanner();

        CharacterInheritedStartTarget first = AssertEx.NotNull(
            planner.Plan(inherited, live, baseline).Value);
        CharacterInheritedStartTarget repeated = AssertEx.NotNull(
            planner.Plan(inherited, first.State, baseline).Value);

        AssertEx.Equal(new CharacterVector3(55f, -80f, -1f), first.State.Position);
        AssertEx.Equal(12f, first.State.LocalEulerAngles.Z);
        // Toggle semantics: the flip turns the LIVE orientation (40°) by a
        // half turn instead of heading to official.Y + 180.
        AssertEx.Equal(220f, first.State.LocalEulerAngles.Y);
        AssertEx.True(CharacterScreenRotation.IsHorizontallyFlipped(
            first.State.LocalEulerAngles.Y));
        AssertEx.False(first.PositionChanged);
        AssertEx.True(first.RotationChanged);
        AssertEx.Equal(first.State, repeated.State);
        AssertEx.False(repeated.PositionChanged);
        AssertEx.False(repeated.RotationChanged);
    }

    public static void OriginBaselineStoreClaimsProvisionalAndRecapturesBoundaries()
    {
        var store = new CharacterTransformOriginBaselineStore();
        CharacterTransformState firstOfficial = State(0, 0, -1, 0, 0, 0);
        CharacterTransformState alreadyTransformed = State(500, -20, -1, 0, 180, -12);

        CharacterTransformOriginBaseline provisional = AssertEx.NotNull(
            store.Capture(string.Empty, 4, "hoshino", 101, firstOfficial).Value);
        AssertEx.False(provisional.HasExactLineage);

        CharacterTransformOriginBaseline claimed = AssertEx.NotNull(
            store.Capture(
                "character-lineage/v1:AAAA",
                4,
                "hoshino",
                101,
                alreadyTransformed).Value);
        AssertEx.True(claimed.HasExactLineage);
        AssertEx.Equal(firstOfficial, claimed.State);

        CharacterTransformOriginBaseline retainedWithoutMetadata = AssertEx.NotNull(
            store.Capture(
                string.Empty,
                4,
                "hoshino",
                101,
                alreadyTransformed).Value);
        AssertEx.Equal(claimed, retainedWithoutMetadata);

        CharacterTransformState nextOfficial = State(100, 40, -1, 0, 0, 5);
        CharacterTransformOriginBaseline nextLineage = AssertEx.NotNull(
            store.Capture(
                "character-lineage/v1:BBBB",
                4,
                "hoshino",
                101,
                nextOfficial).Value);
        AssertEx.Equal(nextOfficial, nextLineage.State);

        CharacterTransformState replacementOfficial = State(-300, 20, -1, 0, 0, 0);
        CharacterTransformOriginBaseline replacement = AssertEx.NotNull(
            store.Capture(
                "character-lineage/v1:BBBB",
                4,
                "hoshino",
                202,
                replacementOfficial).Value);
        AssertEx.Equal(202L, replacement.ManagedCharacterInstanceId);
        AssertEx.Equal(replacementOfficial, replacement.State);
        AssertEx.False(store.Get(
            "character-lineage/v1:BBBB", 4, "hoshino", 101).Success);

        CharacterTransformState newOccupantOfficial = State(700, 0, -1, 0, 0, 0);
        CharacterTransformOriginBaseline newOccupant = AssertEx.NotNull(
            store.Capture(
                "character-lineage/v1:BBBB",
                4,
                "nagi",
                202,
                newOccupantOfficial).Value);
        AssertEx.Equal("nagi", newOccupant.OccupantIdentifier);
        AssertEx.Equal(newOccupantOfficial, newOccupant.State);
        AssertEx.False(store.Get(
            "character-lineage/v1:BBBB", 4, "hoshino", 202).Success);
    }

    public static void OriginBaselineStoreAllowsResetOnlyOfficialCapture()
    {
        CharacterTransformState official = State(-400, 80, -1, 0, 25, 8);
        CharacterTransformOriginBaseline origin = AssertEx.NotNull(
            new CharacterTransformOriginBaselineStore().Capture(
                "character-lineage/v1:RESET",
                4,
                "hoshino",
                303,
                official).Value);
        CharacterTransformCommand reset = AssertEx.NotNull(
            new CharacterTransformDirectiveParser().Parse(
                "#char;4;reset;duration=500;easing=easeInOut").Value);
        var sceneBaseline = new CharacterTransformBaseline(
            "scene-reset-only",
            origin.PublicSlot,
            origin.OccupantIdentifier,
            origin.State);

        CharacterTransformTarget target = AssertEx.NotNull(
            new CharacterTransformPlanner().Plan(
                reset,
                official,
                sceneBaseline).Value);

        AssertEx.Equal(official, target.State);
        AssertEx.True(target.IsReset);
    }

    private static CharacterTransformState State(
        float x,
        float y,
        float z,
        float rotationX,
        float rotationY,
        float rotationZ) =>
        new(
            new CharacterVector3(x, y, z),
            new CharacterVector3(rotationX, rotationY, rotationZ));
}
