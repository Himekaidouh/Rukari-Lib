using System.Diagnostics;
using System.Reflection;
using Rukari.CharacterVoice.Core;
using BepInEx.Configuration;
using FlatData;
using HarmonyLib;

namespace Rukari.CharacterVoice.Runtime;

/// <summary>
/// Owns only aavt/voice requests. Native coroutines load clips and native SetVoice plays them.
/// All retained state is managed; every engine object is reacquired on the main thread.
/// </summary>
internal static class VoicePlaybackRuntime
{
    private const int MaximumUnfinishedLoads = 128;
    private static readonly VoicePlaybackGate Gate = new();
    private static readonly HashSet<string> NativeLoads = new(StringComparer.Ordinal);
    private static readonly Queue<string> LoadSweep = new();
    private static readonly HashSet<string> Reported = new(StringComparer.Ordinal);
    private static int _mainThreadId;
    private static int _managerInstanceId;
    private static int _ownerPlayerInstanceId;
    private static bool _ownerPreview;
    private static bool _installed;
    private static bool _replayingLoaded;
    private static bool _readingNativeVoiceState;
    private static bool _ownsAudioSource;
    private static bool _pollingSuspended;
    private static bool _preloadAdmissionSuspended;
    // Cached accessors for the compiler-generated preload state machine (resolved in Install).
    private static PropertyInfo? _preloadState;
    private static PropertyInfo? _preloadVoice;
    private static PropertyInfo? _preloadManager;
    private static double _loadingTimeout = 10;
    private static double _playbackTimeout = 300;
    private static ConfigEntry<float>? _postPlaybackDelay;

    internal static float PostPlaybackDelaySeconds =>
        (float)VoicePlaybackDelayPolicy.Normalize(_postPlaybackDelay?.Value ?? 0);

    internal static void SavePostPlaybackDelay(float seconds)
    {
        if (!IsMainThread() || _postPlaybackDelay is null)
            throw new InvalidOperationException("Voice playback settings are unavailable.");
        _postPlaybackDelay.Value = (float)VoicePlaybackDelayPolicy.Normalize(seconds);
        Plugin.Logger.LogInfo($"[voice] Global post-playback delay saved: {PostPlaybackDelaySeconds:0.0}s; effective from next voice request; official AUTO delay unchanged.");
    }

