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
    private static readonly VoicePlaybackVolumeAdmission VolumeAdmission = new();
    private static readonly VoicePlaybackVolumeLease VolumeLease = new();
    private static readonly VoicePlaybackVolumeStartQualification VolumeStartQualification = new();
    private static readonly HashSet<string> NativeLoads = new(StringComparer.Ordinal);
    private static readonly Queue<string> LoadSweep = new();
    private static readonly HashSet<string> Reported = new(StringComparer.Ordinal);
    private static int _mainThreadId;
    private static int _managerInstanceId;
    private static int _ownerPlayerInstanceId;
    private static bool _ownerPreview;
    private static bool _installed;
    private static bool _usesNativeStartedCallback;
    private static Action<AudioManager, string>? _legacySetVoice;
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
    private static ConfigEntry<float>? _volumePercent;
    private static float _requestVolumePercent = VoicePlaybackVolumePolicy.DefaultPercent;
    private static int _volumeLogCount;
    private static long _volumeConsideredRequest;

    internal static float VolumePercent => VoicePlaybackVolumePolicy.Normalize(
        _volumePercent?.Value ?? VoicePlaybackVolumePolicy.DefaultPercent);

    internal static void SaveVolumePercent(float percent)
    {
        if (!IsMainThread() || _volumePercent is null)
            throw new InvalidOperationException("Voice playback settings are unavailable.");
        _volumePercent.Value = VoicePlaybackVolumePolicy.Normalize(percent);
        Plugin.Logger.LogInfo($"[voice] Global Mod voice strength saved: {VolumePercent:0}%; midpoint 50% preserves official output; effective from next owned voice request.");
    }

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
        _volumePercent = config.Bind("Voice", "VolumePercent", VoicePlaybackVolumePolicy.DefaultPercent,
            new ConfigDescription("Strength of imported Mod voice playback in this profile: 0% mute, 50% unchanged, 100% up to 2x the current official voice source level (clamped to the source limit). The first non-default request on each source observes volume without writing; a later different request may apply it. An in-flight request retains its setting; an existing zero baseline stays zero. Native getter, write and restoration each require validation.",
                new AcceptableValueRange<float>(0f, VoicePlaybackVolumePolicy.MaximumPercent)));
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
            MethodInfo setVoice = VoicePlaybackMethodPolicy.ResolveSetVoice(typeof(AudioManager),
                typeof(Il2CppSystem.Action), out _usesNativeStartedCallback);
            _legacySetVoice = VoicePlaybackMethodPolicy.BindLegacyReplay<AudioManager>(
                setVoice, _usesNativeStartedCallback);
            MethodInfo voicePlaying = AccessTools.PropertyGetter(typeof(AudioManager), "isVoicePlaying")
                ?? throw new MissingMethodException("AudioManager.get_isVoicePlaying");
            MethodInfo touch = Require(typeof(Test), "OnTouchAreaClicked");
            MethodInfo advance = Require(typeof(Test), "AdvanceScenario", typeof(bool), typeof(IScenarioScriptExcel));
            // The preload coroutine's state machine is compiler-generated, so its name carries a compiler
            // counter that moves between AA builds (AAfix4 and the 2026-10-01 release both use 104).
            // Resolve by prefix and validate the member types; do not bind to that incidental counter.
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
            Plugin.Logger.LogInfo($"Voice playback candidate installed; scope=aavt/voice/; setVoice-uses-native-started-callback={_usesNativeStartedCallback}; legacy-replay-bound={_legacySetVoice is not null}; callback wrapper is not read or retained; shared native preload admission/cache/source; bounded loading/manual/playback gate; caller-tick-fuse=true; Clear left unpatched; lifetimePending=true.");
            return true;
        }
        catch (Exception ex)
        {
            _legacySetVoice = null;
            harmony.UnpatchSelf();
            ReportFailure("install", ex);
            return false;
        }
    }

    public static bool SetVoicePrefix(string __0)
    {
        string name = __0;
        if (!IsMainThread()) return true;
        bool owned = name?.StartsWith(VoiceDirectivePolicy.NativeIdentifierPrefix, StringComparison.Ordinal) == true;
        if (!owned)
        {
            // Restore before the original SetVoice replaces the shared source, even if
            // normal completion already released audio/wait ownership.
            RestoreOwnedVolume();
            ResetOnMainThread("native-voice-replaced-owned-request");
            return true;
        }
        if (_replayingLoaded) return Gate.Active && string.Equals(name, Gate.VoiceIdentifier, StringComparison.Ordinal);
        if (_pollingSuspended)
        {
            if (_usesNativeStartedCallback) ResetOnMainThread("suspended-owned-call-passed-to-native");
            return _usesNativeStartedCallback;
        }
        try
        {
            ResetOnMainThread("new-voice-request");
            if (string.IsNullOrEmpty(name) || !VoiceDirectivePolicy.TryDecodeNativeIdentifier(name, out _))
            {
                ReportOnce("invalid-key", "Owned voice skipped: malformed identifier.", true);
                return _usesNativeStartedCallback;
            }
            Test player = Test.Instance;
            AudioManager audio = AudioManager.Instance;
            ScenarioResourceManager manager = ScenarioResourceManager.Instance;
            if (ReferenceEquals(player, null) || ReferenceEquals(audio, null) || ReferenceEquals(manager, null))
            {
                ReportOnce("missing-owner", "Owned voice skipped: player or audio/resource manager unavailable.", true);
                return _usesNativeStartedCallback;
            }
            ObserveManager(manager);
            _ownerPlayerInstanceId = player.GetInstanceID();
            _ownerPreview = player.previewMode;
            long sequence = Gate.Begin(name, Now(), _loadingTimeout, _ownerPreview ? 0 : PostPlaybackDelaySeconds);
            VolumeStartQualification.Begin(sequence);
            _requestVolumePercent = VolumePercent; // One managed snapshot, including cold-load retries.
            if (_usesNativeStartedCallback)
            {
                // A cached clip may start inside the original SetVoice call, so its
                // postfix need not expose a quiet frame. For non-default volume only,
                // observe the already-used raw predicate before handing over. Quiet
                // here permits the later playing edge; existing playback here does
                // not authorize volume and must become quiet after this call instead.
                if (!VoicePlaybackVolumePolicy.IsNeutral(_requestVolumePercent)
                    && !VolumeAdmission.IsDisabled)
                {
                    try
                    {
                        if (!ReadNativePlaying(audio))
                            VolumeStartQualification.Observe(sequence, false);
                    }
                    catch (Exception ex)
                    {
                        VolumeAdmission.Disable();
                        LogVolume("pre-start-observation-fault=" + ex.GetType().Name
                            + "; volume-subfeature-disabled", sequence, 0, null);
                    }
                }
                // fix6 owns the asynchronous start and the supplied onStarted callback.
                // Never swallow that call or retain its Action wrapper for a later replay.
                Gate.MarkStarting(sequence, Now(), _loadingTimeout);
                return true;
            }
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
            return _usesNativeStartedCallback;
        }
    }

    public static bool PreloadMoveNextPrefix(
        object __instance,
        ref bool __result)
    {
        if (!IsMainThread() || !VoicePlaybackMethodPolicy.ShouldInterceptPreload(
                _usesNativeStartedCallback, _pollingSuspended, _preloadAdmissionSuspended)) return true;
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
            // fix6 still owns this start and its onStarted callback. An observation
            // fault must pass through this already-entered native preload as well
            // as the later calls covered by the suspended admission flag.
            return VoicePlaybackMethodPolicy.AllowNativePreloadAfterFailure(
                _usesNativeStartedCallback, owned, ref __result);
        }
    }

    public static void SetVoicePostfix(string __0)
    {
        string name = __0;
        if (!IsMainThread() || Gate.Phase != VoicePlaybackPhase.Starting
            || !string.Equals(name, Gate.VoiceIdentifier, StringComparison.Ordinal)) return;
        try
        {
            AudioManager audio = AudioManager.Instance;
            if (!ReferenceEquals(audio, null))
            {
                ObserveOwnedPlayback(ReadNativePlaying(audio));
            }
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
        RestoreOwnedVolume();
        if (!_installed) return;
        ResetOnMainThread("voice-module-stopped");
        _installed = false;
        _pollingSuspended = true;
        _legacySetVoice = null;
        new Harmony(Plugin.Guid + ".voice-playback").UnpatchSelf();
    }

    internal static void SuspendAfterTickEntryFailure(Exception error)
    {
        // Release the managed wait before any existing engine cleanup. A missing
        // method at tick entry must not strand the dialogue or retry every frame.
        _pollingSuspended = true;
        _preloadAdmissionSuspended = true;
        _legacySetVoice = null;
        Gate.Cancel("tick-entry-failure");
        VolumeStartQualification.Reset();
        _ownerPlayerInstanceId = 0;
        ReportOnce("tick-entry-suspended",
            $"Owned voice playback disabled after tick entry failed: {error.GetType().Name}: {error.Message}; dialogue wait released; page and diagnostics updates continue; restart required to retry playback.", true);
        // Reuse only the existing, bounded source/volume cleanup. The caller's
        // guard also contains a cleanup failure; no failed tick is re-entered.
        StopOwnedAudio();
    }

    public static void TickOnMainThread()
    {
        if (!_installed || !IsMainThread() || (!Gate.Active && NativeLoads.Count == 0 && !VolumeLease.HasLease)) return;
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
            if (!Gate.Active) { RestoreOwnedVolume(); return; }
            if (!IsOwnerCurrent(out _))
            {
                ResetOnMainThread("player-context-changed");
                return;
            }
            long sequence = Gate.RequestSequence;
            string identifier = Gate.VoiceIdentifier!;
            if (Gate.Phase == VoicePlaybackPhase.Loading)
            {
                // The callback-capable mode is always Starting/Playing and must never
                // synthesize an argument-less replay that drops official onStarted.
                if (_usesNativeStartedCallback) { FailCurrent("unexpected-callback-mode-loading"); return; }
                Action<AudioManager, string> replay = _legacySetVoice
                    ?? throw new MissingMethodException("Validated legacy SetVoice replay binding is unavailable.");
                if (!IsCached(manager, identifier)) return;
                NativeLoads.Remove(identifier);
                if (!Gate.IsCurrent(sequence, identifier)) return;
                AudioManager audio = AudioManager.Instance;
                if (ReferenceEquals(audio, null)) { FailCurrent("audio-manager-unavailable"); return; }
                Gate.MarkStarting(sequence, Now());
                _replayingLoaded = true;
                try { replay(audio, identifier); }
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
    /// Releases the wait using managed state. A confirmed independent volume lease gets its
    /// bounded cleanup, but the failed playback-polling boundary is never retried here.
    /// </summary>
    private static void ReleaseExpiredWhileSuspended()
    {
        if (!Gate.Active) { RestoreOwnedVolume(); return; }
        bool expired = Gate.Expire(Now());
        if (!expired && Gate.Phase != VoicePlaybackPhase.Completed) return;
        RestoreOwnedVolume();
        _ownsAudioSource = false;
        if (!expired) return;
        ReportOnce("suspended-release-" + Gate.LastReason,
            $"Owned voice wait released while polling is suspended: {Gate.LastReason}. Dialogue advance is unblocked without retrying playback polling; restart to retry playback.", true);
    }

    public static void ResetOnMainThread(string reason)
    {
        if (!IsMainThread()) return;
        VolumeStartQualification.Reset();
        if (!Gate.Active && !_ownsAudioSource && !VolumeLease.HasLease) return;
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
        // Only raw observations while Starting feed the qualifier. A synthetic false
        // during PostPlaybackDelay cannot qualify a later request. Legacy cached starts
        // retain their existing behavior; fix6 must see quiet before its first true.
        bool mayAdjustVolume = !_usesNativeStartedCallback || (previous == VoicePlaybackPhase.Starting
            && VolumeStartQualification.Observe(Gate.RequestSequence, nativePlaying));
        if (previous == VoicePlaybackPhase.Starting && nativePlaying)
        {
            _ownsAudioSource = true;
            // The already used native predicate confirms playback. Consider volume at
            // most once for this request, whether start is immediate or asynchronous.
            if (_volumeConsideredRequest != Gate.RequestSequence)
            {
                _volumeConsideredRequest = Gate.RequestSequence;
                if (mayAdjustVolume) ApplyOwnedVolume();
                else if (!VoicePlaybackVolumePolicy.IsNeutral(_requestVolumePercent))
                    LogVolume("adjustment-skipped; native-playing-without-observed-quiet", Gate.RequestSequence, 0, null);
            }
        }
        bool wait = Gate.ObserveNativeWait(nativePlaying, Now(), _playbackTimeout);
        if (Gate.Phase is VoicePlaybackPhase.PostPlaybackDelay or VoicePlaybackPhase.Completed)
        {
            VolumeStartQualification.Reset();
            RestoreOwnedVolume();
            _ownsAudioSource = false; // The clip has ended; do not stop a later source during the quiet wait.
        }
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
        if (!Gate.Fail(Gate.RequestSequence, reason)) { RestoreOwnedVolume(); return; }
        StopOwnedAudio();
        ReportOnce("failure-" + reason, $"Owned voice skipped and dialogue wait released: {reason}.", true);
    }

    private static void StopOwnedAudio()
    {
        // A lease owns its cleanup independently of the clip/wait ownership flags.
        RestoreOwnedVolume();
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

    private static bool IsOwnedStartingRequest(long sequence) => IsMainThread() && _ownsAudioSource
        && Gate.Phase == VoicePlaybackPhase.Starting && Gate.RequestSequence == sequence;

    // Called once when the existing native playing predicate first confirms this
    // request. Default 50% adds no native volume reads/writes. No ongoing polling.
    private static void ApplyOwnedVolume()
    {
        long request = Gate.RequestSequence;
        if (!IsOwnedStartingRequest(request) || VolumeAdmission.IsDisabled
            || VoicePlaybackVolumePolicy.IsNeutral(_requestVolumePercent)) return;
        int identity = 0;
        try
        {
            AudioManager audio = AudioManager.Instance;
            if (ReferenceEquals(audio, null)) throw new InvalidOperationException("Owned audio manager unavailable.");
            var source = audio.voiceSource;
            if (ReferenceEquals(source, null)) throw new InvalidOperationException("Owned voice source unavailable.");
            identity = source.GetInstanceID();
            if (identity == 0) throw new InvalidOperationException("Owned volume source identity unavailable.");
            if (!VolumeAdmission.NeedsObservation(request, identity)) return;
            float baseline = source.volume; // First new boundary: one getter, no write on first request.
            if (!IsOwnedStartingRequest(request)) return;
            bool mayWrite = VolumeAdmission.ObserveBaseline(request, identity, baseline);
            if (VolumeAdmission.IsDisabled)
            {
                LogVolume("baseline-rejected; volume-subfeature-disabled", request, identity, null);
                return;
            }
            if (!mayWrite)
            {
                LogVolume("getter-only; adjustment=deferred-until-next-voice", request, identity, baseline);
                return;
            }
            if (!VolumeLease.TryPrepare(request, identity, baseline, _requestVolumePercent, out float desired)) return;

            // Reacquire and check the current ownership/source just before writing. The
            // previously observed wrapper is never retained beyond this call.
            source = audio.voiceSource;
            if (!IsOwnedStartingRequest(request) || ReferenceEquals(source, null) || source.GetInstanceID() != identity)
            {
                VolumeLease.TryRestore(request, identity, float.NaN, out _);
                LogVolume("write-skipped; owned-request-or-source-changed", request, identity, null);
                return;
            }
            if (!VolumeLease.MarkWriteAttempted(request, identity)) return;
            source.volume = desired;
            float verified = source.volume; // Same getter, independent write/readback validation.
            if (!float.IsFinite(verified) || verified != desired || !VolumeLease.MarkApplied(request, identity, verified))
            {
                VolumeAdmission.Disable();
                LogVolume("write-confirmation-rejected; volume-subfeature-disabled", request, identity, null);
                return;
            }
            LogVolume("write-confirmed; validation-scope=this-volume-call", request, identity, verified);
        }
        catch (Exception ex)
        {
            // Never fail the voice gate or stop playback for an optional volume fault.
            // A possibly effective write retains only primitive intent for later
            // fresh exact comparison. Never retry the native boundary recursively.
            VolumeAdmission.Disable();
            if (!VolumeLease.WriteAttempted) VolumeLease.TryRestore(request, identity, float.NaN, out _);
            LogVolume("application-fault=" + ex.GetType().Name + "; volume-subfeature-disabled", request, identity, null);
        }
    }

    private static void RestoreOwnedVolume()
    {
        if (!VolumeLease.HasLease || !IsMainThread()) return;
        long request = VolumeLease.RequestSequence;
        if (!VolumeLease.WriteAttempted)
        {
            VolumeLease.TryRestore(request, VolumeLease.SourceIdentity, float.NaN, out _);
            return; // Preparing an intent alone must not cause a native volume read/write.
        }
        int identity = 0;
        try
        {
            AudioManager audio = AudioManager.Instance;
            if (ReferenceEquals(audio, null)) throw new InvalidOperationException("Volume cleanup manager unavailable.");
            var source = audio.voiceSource;
            if (ReferenceEquals(source, null)) throw new InvalidOperationException("Volume cleanup source unavailable.");
            identity = source.GetInstanceID();
            if (identity != VolumeLease.SourceIdentity)
            {
                VolumeLease.TryRestore(request, identity, float.NaN, out _);
                LogVolume("restore-skipped; source-changed", request, identity, null);
                return;
            }
            float current = source.volume;
            if (!VolumeLease.TryRestore(request, identity, current, out float baseline))
            {
                LogVolume("restore-skipped; no-write-attempt-or-external-volume-change", request, identity, null);
                return;
            }
            // Best-effort compare-before-restore, not atomic CAS: same-value ABA changes
            // cannot be distinguished without an official change/version boundary.
            source.volume = baseline;
            LogVolume("baseline-restore-setter-returned; restore-readback=not-observed", request, identity, baseline);
        }
        catch (Exception ex)
        {
            VolumeAdmission.Disable();
            VolumeLease.TryRestore(request, identity, float.NaN, out _);
            LogVolume("restore-fault=" + ex.GetType().Name + "; volume-subfeature-disabled", request, identity, null);
        }
    }

    private static void LogVolume(string status, long request, int sourceIdentity, float? observed)
    {
        if (_volumeLogCount >= 32) return;
        _volumeLogCount++;
        // Log only primitive identities/numbers and fixed statuses; logging cannot make
        // an optional volume fault escape into the containing playback postfix.
        try
        {
            string number = observed is float finite && float.IsFinite(finite)
                ? finite.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "unavailable";
            Plugin.Logger.LogInfo($"[voice-volume] request={request}; source-instance={sourceIdentity}; value={number}; {status}; native-volume-acceptance=not-globally-verified.");
        }
        catch (Exception) { }
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
