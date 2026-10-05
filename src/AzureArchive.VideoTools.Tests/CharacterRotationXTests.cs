using AzureArchive.VideoTools.Core.Characters;
using AzureArchive.VideoTools.Core.Commands;

namespace AzureArchive.VideoTools.Tests;

internal static class CharacterRotationXTests
{
    public static void OriginalConstructorsAndZOnlyCommandsRemainCompatible()
    {
        var original = new CharacterTransformCommand(3, CharacterTransformOperation.Set,
            null, null, null, null, 15f, null, null, 0, CharacterTransformEasing.Linear);
        AssertEx.Equal<float?>(null, original.RotationXDegrees);
        AssertEx.Equal<float?>(null, original.DeltaRotationXDegrees);
        AssertEx.True(typeof(CharacterTransformCommand).GetConstructors().Any(ctor =>
            ctor.GetParameters().Length == 11 && ctor.GetParameters().All(parameter => !parameter.IsOptional)));
        var oldChain = new PreviewChainSlotState(3, default, default, default, false, 1);
        AssertEx.False(oldChain.RotationX.IsControlled);
        AssertEx.True(typeof(PreviewChainSlotState).GetConstructors().Any(ctor => ctor.GetParameters().Length == 6));

        CharacterTransformCommand old = Parse("#char;3;set;rotation=15");
        AssertEx.Equal<float?>(null, old.RotationXDegrees);
        CharacterTransformState live = State(127f, 180f, 30f);
        CharacterTransformTarget target = Plan(old, live);
        AssertEx.Equal(127f, target.State.LocalEulerAngles.X);
        AssertEx.Equal(180f, target.State.LocalEulerAngles.Y);
        AssertEx.Equal(15f, target.State.LocalEulerAngles.Z);
        AssertEx.Equal("#char;3;set;rotation=15;duration=0;easing=linear",
            AssertEx.NotNull(new CharacterTransformCommandFamilyCompiler().Canonicalize("#char;3;set;rotation=15").Value).Directive);
    }

    public static void ParserAndCanonicalCompilerPreserveBothRotationAxes()
    {
        var compiler = new CharacterTransformCommandFamilyCompiler();
        var canonical = compiler.Canonicalize("#CHAR;3;SET;ROTATIONX=120.5;rotation=-15;duration=600;easing=easeOut");
        AssertEx.True(canonical.Success, canonical.Error);
        string text = AssertEx.NotNull(canonical.Value).Directive;
        AssertEx.Equal("#char;3;set;rotation=-15;rotationX=120.5;duration=600;easing=easeOut", text);
        AssertEx.Equal(text, AssertEx.NotNull(compiler.Canonicalize(text).Value).Directive);
        CharacterTransformCommand set = Parse(text);
        AssertEx.Equal<float?>(120.5f, set.RotationXDegrees);
        AssertEx.Equal<float?>(-15f, set.RotationDegrees);
        CharacterTransformCommand move = Parse("#char;3;move;drotationX=-20;drotation=30");
        AssertEx.Equal<float?>(-20f, move.DeltaRotationXDegrees);
        AssertEx.Equal<float?>(30f, move.DeltaRotationDegrees);
    }

    public static void ParserAndValidatorRejectUnsafeOrMixedXAxisFields()
    {
        var parser = new CharacterTransformDirectiveParser();
        foreach (string directive in new[]
        {
            "#char;3;set;rotationX=NaN", "#char;3;set;rotationX=Infinity",
            "#char;3;set;rotationX=3601", "#char;3;move;drotationX=-3601",
            "#char;3;set;rotationX=10;RotationX=20", "#char;3;set;drotationX=20",
            "#char;3;move;rotationX=20", "#char;3;reset;rotationX=0"
        }) AssertEx.False(parser.Parse(directive).Success, directive);

        CharacterTransformCommand set = Parse("#char;3;set;rotationX=3600");
        AssertEx.True(CharacterTransformCommandValidator.Validate(set).Success);
        AssertEx.False(CharacterTransformCommandValidator.Validate(set with { DeltaRotationXDegrees = 1 }).Success);
        AssertEx.False(CharacterTransformCommandValidator.Validate(set with { RotationXDegrees = float.NaN }).Success);
        CharacterTransformCommand move = Parse("#char;3;move;drotationX=-3600");
        AssertEx.True(CharacterTransformCommandValidator.Validate(move).Success);
        AssertEx.False(CharacterTransformCommandValidator.Validate(move with { RotationXDegrees = 1 }).Success);
        AssertEx.False(CharacterTransformCommandValidator.Validate(Parse("#char;3;reset") with { DeltaRotationXDegrees = 0 }).Success);
    }