    public static bool Install(ConfigFile config)
    {
        if (_installed) return true;
        if (!config.Bind("Voice", "Enabled", true,
                "Enable owned voice directives and wait for their audio. Restart required.").Value) return false;
        _mainThreadId = Environment.CurrentManagedThreadId;
        _postPlaybackDelay = config.Bind("Voice", "PostPlaybackDelaySeconds", 0f,
            new ConfigDescription("Extra wait after an owned voice finishes, including its trailing silence. Shared by all Mod voice bindings in this profile; applies from the next playback. Official AUTO timing is unchanged.",
                new AcceptableValueRange<float>(0f, VoicePlaybackDelayPolicy.MaximumSeconds)));
        _loadingTimeout = config.Bind("Voice", "LoadingTimeoutSeconds", 10f,
            new ConfigDescription("Release the dialogue wait when an owned voice cannot finish loading.",
                new AcceptableValueRange<float>(2f, 60f))).Value;
        _playbackTimeout = config.Bind("Voice", "PlaybackTimeoutSeconds", 300f,
            new ConfigDescription("Safety release for a voice that never reports as finished. Set this above the longest voice clip; it only fires when the engine stops reporting playback progress.",
                new AcceptableValueRange<float>(15f, 3600f))).Value;
        var harmony = new Harmony(Plugin.Guid + ".voice-playback");
        try
        {
            MethodInfo setVoice = Require(typeof(AudioManager), "SetVoice", typeof(string));
            MethodInfo voicePlaying = AccessTools.PropertyGetter(typeof(AudioManager), "isVoicePlaying")
                ?? throw new MissingMethodException("AudioManager.get_isVoicePlaying");
            MethodInfo touch = Require(typeof(Test), "OnTouchAreaClicked");
            MethodInfo advance = Require(typeof(Test), "AdvanceScenario", typeof(bool), typeof(IScenarioScriptExcel));
            // The preload coroutine's state machine is compiler-generated, so its name carries a compiler
            // counter that moves between AA builds (AA 1.0 ships _CoTryPreloadVoice_d__72, the 0.8.9 build
            // _d__74). Resolve the type by name prefix and read its members through cached accessors; the
            // member names themselves (__1__state, voiceIdentifier, __4__this) are identical in both.
            Type preloadIterator = ResolvePreloadIterator();
            _preloadState = RequireIteratorProperty(preloadIterator, "__1__state", typeof(int));
            _preloadVoice = RequireIteratorProperty(preloadIterator, "voiceIdentifier", typeof(string));
            _preloadManager = RequireIteratorProperty(preloadIterator, "__4__this", typeof(ScenarioResourceManager));
            MethodInfo preloadMoveNext = Require(preloadIterator, "MoveNext");

            harmony.Patch(setVoice,
                prefix: new HarmonyMethod(typeof(VoicePlaybackRuntime), nameof(SetVoicePrefix)),
                postfix: new HarmonyMethod(typeof(VoicePlaybackRuntime), nameof(SetVoicePostfix)));
            // 0.8.8 script.json has exactly one method at RVA 0x83EC00. The automatic
            // wait predicate calls this address; it is not an unrelated folded getter.
            harmony.Patch(voicePlaying, postfix: new HarmonyMethod(typeof(VoicePlaybackRuntime), nameof(VoicePlayingPostfix)));
            // 0.8.8 script.json has exactly one method at RVA 0x72B3D0. The save
            // preloader inlines the iterator factory, so its MoveNext is the shared
            // admission boundary. Native completion uses Dictionary.Add without a
            // second cache check; all owned requests must claim here before yielding.
            harmony.Patch(preloadMoveNext,
                prefix: new HarmonyMethod(typeof(VoicePlaybackRuntime), nameof(PreloadMoveNextPrefix)));
            harmony.Patch(touch, prefix: new HarmonyMethod(typeof(VoicePlaybackRuntime), nameof(TouchPrefix)));
            var advancePrefix = new HarmonyMethod(typeof(VoicePlaybackRuntime), nameof(AdvancePrefix))
            {
                before = new[] { "rukari.moreeffects.player-command-observation", "local.azurearchive.videotools.player-command-observation" }
            };
            harmony.Patch(advance, prefix: advancePrefix);
            // Test.Clear is deliberately NOT patched. Its one parameter is Nullable<uint>; the generated
            // Il2CppInterop trampoline for it throws NullReferenceException inside Buffer.Memmove on every
            // call ("(il2cpp -> managed) Clear(IntPtr, IntPtr, Il2CppMethodInfo*)"), even though the Harmony
            // prefix itself takes no arguments. Measured 8 such native->managed trampoline failures in one
            // session, all on Clear. A player clear is instead covered by the bounded wait: every phase now
            // releases on its own deadline, so a stuck gate can no longer block dialogue advance.
            _installed = true;
            Plugin.Logger.LogInfo("Voice playback candidate installed; scope=aavt/voice/; shared native preload admission/cache/source; loading and manual advance gate; bounded playback deadline; Clear left unpatched; lifetimePending=true.");
            return true;
        }
        catch (Exception ex)
        {
            harmony.UnpatchSelf();
            ReportFailure("install", ex);
            return false;
        }
    }

