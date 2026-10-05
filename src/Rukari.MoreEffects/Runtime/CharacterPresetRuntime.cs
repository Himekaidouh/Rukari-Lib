extern alias unitycore;

using System.Diagnostics;
using AzureArchive.VideoTools.Api;
using AzureArchive.VideoTools.Core;
using AzureArchive.VideoTools.Core.Characters;
using AzureArchive.VideoTools.Core.Commands;
using Transform = unitycore::UnityEngine.Transform;
using UnityObject = unitycore::UnityEngine.Object;
using Vector3 = unitycore::UnityEngine.Vector3;

namespace AzureArchive.VideoTools.Runtime;

/// <summary>
/// Temporary root-pivot overlays. No actor, Transform or other native wrapper survives
/// a call. The root's authored pivot is preserved; its anatomical foot position has
/// not been established by the current native evidence and is not guessed here.
/// </summary>
internal sealed class CharacterPresetRuntime
{
    private readonly int _mainThreadId = Environment.CurrentManagedThreadId;
    private readonly CharacterPresetDirectiveParser _parser = new();
    private readonly Dictionary<int, ActivePreset> _active = new();
    private readonly CharacterPresetWindowGate _windows = new();

    internal CharacterPresetRuntime(RuntimeCapabilityService capabilities)
    {
        capabilities.Bound(CharacterPresetCommandFamilyCompiler.CapabilityId,
            "managed relative root-pivot overlay dispatcher; native preset/pivot acceptance pending");
    }

