extern alias unitycore;

using AzureArchive.VideoTools.Api;
using AzureArchive.VideoTools.Core;
using AzureArchive.VideoTools.Core.Characters;
using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Results;
using Quaternion = unitycore::UnityEngine.Quaternion;
using Transform = unitycore::UnityEngine.Transform;
using Vector3 = unitycore::UnityEngine.Vector3;

namespace AzureArchive.VideoTools.Interop;

internal sealed class CharacterTransformService : ICharacterTransformService
{
    private readonly int _mainThreadId = Environment.CurrentManagedThreadId;
    private readonly CharacterTransformDirectiveParser _parser = new();
    private readonly CharacterTransformPlanner _planner = new();
    private readonly CharacterInheritedStartPlanner _inheritedStartPlanner = new();
    private readonly CharacterTransformOriginBaselineStore _officialOrigins = new();
    private readonly CharacterTransformBaselineStore _baselines = new();
    private readonly SlotPendingCommandFamilyCompiler _pendingCompiler = new();
    private readonly SlotPendingStore _slotPendings = new();
    private readonly bool?[] _previousSlotPresence = new bool?[CharacterTransformCommandValidator.MaximumPublicSlot];

    public CharacterTransformService(RuntimeCapabilityService capabilities)
    {
        capabilities.Verified(
            "Player.CharacterTransform",
            "runtime-proven slots/GetPos/SetPos/Y-flip/TweenPosition/TweenRotation/EaseInOut");
        capabilities.Verified(
            "Player.CharacterTransform.Reset",
            "official pre-inheritance baseline is isolated from the per-scene replay entry baseline");
        capabilities.Verified(
            "Player.CharacterTransform.ReadSlots",
            "runtime-proven Test.slots/GetPos/Transform read returned as managed snapshots only");
    }

