using AzureArchive.VideoTools.Core.Characters;
using AzureArchive.VideoTools.Core.Commands;

namespace AzureArchive.VideoTools.Tests;

internal static class CharacterPresetSpinAxisTests
{
    public static void DefaultYRetainsConstructorsOverloadsAndCanonicalText()
    {
        var legacy = new CharacterPresetCommand(1, CharacterPresetKind.Spin, 0, 1, 2, 1, 0, 0, 0);
        AssertEx.Equal(CharacterPresetSpinAxis.Y, legacy.SpinAxis);
        AssertEx.Equal(legacy, Parse("#fx;1;spin"));
        AssertEx.Equal(legacy, Parse("#fx;1;spin;axis=y"));
        AssertEx.Equal(0, (int)CharacterPresetSpinAxis.Y);
        AssertEx.Equal(1, (int)CharacterPresetSpinAxis.X);
        const string canonical = "#fx;1;spin;frequency=1;cycles=2;direction=right";
        var compiler = new CharacterPresetCommandFamilyCompiler();
        AssertEx.Equal(canonical, AssertEx.NotNull(compiler.Canonicalize("#fx;1;spin").Value).Directive);
        AssertEx.Equal(canonical, AssertEx.NotNull(compiler.Canonicalize("#fx;1;spin;axis=Y").Value).Directive);
        AssertEx.Equal(0f, new CharacterPresetFrame(0f, 1f, 1f, false).PitchDegrees);
        AssertEx.True(typeof(CharacterPresetCommand).GetConstructor(new[]
        {
            typeof(int), typeof(CharacterPresetKind), typeof(float), typeof(float), typeof(int),
            typeof(int), typeof(float), typeof(float), typeof(int)
        }) != null);
        AssertEx.True(typeof(CharacterPresetFrame).GetConstructor(new[]
            { typeof(float), typeof(float), typeof(float), typeof(bool) }) != null);
        foreach (int arity in new[] { 4, 5 })
            AssertEx.True(typeof(CharacterPresetPoseOverlay).GetMethods().Any(method =>
                method.Name == nameof(CharacterPresetPoseOverlay.ProjectAuthoredState)
                && method.GetParameters().Length == arity
                && method.GetParameters().All(parameter => !parameter.IsOptional)));
        foreach (int arity in new[] { 5, 6 })
            AssertEx.True(typeof(CharacterPresetPoseOverlay).GetMethods().Any(method =>
                method.Name == nameof(CharacterPresetPoseOverlay.RemoveOwned)
                && method.GetParameters().Length == arity
                && method.GetParameters().All(parameter => !parameter.IsOptional)));
        Near(45, CharacterPresetEvaluator.Sample(legacy, .125).YawDegrees);
        Near(0, CharacterPresetEvaluator.Sample(legacy, .125).PitchDegrees);
    }

    public static void ParserAcceptsOnlySpinXOrYAndCanonicalizesIdempotently()
    {
        var compiler = new CharacterPresetCommandFamilyCompiler();
        CharacterPresetCommand command = Parse("#FX;2;SPIN; AXIS = X ;cycles=4;frequency=1.5;direction=LEFT");
        AssertEx.Equal(CharacterPresetSpinAxis.X, command.SpinAxis);
        CanonicalTimelineCommand canonical = AssertEx.NotNull(compiler.Canonicalize(
            "#FX;2;SPIN; AXIS = X ;cycles=4;frequency=1.5;direction=LEFT").Value);
        AssertEx.Equal("#fx;2;spin;frequency=1.5;cycles=4;axis=x;direction=left", canonical.Directive);
        AssertEx.Equal(command, Parse(canonical.Directive));
        AssertEx.Equal(canonical, AssertEx.NotNull(compiler.Canonicalize(canonical.Directive).Value));
        foreach (string invalid in new[]
        {
            "#fx;1;spin;axis=", "#fx;1;spin;axis=z", "#fx;1;spin;axis=0", "#fx;1;spin;axis=pitch",
            "#fx;1;spin;axis=x;AXIS=y", "#fx;1;sway;axis=x", "#fx;1;sway;axis=y",
            "#fx;1;headbutt;axis=x", "#fx;1;headbutt;axis=y", "#fx;1;squash;axis=x", "#fx;1;squash;axis=y"
        })
        {
            AssertEx.False(new CharacterPresetDirectiveParser().Parse(invalid).Success, invalid);
            AssertEx.False(compiler.Canonicalize(invalid).Success, invalid);
            EmbeddedAavtExtraction extraction = new EmbeddedAavtDirectiveExtractor().Extract("#aavt;" + invalid[1..]);
            AssertEx.Equal(0, extraction.Commands.Count, invalid);
            AssertEx.True(extraction.Errors.Count > 0, invalid);
        }
    }