    public static bool SetVoicePrefix(string name)
    {
        if (!IsMainThread()) return true;
        bool owned = name?.StartsWith(VoiceDirectivePolicy.NativeIdentifierPrefix, StringComparison.Ordinal) == true;
        if (_replayingLoaded) return owned && Gate.Active && string.Equals(name, Gate.VoiceIdentifier, StringComparison.Ordinal);
        if (!owned)
        {
            ResetOnMainThread("native-voice-replaced-owned-request");
            return true;
        }
        if (_pollingSuspended) return false;
        try
        {
            ResetOnMainThread("new-voice-request");
            if (string.IsNullOrEmpty(name) || !VoiceDirectivePolicy.TryDecodeNativeIdentifier(name, out _))
            {
                ReportOnce("invalid-key", "Owned voice skipped: malformed identifier.", true);
                return false;
            }
            Test player = Test.Instance;
            AudioManager audio = AudioManager.Instance;
            ScenarioResourceManager manager = ScenarioResourceManager.Instance;
            if (ReferenceEquals(player, null) || ReferenceEquals(audio, null) || ReferenceEquals(manager, null))
            {
                ReportOnce("missing-owner", "Owned voice skipped: player or audio/resource manager unavailable.", true);
                return false;
            }
            ObserveManager(manager);
            _ownerPlayerInstanceId = player.GetInstanceID();
            _ownerPreview = player.previewMode;
            long sequence = Gate.Begin(name, Now(), _loadingTimeout, _ownerPreview ? 0 : PostPlaybackDelaySeconds);
            if (IsCached(manager, name))
            {
                Gate.MarkStarting(sequence, Now());
                return true;
            }
            if (!manager.TryGetVoiceOverridePath(name, out string path) || !File.Exists(path))
            {
                FailCurrent("voice-file-not-found");
                return false;
            }
            if (!NativeLoads.Contains(name))
            {
                if (NativeLoads.Count >= MaximumUnfinishedLoads)
                {
                    FailCurrent("unfinished-load-limit");
                    return false;
                }
                // The native manager owns the coroutine. No IEnumerator/Coroutine/clip
                // wrapper is retained. The shared MoveNext prefix claims this request
                // as well as native editor/save preload requests before either yields.
                manager.StartCoroutine(manager.CoTryPreloadVoice(name));
            }
            ReportOnce("cold-load", "Owned voice cold-load callback reached; waiting for native voice cache before playback; lifetimePending=true.");
            return false;
        }
        catch (Exception ex)
        {
            FailCurrent("start-error");
            ReportFailure("start", ex);
            return false;
        }
    }

    public static bool PreloadMoveNextPrefix(
        object __instance,
        ref bool __result)
    {
        if (!IsMainThread() || _preloadAdmissionSuspended) return true;
        bool owned = false;
        try
        {
            // Resume states finish the already admitted native request unchanged.
            if (ReadPreloadState(__instance) != 0) return true;
            string? name = ReadPreloadVoice(__instance);
            owned = name?.StartsWith(VoiceDirectivePolicy.NativeIdentifierPrefix, StringComparison.Ordinal) == true;
            if (!owned) return true;
            __result = false;
            if (_pollingSuspended) return false;
            if (string.IsNullOrEmpty(name) || !VoiceDirectivePolicy.TryDecodeNativeIdentifier(name, out _))
            {
                ReportOnce("invalid-preload-key", "Owned voice preload skipped: malformed identifier.", true);
                return false;
            }

            ScenarioResourceManager manager = ReadPreloadManager(__instance);
            if (ReferenceEquals(manager, null)) throw new InvalidOperationException("Native voice preloader has no resource manager.");
            ObserveManager(manager);
            if (IsCached(manager, name))
            {
                NativeLoads.Remove(name);
                return false;
            }
            if (NativeLoads.Contains(name)) return false;
            SweepOneCompletedLoad(manager);
            if (NativeLoads.Count >= MaximumUnfinishedLoads)
            {
                if (string.Equals(name, Gate.VoiceIdentifier, StringComparison.Ordinal)) FailCurrent("unfinished-load-limit");
                ReportOnce("preload-limit", "Owned voice preload skipped: unfinished native request limit reached.", true);
                return false;
            }
            NativeLoads.Add(name);
            LoadSweep.Enqueue(name);
            ReportOnce("preload-admission", "Owned voice shared native preload admission reached; duplicate editor/save/runtime requests are coalesced; lifetimePending=true.");
            return true;
        }
        catch (Exception ex)
        {
            // Stop issuing mod requests and stop retrying a failed iterator boundary.
            // Later native calls, including in-flight resume states, retain their
            // original behavior; no coroutine wrapper or native object is retained.
            _preloadAdmissionSuspended = true;
            _pollingSuspended = true;
            FailCurrent("preload-admission-error");
            ReportFailure("preload-admission", ex);
            if (!owned) return true;
            __result = false;
            return false;
        }
    }

