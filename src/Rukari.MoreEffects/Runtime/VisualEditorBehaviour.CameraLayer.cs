extern alias unitycore;
extern alias unitytext;
extern alias unityui;

using System;
using System.Diagnostics;
using AzureArchive.VideoTools.Core.Cameras;
using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Interop;
using AzureArchive.VideoTools.Core.VisualEditor;
using Color = unitycore::UnityEngine.Color;
using Image = unityui::UnityEngine.UI.Image;
using RectTransform = unitycore::UnityEngine.RectTransform;
using Text = unityui::UnityEngine.UI.Text;
using TextAnchor = unitytext::UnityEngine.TextAnchor;

namespace AzureArchive.VideoTools.Runtime;

/// <summary>
/// The camera layer editor, inside the picture editor's own camera mode (2026-10-13).
///
/// <para>
/// It lives here rather than in a window of its own because the camera is composed here: the author drags the frame
/// in this mode, so this is where "and now stack that as the next effect" belongs. A second surface would have meant
/// carrying a draft to another window to use it, and two places to decide what the camera is.
/// </para>
///
/// <para>
/// It is a partial file so that the 5000-line editor keeps its shape: the stack's behaviour is in Core
/// (<see cref="EffectLayerStack"/>, tested without a game), the write is the shared transaction in
/// <see cref="EditorCommandDocumentService"/>, and this file is only the controls that connect them.
/// </para>
///
/// <para>
/// The interaction, chosen by the user: ＋ 加为下一段 is the action the author takes every time, so it is a big
/// button in the same bottom row as 应用镜头层 and 撤销 — same height, same decoration, same prominence — rather than
/// one control among many. The secondary controls (which effect the next press acts on, and moving or deleting it)
/// sit in the ordinary 42px rows above that row: a segment selector that names the effect, 上移, 下移, and 删除第 N 段.
/// The existing 应用 button writes the layer; nothing is written until it is pressed, exactly like the
/// draft-then-apply flow of the other two modes.
/// </para>
/// </summary>
public sealed partial class VisualEditorBehaviour
{
    /// <summary>Height of a control row here: the same 42 this panel uses everywhere.</summary>
    private const float CameraLayerRowHeight = 42f;

    /// <summary>Distance between two control rows here: the same 50 this panel uses everywhere.</summary>
    private const float CameraLayerRowPitch = 50f;

    private static readonly string[] CameraLayerControlKeys =
    {
        "camera-seg", "camera-move-up", "camera-move-down", "camera-delete-seg"
    };

    private RectTransform? _effectAddButtonRect;
    private Image? _effectAddButtonImage;
    private Text? _effectAddButtonText;
    private VisualEditorRect _effectAddHitRect;
    private bool _effectAddEnabled;

    /// <summary>The order of the camera's effects. One instance is one dialogue line.</summary>
    private readonly EffectLayerStack _cameraLayers = new(CameraLayers.All);

    private long _cameraLayerReadAt;
    private string _cameraLayerLogged = string.Empty;

    /// <summary>The layer the current scope edits: the whole-picture camera, or the background one.</summary>
    private EffectLayer CurrentCameraLayer => _cameraScope == SceneCameraScope.Background
        ? EffectLayerCatalog.BackgroundCamera
        : EffectLayerCatalog.Camera;

    /// <summary>The name an author reads for one camera layer key.</summary>
    private static string CameraLayerLabelOf(string layerKey) =>
        string.Equals(layerKey, EffectLayerCatalog.BackgroundCamera.ResourceKey, StringComparison.Ordinal)
            ? EffectLayerCatalog.BackgroundCamera.Label
            : EffectLayerCatalog.Camera.Label;

    private void CreateCameraLayerControls()
    {
        // Segment selection is a value button here, like 时长/缓动/对齐: this panel has no popup control, so a
        // "下拉框" is a button that shows the current value and moves to the next one when pressed.
        CreateControlButton(
            "camera-seg",
            "镜头段 0/0",
            CycleCameraSegment,
            () => _cameraLayers.CountOf(CurrentCameraLayer) > 0);
        CreateControlButton("camera-move-up", "上移", () => MoveCameraSegment(-1), () => CanMoveCameraSegment(-1));
        CreateControlButton("camera-move-down", "下移", () => MoveCameraSegment(1), () => CanMoveCameraSegment(1));
        CreateControlButton(
            "camera-delete-seg",
            "删除这段",
            DeleteCameraSegment,
            () => _cameraLayers.SelectedIndex >= 0);
    }