    public static void ValidatorRejectsUnknownAxisAndCustomAxisOnOtherKinds()
    {
        foreach (string kind in new[] { "spin", "sway", "headbutt", "squash" })
        {
            CharacterPresetCommand command = Parse("#fx;1;" + kind);
            AssertEx.True(CharacterPresetCommandValidator.Validate(command).Success);
            var unknown = command with { SpinAxis = (CharacterPresetSpinAxis)99 };
            AssertEx.False(CharacterPresetCommandValidator.Validate(unknown).Success);
            AssertEx.Equal(CharacterPresetFrame.Neutral(true), CharacterPresetEvaluator.Sample(unknown, .125));
            var pitch = command with { SpinAxis = CharacterPresetSpinAxis.X };
            AssertEx.Equal(kind == "spin", CharacterPresetCommandValidator.Validate(pitch).Success);
            if (kind != "spin")
                AssertEx.Equal(CharacterPresetFrame.Neutral(true), CharacterPresetEvaluator.Sample(pitch, .125));
        }
    }

    public static void XSpinUsesPitchForWholeRevolutionsAndExactCompletion()
    {
        CharacterPresetCommand command = Parse("#fx;2;spin;axis=x;frequency=2;cycles=3;direction=left");
        Near(-90, CharacterPresetEvaluator.Sample(command, .125).PitchDegrees);
        Near(-180, CharacterPresetEvaluator.Sample(command, .25).PitchDegrees);
        Near(-360, CharacterPresetEvaluator.Sample(command, .5).PitchDegrees);
        Near(-900, CharacterPresetEvaluator.Sample(command, 1.25).PitchDegrees);
        Near(900, CharacterPresetEvaluator.Sample(command with { Direction = 1 }, 1.25).PitchDegrees);
        for (int index = 0; index < 1500; index++)
        {
            CharacterPresetFrame frame = CharacterPresetEvaluator.Sample(command, index / 1000d);
            Near(0, frame.YawDegrees); Near(0, frame.RotationDegrees);
            Near(1, frame.ScaleX); Near(1, frame.ScaleY);
            AssertEx.False(frame.Completed);
        }
        foreach (double time in new[] { 0d, -1d, double.NaN, double.NegativeInfinity, double.PositiveInfinity })
            AssertEx.Equal(CharacterPresetFrame.Neutral(), CharacterPresetEvaluator.Sample(command, time));
        AssertEx.Equal(CharacterPresetFrame.Neutral(true), CharacterPresetEvaluator.Sample(command, 1.5));
        AssertEx.Equal(CharacterPresetFrame.Neutral(true), CharacterPresetEvaluator.Sample(command, double.MaxValue));
    }

