using AzureArchive.VideoTools.Core.Cameras;
using AzureArchive.VideoTools.Core.Characters;
using AzureArchive.VideoTools.Core.Commands;

namespace AzureArchive.VideoTools.Tests;

internal static class SceneCameraMutationFailureTests
{
    public static void BackgroundOnlyResidualCleansOnlyBackground()
    {
        SceneCameraMutationFailureEvidence failure = FailApplication(
            SceneCameraMotionFields.BackgroundPosition | SceneCameraMotionFields.BackgroundZoom,
            "Back-scale", rollbackSucceeded: false);
        AssertEx.True(failure.HasResidualMutation);
        AssertEx.Equal(SceneCameraMotionFields.BackgroundPosition | SceneCameraMotionFields.BackgroundZoom,
            failure.AttemptedFields);
        AssertSlots(failure, SceneCameraCommandFamilyCompiler.BackgroundResourceSlot);
    }

    public static void BothAttemptedScopesRetainBothCleanupResources()
    {
        SceneCameraMutationFailureEvidence failure = FailApplication(
            SceneCameraMotionFields.OverallPosition | SceneCameraMotionFields.BackgroundZoom,
            "Spine-position", rollbackSucceeded: false);
        AssertEx.Equal(SceneCameraMotionFields.OverallPosition | SceneCameraMotionFields.BackgroundZoom,
            failure.AttemptedFields);
        AssertSlots(failure, SceneCameraCommandFamilyCompiler.SingletonResourceSlot,
            SceneCameraCommandFamilyCompiler.BackgroundResourceSlot);
    }

    public static void SuccessfulRollbackAndOldFailureWithoutEvidenceDoNotClean()
    {
        SceneCameraMutationFailureEvidence rolledBack = FailApplication(
            SceneCameraMotionFields.OverallZoom | SceneCameraMotionFields.BackgroundZoom,
            "Spine-scale", rollbackSucceeded: true);
        AssertEx.False(rolledBack.HasResidualMutation);
        AssertEx.True(rolledBack.AttemptedFields != SceneCameraMotionFields.None);
        AssertSlots(rolledBack);
        AssertSlots(default);
        AssertSlots(new(SceneCameraMotionFields.None, true));
    }

    public static void AnotherScopesActiveAndPendingWritesAreIncludedInThisAttempt()
    {
        var motion = new SceneCameraMotion();
        var parser = new SceneCameraDirectiveParser();
        var planner = new SceneCameraPlanner();
        SceneCameraCommand command = AssertEx.NotNull(parser.Parse(
            "#camera;set;zoom=2;duration=1000;easing=linear").Value);
        SceneCameraTarget target = AssertEx.NotNull(planner.Plan(command, SceneCameraState.Default,
            new SceneCameraBaseline("ancestor", SceneCameraState.Default)).Value);
        AssertEx.True(motion.Begin(command, target, 0).Success);
        SceneCameraMotionFields pending = motion.ActiveFields(0);
        SceneCameraMotion candidate = motion.Copy();
        AssertEx.True(candidate.Seed(new(SceneCameraState.Default, new(10, 20, 1.5f)),
            false, true, 0.5).Success);
        SceneCameraMotionFields inheritedFields =
            SceneCameraMotionFields.BackgroundPosition | SceneCameraMotionFields.BackgroundZoom;
        SceneCameraMotionFields actualFields = inheritedFields | pending | candidate.ActiveFields(0.5);
        SceneCameraMutationFailureEvidence failure = FailApplication(actualFields,
            "Spine-scale", rollbackSucceeded: false);
        AssertEx.True((failure.AttemptedFields & SceneCameraMotionFields.OverallZoom) != 0);
        AssertSlots(failure, SceneCameraCommandFamilyCompiler.SingletonResourceSlot,
            SceneCameraCommandFamilyCompiler.BackgroundResourceSlot);

        // The final heartbeat can be pending even when its tween is no longer active.
        AssertEx.Equal(SceneCameraMotionFields.None, candidate.ActiveFields(1.1));
        SceneCameraMutationFailureEvidence finalPendingFailure = FailApplication(
            inheritedFields | pending | candidate.ActiveFields(1.1), "Spine-scale", rollbackSucceeded: false);
        AssertSlots(finalPendingFailure, SceneCameraCommandFamilyCompiler.SingletonResourceSlot,
            SceneCameraCommandFamilyCompiler.BackgroundResourceSlot);
    }