    /// <summary>
    /// The big ＋ 加为下一段 button that shares the bottom row with 应用镜头层 and 撤销. It is built here rather than
    /// as a control button because it has to be the same size and decoration as those two: this is the action the
    /// author takes on every effect, and a 42px row among rows does not read as one (2026-10-13).
    /// </summary>
    private void CreateEffectAddButton()
    {
        if (_panelObject is null) return;
        UiElement button = CreateImage("CameraAddButton", _panelObject.transform, ButtonDisabledColor);
        _effectAddButtonRect = button.Rect;
        _effectAddButtonImage = button.GameObject.GetComponent<Image>();
        _effectAddButtonText = CreateText(
            "CameraAddLabel",
            button.GameObject.transform,
            "＋ 加为下一段",
            24,
            TextAnchor.MiddleCenter).Text;
    }

    /// <summary>
    /// Lays the secondary rows out under the camera's own 复位/清空 row in the shapes the rest of this panel uses, and
    /// returns the row 连续对话 should move to so that nothing overlaps. The big row is laid out with the other two
    /// big buttons, by <c>Layout</c>.
    /// </summary>
    private float LayoutCameraLayerControls(float resetRowY, float rowGap)
    {
        float selectRowY = resetRowY - CameraLayerRowPitch;
        float optionWidth = (PanelWidth - 60f - (rowGap * 2f)) / 3f;
        LayoutControl("camera-seg", 30f, selectRowY, optionWidth, CameraLayerRowHeight);
        LayoutControl("camera-move-up", 30f + optionWidth + rowGap, selectRowY, optionWidth, CameraLayerRowHeight);
        LayoutControl(
            "camera-move-down",
            30f + ((optionWidth + rowGap) * 2f),
            selectRowY,
            optionWidth,
            CameraLayerRowHeight);

        float actionRowY = selectRowY - CameraLayerRowPitch;
        LayoutControl("camera-delete-seg", 30f, actionRowY, PanelWidth - 60f, CameraLayerRowHeight);
        return actionRowY - CameraLayerRowPitch;
    }

    /// <summary>
    /// Where the big ＋ 加为下一段 button goes, and where the other two go when it is on screen: the modes that own a
    /// layer split the same row three ways instead of two, and the modes that do not never see it.
    /// </summary>
    private void LayoutEffectAddButton(float buttonGap, out float buttonWidth, out float thirdX)
    {
        bool layerEditor = _mode is VisualEditorMode.Camera or VisualEditorMode.Character;
        buttonWidth = layerEditor
            ? (PanelWidth - 60f - (buttonGap * 2f)) / 3f
            : (PanelWidth - 60f - buttonGap) / 2f;
        thirdX = 30f + ((buttonWidth + buttonGap) * 2f);
        if (_effectAddButtonRect is not null) _effectAddButtonRect.gameObject.SetActive(layerEditor);
        if (_effectAddButtonText is not null) _effectAddButtonText.gameObject.SetActive(layerEditor);
    }

    /// <summary>
    /// Adds the current draft to whichever layer this mode owns. One button, two layers: the mode decides (2026-10-13).
    /// </summary>
    private void AddEffectSegment()
    {
        if (_mode == VisualEditorMode.Character)
        {
            AddCharacterSegment();
            return;
        }

        AddCameraSegment();
    }

    /// <summary>
    /// Re-reads the row at a walk, and never on top of work the author has not applied: a re-read would silently
    /// replace the stack being built.
    /// </summary>
    private void EnsureEffectLayerStack(bool force = false)
    {
        if (!_expanded) return;
        long now = Stopwatch.GetTimestamp();
        if (!force)
        {
            if (_cameraLayers.HasPendingChanges) return;
            if (now - _cameraLayerReadAt < Stopwatch.Frequency / 4) return;
        }

        _cameraLayerReadAt = now;
        if (Plugin.Api.EditorCommandDocuments is not EditorCommandDocumentService service) return;
        _cameraLayers.Adopt(service.ReadLayersOnMainThread(CameraLayers.All));
        LogCameraLayerOnce();
    }