    public static void PitchUsesShortestPathOrExplicitMultiTurnAndPreservesOtherAxes()
    {
        CharacterTransformState live = State(350f, 180f, -25f);
        CharacterTransformTarget ordinary = Plan(Parse("#char;3;set;rotationX=10;duration=500"), live);
        AssertEx.Equal(new CharacterVector3(370f, 180f, -25f), ordinary.State.LocalEulerAngles);
        AssertEx.False(ordinary.PositionChanged);
        AssertEx.True(ordinary.RotationChanged);
        AssertEx.Equal(500, ordinary.DurationMilliseconds);
        CharacterTransformTarget multi = Plan(Parse("#char;3;set;rotationX=720;duration=1000"), live);
        AssertEx.Equal(new CharacterVector3(720f, 180f, -25f), multi.State.LocalEulerAngles);
        CharacterTransformTarget relative = Plan(Parse("#char;3;move;drotationX=720"), live);
        AssertEx.Equal(1070f, relative.State.LocalEulerAngles.X);
        AssertEx.Equal(180f, relative.State.LocalEulerAngles.Y);
        AssertEx.Equal(-25f, relative.State.LocalEulerAngles.Z);
    }

    public static void PitchIsIndependentOfHorizontalFlipAndScreenTilt()
    {
        CharacterTransformState official = State(5f, 0f, 15f);
        var baseline = new CharacterTransformBaseline("scene", 3, "mika", official);
        var planner = new CharacterTransformPlanner();
        CharacterTransformTarget set = AssertEx.NotNull(planner.Plan(
            Parse("#char;3;set;rotationX=120;rotation=30;flipX=true"), official, baseline).Value);
        AssertEx.Equal(120f, set.State.LocalEulerAngles.X);
        AssertEx.True(CharacterScreenRotation.IsHorizontallyFlipped(set.State.LocalEulerAngles.Y));
        AssertEx.Equal(30f, set.State.LocalEulerAngles.Z);
        CharacterTransformTarget move = AssertEx.NotNull(planner.Plan(
            Parse("#char;3;move;drotationX=20;drotation=10"), set.State, baseline).Value);
        AssertEx.Equal(140f, move.State.LocalEulerAngles.X);
        AssertEx.Equal(40f, move.State.LocalEulerAngles.Z);
        AssertEx.Equal(set.State.LocalEulerAngles.Y, move.State.LocalEulerAngles.Y);
    }

    public static void ReplayRestoresOnlyPitchAndRepeatedReplayDoesNotAccumulate()
    {
        CharacterTransformState official = State(4f, 0f, 15f);
        var baseline = new CharacterTransformBaseline("scene", 3, "mika", official);
        var planner = new CharacterTransformPlanner();
        CharacterTransformCommand command = Parse("#char;3;move;drotationX=120;duration=600");
        CharacterTransformTarget first = AssertEx.NotNull(planner.Plan(command, official, baseline).Value);
        var representations = new CharacterRotationRepresentationStore();
        representations.Remember(3, 11, 12, "mika", new CharacterVector3(124f, 0f, 15f));
        CharacterVector3 observed = representations.Observe(3, 11, 12, "mika", new CharacterVector3(56f, 180f, 195f));
        Near(124f, observed.X);
        Near(0f, observed.Y);
        Near(15f, observed.Z);
        var beforeReplay = official with { Position = new CharacterVector3(77f, 88f, -2f), LocalEulerAngles = observed };
        for (int attempt = 0; attempt < 3; attempt++)
        {
            CharacterTransformTarget restore = AssertEx.NotNull(planner.PlanReplayRestore(command, beforeReplay, baseline).Value);
            Near(4f, restore.State.LocalEulerAngles.X);
            AssertEx.Equal(beforeReplay.LocalEulerAngles.Y, restore.State.LocalEulerAngles.Y);
            AssertEx.Equal(beforeReplay.LocalEulerAngles.Z, restore.State.LocalEulerAngles.Z);
            AssertEx.Equal(beforeReplay.Position, restore.State.Position);
            AssertEx.False(restore.PositionChanged);
            CharacterTransformTarget replayed = AssertEx.NotNull(planner.Plan(command, restore.State, baseline).Value);
            Near(first.State.LocalEulerAngles.X, replayed.State.LocalEulerAngles.X);
            beforeReplay = replayed.State;
        }
    }

    public static void ResetRestoresOfficialPitchWithoutAddingATurn()
    {
        CharacterTransformState official = State(5f, 10f, 15f);
        CharacterTransformTarget reset = Plan(Parse("#char;3;reset;duration=400"), State(725f, 190f, 45f), official);
        AssertEx.Equal(725f, reset.State.LocalEulerAngles.X);
        AssertEx.True(reset.IsReset);
        AssertEx.True(reset.RotationChanged);
        AssertEx.Equal(400, reset.DurationMilliseconds);
        var origin = new CharacterTransformOriginBaselineStore();
        AssertEx.NotNull(origin.Capture("lineage", 3, "mika", 11, official).Value);
        AssertEx.Equal(official, AssertEx.NotNull(origin.Capture("lineage", 3, "mika", 11, reset.State).Value).State);
    }

