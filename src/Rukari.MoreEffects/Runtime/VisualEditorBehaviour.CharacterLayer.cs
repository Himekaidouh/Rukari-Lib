extern alias unitycore;
extern alias unitytext;
extern alias unityui;

using System;
using AzureArchive.VideoTools.Core.Commands;
using Text = unityui::UnityEngine.UI.Text;

namespace AzureArchive.VideoTools.Runtime;

/// <summary>
/// The character layer editor, inside the picture editor's own character mode (2026-10-13).
///
/// <para>
/// It is the same shape as the camera mode's editor — a segment selector, move/delete rows, and a big
/// ＋ 加为下一段 beside 应用 and 撤销 — because it is the same idea: the mode that composes an effect is the surface
/// that owns its layer. What differs is which layer that is: one character slot, chosen by the slot buttons, so the
/// editor is rebuilt when the author targets another character.
/// </para>
///
/// <para>
/// The policy behind all of that is Core's <see cref="EffectLayerEditor"/>, shared with the camera mode and tested
/// without a game. This file is the widgets: which ids exist, where they sit, and what the panel says.
/// </para>
/// </summary>
public sealed partial class VisualEditorBehaviour
{
    private static readonly string[] CharacterLayerControlKeys =
    {
        "character-seg", "character-move-up", "character-move-down", "character-delete-seg"
    };

    private EffectLayerEditor? _characterEditor;
    private int _characterEditorSlot = -1;

    /// <summary>The layer this mode owns, or null while no character slot is targeted.</summary>
    private EffectLayer? CurrentCharacterLayer => HasSelectedSlot()
        ? EffectLayerCatalog.Character(_selectedSlotIndex + 1)
        : null;

    /// <summary>
    /// The editor of the targeted character's layer. It is rebuilt when the author changes slot: a layer is identified
    /// by its slot, so keeping the old one would stack effects onto the wrong character.
    /// </summary>
    private EffectLayerEditor? CharacterEditor
    {
        get
        {
            EffectLayer? layer = CurrentCharacterLayer;
            if (layer is null) return null;
            int slot = _selectedSlotIndex + 1;
            if (_characterEditor is null || _characterEditorSlot != slot)
            {
                _characterEditorSlot = slot;
                _characterEditor = new EffectLayerEditor(
                    new[] { layer },
                    ReadLayersOnMainThread,
                    WriteLayersOnMainThread,
                    () => _currentDraftDirective,
                    "角色段");
            }

            return _characterEditor;
        }
    }

    private void CreateCharacterLayerControls()
    {
        CreateControlButton(
            "character-seg",
            "角色段 0/0",
            CycleCharacterSegment,
            () => CharacterEditor?.HasSegments == true);
        CreateControlButton(
            "character-move-up",
            "上移",
            () => MoveCharacterSegment(-1),
            () => CharacterEditor?.CanMove(-1) == true);
        CreateControlButton(
            "character-move-down",
            "下移",
            () => MoveCharacterSegment(1),
            () => CharacterEditor?.CanMove(1) == true);
        CreateControlButton(
            "character-delete-seg",
            "删除这段",
            DeleteCharacterSegment,
            () => CharacterEditor?.Stack.SelectedIndex >= 0);
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

    /// <summary>Re-reads the targeted character's layer, unless the author has stacked something unwritten.</summary>
    private void EnsureCharacterLayerStack(bool force = false) => CharacterEditor?.Refresh(force);

    /// <summary>
    /// What the inspector says about the layer: its own count and length, and which effect the buttons would act on.
    /// </summary>
    private string CharacterLayerStatusLine()
    {
        EffectLayerEditor? editor = CharacterEditor;
        if (editor is null) return "先选中一个角色槽位，这一层才有内容。";
        string text = editor.LineOf(editor.PrimaryLayer);
        if (editor.Stack.SelectedIndex < 0) return text + "　调好动作后按「＋ 加为下一段」";
        EffectLayerSegment? selected = editor.Stack.Selected;
        return selected is null
            ? text
            : text + $"　选中 {editor.Stack.SelectedIndex + 1}. {selected.Label}";
    }

    private void CycleCharacterSegment()
    {
        EffectLayerEditor? editor = CharacterEditor;
        if (editor is null) return;
        editor.Cycle();
        SetCharacterLayerStatus(editor.Status);
    }

    private void AddCharacterSegment()
    {
        EffectLayerEditor? editor = CharacterEditor;
        if (editor is null)
        {
            SetCharacterLayerStatus("先选中一个角色槽位。");
            return;
        }

        editor.Add();
        SetCharacterLayerStatus(editor.Status);
    }

    private void MoveCharacterSegment(int delta)
    {
        EffectLayerEditor? editor = CharacterEditor;
        if (editor is null) return;
        editor.Move(delta);
        SetCharacterLayerStatus(editor.Status);
    }

    private void DeleteCharacterSegment()
    {
        EffectLayerEditor? editor = CharacterEditor;
        if (editor is null) return;
        editor.Delete();
        SetCharacterLayerStatus(editor.Status);
    }

    /// <summary>
    /// Writes the whole character layer in one transaction: what this mode owns, in the order the author stacked it.
    /// The existing 应用 button calls this in character mode.
    /// </summary>
    private void ApplyCharacterLayer()
    {
        EffectLayerEditor? editor = CharacterEditor;
        if (editor is null)
        {
            SetCharacterLayerStatus("先选中一个角色槽位。");
            return;
        }

        EffectLayerApply? applied = editor.Apply();
        if (applied is { Success: true })
        {
            // The panel's own snapshot is the same document, so it follows the write before anything else reads it.
            RefreshCommandDocument();
            Plugin.Logger.LogInfo("[character-layer] applied · " + editor.Describe());
        }

        SetCharacterLayerStatus(editor.Status);
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

    /// <summary>Keeps the two value buttons' labels in step with the editor; enabled state is a predicate.</summary>
    private void UpdateCharacterLayerButtons()
    {
        EffectLayerEditor? editor = CharacterEditor;
        if (_controlButtons.TryGetValue("character-seg", out ControlButton? segment))
            segment.Label.text = editor?.SegmentLabel ?? "角色段 0/0";
        if (_controlButtons.TryGetValue("character-delete-seg", out ControlButton? delete))
            delete.Label.text = editor?.DeleteLabel ?? "删除这段";
    }
}