    public static void BezierWarpsEveryPitchTurnWithoutChangingDuration()
    {
        CharacterPresetCommand command = Parse("#fx;2;spin;axis=x;frequency=1;cycles=3;bezier=0,0,0,1");
        Near(180, CharacterPresetEvaluator.Sample(command, .125).PitchDegrees);
        Near(540, CharacterPresetEvaluator.Sample(command, 1.125).PitchDegrees);
        Near(900, CharacterPresetEvaluator.Sample(command, 2.125).PitchDegrees);
        Near(-900, CharacterPresetEvaluator.Sample(command with { Direction = -1 }, 2.125).PitchDegrees);
        foreach (double boundary in new[] { 1d, 2d })
        {
            Near(boundary * 360, CharacterPresetEvaluator.Sample(command, boundary).PitchDegrees);
            Near(boundary * 360, CharacterPresetEvaluator.Sample(command, boundary - 1e-8).PitchDegrees, .01);
            Near(boundary * 360, CharacterPresetEvaluator.Sample(command, boundary + 1e-8).PitchDegrees, .01);
        }
        for (int index = 0; index <= 3000; index++)
        {
            CharacterPresetFrame pitch = CharacterPresetEvaluator.Sample(command, index / 1000d);
            CharacterPresetFrame yaw = CharacterPresetEvaluator.Sample(command with { SpinAxis = CharacterPresetSpinAxis.Y }, index / 1000d);
            Near(yaw.YawDegrees, pitch.PitchDegrees);
            Near(0, pitch.YawDegrees);
            AssertEx.Equal(yaw.Completed, pitch.Completed);
        }
        AssertEx.Equal(CharacterPresetFrame.Neutral(true), CharacterPresetEvaluator.Sample(command, 3));
    }

    public static void UnboundedXSpinStaysFiniteAndDialogueAuthorityDoesNotReviveIt()
    {
        CharacterPresetCommand command = Parse("#fx;1;spin;axis=x;frequency=2;cycles=0;bezier=0.42,0,0.58,1");
        AssertEx.Equal(CharacterPresetEvaluator.Sample(command, .625), CharacterPresetEvaluator.Sample(command, 50000.625));
        foreach (double time in new[] { 100d, 1000000d, double.MaxValue })
        {
            CharacterPresetFrame frame = CharacterPresetEvaluator.Sample(command, time);
            AssertEx.False(frame.Completed);
            AssertEx.True(float.IsFinite(frame.PitchDegrees));
            Near(0, frame.YawDegrees); Near(0, frame.RotationDegrees);
            Near(1, frame.ScaleX); Near(1, frame.ScaleY);
        }
        var boundaries = new CharacterPresetWindowGate();
        AssertEx.True(boundaries.ObserveBoundary(10));
        AssertEx.True(boundaries.MayStart(10));
        AssertEx.False(boundaries.ObservePlaybackBoundary(11, 3, 3, 0));
        AssertEx.True(boundaries.ObservePlaybackBoundary(12, 3, 4, 0));
        AssertEx.False(boundaries.MayStart(10));
        AssertEx.True(boundaries.MayStart(12));
    }

    public static void PitchOverlayPreservesAuthoredPoseAndEquivalentEulerOwnership()
    {
        foreach (float startingPitch in new[] { -90f, -25f, 0f, 25f, 90f })
        foreach (float startingYaw in new[] { 0f, 180f })
        foreach (float pitch in new[] { -720f, -270f, -90f, 90f, 180f, 270f, 720f })
        {
            var baseline = new CharacterPresetPose(startingPitch, startingYaw, 23f, -3f, 6f, 2f);
            CharacterPresetPose applied = CharacterPresetPoseOverlay.Apply(baseline,
                new CharacterPresetFrame(0f, 1f, 1f, false) { PitchDegrees = pitch });
            AssertEx.Equal(baseline with { EulerX = startingPitch + pitch }, applied);
            CharacterPresetPose[] nativeRepresentations =
            {
                applied,
                applied with { EulerX = applied.EulerX + 360f, EulerY = applied.EulerY - 720f, EulerZ = applied.EulerZ + 360f },
                applied with { EulerX = 180f - applied.EulerX, EulerY = applied.EulerY + 180f, EulerZ = applied.EulerZ + 180f }
            };
            foreach (CharacterPresetPose native in nativeRepresentations)
            {
                AssertEx.True(CharacterPresetPoseOverlay.SameRotation(applied, native));
                AssertEx.Equal(baseline, RemovePitch(native, baseline, applied));
            }
        }
    }