    public static void InheritedPitchUsesOfficialOriginAndKeepsUncontrolledAxes()
    {
        CharacterTransformState official = State(4f, 10f, 15f);
        CharacterTransformState live = State(200f, 55f, -25f);
        var baseline = new CharacterTransformBaseline("scene", 3, "mika", official);
        var inherited = new PreviewChainSlotState(3, default, default, default, false, 2)
        { RotationX = PreviewChainAxisState.Relative(20f) };
        var planner = new CharacterInheritedStartPlanner();
        CharacterInheritedStartTarget first = AssertEx.NotNull(planner.Plan(inherited, live, baseline).Value);
        AssertEx.Equal(new CharacterVector3(24f, 55f, -25f), first.State.LocalEulerAngles);
        AssertEx.Equal(live.Position, first.State.Position);
        AssertEx.True(first.RotationChanged);
        CharacterInheritedStartTarget again = AssertEx.NotNull(planner.Plan(inherited, first.State, baseline).Value);
        AssertEx.Equal(first.State, again.State);
        AssertEx.False(again.RotationChanged);
        CharacterInheritedStartTarget old = AssertEx.NotNull(planner.Plan(inherited with { RotationX = default }, live, baseline).Value);
        AssertEx.Equal(live, old.State);
        AssertEx.False(planner.Plan(inherited with { RotationX = PreviewChainAxisState.Absolute(float.PositiveInfinity) }, live, baseline).Success);
    }

    public static void SlotPendingPreservesPitchInCanonicalStorageAndEntryTarget()
    {
        var compiler = new SlotPendingCommandFamilyCompiler();
        string text = AssertEx.NotNull(compiler.Canonicalize("#charp;3;set;rotationX=120;rotation=15").Value).Directive;
        AssertEx.Equal("#charp;3;set;rotation=15;rotationX=120", text);
        AssertEx.Equal(text, AssertEx.NotNull(compiler.Canonicalize(text).Value).Directive);
        CharacterTransformCommand command = AssertEx.NotNull(compiler.ParseCanonical(text).Value);
        var pending = new SlotPendingStore();
        AssertEx.True(pending.Store("empty-slot-scene", command, 10).Success);
        AssertEx.True(pending.TryConsume(3, out SlotPendingEntry entryProof));
        AssertEx.Equal<float?>(120f, entryProof.Command.RotationXDegrees);
        AssertEx.False(pending.TryConsume(3, out _));
        CharacterTransformState entry = State(10f, 180f, 0f);
        CharacterTransformTarget target = AssertEx.NotNull(SlotPendingTargetPlanner.Compute(entryProof.Command, entry).Value);
        AssertEx.Equal(new CharacterVector3(120f, 180f, 15f), target.State.LocalEulerAngles);
        AssertEx.True(target.RotationChanged);
        AssertEx.Equal(0, target.DurationMilliseconds);
        CharacterTransformCommand move = AssertEx.NotNull(compiler.ParseCanonical(
            AssertEx.NotNull(compiler.Canonicalize("#charp;3;move;drotationX=20").Value).Directive).Value);
        AssertEx.Equal(30f, AssertEx.NotNull(SlotPendingTargetPlanner.Compute(move, entry).Value).State.LocalEulerAngles.X);
        AssertEx.Equal(720f, AssertEx.NotNull(SlotPendingTargetPlanner.Compute(
            Parse("#char;3;move;drotation=20"), State(720f, 0f, 0f)).Value).State.LocalEulerAngles.X);
        AssertEx.False(compiler.Canonicalize("#charp;3;set;rotationX=10;duration=500").Success);
    }

    public static void EquivalentEulerReadbackPreservesPitchAcrossPolesAndFullTurns()
    {
        foreach (var pair in new[]
        {
            (new CharacterVector3(60f, 180f, 180f), new CharacterVector3(120f, 0f, 0f)),
            (new CharacterVector3(60f, 210f, 190f), new CharacterVector3(120f, 30f, 10f)),
            (new CharacterVector3(90f, 20f, 0f), new CharacterVector3(90f, 30f, 10f)),
            (new CharacterVector3(270f, 40f, 0f), new CharacterVector3(-90f, 30f, 10f)),
            (new CharacterVector3(0f, 0f, 0f), new CharacterVector3(720f, 0f, 0f))
        })
        {
            CharacterVector3 result = CharacterEulerRepresentation.NearestEquivalent(pair.Item1, pair.Item2);
            AssertEx.Equal(pair.Item2, result);
            SameOrientation(pair.Item1, result);
        }
        CharacterVector3 crossing = CharacterEulerRepresentation.NearestEquivalent(
            new CharacterVector3(60f, 210f, 190f), new CharacterVector3(100f, 30f, 10f));
        AssertEx.Equal(new CharacterVector3(120f, 30f, 10f), crossing);
        CharacterVector3 pole = CharacterEulerRepresentation.NearestEquivalent(
            new CharacterVector3(90f, 20f, 0f), new CharacterVector3(120f, 30f, 10f));
        AssertEx.Equal(new CharacterVector3(90f, 30f, 10f), pole);
    }

