extern alias unitycore;
extern alias unitytext;
extern alias unityui;

using System;
using System.Diagnostics;
using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Interop;
using Color = unitycore::UnityEngine.Color;
using RectTransform = unitycore::UnityEngine.RectTransform;
using Text = unityui::UnityEngine.UI.Text;

namespace AzureArchive.VideoTools.Runtime;

/// <summary>
/// The character layer editor, inside the picture editor's own character mode (2026-10-13).
///
/// <para>
/// It is the same shape as the camera mode's editor — a segment selector, move/delete rows, and a big
/// ＋ 加为下一段 beside 应用 and 撤销 — because it is the same idea: the mode that composes an effect is the surface
/// that owns its layer. What differs is which layer that is: one character slot, chosen by the slot buttons, so the
/// stack is rebuilt when the author targets another character.
/// </para>
///
/// <para>
/// A character layer holds transforms and pending start states (they share the track, per the queue planner), so a
/// row here can stack "move, then turn" the way the camera stacks its moves. The layer machinery itself is Core's
/// (<see cref="EffectLayerStack"/>, tested without a game); this file only connects it to the panel's controls.
/// </para>
/// </summary>
public sealed partial class VisualEditorBehaviour
{
    private static readonly string[] CharacterLayerControlKeys =
    {
        "character-seg", "character-move-up", "character-move-down", "character-delete-seg"
    };

    private EffectLayerStack? _characterStack;
    private int _characterStackSlot = -1;
    private long _characterLayerReadAt;

    /// <summary>The layer this mode owns, or null while no character slot is targeted.</summary>
    private EffectLayer? CurrentCharacterLayer => HasSelectedSlot()
        ? EffectLayerCatalog.Character(_selectedSlotIndex + 1)
        : null;

    /// <summary>
    /// The stack of the targeted character's layer. It is rebuilt when the author changes slot: a layer is identified
    /// by its slot, so keeping the old one would stack effects onto the wrong character.
    /// </summary>
    private EffectLayerStack? CharacterLayers
    {
        get
        {
            EffectLayer? layer = CurrentCharacterLayer;
            if (layer is null) return null;
            int slot = _selectedSlotIndex + 1;
            if (_characterStack is null || _characterStackSlot != slot)
            {
                _characterStackSlot = slot;
                _characterStack = new EffectLayerStack(new[] { layer });
            }

            return _characterStack;
        }
    }

    private void CreateCharacterLayerControls()
    {
        CreateControlButton(
            "character-seg",
            "角色段 0/0",
            CycleCharacterSegment,
            () => CharacterLayers?.Rows.Count > 0);
        CreateControlButton(
            "character-move-up",
            "上移",
            () => MoveCharacterSegment(-1),
            () => CanMoveCharacterSegment(-1));
        CreateControlButton(
            "character-move-down",
            "下移",
            () => MoveCharacterSegment(1),
            () => CanMoveCharacterSegment(1));
        CreateControlButton(
            "character-delete-seg",
            "删除这段",
            DeleteCharacterSegment,
            () => CharacterLayers?.SelectedIndex >= 0);
    }

    /// <summary>
    /// Two rows under the character mode's own 复位/清空 row, in the same shapes the rest of this panel uses, and the
    /// row 连续对话 should move to so that nothing overlaps.
    /// </summary>
    private float LayoutCharacterLayerControls(float resetRowY, float rowGap)
    {
        float selectRowY = resetRowY - CameraLayerRowPitch;
        float optionWidth = (PanelWidth - 60f - (rowGap * 2f)) / 3f;
        LayoutControl("character-seg", 30f, selectRowY, optionWidth, CameraLayerRowHeight);
        LayoutControl("character-move-up", 30f + optionWidth + rowGap, selectRowY, optionWidth, CameraLayerRowHeight);
        LayoutControl(
            "character-move-down",
            30f + ((optionWidth + rowGap) * 2f),
            selectRowY,
            optionWidth,
            CameraLayerRowHeight);
        LayoutControl(
            "character-delete-seg",
            30f,
            selectRowY - CameraLayerRowPitch,
            PanelWidth - 60f,
            CameraLayerRowHeight);
        return selectRowY - (CameraLayerRowPitch * 2f);
    }

    /// <summary>
    /// Re-reads the targeted character's layer at a walk, and never on top of work the author has not applied.
    /// </summary>
    private void EnsureCharacterLayerStack(bool force = false)
    {
        if (!_expanded) return;
        EffectLayerStack? stack = CharacterLayers;
        EffectLayer? layer = CurrentCharacterLayer;
        if (stack is null || layer is null) return;
        long now = Stopwatch.GetTimestamp();
        if (!force)
        {
            if (stack.HasPendingChanges) return;
            if (now - _characterLayerReadAt < Stopwatch.Frequency / 4) return;
        }

        _characterLayerReadAt = now;
        if (Plugin.Api.EditorCommandDocuments is not EditorCommandDocumentService service) return;
        stack.Adopt(service.ReadLayersOnMainThread(new[] { layer }));
    }

