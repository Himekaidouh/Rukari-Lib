using AzureArchive.VideoTools.Core.Characters;
using AzureArchive.VideoTools.Core.Commands;

namespace AzureArchive.VideoTools.Tests;

internal static class PreviewChainRecoveryTests
{
    public static void LateInheritedStartReplacesDefaultReplayEntry()
    {
        var store = new CharacterTransformBaselineStore();
        CharacterTransformState official = State(0, 20, -1, 10, 0, 15);
        AssertEx.True(store.Capture("scene-b", 3, "mika", official).Success);

        // The scene already previewed from the default while its graph was
        // unavailable. The next replay resolves A and applies it before B.
        PreviewChainSlotState inherited = InheritedX(300);
        CharacterTransformState appliedA = ApplyInherited(inherited, State(500, 20, -1, 10, 0, 15), official);
        CharacterTransformBaseline entry = AssertEx.NotNull(store.SeedInheritedEntry(
            "scene-b", 3, "mika", appliedA, inherited).Value);
        CharacterTransformCommand b = Parse("#char;3;set;x=500;duration=800;easing=linear");
        var planner = new CharacterTransformPlanner();
        CharacterTransformTarget restored = AssertEx.NotNull(planner.PlanReplayRestore(b, appliedA, entry).Value);
        CharacterTransformTarget target = AssertEx.NotNull(planner.Plan(b, restored.State, entry).Value);

        AssertEx.Equal(300f, restored.State.Position.X);
        AssertEx.Equal(500f, target.State.Position.X);
        AssertEx.Equal(800, target.DurationMilliseconds);
    }

    public static void AncestorEditReseedsEntryWithoutAccumulatingRelativeReplay()
    {
        var store = new CharacterTransformBaselineStore();
        CharacterTransformState official = State(0, 0, -1, 0, 0, 0);
        PreviewChainSlotState a = InheritedX(100);
        CharacterTransformState current = ApplyInherited(a, official, official);
        CharacterTransformBaseline entry = AssertEx.NotNull(store.SeedInheritedEntry(
            "scene-b", 3, "mika", current, a).Value);
        CharacterTransformCommand b = Parse("#char;3;move;dx=200;duration=700;easing=linear");
        var planner = new CharacterTransformPlanner();
        for (int replay = 0; replay < 3; replay++)
        {
            CharacterTransformTarget start = AssertEx.NotNull(planner.PlanReplayRestore(b, current, entry).Value);
            current = AssertEx.NotNull(planner.Plan(b, start.State, entry).Value).State;
            AssertEx.Equal(100f, start.State.Position.X);
            AssertEx.Equal(300f, current.Position.X);
        }

        PreviewChainSlotState editedA = InheritedX(250);
        current = ApplyInherited(editedA, current, official);
        entry = AssertEx.NotNull(store.SeedInheritedEntry("scene-b", 3, "mika", current, editedA).Value);
        CharacterTransformTarget editedStart = AssertEx.NotNull(planner.PlanReplayRestore(b, current, entry).Value);
        CharacterTransformTarget editedTarget = AssertEx.NotNull(planner.Plan(b, editedStart.State, entry).Value);
        AssertEx.Equal(250f, editedStart.State.Position.X);
        AssertEx.Equal(450f, editedTarget.State.Position.X);
        AssertEx.Equal(250f, AssertEx.NotNull(store.Get("scene-b", 3, "mika").Value).State.Position.X);
    }

    public static void InheritedEntryPreservesUncontrolledAxesAndDepth()
    {
        var store = new CharacterTransformBaselineStore();
        CharacterTransformState entry = State(10, 20, -4, 30, 40, 50);
        AssertEx.True(store.Capture("scene-b", 3, "mika", entry).Success);
        var onlyY = new PreviewChainSlotState(3,
            PreviewChainAxisState.Clear(), PreviewChainAxisState.Absolute(300),
            PreviewChainAxisState.Clear(), false, 1);
        CharacterTransformState readBack = State(999, 300, 777, 91, 180, -99);
        CharacterTransformBaseline seeded = AssertEx.NotNull(store.SeedInheritedEntry(
            "scene-b", 3, "mika", readBack, onlyY).Value);

        AssertEx.Equal(State(10, 300, -4, 30, 40, 50), seeded.State);
    }

