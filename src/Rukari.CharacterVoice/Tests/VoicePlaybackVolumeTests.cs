using Rukari.CharacterVoice.Core;
using Rukari.CharacterVoice.Runtime;
using Rukari.Lib.Tools;

internal static class VoicePlaybackVolumeTests
{
    internal static void NonfiniteAndOutOfRangeSettingsNormalizeWithoutHalvingTheDefault()
    {
        Equal(50f, VoicePlaybackVolumePolicy.DefaultPercent);
        foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Equal(50f, VoicePlaybackVolumePolicy.Normalize(invalid));
            True(VoicePlaybackVolumePolicy.IsNeutral(invalid));
        }
        Equal(0f, VoicePlaybackVolumePolicy.Normalize(-1));
        Equal(100f, VoicePlaybackVolumePolicy.Normalize(500));
        Equal(76f, VoicePlaybackVolumePolicy.Normalize(75.5));
        Equal(0f, VoicePlaybackVolumePolicy.FromPointer(-100, 12, 100));
        Equal(50f, VoicePlaybackVolumePolicy.FromPointer(62, 12, 100));
        Equal(100f, VoicePlaybackVolumePolicy.FromPointer(999, 12, 100));
        Equal(50f, VoicePlaybackVolumePolicy.FromPointer(float.NaN, 12, 100));
        Equal(50f, VoicePlaybackVolumePolicy.FromPointer(10, 12, 0));
        Equal(50f, VoicePlaybackVolumePolicy.FromPointer(10, 12, float.PositiveInfinity));
    }

    internal static void ScalingPreservesMidpointAndMuteAndClampsSourceOutput()
    {
        Equal(.4f, VoicePlaybackVolumePolicy.ScaleOfficialVolume(.4f, 50));
        Equal(.8f, VoicePlaybackVolumePolicy.ScaleOfficialVolume(.4f, 100));
        Equal(1f, VoicePlaybackVolumePolicy.ScaleOfficialVolume(.8f, 100));
        Equal(.2f, VoicePlaybackVolumePolicy.ScaleOfficialVolume(.4f, 25));
        Equal(0f, VoicePlaybackVolumePolicy.ScaleOfficialVolume(.4f, 0));
        Equal(0f, VoicePlaybackVolumePolicy.ScaleOfficialVolume(0, 100));
        Equal(0f, VoicePlaybackVolumePolicy.ScaleOfficialVolume(float.NaN, 100));
    }

    internal static void AdmissionRequiresAnotherRequestAndRequalifiesAChangedSource()
    {
        var admission = new VoicePlaybackVolumeAdmission();
        True(admission.NeedsObservation(1, 10));
        True(!admission.ObserveBaseline(1, 10, .4f), "First playback is getter-only.");
        True(!admission.NeedsObservation(1, 10));
        True(!admission.ObserveBaseline(1, 10, .4f), "Duplicate callbacks cannot enable writing.");
        True(admission.NeedsObservation(2, 10));
        True(admission.ObserveBaseline(2, 10, .4f));
        True(!admission.ObserveBaseline(2, 10, .4f));
        True(!admission.ObserveBaseline(3, 20, .2f), "A new source starts at getter-only again.");
        True(admission.ObserveBaseline(4, 20, .2f));
        True(!admission.ObserveBaseline(5, 10, .4f), "Returning to an old source must requalify.");
    }

    internal static void InvalidBaselinePermanentlyDisablesOnlyTheAdmissionPolicy()
    {
        foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, -.1f, 1.1f })
        {
            var admission = new VoicePlaybackVolumeAdmission();
            True(!admission.ObserveBaseline(1, 10, invalid));
            True(admission.IsDisabled);
            True(!admission.NeedsObservation(2, 10));
            True(!admission.ObserveBaseline(2, 10, .5f));
            Equal(0L, admission.ObservedRequest);
        }
        var wrongIdentity = new VoicePlaybackVolumeAdmission();
        True(!wrongIdentity.ObserveBaseline(1, 0, .5f));
        True(wrongIdentity.IsDisabled);
        var wrongRequest = new VoicePlaybackVolumeAdmission();
        True(!wrongRequest.ObserveBaseline(0, 10, .5f));
        True(wrongRequest.IsDisabled);
    }

    internal static void InitiallyPlayingSourceCannotQualifyVolumeEvenIfItLaterBecomesQuiet()
    {
        var qualification = new VoicePlaybackVolumeStartQualification();
        True(qualification.Begin(1));
        True(!qualification.Observe(1, true), "The first true may still be the previous native clip.");
        True(qualification.Decided && !qualification.SawQuiet);
        True(!qualification.Observe(1, false));
        True(!qualification.Observe(1, true), "An already skipped request cannot retry on a later clip.");
    }

    internal static void QuietThenPlayingQualifiesExactlyOnceForTheCurrentRequest()
    {
        var qualification = new VoicePlaybackVolumeStartQualification();
        True(qualification.Begin(1));
        True(!qualification.Observe(1, false));
        True(!qualification.Observe(1, false));
        True(qualification.SawQuiet && !qualification.Decided);
        True(qualification.Observe(1, true));
        True(!qualification.Observe(1, true), "Duplicate playing observations cannot repeat adjustment.");
        True(!qualification.Observe(1, false));
        True(!qualification.Observe(1, true));
    }

    internal static void QuietQualificationNeverLeaksAcrossResetReplacementOrStaleCallbacks()
    {
        var qualification = new VoicePlaybackVolumeStartQualification();
        True(!qualification.Observe(1, false));
        True(qualification.Begin(1));
        True(!qualification.Observe(1, false));
        qualification.Reset();
        True(!qualification.Observe(1, true));
        True(qualification.Begin(2));
        True(!qualification.Observe(1, false), "A stale callback cannot qualify the new request.");
        True(!qualification.SawQuiet);
        True(!qualification.Observe(2, true));
        True(qualification.Begin(3));
        True(!qualification.Observe(3, false));
        True(qualification.Begin(4));
        True(!qualification.SawQuiet && !qualification.Decided);
        True(!qualification.Observe(4, true));
        True(!qualification.Begin(0));
        Equal(0L, qualification.RequestSequence);
    }

    internal static void PreparingWithoutAWriteNeverRestoresEvenIfTheValuesCoincide()
    {
        var lease = new VoicePlaybackVolumeLease();
        True(!lease.TryPrepare(1, 10, .4f, 50, out _));
        True(!lease.TryPrepare(1, 10, 0, 100, out _), "A muted baseline must not be boosted.");
        True(!lease.TryPrepare(1, 10, float.NaN, 100, out _));
        True(lease.TryPrepare(1, 10, .4f, 100, out float target));
        Equal(.8f, target);
        True(!lease.MarkApplied(1, 10, target));
        True(!lease.TryRestore(1, 10, target, out _), "No native write was attempted.");
        True(!lease.HasLease);
        True(!lease.WriteAttempted);
    }

    internal static void ExactLeaseRestoresOnceButExternalVolumeOrSourceChangesAreLeftAlone()
    {
        var lease = Applied();
        True(lease.TryRestore(1, 10, .8f, out float restored));
        Equal(.4f, restored);
        True(!lease.HasLease);
        True(!lease.TryRestore(1, 10, .8f, out _));
        lease = Applied();
        True(!lease.TryRestore(1, 10, .7f, out _), "An official/other-mod change must not be overwritten.");
        True(!lease.HasLease);
        lease = Applied();
        True(!lease.TryRestore(1, 20, .8f, out _), "Never restore another source.");
        True(!lease.HasLease);
    }

    internal static void UnconfirmedWriteCanOnlyRecoverThroughLaterFreshExactCleanup()
    {
        var lease = new VoicePlaybackVolumeLease();
        True(lease.TryPrepare(1, 10, .4f, 100, out float target));
        True(!lease.MarkWriteAttempted(2, 10));
        True(!lease.MarkWriteAttempted(1, 20));
        True(lease.MarkWriteAttempted(1, 10)); // Setter may have changed native state before failing.
        True(!lease.MarkWriteAttempted(1, 10));
        True(!lease.MarkApplied(1, 10, float.NaN)); // Failed readback is not a confirmed application.
        True(!lease.HasApplied);
        True(lease.HasLease && lease.WriteAttempted);
        True(lease.TryRestore(1, 10, target, out float baseline));
        Equal(.4f, baseline);
        lease = new VoicePlaybackVolumeLease();
        True(lease.TryPrepare(1, 10, .4f, 100, out _));
        True(lease.MarkWriteAttempted(1, 10));
        True(!lease.MarkApplied(1, 10, .7f));
        True(!lease.TryRestore(1, 10, .7f, out _), "Cleanup cannot guess an uncertain setter outcome.");
        True(!lease.HasLease);
    }

    internal static void AStaleRequestCannotReleaseOrConfirmAReplacementLease()
    {
        var lease = Applied();
        True(!lease.TryPrepare(2, 10, .4f, 100, out _));
        True(lease.TryRestore(1, 10, .8f, out _));
        True(lease.TryPrepare(2, 10, .3f, 100, out float target));
        True(!lease.MarkWriteAttempted(1, 10));
        True(lease.MarkWriteAttempted(2, 10));
        True(!lease.MarkApplied(1, 10, target));
        True(lease.MarkApplied(2, 10, target));
        True(!lease.TryRestore(1, 10, target, out _));
        True(lease.HasLease && lease.HasApplied);
        Equal(2L, lease.RequestSequence);
        True(lease.TryRestore(2, 10, target, out float baseline));
        Equal(.3f, baseline);
    }

    internal static void VolumeDragSavesOnReleaseAndOnHideWithoutWritingTheDelaySetting()
    {
        float volume = 50;
        int volumeWrites = 0, delayWrites = 0;
        var page = new VoicePlaybackSettingsPage(() => 1.5f, _ => delayWrites++,
            () => volume, value => { volume = value; volumeWrites++; });
        page.OnShown();
        var idle = Frame(page);
        ToolInputRect bounds = idle.Find(VoicePlaybackSettingsPage.VolumeSliderId)!.Bounds;
        Equal(VoicePlaybackSettingsPage.VolumeSliderId, ToolPanelBuilder.HitTest(idle.Elements, 12, bounds.Y + 24));
        Frame(page, new(9999, bounds.Y + 24, true, true, false, 0, 0), VoicePlaybackSettingsPage.VolumeSliderId);
        Equal(0, volumeWrites);
        Frame(page, new(9999, -10, false, false, true, 0, 0), VoicePlaybackSettingsPage.VolumeSliderId);
        Equal(1, volumeWrites);
        Equal(100f, volume);
        page.OnHidden();
        Equal(1, volumeWrites);
        page.OnShown();
        True(Frame(page).Find("voice-volume.title")!.Text.Contains("100%"));
        Frame(page, new(-999, bounds.Y + 24, true, true, false, 0, 0), VoicePlaybackSettingsPage.VolumeSliderId);
        page.OnHidden();
        Equal(2, volumeWrites);
        Equal(0f, volume);
        Equal(0, delayWrites);
    }

    internal static void RestoreButtonsRemainIndependentAndLegacySettingsConstructorStillWorks()
    {
        float volume = 100, delay = 2;
        int volumeWrites = 0, delayWrites = 0;
        var page = new VoicePlaybackSettingsPage(() => delay, value => { delay = value; delayWrites++; },
            () => volume, value => { volume = value; volumeWrites++; });
        page.OnShown();
        Click(page, "voice-volume.default");
        Equal(50f, volume);
        Equal(2f, delay);
        Equal(1, volumeWrites);
        Equal(0, delayWrites);
        Click(page, "voice-delay.zero");
        Equal(0f, delay);
        Equal(50f, volume);
        Equal(1, delayWrites);
        var legacy = new VoicePlaybackSettingsPage(() => delay, value => delay = value);
        legacy.OnShown();
        True(Frame(legacy).Find("voice-volume.title")!.Text.Contains("50%"));
        legacy.OnHidden();
    }

    internal static void SettingsBoundsAndNoWriteOnOpenHoldAndFailedSavesRetryOnHide()
    {
        int attempts = 0, delayWrites = 0;
        float volume = 50;
        var page = new VoicePlaybackSettingsPage(() => 0, _ => delayWrites++, () => volume, value =>
        {
            if (++attempts == 1) throw new IOException("Simulated occupied configuration.");
            volume = value;
        });
        page.OnShown();
        var frame = Frame(page);
        True(frame.Elements.All(e => e.Bounds.Y >= 0 && e.Bounds.Y + e.Bounds.Height <= frame.Height));
        True(frame.Find("voice-volume.note")!.Text.Contains("后续一段"));
        Frame(page, new(100, 100, false, false, true, 0, 0), "other-panel");
        Equal(0, attempts);
        Click(page, "voice-volume.more");
        Equal(1, attempts);
        True(Frame(page).Find("voice-delay.saved")!.Text.Contains("保存失败"));
        Equal(50f, volume);
        page.OnHidden();
        Equal(2, attempts);
        Equal(55f, volume);
        Equal(0, delayWrites);
    }

    internal static void MethodMetadataPrefersTheExactCallbackOverloadAndKeepsLegacyFallback()
    {
        var modern = VoicePlaybackMethodPolicy.ResolveSetVoice(typeof(ModernShape), typeof(Action), out bool callback);
        True(callback);
        Equal(2, modern.GetParameters().Length);
        Equal(typeof(Action), modern.GetParameters()[1].ParameterType);
        True(!modern.IsStatic && modern.ReturnType == typeof(void));
        var legacy = VoicePlaybackMethodPolicy.ResolveSetVoice(typeof(LegacyShape), typeof(Action), out callback);
        True(!callback);
        Equal(1, legacy.GetParameters().Length);
    }

    internal static void MethodMetadataRejectsStaticWrongReturnAndUnknownCallbackTypes()
    {
        foreach (Type invalid in new[] { typeof(StaticShape), typeof(WrongReturnShape), typeof(WrongCallbackShape) })
        {
            bool failed = false;
            try { VoicePlaybackMethodPolicy.ResolveSetVoice(invalid, typeof(Action), out _); }
            catch (MissingMethodException) { failed = true; }
            True(failed, "Unrecognized native signature must fail closed.");
        }
    }

    internal static void DegradedCallbackModeDoesNotVetoThePassedThroughOfficialPreloader()
    {
        True(VoicePlaybackMethodPolicy.ShouldInterceptPreload(true, false, false));
        True(!VoicePlaybackMethodPolicy.ShouldInterceptPreload(true, true, false),
            "Passed-through SetVoice must not meet a suspended-polling preloader veto.");
        True(!VoicePlaybackMethodPolicy.ShouldInterceptPreload(true, false, true));
        True(!VoicePlaybackMethodPolicy.ShouldInterceptPreload(true, true, true));
        True(VoicePlaybackMethodPolicy.ShouldInterceptPreload(false, true, false),
            "Legacy polling suspension retains its prior interception behavior.");
        True(!VoicePlaybackMethodPolicy.ShouldInterceptPreload(false, false, true));
        True(!VoicePlaybackMethodPolicy.ShouldInterceptPreload(false, true, true));
    }

    private static VoicePlaybackVolumeLease Applied()
    {
        var lease = new VoicePlaybackVolumeLease();
        True(lease.TryPrepare(1, 10, .4f, 100, out float target));
        True(lease.MarkWriteAttempted(1, 10));
        True(lease.MarkApplied(1, 10, target));
        return lease;
    }
    private static ToolPanelBuilder Frame(VoicePlaybackSettingsPage page, ToolPanelPointer pointer = default, string? pressed = null)
    {
        var layout = ToolDrawerLayout.MeasureHosted(page.PreferredHeight, page.PreferredWidth);
        var frame = new ToolPanelBuilder(new(0, 0, page.PreferredWidth - 40, layout.ContentHeight), pointer, pressed);
        page.Draw(frame);
        return frame;
    }
    private static void Click(VoicePlaybackSettingsPage page, string id)
    {
        ToolInputRect bounds = Frame(page).Find(id)!.Bounds;
        Frame(page, new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2,
            false, false, true, 0, 0), id);
    }
    private static void True(bool value, string? message = null)
    { if (!value) throw new InvalidOperationException(message ?? "Expected true."); }
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, actual {actual}."); }

    private sealed class ModernShape
    {
        public void SetVoice(string name) { }
        public void SetVoice(string name, Action? onStarted = null) { }
        public void SetVoice(string name, object callback) { }
    }
    private sealed class LegacyShape { public void SetVoice(string name) { } }
    private sealed class StaticShape { public static void SetVoice(string name, Action? onStarted = null) { } }
    private sealed class WrongReturnShape { public int SetVoice(string name) => 0; }
    private sealed class WrongCallbackShape { public void SetVoice(string name, object callback) { } }
}
