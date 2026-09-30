extern alias unitycore;

using AzureArchive.VideoTools.Api;
using AzureArchive.VideoTools.Core;
using AzureArchive.VideoTools.Core.Cameras;
using AzureArchive.VideoTools.Core.Characters;
using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Results;
using Transform = unitycore::UnityEngine.Transform;
using Vector3 = unitycore::UnityEngine.Vector3;

namespace AzureArchive.VideoTools.Interop;

internal sealed class SceneCameraService : ISceneCameraService, ISceneCameraCommandDispatcher
{
    private const int MaximumAncestorDepth = 16;

    private readonly int _mainThreadId = Environment.CurrentManagedThreadId;
    private readonly SceneCameraDirectiveParser _parser = new();
    private readonly SceneCameraPlanner _planner = new();
    private readonly Dictionary<string, SceneCameraBaseline> _sceneEntries =
        new(StringComparer.Ordinal);

    private int? _backInstanceId;
    private int? _spineInstanceId;
    private LayerBaseline? _backBaseline;
    private LayerBaseline? _spineBaseline;
    private SceneCameraState _currentState = SceneCameraState.Default;

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
                _currentState,
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
            bool baselineCaptured = !_sceneEntries.TryGetValue(
                sceneIdentity,
                out SceneCameraBaseline? existingEntry);
            SceneCameraBaseline sceneEntry;
            bool replayRestored = false;
            if (baselineCaptured)
            {
                sceneEntry = new SceneCameraBaseline(sceneIdentity, _currentState);
            }
            else
            {
                sceneEntry = existingEntry!;
                var restore = _planner.PlanReplayRestore(command, _currentState, sceneEntry);
                if (!restore.Success || restore.Value == null)
                {
                    return ApiResult<SceneCameraExecutionSnapshot>.Fail(restore.Error);
                }

                ApiResult<bool> restored = ApplyTarget(layers, restore.Value);
                if (!restored.Success)
                {
                    return ApiResult<SceneCameraExecutionSnapshot>.Fail(
                        $"Scene camera replay restore failed: {restored.Error}");
                }

                _currentState = restore.Value.State;
                replayRestored = true;
            }

            SceneCameraState before = _currentState;
            var planned = _planner.Plan(command, before, sceneEntry);
            if (!planned.Success || planned.Value == null)
            {
                return ApiResult<SceneCameraExecutionSnapshot>.Fail(planned.Error);
            }

            SceneCameraTarget target = planned.Value;
            ApiResult<bool> applied = ApplyTarget(layers, target);
            if (!applied.Success)
            {
                return ApiResult<SceneCameraExecutionSnapshot>.Fail(applied.Error);
            }

