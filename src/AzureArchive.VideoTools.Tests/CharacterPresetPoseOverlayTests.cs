using AzureArchive.VideoTools.Core.Characters;

namespace AzureArchive.VideoTools.Tests;

internal static class CharacterPresetPoseOverlayTests
{
    public static void RelativeOverlayPreservesAuthoredPoseAndFlip()
    {
        var original = new CharacterPresetPose(5f, 180f, 23f, -4f, 2f, 7f);
        var applied = CharacterPresetPoseOverlay.Apply(original, new CharacterPresetFrame(10f, .5f, 2f, false));
        AssertEx.Equal(new CharacterPresetPose(5f, 180f, 13f, -2f, 4f, 7f), applied);
        AssertEx.Equal(original, CharacterPresetPoseOverlay.RemoveOwned(applied, original, applied, true, true));
    }

    public static void RepeatedFramesRestoreWithoutAccumulation()
    {
        var original = new CharacterPresetPose(0f, 0f, 37f, -3f, 6f, 2f);
        CharacterPresetPose current = original;
        for (int repeat = 0; repeat < 2000; repeat++)
        {
            var frame = new CharacterPresetFrame(repeat * 29f, 1f / 1.2f, 1.2f, false);
            var applied = CharacterPresetPoseOverlay.Apply(current, frame);
            current = CharacterPresetPoseOverlay.RemoveOwned(applied, current, applied, true, true);
            AssertEx.Equal(original, current);
        }
        AssertEx.Equal(original, CharacterPresetPoseOverlay.Apply(current, CharacterPresetFrame.Neutral(true)));
    }

    public static void ExternalAxisWritesBecomeNextBaseline()
    {
        var original = new CharacterPresetPose(0f, 180f, 30f, -3f, 6f, 2f);
        var applied = CharacterPresetPoseOverlay.Apply(original, new CharacterPresetFrame(20f, .5f, 2f, false));
        // An ordinary char tween replaces Z while an unrelated scale writer replaces only X/Z.
        var external = applied with { EulerX = 8f, EulerY = 0f, EulerZ = 77f, ScaleX = -9f, ScaleZ = 5f };
        var expected = external with { ScaleY = original.ScaleY };
        var cleaned = CharacterPresetPoseOverlay.RemoveOwned(external, original, applied, true, true);
        AssertEx.Equal(expected, cleaned);
        var next = CharacterPresetPoseOverlay.Apply(cleaned, new CharacterPresetFrame(10f, 2f, .5f, false));
        AssertEx.Equal(87f, next.EulerZ);
        AssertEx.Equal(-18f, next.ScaleX);
        AssertEx.Equal(3f, next.ScaleY);
        AssertEx.Equal(5f, next.ScaleZ);
    }

    public static void RotationOwnershipHandlesUnityAngleWrapping()
    {
        var original = new CharacterPresetPose(0f, 0f, 15f, 1f, 1f, 1f);
        var applied = CharacterPresetPoseOverlay.Apply(original, new CharacterPresetFrame(720f, 1f, 1f, false));
        var native = applied with { EulerZ = 15f };
        AssertEx.Equal(original, CharacterPresetPoseOverlay.RemoveOwned(native, original, applied, true, false));
        var changed = native with { EulerZ = 20f };
        AssertEx.Equal(changed, CharacterPresetPoseOverlay.RemoveOwned(changed, original, applied, true, false));
    }

    public static void ScaleOwnershipPreservesTinyAndZeroAuthoredValues()
    {
        var original = new CharacterPresetPose(0f, 0f, 0f, -1e-12f, 0f, 1f);
        var applied = CharacterPresetPoseOverlay.Apply(original, new CharacterPresetFrame(0f, 2f, .5f, false));
        var changed = applied with { ScaleX = -3e-12f };
        AssertEx.Equal(changed, CharacterPresetPoseOverlay.RemoveOwned(changed, original, applied, false, true));
        AssertEx.Equal(original, CharacterPresetPoseOverlay.RemoveOwned(applied, original, applied, false, true));
    }