    public static void PitchCleanupPreservesExternalOrientationAsTheNextBaseline()
    {
        var baseline = new CharacterPresetPose(12f, 180f, 23f, -3f, 6f, 2f);
        var applied = CharacterPresetPoseOverlay.Apply(baseline,
            new CharacterPresetFrame(0f, 1f, 1f, false) { PitchDegrees = 50f });
        foreach (CharacterPresetPose external in new[]
        {
            applied with { EulerX = 33f }, applied with { EulerY = 77f }, applied with { EulerZ = 55f },
            applied with { EulerX = 33f, EulerY = 77f, EulerZ = 55f, ScaleX = -9f, ScaleZ = 5f }
        })
        {
            AssertEx.False(CharacterPresetPoseOverlay.SameRotation(applied, external));
            CharacterPresetPose clean = RemovePitch(external, baseline, applied);
            AssertEx.Equal(external, clean);
            var next = CharacterPresetPoseOverlay.Apply(clean,
                new CharacterPresetFrame(0f, 1f, 1f, false) { PitchDegrees = 30f });
            AssertEx.Equal(external with { EulerX = external.EulerX + 30f }, next);
            AssertEx.Equal(external, RemovePitch(next, clean, next));
        }
        AssertEx.Equal(applied, CharacterPresetPoseOverlay.RemoveOwned(applied, baseline, applied,
            false, false, ownsYaw: false, ownsPitch: true));
    }

    public static void PitchSnapshotsRemoveOnlyOwnedTemporaryRotation()
    {
        foreach (float startingYaw in new[] { 0f, 180f })
        {
            var baseline = new CharacterPresetPose(5f, startingYaw, 30f, -3f, 6f, 2f);
            var applied = CharacterPresetPoseOverlay.Apply(baseline,
                new CharacterPresetFrame(0f, 1f, 1f, false) { PitchDegrees = 135f });
            var equivalent = applied with
            {
                EulerX = 180f - applied.EulerX, EulerY = applied.EulerY + 180f, EulerZ = applied.EulerZ + 180f
            };
            foreach (CharacterPresetPose native in new[] { applied, equivalent })
            {
                CharacterTransformState displayed = DisplayedState(native);
                AssertEx.Equal(DisplayedState(baseline), CharacterPresetPoseOverlay.ProjectAuthoredState(
                    displayed, baseline, applied, true, ownsYaw: false, ownsPitch: true));
                AssertEx.Equal(DisplayedState(native), displayed);
                AssertEx.Equal(displayed, CharacterPresetPoseOverlay.ProjectAuthoredState(
                    displayed, baseline, applied, false, ownsYaw: false, ownsPitch: true));
            }
            var external = applied with { EulerY = 77f };
            AssertEx.Equal(DisplayedState(external), CharacterPresetPoseOverlay.ProjectAuthoredState(
                DisplayedState(external), baseline, applied, true, ownsYaw: false, ownsPitch: true));
        }
    }

    public static void RepeatedPitchFramesAndReleaseRestoreExactAuthoredPose()
    {
        var command = Parse("#fx;3;spin;axis=x;frequency=2;cycles=3;direction=right;bezier=0.42,0,0.58,1");
        var baseline = new CharacterPresetPose(7f, 180f, 31f, -3f, 6f, 2f);
        CharacterPresetPose logical = baseline;
        for (int index = 0; index <= 1500; index++)
        {
            var frame = CharacterPresetEvaluator.Sample(command, index / 1000d);
            var applied = CharacterPresetPoseOverlay.Apply(logical, frame);
            var native = applied with { EulerX = applied.EulerX % 360f };
            logical = RemovePitch(native, logical, applied);
            AssertEx.Equal(baseline, logical);
            AssertEx.Equal(baseline.EulerY, applied.EulerY);
            AssertEx.Equal(baseline.EulerZ, applied.EulerZ);
        }
        var active = CharacterPresetPoseOverlay.Apply(logical, CharacterPresetEvaluator.Sample(command, .125));
        // The same cleanup is used by completion, next dialogue, replay, and mode exit.
        AssertEx.Equal(baseline, RemovePitch(active, logical, active));
        AssertEx.Equal(baseline, CharacterPresetPoseOverlay.Apply(logical, CharacterPresetEvaluator.Sample(command, 1.5)));
        // Replaying changes the temporary axis without inheriting the old turn.
        var replay = CharacterPresetPoseOverlay.Apply(logical,
            CharacterPresetEvaluator.Sample(command with { SpinAxis = CharacterPresetSpinAxis.Y }, .125));
        AssertEx.Equal(baseline.EulerX, replay.EulerX);
        AssertEx.Equal(baseline, CharacterPresetPoseOverlay.RemoveOwned(replay, logical, replay, true, false, ownsYaw: true));
    }