    /// <summary>
    /// What the inspector says about the layer: its own count and length, and which effect the buttons would act on.
    /// </summary>
    private string CharacterLayerStatusLine()
    {
        EffectLayerStack? stack = CharacterLayers;
        EffectLayer? layer = CurrentCharacterLayer;
        if (stack is null || layer is null) return "先选中一个角色槽位，这一层才有内容。";
        string text = stack.LineOf(layer);
        if (stack.SelectedIndex < 0) return text + "　调好动作后按「＋ 加为下一段」";
        EffectLayerSegment? selected = stack.Selected;        return selected is null
            ? text
            : text + $"　选中 {stack.SelectedIndex + 1}. {selected.Label}";
    }

    private bool CanMoveCharacterSegment(int delta)
    {
        EffectLayerStack? stack = CharacterLayers;
        return stack is not null
            && stack.SelectedIndex >= 0
            && stack.SelectedIndex + delta >= 0
            && stack.SelectedIndex + delta < stack.Rows.Count;
    }

    private void CycleCharacterSegment()
    {
        EffectLayerStack? stack = CharacterLayers;
        if (stack is null) return;
        int total = stack.Rows.Count;
        if (total == 0)
        {
            SetCharacterLayerStatus("这个角色槽位还没有效果。");
            return;
        }

        int next = stack.SelectedIndex < 0 ? 0 : (stack.SelectedIndex + 1) % total;
        stack.Select(stack.Layers[0], next);
        SetCharacterLayerStatus(stack.HasPendingChanges
            ? $"第 {next + 1} 段：{stack.Selected?.Label}"
            : stack.Status);
    }

    private void AddCharacterSegment()
    {
        EffectLayerStack? stack = CharacterLayers;
        if (stack is null)
        {
            SetCharacterLayerStatus("先选中一个角色槽位。");
            return;
        }

        if (!stack.Add(EffectDraft.FromDirective(_currentDraftDirective)))
        {
            SetCharacterLayerStatus(stack.Status);
            return;
        }

        SetCharacterLayerStatus(stack.Status);
    }

    private void MoveCharacterSegment(int delta)
    {
        EffectLayerStack? stack = CharacterLayers;
        if (stack is null) return;
        stack.MoveSelected(delta);
        SetCharacterLayerStatus(stack.Status);
    }

    private void DeleteCharacterSegment()
    {
        EffectLayerStack? stack = CharacterLayers;
        if (stack is null) return;
        stack.DeleteSelected();
        SetCharacterLayerStatus(stack.Status);
    }

    /// <summary>
    /// Writes the whole character layer in one transaction: what this mode owns, in the order the author stacked it.
    /// The existing 应用 button calls this in character mode.
    /// </summary>
    private void ApplyCharacterLayer()
    {
        EffectLayerStack? stack = CharacterLayers;
        if (stack is null)
        {
            SetCharacterLayerStatus("先选中一个角色槽位。");
            return;
        }

        if (!stack.HasPendingChanges)
        {
            SetCharacterLayerStatus("这一层没有需要写入的改动。");
            return;
        }

        if (!stack.CanWrite)
        {
            SetCharacterLayerStatus("这一句还没有读取成功，请重新选中台词后再试。");
            return;
        }

        if (Plugin.Api.EditorCommandDocuments is not EditorCommandDocumentService service)
        {
            SetCharacterLayerStatus("当前编辑服务暂不支持整层写入。");
            return;
        }

        EffectLayerApply result = service.ApplyLayersOnMainThread(
            stack.RevisionSha256,
            stack.SelectionToken,
            stack.BuildEdits());
        if (!result.Success)
        {
            SetCharacterLayerStatus("写入失败：" + result.Error);
            return;
        }

        stack.Adopt(result.Read, "已写入当前句；" + stack.Consequence());
        RefreshCommandDocument();
        SetCharacterLayerStatus(stack.Status);
        Plugin.Logger.LogInfo("[character-layer] applied · " + stack.Describe());
    }

    private void SetCharacterLayerStatus(string message)
    {
        _operationMessage = message;
        Text? draft = _draftText;
        if (draft is not null)
            draft.text = $"{CurrentCharacterLayer?.Label ?? "角色"}层\n{message}";
        UpdateInspector();
        UpdateButtons();
    }

    /// <summary>Keeps the two value buttons' labels in step with the stack; enabled state is a predicate.</summary>
    private void UpdateCharacterLayerButtons()
    {
        EffectLayerStack? stack = CharacterLayers;
        if (_controlButtons.TryGetValue("character-seg", out ControlButton? segment))
        {
            int total = stack?.Rows.Count ?? 0;
            int selected = stack?.SelectedIndex ?? -1;
            segment.Label.text = $"角色段 {Math.Max(0, selected + 1)}/{total}";
        }

        if (_controlButtons.TryGetValue("character-delete-seg", out ControlButton? delete))
        {
            int selected = stack?.SelectedIndex ?? -1;
            delete.Label.text = selected >= 0 ? $"删除第 {selected + 1} 段" : "删除这段";
        }
    }
}