    public static void FailureEvidenceCannotBorrowFromAnotherApplication()
    {
        SceneCameraMutationFailureEvidence previous = FailApplication(SceneCameraMotionFields.OverallZoom,
            "Spine-scale", rollbackSucceeded: false);
        AssertSlots(previous, SceneCameraCommandFamilyCompiler.SingletonResourceSlot);
        SceneCameraMutationFailureEvidence current = FailApplication(SceneCameraMotionFields.BackgroundZoom,
            "Back-position", rollbackSucceeded: false);
        AssertSlots(current, SceneCameraCommandFamilyCompiler.BackgroundResourceSlot);
        AssertEx.Equal(SceneCameraMotionFields.BackgroundZoom, current.AttemptedFields);
        AssertSlots(new SceneCameraMutationAttempt(SceneCameraMotionFields.OverallZoom).Evidence(true));
    }

    public static void UntouchedLayerFieldsAndUnknownBitsCannotInventScopeOwnership()
    {
        var attempt = new SceneCameraMutationAttempt(
            SceneCameraMotionFields.BackgroundZoom | SceneCameraMotionFields.OverallPosition);
        attempt.BeginScaleWrite(isBack: false);
        AssertSlots(attempt.Evidence(true));
        attempt.BeginPositionWrite(isBack: false);
        AssertSlots(attempt.Evidence(true), SceneCameraCommandFamilyCompiler.SingletonResourceSlot);
        AssertEx.Equal(SceneCameraMotionFields.OverallPosition, attempt.Evidence(true).AttemptedFields);
        var unknown = new SceneCameraMutationAttempt((SceneCameraMotionFields)128);
        unknown.BeginPositionWrite(isBack: true);
        AssertSlots(unknown.Evidence(true));
    }

    public static void ManagedCommitFailureUsesOnlyTheSuccessfulApplicationsFields()
    {
        var attempt = new SceneCameraMutationAttempt(SceneCameraMotionFields.BackgroundZoom);
        attempt.BeginPositionWrite(isBack: true);
        attempt.BeginScaleWrite(isBack: true);
        SceneCameraMutationFailureEvidence successfulWrite = attempt.Evidence(false);
        AssertSlots(successfulWrite);
        AssertSlots(successfulWrite with { HasResidualMutation = true },
            SceneCameraCommandFamilyCompiler.BackgroundResourceSlot);
    }

    private static SceneCameraMutationFailureEvidence FailApplication(
        SceneCameraMotionFields fields, string failedSetter, bool rollbackSucceeded)
    {
        SceneCameraPhysicalWrites writes = SceneCameraCompositionPlanner.WritesFor(fields);
        var attempt = new SceneCameraMutationAttempt(fields);
        foreach ((string Name, bool Enabled, bool Back, bool Scale) setter in new[]
        {
            ("Back-position", writes.BackPosition, true, false),
            ("Back-scale", writes.BackScale, true, true),
            ("Spine-position", writes.SpinePosition, false, false),
            ("Spine-scale", writes.SpineScale, false, true)
        })
        {
            if (!setter.Enabled) continue;
            if (setter.Scale) attempt.BeginScaleWrite(setter.Back);
            else attempt.BeginPositionWrite(setter.Back);
            if (setter.Name == failedSetter) return attempt.Evidence(!rollbackSucceeded);
        }
        throw new InvalidOperationException("The failure fixture must target a setter that actually begins.");
    }

    private static void AssertSlots(SceneCameraMutationFailureEvidence failure, params int[] expected)
    {
        IReadOnlyList<int> actual = SceneCameraMutationAttempt.CleanupResourceSlots(failure);
        AssertEx.Equal(expected.Length, actual.Count);
        for (int index = 0; index < expected.Length; index++) AssertEx.Equal(expected[index], actual[index]);
    }
}