    public static void AuthoredHighPitchUsesEquivalentBaselineWithoutReversingXSpin()
    {
        var baseline = new CharacterPresetPose(120f, 0f, 15f, -3f, 6f, 2f);
        var canonical = baseline with { EulerX = 60f, EulerY = 180f, EulerZ = 195f };
        AssertEx.True(CharacterPresetPoseOverlay.SameRotation(baseline, canonical));
        var quarterTurn = new CharacterPresetFrame(0f, 1f, 1f, false) { PitchDegrees = 90f };
        var expected = baseline with { EulerX = 210f };
        // Adding X directly to Unity's alternate triple reverses this turn:
        // (150,180,195) denotes (30,0,15), not the authored (210,0,15).
        var incorrect = CharacterPresetPoseOverlay.Apply(canonical, quarterTurn);
        AssertEx.True(CharacterPresetPoseOverlay.SameRotation(incorrect, baseline with { EulerX = 30f }));
        AssertEx.False(CharacterPresetPoseOverlay.SameRotation(incorrect, expected));
        var before = CharacterPresetPoseOverlay.ReexpressRotation(canonical, baseline);
        AssertEx.Equal(baseline, before);
        AssertEx.Equal(expected, CharacterPresetPoseOverlay.Apply(before, quarterTurn));

        foreach (float pitch in new[] { -120f, 120f, 270f, 450f })
        foreach (float yaw in new[] { 0f, 180f })
        {
            var authored = baseline with { EulerX = pitch, EulerY = yaw };
            var native = authored with
            {
                EulerX = 180f - pitch, EulerY = yaw + 180f, EulerZ = authored.EulerZ + 180f,
                ScaleX = -9f, ScaleY = 4f
            };
            var measured = CharacterPresetPoseOverlay.ReexpressRotation(native, authored);
            AssertEx.True(CharacterPresetPoseOverlay.SameRotation(native, measured));
            AssertEx.Equal(authored with { ScaleX = -9f, ScaleY = 4f }, measured);
            var target = CharacterPresetPoseOverlay.Apply(measured, quarterTurn);
            AssertEx.Equal(measured with { EulerX = pitch + 90f }, target);
        }
    }

    public static void HighPitchFramesRetainAuthoredDirectionAfterCanonicalCleanup()
    {
        var command = Parse("#fx;3;spin;axis=x;frequency=2;cycles=3;bezier=0.42,0,0.58,1");
        var baseline = new CharacterPresetPose(120f, 0f, 15f, -3f, 6f, 2f);
        CharacterPresetPose reference = baseline;
        CharacterPresetPose observed = AlternateEuler(baseline);
        for (int index = 0; index <= 1500; index++)
        {
            CharacterPresetFrame frame = CharacterPresetEvaluator.Sample(command, index / 1000d);
            CharacterPresetPose before = CharacterPresetPoseOverlay.ReexpressRotation(observed, reference);
            AssertEx.Equal(baseline, before);
            var applied = CharacterPresetPoseOverlay.Apply(before, frame);
            AssertEx.Equal(baseline with { EulerX = baseline.EulerX + frame.PitchDegrees }, applied);
            var actualNative = AlternateEuler(applied);
            // Runtime records the actual written native triple for ownership,
            // then restores its logical Before. Unity can canonicalize it again.
            var restored = RemovePitch(actualNative, before, actualNative);
            AssertEx.Equal(baseline, restored);
            reference = before;
            observed = AlternateEuler(restored);
        }
        AssertEx.Equal(baseline, CharacterPresetPoseOverlay.ReexpressRotation(observed, reference));
    }