    public static void NextDialogueWatermarkRejectsDelayedOlderPresets()
    {
        var windows = new CharacterPresetWindowGate();
        AssertEx.False(windows.MayStart(0));
        AssertEx.True(windows.ObserveBoundary(10));
        AssertEx.True(windows.MayStart(10));
        // An empty row still advances the boundary: a previously deferred start cannot revive it.
        AssertEx.True(windows.ObserveBoundary(11));
        AssertEx.False(windows.MayStart(10));
        AssertEx.False(windows.ObserveBoundary(10));
        AssertEx.False(windows.ObserveBoundary(11));
        AssertEx.True(windows.MayStart(11));
        AssertEx.True(windows.ObserveBoundary(12));
        AssertEx.True(windows.MayStart(12));
    }

    public static void SameRowWithoutMessagesDoesNotEndDialogue()
    {
        var windows = new CharacterPresetWindowGate();
        AssertEx.True(windows.ObserveBoundary(10));
        AssertEx.False(windows.ObservePlaybackBoundary(11, 3, 3, 0));
        AssertEx.True(windows.MayStart(10));
        // Same-row real replay is authorized by its batch and still restarts.
        AssertEx.True(windows.ObserveBoundary(12));
        AssertEx.False(windows.MayStart(10));
    }

    public static void EmptyPlaybackRowAdvanceEndsDialogue()
    {
        var windows = new CharacterPresetWindowGate();
        AssertEx.True(windows.ObserveBoundary(10));
        AssertEx.True(windows.ObservePlaybackBoundary(11, 3, 4, 0));
        AssertEx.False(windows.MayStart(10));
        AssertEx.True(windows.MayStart(11));
        // Unknown rows fail closed for the old effect; positive identity
        // messages prove a boundary even if the engine reports the same row.
        AssertEx.True(windows.ObservePlaybackBoundary(12, -1, -1, 0));
        AssertEx.True(windows.ObservePlaybackBoundary(13, 4, 4, 1));
    }

    public static void SnapshotProjectionExcludesOwnedRotationWithoutWritingPose()
    {
        var original = new CharacterPresetPose(5f, 180f, 30f, -3f, 6f, 2f);
        var applied = CharacterPresetPoseOverlay.Apply(original, new CharacterPresetFrame(20f, 1f, 1f, false));
        var displayed = new CharacterTransformState(new CharacterVector3(700f, -250f, 4f),
            new CharacterVector3(applied.EulerX, applied.EulerY,
                CharacterScreenRotation.ToScreenDegrees(applied.EulerZ, applied.EulerY)));
        var logical = CharacterPresetPoseOverlay.ProjectAuthoredState(displayed, original, applied, true);
        AssertEx.Equal(-10f, displayed.LocalEulerAngles.Z);
        AssertEx.Equal(-30f, logical.LocalEulerAngles.Z);
        AssertEx.Equal(displayed.Position, logical.Position);
        AssertEx.Equal(displayed.LocalEulerAngles.X, logical.LocalEulerAngles.X);
        AssertEx.Equal(displayed.LocalEulerAngles.Y, logical.LocalEulerAngles.Y);
        var tweened = displayed with { LocalEulerAngles = displayed.LocalEulerAngles with { Z = -42f } };
        AssertEx.Equal(tweened, CharacterPresetPoseOverlay.ProjectAuthoredState(tweened, original, applied, true));
    }

