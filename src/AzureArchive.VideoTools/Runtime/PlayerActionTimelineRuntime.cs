extern alias unitycore;

using System.Diagnostics;
using System.Reflection;
using AzureArchive.VideoTools.Core.Characters;
using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Api;
using AzureArchive.VideoTools.Interop;
using FlatData;
using HarmonyLib;
using ScenarioAnimation;

namespace AzureArchive.VideoTools.Runtime;

internal sealed record ActionTimelinePlan(bool HasCommands, double Seconds);

/// <summary>
/// Joins the official animation list with an official WaitAnimation. No custom IL2CPP
/// class, borrowed native delegate, replacement player coroutine or synthetic advance.
/// A native wrapper is reacquired from the CURRENT player's animation list each tick.
/// </summary>
internal static class PlayerActionTimelineRuntime
{
    private const string HarmonyId = "rukari.moreeffects.action-timeline";
    private static readonly ActionTimelineCompletionGate Gate = new();
    private static readonly Dictionary<int, CharacterCompletionTarget> CharacterTargets = new();
    private static Harmony? _harmony;
    private static int _mainThread;
    private static int _playerId;
    private static int _row;
    private static bool _preview;
    private static long _storageRevision;
    private static long _animationPointer;
    private static bool _camera;
    private static bool _coldHoldUsed;
    private static int _coldPlayer;
    private static long _coldStorage;
    private static bool _watchdogReset;
    private static bool _tickFailureReported;
    private static bool _scriptReadFailureReported;
    private static readonly List<long> _supersededSequences = new(4);

    internal static bool Install()
    {
        if (_harmony != null) return true;
        _mainThread = Environment.CurrentManagedThreadId;
        var harmony = new Harmony(HarmonyId);
        try
        {
            MethodInfo parse = AccessTools.Method(typeof(Utils.ScenarioUtil), "ParseScript",
                new[] { typeof(IScenarioScriptExcel), typeof(Test), typeof(Enums.Language) })
                ?? throw new MissingMethodException("ScenarioUtil.ParseScript");
            if (typeof(WaitAnimation).GetConstructor(new[] { typeof(float), typeof(Test) }) == null)
                throw new MissingMethodException("WaitAnimation(Single,Test)");
            harmony.Patch(parse, postfix: new HarmonyMethod(typeof(PlayerActionTimelineRuntime), nameof(ParsePostfix)) { priority = Priority.Last });
            _harmony = harmony;
            Log("installed; official-animation-list; actual-parser-input; native acceptance pending");
            return true;
        }
        catch (Exception ex)
        {
            harmony.UnpatchSelf(); Log("unavailable: " + ex.GetType().Name + ": " + ex.Message); return false;
        }
    }