    public static void HighPitchExternalXAndYTakeoverKeepTheObservedOrientation()
    {
        var baseline = new CharacterPresetPose(120f, 0f, 15f, -3f, 6f, 2f);
        var frame = new CharacterPresetFrame(0f, 1f, 1f, false) { PitchDegrees = 90f };
        var applied = CharacterPresetPoseOverlay.Apply(baseline, frame);
        var actualNative = AlternateEuler(applied);
        var changes = new[]
        {
            (Observed: actualNative with { EulerX = actualNative.EulerX + 12f },
                Logical: baseline with { EulerX = 198f }),
            (Observed: actualNative with { EulerY = actualNative.EulerY + 38f },
                Logical: baseline with { EulerX = 210f, EulerY = 38f }),
            (Observed: actualNative with { EulerZ = actualNative.EulerZ + 30f },
                Logical: baseline with { EulerX = 210f, EulerZ = 45f })
        };
        foreach (var change in changes)
        {
            AssertEx.False(CharacterPresetPoseOverlay.SameRotation(change.Observed, actualNative));
            var cleaned = RemovePitch(change.Observed, baseline, actualNative);
            AssertEx.Equal(change.Observed, cleaned);
            var before = CharacterPresetPoseOverlay.ReexpressRotation(cleaned, baseline);
            AssertEx.Equal(change.Logical, before);
            AssertEx.True(CharacterPresetPoseOverlay.SameRotation(change.Observed, before));
            var next = CharacterPresetPoseOverlay.Apply(before, frame);
            AssertEx.Equal(change.Logical with { EulerX = change.Logical.EulerX + 90f }, next);
            var released = RemovePitch(AlternateEuler(next), before, next);
            AssertEx.Equal(before, released);
            AssertEx.True(CharacterPresetPoseOverlay.SameRotation(change.Observed, released));
            AssertEx.Equal(DisplayedState(change.Observed), CharacterPresetPoseOverlay.ProjectAuthoredState(
                DisplayedState(change.Observed), baseline, actualNative, true, ownsYaw: false, ownsPitch: true));
        }
    }

    private static CharacterPresetPose AlternateEuler(CharacterPresetPose pose) => pose with
    {
        EulerX = 180f - pose.EulerX, EulerY = pose.EulerY + 180f, EulerZ = pose.EulerZ + 180f
    };

    private static CharacterPresetCommand Parse(string directive)
    {
        var parsed = new CharacterPresetDirectiveParser().Parse(directive);
        AssertEx.True(parsed.Success, parsed.Error);
        return AssertEx.NotNull(parsed.Value);
    }

    private static CharacterPresetPose RemovePitch(CharacterPresetPose current,
        CharacterPresetPose baseline, CharacterPresetPose applied) =>
        CharacterPresetPoseOverlay.RemoveOwned(current, baseline, applied, true, false, ownsYaw: false, ownsPitch: true);

    private static CharacterTransformState DisplayedState(CharacterPresetPose pose) => new(
        new CharacterVector3(700f, -250f, 4f),
        new CharacterVector3(pose.EulerX, pose.EulerY,
            CharacterScreenRotation.ToScreenDegrees(pose.EulerZ, pose.EulerY)));

    private static void Near(double expected, double actual, double tolerance = .0001) =>
        AssertEx.True(double.IsFinite(actual) && Math.Abs(expected - actual) <= tolerance,
            $"Expected {expected} ± {tolerance}, got {actual}.");
}