    public static void TiltCleanupPreservesExternalPitchAndYawWhileRemovingOwnedRoll()
    {
        foreach (float startingYaw in new[] { 0f, 180f })
        {
            var original = new CharacterPresetPose(5f, startingYaw, 23f, -3f, 6f, 2f);
            var applied = CharacterPresetPoseOverlay.Apply(original,
                new CharacterPresetFrame(10f, 1f, 1f, false));
            CharacterPresetPose[] externalWrites =
            {
                applied with { EulerX = 41f },
                applied with { EulerY = 180f - startingYaw },
                applied with { EulerX = 41f, EulerY = 180f - startingYaw }
            };
            foreach (CharacterPresetPose external in externalWrites)
            {
                CharacterPresetPose expected = external with { EulerZ = original.EulerZ };
                // The compatibility overload must keep Z-only tilt ownership even
                // when another writer changes pitch, facing, or both between frames.
                AssertEx.Equal(expected,
                    CharacterPresetPoseOverlay.RemoveOwned(external, original, applied, true, false));
                AssertEx.Equal(expected,
                    CharacterPresetPoseOverlay.RemoveOwned(external, original, applied, true, false, ownsYaw: false));
                CharacterTransformState displayed = DisplayedState(external);
                AssertEx.Equal(DisplayedState(expected),
                    CharacterPresetPoseOverlay.ProjectAuthoredState(displayed, original, applied, true));
                AssertEx.Equal(DisplayedState(expected),
                    CharacterPresetPoseOverlay.ProjectAuthoredState(displayed, original, applied, true, ownsYaw: false));
            }
        }
    }
    public static void YawOverlayPreservesAuthoredTiltFlipAndScaleThroughoutTurns()
    {
        foreach (float startingYaw in new[] { 0f, 180f })
        {
            var original = new CharacterPresetPose(13f, startingYaw, 27f, -3f, 6f, 2f);
            foreach (float yaw in new[] { -720f, -270f, -180f, -90f, 90f, 180f, 270f, 720f })
            {
                var frame = new CharacterPresetFrame(0f, 1f, 1f, false) { YawDegrees = yaw };
                CharacterPresetPose applied = CharacterPresetPoseOverlay.Apply(original, frame);
                AssertEx.Equal(original.EulerX, applied.EulerX);
                AssertEx.Equal(original.EulerY + yaw, applied.EulerY);
                AssertEx.Equal(original.EulerZ, applied.EulerZ);
                AssertEx.Equal(original.ScaleX, applied.ScaleX);
                AssertEx.Equal(original.ScaleY, applied.ScaleY);
                AssertEx.Equal(original.ScaleZ, applied.ScaleZ);
                AssertEx.Equal(original, CharacterPresetPoseOverlay.RemoveOwned(applied, original, applied, true, false, ownsYaw: true));
            }
            AssertEx.Equal(original, CharacterPresetPoseOverlay.Apply(original, CharacterPresetFrame.Neutral(true)));
        }
    }

    public static void YawOwnershipAcceptsEquivalentUnityEulerRepresentations()
    {
        // Unity may report another ZXY Euler representation of precisely the
        // same orientation, especially around a half-turn and the pitch poles.
        foreach (float pitch in new[] { -90f, -25f, 0f, 25f, 90f })
        {
            var original = new CharacterPresetPose(pitch, 180f, 23f, 1f, 1f, 1f);
            var applied = CharacterPresetPoseOverlay.Apply(original,
                new CharacterPresetFrame(0f, 1f, 1f, false) { YawDegrees = 135f });
            CharacterPresetPose[] equivalents =
            {
                applied with { EulerX = applied.EulerX + 360f, EulerY = applied.EulerY - 720f, EulerZ = applied.EulerZ + 360f },
                applied with { EulerX = 180f - applied.EulerX, EulerY = applied.EulerY + 180f, EulerZ = applied.EulerZ + 180f }
            };
            foreach (CharacterPresetPose native in equivalents)
            {
                AssertEx.True(CharacterPresetPoseOverlay.SameRotation(applied, native));
                AssertEx.Equal(original, CharacterPresetPoseOverlay.RemoveOwned(native, original, applied, true, false, ownsYaw: true));
            }
            CharacterPresetPose externallyTurned = applied with { EulerY = applied.EulerY + .1f };
            AssertEx.False(CharacterPresetPoseOverlay.SameRotation(applied, externallyTurned));
            AssertEx.Equal(externallyTurned,
                CharacterPresetPoseOverlay.RemoveOwned(externallyTurned, original, applied, true, false, ownsYaw: true));
        }
    }