    public ApiResult<IReadOnlyList<CharacterSlotSnapshot>> ReadSlotsOnMainThread()
    {
        if (!IsMainThread())
        {
            return ApiResult<IReadOnlyList<CharacterSlotSnapshot>>.Fail(
                "Character slots may only be read on the Unity main thread.");
        }

        try
        {
            Test? player = Test.Instance;
            if (ReferenceEquals(player, null))
            {
                return ApiResult<IReadOnlyList<CharacterSlotSnapshot>>.Fail(
                    "Story player is not active.");
            }

            var slots = player.slots;
            if (ReferenceEquals(slots, null)
                || slots.Length < CharacterTransformCommandValidator.MaximumPublicSlot)
            {
                return ApiResult<IReadOnlyList<CharacterSlotSnapshot>>.Fail(
                    "Physical character slots are unavailable.");
            }

            var snapshots = new List<CharacterSlotSnapshot>(
                CharacterTransformCommandValidator.MaximumPublicSlot);
            for (int index = 0;
                 index < CharacterTransformCommandValidator.MaximumPublicSlot;
                 index++)
            {
                int publicSlot = index + 1;
                Character? character = slots[index];
                if (ReferenceEquals(character, null))
                {
                    snapshots.Add(new CharacterSlotSnapshot(
                        publicSlot,
                        false,
                        string.Empty,
                        null));
                    continue;
                }

                Transform? transform = character.transform;
                if (ReferenceEquals(transform, null))
                {
                    return ApiResult<IReadOnlyList<CharacterSlotSnapshot>>.Fail(
                        $"Character root Transform is unavailable in slot {publicSlot}.");
                }

                string occupant = character.identifier ?? string.Empty;
                CharacterTransformState logicalState = ReadState(character, transform);
                var presets = Plugin.Host.CharacterPresetsInternal;
                if (presets.NeedsSnapshotProjection(publicSlot))
                {
                    // UI reads can precede the heartbeat's Update. Return the
                    // authored pose without removing the currently rendered effect.
                    logicalState = presets.ProjectAuthoredState(publicSlot,
                        character.GetInstanceID(), transform.GetInstanceID(), occupant, logicalState);
                }
                snapshots.Add(new CharacterSlotSnapshot(
                    publicSlot,
                    true,
                    occupant,
                    logicalState));
            }

            return ApiResult<IReadOnlyList<CharacterSlotSnapshot>>.Ok(snapshots);
        }
        catch (Exception ex)
        {
            return ApiResult<IReadOnlyList<CharacterSlotSnapshot>>.Fail(
                $"Character slot read failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public ApiResult<CharacterTransformExecutionSnapshot> ExecuteOnMainThread(
        string sceneIdentity,
        string directive)
    {
        if (!IsMainThread())
        {
            return ApiResult<CharacterTransformExecutionSnapshot>.Fail(
                "Character transforms may only execute on the Unity main thread.");
        }

        try
        {
            var parsed = _parser.Parse(directive);
            if (!parsed.Success || parsed.Value == null)
            {
                return ApiResult<CharacterTransformExecutionSnapshot>.Fail(parsed.Error);
            }

            return ExecuteCommandOnMainThread(sceneIdentity, parsed.Value);
        }
        catch (Exception ex)
        {
            return ApiResult<CharacterTransformExecutionSnapshot>.Fail(
                $"Character transform failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public ApiResult<CharacterTransformExecutionSnapshot> ExecuteCommandOnMainThread(
        string sceneIdentity,
        CharacterTransformCommand command) =>
        ExecuteCommandWithOriginOnMainThread(sceneIdentity, command, string.Empty);

    internal ApiResult<CharacterTransformExecutionSnapshot> ExecuteCommandWithOriginOnMainThread(
        string sceneIdentity,
        CharacterTransformCommand command,
        string originIdentity)
    {
        if (!IsMainThread())
        {
            return ApiResult<CharacterTransformExecutionSnapshot>.Fail(
                "Character transforms may only execute on the Unity main thread.");
        }

        try
        {
            if (string.IsNullOrWhiteSpace(sceneIdentity))
            {
                return ApiResult<CharacterTransformExecutionSnapshot>.Fail(
                    "Scene identity is required for a character transform.");
            }

            var validation = CharacterTransformCommandValidator.Validate(command);
            if (!validation.Success)
            {
                return ApiResult<CharacterTransformExecutionSnapshot>.Fail(validation.Error);
            }

            if (!Plugin.Host.CharacterPresetsInternal.SuspendOverlay(command.PublicSlot))
            {
                return ApiResult<CharacterTransformExecutionSnapshot>.Fail(
                    "Character preset cleanup is pending; no transform baseline was captured.");
            }

            ApiResult<LiveCharacter> resolved = Resolve(command.PublicSlot);
            if (!resolved.Success || resolved.Value == null)
            {
                return ApiResult<CharacterTransformExecutionSnapshot>.Fail(resolved.Error);
            }

            LiveCharacter live = resolved.Value;
            CharacterTransformState before = ReadState(live.Character, live.Transform);
            Result<CharacterTransformOriginBaseline> existingOrigin = _officialOrigins.Get(
                originIdentity ?? string.Empty,
                command.PublicSlot,
                live.OccupantIdentifier,
                live.ManagedInstanceId);
            Result<CharacterTransformOriginBaseline> originResult = _officialOrigins.Capture(
                originIdentity ?? string.Empty,
                command.PublicSlot,
                live.OccupantIdentifier,
                live.ManagedInstanceId,
                before);
            if (!originResult.Success || originResult.Value == null)
            {
                return ApiResult<CharacterTransformExecutionSnapshot>.Fail(
                    originResult.Error);
            }

            CharacterTransformOriginBaseline origin = originResult.Value;
            bool baselineCaptured;
            var baseline = new CharacterTransformBaseline(
                sceneIdentity,
                command.PublicSlot,
                live.OccupantIdentifier,
                origin.State);
            if (command.Operation == CharacterTransformOperation.Reset)
            {
                // A reset-only first command captures the untouched live state
                // and becomes a successful no-op. When the lineage already
                // wrote earlier scenes, Capture returns that original state.
                baselineCaptured = !existingOrigin.Success;
            }
            else
            {
                var existingBaseline = _baselines.Get(
                    sceneIdentity,
                    command.PublicSlot,
                    live.OccupantIdentifier);
                var baselineResult = _baselines.Capture(
                    sceneIdentity,
                    command.PublicSlot,
                    live.OccupantIdentifier,
                    before);
                if (!baselineResult.Success || baselineResult.Value == null)
                {
                    return ApiResult<CharacterTransformExecutionSnapshot>.Fail(
                        baselineResult.Error);
                }

                baselineCaptured = !existingBaseline.Success;
            }

            var planned = _planner.Plan(command, before, baseline);
            if (!planned.Success || planned.Value == null)
            {
                return ApiResult<CharacterTransformExecutionSnapshot>.Fail(planned.Error);
            }

            CharacterTransformTarget target = planned.Value;
            ApiResult<bool> applied = Apply(live, target);
            if (!applied.Success)
            {
                return ApiResult<CharacterTransformExecutionSnapshot>.Fail(applied.Error);
            }

            return ApiResult<CharacterTransformExecutionSnapshot>.Ok(
                new CharacterTransformExecutionSnapshot(
                    sceneIdentity,
                    live.OccupantIdentifier,
                    command,
                    before,
                    target.State,
                    target.PositionChanged,
                    target.RotationChanged,
                    baselineCaptured,
                    target.IsReset));
        }
        catch (Exception ex)
        {
            return ApiResult<CharacterTransformExecutionSnapshot>.Fail(
                $"Character transform failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public ApiResult<CharacterTransformInheritedStartSnapshot> ApplyInheritedStartOnMainThread(
        string sceneIdentity,
        int publicSlot,
        string expectedOccupantIdentifier,
        PreviewChainSlotState inheritedStart,
        string originIdentity)
    {
        if (!IsMainThread())
        {
            return ApiResult<CharacterTransformInheritedStartSnapshot>.Fail(
                "Character transforms may only execute on the Unity main thread.");
        }

        try
        {
            if (string.IsNullOrWhiteSpace(sceneIdentity))
            {
                return ApiResult<CharacterTransformInheritedStartSnapshot>.Fail(
                    "Scene identity is required for an inherited preview start.");
            }

            if (inheritedStart == null)
            {
                return ApiResult<CharacterTransformInheritedStartSnapshot>.Fail(
                    "Inherited start state is required.");
            }

            if (publicSlot < CharacterTransformCommandValidator.MinimumPublicSlot
                || publicSlot > CharacterTransformCommandValidator.MaximumPublicSlot)
            {
                return ApiResult<CharacterTransformInheritedStartSnapshot>.Fail(
                    "Physical character slot must be between 1 and 5.");
            }

            if (inheritedStart.PublicSlot != publicSlot)
            {
                return ApiResult<CharacterTransformInheritedStartSnapshot>.Fail(
                    "Inherited start state belongs to a different slot.");
            }

            if (!Plugin.Host.CharacterPresetsInternal.SuspendOverlay(publicSlot))
            {
                return ApiResult<CharacterTransformInheritedStartSnapshot>.Fail(
                    "Character preset cleanup is pending; no inherited baseline was captured.");
            }

            ApiResult<LiveCharacter> resolved = Resolve(publicSlot);
            if (!resolved.Success || resolved.Value == null)
            {
                return ApiResult<CharacterTransformInheritedStartSnapshot>.Fail(resolved.Error);
            }

            LiveCharacter live = resolved.Value;
            if (!string.Equals(
                    live.OccupantIdentifier,
                    expectedOccupantIdentifier,
                    StringComparison.Ordinal))
            {
                return ApiResult<CharacterTransformInheritedStartSnapshot>.Fail(
                    $"Live occupant '{live.OccupantIdentifier}' does not match the AAP expected "
                    + $"occupant '{expectedOccupantIdentifier}' in slot {publicSlot}.");
            }

            CharacterTransformState before = ReadState(live.Character, live.Transform);
            Result<CharacterTransformOriginBaseline> official = _officialOrigins.Capture(
                originIdentity ?? string.Empty,
                publicSlot,
                live.OccupantIdentifier,
                live.ManagedInstanceId,
                before);
            if (!official.Success || official.Value == null)
            {
                return ApiResult<CharacterTransformInheritedStartSnapshot>.Fail(
                    official.Error);
            }

            Result<CharacterInheritedStartTarget> planned = _inheritedStartPlanner.Plan(
                inheritedStart,
                before,
                new CharacterTransformBaseline(
                    sceneIdentity,
                    publicSlot,
                    live.OccupantIdentifier,
                    official.Value.State));
            if (!planned.Success || planned.Value == null)
            {
                return ApiResult<CharacterTransformInheritedStartSnapshot>.Fail(planned.Error);
            }

            CharacterInheritedStartTarget target = planned.Value;
            ApiResult<bool> applied = Apply(
                live,
                new CharacterTransformTarget(
                    target.State,
                    target.PositionChanged,
                    target.RotationChanged,
                    0,
                    CharacterTransformEasing.Linear,
                    IsReset: false));
            if (!applied.Success)
            {
                return ApiResult<CharacterTransformInheritedStartSnapshot>.Fail(applied.Error);
            }

            CharacterTransformState actual = ReadState(live.Character, live.Transform);
            var existingBaseline = _baselines.Get(
                sceneIdentity,
                publicSlot,
                live.OccupantIdentifier);
            var captured = _baselines.Capture(
                sceneIdentity,
                publicSlot,
                live.OccupantIdentifier,
                actual);
            if (!captured.Success || captured.Value == null)
            {
                return ApiResult<CharacterTransformInheritedStartSnapshot>.Fail(captured.Error);
            }

            return ApiResult<CharacterTransformInheritedStartSnapshot>.Ok(
                new CharacterTransformInheritedStartSnapshot(
                    sceneIdentity,
                    publicSlot,
                    live.OccupantIdentifier,
                    before,
                    actual,
                    target.PositionChanged,
                    target.RotationChanged,
                    !existingBaseline.Success));
        }
        catch (Exception ex)
        {
            return ApiResult<CharacterTransformInheritedStartSnapshot>.Fail(
                $"Character transform failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal ApiResult<CharacterTransformExecutionSnapshot> ReplayCommandOnMainThread(
        string sceneIdentity,
        CharacterTransformCommand command,
        string originIdentity)
    {
        if (!IsMainThread())
        {
            return ApiResult<CharacterTransformExecutionSnapshot>.Fail(
                "Character transforms may only execute on the Unity main thread.");
        }

        try
        {
            ApiResult<ReplayPreparation> prepared = PrepareReplay(sceneIdentity, command);
            if (!prepared.Success || prepared.Value == null)
            {
                return ApiResult<CharacterTransformExecutionSnapshot>.Fail(prepared.Error);
            }

            ApiResult<CharacterTransformExecutionSnapshot> executed =
                ExecuteCommandWithOriginOnMainThread(
                    sceneIdentity,
                    command,
                    originIdentity ?? string.Empty);
            if (!executed.Success || executed.Value == null)
            {
                return executed;
            }

            CharacterTransformExecutionSnapshot snapshot = executed.Value;
            if (prepared.Value.BaselineCaptured && !snapshot.BaselineCaptured)
            {
                snapshot = snapshot with { BaselineCaptured = true };
            }

            return ApiResult<CharacterTransformExecutionSnapshot>.Ok(snapshot);
        }
        catch (Exception ex)
        {
            return ApiResult<CharacterTransformExecutionSnapshot>.Fail(
                $"Character replay preparation failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public ApiResult<bool> ClearRuntimeBaselines()
    {
        if (!IsMainThread())
        {
            return ApiResult<bool>.Fail(
                "Character transform baselines may only be cleared on the Unity main thread.");
        }

        _officialOrigins.Clear();
        _baselines.Clear();
        return ApiResult<bool>.Ok(true);
    }

    public ApiResult<SlotPendingStoredSnapshot> StoreSlotPendingOnMainThread(
        string sceneIdentity,
        string canonicalDirective,
        long createdWindowSequence)
    {
        if (!IsMainThread())
        {
            return ApiResult<SlotPendingStoredSnapshot>.Fail(
                "Slot pending transforms may only be stored on the Unity main thread.");
        }

        try
        {
            if (string.IsNullOrWhiteSpace(sceneIdentity))
            {
                return ApiResult<SlotPendingStoredSnapshot>.Fail(
                    "Scene identity is required to store a slot pending transform.");
            }

            if (string.IsNullOrWhiteSpace(canonicalDirective))
            {
                return ApiResult<SlotPendingStoredSnapshot>.Fail(
                    "Slot pending directive is empty.");
            }

            if (!canonicalDirective.StartsWith(
                    SlotPendingCommandFamilyCompiler.CanonicalRootToken + ";",
                    StringComparison.Ordinal))
            {
                return ApiResult<SlotPendingStoredSnapshot>.Fail(
                    "Slot pending storage requires a canonical "
                    + $"{SlotPendingCommandFamilyCompiler.CanonicalRootToken}; directive.");
            }

            var parsed = _pendingCompiler.ParseCanonical(canonicalDirective);
            if (!parsed.Success || parsed.Value == null)
            {
                return ApiResult<SlotPendingStoredSnapshot>.Fail(parsed.Error);
            }

            CharacterTransformCommand command = parsed.Value;
            if (command.Operation == CharacterTransformOperation.Reset)
            {
                return ApiResult<SlotPendingStoredSnapshot>.Fail(
                    "Slot pending transforms do not support reset.");
            }

            ApiResult<LiveCharacter> resolved = Resolve(command.PublicSlot);
            if (resolved.Success && resolved.Value != null)
            {
                return ApiResult<SlotPendingStoredSnapshot>.Fail(
                    $"Physical character slot {command.PublicSlot} currently holds "
                    + $"'{resolved.Value.OccupantIdentifier}'; slot pending transforms "
                    + "require an empty slot.");
            }

            Result<SlotPendingEntry> stored = _slotPendings.Store(
                sceneIdentity,
                command,
                createdWindowSequence);
            if (!stored.Success || stored.Value == null)
            {
                return ApiResult<SlotPendingStoredSnapshot>.Fail(stored.Error);
            }

            return ApiResult<SlotPendingStoredSnapshot>.Ok(
                new SlotPendingStoredSnapshot(
                    stored.Value.SceneIdentity,
                    stored.Value.PublicSlot,
                    canonicalDirective,
                    stored.Value.CreatedWindowSequence));
        }
        catch (Exception ex)
        {
            return ApiResult<SlotPendingStoredSnapshot>.Fail(
                $"Slot pending storage failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public ApiResult<IReadOnlyList<SlotPendingOutcome>> ScanSlotPendingsOnMainThread(
        string? currentSceneIdentity,
        long currentWindowSequence)
    {
        if (!IsMainThread())
        {
            return ApiResult<IReadOnlyList<SlotPendingOutcome>>.Fail(
                "Slot pending scans may only run on the Unity main thread.");
        }

        try
        {
            var outcomes = new List<SlotPendingOutcome>();
            foreach (SlotPendingEntry expired in _slotPendings.ExpireBefore(currentWindowSequence))
            {
                outcomes.Add(new SlotPendingOutcome(
                    expired.PublicSlot,
                    SlotPendingOutcomeStatus.Expired,
                    null,
                    null,
                    null,
                    $"pending was not consumed within the expiry window count"));
            }

            Test? player = Test.Instance;
            if (ReferenceEquals(player, null) || ReferenceEquals(player.slots, null))
            {
                Array.Fill(_previousSlotPresence, null);
                return ApiResult<IReadOnlyList<SlotPendingOutcome>>.Ok(outcomes);
            }

            Character?[] slots = player.slots;
            for (int arrayIndex = 0; arrayIndex < _previousSlotPresence.Length; arrayIndex++)
            {
                if (arrayIndex >= slots.Length)
                {
                    break;
                }

                bool presence = !ReferenceEquals(slots[arrayIndex], null);
                bool wasPresent = _previousSlotPresence[arrayIndex] == true;
                _previousSlotPresence[arrayIndex] = presence;
                if (!presence || wasPresent)
                {
                    continue;
                }

                int publicSlot = arrayIndex + 1;
                if (!_slotPendings.TryConsume(publicSlot, out SlotPendingEntry? entry))
                {
                    continue;
                }

                outcomes.Add(ApplyConsumedPending(currentSceneIdentity, entry));
            }

            return ApiResult<IReadOnlyList<SlotPendingOutcome>>.Ok(outcomes);
        }
        catch (Exception ex)
        {
            return ApiResult<IReadOnlyList<SlotPendingOutcome>>.Fail(
                $"Slot pending scan failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public ApiResult<bool> ClearSlotPendings()
    {
        if (!IsMainThread())
        {
            return ApiResult<bool>.Fail(
                "Slot pending transforms may only be cleared on the Unity main thread.");
        }

        _slotPendings.Clear();
        Array.Fill(_previousSlotPresence, null);
        return ApiResult<bool>.Ok(true);
    }

    private SlotPendingOutcome ApplyConsumedPending(
        string? currentSceneIdentity,
        SlotPendingEntry entry)
    {
        int publicSlot = entry.PublicSlot;
        if (string.IsNullOrWhiteSpace(currentSceneIdentity))
        {
            return new SlotPendingOutcome(
                publicSlot,
                SlotPendingOutcomeStatus.Failed,
                null,
                null,
                null,
                "no current scene identity was available for baseline capture");
        }

        if (!Plugin.Host.CharacterPresetsInternal.SuspendOverlay(publicSlot))
        {
            return new SlotPendingOutcome(publicSlot, SlotPendingOutcomeStatus.Failed,
                null, null, null, "character preset cleanup is pending; no pending baseline was captured");
        }

        ApiResult<LiveCharacter> resolved = Resolve(publicSlot);
        if (!resolved.Success || resolved.Value == null)
        {
            return new SlotPendingOutcome(
                publicSlot,
                SlotPendingOutcomeStatus.Failed,
                null,
                null,
                null,
                ReasonOrUnknown(resolved.Error));
        }

        LiveCharacter live = resolved.Value;
        CharacterTransformState before = ReadState(live.Character, live.Transform);
        Result<CharacterTransformOriginBaseline> official = _officialOrigins.Capture(
            string.Empty,
            publicSlot,
            live.OccupantIdentifier,
            live.ManagedInstanceId,
            before);
        if (!official.Success || official.Value == null)
        {
            return new SlotPendingOutcome(
                publicSlot,
                SlotPendingOutcomeStatus.Failed,
                live.OccupantIdentifier,
                before.Position.X,
                null,
                ReasonOrUnknown(official.Error));
        }

        Result<CharacterTransformTarget> planned = SlotPendingTargetPlanner.Compute(
            entry.Command,
            before);
        if (!planned.Success || planned.Value == null)
        {
            return new SlotPendingOutcome(
                publicSlot,
                SlotPendingOutcomeStatus.Failed,
                live.OccupantIdentifier,
                before.Position.X,
                null,
                ReasonOrUnknown(planned.Error));
        }

        ApiResult<bool> applied = Apply(live, planned.Value);
        if (!applied.Success)
        {
            return new SlotPendingOutcome(
                publicSlot,
                SlotPendingOutcomeStatus.Failed,
                live.OccupantIdentifier,
                before.Position.X,
                null,
                ReasonOrUnknown(applied.Error));
        }

        CharacterTransformState actual = ReadState(live.Character, live.Transform);
        var captured = _baselines.Capture(
            currentSceneIdentity,
            publicSlot,
            live.OccupantIdentifier,
            actual);
        if (!captured.Success)
        {
            return new SlotPendingOutcome(
                publicSlot,
                SlotPendingOutcomeStatus.Failed,
                live.OccupantIdentifier,
                before.Position.X,
                actual.Position.X,
                ReasonOrUnknown(captured.Error));
        }

        return new SlotPendingOutcome(
            publicSlot,
            SlotPendingOutcomeStatus.Applied,
            live.OccupantIdentifier,
            before.Position.X,
            actual.Position.X,
            null);
    }

    private static string ReasonOrUnknown(string? message) =>
        string.IsNullOrWhiteSpace(message) ? "unknown" : message;

    private ApiResult<ReplayPreparation> PrepareReplay(
        string sceneIdentity,
        CharacterTransformCommand command)
    {
        if (string.IsNullOrWhiteSpace(sceneIdentity))
        {
            return ApiResult<ReplayPreparation>.Fail(
                "Scene identity is required for replay preparation.");
        }

        var validation = CharacterTransformCommandValidator.Validate(command);
        if (!validation.Success)
        {
            return ApiResult<ReplayPreparation>.Fail(validation.Error);
        }

        if (!Plugin.Host.CharacterPresetsInternal.SuspendOverlay(command.PublicSlot))
        {
            return ApiResult<ReplayPreparation>.Fail(
                "Character preset cleanup is pending; no replay baseline was captured.");
        }

        ApiResult<LiveCharacter> resolved = Resolve(command.PublicSlot);
        if (!resolved.Success || resolved.Value == null)
        {
            return ApiResult<ReplayPreparation>.Fail(resolved.Error);
        }

        LiveCharacter live = resolved.Value;
        CharacterTransformState current = ReadState(live.Character, live.Transform);
        var baselineResult = _baselines.Get(
            sceneIdentity,
            command.PublicSlot,
            live.OccupantIdentifier);
        if (!baselineResult.Success || baselineResult.Value == null)
        {
            var captured = _baselines.Capture(
                sceneIdentity,
                command.PublicSlot,
                live.OccupantIdentifier,
                current);
            if (!captured.Success || captured.Value == null)
            {
                return ApiResult<ReplayPreparation>.Fail(captured.Error);
            }

            return ApiResult<ReplayPreparation>.Ok(new ReplayPreparation(true));
        }

        var planned = _planner.PlanReplayRestore(
            command,
            current,
            baselineResult.Value);
        if (!planned.Success || planned.Value == null)
        {
            return ApiResult<ReplayPreparation>.Fail(planned.Error);
        }

        ApiResult<bool> applied = Apply(live, planned.Value);
        if (!applied.Success)
        {
            return ApiResult<ReplayPreparation>.Fail(applied.Error);
        }

        return ApiResult<ReplayPreparation>.Ok(new ReplayPreparation(false));
    }

    private static ApiResult<LiveCharacter> Resolve(int publicSlot)
    {
        Test? player = Test.Instance;
        if (ReferenceEquals(player, null))
        {
            return ApiResult<LiveCharacter>.Fail("Story player is not active.");
        }

        var slots = player.slots;
        if (ReferenceEquals(slots, null))
        {
            return ApiResult<LiveCharacter>.Fail("Story player slots are unavailable.");
        }

        int arrayIndex = publicSlot - 1;
        if (arrayIndex < 0 || arrayIndex >= slots.Length)
        {
            return ApiResult<LiveCharacter>.Fail("Physical character slot is outside the live slot array.");
        }

        Character? character = slots[arrayIndex];
        if (ReferenceEquals(character, null))
        {
            return ApiResult<LiveCharacter>.Fail($"Physical character slot {publicSlot} is empty.");
        }

        Transform? transform = character.transform;
        if (ReferenceEquals(transform, null))
        {
            return ApiResult<LiveCharacter>.Fail("Character root Transform is unavailable.");
        }

        string occupantIdentifier = character.identifier ?? string.Empty;
        if (string.IsNullOrWhiteSpace(occupantIdentifier))
        {
            return ApiResult<LiveCharacter>.Fail("Character occupant identifier is unavailable.");
        }

        int managedInstanceId = character.GetInstanceID();
        if (managedInstanceId == 0)
        {
            return ApiResult<LiveCharacter>.Fail(
                "Character native instance identity is unavailable.");
        }

        return ApiResult<LiveCharacter>.Ok(
            new LiveCharacter(
                character,
                transform,
                occupantIdentifier,
                managedInstanceId));
    }

    private static CharacterTransformState ReadState(Character character, Transform transform)
    {
        Vector3 position = character.GetPos();
        Vector3 euler = transform.localEulerAngles;
        return new CharacterTransformState(
            new CharacterVector3(position.x, position.y, position.z),
            new CharacterVector3(
                euler.x,
                euler.y,
                CharacterScreenRotation.ToScreenDegrees(euler.z, euler.y)));
    }

    private static ApiResult<bool> Apply(LiveCharacter live, CharacterTransformTarget target)
    {
        CharacterTransformState before = ReadState(live.Character, live.Transform);
        float durationSeconds = target.DurationMilliseconds / 1000f;
        try
        {
            if (target.PositionChanged)
            {
                CharacterVector3 position = target.State.Position;
                var nativePosition = new Vector3(position.X, position.Y, position.Z);
                if (target.DurationMilliseconds == 0)
                {
                    ApiResult<bool> immediate = SetPositionImmediately(live, nativePosition);
                    if (!immediate.Success)
                    {
                        return FailAfterRollback(live, target, before, immediate.Error);
                    }
                }
                else
                {
                    TweenPosition? existing = live.Character.posTweener;
                    if (ReferenceEquals(existing, null))
                    {
                        return FailAfterRollback(
                            live,
                            target,
                            before,
                            "Character position tweener is unavailable.");
                    }

                    TweenPosition? tween = TweenPosition.Begin(
                        existing.gameObject,
                        durationSeconds,
                        nativePosition);
                    if (ReferenceEquals(tween, null))
                    {
                        return FailAfterRollback(
                            live,
                            target,
                            before,
                            "TweenPosition.Begin returned null.");
                    }

                    tween.method = MapEasing(target.Easing);
                }
            }

            if (target.RotationChanged)
            {
                CharacterVector3 euler = target.State.LocalEulerAngles;
                Vector3 nativeFromEuler = live.Transform.localEulerAngles;
                float physicalEulerZ = CharacterScreenRotation.ToPhysicalDegreesFromLive(
                    euler.Z,
                    euler.Y,
                    nativeFromEuler.z);
                var nativeEuler = new Vector3(euler.X, euler.Y, physicalEulerZ);
                if (target.DurationMilliseconds == 0)
                {
                    ApiResult<bool> immediate = SetRotationImmediately(live, nativeEuler);
                    if (!immediate.Success)
                    {
                        return FailAfterRollback(live, target, before, immediate.Error);
                    }
                }
                else
                {
                    TweenRotation? tween = TweenRotation.Begin(
                        live.Transform.gameObject,
                        durationSeconds,
                        Quaternion.Euler(nativeEuler));
                    if (ReferenceEquals(tween, null))
                    {
                        return FailAfterRollback(
                            live,
                            target,
                            before,
                            "TweenRotation.Begin returned null.");
                    }

                    // Begin converts the target quaternion back to 0..360 Euler angles. Preserve the
                    // planner's nearest equivalent so -10 and 370 do not become 350 and 10-degree spins.
                    tween.from = nativeFromEuler;
                    tween.to = nativeEuler;
                    tween.quaternionLerp = false;
                    tween.method = MapEasing(target.Easing);
                }
            }

            return ApiResult<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return FailAfterRollback(
                live,
                target,
                before,
                $"Character transform native write failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static ApiResult<bool> FailAfterRollback(
        LiveCharacter live,
        CharacterTransformTarget attempted,
        CharacterTransformState before,
        string reason)
    {
        var failures = new List<string>();
        if (attempted.PositionChanged)
        {
            CharacterVector3 position = before.Position;
            ApiResult<bool> restored = SetPositionImmediately(
                live,
                new Vector3(position.X, position.Y, position.Z));
            if (!restored.Success)
            {
                failures.Add("position:" + restored.Error);
            }
        }

        if (attempted.RotationChanged)
        {
            try
            {
                CharacterVector3 euler = before.LocalEulerAngles;
                Vector3 currentNative = live.Transform.localEulerAngles;
                float physicalEulerZ = CharacterScreenRotation.ToPhysicalDegreesFromLive(
                    euler.Z,
                    euler.Y,
                    currentNative.z);
                ApiResult<bool> restored = SetRotationImmediately(
                    live,
                    new Vector3(euler.X, euler.Y, physicalEulerZ));
                if (!restored.Success)
                {
                    failures.Add("rotation:" + restored.Error);
                }
            }
            catch (Exception ex)
            {
                failures.Add(
                    $"rotation:{ex.GetType().Name}:{ex.Message}");
            }
        }

        string rollback = failures.Count == 0
            ? "succeeded"
            : "failed(" + string.Join("|", failures) + "); "
                + RuntimeMutationFailure.ResidualMutationMarker;
        return ApiResult<bool>.Fail($"{reason}; rollback={rollback}");
    }

    private static ApiResult<bool> SetPositionImmediately(
        LiveCharacter live,
        Vector3 nativePosition)
    {
        try
        {
            // Character.posTweener is the runtime-proven position component.
            // Disable it before the direct write so deleting/replaying a long
            // move cannot resume on the following frame.
            TweenPosition? existing = live.Character.posTweener;
            if (!ReferenceEquals(existing, null))
            {
                existing.enabled = false;
            }

            live.Character.SetPos(nativePosition);
            return ApiResult<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return ApiResult<bool>.Fail(
                $"Immediate character position write failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static ApiResult<bool> SetRotationImmediately(
        LiveCharacter live,
        Vector3 nativeEuler)
    {
        try
        {
            // NGUI reuses the TweenRotation component returned by Begin. A
            // zero-duration Begin followed by enabled=false supersedes any
            // Mod-started long tween without retaining the IL2CPP wrapper.
            TweenRotation? tween = TweenRotation.Begin(
                live.Transform.gameObject,
                0f,
                Quaternion.Euler(nativeEuler));
            if (ReferenceEquals(tween, null))
            {
                return ApiResult<bool>.Fail("TweenRotation.Begin returned null.");
            }

            tween.enabled = false;
            live.Transform.localEulerAngles = nativeEuler;
            return ApiResult<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return ApiResult<bool>.Fail(
                $"Immediate character rotation write failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static UITweener.Method MapEasing(CharacterTransformEasing easing) => easing switch
    {
        CharacterTransformEasing.Linear => UITweener.Method.Linear,
        CharacterTransformEasing.EaseIn => UITweener.Method.EaseIn,
        CharacterTransformEasing.EaseOut => UITweener.Method.EaseOut,
        CharacterTransformEasing.EaseInOut => UITweener.Method.EaseInOut,
        _ => UITweener.Method.Linear
    };

    private static float NormalizeDegrees(float value)
    {
        float normalized = value % 360f;
        return normalized < 0f ? normalized + 360f : normalized;
    }

    private bool IsMainThread() => Environment.CurrentManagedThreadId == _mainThreadId;

    private sealed record LiveCharacter(
        Character Character,
        Transform Transform,
        string OccupantIdentifier,
        long ManagedInstanceId);

    private sealed record ReplayPreparation(bool BaselineCaptured);
}
