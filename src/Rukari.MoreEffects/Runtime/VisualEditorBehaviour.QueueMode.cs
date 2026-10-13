using AzureArchive.VideoTools.Core.Commands;

namespace AzureArchive.VideoTools.Runtime;

/// <summary>
/// The row's queue mode: one switch for the two ways a row of layered effects can play (2026-10-13).
///
/// <para>
/// 串成一条 reads the whole row as a single queue — 人物-人物-镜头, each effect starting where the one before it ended —
/// and writes that as <c>at=</c> offsets at the joints. 各层并行 leaves every layer on its own track, where they all
/// start together, which is what the row did before this switch existed. The mode itself is not written into the
/// document: what it produces is, and that is the part playback reads.
/// </para>
///
/// <para>
/// It sits in the panel rather than in either window because it is a property of the row, and it is shown in both
/// modes that write a layer. The writing itself happens in one place, the layer write path, so both modes and the
/// preset window cannot disagree about what the author chose here.
/// </para>
/// </summary>
public sealed partial class VisualEditorBehaviour
{
    private const string QueueModeKey = "queue-mode";

    /// <summary>
    /// Whether the mode on screen owns a layer, and therefore writes one with 应用. The ＋ 加为下一段 button and this
    /// switch are the two things that follow from it, and both ask here rather than testing the modes themselves.
    /// </summary>
    private bool IsLayerMode => _mode is VisualEditorMode.Camera or VisualEditorMode.Character;

    private void CreateQueueModeControl() => CreateControlButton(
        QueueModeKey,
        EffectQueueMode.Label,
        ToggleQueueMode,
        () => true,
        () => EffectQueueMode.Serial);

    /// <summary>Switches what the next 应用 writes, and says which of the two it is now.</summary>
    private void ToggleQueueMode()
    {
        bool serial = EffectQueueMode.Toggle();
        string message = serial
            ? "队列：整行串成一条。" + EffectQueueMode.Describe
            : "队列：各层并行。" + EffectQueueMode.Describe;
        Plugin.Logger.LogInfo($"[layer-ui] queue mode · serial={serial}");
        if (_mode == VisualEditorMode.Character) SetCharacterLayerStatus(message);
        else if (_mode == VisualEditorMode.Camera) SetCameraLayerStatus(message);
        else
        {
            _operationMessage = message;
            UpdateInspector();
            UpdateButtons();
        }
    }

    /// <summary>The switch reads the mode, not the action: it says what the next 应用 will do.</summary>
    private void UpdateQueueModeButton()
    {
        if (_controlButtons.TryGetValue(QueueModeKey, out ControlButton? button))
        {
            button.Label.text = EffectQueueMode.Label;
        }
    }
}