    /// <summary>
    /// What the inspector says about the layer: its own count and length, and which effect the buttons would act on.
    /// This is the list the author reads — the selector row shows only the numbers.
    /// </summary>
    private string CameraLayerStatusLine()
    {
        EffectLayer layer = CurrentCameraLayer;
        EffectLayerSummary summary = _cameraLayers.SummaryOf(layer);
        string text = $"{layer.Label}：{summary.Text}";
        if (_cameraLayers.SelectedIndex < 0)
            return text + "　调好镜头后按「＋ 加为下一段」";

        bool sameLayer = string.Equals(
            _cameraLayers.SelectedLayer.ResourceKey,
            layer.ResourceKey,
            StringComparison.Ordinal);
        EffectLayerSegment? selected = _cameraLayers.Selected;
        return sameLayer && selected is not null
            ? text + $"　选中 {_cameraLayers.SelectedIndex + 1}. {selected.Label}"
            : text + "　（选中的段在另一层，切换范围可看到它）";
    }

    private bool CanMoveCameraSegment(int delta)
    {
        int index = _cameraLayers.SelectedIndex;
        return index >= 0
            && index + delta >= 0
            && index + delta < _cameraLayers.CountOf(CurrentCameraLayer);
    }

    /// <summary>Moves the selection to the next effect of this layer: what the selector button does when pressed.</summary>
    private void CycleCameraSegment()
    {
        int total = _cameraLayers.CountOf(CurrentCameraLayer);
        if (total == 0)
        {
            _cameraLayers.Note($"{CurrentCameraLayer.Label}层还没有效果。");
            SetCameraLayerStatus(_cameraLayers.Status);
            return;
        }

        int next = _cameraLayers.SelectedIndex < 0 ? 0 : (_cameraLayers.SelectedIndex + 1) % total;
        _cameraLayers.Select(CurrentCameraLayer, next);
        SetCameraLayerStatus(_cameraLayers.Status);
    }

    private void AddCameraSegment()
    {
        EffectDraft draft = EffectDraft.FromDirective(_currentDraftDirective);
        if (!_cameraLayers.Add(draft))
        {
            SetCameraLayerStatus(_cameraLayers.Status);
            return;
        }

        // The scope follows the effect that was added, so the selector row shows the layer it landed in.
        SelectCameraScope(draft.LayerKey == EffectLayerCatalog.BackgroundCamera.ResourceKey
            ? SceneCameraScope.Background
            : SceneCameraScope.Overall);
        SetCameraLayerStatus(_cameraLayers.Status);
    }

    private void MoveCameraSegment(int delta)
    {
        _cameraLayers.MoveSelected(delta);
        SetCameraLayerStatus(_cameraLayers.Status);
    }

    private void DeleteCameraSegment()
    {
        _cameraLayers.DeleteSelected();
        SetCameraLayerStatus(_cameraLayers.Status);
    }

    /// <summary>
    /// Writes both camera layers of the row in one transaction. The existing 应用 button is what calls this in camera
    /// mode: one apply button per mode, and in this mode "apply" means "write my layer".
    /// </summary>
    private void ApplyCameraLayer()
    {
        if (!_cameraLayers.HasPendingChanges)
        {
            SetCameraLayerStatus("镜头层没有需要写入的改动。");
            return;
        }

        if (!_cameraLayers.CanWrite)
        {
            SetCameraLayerStatus("这一句还没有读取成功，请重新选中台词后再试。");
            return;
        }

        if (Plugin.Api.EditorCommandDocuments is not EditorCommandDocumentService service)
        {
            SetCameraLayerStatus("当前编辑服务暂不支持镜头层写入。");
            return;
        }

        EffectLayerApply result = service.ApplyLayersOnMainThread(
            _cameraLayers.RevisionSha256,
            _cameraLayers.SelectionToken,
            _cameraLayers.BuildEdits());
        if (!result.Success)
        {
            SetCameraLayerStatus("写入失败：" + result.Error);
            return;
        }

        _cameraLayers.Adopt(result.Read, "已写入当前句；" + _cameraLayers.Consequence());
        // The page's own snapshot is the same document, so it has to follow the write before anything else reads it.
        RefreshCommandDocument();
        SetCameraLayerStatus(_cameraLayers.Status);
        Plugin.Logger.LogInfo("[camera-layer] applied · " + _cameraLayers.Describe());
    }

