using Rukari.CharacterVoice.Core;

internal static class VoicePlaybackCompatibilityTests
{
    public static void LegacyReplayUsesTheResolvedMethodAndAFreshReceiver()
    {
        var method = VoicePlaybackMethodPolicy.ResolveSetVoice(typeof(Legacy), typeof(Action), out bool callback);
        Action<Legacy, string> replay = VoicePlaybackMethodPolicy.BindLegacyReplay<Legacy>(method, callback)
            ?? throw new InvalidOperationException("Legacy replay was not bound.");
        True(!callback);
        True(replay.Target is null, "The replay must be open and retain no receiver.");
        var first = new Legacy();
        var second = new Legacy();
        replay(first, "voice-one");
        replay(second, "voice-two");
        Equal("voice-one", first.LastVoice);
        Equal("voice-two", second.LastVoice);
        Equal(1, first.Calls);
        Equal(1, second.Calls);
    }

    public static void CallbackOnlyDeclarationPreservesTheOriginalStartedCallback()
    {
        var method = VoicePlaybackMethodPolicy.ResolveSetVoice(typeof(CallbackOnly), typeof(Action), out bool callback);
        True(callback);
        True(VoicePlaybackMethodPolicy.BindLegacyReplay<CallbackOnly>(method, callback) is null,
            "Callback-capable starts must never acquire a synthetic legacy replay.");
        var receiver = new CallbackOnly();
        int starts = 0;
        Action onStarted = () => starts++;
        // Simulate the original caller using the selected declaration, with its
        // actual callback. The compatibility adapter neither retains nor replaces it.
        var officialStart = method.CreateDelegate<Action<CallbackOnly, string, Action>>();
        officialStart(receiver, "callback-voice", onStarted);
        Equal("callback-voice", receiver.LastVoice);
        True(ReferenceEquals(onStarted, receiver.SeenCallback));
        Equal(1, starts);
    }

    public static void CallbackDeclarationWinsWithoutCallingTheAvailableLegacyOverload()
    {
        var method = VoicePlaybackMethodPolicy.ResolveSetVoice(typeof(Both), typeof(Action), out bool callback);
        True(callback);
        True(VoicePlaybackMethodPolicy.BindLegacyReplay<Both>(method, callback) is null);
        var receiver = new Both();
        int starts = 0;
        method.CreateDelegate<Action<Both, string, Action>>()(receiver, "modern-voice", () => starts++);
        Equal(0, receiver.LegacyCalls);
        Equal(1, receiver.CallbackCalls);
        Equal(1, starts);
    }

    public static void UnknownDeclarationsAndMismatchedReplayBindingsNeverInvoke()
    {
        bool missing = false;
        try { VoicePlaybackMethodPolicy.ResolveSetVoice(typeof(Unknown), typeof(Action), out _); }
        catch (MissingMethodException) { missing = true; }
        True(missing, "An unknown callback declaration must fail closed.");
        Equal(0, Unknown.Calls);

        var callback = VoicePlaybackMethodPolicy.ResolveSetVoice(typeof(CallbackOnly), typeof(Action), out _);
        bool rejected = false;
        try { VoicePlaybackMethodPolicy.BindLegacyReplay<CallbackOnly>(callback, usesStartedCallback: false); }
        catch (ArgumentException) { rejected = true; }
        True(rejected, "A callback method cannot be rebound as a single-argument replay.");

        var legacy = VoicePlaybackMethodPolicy.ResolveSetVoice(typeof(Legacy), typeof(Action), out _);
        rejected = false;
        try { VoicePlaybackMethodPolicy.BindLegacyReplay<Both>(legacy, usesStartedCallback: false); }
        catch (ArgumentException) { rejected = true; }
        True(rejected, "A replay cannot be bound to a different receiver declaration.");
    }

    public static void OwnedCallbackPreloadObservationFailureStillRunsTheCurrentNativeStart()
    {
        var method = VoicePlaybackMethodPolicy.ResolveSetVoice(typeof(CallbackOnly), typeof(Action), out bool callback);
        var officialStart = method.CreateDelegate<Action<CallbackOnly, string, Action>>();
        foreach (string boundary in new[] { "manager", "cache" })
        {
            var receiver = new CallbackOnly();
            int starts = 0, nativePreloads = 0;
            bool result = false; // Already assigned after identifying the owned iterator.
            bool runOriginal;
            try { ThrowObservationFailure(boundary); runOriginal = false; }
            catch (InvalidOperationException)
            {
                runOriginal = VoicePlaybackMethodPolicy.AllowNativePreloadAfterFailure(
                    callback, owned: true, ref result);
            }
            if (runOriginal)
            {
                nativePreloads++;
                officialStart(receiver, "native-after-" + boundary, () => starts++);
                result = true; // The original MoveNext supplies its own return value.
            }
            Equal(1, nativePreloads);
            Equal(1, starts);
            Equal("native-after-" + boundary, receiver.LastVoice);
            True(result);
        }
    }