    internal ApiResult<bool> Begin(string sceneIdentity, string directive, long windowSequence)
    {
        if (!IsMainThread()) return ApiResult<bool>.Fail("Character presets require the Unity main thread.");
        if (string.IsNullOrWhiteSpace(sceneIdentity) || !_windows.MayStart(windowSequence))
            return ApiResult<bool>.Fail("Character preset scene/window is missing or superseded.");
        var parsed = _parser.Parse(directive);
        if (!parsed.Success || parsed.Value == null) return ApiResult<bool>.Fail(parsed.Error);
        int slot = parsed.Value.PublicSlot;
        if (!StopSlot(slot, "replace-or-replay"))
            return ApiResult<bool>.Fail("Previous character preset cleanup is still pending.");
        try
        {
            LiveTarget? live = Resolve(slot);
            if (live == null) return ApiResult<bool>.Fail($"Character preset slot {slot} is unavailable.");
            CharacterPresetPose pose = Read(live.Transform);
            if (!CharacterPresetPoseOverlay.IsFinite(pose)) return ApiResult<bool>.Fail("Character pose is non-finite.");
            if (parsed.Value.Kind == CharacterPresetKind.Spin && parsed.Value.SpinAxis == CharacterPresetSpinAxis.X)
            {
                // StopSlot already removed this slot's previous overlay, so the
                // existing read service returns its authored Euler representation
                // without projecting this new effect or entering recursive reads.
                var snapshots = Plugin.Api.CharacterTransforms.ReadSlotsOnMainThread();
                CharacterSlotSnapshot? snapshot = snapshots.Success ? snapshots.Value?.FirstOrDefault(item =>
                    item.PublicSlot == slot && item.Occupied
                    && string.Equals(item.OccupantIdentifier, live.Occupant, StringComparison.Ordinal)) : null;
                if (snapshot?.State is not { } authored || !authored.LocalEulerAngles.IsFinite)
                    return ApiResult<bool>.Fail("Authored rotation is unavailable for the X-axis preset.");
                CharacterVector3 euler = authored.LocalEulerAngles;
                float physicalZ = CharacterScreenRotation.IsHorizontallyFlipped(euler.Y) ? -euler.Z : euler.Z;
                CharacterPresetPose reference = pose with { EulerX = euler.X, EulerY = euler.Y, EulerZ = physicalZ };
                LiveTarget? confirmed = Resolve(slot);
                if (confirmed == null || confirmed.PlayerId != live.PlayerId || confirmed.CharacterId != live.CharacterId
                    || confirmed.TransformId != live.TransformId || confirmed.PreviewMode != live.PreviewMode
                    || !string.Equals(confirmed.Occupant, live.Occupant, StringComparison.Ordinal))
                    return ApiResult<bool>.Fail("Character identity changed while starting the X-axis preset.");
                CharacterPresetPose measured = Read(confirmed.Transform);
                if (!CharacterPresetPoseOverlay.IsFinite(measured)
                    || !CharacterPresetPoseOverlay.SameRotation(pose, reference)
                    || !CharacterPresetPoseOverlay.SameRotation(measured, reference))
                    return ApiResult<bool>.Fail("Character rotation changed while starting the X-axis preset.");
                live = confirmed;
                pose = CharacterPresetPoseOverlay.ReexpressRotation(measured, reference);
            }
            _active[slot] = new ActivePreset(sceneIdentity, parsed.Value, windowSequence,
                live.PlayerId, live.CharacterId, live.TransformId, live.Occupant, live.PreviewMode,
                Stopwatch.GetTimestamp()) { Before = pose };
            return ApiResult<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return ApiResult<bool>.Fail($"Character preset start failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Called only for accepted dialogue boundaries, even when there is no command batch.
    // Its watermark also prevents an older batch queued in this frame from reviving a loop.
    internal void ObserveDialogueBoundary(long sequence, string reason)
    {
        if (!IsMainThread() || !_windows.ObserveBoundary(sequence)) return;
        StopAll(reason);
    }

    internal void ObservePlaybackDialogueBoundary(long sequence, int rowBefore, int rowAfter, int messageCount)
    {
        if (IsMainThread() && _windows.ObservePlaybackBoundary(sequence, rowBefore, rowAfter, messageCount))
            StopAll("next-playback-dialogue");
    }

    internal bool NeedsSnapshotProjection(int slot) => IsMainThread()
        && _active.TryGetValue(slot, out ActivePreset? active) && active.OwnsRotation;

    internal CharacterTransformState ProjectAuthoredState(int slot, int characterId,
        int transformId, string occupant, CharacterTransformState current)
    {
        if (!IsMainThread() || !_active.TryGetValue(slot, out ActivePreset? active)
            || active.CharacterId != characterId || active.TransformId != transformId
            || !string.Equals(active.Occupant, occupant, StringComparison.Ordinal)) return current;
        return CharacterPresetPoseOverlay.ProjectAuthoredState(current,
            active.Before, active.Applied, active.OwnsRotation, active.OwnsYaw, active.OwnsPitch);
    }

    internal void Update()
    {
        if (!IsMainThread()) return;
        foreach (int slot in _active.Keys.ToArray()) Tick(slot, apply: false);
    }

    internal void LateUpdate()
    {
        if (!IsMainThread()) return;
        foreach (int slot in _active.Keys.ToArray()) Tick(slot, apply: true);
    }

    internal bool SuspendOverlay(int slot)
    {
        if (!IsMainThread()) return false;
        Tick(slot, apply: false);
        return !_active.TryGetValue(slot, out ActivePreset? active) || !active.Stopping;
    }

    internal void StopAll(string reason)
    {
        if (!IsMainThread()) return;
        foreach (int slot in _active.Keys.ToArray()) StopSlot(slot, reason);
    }

    internal void StopScene(string sceneIdentity, string reason)
    {
        if (!IsMainThread()) return;
        foreach (int slot in _active.Where(pair => pair.Value.SceneIdentity == sceneIdentity)
                     .Select(pair => pair.Key).ToArray()) StopSlot(slot, reason);
    }

    private void Tick(int slot, bool apply)
    {
        if (!_active.TryGetValue(slot, out ActivePreset? active)) return;
        if (active.Stopping)
        {
            StopSlot(slot, "retry-pending-cleanup");
            return;
        }
        try
        {
            LiveTarget? live = Resolve(slot);
            if (live == null || !SameActor(active, live))
            {
                _active.Remove(slot);
                Log(slot, "STOPPED", "actor-lifetime-or-mode-changed; replacementWritten=false");
                return;
            }

            RemoveOverlay(active, live.Transform);
            if (active.PreviewMode != live.PreviewMode)
            {
                _active.Remove(slot);
                Log(slot, "STOPPED", "player-mode-changed");
                return;
            }
            if (!apply) return;
            double elapsed = (Stopwatch.GetTimestamp() - active.StartedAt) / (double)Stopwatch.Frequency;
            CharacterPresetFrame frame = CharacterPresetEvaluator.Sample(active.Command, elapsed);
            if (frame.Completed)
            {
                _active.Remove(slot);
                Log(slot, "COMPLETED", "relative-overlay-restored");
                return;
            }

            CharacterPresetPose before = Read(live.Transform);
            if (active.Command.Kind == CharacterPresetKind.Spin && active.Command.SpinAxis == CharacterPresetSpinAxis.X)
            {
                // Native cleanup and external writers can both choose a different
                // equivalent triple. Keep the actually observed orientation; the
                // previous authored triple only selects its nearby representation.
                before = CharacterPresetPoseOverlay.ReexpressRotation(before, active.Before);
            }
            CharacterPresetPose target = CharacterPresetPoseOverlay.Apply(before, frame);
            if (!CharacterPresetPoseOverlay.IsFinite(target)) throw new InvalidOperationException("Non-finite preset pose.");
            active.Before = before;
            active.Applied = target;
            if (frame.RotationDegrees != 0f || frame.YawDegrees != 0f || frame.PitchDegrees != 0f)
            {
                active.OwnsRotation = true;
                active.OwnsYaw = frame.YawDegrees != 0f;
                active.OwnsPitch = frame.PitchDegrees != 0f;
                live.Transform.localEulerAngles = new Vector3(target.EulerX, target.EulerY, target.EulerZ);
            }
            if (frame.ScaleX != 1f || frame.ScaleY != 1f)
            {
                active.OwnsScale = true;
                live.Transform.localScale = new Vector3(target.ScaleX, target.ScaleY, target.ScaleZ);
            }
            // Unity normalizes Euler representation; compare cleanup with its actual written value.
            active.Applied = Read(live.Transform);
        }
        catch (Exception ex)
        {
            Log(slot, "FAILED", $"{ex.GetType().Name}: {ex.Message}");
            StopSlot(slot, "tick-failed");
        }
    }

    private bool StopSlot(int slot, string reason)
    {
        if (!_active.TryGetValue(slot, out ActivePreset? active)) return true;
        active.Stopping = true;
        try
        {
            LiveTarget? live = Resolve(slot);
            if (live != null && SameActor(active, live)) RemoveOverlay(active, live.Transform);
            _active.Remove(slot);
            Log(slot, "STOPPED", reason);
            return true;
        }
        catch (Exception ex)
        {
            Log(slot, "CLEANUP_FAILED", $"{reason}; {ex.GetType().Name}: {ex.Message}; residual-mutation-unverified");
            return false;
        }
    }

    private static void RemoveOverlay(ActivePreset active, Transform transform)
    {
        if (!active.OwnsRotation && !active.OwnsScale) return;
        CharacterPresetPose current = Read(transform);
        CharacterPresetPose restored = CharacterPresetPoseOverlay.RemoveOwned(current,
            active.Before, active.Applied, active.OwnsRotation, active.OwnsScale, active.OwnsYaw, active.OwnsPitch);
        if (restored.EulerX != current.EulerX || restored.EulerY != current.EulerY
            || restored.EulerZ != current.EulerZ)
            transform.localEulerAngles = new Vector3(restored.EulerX, restored.EulerY, restored.EulerZ);
        active.OwnsRotation = false;
        active.OwnsYaw = false;
        active.OwnsPitch = false;
        if (restored.ScaleX != current.ScaleX || restored.ScaleY != current.ScaleY)
            transform.localScale = new Vector3(restored.ScaleX, restored.ScaleY, current.ScaleZ);
        active.OwnsScale = false;
    }

    private static CharacterPresetPose Read(Transform transform)
    {
        Vector3 euler = transform.localEulerAngles;
        Vector3 scale = transform.localScale;
        return new CharacterPresetPose(euler.x, euler.y, euler.z, scale.x, scale.y, scale.z);
    }

    private static LiveTarget? Resolve(int slot)
    {
        // Same strongly typed slot/Transform path as CharacterTransformService.Resolve.
        // Unity destruction is checked in addition to IL2CPP wrapper lifetime.
        Test? player = Test.Instance;
        if (!Alive(player)) return null;
        var slots = player!.slots;
        if (slots == null || slot < 1 || slot > slots.Length) return null;
        Character? character = slots[slot - 1];
        if (!Alive(character)) return null;
        Transform? transform = character!.transform;
        if (!Alive(transform)) return null;
        string occupant = character.identifier ?? string.Empty;
        int playerId = player.GetInstanceID();
        int characterId = character.GetInstanceID();
        int transformId = transform!.GetInstanceID();
        if (occupant.Length == 0 || playerId == 0 || characterId == 0 || transformId == 0) return null;
        return new LiveTarget(transform, playerId, characterId, transformId, occupant, player.previewMode);
    }

    private static bool Alive(UnityObject? value) => value is not null && !value.WasCollected
        && value.Pointer != IntPtr.Zero && value != null;

    private static bool SameActor(ActivePreset active, LiveTarget live) =>
        active.PlayerId == live.PlayerId && active.CharacterId == live.CharacterId
        && active.TransformId == live.TransformId && active.Occupant == live.Occupant;

    private bool IsMainThread() => Environment.CurrentManagedThreadId == _mainThreadId;

    private static void Log(int slot, string status, string detail) =>
        PlayerCommandObservationLog.Append($"{PlayerCommandObservationRuntime.CreateStamp()} character-preset "
            + $"status={status}; slot={slot}; detail={detail.Replace('\r', ' ').Replace('\n', ' ')}");

    // LiveTarget is strictly call-local; ActivePreset contains managed values only.
    private sealed record LiveTarget(Transform Transform, int PlayerId, int CharacterId,
        int TransformId, string Occupant, bool PreviewMode);

    private sealed class ActivePreset
    {
        internal ActivePreset(string sceneIdentity, CharacterPresetCommand command, long windowSequence,
            int playerId, int characterId, int transformId, string occupant, bool previewMode, long startedAt)
        {
            SceneIdentity = sceneIdentity; Command = command; WindowSequence = windowSequence;
            PlayerId = playerId; CharacterId = characterId; TransformId = transformId;
            Occupant = occupant; PreviewMode = previewMode; StartedAt = startedAt;
        }
        internal string SceneIdentity { get; }
        internal CharacterPresetCommand Command { get; }
        internal long WindowSequence { get; }
        internal int PlayerId { get; }
        internal int CharacterId { get; }
        internal int TransformId { get; }
        internal string Occupant { get; }
        internal bool PreviewMode { get; }
        internal long StartedAt { get; }
        internal CharacterPresetPose Before { get; set; }
        internal CharacterPresetPose Applied { get; set; }
        internal bool OwnsRotation { get; set; }
        internal bool OwnsYaw { get; set; }
        internal bool OwnsPitch { get; set; }
        internal bool OwnsScale { get; set; }
        internal bool Stopping { get; set; }
    }
}