    public static void RepeatedYawFramesAndCompletionRestoreTheSameAuthoredPose()
    {
        var command = AssertEx.NotNull(new CharacterPresetDirectiveParser()
            .Parse("#fx;3;spin;frequency=2;cycles=3;direction=right").Value);
        var original = new CharacterPresetPose(7f, 180f, 31f, -3f, 6f, 2f);
        CharacterPresetPose logical = original;
        for (int index = 0; index <= 1500; index++)
        {
            CharacterPresetFrame frame = CharacterPresetEvaluator.Sample(command, index / 1000d);
            CharacterPresetPose applied = CharacterPresetPoseOverlay.Apply(logical, frame);
            // Model the engine folding complete turns before the next managed frame.
            CharacterPresetPose native = applied with { EulerY = applied.EulerY % 360f };
            logical = CharacterPresetPoseOverlay.RemoveOwned(native, logical, applied, true, false, ownsYaw: true);
            AssertEx.Equal(original, logical);
            AssertEx.Equal(original.EulerZ, applied.EulerZ);
            AssertEx.Equal(original.EulerX, applied.EulerX);
        }
        CharacterPresetFrame completed = CharacterPresetEvaluator.Sample(command, 1.5);
        AssertEx.True(completed.Completed);
        AssertEx.Equal(0f, completed.YawDegrees);
        AssertEx.Equal(original, CharacterPresetPoseOverlay.Apply(logical, completed));
    }

    public static void ExternalRotationDuringYawIsKeptAsACompleteNewBaseline()
    {
        var original = new CharacterPresetPose(12f, 180f, 23f, -3f, 6f, 2f);
        var applied = CharacterPresetPoseOverlay.Apply(original,
            new CharacterPresetFrame(0f, 1f, 1f, false) { YawDegrees = 90f });
        CharacterPresetPose[] externalWrites =
        {
            applied with { EulerX = 33f },
            applied with { EulerY = 77f },
            applied with { EulerZ = 55f },
            applied with { EulerY = applied.EulerY + 180f },
            applied with { EulerX = 33f, EulerY = 77f, EulerZ = 55f, ScaleX = -9f, ScaleZ = 5f }
        };
        foreach (CharacterPresetPose external in externalWrites)
        {
            CharacterPresetPose clean = CharacterPresetPoseOverlay.RemoveOwned(external, original, applied, true, false, ownsYaw: true);
            AssertEx.Equal(external, clean, "An external rotation owns the whole orientation; cleanup must not restore only its Y or Z.");
            CharacterPresetPose next = CharacterPresetPoseOverlay.Apply(clean,
                new CharacterPresetFrame(0f, 1f, 1f, false) { YawDegrees = 30f });
            AssertEx.Equal(external with { EulerY = external.EulerY + 30f }, next);
            CharacterTransformState displayed = DisplayedState(external);
            AssertEx.Equal(displayed, CharacterPresetPoseOverlay.ProjectAuthoredState(displayed, original, applied, true, ownsYaw: true));
        }
        AssertEx.Equal(applied, CharacterPresetPoseOverlay.RemoveOwned(applied, original, applied, false, false, ownsYaw: true));
    }

    public static void YawSnapshotsExcludeTemporaryTurnAndRestoreTheAuthoredScreenRotation()
    {
        foreach (float originalYaw in new[] { 0f, 180f })
        {
            var original = new CharacterPresetPose(5f, originalYaw, 30f, -3f, 6f, 2f);
            var applied = CharacterPresetPoseOverlay.Apply(original,
                new CharacterPresetFrame(0f, 1f, 1f, false) { YawDegrees = 180f });
            var equivalentNative = applied with
            {
                EulerX = 180f - applied.EulerX,
                EulerY = applied.EulerY + 180f,
                EulerZ = applied.EulerZ + 180f
            };
            foreach (CharacterPresetPose native in new[] { applied, equivalentNative })
            {
                CharacterTransformState displayed = DisplayedState(native);
                CharacterTransformState logical = CharacterPresetPoseOverlay.ProjectAuthoredState(displayed, original, applied, true, ownsYaw: true);
                AssertEx.Equal(DisplayedState(original), logical);
                AssertEx.Equal(DisplayedState(native), displayed, "Snapshot projection must not alter the displayed pose.");
                AssertEx.Equal(displayed, CharacterPresetPoseOverlay.ProjectAuthoredState(displayed, original, applied, false, ownsYaw: true));
            }
        }
    }

    private static CharacterTransformState DisplayedState(CharacterPresetPose pose) => new(
        new CharacterVector3(700f, -250f, 4f),
        new CharacterVector3(pose.EulerX, pose.EulerY,
            CharacterScreenRotation.ToScreenDegrees(pose.EulerZ, pose.EulerY)));
}