    public static void SetVoicePostfix(string name)
    {
        if (!IsMainThread() || Gate.Phase != VoicePlaybackPhase.Starting
            || !string.Equals(name, Gate.VoiceIdentifier, StringComparison.Ordinal)) return;
        try
        {
            _ownsAudioSource = true;
            AudioManager audio = AudioManager.Instance;
            if (!ReferenceEquals(audio, null)) ObserveOwnedPlayback(ReadNativePlaying(audio));
            ReportOnce("playback-start", "Owned voice native cached-play callback reached; automatic and ordinary-click waits active; lifetimePending=true.");
        }
        catch (Exception ex)
        {
            FailCurrent("playback-observation-error");
            ReportFailure("playback-start", ex);
        }
    }

    public static void VoicePlayingPostfix(ref bool __result)
    {
        if (!IsMainThread() || _readingNativeVoiceState || !Gate.Active) return;
        try
        {
            if (!IsOwnerCurrent(out _)) { ResetOnMainThread("wait-owner-changed"); return; }
            // Use the original predicate value, never our patched getter. The false -> delay transition must
            // happen in this very call, before the native coroutine can leave its wait and advance just once.
            __result = ObserveOwnedPlayback(__result);
        }
        catch (Exception ex)
        {
            FailCurrent("wait-observation-error");
            ReportFailure("wait-observation", ex);
        }
    }

    public static bool TouchPrefix() => !ShouldBlockAdvance(false);

    public static bool AdvancePrefix(bool preview)
    {
        if (!IsMainThread()) return true;
        if (preview)
        {
            ResetOnMainThread("preview-advance");
            return true;
        }
        return !ShouldBlockAdvance(true);
    }

    // Test.Clear is intentionally left unpatched; see the install comment for the measured trampoline failure.

    internal static void Stop()
    {
        if (!_installed) return;
        ResetOnMainThread("voice-module-stopped");
        _installed = false;
        _pollingSuspended = true;
        new Harmony(Plugin.Guid + ".voice-playback").UnpatchSelf();
    }

    public static void TickOnMainThread()
    {
        if (!_installed || !IsMainThread() || (!Gate.Active && NativeLoads.Count == 0)) return;
        // Release an expired wait FIRST, before any engine access and before the suspension guard. A gate that
        // is already stuck must be able to time out even after an engine failure stopped normal polling;
        // otherwise the dialogue advance block survives with no recovery path.
        if (Gate.Pending && Gate.Expire(Now()))
        {
            StopOwnedAudio();
            ReportOnce("timeout-" + Gate.LastReason,
                $"Owned voice wait released: {Gate.LastReason}. Native load ownership remains recorded to avoid duplicate requests; restart to retry a failed uncached identifier.", true);
            return;
        }
        if (_pollingSuspended)
        {
            ReleaseExpiredWhileSuspended();
            return;
        }
        try
        {
            ScenarioResourceManager manager = ScenarioResourceManager.Instance;
            if (ReferenceEquals(manager, null))
            {
                ResetOnMainThread("resource-manager-unavailable");
                return;
            }
            ObserveManager(manager);
            SweepOneCompletedLoad(manager);
            if (!Gate.Active) return;
            if (!IsOwnerCurrent(out _))
            {
                ResetOnMainThread("player-context-changed");
                return;
            }
            long sequence = Gate.RequestSequence;
            string identifier = Gate.VoiceIdentifier!;
            if (Gate.Phase == VoicePlaybackPhase.Loading)
            {
                if (!IsCached(manager, identifier)) return;
                NativeLoads.Remove(identifier);
                if (!Gate.IsCurrent(sequence, identifier)) return;
                AudioManager audio = AudioManager.Instance;
                if (ReferenceEquals(audio, null)) { FailCurrent("audio-manager-unavailable"); return; }
                Gate.MarkStarting(sequence, Now());
                _replayingLoaded = true;
                try { audio.SetVoice(identifier); }
                finally { _replayingLoaded = false; }
                return;
            }
            AudioManager currentAudio = AudioManager.Instance;
            if (ReferenceEquals(currentAudio, null)) { FailCurrent("audio-manager-unavailable"); return; }
            ObserveOwnedPlayback(ReadNativePlaying(currentAudio));
        }
        catch (Exception ex)
        {
            FailCurrent("tick-error");
            // A broken IL2CPP cache boundary must not be retried every frame.
            // Pending native preloaders may still finish, but none can start audio.
            _pollingSuspended = true;
            ReportFailure("tick", ex);
            ReportOnce("polling-suspended", "Owned voice playback suspended after an engine access failure; native audio remains available; remaining waits are still released by their own bounded timeouts; restart required to retry playback.", true);
            // Do not leave the dialogue blocked just because polling stopped: release the current wait now.
            ReleaseExpiredWhileSuspended();
        }
    }

