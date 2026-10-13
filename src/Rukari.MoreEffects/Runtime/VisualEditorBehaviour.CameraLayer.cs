extern alias unitycore;
extern alias unitytext;
extern alias unityui;

using System;
using AzureArchive.VideoTools.Core.Cameras;
using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Results;
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
        "camera-seg", "camera-move-up", "camera-move-down", "camera-load-seg", "camera-replace-seg",
        "camera-delete-seg"
    };

    private RectTransform? _effectAddButtonRect;
    private Image? _effectAddButtonImage;
    private Text? _effectAddButtonText;
    private VisualEditorRect _effectAddHitRect;
    private bool _effectAddEnabled;

    /// <summary>
    /// The editor of both camera layers. One instance is one dialogue line, and the policy behind it — when it
    /// re-reads, what a press sends, what its buttons say — is Core's <see cref="EffectLayerEditor"/>, shared with the
    /// character mode and the preset page (2026-10-14).
    /// </summary>
    private EffectLayerEditor? _cameraEditor;

    private string _cameraLayerLogged = string.Empty;

    private EffectLayerEditor CameraEditor => _cameraEditor ??= new EffectLayerEditor(
        CameraLayers.All,
        ReadLayersOnMainThread,
        WriteLayersOnMainThread,
        () => _currentDraftDirective,
        "镜头段");

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
            () => CameraEditor.HasSegments);
        CreateControlButton("camera-move-up", "上移", () => MoveCameraSegment(-1), () => CameraEditor.CanMove(-1));
        CreateControlButton("camera-move-down", "下移", () => MoveCameraSegment(1), () => CameraEditor.CanMove(1));
        CreateControlButton(
            "camera-load-seg",
            "载入这段",
            LoadCameraSegment,
            () => CameraEditor.Stack.SelectedIndex >= 0);
        CreateControlButton(
            "camera-replace-seg",
            "替换这段",
            ReplaceCameraSegment,
            () => CameraEditor.Stack.SelectedIndex >= 0
                && CameraEditor.Stack.SelectedLayer.ResourceKey == CurrentCameraLayer.ResourceKey
                && !string.IsNullOrEmpty(_currentDraftDirective));
        CreateControlButton(
            "camera-delete-seg",
            "删除这段",
            DeleteCameraSegment,
            () => CameraEditor.Stack.SelectedIndex >= 0);
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
    /// <param name="resetClearRowY">
    /// The row this mode puts 复位/清空 on. Every mode has one, and it is not the same variable in each: pass the row
    /// the buttons are actually drawn on, or the layer rows land on top of them (2026-10-13).
    /// </param>
    private float LayoutCameraLayerControls(float resetClearRowY, float rowGap)
    {
        float selectRowY = resetClearRowY - CameraLayerRowPitch;
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
        float third = (PanelWidth - 60f - (rowGap * 2f)) / 3f;
        LayoutControl("camera-load-seg", 30f, actionRowY, third, CameraLayerRowHeight);
        LayoutControl("camera-replace-seg", 30f + third + rowGap, actionRowY, third, CameraLayerRowHeight);
        LayoutControl(
            "camera-delete-seg",
            30f + ((third + rowGap) * 2f),
            actionRowY,
            third,
            CameraLayerRowHeight);
        return actionRowY - CameraLayerRowPitch;
    }

    /// <summary>
    /// Where the big ＋ 加为下一段 button goes, and where the other two go when it is on screen: the modes that own a
    /// layer split the same row three ways instead of two, and the modes that do not never see it.
    ///
    /// <para>
    /// Showing it and making it pressable are the same decision, so both are made here. They were written out
    /// separately once, and the character mode ended up with the drawing but not the hit rectangle: the button looked
    /// live, took the press, and did nothing (2026-10-13).
    /// </para>
    /// </summary>
    private void LayoutEffectAddButton(
        float buttonGap,
        float buttonY,
        float buttonHeight,
        out float buttonWidth,
        out float addX)
    {
        bool layerEditor = IsLayerMode;
        buttonWidth = layerEditor
            ? (PanelWidth - 60f - (buttonGap * 2f)) / 3f
            : (PanelWidth - 60f - buttonGap) / 2f;
        addX = 30f + ((buttonWidth + buttonGap) * 2f);
        if (_effectAddButtonRect is not null) _effectAddButtonRect.gameObject.SetActive(layerEditor);
        if (_effectAddButtonText is not null) _effectAddButtonText.gameObject.SetActive(layerEditor);
        _effectAddHitRect = layerEditor
            ? new VisualEditorRect(_panelRect.X + addX, _panelRect.Y + buttonY, buttonWidth, buttonHeight)
            : default;
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
    /// Reads the given layers of the selected row for whichever layer editor is asking. The two editor partials share
    /// these two calls, so a mode cannot read or write differently from its neighbour (2026-10-13).
    /// </summary>
    private static EffectLayerRead ReadLayersOnMainThread(IReadOnlyList<EffectLayer> layers) =>
        Plugin.Api.EditorCommandDocuments is EditorCommandDocumentService service
            ? service.ReadLayersOnMainThread(layers)
            : EffectLayerRead.Failed("当前编辑服务暂不支持读取效果层。");

    /// <summary>Writes the given layers in one transaction, or says why this build cannot.</summary>
    private static EffectLayerApply WriteLayersOnMainThread(
        string expectedRevisionSha256,
        string selectionToken,
        IReadOnlyList<EffectLayerEdit> layers) =>
        Plugin.Api.EditorCommandDocuments is EditorCommandDocumentService service
            ? service.ApplyLayersOnMainThread(expectedRevisionSha256, selectionToken, layers)
            : EffectLayerApply.Failed("当前编辑服务暂不支持整层写入。");

    /// <summary>
    /// Re-reads the row at a walk, and never on top of work the author has not applied: a re-read would silently
    /// replace the stack being built. The walk, the pending check and the failure text are all the shared editor's.
    /// </summary>
    private void EnsureEffectLayerStack(bool force = false)
    {
        if (!_expanded) return;
        if (_cameraEditor?.Refresh(force) == true) LogCameraLayerOnce();
    }

    /// <summary>
    /// What the inspector says about the layer: its own count and length, and which effect the buttons would act on.
    /// This is the list the author reads — the selector row shows only the numbers.
    /// </summary>
    private string CameraLayerStatusLine()
    {
        EffectLayerEditor editor = CameraEditor;
        EffectLayer layer = CurrentCameraLayer;
        string text = editor.LineOf(layer);
        if (editor.Stack.SelectedIndex < 0)
            return text + "　调好镜头后按「＋ 加为下一段」";

        bool sameLayer = string.Equals(
            editor.Stack.SelectedLayer.ResourceKey,
            layer.ResourceKey,
            StringComparison.Ordinal);
        EffectLayerSegment? selected = editor.Stack.Selected;
        return sameLayer && selected is not null
            ? text + $"　选中 {editor.Stack.SelectedIndex + 1}. {selected.Label}"
            : text + "　（选中的段在另一层，切换范围可看到它）";
    }

    /// <summary>Moves the selection to the next effect of this layer: what the selector button does when pressed.</summary>
    private void CycleCameraSegment()
    {
        EffectLayerEditor editor = CameraEditor;
        editor.Cycle();
        SetCameraLayerStatus(editor.Status);
    }

    private void AddCameraSegment()
    {
        EffectLayerEditor editor = CameraEditor;
        EffectDraft draft = EffectDraft.FromDirective(_currentDraftDirective);
        if (!editor.Add())
        {
            Plugin.Logger.LogInfo("[camera-layer] add-as-next refused · " + editor.Status);
            SetCameraLayerStatus(editor.Status);
            return;
        }

        // The scope follows the effect that was added, so the selector row shows the layer it landed in.
        SelectCameraScope(draft.LayerKey == EffectLayerCatalog.BackgroundCamera.ResourceKey
            ? SceneCameraScope.Background
            : SceneCameraScope.Overall);
        Plugin.Logger.LogInfo("[camera-layer] add-as-next · " + editor.Status);
        SetCameraLayerStatus(editor.Status);
    }

    private void MoveCameraSegment(int delta)
    {
        EffectLayerEditor editor = CameraEditor;
        editor.Move(delta);
        SetCameraLayerStatus(editor.Status);
    }

    private void DeleteCameraSegment()
    {
        EffectLayerEditor editor = CameraEditor;
        editor.Delete();
        SetCameraLayerStatus(editor.Status);
    }

    /// <summary>
    /// Pulls the selected effect back into this mode's own editor, so it can be retuned in place instead of deleted and
    /// written again (2026-10-14). It replaces whatever draft the author was holding, on purpose: pressing 载入 is the
    /// act of saying "I am editing that one".
    /// </summary>
    private void LoadCameraSegment()
    {
        EffectLayerEditor editor = CameraEditor;
        EffectLayerSegment? segment = editor.Stack.Selected;
        if (segment is null)
        {
            SetCameraLayerStatus("先在「镜头段」里选一段，再载入。");
            return;
        }

        Result<SceneCameraCommand> read = _cameraDraftBuilder.ReadCanonical(segment.CanonicalDirective);
        if (!read.Success || read.Value is null)
        {
            SetCameraLayerStatus("这一段读不出来：" + (read.Error ?? "未知原因"));
            return;
        }

        SceneCameraCommand command = read.Value;
        if (command.Operation == SceneCameraOperation.Move)
        {
            // A relative move is defined against the picture at the time it runs; this mode's draft is an absolute
            // composition, so there is nothing honest to load. The directive stays editable as text.
            SetCameraLayerStatus("相对移动段（move）只能用指令编辑；载入支持 set 与 reset。");
            return;
        }

        // The scope follows the effect: its own directive says which camera it belongs to.
        SelectCameraScope(command.Scope);
        _cameraDraftDurationMilliseconds = command.DurationMilliseconds;
        _cameraDraftEasing = command.Easing;
        if (command.Operation == SceneCameraOperation.Reset)
        {
            _cameraDraftState = null;
            _cameraResetDraft = true;
            PublishEffectDraft();
        }
        else
        {
            SetEffectDraft(
                new SceneCameraState(command.X ?? 0f, command.Y ?? 0f, command.Zoom ?? 1f),
                verifyImmediately: false);
        }

        LayoutCameraFrame();
        UpdateInspector();
        SetCameraLayerStatus(
            $"已载入第 {editor.Stack.SelectedIndex + 1} 段；调好后按「替换这段」写回原位置。");
        Plugin.Logger.LogInfo($"[camera-layer] loaded · index={editor.Stack.SelectedIndex}");
    }

    /// <summary>Writes the current draft over the selected effect, keeping its place in the layer.</summary>
    private void ReplaceCameraSegment()
    {
        EffectLayerEditor editor = CameraEditor;
        bool replaced = editor.Replace();
        Plugin.Logger.LogInfo("[camera-layer] replace · replaced=" + replaced + " · " + editor.Status);
        SetCameraLayerStatus(editor.Status);
    }

    /// <summary>
    /// Writes both camera layers of the row in one transaction. The existing 应用 button is what calls this in camera
    /// mode: one apply button per mode, and in this mode "apply" means "write my layer".
    /// </summary>
    private void ApplyCameraLayer()
    {
        EffectLayerEditor editor = CameraEditor;
        EffectLayerApply? applied = editor.Apply();
        if (applied is { Success: true })
        {
            // The page's own snapshot is the same document, so it has to follow the write before anything else reads it.
            RefreshCommandDocument();
            Plugin.Logger.LogInfo("[camera-layer] applied · " + editor.Describe());
        }

        SetCameraLayerStatus(editor.Status);
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

    /// <summary>Keeps the two value buttons' labels in step with the editor; enabled state is a predicate.</summary>
    private void UpdateCameraLayerButtons()
    {
        EffectLayerEditor editor = CameraEditor;
        bool selected = editor.Stack.SelectedIndex >= 0;
        if (_controlButtons.TryGetValue("camera-seg", out ControlButton? segment))
        {
            segment.Label.text = editor.SegmentLabel;
        }

        if (_controlButtons.TryGetValue("camera-load-seg", out ControlButton? load))
        {
            load.Label.text = selected ? $"载入第 {editor.Stack.SelectedIndex + 1} 段" : "载入这段";
        }

        if (_controlButtons.TryGetValue("camera-replace-seg", out ControlButton? replace))
        {
            replace.Label.text = selected ? $"替换第 {editor.Stack.SelectedIndex + 1} 段" : "替换这段";
        }

        if (_controlButtons.TryGetValue("camera-delete-seg", out ControlButton? delete))
        {
            delete.Label.text = editor.DeleteLabel;
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
        string line = "[camera-layer] " + CameraEditor.Describe();
        if (string.Equals(line, _cameraLayerLogged, StringComparison.Ordinal)) return;
        _cameraLayerLogged = line;
        Plugin.Logger.LogInfo(line);
    }
}