            _currentState = target.State;
            if (baselineCaptured)
            {
                _sceneEntries.Add(sceneIdentity, sceneEntry);
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
                    target.IsReset));
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
        if (!IsMainThread())
        {
            return ApiResult<SceneCameraInheritedStartSnapshot>.Fail(
                "Scene camera inherited starts may only be applied on the Unity main thread.");
        }

        try
        {
            if (string.IsNullOrWhiteSpace(sceneIdentity))
            {
                return ApiResult<SceneCameraInheritedStartSnapshot>.Fail(
                    "Scene identity is required for a scene camera inherited start.");
            }

            Result stateValidation = SceneCameraCommandValidator.ValidateTarget(inheritedState);
            if (!stateValidation.Success)
            {
                return ApiResult<SceneCameraInheritedStartSnapshot>.Fail(
                    $"Scene camera inherited start state is invalid: {stateValidation.Error}");
            }

            ApiResult<LiveSceneLayers> resolved = ResolveLayers();
            if (!resolved.Success || resolved.Value == null)
            {
                return ApiResult<SceneCameraInheritedStartSnapshot>.Fail(resolved.Error);
            }

            ApiResult<bool> baselineReady = EnsurePhysicalBaselines(resolved.Value);
            if (!baselineReady.Success)
            {
                return ApiResult<SceneCameraInheritedStartSnapshot>.Fail(baselineReady.Error);
            }

            SceneCameraState before = _currentState;
            var target = new SceneCameraTarget(
                inheritedState,
                PositionChanged: true,
                ZoomChanged: true,
                DurationMilliseconds: 0,
                CharacterTransformEasing.Linear,
                IsReset: false);
            ApiResult<bool> applied = ApplyTarget(resolved.Value, target);
            if (!applied.Success)
            {
                return ApiResult<SceneCameraInheritedStartSnapshot>.Fail(
                    $"Scene camera inherited start could not be applied: {applied.Error}");
            }

            bool baselineCaptured = !_sceneEntries.ContainsKey(sceneIdentity);
            _sceneEntries[sceneIdentity] = new SceneCameraBaseline(sceneIdentity, inheritedState);
            _currentState = inheritedState;
            return ApiResult<SceneCameraInheritedStartSnapshot>.Ok(new(
                sceneIdentity,
                before,
                inheritedState,
                baselineCaptured));
        }
        catch (Exception ex)
        {
            return ApiResult<SceneCameraInheritedStartSnapshot>.Fail(
                $"Scene camera inherited start failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

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
            if (sameBack)
            {
                RollBackLayer(
                    "Back-survivor",
                    layers.Back,
                    _backBaseline.Value,
                    restorePosition: true,
                    restoreScale: true,
                    failures);
            }
            else
            {
                RollBackLayer(
                    "Spine-survivor",
                    layers.Spine,
                    _spineBaseline.Value,
                    restorePosition: true,
                    restoreScale: true,
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
        _currentState = SceneCameraState.Default;
        _sceneEntries.Clear();
        return ApiResult<bool>.Ok(true);
    }

    private ApiResult<bool> ApplyTarget(
        LiveSceneLayers layers,
        SceneCameraTarget target)
    {
        if (!_backBaseline.HasValue || !_spineBaseline.HasValue)
        {
            return ApiResult<bool>.Fail("Scene camera physical baselines are unavailable.");
        }

        bool applyPosition = target.PositionChanged || target.ZoomChanged;
        bool applyScale = target.ZoomChanged;
        LayerTarget backTarget = MapTarget(_backBaseline.Value, target.State);
        LayerTarget spineTarget = MapTarget(_spineBaseline.Value, target.State);
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

        try
        {
            ApplyLayer(
                layers.Back,
                backTarget,
                applyPosition,
                applyScale,
                target.DurationMilliseconds,
                target.Easing);
            ApplyLayer(
                layers.Spine,
                spineTarget,
                applyPosition,
                applyScale,
                target.DurationMilliseconds,
                target.Easing);
            return ApiResult<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            string rollback = RollBackLayers(
                layers,
                backBefore,
                spineBefore,
                applyPosition,
                applyScale);
            string residual = string.Equals(rollback, "succeeded", StringComparison.Ordinal)
                ? string.Empty
                : "; " + RuntimeMutationFailure.ResidualMutationMarker;
            return ApiResult<bool>.Fail(
                $"Back/Spine camera application failed: {ex.GetType().Name}: {ex.Message}; "
                + $"rollback={rollback}{residual}");
        }
    }

    private static void ApplyLayer(
        Transform transform,
        LayerTarget target,
        bool applyPosition,
        bool applyScale,
        int durationMilliseconds,
        CharacterTransformEasing easing)
    {
        float durationSeconds = durationMilliseconds / 1000f;
        if (applyPosition)
        {
            var position = target.Position.ToNative();
            if (durationMilliseconds == 0)
            {
                SetPositionImmediately(transform, position);
            }
            else
            {
                TweenPosition? tween = TweenPosition.Begin(
                    transform.gameObject,
                    durationSeconds,
                    position,
                    false);
                if (ReferenceEquals(tween, null))
                {
                    throw new InvalidOperationException("TweenPosition.Begin returned null.");
                }

                tween.method = MapEasing(easing);
            }
        }

        if (applyScale)
        {
            var scale = target.Scale.ToNative();
            if (durationMilliseconds == 0)
            {
                SetScaleImmediately(transform, scale);
            }
            else
            {
                TweenScale? tween = TweenScale.Begin(
                    transform.gameObject,
                    durationSeconds,
                    scale);
                if (ReferenceEquals(tween, null))
                {
                    throw new InvalidOperationException("TweenScale.Begin returned null.");
                }

                tween.method = MapEasing(easing);
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

    private static string RollBackLayers(
        LiveSceneLayers layers,
        LayerBaseline backBefore,
        LayerBaseline spineBefore,
        bool restorePosition,
        bool restoreScale)
    {
        var failures = new List<string>();
        RollBackLayer(
            "Back",
            layers.Back,
            backBefore,
            restorePosition,
            restoreScale,
            failures);
        RollBackLayer(
            "Spine",
            layers.Spine,
            spineBefore,
            restorePosition,
            restoreScale,
            failures);
        return failures.Count == 0 ? "succeeded" : string.Join("|", failures);
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
        SceneCameraState camera)
    {
        float zoom = camera.Zoom;
        return new LayerTarget(
            new ManagedVector3(
                baseline.Position.X - (camera.X * zoom),
                baseline.Position.Y - (camera.Y * zoom),
                baseline.Position.Z),
            new ManagedVector3(
                baseline.Scale.X * zoom,
                baseline.Scale.Y * zoom,
                baseline.Scale.Z));
    }

    private static UITweener.Method MapEasing(CharacterTransformEasing easing) => easing switch
    {
        CharacterTransformEasing.Linear => UITweener.Method.Linear,
        CharacterTransformEasing.EaseIn => UITweener.Method.EaseIn,
        CharacterTransformEasing.EaseOut => UITweener.Method.EaseOut,
        CharacterTransformEasing.EaseInOut => UITweener.Method.EaseInOut,
        _ => UITweener.Method.Linear
    };

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