    private void SetCameraLayerStatus(string message)
    {
        _operationMessage = message;
        Text? draft = _draftText;
        if (draft is not null) draft.text = $"{CurrentCameraLayer.Label}层\n{message}";
        UpdateInspector();
        UpdateButtons();
    }

    /// <summary>
    /// What the draft text says while a mode owns a layer: the draft, and which layer「＋ 加为下一段」would put it in.
    /// It replaces the one-command-per-resource preview, which cannot describe a layer holding several effects, and it
    /// names the layer the draft itself belongs to rather than the one the mode is showing (2026-10-13).
    /// </summary>
    private void ShowEffectDraftText()
    {
        Text? draftText = _draftText;
        if (draftText is null) return;
        if (string.IsNullOrEmpty(_currentDraftDirective))
        {
            draftText.text = _documentSnapshot == null ? "等待当前台词" : SourceSummary(_documentSnapshot) + "\n草稿 --";
            return;
        }

        EffectDraft draft = EffectDraft.FromDirective(_currentDraftDirective);
        string layer = EffectLayerCatalog.ForResourceKey(draft.LayerKey)?.Label ?? draft.LayerKey;
        draftText.text = "效果草稿（未保存）\n" + _currentDraftDirective + "\n"
            + (draft.Available
                ? $"按「＋ 加为下一段」加入{layer}层"
                : draft.Error);
    }

    /// <summary>Keeps the two value buttons' labels in step with the stack; enabled state is a predicate.</summary>
    private void UpdateCameraLayerButtons()
    {
        if (_controlButtons.TryGetValue("camera-seg", out ControlButton? segment))
        {
            int total = _cameraLayers.CountOf(CurrentCameraLayer);
            int selected = string.Equals(
                    _cameraLayers.SelectedLayer.ResourceKey,
                    CurrentCameraLayer.ResourceKey,
                    StringComparison.Ordinal)
                ? _cameraLayers.SelectedIndex
                : -1;
            segment.Label.text = $"镜头段 {Math.Max(0, selected + 1)}/{total}";
        }

        if (_controlButtons.TryGetValue("camera-delete-seg", out ControlButton? delete))
        {
            delete.Label.text = _cameraLayers.SelectedIndex >= 0
                ? $"删除第 {_cameraLayers.SelectedIndex + 1} 段"
                : "删除这段";
        }
    }

    /// <summary>
    /// The big button's own appearance. It is drawn by the same theme call the other two use, so "the same button"
    /// is also literally the same code path, and it is enabled exactly when the draft could be added to the layer of
    /// whichever mode is showing.
    /// </summary>
    private void UpdateEffectAddButton()
    {
        _effectAddEnabled = _mode switch
        {
            VisualEditorMode.Camera => CanEditCamera() && !string.IsNullOrEmpty(_currentDraftDirective),
            VisualEditorMode.Character => CanEditSelectedSlot()
                && CurrentCharacterLayer is not null
                && !string.IsNullOrEmpty(_currentDraftDirective),
            _ => false
        };
        if (_effectAddButtonImage is null || _effectAddButtonRect is null) return;
        Color text = Rukari.Lib.Runtime.Tools.ToolTheme.ApplyButton(
            _effectAddButtonImage,
            _effectAddButtonRect.sizeDelta.x,
            _effectAddButtonRect.sizeDelta.y,
            _effectAddEnabled,
            false,
            false,
            decorated: true,
            primary: true);
        if (_effectAddButtonText is not null) _effectAddButtonText.color = text;
    }

    /// <summary>
    /// One line per distinct state, so the numbers the page shows can be checked against the log without the screen.
    /// </summary>
    private void LogCameraLayerOnce()
    {
        string line = "[camera-layer] " + _cameraLayers.Describe();
        if (string.Equals(line, _cameraLayerLogged, StringComparison.Ordinal)) return;
        _cameraLayerLogged = line;
        Plugin.Logger.LogInfo(line);
    }
}
