extern alias unitycore;

using System.Diagnostics;
using AzureArchive.VideoTools.Api;
using AzureArchive.VideoTools.Core;
using AzureArchive.VideoTools.Core.Cameras;
using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Results;
using Transform = unitycore::UnityEngine.Transform;
using Vector3 = unitycore::UnityEngine.Vector3;

namespace AzureArchive.VideoTools.Interop;

internal sealed class SceneCameraService : ISceneCameraService, ISceneCameraCommandDispatcher,
    IScopedSceneCameraService, IScopedSceneCameraCommandDispatcher, ISceneCameraMutationEvidenceDispatcher
{
    private const int MaximumAncestorDepth = 16;

    private readonly int _mainThreadId = Environment.CurrentManagedThreadId;
    private readonly SceneCameraDirectiveParser _parser = new();
    private readonly SceneCameraPlanner _planner = new();
    private readonly Dictionary<(string Scene, SceneCameraScope Scope), SceneCameraBaseline> _sceneEntries = new();

    private int? _backInstanceId;
    private int? _spineInstanceId;
    private LayerBaseline? _backBaseline;
    private LayerBaseline? _spineBaseline;
    private SceneCameraMotion _motion = new();
    private SceneCameraComposition _appliedComposition = SceneCameraComposition.Default;
    private SceneCameraMotionFields _pendingWrites;
    private SceneCameraPhysicalWrites _writtenLayers;

    public SceneCameraService(RuntimeCapabilityService capabilities)
    {
        capabilities.Bound(
            SceneCameraCommandFamilyCompiler.CapabilityId,
            "main-thread Back/Spine scene camera service bound; live mutation proof pending");
    }

    public ApiResult<SceneCameraReadSnapshot> ReadOnMainThread()
    {
        if (!IsMainThread())
        {
            return ApiResult<SceneCameraReadSnapshot>.Fail(
                "Scene camera state may only be read on the Unity main thread.");
        }

        try
        {
            ApiResult<LiveSceneLayers> resolved = ResolveLayers();
            if (!resolved.Success || resolved.Value == null)
            {
                return ApiResult<SceneCameraReadSnapshot>.Fail(resolved.Error);
            }

            LiveSceneLayers layers = resolved.Value;
            ApiResult<bool> baselineReady = EnsurePhysicalBaselines(layers);
            if (!baselineReady.Success)
            {
                return ApiResult<SceneCameraReadSnapshot>.Fail(baselineReady.Error);
            }

            return ApiResult<SceneCameraReadSnapshot>.Ok(new(
                _motion.Target.Overall,
                layers.Back.GetInstanceID(),
                layers.Spine.GetInstanceID(),
                _backBaseline.HasValue && _spineBaseline.HasValue));
        }
        catch (Exception ex)
        {
            return ApiResult<SceneCameraReadSnapshot>.Fail(
                $"Scene camera state read failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public ApiResult<SceneCameraScopedReadSnapshot> ReadScopedOnMainThread()
    {
        ApiResult<SceneCameraReadSnapshot> read = ReadOnMainThread();
        return read.Success && read.Value != null
            ? ApiResult<SceneCameraScopedReadSnapshot>.Ok(new(
                _motion.Target, read.Value.BackInstanceId, read.Value.SpineInstanceId,
                read.Value.PhysicalBaselinesCaptured))
            : ApiResult<SceneCameraScopedReadSnapshot>.Fail(read.Error);
    }

    public ApiResult<SceneCameraExecutionSnapshot> ExecuteOnMainThread(
        string sceneIdentity,
        string canonicalDirective)
    {
        if (!IsMainThread())
        {
            return ApiResult<SceneCameraExecutionSnapshot>.Fail(
                "Scene camera commands may only execute on the Unity main thread.");
        }

        try
        {
            if (string.IsNullOrWhiteSpace(sceneIdentity))
            {
                return ApiResult<SceneCameraExecutionSnapshot>.Fail(
                    "Scene identity is required for a scene camera command.");
            }

            if (string.IsNullOrWhiteSpace(canonicalDirective)
                || !canonicalDirective.StartsWith(
                    SceneCameraCommandFamilyCompiler.CanonicalRootToken + ";",
                    StringComparison.Ordinal))
            {
                return ApiResult<SceneCameraExecutionSnapshot>.Fail(
                    "Scene camera execution requires a canonical #camera; directive.");
            }

            var parsed = _parser.Parse(canonicalDirective);
            if (!parsed.Success || parsed.Value == null)
            {
                return ApiResult<SceneCameraExecutionSnapshot>.Fail(parsed.Error);
            }

            ApiResult<LiveSceneLayers> resolved = ResolveLayers();
            if (!resolved.Success || resolved.Value == null)
            {
                return ApiResult<SceneCameraExecutionSnapshot>.Fail(resolved.Error);
            }

            LiveSceneLayers layers = resolved.Value;
            ApiResult<bool> baselineReady = EnsurePhysicalBaselines(layers);
            if (!baselineReady.Success)
            {
                return ApiResult<SceneCameraExecutionSnapshot>.Fail(baselineReady.Error);
            }

            SceneCameraCommand command = parsed.Value;
            var entryKey = (sceneIdentity, command.Scope);
            bool baselineCaptured = !_sceneEntries.TryGetValue(
                entryKey,
                out SceneCameraBaseline? existingEntry);
            SceneCameraState current = _motion.Target.ForScope(command.Scope);
            SceneCameraBaseline sceneEntry = existingEntry ?? new SceneCameraBaseline(sceneIdentity, current);
            SceneCameraMotion candidate = _motion.Copy();
            double now = MonotonicSeconds();
            bool replayRestored = false;
            SceneCameraTarget? replayRestore = null;
            if (!baselineCaptured)
            {
                var restore = _planner.PlanReplayRestore(command, current, sceneEntry);
                if (!restore.Success || restore.Value == null)
                {
                    return ApiResult<SceneCameraExecutionSnapshot>.Fail(restore.Error);
                }

                replayRestore = restore.Value;
                replayRestored = true;
            }

            SceneCameraState before = replayRestore?.State ?? candidate.Target.ForScope(command.Scope);
            var planned = _planner.Plan(command, before, sceneEntry);
            if (!planned.Success || planned.Value == null)
            {
                return ApiResult<SceneCameraExecutionSnapshot>.Fail(planned.Error);
            }

            SceneCameraTarget target = planned.Value;
            Result begun = replayRestore == null
                ? candidate.Begin(command, target, now)
                : candidate.BeginReplay(command, replayRestore, target, now);
            if (!begun.Success) return ApiResult<SceneCameraExecutionSnapshot>.Fail(begun.Error);
            Result<SceneCameraComposition> sampled = candidate.Sample(now);
            if (!sampled.Success) return ApiResult<SceneCameraExecutionSnapshot>.Fail(sampled.Error);
            SceneCameraMotionFields commandFields = SceneCameraMotion.FieldsFor(command);
            ApiResult<bool> applied = ApplyComposition(
                layers, sampled.Value, commandFields | _pendingWrites | candidate.ActiveFields(now), commandFields);
            if (!applied.Success)
            {
                return ApiResult<SceneCameraExecutionSnapshot>.Fail(applied.Error);
            }

            _motion = candidate;
            _appliedComposition = sampled.Value;
            _pendingWrites = candidate.ActiveFields(now);
            if (baselineCaptured)
            {
                _sceneEntries.Add(entryKey, sceneEntry);
            }

            return ApiResult<SceneCameraExecutionSnapshot>.Ok(
                new SceneCameraExecutionSnapshot(
                    sceneIdentity,
                    command,
                    before,
                    target.State,
                    target.PositionChanged,
                    target.ZoomChanged,
                    baselineCaptured,
                    replayRestored,
                    target.IsReset)
                {
                    Scope = command.Scope,
                    Composition = candidate.Target
                });
        }
        catch (Exception ex)
        {
            return ApiResult<SceneCameraExecutionSnapshot>.Fail(
                $"Scene camera execution failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Scene camera inheritance for the editor preview (2026-09-18).
    /// <para>
    /// Seeds this scene's entry state with the state the scene would already be
    /// showing in sequential playback and applies it instantly. Seeding the
    /// entry is the whole point: <see cref="ExecuteOnMainThread"/> replay-restores
    /// every command to its scene entry before planning, so a synthetic
    /// <c>set</c> dispatch would be wiped by the very next relative command of
    /// the same scene, while a seeded entry keeps the inherited composition as
    /// the base for that command and for green replays.
    /// </para>
    /// </summary>
    public ApiResult<SceneCameraInheritedStartSnapshot> ApplyInheritedStartOnMainThread(
        string sceneIdentity,
        SceneCameraState inheritedState)
    {
        ApiResult<SceneCameraScopedInheritedStartSnapshot> result = ApplyInheritedStartOnMainThread(
            sceneIdentity, new SceneCameraComposition(inheritedState, SceneCameraState.Default), true, false);
        return result.Success && result.Value != null
            ? ApiResult<SceneCameraInheritedStartSnapshot>.Ok(new(
                result.Value.SceneIdentity, result.Value.Before.Overall, result.Value.Applied.Overall,
                result.Value.BaselineCaptured))
            : ApiResult<SceneCameraInheritedStartSnapshot>.Fail(result.Error);
    }

    public ApiResult<SceneCameraScopedInheritedStartSnapshot> ApplyInheritedStartOnMainThread(
        string sceneIdentity,
        SceneCameraComposition inheritedState,
        bool hasOverall,
        bool hasBackground) =>
        ApplyInheritedStartWithFailureEvidenceOnMainThread(
            sceneIdentity, inheritedState, hasOverall, hasBackground, out _);

    public ApiResult<SceneCameraScopedInheritedStartSnapshot> ApplyInheritedStartWithFailureEvidenceOnMainThread(
        string sceneIdentity,
        SceneCameraComposition inheritedState,
        bool hasOverall,
        bool hasBackground,
        out SceneCameraMutationFailureEvidence failure)
    {
        // Every early return replaces the caller's prior evidence. No failure
        // fields are retained between calls or borrowed from _writtenLayers.
        failure = default;
        bool physicalApplied = false;
        if (!IsMainThread())
        {
            return ApiResult<SceneCameraScopedInheritedStartSnapshot>.Fail(
                "Scene camera inherited starts may only be applied on the Unity main thread.");
        }

        try
        {
            if (string.IsNullOrWhiteSpace(sceneIdentity))
            {
                return ApiResult<SceneCameraScopedInheritedStartSnapshot>.Fail(
                    "Scene identity is required for a scene camera inherited start.");
            }

            ApiResult<LiveSceneLayers> resolved = ResolveLayers();
            if (!resolved.Success || resolved.Value == null)
            {
                return ApiResult<SceneCameraScopedInheritedStartSnapshot>.Fail(resolved.Error);
            }

            ApiResult<bool> baselineReady = EnsurePhysicalBaselines(resolved.Value);
            if (!baselineReady.Success)
            {
                return ApiResult<SceneCameraScopedInheritedStartSnapshot>.Fail(baselineReady.Error);
            }

            SceneCameraComposition before = _motion.Target;
            SceneCameraMotion candidate = _motion.Copy();
            double now = MonotonicSeconds();
            Result seeded = candidate.Seed(inheritedState, hasOverall, hasBackground, now);
            if (!seeded.Success) return ApiResult<SceneCameraScopedInheritedStartSnapshot>.Fail(seeded.Error);
            SceneCameraMotionFields fields = (hasOverall
                ? SceneCameraMotionFields.OverallPosition | SceneCameraMotionFields.OverallZoom : 0)
                | (hasBackground ? SceneCameraMotionFields.BackgroundPosition | SceneCameraMotionFields.BackgroundZoom : 0);
            Result<SceneCameraComposition> sampled = candidate.Sample(now);
            if (!sampled.Success) return ApiResult<SceneCameraScopedInheritedStartSnapshot>.Fail(sampled.Error);
            ApiResult<bool> applied = ApplyComposition(
                resolved.Value, sampled.Value, fields | _pendingWrites | candidate.ActiveFields(now), fields,
                out failure);
            if (!applied.Success)
            {
                return ApiResult<SceneCameraScopedInheritedStartSnapshot>.Fail(
                    $"Scene camera inherited start could not be applied: {applied.Error}");
            }
            physicalApplied = true;

            bool baselineCaptured = false;
            if (hasOverall)
            {
                baselineCaptured |= !_sceneEntries.ContainsKey((sceneIdentity, SceneCameraScope.Overall));
                _sceneEntries[(sceneIdentity, SceneCameraScope.Overall)] = new(sceneIdentity, inheritedState.Overall);
            }
            if (hasBackground)
            {
                baselineCaptured |= !_sceneEntries.ContainsKey((sceneIdentity, SceneCameraScope.Background));
                _sceneEntries[(sceneIdentity, SceneCameraScope.Background)] = new(sceneIdentity, inheritedState.Background);
            }
            _motion = candidate;
            _appliedComposition = sampled.Value;
            _pendingWrites = candidate.ActiveFields(now);
            return ApiResult<SceneCameraScopedInheritedStartSnapshot>.Ok(new(
                sceneIdentity,
                before,
                candidate.Target,
                baselineCaptured));
        }
        catch (Exception ex)
        {
            // A managed commit failure after the setters also needs cleanup;
            // the evidence still belongs to this exact application.
            if (physicalApplied)
                failure = failure with { HasResidualMutation = failure.AttemptedFields != SceneCameraMotionFields.None };
            return ApiResult<SceneCameraScopedInheritedStartSnapshot>.Fail(
                $"Scene camera inherited start failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void UpdateOnMainThread()
    {
        if (!IsMainThread() || _pendingWrites == SceneCameraMotionFields.None) return;
        try
        {
            ApiResult<LiveSceneLayers> resolved = ResolveLayers();
            if (!resolved.Success || resolved.Value == null)
            {
                StopMotion(resolved.Error);
                return;
            }
            ApiResult<bool> baseline = EnsurePhysicalBaselines(resolved.Value);
            if (!baseline.Success)
            {
                StopMotion(baseline.Error);
                return;
            }
            if (_pendingWrites == SceneCameraMotionFields.None) return;
            double now = MonotonicSeconds();
            Result<SceneCameraComposition> sample = _motion.Sample(now);
            if (!sample.Success)
            {
                StopMotion(sample.Error);
                return;
            }
            SceneCameraMotionFields active = _motion.ActiveFields(now);
            ApiResult<bool> applied = ApplyComposition(resolved.Value, sample.Value, _pendingWrites | active,
                SceneCameraMotionFields.None);
            if (!applied.Success)
            {
                StopMotion(applied.Error);
                return;
            }
            _appliedComposition = sample.Value;
            _pendingWrites = active;
        }
        catch (Exception ex)
        {
            StopMotion($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void StopMotion(string reason)
    {
        _motion = new SceneCameraMotion(_appliedComposition);
        _pendingWrites = SceneCameraMotionFields.None;
        Plugin.Logger.LogWarning("Scene camera animation stopped: " + reason);
    }

    private static double MonotonicSeconds() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private ApiResult<bool> EnsurePhysicalBaselines(LiveSceneLayers layers)
    {
        int backInstanceId = layers.Back.GetInstanceID();
        int spineInstanceId = layers.Spine.GetInstanceID();
        bool sameBack = _backInstanceId == backInstanceId;
        bool sameSpine = _spineInstanceId == spineInstanceId;
        if (sameBack
            && sameSpine
            && _backBaseline.HasValue
            && _spineBaseline.HasValue)
        {
            return ApiResult<bool>.Ok(true);
        }

        if (sameBack != sameSpine
            && _backBaseline.HasValue
            && _spineBaseline.HasValue)
        {
            var failures = new List<string>();
            SceneCameraPhysicalWrites owned = _writtenLayers;
            if (sameBack)
            {
                RollBackLayer(
                    "Back-survivor",
                    layers.Back,
                    _backBaseline.Value,
                    restorePosition: owned.BackPosition,
                    restoreScale: owned.BackScale,
                    failures);
            }
            else
            {
                RollBackLayer(
                    "Spine-survivor",
                    layers.Spine,
                    _spineBaseline.Value,
                    restorePosition: owned.SpinePosition,
                    restoreScale: owned.SpineScale,
                    failures);
            }

            if (failures.Count != 0)
            {
                return ApiResult<bool>.Fail(
                    "A surviving scene camera layer could not be restored before "
                    + $"baseline recapture: {string.Join("|", failures)}");
            }
        }

        LayerBaseline back = ReadLayer(layers.Back);
        LayerBaseline spine = ReadLayer(layers.Spine);
        if (!back.IsFinite || !spine.IsFinite)
        {
            return ApiResult<bool>.Fail(
                "Back or Spine has a non-finite local transform and cannot establish a camera baseline.");
        }

        _backInstanceId = backInstanceId;
        _spineInstanceId = spineInstanceId;
        _backBaseline = back;
        _spineBaseline = spine;
        _motion = new SceneCameraMotion();
        _appliedComposition = SceneCameraComposition.Default;
        _pendingWrites = SceneCameraMotionFields.None;
        _writtenLayers = default;
        _sceneEntries.Clear();
        return ApiResult<bool>.Ok(true);
    }

    private ApiResult<bool> ApplyComposition(
        LiveSceneLayers layers,
        SceneCameraComposition composition,
        SceneCameraMotionFields fields,
        SceneCameraMotionFields cancelTweens) =>
        ApplyComposition(layers, composition, fields, cancelTweens, out _);

    private ApiResult<bool> ApplyComposition(
        LiveSceneLayers layers,
        SceneCameraComposition composition,
        SceneCameraMotionFields fields,
        SceneCameraMotionFields cancelTweens,
        out SceneCameraMutationFailureEvidence failure)
    {
        failure = default;
        if (!_backBaseline.HasValue || !_spineBaseline.HasValue)
        {
            return ApiResult<bool>.Fail("Scene camera physical baselines are unavailable.");
        }

        Result<SceneCameraComposedLayers> composed = SceneCameraCompositionPlanner.Compose(composition);
        if (!composed.Success || composed.Value == null) return ApiResult<bool>.Fail(composed.Error);
        SceneCameraPhysicalWrites writes = SceneCameraCompositionPlanner.WritesFor(fields);
        SceneCameraPhysicalWrites cancel = SceneCameraCompositionPlanner.WritesFor(cancelTweens);
        LayerTarget backTarget = MapTarget(_backBaseline.Value, composed.Value.Back);
        LayerTarget spineTarget = MapTarget(_spineBaseline.Value, composed.Value.Spine);
        if (!backTarget.IsFinite || !spineTarget.IsFinite)
        {
            return ApiResult<bool>.Fail(
                "Scene camera target overflows the finite Back/Spine transform range.");
        }

        LayerBaseline backBefore = ReadLayer(layers.Back);
        LayerBaseline spineBefore = ReadLayer(layers.Spine);
        if (!backBefore.IsFinite || !spineBefore.IsFinite)
        {
            return ApiResult<bool>.Fail(
                "Back or Spine has a non-finite pre-application transform; rollback is unsafe.");
        }

        SceneCameraPhysicalWrites attempted = default;
        var mutationAttempt = new SceneCameraMutationAttempt(fields);
        try
        {
            WriteLayer(
                layers.Back,
                isBack: true,
                backTarget,
                writes.BackPosition,
                writes.BackScale,
                cancel.BackPosition,
                cancel.BackScale,
                ref attempted,
                mutationAttempt);
            WriteLayer(
                layers.Spine,
                isBack: false,
                spineTarget,
                writes.SpinePosition,
                writes.SpineScale,
                cancel.SpinePosition,
                cancel.SpineScale,
                ref attempted,
                mutationAttempt);
            failure = mutationAttempt.Evidence(hasResidualMutation: false);
            return ApiResult<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            var failures = new List<string>();
            RollBackLayer("Back", layers.Back, backBefore, attempted.BackPosition, attempted.BackScale, failures);
            RollBackLayer("Spine", layers.Spine, spineBefore, attempted.SpinePosition, attempted.SpineScale, failures);
            string rollback = failures.Count == 0 ? "succeeded" : string.Join("|", failures);
            failure = mutationAttempt.Evidence(hasResidualMutation: failures.Count != 0);
            string residual = string.Equals(rollback, "succeeded", StringComparison.Ordinal)
                ? string.Empty
                : "; " + RuntimeMutationFailure.ResidualMutationMarker;
            return ApiResult<bool>.Fail(
                $"Back/Spine camera application failed: {ex.GetType().Name}: {ex.Message}; "
                + $"rollback={rollback}{residual}");
        }
    }

    private void WriteLayer(
        Transform transform,
        bool isBack,
        LayerTarget target,
        bool applyPosition,
        bool applyScale,
        bool cancelPositionTween,
        bool cancelScaleTween,
        ref SceneCameraPhysicalWrites attempted,
        SceneCameraMutationAttempt mutationAttempt)
    {
        if (applyPosition)
        {
            var position = target.Position.ToNative();
            mutationAttempt.BeginPositionWrite(isBack);
            // Immediate native setters write in their finally block even if
            // tween cancellation throws. Retain attempted-axis ownership so a
            // later single-root rebuild cannot absorb a residual mutation.
            _writtenLayers = isBack ? _writtenLayers with { BackPosition = true }
                : _writtenLayers with { SpinePosition = true };
            attempted = isBack ? attempted with { BackPosition = true }
                : attempted with { SpinePosition = true };
            if (cancelPositionTween)
            {
                SetPositionImmediately(transform, position);
            }
            else
            {
                transform.localPosition = position;
            }
        }

        if (applyScale)
        {
            var scale = target.Scale.ToNative();
            mutationAttempt.BeginScaleWrite(isBack);
            _writtenLayers = isBack ? _writtenLayers with { BackScale = true }
                : _writtenLayers with { SpineScale = true };
            attempted = isBack ? attempted with { BackScale = true }
                : attempted with { SpineScale = true };
            if (cancelScaleTween)
            {
                SetScaleImmediately(transform, scale);
            }
            else
            {
                transform.localScale = scale;
            }
        }
    }

    private static void SetPositionImmediately(Transform transform, Vector3 position)
    {
        try
        {
            TweenPosition? tween = TweenPosition.Begin(
                transform.gameObject,
                0f,
                position,
                false);
            if (ReferenceEquals(tween, null))
            {
                throw new InvalidOperationException("TweenPosition.Begin returned null.");
            }

            tween.enabled = false;
        }
        finally
        {
            transform.localPosition = position;
        }
    }

    private static void SetScaleImmediately(Transform transform, Vector3 scale)
    {
        try
        {
            TweenScale? tween = TweenScale.Begin(transform.gameObject, 0f, scale);
            if (ReferenceEquals(tween, null))
            {
                throw new InvalidOperationException("TweenScale.Begin returned null.");
            }

            tween.enabled = false;
        }
        finally
        {
            transform.localScale = scale;
        }
    }

    private static void RollBackLayer(
        string label,
        Transform transform,
        LayerBaseline before,
        bool restorePosition,
        bool restoreScale,
        ICollection<string> failures)
    {
        if (restorePosition)
        {
            try
            {
                Vector3 position = before.Position.ToNative();
                SetPositionImmediately(transform, position);
            }
            catch (Exception ex)
            {
                failures.Add($"{label}-position-{ex.GetType().Name}");
            }
        }

        if (restoreScale)
        {
            try
            {
                Vector3 scale = before.Scale.ToNative();
                SetScaleImmediately(transform, scale);
            }
            catch (Exception ex)
            {
                failures.Add($"{label}-scale-{ex.GetType().Name}");
            }
        }
    }

    private static ApiResult<LiveSceneLayers> ResolveLayers()
    {
        Test? player = Test.Instance;
        if (ReferenceEquals(player, null))
        {
            return ApiResult<LiveSceneLayers>.Fail("Story player is not active.");
        }

        UITexture? background = player.background;
        Transform? current = ReferenceEquals(background, null) ? null : background!.transform;
        Transform? back = null;
        for (int depth = 0;
             depth < MaximumAncestorDepth && !ReferenceEquals(current, null);
             depth++)
        {
            if (string.Equals(current!.name, "Back", StringComparison.Ordinal))
            {
                back = current;
                break;
            }

            current = current.parent;
        }

        if (ReferenceEquals(back, null))
        {
            return ApiResult<LiveSceneLayers>.Fail(
                "The Back ancestor could not be resolved from Test.background.");
        }

        Transform? uiRoot = back!.parent;
        if (ReferenceEquals(uiRoot, null)
            || !string.Equals(uiRoot!.name, "UI Root", StringComparison.Ordinal))
        {
            return ApiResult<LiveSceneLayers>.Fail(
                "The resolved Back transform is not a direct child of UI Root.");
        }

        Transform? spine = null;
        int childCount = uiRoot.childCount;
        for (int index = 0; index < childCount; index++)
        {
            Transform? child = uiRoot.GetChild(index);
            if (!ReferenceEquals(child, null)
                && string.Equals(child!.name, "Spine", StringComparison.Ordinal))
            {
                spine = child;
                break;
            }
        }

        return ReferenceEquals(spine, null)
            ? ApiResult<LiveSceneLayers>.Fail(
                "The Spine direct child could not be resolved from UI Root.")
            : ApiResult<LiveSceneLayers>.Ok(new LiveSceneLayers(back, spine!));
    }

    private static LayerBaseline ReadLayer(Transform transform)
    {
        Vector3 position = transform.localPosition;
        Vector3 scale = transform.localScale;
        return new LayerBaseline(
            new ManagedVector3(position.x, position.y, position.z),
            new ManagedVector3(scale.x, scale.y, scale.z));
    }

    private static LayerTarget MapTarget(
        LayerBaseline baseline,
        SceneCameraLayerComposition camera)
    {
        float zoom = camera.Zoom;
        return new LayerTarget(
            new ManagedVector3(
                baseline.Position.X - camera.OffsetX,
                baseline.Position.Y - camera.OffsetY,
                baseline.Position.Z),
            new ManagedVector3(
                baseline.Scale.X * zoom,
                baseline.Scale.Y * zoom,
                baseline.Scale.Z));
    }

    private bool IsMainThread() => Environment.CurrentManagedThreadId == _mainThreadId;

    private sealed record LiveSceneLayers(Transform Back, Transform Spine);

    private readonly record struct ManagedVector3(float X, float Y, float Z)
    {
        public bool IsFinite => float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Z);

        public Vector3 ToNative() => new(X, Y, Z);
    }

    private readonly record struct LayerBaseline(ManagedVector3 Position, ManagedVector3 Scale)
    {
        public bool IsFinite => Position.IsFinite && Scale.IsFinite;
    }

    private readonly record struct LayerTarget(ManagedVector3 Position, ManagedVector3 Scale)
    {
        public bool IsFinite => Position.IsFinite && Scale.IsFinite;
    }
}
