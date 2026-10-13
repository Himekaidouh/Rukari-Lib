extern alias unitycore;
extern alias unitytext;
extern alias unityui;

using System;
using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Results;
using AzureArchive.VideoTools.Core.VisualEditor;
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
        "character-seg", "character-move-up", "character-move-down", "character-load-seg",
        "character-replace-seg", "character-delete-seg"
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
            "character-load-seg",
            "载入这段",
            LoadCharacterSegment,
            () => CharacterEditor?.Stack.SelectedIndex >= 0);
        CreateControlButton(
            "character-replace-seg",
            "替换这段",
            ReplaceCharacterSegment,
            () => CharacterEditor?.Stack.SelectedIndex >= 0
                && !string.IsNullOrEmpty(_currentDraftDirective));
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
    /// <param name="resetClearRowY">
    /// The row this mode puts 复位/清空 on — 整体重置·回归官方槽位 and 清除草稿. In character mode that is one row below
    /// 翻转复位, so the caller passes the row those two buttons are drawn on rather than the 翻转复位 row.
    /// </param>
    private float LayoutCharacterLayerControls(float resetClearRowY, float rowGap)
    {
        float selectRowY = resetClearRowY - CameraLayerRowPitch;
        float optionWidth = (PanelWidth - 60f - (rowGap * 2f)) / 3f;
        LayoutControl("character-seg", 30f, selectRowY, optionWidth, CameraLayerRowHeight);
        LayoutControl("character-move-up", 30f + optionWidth + rowGap, selectRowY, optionWidth, CameraLayerRowHeight);
        LayoutControl(
            "character-move-down",
            30f + ((optionWidth + rowGap) * 2f),
            selectRowY,
            optionWidth,
            CameraLayerRowHeight);
        float actionRowY = selectRowY - CameraLayerRowPitch;
        LayoutControl("character-load-seg", 30f, actionRowY, optionWidth, CameraLayerRowHeight);
        LayoutControl(
            "character-replace-seg",
            30f + optionWidth + rowGap,
            actionRowY,
            optionWidth,
            CameraLayerRowHeight);
        LayoutControl(
            "character-delete-seg",
            30f + ((optionWidth + rowGap) * 2f),
            actionRowY,
            optionWidth,
            CameraLayerRowHeight);
        return actionRowY - CameraLayerRowPitch;
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
        Plugin.Logger.LogInfo("[character-layer] add-as-next · " + editor.Status);
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
    /// Pulls the selected effect back into this mode's own widgets, so it can be retuned in place (2026-10-14). The
    /// draft the author was holding is replaced on purpose: pressing 载入 is the act of saying "I am editing that one".
    /// </summary>
    private void LoadCharacterSegment()
    {
        EffectLayerEditor? editor = CharacterEditor;
        if (editor is null)
        {
            SetCharacterLayerStatus("先选中一个角色槽位。");
            return;
        }

        EffectLayerSegment? segment = editor.Stack.Selected;
        if (segment is null)
        {
            SetCharacterLayerStatus("先在「角色段」里选一段，再载入。");
            return;
        }

        Result<VisualCharacterDraftRequest> read = _draftBuilder.ReadCanonical(segment.CanonicalDirective);
        if (!read.Success || read.Value is null)
        {
            SetCharacterLayerStatus("这一段读不出来：" + (read.Error ?? "未知原因"));
            return;
        }

        VisualCharacterDraftRequest request = read.Value;
        if (request.Operation == VisualCharacterDraftOperation.Move)
        {
            // A relative move is defined against wherever the character stands when it runs; the widgets edit an
            // absolute position, so there is nothing honest to load. The directive stays editable as text.
            SetCharacterLayerStatus("相对移动段（move）只能用指令编辑；载入支持定位与重置。");
            return;
        }

        int index = _selectedSlotIndex;
        if (!HasSelectedSlot() || request.PublicSlot != index + 1)
        {
            SetCharacterLayerStatus($"这一段属于角色 {request.PublicSlot}，先切到那个槽位再载入。");
            return;
        }

        if (IsOfficialTransitionSlot(request.PublicSlot))
        {
            RefuseDraftByOfficialTransition(index);
            return;
        }

        BeginDraftSource();
        _activeDraft = request;
        _editInitialPosition = !request.Occupied;
        float? x = request.Occupied ? request.X : request.StartX;
        float? y = request.Occupied ? request.Y : request.StartY;
        _draftPositions[index] = x.HasValue && y.HasValue
            ? new VisualEditorPoint(x.Value, y.Value)
            : null;
        _draftRotations[index] = request.RotationDegrees;
        _draftPitches[index] = request.RotationXDegrees;
        _draftFlips[index] = request.FlipX;
        _draftDurationMilliseconds = request.DurationMilliseconds;
        _draftEasing = request.Easing;
        PublishActiveDraft();
        SetCharacterLayerStatus(
            $"已载入第 {editor.Stack.SelectedIndex + 1} 段；调好后按「替换这段」写回原位置。");
        Plugin.Logger.LogInfo($"[character-layer] loaded · index={editor.Stack.SelectedIndex}");
    }

    /// <summary>Writes the current draft over the selected effect, keeping its place in the layer.</summary>
    private void ReplaceCharacterSegment()
    {
        EffectLayerEditor? editor = CharacterEditor;
        if (editor is null) return;
        bool replaced = editor.Replace();
        Plugin.Logger.LogInfo("[character-layer] replace · replaced=" + replaced + " · " + editor.Status);
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

    /// <summary>Keeps the value buttons' labels in step with the editor; enabled state is a predicate.</summary>
    private void UpdateCharacterLayerButtons()
    {
        EffectLayerEditor? editor = CharacterEditor;
        bool selected = editor?.Stack.SelectedIndex >= 0;
        if (_controlButtons.TryGetValue("character-seg", out ControlButton? segment))
            segment.Label.text = editor?.SegmentLabel ?? "角色段 0/0";
        if (_controlButtons.TryGetValue("character-load-seg", out ControlButton? load))
            load.Label.text = selected ? $"载入第 {editor!.Stack.SelectedIndex + 1} 段" : "载入这段";
        if (_controlButtons.TryGetValue("character-replace-seg", out ControlButton? replace))
            replace.Label.text = selected ? $"替换第 {editor!.Stack.SelectedIndex + 1} 段" : "替换这段";
        if (_controlButtons.TryGetValue("character-delete-seg", out ControlButton? delete))
            delete.Label.text = editor?.DeleteLabel ?? "删除这段";
    }
}
