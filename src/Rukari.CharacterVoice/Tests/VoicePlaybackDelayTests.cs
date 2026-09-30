using Rukari.CharacterVoice.Core;
using Rukari.CharacterVoice.Runtime;
using Rukari.Lib.Tools;

internal static class VoicePlaybackDelayTests
{
    private const string Voice = "aavt/voice/test";

    internal static void ZeroReleasesImmediatelyWithoutAnotherFrame()
    {
        var gate = Playing(0);
        True(!gate.ObserveNativeWait(false, 5, 300));
        Equal(VoicePlaybackPhase.Completed, gate.Phase);
        True(!gate.BlocksAdvance(false, false));
    }

    internal static void NativeWaitCannotExitBeforeTheDelayEvenWithoutUpdate()
    {
        var gate = Playing(1.5);
        // The native coroutine asks first, before the Mod Update can notice the clip ending.
        True(gate.ObserveNativeWait(false, 5, 300));
        Equal(VoicePlaybackPhase.PostPlaybackDelay, gate.Phase);
        True(gate.BlocksAdvance(false, false));
        True(gate.ObserveNativeWait(false, 6.49, 300));
        True(!gate.ObserveNativeWait(false, 6.5, 300));
        True(!gate.BlocksAdvance(false, false)); // its single AdvanceScenario is now admitted
        True(!gate.Expire(999));
        Equal(VoicePlaybackPhase.Completed, gate.Phase);
    }

    internal static void TrailingSilenceStillBelongsToTheClipAndPollingDoesNotRestartDelay()
    {
        var gate = Playing(2);
        True(gate.ObserveNativeWait(true, 5, 300)); // actual clip still includes silent audio samples
        True(gate.ObserveNativeWait(true, 7, 300));
        True(gate.ObserveNativeWait(false, 8, 300));
        True(gate.ObserveNativeWait(false, 9.9, 300));
        True(!gate.ObserveNativeWait(false, 10, 300));
    }

    internal static void ManagedTickCanFinishDelayWithoutTouchingTheAudioEngine()
    {
        var gate = Playing(1);
        gate.ObservePlayback(gate.RequestSequence, false, 5, 300);
        True(!gate.Expire(5.9));
        True(gate.Pending);
        True(!gate.Expire(6), "Normal delay expiry must not be reported as an audio failure.");
        Equal(VoicePlaybackPhase.Completed, gate.Phase);
        True(!gate.Pending);
    }

    internal static void ReplacementCancelAndFailureNeverWaitForAnOldTail()
    {
        var gate = Playing(10);
        long old = gate.RequestSequence;
        gate.ObserveNativeWait(false, 5, 300);
        gate.Cancel("project-closed");
        True(!gate.ObserveNativeWait(false, 6, 300));
        long next = gate.Begin(Voice, 6, 10, 0);
        True(!gate.ObservePlayback(old, false, 50, 300));
        Equal(VoicePlaybackPhase.Loading, gate.Phase);
        True(gate.Fail(next, "missing-file"));
        True(!gate.HoldNativeVoiceWait);
        True(!gate.BlocksAdvance(false, false));
        True(!gate.Expire(999));
    }

    internal static void DelayIsBoundedAndOldBeginContractStillMeansZero()
    {
        Equal(0d, VoicePlaybackDelayPolicy.Normalize(double.NaN));
        Equal(0d, VoicePlaybackDelayPolicy.Normalize(double.PositiveInfinity));
        Equal(0d, VoicePlaybackDelayPolicy.Normalize(-1));
        Equal(10d, VoicePlaybackDelayPolicy.Normalize(100));
        Equal(1.3d, VoicePlaybackDelayPolicy.Normalize(1.25));
        var gate = Playing(2);
        gate.Begin(Voice, 10, 10);
        Equal(0d, gate.PostPlaybackDelaySeconds);
        True(!gate.BlocksAdvance(true, false));
        True(!gate.BlocksAdvance(false, true));
        True(gate.Expire(20)); // loading fails, it never enters a post-playback delay
        Equal(VoicePlaybackPhase.Failed, gate.Phase);
    }

    internal static void SliderCapturesOutsideDragClampsAndSavesOnlyOnRelease()
    {
        float setting = 0;
        int writes = 0;
        var page = new VoicePlaybackSettingsPage(() => setting, value => { setting = value; writes++; });
        page.OnShown();
        ToolPanelBuilder idle = Frame(page);
        ToolInputRect bounds = idle.Find(VoicePlaybackSettingsPage.SliderId)!.Bounds;
        // Thumb decoration must hit the same Area as the track.
        Equal(VoicePlaybackSettingsPage.SliderId, ToolPanelBuilder.HitTest(idle.Elements, 12, bounds.Y + 24));
        Frame(page, new(9999, bounds.Y + 24, true, true, false, 0, 0), VoicePlaybackSettingsPage.SliderId);
        Equal(0, writes);
        Frame(page, new(9999, -20, true, false, false, 0, 0), VoicePlaybackSettingsPage.SliderId);
        Equal(0, writes);
        Frame(page, new(9999, -20, false, false, true, 0, 0), VoicePlaybackSettingsPage.SliderId);
        Equal(1, writes);
        Equal(10f, setting);
        page.OnHidden();
        Equal(1, writes);
        page.OnShown();
        Frame(page, new(-99, bounds.Y, true, true, false, 0, 0), VoicePlaybackSettingsPage.SliderId);
        page.OnHidden(); // closing the panel in mid-drag also commits once
        Equal(2, writes);
        Equal(0f, setting);
    }

    internal static void SettingsUseTheSharedLayoutAndDoNotWriteOnOpenOrUnrelatedClicks()
    {
        int writes = 0;
        var page = new VoicePlaybackSettingsPage(() => 1.5f, _ => writes++);
        page.OnShown();
        var frame = Frame(page);
        True(frame.Elements.All(e => e.Bounds.Y >= 0 && e.Bounds.Y + e.Bounds.Height <= frame.Height));
        True(frame.Find("voice-delay.title")!.Text.Contains("1.5"));
        Frame(page, new(200, 200, true, false, false, 0, 0), "another-page");
        page.OnHidden();
        Equal(0, writes);
    }

    private static VoicePlaybackGate Playing(double delay)
    {
        var gate = new VoicePlaybackGate();
        long request = gate.Begin(Voice, 0, 10, delay);
        True(gate.MarkStarting(request, 1));
        True(gate.ObservePlayback(request, true, 1, 300));
        return gate;
    }

    private static ToolPanelBuilder Frame(VoicePlaybackSettingsPage page, ToolPanelPointer pointer = default, string? pressed = null)
    {
        var layout = ToolDrawerLayout.MeasureHosted(page.PreferredHeight, page.PreferredWidth);
        var frame = new ToolPanelBuilder(new(0, 0, page.PreferredWidth - 40, layout.ContentHeight), pointer, pressed);
        page.Draw(frame);
        return frame;
    }

    private static void True(bool value, string? message = null)
    { if (!value) throw new InvalidOperationException(message ?? "Expected true."); }
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, actual {actual}."); }
}