    /// <summary>
    /// Releases a wait while polling is suspended, using managed state only. No engine object is touched, so
    /// this cannot re-enter the failing boundary; the audio source is left to the engine.
    /// </summary>
    private static void ReleaseExpiredWhileSuspended()
    {
        if (!Gate.Active) return;
        if (!Gate.Expire(Now())) return;
        _ownsAudioSource = false;
        ReportOnce("suspended-release-" + Gate.LastReason,
            $"Owned voice wait released while polling is suspended: {Gate.LastReason}. Dialogue advance is unblocked without touching the audio engine; restart to retry playback.", true);
    }

    public static void ResetOnMainThread(string reason)
    {
        if (!IsMainThread() || (!Gate.Active && !_ownsAudioSource)) return;
        Gate.Cancel(reason);
        StopOwnedAudio();
        _ownerPlayerInstanceId = 0;
        // Do not forget unfinished native requests on a scene/selection change:
        // their late completion may fill the cache but may never restart playback.
    }

    private static bool ShouldBlockAdvance(bool resetPreview)
    {
        if (!IsMainThread() || !Gate.Active) return false;
        try
        {
            if (!IsOwnerCurrent(out bool playerIsPreview))
            {
                ResetOnMainThread("advance-owner-changed");
                return false;
            }
            if (playerIsPreview)
            {
                if (resetPreview) ResetOnMainThread("editor-preview-advance");
                return false;
            }
            // The native coroutine can observe audio completion before our next Update.
            // Refresh here so its single AdvanceScenario call is never swallowed by
            // yesterday's Playing state with no coroutine left to retry it.
            if (Gate.Phase == VoicePlaybackPhase.Playing)
            {
                AudioManager audio = AudioManager.Instance;
                if (ReferenceEquals(audio, null))
                {
                    FailCurrent("audio-manager-unavailable");
                    return false;
                }
                ObserveOwnedPlayback(ReadNativePlaying(audio));
            }
            else if (Gate.Phase == VoicePlaybackPhase.PostPlaybackDelay) ObserveOwnedPlayback(false);
            return Gate.BlocksAdvance(false, false);
        }
        catch (Exception ex)
        {
            ResetOnMainThread("advance-context-error");
            ReportFailure("advance", ex);
            return false;
        }
    }

    private static bool IsOwnerCurrent(out bool preview)
    {
        preview = false;
        Test player = Test.Instance;
        if (ReferenceEquals(player, null) || player.GetInstanceID() != _ownerPlayerInstanceId) return false;
        preview = player.previewMode;
        return preview == _ownerPreview;
    }

    private static bool ReadNativePlaying(AudioManager audio)
    {
        _readingNativeVoiceState = true;
        try { return audio.isVoicePlaying; }
        finally { _readingNativeVoiceState = false; }
    }

    private static bool ObserveOwnedPlayback(bool nativePlaying)
    {
        VoicePlaybackPhase previous = Gate.Phase;
        bool wait = Gate.ObserveNativeWait(nativePlaying, Now(), _playbackTimeout);
        if (Gate.Phase is VoicePlaybackPhase.PostPlaybackDelay or VoicePlaybackPhase.Completed)
            _ownsAudioSource = false; // The clip has ended; do not stop a later source during the quiet wait.
        if (previous == VoicePlaybackPhase.Playing && Gate.Phase != previous)
            Plugin.Logger.LogInfo($"[voice] Audio finished; request={Gate.RequestSequence}; postDelaySeconds={Gate.PostPlaybackDelaySeconds:0.0}; phase={Gate.Phase}; preview={_ownerPreview}.");
        return wait;
    }