    public static void OwnedLegacyPreloadFailureStillVetoesReplayAndUnownedCallsPassThrough()
    {
        var method = VoicePlaybackMethodPolicy.ResolveSetVoice(typeof(Legacy), typeof(Action), out bool callback);
        Action<Legacy, string> replay = VoicePlaybackMethodPolicy.BindLegacyReplay<Legacy>(method, callback)
            ?? throw new InvalidOperationException("Legacy replay was not bound.");
        var receiver = new Legacy();
        bool result = true;
        bool runOriginal;
        try { ThrowObservationFailure("cache"); runOriginal = true; }
        catch (InvalidOperationException)
        {
            runOriginal = VoicePlaybackMethodPolicy.AllowNativePreloadAfterFailure(
                callback, owned: true, ref result);
        }
        if (runOriginal) replay(receiver, "unsafe-replay");
        True(!runOriginal && !result);
        Equal(0, receiver.Calls);

        result = true;
        True(VoicePlaybackMethodPolicy.AllowNativePreloadAfterFailure(
            usesStartedCallback: false, owned: false, ref result));
        True(result, "An unrelated native iterator's result must be left unchanged.");
    }

    public static void TickEntryFailureIsReportedOnceAndOtherUpdatesContinue()
    {
        var guard = new VoicePlaybackTickGuard();
        var gate = new VoicePlaybackGate();
        gate.Begin("aavt/voice/rukari-import/" + new string('a', 64), 0, loadingTimeout: 10);
        int ticks = 0, reports = 0, pageUpdates = 0, probeUpdates = 0;
        var failure = new MissingMethodException("Missing SetVoice declaration at tick entry.");
        Action tick = () => { ticks++; throw failure; };
        Action<Exception> report = error =>
        {
            reports++;
            True(ReferenceEquals(failure, error));
            True(guard.Suspended, "The guard must close before cleanup/reporting executes.");
            gate.Cancel("tick-entry-failure");
        };
        for (int frame = 0; frame < 100; frame++)
        {
            guard.Run(tick, report);
            pageUpdates++;
            probeUpdates++;
        }
        Equal(1, ticks);
        Equal(1, reports);
        Equal(100, pageUpdates);
        Equal(100, probeUpdates);
        True(!gate.Active && !gate.BlocksAdvance(false, false));
    }

    public static void FaultReportingFailureCannotReopenTheTickOrEscapeTheGuard()
    {
        var guard = new VoicePlaybackTickGuard();
        int ticks = 0, reports = 0;
        Action tick = () => { ticks++; throw new MissingMethodException(); };
        Action<Exception> report = _ => { reports++; throw new InvalidOperationException("Cleanup failed."); };
        guard.Run(tick, report);
        guard.Run(tick, report);
        True(guard.Suspended);
        Equal(1, ticks);
        Equal(1, reports);
    }

    public static void SuccessfulTicksRemainEnabledAcrossFrames()
    {
        var guard = new VoicePlaybackTickGuard();
        int ticks = 0, reports = 0;
        Action tick = () => ticks++;
        Action<Exception> report = _ => reports++;
        for (int frame = 0; frame < 5; frame++) guard.Run(tick, report);
        True(!guard.Suspended);
        Equal(5, ticks);
        Equal(0, reports);
    }

    private static void True(bool value, string? message = null)
    { if (!value) throw new InvalidOperationException(message ?? "Expected true."); }
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, actual {actual}."); }
    private static void ThrowObservationFailure(string boundary) =>
        throw new InvalidOperationException("Preload " + boundary + " observation failed.");

    public sealed class Legacy
    {
        public string LastVoice = string.Empty;
        public int Calls;
        public void SetVoice(string name) { LastVoice = name; Calls++; }
    }
    public sealed class CallbackOnly
    {
        public string LastVoice = string.Empty;
        public Action? SeenCallback;
        public void SetVoice(string name, Action? onStarted = null)
        { LastVoice = name; SeenCallback = onStarted; onStarted?.Invoke(); }
    }
    public sealed class Both
    {
        public int LegacyCalls, CallbackCalls;
        public void SetVoice(string name) => LegacyCalls++;
        public void SetVoice(string name, Action? onStarted = null)
        { CallbackCalls++; onStarted?.Invoke(); }
    }
    public sealed class Unknown
    {
        public static int Calls;
        public void SetVoice(string name, object callback) => Calls++;
    }
}