    public static void ParsePostfix(Utils.ScenarioUtil.ScriptInfo? __result, IScenarioScriptExcel scenario, Test owner, bool __runOriginal)
    {
        if (!__runOriginal || Environment.CurrentManagedThreadId != _mainThread || !InteropObjectGuard.IsAlive(owner)
            || !InteropObjectGuard.IsAlive(scenario) || !InteropObjectGuard.IsAlive(__result)) return;
        // 2026-10-13: every read below crosses into IL2CPP. The stage marker names
        // the exact crossing that failed when one throws; the scenario script read
        // is guarded separately because the official ScriptKr getter throws a
        // native NullReferenceException when it is read a second time from this
        // postfix. Losing that read must not lose the hold: without the hold the
        // engine advances 4-5 ms after dispatch, so a 600 ms action is overwritten
        // and only its result is ever seen.
        int stage = 0;
        int row = -1;
        int scriptLength = -1;
        bool previewKnown = false;
        try
        {
            stage = 1;
            row = owner.cur;
            stage = 2;
            string script = ReadScenarioScript(scenario);
            scriptLength = script.Length;
            stage = 3;
            // The parser input capture keeps its own verdict: a second, conflicting
            // input inside one window still refuses the hold. Only the fallback path
            // (no readable script) uses the active window sequence directly.
            long sequence = script.Length != 0
                ? PlayerAdvanceObservationWindow.CaptureParsedScript(script, row)
                : PlayerAdvanceObservationWindow.ReadLifecycleSnapshot().ActiveWindowSequence;
            stage = 4;
            if (sequence == 0 || !PlayerCommandObservationRuntime.ActionTimelineEnabled) return;
            stage = 5;
            int playerId = owner.GetInstanceID();
            bool preview = owner.previewMode;
            previewKnown = true;
            stage = 6;
            long storage = ActiveProjectPairSource.StorageRevision;
            if (playerId != _coldPlayer || storage != _coldStorage)
            { _coldPlayer = playerId; _coldStorage = storage; _coldHoldUsed = false; }
            stage = 7;
            ActionTimelinePlan? plan = PlayerCommandObservationRuntime.GetTimelinePlan(script, row, preview);
            stage = 8;
            if (plan is { HasCommands: false } || (plan is null && (preview || _coldHoldUsed))) return;
            stage = 9;
            var animations = __result!.scenarioAnimations;
            if (!InteropObjectGuard.IsAlive(animations) || animations.Count >= 256) return;
            // A nested parser can revisit the same list. Never add two waits for
            // one admitted advance; conflicting inputs are rejected by capture.
            stage = 10;
            if (Owns(sequence))
            {
                for (int i = 0; i < animations.Count; i++)
                    if (animations[i]?.Pointer.ToInt64() == _animationPointer) return;
            }
            // The cold hold allows ONE asynchronous index load to complete before the
            // first card advances. Ordinary cards release as soon as they are identified.
            if (plan is null) _coldHoldUsed = true;
            // 2026-10-13: a live hold of an EARLIER row is closed by its owner instead of
            // being overwritten in silence. Arming here used to cancel the previous gate
            // with no trace, which left that row's not-yet-due segments scheduled: they
            // landed inside a later row's timeline and overwrote its result (measured: an
            // `at=1500` camera segment of row 0 landed while row 2 was current and pushed
            // the camera from 460 to 520). The flush runs on the next main-thread turn,
            // before any dispatch — this postfix runs inside the native script parse, where
            // writing transforms would race the scene change, and a later tick would apply
            // the old row's endpoint on top of the new row's commands instead of under
            // them. Preview holds are excluded: the editor preview deliberately writes
            // nothing once the author has left the selection it belonged to.
            if (_animationPointer != 0 && Gate.Sequence != sequence && !_preview)
            {
                _supersededSequences.Add(Gate.Sequence);
            }

            Gate.Cancel(); CharacterTargets.Clear();
            // Dispatch has a short native watchdog. Once actual actions start,
            // only this wait's coroutine is rearmed with their real deadline.
            stage = 11;
            var hold = new WaitAnimation((float)ActionTimelineCompletionGate.DispatchTimeoutSeconds, owner);
            stage = 12;
            animations.Add(hold);
            stage = 13;
            _playerId = playerId; _row = row; _preview = preview; _storageRevision = storage;
            _animationPointer = hold.Pointer.ToInt64(); _camera = false;
            _watchdogReset = false; _tickFailureReported = false;
            stage = 14;
            Gate.Arm(sequence, Now());
            Log($"armed; sequence={sequence}; row={_row}; preview={preview}; "
                + $"cold={plan is null}; scriptKnown={script.Length != 0}");
        }
        catch (Exception ex)
        {
            Gate.Cancel(); _animationPointer = 0; _camera = false; CharacterTargets.Clear();
            Log($"parse failed at stage={stage}; row={row}; preview={previewKnown}; "
                + $"scriptLength={scriptLength}; "
                + $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// The parser's own input, read defensively through the shared two-step read
    /// (interface call, then the concrete GenericScenarioExcel cast). The official
    /// ScriptKr getter throws a native NullReferenceException on the interface call
    /// but answers through the cast, so the failure is reported once and the hold
    /// continues without the text when both reads fail: planning then falls back to
    /// the row the live player is parsing.
    /// </summary>
    private static string ReadScenarioScript(IScenarioScriptExcel scenario)
    {
        string? script = ContinueDialogueRuntime.TryReadScriptKr(scenario);
        if (script != null)
        {
            return script;
        }

        if (!_scriptReadFailureReported)
        {
            _scriptReadFailureReported = true;
            Log("scenario script unreadable through interface and concrete cast; "
                + "planning by live row only");
        }

        return string.Empty;
    }

    internal static bool Owns(long sequence) => _animationPointer != 0 && Gate.Sequence == sequence
        && Gate.Status is ActionTimelineStatus.AwaitingDispatch or ActionTimelineStatus.Running;

    internal static IDisposable BeginDelivery(long sequence, double plannedSeconds)
    {
        bool admitted = Owns(sequence) && Gate.BeginDelivery(sequence, Now());
        if (admitted && plannedSeconds > 0d)
        {
            // 2026-10-13: the hold covers the WHOLE planned row, not only the actions that
            // have been dispatched so far. The plan arrives here, not at arm time, because
            // the gate only accepts actions while it is running — and because a row armed
            // before its index existed has no plan at all. Without this the gate completed
            // as soon as the delivered actions finished and force-landed whatever was still
            // queued, so a gap offset collapsed: a camera segment of 300 ms with `at=1500`
            // released at +303 ms with remainingMs=1149 and jumped to the tail endpoint
            // instead of pausing. The gate takes the maximum, so this can only ever extend
            // a hold — never shorten one.
            Gate.AddAction(sequence, Now(), plannedSeconds);
        }

        return new Delivery(sequence, admitted);
    }

    internal static void AddDuration(long sequence, double seconds) => Gate.AddAction(sequence, Now(), seconds);

    internal static bool CanResume(long sequence)
    {
        if (!Owns(sequence)) return false;
        Test? player = Test.Instance;
        return InteropObjectGuard.IsAlive(player) && player!.GetInstanceID() == _playerId
            && player.cur == _row && player.previewMode == _preview
            && ActiveProjectPairSource.StorageRevision == _storageRevision;
    }

    internal static void AddPreset(long sequence, string directive)
    {
        var parsed = new CharacterPresetDirectiveParser().Parse(directive);
        if (parsed.Success && parsed.Value != null) AddDuration(sequence, ActionTimelineTiming.Seconds(parsed.Value));
    }

    internal static void AddCharacter(long sequence, CharacterTransformExecutionSnapshot execution)
    {
        if (!Owns(sequence) || Gate.Status != ActionTimelineStatus.Running) return;
        if (execution.PositionChanged || execution.RotationChanged)
        {
            AddDuration(sequence, ActionTimelineTiming.Seconds(execution.Command));
            CharacterTargets.TryGetValue(execution.Command.PublicSlot, out var previous);
            if (previous?.Occupant != execution.OccupantIdentifier) previous = null;
            // Position and rotation use independent tweens. Preserve the other
            // channel's endpoint; another write on this channel replaces it.
            CharacterTargets[execution.Command.PublicSlot] = new(execution.OccupantIdentifier,
                execution.PositionChanged ? execution.Target.Position : previous?.Position,
                execution.RotationChanged ? execution.Target.LocalEulerAngles : previous?.Rotation);
        }
    }

    internal static void AddCamera(long sequence, double seconds)
    { if (Owns(sequence)) { _camera = true; AddDuration(sequence, seconds); } }

    internal static void ReleaseUnclaimed(long sequence)
    {
        if (!Owns(sequence) || Gate.Status != ActionTimelineStatus.AwaitingDispatch) return;
        Gate.BeginDelivery(sequence, Now()); Gate.Seal(sequence);
    }

    internal static void TickAfterRendering()
    {
        if (_animationPointer == 0) return;
        WaitAnimation? hold = null;
        Test? player = null;
        try
        {
            player = Test.Instance;
            if (!InteropObjectGuard.IsAlive(player) || player!.GetInstanceID() != _playerId || player.previewMode != _preview
                || player.cur != _row || ActiveProjectPairSource.StorageRevision != _storageRevision)
            { Forget("owner-or-row-changed"); return; }
            var list = player.currentAnims;
            if (!InteropObjectGuard.IsAlive(list) || list.Count > 256) { Forget("animation-list-unavailable"); return; }
            for (int i = 0; i < list.Count; i++)
            {
                var animation = list[i];
                if (InteropObjectGuard.IsAlive(animation) && animation.Pointer.ToInt64() == _animationPointer)
                { hold = animation.TryCast<WaitAnimation>(); break; }
            }
            if (!InteropObjectGuard.IsAlive(hold)) { Forget("native-animation-no-longer-owned"); return; }
            if (hold!.hasCompleted || hold.isCancelled) { Forget("native-complete-or-manual-skip"); return; }
            if (!hold.isStarted)
            {
                if (Gate.Tick(Now(), false) == ActionTimelineStatus.DispatchTimeout) Forget("native-animation-never-started");
                return;
            }
            if (Gate.Status == ActionTimelineStatus.Running && Gate.IsSealed && !_watchdogReset)
            {
                var task = hold.currTask;
                if (InteropObjectGuard.IsAlive(task)) player.StopCoroutine(task);
                // Official WaitTask retains AA's own completion/cancel callbacks.
                // Start is NOT called twice and stoppedAnimCount is never edited.
                float remaining = (float)Math.Max(0.05, Gate.DueAt - Now()
                    + ActionTimelineCompletionGate.CompletionGraceSeconds + 1d);
                hold.currTask = player.StartCoroutine(hold.WaitTask(remaining));
                _watchdogReset = true;
            }
            double now = Now();
            bool pending = Gate.Status == ActionTimelineStatus.Running && now >= Gate.DueAt && NativeActionsPending();
            var status = Gate.Tick(now, pending);
            if (status is not (ActionTimelineStatus.Completed or ActionTimelineStatus.DispatchTimeout or ActionTimelineStatus.CompletionTimeout)) return;
            // Stop the watchdog coroutine before its native completion. Complete invokes the
            // official on-stop callback and AA decides when/how to advance. No count is edited.
            Release(player, hold, status.ToString());
        }
        catch (Exception ex)
        {
            if (!_tickFailureReported)
            { _tickFailureReported = true; Log("tick failed: " + ex.GetType().Name + ": " + ex.Message); }
            // Release only the verified current list's exact owned animation.
            // If it cannot be reached, the short native watchdog still completes.
            try
            {
                if (InteropObjectGuard.IsAlive(player) && InteropObjectGuard.IsAlive(hold)
                    && hold!.Pointer.ToInt64() == _animationPointer && hold.isStarted)
                    Release(player!, hold, "managed-observation-failed");
            }
            catch { Forget("release-failed-native-watchdog-retained"); }
        }
    }

    private static bool NativeActionsPending()
    {
        if (Plugin.Host.CharacterPresetsInternal.HasFiniteAction(Gate.Sequence)) return true;
        if (_camera && Plugin.Host.SceneCameraInternal.HasFiniteMotion) return true;
        if (CharacterTargets.Count == 0) return false;
        var read = Plugin.Api.CharacterTransforms.ReadSlotsOnMainThread();
        if (!read.Success || read.Value == null) return true;
        foreach (var pair in CharacterTargets)
        {
            var slot = read.Value.FirstOrDefault(s => s.PublicSlot == pair.Key && s.Occupied
                && string.Equals(s.OccupantIdentifier, pair.Value.Occupant, StringComparison.Ordinal));
            if (slot?.State is not { } state) continue;
            if (pair.Value.Position is { } position && (Math.Abs(position.X - state.Position.X) > 0.2f
                || Math.Abs(position.Y - state.Position.Y) > 0.2f)) return true;
            if (pair.Value.Rotation is { } rotation && (AngleDifference(rotation.X, state.LocalEulerAngles.X) > 0.2f
                || AngleDifference(rotation.Y, state.LocalEulerAngles.Y) > 0.2f
                || AngleDifference(rotation.Z, state.LocalEulerAngles.Z) > 0.2f)) return true;
        }
        return false;
    }

    private static void Release(Test player, WaitAnimation hold, string reason)
    {
        var task = hold.currTask;
        if (InteropObjectGuard.IsAlive(task)) { player.StopCoroutine(task); hold.currTask = null; }
        _animationPointer = 0; _camera = false; CharacterTargets.Clear();
        Log($"released; sequence={Gate.Sequence}; status={reason}; syntheticAdvance=false");
        // A released hold must never leave the row's remaining slot segments
        // unapplied: they are fast-forwarded to their end state before the official
        // stop callback lets the engine move on, so the next row inherits the
        // endpoint the author wrote rather than a mid-animation position.
        PlayerCommandObservationRuntime.FastForwardQueuedSegments(Gate.Sequence, reason);
        hold.Complete(false);
    }

    internal static void Stop()
    {
        try
        {
            Test? player = Test.Instance;
            if (_animationPointer != 0 && InteropObjectGuard.IsAlive(player) && player!.GetInstanceID() == _playerId
                && player.cur == _row && player.previewMode == _preview)
            {
                var list = player.currentAnims;
                if (InteropObjectGuard.IsAlive(list) && list.Count <= 256)
                    for (int i = 0; i < list.Count; i++)
                    {
                        var animation = list[i];
                        if (InteropObjectGuard.IsAlive(animation) && animation.Pointer.ToInt64() == _animationPointer)
                        {
                            var hold = animation.TryCast<WaitAnimation>();
                            if (InteropObjectGuard.IsAlive(hold) && hold!.isStarted && !hold.hasCompleted && !hold.isCancelled)
                                Release(player, hold, "runtime-stopped");
                            break;
                        }
                    }
            }
        }
        catch { } // During game exit the player can already be unavailable.
        // Shutdown clears instead of fast-forwarding: writing an endpoint into a
        // player that is already going away is worse than leaving it unset.
        PlayerCommandObservationRuntime.ClearQueuedSegments();
        Forget("runtime-stopped");
        try { _harmony?.UnpatchSelf(); } catch { }
        _harmony = null;
    }
    internal static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    private static double AngleDifference(float a, float b) =>
        Math.Abs((((double)a - b) % 360d + 540d) % 360d - 180d);
    private static void Forget(string reason)
    {
        long sequence = Gate.Sequence;
        Gate.Cancel(); _animationPointer = 0; _camera = false; CharacterTargets.Clear();
        Log("cancelled; " + reason);
        // Same rule as Release: dropping the queue is not an option, because a
        // relative move that never lands leaves an endpoint the author never wrote.
        PlayerCommandObservationRuntime.FastForwardQueuedSegments(sequence, reason);
    }
    private static void Log(string text) { try { Plugin.Logger.LogInfo("action-timeline: " + text); } catch { } }

    /// <summary>
    /// Closes every hold that a later row's arm replaced. Called on the main thread BEFORE
    /// the frame dispatches anything, so a superseded row's remaining queued segments land
    /// as their end state underneath the new row's commands — the endpoint the next row is
    /// meant to inherit — instead of on top of them. Nothing is cancelled here: the gate and
    /// the native animation pointer already belong to the new row.
    /// </summary>
    internal static void FlushSuperseded()
    {
        if (_supersededSequences.Count == 0) return;
        long[] superseded = _supersededSequences.ToArray();
        _supersededSequences.Clear();
        foreach (long sequence in superseded)
        {
            Log($"superseded; sequence={sequence}; reason=next-row-armed");
            PlayerCommandObservationRuntime.FastForwardQueuedSegments(
                sequence,
                "superseded-by-next-row");
        }
    }
    private sealed record CharacterCompletionTarget(string Occupant, CharacterVector3? Position, CharacterVector3? Rotation);
    private sealed class Delivery : IDisposable
    {
        private readonly long _sequence; private readonly bool _admitted;
        internal Delivery(long sequence, bool admitted) => (_sequence, _admitted) = (sequence, admitted);
        public void Dispose() { if (_admitted) Gate.Seal(_sequence); }
    }
}