    private static bool IsCached(ScenarioResourceManager manager, string identifier) =>
        manager._loadedCustomVoices.ContainsKey(identifier);

    private static void ObserveManager(ScenarioResourceManager manager)
    {
        int instanceId = manager.GetInstanceID();
        if (_managerInstanceId == instanceId) return;
        if (_managerInstanceId != 0) ResetOnMainThread("resource-manager-replaced");
        _managerInstanceId = instanceId;
        NativeLoads.Clear();
        LoadSweep.Clear();
    }

    private static void SweepOneCompletedLoad(ScenarioResourceManager manager)
    {
        if (LoadSweep.Count == 0) return;
        string identifier = LoadSweep.Dequeue();
        if (!NativeLoads.Contains(identifier)) return;
        if (IsCached(manager, identifier)) NativeLoads.Remove(identifier);
        else LoadSweep.Enqueue(identifier);
    }

    private static void FailCurrent(string reason)
    {
        if (!Gate.Fail(Gate.RequestSequence, reason)) return;
        StopOwnedAudio();
        ReportOnce("failure-" + reason, $"Owned voice skipped and dialogue wait released: {reason}.", true);
    }

    private static void StopOwnedAudio()
    {
        if (!_ownsAudioSource) return;
        _ownsAudioSource = false;
        try
        {
            AudioManager audio = AudioManager.Instance;
            if (ReferenceEquals(audio, null)) return;
            var source = audio.voiceSource;
            if (!ReferenceEquals(source, null)) source.Stop();
        }
        catch (Exception ex) { ReportFailure("stop-owned-audio", ex); }
    }

    /// <summary>
    /// Finds the compiler-generated <c>CoTryPreloadVoice</c> state machine. Its trailing number is a
    /// compiler counter, not part of the game's API, so it must never be compiled into a patch.
    /// </summary>
    private static Type ResolvePreloadIterator()
    {
        const string prefix = "_CoTryPreloadVoice_d__";
        Type? match = null;
        foreach (Type candidate in typeof(ScenarioResourceManager).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (!candidate.Name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (match is not null)
                throw new AmbiguousMatchException($"More than one {prefix} state machine on {typeof(ScenarioResourceManager).FullName}.");
            match = candidate;
        }
        return match ?? throw new MissingMemberException(typeof(ScenarioResourceManager).FullName, prefix);
    }

    private static PropertyInfo RequireIteratorProperty(Type iterator, string name, Type type)
    {
        PropertyInfo? property = iterator.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (property is null || property.PropertyType != type) throw new MissingMemberException(iterator.FullName, name);
        return property;
    }

    private static int ReadPreloadState(object iterator) => (int)_preloadState!.GetValue(iterator)!;
    private static string? ReadPreloadVoice(object iterator) => (string?)_preloadVoice!.GetValue(iterator);
    private static ScenarioResourceManager ReadPreloadManager(object iterator) =>
        (ScenarioResourceManager)_preloadManager!.GetValue(iterator)!;

    private static MethodInfo Require(Type type, string name, params Type[] arguments) =>
        AccessTools.Method(type, name, arguments) ?? throw new MissingMethodException(type.FullName, name);

    private static bool IsMainThread() => _mainThreadId != 0 && Environment.CurrentManagedThreadId == _mainThreadId;
    private static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private static void ReportFailure(string boundary, Exception ex) =>
        ReportOnce("exception-" + boundary,
            $"Voice playback candidate boundary failed: {boundary}; {ex.GetType().Name}: {ex.Message}; lifetimePending=true.", true);

    private static void ReportOnce(string key, string message, bool warning = false)
    {
        if (Reported.Count >= 64 || !Reported.Add(key)) return;
        if (warning) Plugin.Logger.LogWarning(message);
        else Plugin.Logger.LogInfo(message);
    }
}