    public static void InheritedEntrySeedsPitchFlipAndScreenRotationIndependently()
    {
        var store = new CharacterTransformBaselineStore();
        CharacterTransformState entry = State(10, 20, -4, 30, 0, 50);
        AssertEx.True(store.Capture("scene-b", 3, "mika", entry).Success);
        var orientation = new PreviewChainSlotState(3,
            PreviewChainAxisState.Clear(), PreviewChainAxisState.Clear(),
            PreviewChainAxisState.Absolute(-25), true, 1)
        {
            RotationX = PreviewChainAxisState.Absolute(120),
            FlipControlled = true
        };
        CharacterTransformState readBack = State(999, 888, 777, 120, 180, -25);
        CharacterTransformBaseline seeded = AssertEx.NotNull(store.SeedInheritedEntry(
            "scene-b", 3, "mika", readBack, orientation).Value);
        AssertEx.Equal(State(10, 20, -4, 120, 180, -25), seeded.State);

        var flipOnly = orientation with
        {
            RotationX = PreviewChainAxisState.Clear(),
            RotationZ = PreviewChainAxisState.Clear(),
            FlippedFromOfficial = false
        };
        seeded = AssertEx.NotNull(store.SeedInheritedEntry(
            "scene-b", 3, "mika", State(5, 6, 7, 99, 360, 99), flipOnly).Value);
        AssertEx.Equal(State(10, 20, -4, 120, 360, -25), seeded.State);
    }

    public static void InvalidInheritedReadbackDoesNotReplaceEntry()
    {
        var store = new CharacterTransformBaselineStore();
        CharacterTransformState entry = State(10, 20, -1, 0, 0, 15);
        AssertEx.True(store.Capture("scene-b", 3, "mika", entry).Success);
        AssertEx.False(store.SeedInheritedEntry("scene-b", 3, "mika",
            State(float.NaN, 20, -1, 0, 0, 15), InheritedX(300)).Success);
        AssertEx.False(store.SeedInheritedEntry("scene-b", 3, "mika",
            State(300, 20, -1, 0, 0, 15), InheritedX(300) with { PublicSlot = 2 }).Success);
        AssertEx.Equal(entry, AssertEx.NotNull(store.Get("scene-b", 3, "mika").Value).State);
    }

    public static void MissingChainCanRecoverWithinOriginalPendingBudget()
    {
        AssertEx.Equal(PreviewChainPendingAction.Retry,
            PreviewChainRecoveryPolicy.DecidePending(true, false, false));
        AssertEx.Equal(PreviewChainPendingAction.Apply,
            PreviewChainRecoveryPolicy.DecidePending(true, true, false));
        AssertEx.False(PreviewChainRecoveryPolicy.HasCompleteCoverage(false, 0, 0, 0));
        AssertEx.True(PreviewChainRecoveryPolicy.HasCompleteCoverage(true, 0, 0, 0));
        AssertEx.False(PreviewChainRecoveryPolicy.HasCompleteCoverage(true, 2, 1, 1));
        AssertEx.True(PreviewChainRecoveryPolicy.HasCompleteCoverage(true, 2, 2, 0));
    }

    public static void PendingBudgetOrContextExpiryDoesNotClaimCoverage()
    {
        AssertEx.Equal(PreviewChainPendingAction.Drop,
            PreviewChainRecoveryPolicy.DecidePending(true, false, true));
        AssertEx.Equal(PreviewChainPendingAction.Drop,
            PreviewChainRecoveryPolicy.DecidePending(false, true, false));
        AssertEx.False(PreviewChainRecoveryPolicy.HasCompleteCoverage(false, 0, 0, 0));
        AssertEx.False(PreviewChainRecoveryPolicy.HasCompleteCoverage(true, 1, 0, 1));
        AssertEx.False(PreviewChainRecoveryPolicy.HasCompleteCoverage(true, -1, -1, 0));
    }

    public static void CommandsStartedPreventLateChainOnlyReset()
    {
        // Even a ready chain must not move A over B after the current scene
        // has claimed its commands; the next replay owns rebuilding A then B.
        AssertEx.Equal(PreviewChainPendingAction.Drop,
            PreviewChainRecoveryPolicy.DecidePending(true, true, false, currentCommandsReserved: true));
        AssertEx.Equal(PreviewChainPendingAction.Drop,
            PreviewChainRecoveryPolicy.DecidePending(true, false, false, currentCommandsReserved: true));
        AssertEx.Equal(PreviewChainPendingAction.Apply,
            PreviewChainRecoveryPolicy.DecidePending(true, true, false, currentCommandsReserved: false));
    }

    private static PreviewChainSlotState InheritedX(float x) => new(3,
        PreviewChainAxisState.Absolute(x), PreviewChainAxisState.Clear(),
        PreviewChainAxisState.Clear(), false, 1);

    private static CharacterTransformState ApplyInherited(
        PreviewChainSlotState inherited,
        CharacterTransformState current,
        CharacterTransformState official) => AssertEx.NotNull(new CharacterInheritedStartPlanner().Plan(
            inherited, current, new CharacterTransformBaseline("official", 3, "mika", official)).Value).State;

    private static CharacterTransformCommand Parse(string text) =>
        AssertEx.NotNull(new CharacterTransformDirectiveParser().Parse(text).Value);

    private static CharacterTransformState State(float x, float y, float z, float pitch, float yaw, float screenZ) =>
        new(new CharacterVector3(x, y, z), new CharacterVector3(pitch, yaw, screenZ));
}
