extern alias unitycore;

using System;
using AzureArchive.VideoTools.Api;
using AzureArchive.VideoTools.Core.Playback;
using Input = UnityEngine.Input;
using KeyCode = unitycore::UnityEngine.KeyCode;
using MonoBehaviour = unitycore::UnityEngine.MonoBehaviour;

namespace AzureArchive.VideoTools.Runtime;

public sealed class ExplicitTriggerBehaviour : MonoBehaviour
{
    private static bool _reportedFirstUpdate;
    private static bool _persistentDataPathCaptured;
    private static int _triggerCount;
    private static int _transformTriggerCount;
    private static int _selectedPublicSlot = 3;
    private static CharacterTransformProofOptions? _transformOptions;

    public ExplicitTriggerBehaviour(IntPtr pointer)
        : base(pointer)
    {
    }

    public void OnDestroy() => MoreEffectsToolPage.Stop();
    public void OnApplicationQuit() => MoreEffectsToolPage.Stop();

    internal static void Initialize(CharacterTransformProofOptions options)
    {
        _transformOptions = options;
        _selectedPublicSlot = options.InitialPublicSlot;
    }

    public void Update()
    {
        if (!_reportedFirstUpdate)
        {
            _reportedFirstUpdate = true;
            Plugin.Logger.LogInfo(
                "Managed lifecycle probe reached its first Update; no AzureArchive object was accessed.");
        }

        if (!_persistentDataPathCaptured)
        {
            try
            {
                string persistentDataPath = unitycore.UnityEngine.Application.persistentDataPath;
                if (!string.IsNullOrWhiteSpace(persistentDataPath))
                {
                    _persistentDataPathCaptured = true;
                    Interop.ActiveProjectPairSource.SetPersistentDataPath(persistentDataPath);
                }
            }
            catch (Exception ex)
            {
                _persistentDataPathCaptured = true;
                Plugin.Logger.LogWarning(
                    $"Auto project discovery could not read the game persistent data path: "
                    + $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        if (Input.GetKeyDown(KeyCode.F6)
            && (Input.GetKey(KeyCode.LeftControl)
                || Input.GetKey(KeyCode.RightControl)))
        {
            if (PlayerCommandObservationRuntime.RequestIndexReload())
            {
                Plugin.Logger.LogInfo(
                    "Ctrl+F6 requested a strict Mod command index reload.");
            }
            else
            {
                Plugin.Logger.LogWarning(
                    "Ctrl+F6 command index reload is unavailable at the configured player stage.");
            }
        }
        else if (Input.GetKeyDown(KeyCode.F6))
        {
            HandleInspectorProbe();
        }

        CharacterTransformProofOptions? options = _transformOptions;
        if (options == null || !options.Enabled)
        {
            return;
        }

        if (!HasManualControlInput(options))
        {
            return;
        }

        ApiResult<PlayerRuntimeContextSnapshot> contextResult =
            Plugin.Api.PlayerContext.ReadOnMainThread();
        if (!contextResult.Success || contextResult.Value == null)
        {
            Plugin.Logger.LogWarning(
                $"Manual character control suppressed because player context is unavailable: {contextResult.Error}");
            return;
        }

        PlayerRuntimeContextSnapshot context = contextResult.Value;
        if (!PlayerManualControlPolicy.AllowsManualControls(context))
        {
            Plugin.Logger.LogInfo(
                $"Manual character control suppressed: mode={context.Mode}; "
                + $"playerAvailable={context.PlayerAvailable}; previewMode={context.PreviewMode}.");
            return;
        }

        int selectedSlot = ReadSlotSelection(options.RequireAltForSlotSelection);
        if (selectedSlot != 0 && selectedSlot != _selectedPublicSlot)
        {
            _selectedPublicSlot = selectedSlot;
            Plugin.Logger.LogInfo(
                $"Manual character transform slot selected: #{_selectedPublicSlot}.");
        }

        if (Input.GetKeyDown(options.ApplyKey))
        {
            ExecuteTransformProof(options, options.ApplyDirective, "move-right");
        }

        if (Input.GetKeyDown(options.LeftKey))
        {
            ExecuteTransformProof(options, options.LeftDirective, "move-left");
        }

        if (Input.GetKeyDown(options.JumpKey))
        {
            ExecuteCharacterAction(CharacterActionKind.Jump, "jump");
        }

        if (Input.GetKeyDown(options.CrouchKey))
        {
            ExecuteCharacterAction(CharacterActionKind.Greeting, "crouch");
        }

        if (Input.GetKeyDown(options.ResetKey))
        {
            ExecuteTransformProof(options, options.ResetDirective, "reset");
        }
    }

    private static bool HasManualControlInput(CharacterTransformProofOptions options)
    {
        if (Input.GetKeyDown(options.ApplyKey)
            || Input.GetKeyDown(options.LeftKey)
            || Input.GetKeyDown(options.JumpKey)
            || Input.GetKeyDown(options.CrouchKey)
            || Input.GetKeyDown(options.ResetKey))
        {
            return true;
        }

        if (options.RequireAltForSlotSelection
            && !Input.GetKey(KeyCode.LeftAlt)
            && !Input.GetKey(KeyCode.RightAlt))
        {
            return false;
        }

        return Input.GetKeyDown(KeyCode.Alpha1)
            || Input.GetKeyDown(KeyCode.Keypad1)
            || Input.GetKeyDown(KeyCode.Alpha2)
            || Input.GetKeyDown(KeyCode.Keypad2)
            || Input.GetKeyDown(KeyCode.Alpha3)
            || Input.GetKeyDown(KeyCode.Keypad3)
            || Input.GetKeyDown(KeyCode.Alpha4)
            || Input.GetKeyDown(KeyCode.Keypad4)
            || Input.GetKeyDown(KeyCode.Alpha5)
            || Input.GetKeyDown(KeyCode.Keypad5);
    }

    private static void HandleInspectorProbe()
    {
        _triggerCount++;
        ApiResult<InspectorReferenceProbe> result = Plugin.Api.Editor.ProbeInspectorReference();
        if (!result.Success || result.Value == null)
        {
            Plugin.Logger.LogError(
                $"Inspector reference probe F6 count={_triggerCount} failed: {result.Error}");
            return;
        }

        Plugin.Logger.LogInfo(
            $"Inspector reference probe F6 count={_triggerCount}: "
            + $"inspector={result.Value.InspectorReferenceReturned}; "
            + $"scriptNode={result.Value.ScriptNodeReferenceReturned}; "
            + $"source={result.Value.Source}; no node members were read.");
    }

    private static void ExecuteTransformProof(
        CharacterTransformProofOptions options,
        string directive,
        string action)
    {
        int count = ++_transformTriggerCount;
        var request = new CharacterTransformDispatchRequest(
            options.SceneIdentity,
            directive,
            CharacterCommandSource.ManualControl,
            _selectedPublicSlot);
        ApiResult<CharacterTransformDispatchSnapshot> result =
            Plugin.Api.CharacterCommands.DispatchOnMainThread(request);
        if (!result.Success || result.Value == null)
        {
            Plugin.Logger.LogError(
                $"Character transform {action} proof count={count} failed: {result.Error}");
            return;
        }

        CharacterTransformDispatchSnapshot dispatch = result.Value;
        CharacterTransformExecutionSnapshot value = dispatch.Execution;
        Plugin.Logger.LogInfo(
            $"Character transform {action} proof count={count} succeeded: "
            + $"dispatchSequence={dispatch.Sequence}; source={dispatch.Source}; "
            + $"slot={value.Command.PublicSlot}; occupant={Escape(value.OccupantIdentifier)}; "
            + $"operation={value.Command.Operation}; durationMs={value.Command.DurationMilliseconds}; "
            + $"easing={value.Command.Easing}; positionChanged={value.PositionChanged}; "
            + $"rotationChanged={value.RotationChanged}; baselineCaptured={value.BaselineCaptured}; "
            + $"isReset={value.IsReset}; before={Format(value.Before)}; target={Format(value.Target)}.");
    }

    private static void ExecuteCharacterAction(CharacterActionKind action, string label)
    {
        var request = new CharacterActionRequest(
            _selectedPublicSlot,
            action,
            CharacterCommandSource.ManualControl);
        ApiResult<CharacterActionExecutionSnapshot> result =
            Plugin.Api.CharacterActions.EnqueueOnMainThread(request);
        if (!result.Success || result.Value == null)
        {
            Plugin.Logger.LogError(
                $"Character action {label} failed: {result.Error}");
            return;
        }

        CharacterActionExecutionSnapshot value = result.Value;
        Plugin.Logger.LogInfo(
            $"Character action {label} enqueued: sequence={value.Sequence}; "
            + $"source={value.Source}; slot={value.PublicSlot}; "
            + $"occupant={Escape(value.OccupantIdentifier)}; action={value.Action}.");
    }

    private static int ReadSlotSelection(bool requireAlt)
    {
        if (requireAlt
            && !Input.GetKey(KeyCode.LeftAlt)
            && !Input.GetKey(KeyCode.RightAlt))
        {
            return 0;
        }

        if (Input.GetKeyDown(KeyCode.Alpha1) || Input.GetKeyDown(KeyCode.Keypad1)) return 1;
        if (Input.GetKeyDown(KeyCode.Alpha2) || Input.GetKeyDown(KeyCode.Keypad2)) return 2;
        if (Input.GetKeyDown(KeyCode.Alpha3) || Input.GetKeyDown(KeyCode.Keypad3)) return 3;
        if (Input.GetKeyDown(KeyCode.Alpha4) || Input.GetKeyDown(KeyCode.Keypad4)) return 4;
        if (Input.GetKeyDown(KeyCode.Alpha5) || Input.GetKeyDown(KeyCode.Keypad5)) return 5;
        return 0;
    }

    private static string Format(AzureArchive.VideoTools.Core.Characters.CharacterTransformState state) =>
        $"pos({state.Position.X:R},{state.Position.Y:R},{state.Position.Z:R})/"
        + $"euler({state.LocalEulerAngles.X:R},{state.LocalEulerAngles.Y:R},{state.LocalEulerAngles.Z:R})";

    private static string Escape(string value) =>
        value.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace(";", ",", StringComparison.Ordinal);
}