    public static void RepresentationHintsNeverReplaceAnExternalMeasuredOrientation()
    {
        var store = new CharacterRotationRepresentationStore();
        var previous = new CharacterVector3(120f, 30f, 10f);
        store.Remember(3, 11, 12, "mika", previous);
        var external = new CharacterVector3(20f, 45f, -35f);
        CharacterVector3 result = store.Observe(3, 11, 12, "mika", external);
        SameOrientation(external, result);
        AssertEx.False(CharacterPresetPoseOverlay.SameRotation(Pose(previous), Pose(result)));
        AssertEx.Equal(external, result);
        var invalid = new CharacterVector3(float.NaN, 0f, 0f);
        AssertEx.True(float.IsNaN(CharacterEulerRepresentation.NearestEquivalent(invalid, previous).X));
    }

    public static void LargeOrInvalidReferencesCannotChangeMeasuredOrientation()
    {
        var measured = new CharacterVector3(23f, 47f, 61f);
        foreach (CharacterVector3 reference in new[]
        {
            new CharacterVector3(float.MaxValue, float.MaxValue, float.MaxValue),
            new CharacterVector3(-float.MaxValue, 1000000000f, -1000000000f),
            new CharacterVector3(float.NaN, 0f, 0f),
            new CharacterVector3(0f, float.PositiveInfinity, 0f)
        })
        {
            CharacterVector3 result = CharacterEulerRepresentation.NearestEquivalent(measured, reference);
            AssertEx.True(result.IsFinite);
            SameOrientation(measured, result);
        }
    }

    public static void RepresentationHintsAreIsolatedByCharacterRootOccupantAndClear()
    {
        var reference = new CharacterVector3(120f, 0f, 0f);
        var measured = new CharacterVector3(60f, 180f, 180f);
        foreach (var identity in new[] { (21L, 12L, "mika"), (11L, 22L, "mika"), (11L, 12L, "nagi") })
        {
            var store = new CharacterRotationRepresentationStore();
            store.Remember(3, 11, 12, "mika", reference);
            AssertEx.Equal(measured, store.Observe(3, identity.Item1, identity.Item2, identity.Item3, measured));
        }
        var clear = new CharacterRotationRepresentationStore();
        clear.Remember(3, 11, 12, "mika", reference);
        clear.Remove(3);
        AssertEx.Equal(measured, clear.Observe(3, 11, 12, "mika", measured));
        clear.Remember(3, 11, 12, "mika", reference);
        clear.Clear();
        AssertEx.Equal(measured, clear.Observe(3, 11, 12, "mika", measured));
        clear.Remember(3, 11, 12, "mika", reference);
        clear.Observe(3, 0, 12, "mika", measured);
        AssertEx.Equal(measured, clear.Observe(3, 11, 12, "mika", measured));
    }

    private static CharacterTransformCommand Parse(string text)
    {
        var result = new CharacterTransformDirectiveParser().Parse(text);
        AssertEx.True(result.Success, result.Error);
        return AssertEx.NotNull(result.Value);
    }

    private static CharacterTransformTarget Plan(CharacterTransformCommand command, CharacterTransformState current,
        CharacterTransformState? official = null) => AssertEx.NotNull(new CharacterTransformPlanner().Plan(
            command, current, new CharacterTransformBaseline("scene", 3, "mika", official ?? current)).Value);

    private static CharacterTransformState State(float pitch, float yaw, float tilt) =>
        new(new CharacterVector3(25f, 50f, -1f), new CharacterVector3(pitch, yaw, tilt));

    private static CharacterPresetPose Pose(CharacterVector3 value) => new(value.X, value.Y, value.Z, 1f, 1f, 1f);

    private static void SameOrientation(CharacterVector3 measured, CharacterVector3 result) =>
        AssertEx.True(CharacterPresetPoseOverlay.SameRotation(Pose(measured), Pose(result)), "A representation hint changed the measured orientation.");

    private static void Near(float expected, float actual) => AssertEx.True(MathF.Abs(expected - actual) < .001f,
        $"Expected {expected} but found {actual}.");
}
