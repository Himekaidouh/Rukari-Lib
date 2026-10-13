using System;
using AzureArchive.VideoTools.Api;
using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Interop;

namespace AzureArchive.VideoTools.Runtime;
/// <summary>
/// The row's queue switch: one marker line that says how the whole row plays (2026-10-13).
///
/// <para>
/// 串成一条 lays the row out as a single queue — 人物-人物-镜头, each effect starting where the one before it ended —
/// and 各层并行 leaves every layer on its own track, where they all start together. The switch writes or erases the
/// row's <c>#aavt;queue</c> marker, which is a document edit like 连续对话 rather than a pending layer edit: it
/// describes the row, not one layer of it, so it takes effect on the next playback without anybody applying anything.
/// </para>
///
/// <para>
/// The layout itself is worked out by the embedded compile, which rebuilds the row's projection from the row's own
/// text every time. So the document holds one line and no offsets: editing any layer re-flows the queue by itself, and
/// no window can leave another window's timing stale. The marker can also simply be typed.
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
        QueueModeLabel(EffectQueueMode.IndependentTracks),
        ToggleQueueMode,
        () => _documentSnapshot != null,
        () => _documentSnapshot?.QueueMode == EffectQueueMode.OneQueue);

    /// <summary>What the switch reads: the row's own state, not a pending intention.</summary>
    private static string QueueModeLabel(EffectQueueMode mode) => mode == EffectQueueMode.OneQueue
        ? "队列：串成一条"
        : "队列：各层并行";

    /// <summary>
    /// Writes or erases the marker, and says what the row became — including the queue's length, which is the number
    /// an author cannot work out from one window's layer.
    /// </summary>
    private void ToggleQueueMode()
    {
        EditorCommandDocumentSnapshot? source = _documentSnapshot;
        if (source is null) return;
        if (Plugin.Api.EditorCommandDocuments is not EditorCommandDocumentService service) return;
        EffectQueueMode target = source.QueueMode == EffectQueueMode.OneQueue
            ? EffectQueueMode.IndependentTracks
            : EffectQueueMode.OneQueue;
        ApiResult<EditorCommandQueueApplySnapshot> result =
            service.ApplyQueueModeOnMainThread(target, source.RuntimeSelectionKey);
        if (!result.Success || result.Value == null)
        {
            _operationMessage = "队列切换失败\n" + result.Error;
            ShowQueueMessage(source, _operationMessage);
            EditorNotifications.TryToast(
                "队列切换失败：" + EditorNotifications.FirstLine(result.Error ?? string.Empty),
                "queue-mode-failed");
            RefreshCommandDocument();
            UpdateButtons();
            return;
        }

        _documentSnapshot = result.Value.After;
        EditorCommandDocumentSnapshot after = result.Value.After;
        _operationMessage = result.Value.AlreadyPresent
            ? "队列已是该状态，无需切换"
            : DescribeQueue(after.QueueMode, after.AdditionalPrompt);
        ShowQueueMessage(after, _operationMessage);
        Plugin.Logger.LogInfo(
            $"[layer-ui] queue marker · mode={after.QueueMode} · alreadyPresent={result.Value.AlreadyPresent}");
        UpdateInspector();
        UpdateButtons();
    }

    /// <summary>The same two-line status the continue toggle writes, so one switch does not look different from the other.</summary>
    private void ShowQueueMessage(EditorCommandDocumentSnapshot source, string message)
    {
        if (_draftText is not null) _draftText.text = $"{SourceSummary(source)}\n{message}";
    }

    /// <summary>
    /// What the mode now means for this row, in the row's own numbers: the queue's length, or the fact that nothing
    /// in the row is laid out as a queue.
    /// </summary>
    private static string DescribeQueue(EffectQueueMode mode, string additionalPrompt)
    {
        if (mode != EffectQueueMode.OneQueue)
        {
            return "已改成各层并行：每一层各自成轨，同时开始。";
        }

        EmbeddedAavtExtraction extraction = new EmbeddedAavtDirectiveExtractor().Extract(additionalPrompt);
        if (extraction.Errors.Count != 0 || extraction.Commands.Count == 0)
        {
            return "已改成串成一条：本行还没有效果，加段后它们会一段接一段地播。";
        }

        EffectQueuePlan plan = EffectQueueChain.Plan(extraction.Commands);
        return plan.Success
            ? "已改成串成一条。" + plan.Line
            : "已改成串成一条，但这一行排不出来：" + plan.Error;
    }

    /// <summary>The switch never lies about the row: it is drawn from the document, not from a remembered flag.</summary>
    private void UpdateQueueModeButton()
    {
        if (_controlButtons.TryGetValue(QueueModeKey, out ControlButton? button))
        {
            button.Label.text = QueueModeLabel(
                _documentSnapshot?.QueueMode ?? EffectQueueMode.IndependentTracks);
        }
    }
}
