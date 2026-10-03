extern alias unitycore;
extern alias unitytext;
extern alias unityui;
extern alias unityuimodule;

using System;
using Rukari.Lib.Tools;
using AzureArchive.VideoTools.Api;
using AzureArchive.VideoTools.Core.Cameras;
using AzureArchive.VideoTools.Core.Characters;
using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Core.Results;
using AzureArchive.VideoTools.Core.VisualEditor;
using AzureArchive.VideoTools.Interop;
using Canvas = unityuimodule::UnityEngine.Canvas;
using CanvasScaler = unityui::UnityEngine.UI.CanvasScaler;
using Color = unitycore::UnityEngine.Color;
using Font = unitytext::UnityEngine.Font;
using FontStyle = unitytext::UnityEngine.FontStyle;
using GameObject = unitycore::UnityEngine.GameObject;
using HorizontalWrapMode = unitytext::UnityEngine.HorizontalWrapMode;
using Image = unityui::UnityEngine.UI.Image;
using Input = UnityEngine.Input;
using MonoBehaviour = unitycore::UnityEngine.MonoBehaviour;
using Object = unitycore::UnityEngine.Object;
using RectTransform = unitycore::UnityEngine.RectTransform;
using RenderMode = unityuimodule::UnityEngine.RenderMode;
using Screen = unitycore::UnityEngine.Screen;
using Sprite = unitycore::UnityEngine.Sprite;
using SpriteMeshType = unitycore::UnityEngine.SpriteMeshType;
using Texture2D = unitycore::UnityEngine.Texture2D;
using TextureFormat = unitycore::UnityEngine.TextureFormat;
using Rect = unitycore::UnityEngine.Rect;
using Text = unityui::UnityEngine.UI.Text;
using TextAnchor = unitytext::UnityEngine.TextAnchor;
using Vector2 = unitycore::UnityEngine.Vector2;
using Vector3 = unitycore::UnityEngine.Vector3;
using Vector4 = unitycore::UnityEngine.Vector4;
using VerticalWrapMode = unitytext::UnityEngine.VerticalWrapMode;

namespace AzureArchive.VideoTools.Runtime;

public sealed partial class VisualEditorBehaviour : MonoBehaviour
{
    private const float PanelWidth = 720f;
    private const float PanelMargin = 80f;
    private const float OccupiedStoryWidth = 420f;
    private const float OccupiedStoryHeight = 900f;
    private const float OccupiedMinimumHitWidth = 96f;
    private const float OccupiedMinimumHitHeight = 120f;
    private const float SlotDragThreshold = 6f;
    private const int ContextPollInterval = 30;
    private const int SlotPollInterval = 12;
    private const int MaximumVisibleScreenTextVisuals = 64;

    private static readonly Color PanelHeaderColor = NativeColor(ToolPalette.Header.Background);
    private static readonly Color FrameColor = new(0.92f, 0.24f, 0.24f, 1f);
    private static readonly Color OccupiedColor = new(0.18f, 0.76f, 0.83f, 1f);
    private static readonly Color PendingColor = new(0.96f, 0.68f, 0.18f, 1f);
    private static readonly Color SelectedColor = new(0.28f, 0.88f, 0.48f, 1f);
    private static readonly Color TextColor = NativeColor(ToolPalette.Text);
    private static readonly Color ButtonEnabledColor = NativeColor(ToolPalette.Primary.Background);
    private static readonly Color ButtonEnabledTextColor = NativeColor(ToolPalette.Primary.Foreground);
    private static readonly Color UndoEnabledColor = NativeColor(ToolPalette.Undo.Background);
    private static readonly Color ButtonDisabledColor = NativeColor(ToolPalette.Disabled.Background);
    private static readonly Color ButtonDisabledTextColor = NativeColor(ToolPalette.Disabled.Foreground);
    private static readonly Color ControlEnabledColor = NativeColor(ToolPalette.Normal.Background);
    private static readonly Color ControlEnabledTextColor = NativeColor(ToolPalette.Normal.Foreground);
    private static readonly Color ControlActiveColor = NativeColor(ToolPalette.Selected.Background);
    private static readonly Color SlotSelectorOccupiedColor = NativeColor(ToolPalette.Header.Background);
    private static readonly Color CameraFrameColor = new(0.40f, 0.83f, 0.94f, 1f);
    private static readonly Color CameraCanvasColor = new(0.64f, 0.68f, 0.74f, 0.72f);
    private static readonly Color ScreenTextAnchorColor = new(0.96f, 0.68f, 0.18f, 1f);
    private static readonly Color GuideColor = new(0.42f, 0.46f, 0.50f, 0.28f);
    private static readonly Color GuideTextColor = new(0.62f, 0.65f, 0.69f, 0.9f);

    private static readonly string[] CharacterControlKeys =
    {
        "x-100", "x-10", "x+10", "x+100",
        "y-100", "y-10", "y+10", "y+100",
        "r-15", "r-1", "r+1", "r+15",
        "flip", "duration", "easing", "r0", "flipreset", "reset", "clear"
    };

    private static readonly string[] CameraControlKeys =
    {
        "camera-x-100", "camera-x-10", "camera-x+10", "camera-x+100",
        "camera-y-100", "camera-y-10", "camera-y+10", "camera-y+100",
        "camera-z-0.1", "camera-z-0.01", "camera-z+0.01", "camera-z+0.1",
        "camera-duration", "camera-easing", "camera-reset", "camera-clear"
    };

    private static readonly string[] ScreenTextControlKeys =
    {
        "text-x-100", "text-x-10", "text-x+10", "text-x+100",
        "text-y-100", "text-y-10", "text-y+10", "text-y+100",
        "text-font-10", "text-font-1", "text-font+1", "text-font+10",
        "text-align", "text-mode", "text-align-previous-x", "text-position-reset",
        "text-remove", "text-clear",
        "text-clear-all"
    };

    private static Sprite? _roundedSprite;

    private static VisualEditorOptions? _options;
    private static VisualEditorBehaviour? _instance;
    private int _openedFromToolboxFrame = -1;
    /// <summary>
    /// Whether the editor can take the shared page over right now: the feature is on, the canvas has been built,
    /// nothing has failed, and the studio is in the editing preview. The page is what the user sees while this is
    /// false, so the promise the page makes has to match what this behaviour can actually draw.
    ///
    /// <para>
    /// It reads <c>_editorPreviewVisible</c> rather than calling <c>ReadEditorPreviewMode()</c> again: that field is
    /// refreshed from the same native read once per frame in <see cref="Update"/>, and the page asks this question
    /// every frame — one native boundary call per frame is already too many (see the audit's §3.4).
    /// </para>
    /// </summary>
    internal static bool CanOpenFromToolbox => _instance?._canvasObject != null
        && _instance._editorPreviewVisible
        && !_instance._creationFailed
        && _options?.Enabled == true;

    // A preset page may reuse the last graphical slot only when it belongs to the same live selection.
    // Never reconstruct a selection token or carry a remembered slot from another dialogue into a draft.
    internal static int PresetSlotForSelection(string selectionKey)
    {
        VisualEditorBehaviour? instance = _instance;
        return instance is not null && instance.HasSynchronizedDocument() && instance.HasSelectedSlot()
            && instance._documentSnapshot?.RuntimeSelectionKey == selectionKey
            ? instance._selectedSlotIndex + 1 : 0;
    }

    /// <summary>
    /// Width of the sheet this page asks the shared panel for, in the panel's canvas units — physical pixels while
    /// the sheet is drawn at its natural size. It is the editor's own 720 story units converted to pixels, plus the
    /// padding the renderer takes off each side before it hands the content rectangle over, so what comes back is
    /// exactly the rectangle every coordinate in this file is authored against.
    /// </summary>
    internal static float HostedPreferredWidth
    {
        get
        {
            int width = unitycore::UnityEngine.Screen.width;
            if (width <= 0) return 0f;
            return (PanelWidth * (width / VisualEditorCanvasMath.StoryWidth)) + (ToolDrawerLayout.Padding * 2f);
        }
    }

    /// <summary>
    /// Height of the sheet this page asks for: the editor's own rectangle, which is
    /// <c>max(900, storyH - 160)</c> story units, plus the padding and the header row the renderer keeps for the
    /// title and the close button. It is capped by the window, because a sheet the renderer has to shrink hands
    /// back a SMALLER content rectangle than the editor's own coordinates assume.
    /// </summary>
    internal static float HostedPreferredHeight
    {
        get
        {
            int width = unitycore::UnityEngine.Screen.width;
            int height = unitycore::UnityEngine.Screen.height;
            if (width <= 0 || height <= 0) return 0f;
            float scale = width / VisualEditorCanvasMath.StoryWidth;
            float storyHeight = VisualEditorCanvasMath.LogicalStoryHeight(width, height).Value;
            float content = Math.Max(900f, storyHeight - (PanelMargin * 2f)) * scale;
            return Math.Min(
                content + (ToolDrawerLayout.Padding * 2f) + ToolDrawerLayout.HeaderRowHeight,
                height - 40f);
        }
    }

    /// <summary>Why the page cannot be the editor yet, for the short sheet the user sees instead of it.</summary>
    internal static string HostedUnavailableReason()
    {
        if (_options?.Enabled != true) return "画面效果功能已在配置中关闭。";
        if (_instance is null || _instance._canvasObject is null) return "还没有可用的编辑预览。";
        if (_instance._creationFailed) return "编辑器在本次会话中已停用，请重启游戏后重试。";
        return "请在右侧 Script 节点上进入编辑预览。";
    }

    /// <summary>
    /// The tool page became the visible leaf. The page, not this behaviour, is what the user pressed: the panel
    /// frame, the title and the close button all belong to the library now.
    /// </summary>
    internal static void HostedShown()
    {
        VisualEditorBehaviour? instance = _instance;
        if (instance is null) return;
        Plugin.Logger.LogInfo($"[host] page shown; expanded={instance._expanded}");
        instance._hostShown = true;
    }

    /// <summary>The tool page was closed, switched away, or its module was collapsed: the editor leaves with it.</summary>
    internal static void HostedHidden()
    {
        VisualEditorBehaviour? instance = _instance;
        if (instance is null) return;
        Plugin.Logger.LogInfo($"[host] page hidden; expanded={instance._expanded}");
        instance._hostShown = false;
        if (instance._expanded) instance.SetExpanded(false);
    }

    /// <summary>
    /// One frame of the tool page. The page draws nothing: its whole job is to hand over the rectangle the shared
    /// panel gave it, which is the one thing this editor cannot work out on its own.
    /// </summary>
    internal static void HostedDraw(IToolPanelSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        VisualEditorBehaviour? instance = _instance;
        if (instance is null) return;
        ToolInputRect bounds = surface.BoundsInPixels;
        instance._hostPixels = new VisualEditorRect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        instance._hostShown = true;
        instance._hostFrame = unitycore::UnityEngine.Time.frameCount;
    }

    private readonly VisualCharacterDraftBuilder _draftBuilder = new();
    private readonly VisualEditorSlotPreference _slotPreference = new();
    private readonly VisualCameraDraftBuilder _cameraDraftBuilder = new();
    private readonly EditorCommandDocumentComposer _documentComposer = new();
    private readonly CharacterSlotSnapshot?[] _slotSnapshots =
        new CharacterSlotSnapshot?[CharacterTransformCommandValidator.MaximumPublicSlot];
    private readonly VisualEditorPoint?[] _draftPositions =
        new VisualEditorPoint?[CharacterTransformCommandValidator.MaximumPublicSlot];
    private readonly float?[] _draftRotations =
        new float?[CharacterTransformCommandValidator.MaximumPublicSlot];
    private readonly bool?[] _draftFlips =
        new bool?[CharacterTransformCommandValidator.MaximumPublicSlot];
    private readonly SlotVisual?[] _slotVisuals =
        new SlotVisual?[CharacterTransformCommandValidator.MaximumPublicSlot];
    private readonly SlotSelectorButton?[] _slotSelectorButtons =
        new SlotSelectorButton?[CharacterTransformCommandValidator.MaximumPublicSlot];
    private readonly Dictionary<string, ControlButton> _controlButtons =
        new(StringComparer.Ordinal);

    private GameObject? _canvasObject;
    private CanvasScaler? _canvasScaler;
    private GameObject? _panelObject;
    /// <summary>Content rectangle handed over by the shared tool page, in physical screen pixels.</summary>
    private VisualEditorRect _hostPixels;
    /// <summary>The host rectangle the current layout was built from, so a moved sheet is noticed.</summary>
    private VisualEditorRect _laidOutHostPixels;
    /// <summary>Whether the tool page says it is the visible leaf right now.</summary>
    private bool _hostShown;
    /// <summary>
    /// Last frame the tool page drew this editor. Zero means it never has.
    ///
    /// <para>
    /// This is the fact the editor's visibility is really derived from. The renderer draws the open page every
    /// single frame, so "the page drew me a moment ago" is stronger evidence than a show/hide callback: if the page
    /// is switched away, collapsed, or the renderer stops for any reason at all — including a missed callback —
    /// the editor takes itself off screen instead of waiting to be told. That is what keeps one mod's editor from
    /// being painted over another mod's panel.
    /// </para>
    /// </summary>
    private int _hostFrame;
    /// <summary>
    /// Frames a stale heartbeat is forgiven. The renderer hides its panel for exactly one frame when it consumes a
    /// level-change click, and the editor must not flicker off and on for that.
    /// </summary>
    private const int HostHeartbeatGrace = 3;
    private Text? _draftText;
    private Text? _statusText;
    private RectTransform? _applyButtonRect;
    private Image? _applyButtonImage;
    private Text? _applyButtonText;
    private RectTransform? _undoButtonRect;
    private Image? _undoButtonImage;
    private Text? _undoButtonText;
    private Text? _inspectorText;
    private RectTransform? _rotationDialRect;
    private RectTransform? _rotationHandleRect;
    private Text? _rotationValueText;
    private RectTransform? _frameRoot;
    private readonly RectTransform?[] _frameLines = new RectTransform?[4];
    private readonly RectTransform?[] _slotGuideLines = new RectTransform?[5];
    private readonly Text?[] _slotGuideLabels = new Text?[5];
    private readonly RectTransform?[] _cameraCanvasLines = new RectTransform?[4];
    private readonly RectTransform?[] _cameraFrameLines = new RectTransform?[4];
    private readonly RectTransform?[] _cameraZoomHandles = new RectTransform?[4];
    private RectTransform? _zeroYGuide;
    private Text? _zeroYLabel;
    private RectTransform? _cameraFrameRoot;
    private Text? _cameraFrameLabel;
    private RectTransform? _screenTextPreviewRect;
    private RectTransform? _screenTextAnchorRect;
    private Text? _screenTextPreview;
    private readonly RectTransform?[] _visibleScreenTextRects =
        new RectTransform?[MaximumVisibleScreenTextVisuals];
    private readonly RectTransform?[] _visibleScreenTextAnchorRects =
        new RectTransform?[MaximumVisibleScreenTextVisuals];
    private readonly Text?[] _visibleScreenTextLabels =
        new Text?[MaximumVisibleScreenTextVisuals];
    private Image? _screenTextAnchorImage;
    private Font? _font;
    private VisualEditorRect _panelRect;
    private VisualEditorRect _viewportRect;
    private VisualEditorRect _applyHitRect;
    private VisualEditorRect _undoHitRect;
    private VisualEditorRect _rotationDialHitRect;
    private VisualEditorRect _cameraFrameHitRect;
    private readonly VisualEditorRect[] _cameraZoomHandleHitRects = new VisualEditorRect[4];
    private VisualEditorRect _screenTextHitRect;
    private float _storyHeight;
    private float _canvasScale = 1f;
    private int _lastScreenWidth;
    private int _lastScreenHeight;
    private int _updateCount;
    private int _pressedSlotIndex = -1;
    private int _draggingSlotIndex = -1;
    private int _selectedSlotIndex = -1;
    private VisualEditorPoint _slotPressPointer;
    private VisualEditorPoint _dragOffset;
    private bool _rotationDragging;
    private bool _cameraDragging;
    private bool _cameraZoomDragging;
    private bool _cameraPressed;
    private bool _screenTextPressed;
    private bool _screenTextDragging;
    private int _cameraZoomHandleIndex = -1;
    private VisualEditorPoint _cameraPressPointer;
    private VisualEditorPoint _cameraDragOffset;
    private VisualEditorPoint _screenTextPressPointer;
    private VisualEditorPoint _screenTextDragOffset;
    private VisualEditorMode _mode = VisualEditorMode.Character;
    private bool _expanded;
    private bool _editorPreviewVisible;
    private bool _creationFailed;
    private bool _canvasActive;
    private string _sceneIdentity = string.Empty;
    private string _currentDraftDirective = string.Empty;
    private string _operationMessage = string.Empty;
    private IReadOnlyList<int> _officialTransitionSlots = Array.Empty<int>();
    private string _documentReadError = string.Empty;
    private int _draftDurationMilliseconds;
    private CharacterTransformEasing _draftEasing;
    private int _cameraDraftDurationMilliseconds;
    private CharacterTransformEasing _cameraDraftEasing;
    private SceneCameraState _cameraState = SceneCameraState.Default;
    private SceneCameraState? _cameraDraftState;
    private SceneCameraCommand? _selectedExistingCameraCommand;
    private bool _cameraResetDraft;
    private bool _cameraReadAvailable;
    private string _cameraReadError = string.Empty;
    private ScreenTextDirective? _screenTextDraft;
    private ScreenTextDirective? _selectedExistingScreenText;
    private ScreenTextDirective? _rememberedScreenTextSettings;
    private bool _screenTextRemoveDraft;
    private bool _screenTextClearAllDraft;
    private string _screenTextContent = string.Empty;
    private IReadOnlyList<VisibleScreenTextSnapshot> _visibleScreenTexts =
        Array.Empty<VisibleScreenTextSnapshot>();
    private string _visibleScreenTextReadError = string.Empty;
    private VisualCharacterDraftRequest? _activeDraft;
    private VisualCharacterDraftRequest? _selectedExistingDraft;
    private bool _selectedCommandGraphicallyUnsupported;
    private EditorCommandDocumentSnapshot? _documentSnapshot;
    private EditorCommandDocumentSnapshot? _draftSourceDocument;
    private EditorCommandEditPreviewSnapshot? _verifiedPreview;

    public VisualEditorBehaviour(IntPtr pointer)
        : base(pointer)
    {
    }

    internal static void Initialize(VisualEditorOptions options)
    {
        _options = options;
    }

    public void Update()
    {
        _instance = this;
        VisualEditorOptions? options = _options;
        if (options == null || !options.Enabled || _creationFailed)
        {
            CancelPointerInteraction();
            VisualEditorInputGuard.Clear();
            if (_canvasActive)
            {
                try { SetCanvasActive(false); }
                catch { /* Native canvas may already have been torn down; input is released above. */ }
            }
            return;
        }

        try
        {
            _updateCount++;
            // Native previewMode remains EditorPreview on the project graph. Resolve the visible
            // Script inspector on every frame, before any retained hit rectangles can be used.
            RefreshEditorVisibility();
            if (!_editorPreviewVisible) return;
            if (ReferenceEquals(_canvasObject, null))
            {
                if (_updateCount != 1 && _updateCount % ContextPollInterval != 0)
                {
                    return;
                }

                CreateUi(options);
            }

            // The shared tool page owns the frame, the title, the close button and every input region now, so the
            // editor follows the page instead of asking the toolbox whether it may draw. There is no
            // "close the editor because the toolbox is open" rule any more: the toolbox IS the editor's panel.
            //
            // Visibility is derived from the page's per-frame heartbeat, not only from its callbacks: a page that
            // is no longer being drawn cannot leave this editor on screen over another module's panel.
            bool hostAlive = _hostShown && _hostFrame != 0
                && unitycore::UnityEngine.Time.frameCount - _hostFrame <= HostHeartbeatGrace;
            if (hostAlive && !_expanded && _canvasObject is not null && _hostPixels.IsValid)
            {
                _openedFromToolboxFrame = unitycore::UnityEngine.Time.frameCount;
                SetExpanded(true);
            }
            else if (!hostAlive && (_expanded || _canvasActive))
            {
                // The canvas is checked as well as the flag: whatever put editor objects on screen, a page that is
                // no longer drawing them is reason enough to take them down again.
                int heartbeatAge = unitycore::UnityEngine.Time.frameCount - _hostFrame;
                Plugin.Logger.LogInfo($"[host] heartbeat lost: pageShown={_hostShown}; heartbeatAge={heartbeatAge}");
                SetExpanded(false);
            }

            if (!_expanded)
            {
                VisualEditorInputGuard.Clear();
                return;
            }

            if (Screen.width != _lastScreenWidth || Screen.height != _lastScreenHeight
                || !_hostPixels.Equals(_laidOutHostPixels))
            {
                // Not only the screen: the shared panel can move or resize the sheet without the screen changing
                // (another module widens the column, a page asks for a different height), and this editor lays
                // itself out from the rectangle it is handed, so it follows that rectangle instead.
                Layout();
            }

            HandlePointer();
            PublishInputRegion();
            if (_expanded
                && _draggingSlotIndex < 0
                && !_rotationDragging
                && !_cameraDragging
                && !_cameraZoomDragging
                && !_screenTextDragging
                && (_updateCount == 1 || _updateCount % SlotPollInterval == 0))
            {
                RefreshSlots();
                RefreshCommandDocument();
                if (_mode == VisualEditorMode.Camera)
                {
                    RefreshCameraState();
                }
                else if (_mode == VisualEditorMode.ScreenText)
                {
                    RefreshVisibleScreenTexts();
                }
            }

            if (_expanded)
            {
                RefreshSceneIdentity();
                LayoutSlots();
                LayoutCameraFrame();
                LayoutVisibleScreenTexts();
                LayoutScreenTextPreview();
            }
        }
        catch (Exception ex)
        {
            _creationFailed = true;
            SetCanvasActive(false);
            Plugin.Logger.LogError(
                $"Visual editor candidate disabled after runtime failure: "
                + $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public void OnDestroy()
    {
        _instance = null;
        VisualEditorInputGuard.Clear();
        if (!ReferenceEquals(_canvasObject, null))
        {
            Object.Destroy(_canvasObject);
        }
    }

    private void CreateUi(VisualEditorOptions options)
    {
        // This module has one entrance: its page in the shared toolbox.
        _expanded = false;
        _draftDurationMilliseconds = options.DraftDurationMilliseconds;
        _draftEasing = options.DraftEasing;
        _cameraDraftDurationMilliseconds = options.DraftDurationMilliseconds;
        _cameraDraftEasing = options.DraftEasing;
        _font = CreateFont();

        _canvasObject = new GameObject("AAVT Visual Editor Canvas");
        Canvas canvas = _canvasObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        // Above the shared toolbox (32710): the editor's controls live inside the panel that library draws, so
        // they have to be painted after it.
        canvas.sortingOrder = 32720;
        canvas.pixelPerfect = false;
        _canvasScaler = _canvasObject.AddComponent<CanvasScaler>();
        _canvasScaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
        Object.DontDestroyOnLoad(_canvasObject);

        // This object is only the root of the editor's own controls. The panel frame, the header, the title and
        // the toggle handle all belong to the shared toolbox now: a second self-built frame is exactly what the
        // migration removes, and two frames fighting over the same space is what it looked like.
        _panelObject = new GameObject("Content");
        RectTransform contentRoot = _panelObject.AddComponent<RectTransform>();
        contentRoot.SetParent(_canvasObject.transform, false);
        _statusText = CreateText(
            "Status",
            _panelObject.transform,
            "来源  |  草稿",
            20,
            TextAnchor.MiddleRight).Text;
        _draftText = CreateText(
            "Draft",
            _panelObject.transform,
            "草稿  --",
            20,
            TextAnchor.UpperLeft).Text;
        _inspectorText = CreateText(
            "InspectorSummary",
            _panelObject.transform,
            "请从固定槽位栏选择，或直接点击红框内的人物槽位",
            18,
            TextAnchor.MiddleLeft).Text;

        UiElement rotationDial = CreateImage(
            "RotationDial",
            _panelObject.transform,
            PanelHeaderColor);
        _rotationDialRect = rotationDial.Rect;
        _rotationHandleRect = CreateImage(
            "RotationHandle",
            rotationDial.GameObject.transform,
            SelectedColor,
            rounded: false).Rect;
        _rotationValueText = CreateText(
            "RotationValue",
            rotationDial.GameObject.transform,
            "旋转 --",
            20,
            TextAnchor.MiddleCenter).Text;

        CreateControlButton(
            "mode-character",
            "立绘操控",
            () => SetEditorMode(VisualEditorMode.Character),
            () => true,
            () => _mode == VisualEditorMode.Character);
        CreateControlButton(
            "mode-camera",
            "镜头操控",
            () => SetEditorMode(VisualEditorMode.Camera),
            () => true,
            () => _mode == VisualEditorMode.Camera);
        CreateControlButton(
            "mode-screen-text",
            "屏幕文字",
            () => SetEditorMode(VisualEditorMode.ScreenText),
            () => true,
            () => _mode == VisualEditorMode.ScreenText);


        CreateControlButton("x-100", "X -100", () => NudgePosition(-100f, 0f));
        CreateControlButton("x-10", "X -10", () => NudgePosition(-10f, 0f));
        CreateControlButton("x+10", "X +10", () => NudgePosition(10f, 0f));
        CreateControlButton("x+100", "X +100", () => NudgePosition(100f, 0f));
        CreateControlButton("y-100", "Y -100", () => NudgePosition(0f, -100f));
        CreateControlButton("y-10", "Y -10", () => NudgePosition(0f, -10f));
        CreateControlButton("y+10", "Y +10", () => NudgePosition(0f, 10f));
        CreateControlButton("y+100", "Y +100", () => NudgePosition(0f, 100f));
        CreateControlButton("r-15", "旋转 -15", () => NudgeRotation(-15f));
        CreateControlButton("r-1", "旋转 -1", () => NudgeRotation(-1f));
        CreateControlButton("r+1", "旋转 +1", () => NudgeRotation(1f));
        CreateControlButton("r+15", "旋转 +15", () => NudgeRotation(15f));
        CreateControlButton(
            "flip",
            "左右翻转",
            ToggleFlip,
            CanEditSelectedSlot,
            () => SelectedFlip());
        CreateControlButton(
            "duration",
            "时长",
            CycleDuration,
            SelectedSlotIsOccupied);
        CreateControlButton(
            "easing",
            "缓动",
            CycleEasing,
            SelectedSlotIsOccupied);
        CreateControlButton(
            "r0",
            "角度归零",
            () => SetRotationDraft(0f, verifyImmediately: true),
            CanEditSelectedSlot,
            () => HasSelectedSlot() && MathF.Abs(SelectedRotation()) > 0.05f);
        CreateControlButton(
            "flipreset",
            "翻转复位",
            ResetFlip,
            CanEditSelectedSlot,
            () => HasSelectedSlot() && SelectedFlip());
        CreateControlButton(
            "reset",
            "整体重置·回归官方槽位",
            CreateResetDraft,
            SelectedSlotIsOccupied,
            () => _activeDraft?.Operation == VisualCharacterDraftOperation.Reset
                || (_activeDraft == null
                    && _selectedExistingDraft?.Operation == VisualCharacterDraftOperation.Reset));
        CreateControlButton(
            "clear",
            "清除草稿",
            ClearActiveDraft,
            () => _activeDraft != null || !string.IsNullOrEmpty(_currentDraftDirective));
        CreateControlButton(
            "continue",
            "连续对话",
            ToggleContinue,
            () => _documentSnapshot != null,
            () => _documentSnapshot?.HasContinueDirective == true);

        CreateControlButton(
            "camera-x-100",
            "镜头 X -100",
            () => NudgeCameraPosition(-100f, 0f),
            CanEditCamera);
        CreateControlButton(
            "camera-x-10",
            "镜头 X -10",
            () => NudgeCameraPosition(-10f, 0f),
            CanEditCamera);
        CreateControlButton(
            "camera-x+10",
            "镜头 X +10",
            () => NudgeCameraPosition(10f, 0f),
            CanEditCamera);
        CreateControlButton(
            "camera-x+100",
            "镜头 X +100",
            () => NudgeCameraPosition(100f, 0f),
            CanEditCamera);
        CreateControlButton(
            "camera-y-100",
            "镜头 Y -100",
            () => NudgeCameraPosition(0f, -100f),
            CanEditCamera);
        CreateControlButton(
            "camera-y-10",
            "镜头 Y -10",
            () => NudgeCameraPosition(0f, -10f),
            CanEditCamera);
        CreateControlButton(
            "camera-y+10",
            "镜头 Y +10",
            () => NudgeCameraPosition(0f, 10f),
            CanEditCamera);
        CreateControlButton(
            "camera-y+100",
            "镜头 Y +100",
            () => NudgeCameraPosition(0f, 100f),
            CanEditCamera);
        CreateControlButton(
            "camera-z-0.1",
            "缩放 -0.1",
            () => NudgeCameraZoom(-0.1f),
            CanEditCamera);
        CreateControlButton(
            "camera-z-0.01",
            "缩放 -0.01",
            () => NudgeCameraZoom(-0.01f),
            CanEditCamera);
        CreateControlButton(
            "camera-z+0.01",
            "缩放 +0.01",
            () => NudgeCameraZoom(0.01f),
            CanEditCamera);
        CreateControlButton(
            "camera-z+0.1",
            "缩放 +0.1",
            () => NudgeCameraZoom(0.1f),
            CanEditCamera);
        CreateControlButton(
            "camera-duration",
            "镜头时长",
            CycleCameraDuration,
            CanEditCamera);
        CreateControlButton(
            "camera-easing",
            "镜头缓动",
            CycleCameraEasing,
            CanEditCamera);
        CreateControlButton(
            "camera-reset",
            "镜头重置",
            CreateCameraResetDraft,
            CanEditCamera,
            () => _cameraResetDraft);
        CreateControlButton(
            "camera-clear",
            "清除镜头草稿",
            ClearCameraDraft,
            () => _cameraDraftState.HasValue || _cameraResetDraft);

        CreateControlButton(
            "text-x-100",
            "文字 X -100",
            () => NudgeScreenTextPosition(-100f, 0f),
            CanEditScreenText);
        CreateControlButton(
            "text-x-10",
            "文字 X -10",
            () => NudgeScreenTextPosition(-10f, 0f),
            CanEditScreenText);
        CreateControlButton(
            "text-x+10",
            "文字 X +10",
            () => NudgeScreenTextPosition(10f, 0f),
            CanEditScreenText);
        CreateControlButton(
            "text-x+100",
            "文字 X +100",
            () => NudgeScreenTextPosition(100f, 0f),
            CanEditScreenText);
        CreateControlButton(
            "text-y-100",
            "文字 Y -100",
            () => NudgeScreenTextPosition(0f, -100f),
            CanEditScreenText);
        CreateControlButton(
            "text-y-10",
            "文字 Y -10",
            () => NudgeScreenTextPosition(0f, -10f),
            CanEditScreenText);
        CreateControlButton(
            "text-y+10",
            "文字 Y +10",
            () => NudgeScreenTextPosition(0f, 10f),
            CanEditScreenText);
        CreateControlButton(
            "text-y+100",
            "文字 Y +100",
            () => NudgeScreenTextPosition(0f, 100f),
            CanEditScreenText);
        CreateControlButton(
            "text-font-10",
            "字号 -10",
            () => NudgeScreenTextFont(-10),
            CanEditScreenText);
        CreateControlButton(
            "text-font-1",
            "字号 -1",
            () => NudgeScreenTextFont(-1),
            CanEditScreenText);
        CreateControlButton(
            "text-font+1",
            "字号 +1",
            () => NudgeScreenTextFont(1),
            CanEditScreenText);
        CreateControlButton(
            "text-font+10",
            "字号 +10",
            () => NudgeScreenTextFont(10),
            CanEditScreenText);
        CreateControlButton(
            "text-align",
            "对齐",
            ToggleScreenTextAlignment,
            CanEditScreenText);
        CreateControlButton(
            "text-mode",
            "显示方式",
            CycleScreenTextRevealMode,
            CanEditScreenText);
        CreateControlButton(
            "text-position-reset",
            "位置归零",
            ResetScreenTextPosition,
            CanEditScreenText);
        CreateControlButton(
            "text-align-previous-x",
            "X 对齐上一段",
            AlignScreenTextToPreviousX,
            CanAlignScreenTextToPrevious);
        CreateControlButton(
            "text-remove",
            "移除本场文字指令",
            CreateScreenTextRemoveDraft,
            () => CanEditScreenText()
                && _selectedExistingScreenText != null,
            () => _screenTextRemoveDraft);
        CreateControlButton(
            "text-clear",
            "清除文字草稿",
            ClearScreenTextDraft,
            () => _screenTextDraft != null
                || _screenTextRemoveDraft
                || _screenTextClearAllDraft);
        CreateControlButton(
            "text-clear-all",
            "清除所有已显示文字  #clearST",
            CreateScreenTextClearAllDraft,
            CanAddClearScreenText,
            () => _screenTextClearAllDraft
                || _documentSnapshot?.HasClearScreenTextDirective == true);

        UiElement applyButton = CreateImage(
            "ApplyButton",
            _panelObject.transform,
            ButtonDisabledColor);
        _applyButtonRect = applyButton.Rect;
        _applyButtonImage = applyButton.GameObject.GetComponent<Image>();
        _applyButtonText = CreateText(
            "ApplyLabel",
            applyButton.GameObject.transform,
            "应用草稿",
            24,
            TextAnchor.MiddleCenter).Text;

        UiElement undoButton = CreateImage(
            "UndoButton",
            _panelObject.transform,
            ButtonDisabledColor);
        _undoButtonRect = undoButton.Rect;
        _undoButtonImage = undoButton.GameObject.GetComponent<Image>();
        _undoButtonText = CreateText(
            "UndoLabel",
            undoButton.GameObject.transform,
            "撤销",
            24,
            TextAnchor.MiddleCenter).Text;

        _frameRoot = CreateRect("StoryFrame", _panelObject.transform);
        for (int i = 0; i < _frameLines.Length; i++)
        {
            _frameLines[i] = CreateImage(
                $"FrameLine{i}",
                _frameRoot,
                FrameColor,
                rounded: false).Rect;
        }

        _zeroYGuide = CreateImage("ZeroYGuide", _frameRoot, GuideColor, rounded: false).Rect;
        _zeroYLabel = CreateText(
            "ZeroYLabel",
            _frameRoot,
            "Y 0",
            14,
            TextAnchor.MiddleLeft).Text;
        _zeroYLabel.color = GuideTextColor;
        for (int index = 0; index < _slotGuideLines.Length; index++)
        {
            int publicSlot = index + 1;
            _slotGuideLines[index] = CreateImage(
                $"SlotGuide{publicSlot}",
                _frameRoot,
                GuideColor,
                rounded: false).Rect;
            _slotGuideLabels[index] = CreateText(
                $"SlotGuideLabel{publicSlot}",
                _frameRoot,
                $"#{publicSlot}",
                14,
                TextAnchor.MiddleCenter).Text;
            _slotGuideLabels[index]!.color = GuideTextColor;
        }

        for (int index = 0; index < _slotVisuals.Length; index++)
        {
            CreateSlotVisual(index, _frameRoot);
            CreateSlotSelectorButton(index);
        }

        for (int index = 0; index < _cameraCanvasLines.Length; index++)
        {
            _cameraCanvasLines[index] = CreateImage(
                $"CameraCanvasLine{index}",
                _frameRoot,
                CameraCanvasColor,
                rounded: false).Rect;
        }

        _cameraFrameRoot = CreateRect("CameraFrame", _frameRoot);
        for (int index = 0; index < _cameraFrameLines.Length; index++)
        {
            _cameraFrameLines[index] = CreateImage(
                $"CameraFrameLine{index}",
                _cameraFrameRoot,
                CameraFrameColor,
                rounded: false).Rect;
            _cameraZoomHandles[index] = CreateImage(
                $"CameraZoomHandle{index}",
                _cameraFrameRoot,
                CameraFrameColor).Rect;
        }

        _cameraFrameLabel = CreateText(
            "CameraFrameLabel",
            _cameraFrameRoot,
            "镜头",
            18,
            TextAnchor.MiddleCenter).Text;

        for (int index = 0; index < MaximumVisibleScreenTextVisuals; index++)
        {
            RectTransform root = CreateRect($"VisibleScreenText{index}", _frameRoot);
            Text label = CreateText(
                "Label",
                root,
                string.Empty,
                16,
                TextAnchor.MiddleLeft).Text;
            RectTransform anchor = CreateImage(
                "StartAnchor",
                _frameRoot,
                ScreenTextAnchorColor,
                rounded: false).Rect;
            root.gameObject.SetActive(false);
            anchor.gameObject.SetActive(false);
            _visibleScreenTextRects[index] = root;
            _visibleScreenTextAnchorRects[index] = anchor;
            _visibleScreenTextLabels[index] = label;
        }

        _screenTextPreviewRect = CreateRect("ScreenTextPreview", _frameRoot);
        _screenTextPreview = CreateText(
            "ScreenTextPreviewLabel",
            _screenTextPreviewRect,
            "屏幕文字预览",
            16,
            TextAnchor.MiddleLeft).Text;
        _screenTextPreview.color = TextColor;
        UiElement screenTextAnchor = CreateImage(
            "ScreenTextAnchor",
            _frameRoot,
            ScreenTextAnchorColor,
            rounded: false);
        _screenTextAnchorRect = screenTextAnchor.Rect;
        _screenTextAnchorImage = screenTextAnchor.GameObject.GetComponent<Image>();

        SetExpanded(_expanded);
        Layout();
        SetCanvasActive(false);
        Plugin.Logger.LogInfo(
            "Visual editor candidate UI created; mode=slot-anchor-aware-precision-composer; coordinateModel=story-anchor-plus-local-offset; directArchiveWrites=false.");
    }

    private void RefreshEditorVisibility()
    {
        bool visible = ReadEditorPreviewMode();
        if (visible == _editorPreviewVisible)
        {
            return;
        }

        _editorPreviewVisible = visible;
        if (!visible)
        {
            // Dropping the entire canvas also removes all preview frames and guide labels.
            CancelPointerInteraction();
            VisualEditorInputGuard.Clear();
            _expanded = false;
            ClearSelectionDrafts();
            _documentSnapshot = null;
            SetCanvasActive(false);
        }
    }

    private static bool ReadEditorPreviewMode() =>
        Rukari.Lib.Runtime.Editor.EditorWorkspaceContext.IsNodeEditorVisible;

    private void RefreshSlots()
    {
        ApiResult<IReadOnlyList<CharacterSlotSnapshot>> result =
            Plugin.Api.CharacterTransforms.ReadSlotsOnMainThread();
        if (!result.Success || result.Value == null)
        {
            Text? unavailableStatus = _statusText;
            if (unavailableStatus is not null)
            {
                unavailableStatus.text = "SLOTS UNAVAILABLE";
            }
            return;
        }

        Array.Clear(_slotSnapshots, 0, _slotSnapshots.Length);
        foreach (CharacterSlotSnapshot snapshot in result.Value)
        {
            int index = snapshot.PublicSlot - 1;
            if (index >= 0 && index < _slotSnapshots.Length)
            {
                _slotSnapshots[index] = snapshot;
            }
        }

        if (_activeDraft != null
            && _activeDraft.PublicSlot >= CharacterTransformCommandValidator.MinimumPublicSlot
            && _activeDraft.PublicSlot <= CharacterTransformCommandValidator.MaximumPublicSlot)
        {
            CharacterSlotSnapshot? current = _slotSnapshots[_activeDraft.PublicSlot - 1];
            bool occupied = current?.Occupied == true;
            if (occupied != _activeDraft.Occupied)
            {
                ClearActiveDraft("SLOT OCCUPANCY CHANGED; DRAFT CLEARED");
            }
        }

        LoadExistingCommandForSelection(preserveActiveDraft: true);
        UpdateInspector();

    }

    private void RefreshSceneIdentity()
    {
        string identity = Plugin.Api.SceneIdentity.Current?.CompiledScriptSha256 ?? string.Empty;
        if (string.Equals(identity, _sceneIdentity, StringComparison.Ordinal))
        {
            return;
        }

        _sceneIdentity = identity;
        ClearSelectionDrafts();
        _documentSnapshot = null;
        RefreshCommandDocument();
        UpdateInspector();
    }

    private void ClearSelectionDrafts()
    {
        // A playback refresh invalidates edit authority, not the user's target button.
        // Keep only the managed UI preference; every draft still clears below.
        if (HasSelectedSlot())
            _slotPreference.Remember(_documentSnapshot?.RuntimeContextId, _selectedSlotIndex + 1);
        Array.Clear(_draftPositions, 0, _draftPositions.Length);
        Array.Clear(_draftRotations, 0, _draftRotations.Length);
        Array.Clear(_draftFlips, 0, _draftFlips.Length);
        _pressedSlotIndex = -1;
        _draggingSlotIndex = -1;
        _selectedSlotIndex = -1;
        _rotationDragging = false;
        _cameraPressed = false;
        _cameraDragging = false;
        _cameraZoomDragging = false;
        _cameraZoomHandleIndex = -1;
        _cameraState = SceneCameraState.Default;
        _cameraDraftState = null;
        _selectedExistingCameraCommand = null;
        _cameraResetDraft = false;
        _cameraReadAvailable = false;
        _cameraReadError = string.Empty;
        _screenTextPressed = false;
        _screenTextDragging = false;
        _screenTextDraft = null;
        _selectedExistingScreenText = null;
        _screenTextRemoveDraft = false;
        _screenTextClearAllDraft = false;
        _screenTextContent = string.Empty;

        _activeDraft = null;
        _selectedExistingDraft = null;
        _selectedCommandGraphicallyUnsupported = false;
        _currentDraftDirective = string.Empty;
        _operationMessage = string.Empty;
        _draftSourceDocument = null;
        _verifiedPreview = null;
    }

    private void HandlePointer()
    {
        // An inactive GameObject still has a RectTransform. Never hit-test the hidden old entrance.
        if (!_expanded || !_canvasActive || !_editorPreviewVisible || !ReadEditorPreviewMode()) return;
        if (_openedFromToolboxFrame == unitycore::UnityEngine.Time.frameCount) return;
        VisualEditorPoint pointer = PointerInCanvas();
        if (Input.GetMouseButtonDown(0) && pointer.X < _panelRect.X)
        {
            // Camera handles or text anchors may extend beyond their preview frame. They must
            // never start a feature drag over the permanent shared entrance rail.
            CancelPointerInteraction();
            return;
        }

        if (Input.GetMouseButtonDown(0))
        {
            if (Contains(_applyHitRect, pointer))
            {
                ApplyVerifiedDraft();
                return;
            }

            if (Contains(_undoHitRect, pointer))
            {
                UndoLastApply();
                return;
            }

            for (int index = 0;
                 _mode == VisualEditorMode.Character
                    && index < _slotSelectorButtons.Length;
                 index++)
            {
                SlotSelectorButton? selector = _slotSelectorButtons[index];
                if (selector != null && Contains(selector.HitRect, pointer))
                {
                    SelectSlot(index);
                    UpdateButtons();
                    LayoutSlots();
                    return;
                }
            }

            foreach (ControlButton button in _controlButtons.Values.Reverse())
            {
                if (button.Visible && button.Enabled() && Contains(button.HitRect, pointer))
                {
                    button.Invoke();
                    UpdateInspector();
                    UpdateButtons();
                    return;
                }
            }

            if (_mode == VisualEditorMode.Camera && HandleCameraPress(pointer))
            {
                return;
            }

            if (_mode == VisualEditorMode.ScreenText
                && HandleScreenTextPress(pointer))
            {
                return;
            }

            if (_mode == VisualEditorMode.Character
                && CanEditSelectedSlot()
                && Contains(_rotationDialHitRect, pointer))
            {
                _rotationDragging = true;
                BeginDraftSource();
                UpdateRotationFromPointer(pointer);
                return;
            }

            int hitSlotIndex = _mode == VisualEditorMode.Character
                ? HitTestSlot(pointer)
                : -1;
            if (hitSlotIndex >= 0)
            {
                SlotVisual? visual = _slotVisuals[hitSlotIndex];
                if (visual != null)
                {
                    SelectSlot(hitSlotIndex);
                    _pressedSlotIndex = hitSlotIndex;
                    _slotPressPointer = pointer;
                    _dragOffset = new VisualEditorPoint(
                        pointer.X - visual.Center.X,
                        pointer.Y - visual.Center.Y);
                }
            }

            UpdateButtons();
        }

        if (_mode == VisualEditorMode.Camera)
        {
            HandleCameraDrag(pointer);
            return;
        }

        if (_mode == VisualEditorMode.ScreenText)
        {
            HandleScreenTextDrag(pointer);
            return;
        }

        if (_rotationDragging && Input.GetMouseButton(0))
        {
            UpdateRotationFromPointer(pointer);
        }

        if (_rotationDragging && Input.GetMouseButtonUp(0))
        {
            _rotationDragging = false;
            VerifyDraftAgainstSelected();
            UpdateInspector();
        }

        if (_pressedSlotIndex >= 0
            && _draggingSlotIndex < 0
            && Input.GetMouseButton(0)
            && VisualEditorCanvasMath.ExceedsDragThreshold(
                _slotPressPointer,
                pointer,
                SlotDragThreshold))
        {
            int pressedIndex = _pressedSlotIndex;
            if (!CanEditSelectedSlot())
            {
                _pressedSlotIndex = -1;
                UpdateButtons();
                return;
            }

            if (IsOfficialTransitionSlot(pressedIndex + 1))
            {
                _pressedSlotIndex = -1;
                RefuseDraftByOfficialTransition(pressedIndex);
                UpdateButtons();
                return;
            }

            _draggingSlotIndex = pressedIndex;
            BeginDraftSource();
        }

        if (_draggingSlotIndex >= 0 && Input.GetMouseButton(0))
        {
            var center = new VisualEditorPoint(
                pointer.X - _dragOffset.X,
                pointer.Y - _dragOffset.Y);
            var mapped = VisualEditorCanvasMath.ViewportToStory(
                center,
                _storyHeight,
                _viewportRect);
            if (mapped.Success)
            {
                Result<VisualEditorPoint> local = VisualEditorCanvasMath.StoryToSlotLocal(
                    _draggingSlotIndex + 1,
                    mapped.Value);
                if (local.Success)
                {
                    _draftPositions[_draggingSlotIndex] = local.Value;
                    UpdatePositionDraft(_draggingSlotIndex, local.Value);
                }
            }
        }

        if (_draggingSlotIndex >= 0 && Input.GetMouseButtonUp(0))
        {
            _draggingSlotIndex = -1;
            VerifyDraftAgainstSelected();
            UpdateInspector();
        }

        if (_pressedSlotIndex >= 0 && Input.GetMouseButtonUp(0))
        {
            _pressedSlotIndex = -1;
        }
    }

    private bool HandleCameraPress(VisualEditorPoint pointer)
    {
        if (!CanEditCamera())
        {
            return false;
        }

        for (int index = 0; index < _cameraZoomHandleHitRects.Length; index++)
        {
            if (!Contains(_cameraZoomHandleHitRects[index], pointer))
            {
                continue;
            }

            _cameraZoomDragging = true;
            _cameraZoomHandleIndex = index;
            BeginDraftSource();
            return true;
        }

        if (!Contains(_cameraFrameHitRect, pointer)
            || !Contains(_viewportRect, pointer))
        {
            return false;
        }

        _cameraPressed = true;
        _cameraPressPointer = pointer;
        _cameraDragOffset = new VisualEditorPoint(
            pointer.X - (_cameraFrameHitRect.X + (_cameraFrameHitRect.Width / 2f)),
            pointer.Y - (_cameraFrameHitRect.Y + (_cameraFrameHitRect.Height / 2f)));
        return true;
    }

    private void HandleCameraDrag(VisualEditorPoint pointer)
    {
        if (_cameraPressed
            && !_cameraDragging
            && Input.GetMouseButton(0)
            && VisualEditorCanvasMath.ExceedsDragThreshold(
                _cameraPressPointer,
                pointer,
                SlotDragThreshold))
        {
            _cameraDragging = true;
            BeginDraftSource();
        }

        if (_cameraDragging && Input.GetMouseButton(0))
        {
            var center = new VisualEditorPoint(
                pointer.X - _cameraDragOffset.X,
                pointer.Y - _cameraDragOffset.Y);
            Result<SceneCameraState> moved = VisualCameraFrameMath.MoveFrameCenter(
                center,
                CurrentCameraState(),
                _storyHeight,
                _viewportRect);
            if (moved.Success)
            {
                SetCameraDraft(moved.Value, verifyImmediately: false);
            }
        }

        if (_cameraZoomDragging && Input.GetMouseButton(0))
        {
            float centerX = _cameraFrameHitRect.X + (_cameraFrameHitRect.Width / 2f);
            float centerY = _cameraFrameHitRect.Y + (_cameraFrameHitRect.Height / 2f);
            float widthFromX = MathF.Abs(pointer.X - centerX) * 2f;
            float widthFromY = MathF.Abs(pointer.Y - centerY)
                * 2f
                * (_viewportRect.Width / _viewportRect.Height);
            float frameWidth = MathF.Max(8f, (widthFromX + widthFromY) / 2f);
            Result<float> zoom = VisualCameraFrameMath.ZoomFromFrameWidth(
                frameWidth,
                _viewportRect);
            if (zoom.Success)
            {
                SceneCameraState current = CurrentCameraState();
                SetCameraDraft(
                    current with { Zoom = MathF.Round(zoom.Value, 3) },
                    verifyImmediately: false);
            }
        }

        if ((_cameraDragging || _cameraZoomDragging) && Input.GetMouseButtonUp(0))
        {
            _cameraDragging = false;
            _cameraZoomDragging = false;
            _cameraZoomHandleIndex = -1;
            VerifyDraftAgainstSelected();
            UpdateInspector();
        }

        if (_cameraPressed && Input.GetMouseButtonUp(0))
        {
            _cameraPressed = false;
        }
    }

    private bool HandleScreenTextPress(VisualEditorPoint pointer)
    {
        if (!CanEditScreenText()
            || _screenTextRemoveDraft
            || !Contains(_screenTextHitRect, pointer))
        {
            return false;
        }

        ScreenTextDirective current = CurrentScreenText();
        Result<VisualEditorPoint> anchor = VisualEditorCanvasMath.StoryToViewport(
            new VisualEditorPoint(current.X, current.Y),
            _storyHeight,
            _viewportRect);
        if (!anchor.Success)
        {
            return false;
        }

        _screenTextPressed = true;
        _screenTextPressPointer = pointer;
        _screenTextDragOffset = new VisualEditorPoint(
            pointer.X - anchor.Value.X,
            pointer.Y - anchor.Value.Y);
        return true;
    }

    private void HandleScreenTextDrag(VisualEditorPoint pointer)
    {
        if (_screenTextPressed
            && !_screenTextDragging
            && Input.GetMouseButton(0)
            && VisualEditorCanvasMath.ExceedsDragThreshold(
                _screenTextPressPointer,
                pointer,
                SlotDragThreshold))
        {
            _screenTextDragging = true;
            BeginDraftSource();
        }

        if (_screenTextDragging && Input.GetMouseButton(0))
        {
            var anchor = new VisualEditorPoint(
                pointer.X - _screenTextDragOffset.X,
                pointer.Y - _screenTextDragOffset.Y);
            Result<VisualEditorPoint> story = VisualEditorCanvasMath.ViewportToStory(
                anchor,
                _storyHeight,
                _viewportRect);
            if (story.Success)
            {
                ScreenTextDirective current = CurrentScreenText();
                SetScreenTextDraft(current with
                {
                    X = MathF.Round(story.Value.X, 1),
                    Y = MathF.Round(story.Value.Y, 1)
                });
            }
        }

        if (_screenTextDragging && Input.GetMouseButtonUp(0))
        {
            _screenTextDragging = false;
            UpdateInspector();
        }

        if (_screenTextPressed && Input.GetMouseButtonUp(0))
        {
            _screenTextPressed = false;
        }
    }

    private ScreenTextDirective CurrentScreenText() =>
        ScreenTextSequenceVisualMath.ResolveTemplate(
            _screenTextDraft ?? _selectedExistingScreenText,
            _rememberedScreenTextSettings);

    private bool CanEditScreenText() =>
        _mode == VisualEditorMode.ScreenText
        && HasSynchronizedDocument()
        && _documentSnapshot!.ScreenTextLines.Count <= 1;

    private bool CanAddClearScreenText() =>
        _mode == VisualEditorMode.ScreenText
        && HasSynchronizedDocument()
        && !_documentSnapshot!.HasClearScreenTextDirective;

    private bool CanAlignScreenTextToPrevious() =>
        CanEditScreenText()
        && !_screenTextRemoveDraft
        && !_screenTextClearAllDraft
        && TryGetPreviousVisibleScreenText(out _);

    private void NudgeScreenTextPosition(float deltaX, float deltaY)
    {
        if (!CanEditScreenText())
        {
            return;
        }

        BeginDraftSource();
        ScreenTextDirective current = CurrentScreenText();
        SetScreenTextDraft(current with
        {
            X = Math.Clamp(
                current.X + deltaX,
                -ScreenTextDirectiveCodec.MaximumCoordinateMagnitude,
                ScreenTextDirectiveCodec.MaximumCoordinateMagnitude),
            Y = Math.Clamp(
                current.Y + deltaY,
                -ScreenTextDirectiveCodec.MaximumCoordinateMagnitude,
                ScreenTextDirectiveCodec.MaximumCoordinateMagnitude)
        });
    }

    private void NudgeScreenTextFont(int delta)
    {
        if (!CanEditScreenText())
        {
            return;
        }

        BeginDraftSource();
        ScreenTextDirective current = CurrentScreenText();
        SetScreenTextDraft(current with
        {
            FontSize = Math.Clamp(
                current.FontSize + delta,
                ScreenTextDirectiveCodec.MinimumFontSize,
                ScreenTextDirectiveCodec.MaximumFontSize)
        });
    }

    private void ToggleScreenTextAlignment()
    {
        if (!CanEditScreenText())
        {
            return;
        }

        BeginDraftSource();
        ScreenTextDirective current = CurrentScreenText();
        SetScreenTextDraft(current with
        {
            Alignment = current.Alignment == ScreenTextAlignment.Left
                ? ScreenTextAlignment.Center
                : ScreenTextAlignment.Left
        });
    }

    private void CycleScreenTextRevealMode()
    {
        if (!CanEditScreenText())
        {
            return;
        }

        BeginDraftSource();
        ScreenTextDirective current = CurrentScreenText();
        SetScreenTextDraft(current with
        {
            RevealMode = current.RevealMode switch
            {
                ScreenTextRevealMode.Instant => ScreenTextRevealMode.Smooth,
                ScreenTextRevealMode.Smooth => ScreenTextRevealMode.Serial,
                _ => ScreenTextRevealMode.Instant
            }
        });
    }

    private void ResetScreenTextPosition()
    {
        if (!CanEditScreenText())
        {
            return;
        }

        BeginDraftSource();
        SetScreenTextDraft(CurrentScreenText() with { X = 0f, Y = 0f });
    }

    private void AlignScreenTextToPreviousX()
    {
        if (!CanEditScreenText()
            || _screenTextRemoveDraft
            || _screenTextClearAllDraft
            || !TryGetPreviousVisibleScreenText(out VisibleScreenTextSnapshot? previous)
            || previous == null)
        {
            return;
        }

        BeginDraftSource();
        SetScreenTextDraft(CurrentScreenText() with { X = previous.Directive.X });
        _operationMessage = $"已与上一段文字的 X {previous.Directive.X:0.###} 对齐";
    }

    private bool TryGetPreviousVisibleScreenText(
        out VisibleScreenTextSnapshot? previous)
    {
        int currentVisibleIndex = FindCurrentVisibleScreenTextIndex();
        int previousIndex = ScreenTextSequenceVisualMath.PreviousVisibleIndex(
            _visibleScreenTexts.Count,
            currentVisibleIndex);
        if (previousIndex < 0 || previousIndex >= _visibleScreenTexts.Count)
        {
            previous = null;
            return false;
        }

        previous = _visibleScreenTexts[previousIndex];
        return true;
    }

    private int FindCurrentVisibleScreenTextIndex()
    {
        ScreenTextDirective? selected = _selectedExistingScreenText;
        if (selected == null)
        {
            return -1;
        }

        for (int index = _visibleScreenTexts.Count - 1; index >= 0; index--)
        {
            if (ScreenTextMatches(
                    _visibleScreenTexts[index],
                    selected,
                    _screenTextContent))
            {
                return index;
            }
        }

        return -1;
    }

    private void SetScreenTextDraft(ScreenTextDirective directive)
    {
        _screenTextDraft = directive;
        _screenTextRemoveDraft = false;
        _screenTextClearAllDraft = false;
        PublishScreenTextDraft();
        LayoutScreenTextPreview();
        UpdateInspector();
    }

    private void PublishScreenTextDraft()
    {
        ScreenTextDirective? directive = _screenTextDraft;
        EditorCommandDocumentSnapshot? source = _draftSourceDocument;
        Text? draftText = _draftText;
        _verifiedPreview = null;
        _currentDraftDirective = directive?.ToOfficialDirective() ?? string.Empty;
        if (draftText == null || directive == null)
        {
            UpdateButtons();
            return;
        }

        if (source == null)
        {
            draftText.text = "来源不可用\n屏幕文字草稿（未保存）\n"
                + _currentDraftDirective;
            UpdateButtons();
            return;
        }

        Result<EditorCommandScreenTextEdit> preview = _documentComposer.SetScreenText(
            source.AdditionalPrompt,
            source.RevisionSha256,
            directive);
        draftText.text = preview.Success
            ? $"{SourceSummary(source)}\n屏幕文字草稿（未保存）\n"
                + _currentDraftDirective
                + "\n合并预览通过"
            : $"{SourceSummary(source)}\n屏幕文字草稿错误\n{preview.Error}";
        UpdateButtons();
    }

    private void CreateScreenTextRemoveDraft()
    {
        if (!CanEditScreenText()
            || (_selectedExistingScreenText == null && _screenTextDraft == null))
        {
            return;
        }

        BeginDraftSource();
        _screenTextDraft = null;
        _screenTextRemoveDraft = true;
        _screenTextClearAllDraft = false;
        _currentDraftDirective = string.Empty;
        _verifiedPreview = null;
        if (_draftText is not null && _draftSourceDocument != null)
        {
            Result<EditorCommandScreenTextEdit> preview = _documentComposer.SetScreenText(
                _draftSourceDocument.AdditionalPrompt,
                _draftSourceDocument.RevisionSha256,
                directive: null);
            _draftText.text = preview.Success
                ? $"{SourceSummary(_draftSourceDocument)}\n屏幕文字移除草稿（未保存）\n"
                    + "将移除本场 #st/#stm；其他指令保持不变"
                : $"{SourceSummary(_draftSourceDocument)}\n屏幕文字草稿错误\n"
                    + preview.Error;
        }

        UpdateModeVisibility();
        UpdateInspector();
        UpdateButtons();
    }

    private void CreateScreenTextClearAllDraft()
    {
        if (!CanAddClearScreenText())
        {
            return;
        }

        BeginDraftSource();
        _screenTextDraft = null;
        _screenTextRemoveDraft = false;
        _screenTextClearAllDraft = true;
        _currentDraftDirective = "#clearST";
        _verifiedPreview = null;
        if (_draftText is not null && _draftSourceDocument != null)
        {
            Result<EditorCommandClearScreenTextEdit> preview =
                _documentComposer.AddClearScreenText(
                    _draftSourceDocument.AdditionalPrompt,
                    _draftSourceDocument.RevisionSha256);
            _draftText.text = preview.Success
                ? $"{SourceSummary(_draftSourceDocument)}\n清除屏幕文字草稿（未保存）\n"
                    + "#clearST\n将清除播放器当前保留的全部屏幕文字"
                : $"{SourceSummary(_draftSourceDocument)}\n屏幕文字草稿错误\n"
                    + preview.Error;
        }

        UpdateModeVisibility();
        LayoutScreenTextPreview();
        UpdateInspector();
        UpdateButtons();
    }

    private void ClearScreenTextDraft() =>
        ClearScreenTextDraft("屏幕文字草稿已清除");

    private void ClearScreenTextDraft(string message)
    {
        _screenTextDraft = null;
        _screenTextRemoveDraft = false;
        _screenTextClearAllDraft = false;
        _currentDraftDirective = string.Empty;
        _draftSourceDocument = null;
        _verifiedPreview = null;
        _operationMessage = message;
        LoadExistingScreenText(preserveActiveDraft: false);
        UpdateModeVisibility();
        LayoutScreenTextPreview();
        UpdateInspector();
        UpdateButtons();
    }

    private void UpdatePositionDraft(int slotIndex, VisualEditorPoint position)
    {
        VisualCharacterDraftRequest request = EnsureSetDraft(slotIndex) with
        {
            X = position.X,
            Y = position.Y
        };
        _activeDraft = request;
        PublishActiveDraft();
        UpdateInspector();
    }

    private void PublishActiveDraft()
    {
        VisualCharacterDraftRequest? request = _activeDraft;
        Result<string> result = request == null
            ? Result<string>.Fail("No character slot draft is active.")
            : _draftBuilder.Build(request);
        Text? draftText = _draftText;
        if (draftText is not null)
        {
            if (!result.Success || result.Value == null)
            {
                _currentDraftDirective = string.Empty;
                draftText.text = $"草稿错误\n{result.Error}";
                return;
            }

            _currentDraftDirective = result.Value;
            _verifiedPreview = null;
            EditorCommandDocumentSnapshot? source = _draftSourceDocument;
            if (source == null)
            {
                draftText.text = "来源不可用\n"
                    + $"{ShortDiagnostic(_documentReadError)}\n"
                    + $"草稿（未保存）\n{result.Value}";
                return;
            }

            var preview = _documentComposer.PreviewUpsert(
                source.AdditionalPrompt,
                source.RevisionSha256,
                result.Value);
            draftText.text = preview.Success && preview.Value != null
                ? $"{SourceSummary(source)}\n草稿（未保存）\n{preview.Value.CanonicalPublicDirective}\n合并预览通过"
                : $"{SourceSummary(source)}\n草稿错误\n{preview.Error}";
        }

        UpdateButtons();
    }

    private void SelectSlot(int slotIndex)
    {
        if (!HasSynchronizedDocument() || slotIndex < 0 || slotIndex >= _slotSnapshots.Length)
        {
            return;
        }

        if (_selectedSlotIndex != slotIndex && _activeDraft != null)
        {
            int oldIndex = _activeDraft.PublicSlot - 1;
            if (oldIndex >= 0 && oldIndex < _draftPositions.Length)
            {
                _draftPositions[oldIndex] = null;
                _draftRotations[oldIndex] = null;
                _draftFlips[oldIndex] = null;
            }

            _activeDraft = null;
            _currentDraftDirective = string.Empty;
            _draftSourceDocument = null;
            _verifiedPreview = null;
        }

        _selectedSlotIndex = slotIndex;
        _slotPreference.Remember(_documentSnapshot!.RuntimeContextId, slotIndex + 1);
        _operationMessage = string.Empty;
        LoadExistingCommandForSelection(preserveActiveDraft: false);
        UpdateInspector();
    }

    private void LoadExistingCommandForSelection(bool preserveActiveDraft)
    {
        if (preserveActiveDraft && _activeDraft != null)
        {
            return;
        }

        _selectedExistingDraft = null;
        _selectedCommandGraphicallyUnsupported = false;
        if (!HasSelectedSlot() || _documentSnapshot == null)
        {
            return;
        }

        int publicSlot = _selectedSlotIndex + 1;
        bool occupied = _slotSnapshots[_selectedSlotIndex]?.Occupied == true;
        foreach (string canonical in _documentSnapshot.CanonicalDirectives)
        {
            Result<VisualCharacterDraftRequest> read = _draftBuilder.ReadCanonical(canonical);
            VisualCharacterDraftRequest? candidate = read.Success ? read.Value : null;
            if (candidate == null
                || candidate.PublicSlot != publicSlot
                || candidate.Occupied != occupied)
            {
                continue;
            }

            _selectedExistingDraft = candidate;
            _selectedCommandGraphicallyUnsupported =
                candidate.Operation == VisualCharacterDraftOperation.Move;
            if (candidate.Occupied)
            {
                _draftDurationMilliseconds = candidate.DurationMilliseconds;
                _draftEasing = candidate.Easing;
            }

            break;
        }
    }

    private void BeginDraftSource()
    {
        if ((_activeDraft == null
                && !_cameraDraftState.HasValue
                && !_cameraResetDraft
                && _screenTextDraft == null
                && !_screenTextRemoveDraft
                && !_screenTextClearAllDraft
               )
            || _draftSourceDocument == null)
        {
            _draftSourceDocument = _documentSnapshot;
        }

        _currentDraftDirective = string.Empty;
        _operationMessage = string.Empty;
        _verifiedPreview = null;
    }

    private void SetEditorMode(VisualEditorMode mode)
    {
        if (_mode == mode)
        {
            return;
        }

        if (_mode == VisualEditorMode.Character && _activeDraft != null)
        {
            ClearActiveDraft("切换模式，未应用的立绘草稿已清除");
        }
        else if (_mode == VisualEditorMode.Camera
            && (_cameraDraftState.HasValue || _cameraResetDraft))
        {
            ClearCameraDraft("切换模式，未应用的镜头草稿已清除");
        }
        else if (_mode == VisualEditorMode.ScreenText
            && (_screenTextDraft != null
                || _screenTextRemoveDraft
                || _screenTextClearAllDraft))
        {
            ClearScreenTextDraft("切换模式，未应用的屏幕文字草稿已清除");
        }

        _mode = mode;
        _pressedSlotIndex = -1;
        _draggingSlotIndex = -1;
        _rotationDragging = false;
        _cameraPressed = false;
        _cameraDragging = false;
        _cameraZoomDragging = false;
        _cameraZoomHandleIndex = -1;
        _screenTextPressed = false;
        _screenTextDragging = false;
        if (_mode == VisualEditorMode.Camera)
        {
            RefreshCameraState();
            LoadExistingCameraCommand(preserveActiveDraft: true);
        }
        else if (_mode == VisualEditorMode.ScreenText)
        {
            LoadExistingScreenText(preserveActiveDraft: true);
            RefreshVisibleScreenTexts();
        }

        UpdateModeVisibility();
        Layout();
        UpdateInspector();
        UpdateButtons();
    }

    private void UpdateModeVisibility()
    {
        bool character = _mode == VisualEditorMode.Character;
        bool camera = _mode == VisualEditorMode.Camera;
        bool screenText = _mode == VisualEditorMode.ScreenText;

        foreach (string key in CharacterControlKeys)
        {
            if (_controlButtons.TryGetValue(key, out ControlButton? button))
            {
                button.SetVisible(character);
            }
        }

        foreach (string key in CameraControlKeys)
        {
            if (_controlButtons.TryGetValue(key, out ControlButton? button))
            {
                button.SetVisible(camera);
            }
        }

        foreach (string key in ScreenTextControlKeys)
        {
            if (_controlButtons.TryGetValue(key, out ControlButton? button))
            {
                button.SetVisible(screenText);
            }
        }

        if (_rotationDialRect is not null)
        {
            _rotationDialRect.gameObject.SetActive(character);
        }

        foreach (SlotSelectorButton? selector in _slotSelectorButtons)
        {
            selector?.Rect.gameObject.SetActive(character);
        }

        foreach (SlotVisual? visual in _slotVisuals)
        {
            visual?.Root.gameObject.SetActive(character);
        }

        _zeroYGuide?.gameObject.SetActive(character);
        _zeroYLabel?.gameObject.SetActive(character);
        foreach (RectTransform? line in _slotGuideLines)
        {
            line?.gameObject.SetActive(character);
        }

        foreach (Text? label in _slotGuideLabels)
        {
            label?.gameObject.SetActive(character);
        }

        foreach (RectTransform? line in _cameraCanvasLines)
        {
            line?.gameObject.SetActive(camera);
        }

        _cameraFrameRoot?.gameObject.SetActive(camera);
        bool editableScreenTextVisible = screenText
            && !_screenTextRemoveDraft
            && !_screenTextClearAllDraft;
        _screenTextPreviewRect?.gameObject.SetActive(editableScreenTextVisible);
        _screenTextAnchorRect?.gameObject.SetActive(editableScreenTextVisible);
        if (!screenText)
        {
            foreach (RectTransform? root in _visibleScreenTextRects)
            {
                root?.gameObject.SetActive(false);
            }

            foreach (RectTransform? anchor in _visibleScreenTextAnchorRects)
            {
                anchor?.gameObject.SetActive(false);
            }
        }
        else
        {
            LayoutVisibleScreenTexts();
        }
    }

    private void RefreshCameraState()
    {
        ApiResult<SceneCameraReadSnapshot> read = Plugin.Api.SceneCamera.ReadOnMainThread();
        if (!read.Success || read.Value == null)
        {
            string error = string.IsNullOrWhiteSpace(read.Error)
                ? "镜头层暂不可用"
                : read.Error;
            if (!string.Equals(error, _cameraReadError, StringComparison.Ordinal))
            {
                _cameraReadError = error;
                Plugin.Logger.LogWarning("Visual camera read unavailable: " + error);
            }

            _cameraReadAvailable = false;
            return;
        }

        if (!_cameraReadAvailable && !string.IsNullOrEmpty(_cameraReadError))
        {
            Plugin.Logger.LogInfo("Visual camera read recovered.");
        }

        _cameraReadAvailable = true;
        _cameraReadError = string.Empty;
        if (!_cameraDraftState.HasValue && !_cameraResetDraft)
        {
            _cameraState = read.Value.State;
        }
    }

    private void LoadExistingCameraCommand(bool preserveActiveDraft)
    {
        if (preserveActiveDraft && (_cameraDraftState.HasValue || _cameraResetDraft))
        {
            return;
        }

        _selectedExistingCameraCommand = null;
        if (_documentSnapshot == null)
        {
            return;
        }

        foreach (string canonical in _documentSnapshot.CanonicalDirectives)
        {
            if (!canonical.StartsWith(
                    SceneCameraCommandFamilyCompiler.CanonicalRootToken + ";",
                    StringComparison.Ordinal))
            {
                continue;
            }

            Result<SceneCameraCommand> read = _cameraDraftBuilder.ReadCanonical(canonical);
            if (!read.Success || read.Value == null)
            {
                continue;
            }

            _selectedExistingCameraCommand = read.Value;
            _cameraDraftDurationMilliseconds = read.Value.DurationMilliseconds;
            _cameraDraftEasing = read.Value.Easing;
            break;
        }
    }

    private void LoadExistingScreenText(bool preserveActiveDraft)
    {
        if (preserveActiveDraft
            && (_screenTextDraft != null
                || _screenTextRemoveDraft
                || _screenTextClearAllDraft))
        {
            return;
        }

        _selectedExistingScreenText = null;
        EditorCommandDocumentSnapshot? document = _documentSnapshot;
        if (document == null)
        {
            _screenTextContent = string.Empty;
            return;
        }

        _screenTextContent = document.DialogueText ?? string.Empty;
        if (document.ScreenTextLines.Count == 1)
        {
            _selectedExistingScreenText = document.ScreenTextLines[0].Directive;
            _rememberedScreenTextSettings = _selectedExistingScreenText;
        }
    }

    private void RefreshVisibleScreenTexts()
    {
        ApiResult<IReadOnlyList<VisibleScreenTextSnapshot>> result =
            Plugin.Api.ScreenTexts.ReadVisibleOnMainThread();
        if (!result.Success || result.Value == null)
        {
            string error = string.IsNullOrWhiteSpace(result.Error)
                ? "当前屏幕文字列表不可用"
                : result.Error;
            if (!string.Equals(
                    error,
                    _visibleScreenTextReadError,
                    StringComparison.Ordinal))
            {
                _visibleScreenTextReadError = error;
                Plugin.Logger.LogWarning(
                    "Visual screen-text read unavailable: " + error);
            }

            _visibleScreenTexts = Array.Empty<VisibleScreenTextSnapshot>();
            LayoutVisibleScreenTexts();
            return;
        }

        if (!string.IsNullOrEmpty(_visibleScreenTextReadError))
        {
            Plugin.Logger.LogInfo("Visual screen-text read recovered.");
            _visibleScreenTextReadError = string.Empty;
        }

        _visibleScreenTexts = result.Value;
        if (_rememberedScreenTextSettings == null
            && _visibleScreenTexts.Count > 0)
        {
            _rememberedScreenTextSettings =
                _visibleScreenTexts[_visibleScreenTexts.Count - 1].Directive;
        }

        LayoutVisibleScreenTexts();
        LayoutScreenTextPreview();
    }

    private SceneCameraState CurrentCameraState() =>
        _cameraResetDraft
            ? SceneCameraState.Default
            : _cameraDraftState ?? _cameraState;

    private bool CanEditCamera() =>
        _mode == VisualEditorMode.Camera
        && HasSynchronizedDocument()
        && _cameraReadAvailable;

    private void NudgeCameraPosition(float deltaX, float deltaY)
    {
        if (!CanEditCamera())
        {
            return;
        }

        SceneCameraState current = CurrentCameraState();
        SetCameraDraft(new SceneCameraState(
            MathF.Round(current.X + deltaX, 2),
            MathF.Round(current.Y + deltaY, 2),
            current.Zoom), verifyImmediately: true);
    }

    private void NudgeCameraZoom(float deltaZoom)
    {
        if (!CanEditCamera())
        {
            return;
        }

        SceneCameraState current = CurrentCameraState();
        float zoom = Math.Clamp(
            MathF.Round(current.Zoom + deltaZoom, 3),
            SceneCameraCommandValidator.MinimumZoom,
            SceneCameraCommandValidator.MaximumZoom);
        SetCameraDraft(current with { Zoom = zoom }, verifyImmediately: true);
    }

    private void SetCameraDraft(SceneCameraState state, bool verifyImmediately)
    {
        Result validation = SceneCameraCommandValidator.ValidateTarget(state);
        if (!CanEditCamera() || !validation.Success)
        {
            return;
        }

        bool starting = !_cameraDraftState.HasValue && !_cameraResetDraft;
        if (starting)
        {
            BeginDraftSource();
        }

        _cameraDraftState = state;
        _cameraResetDraft = false;
        if (starting
            && _selectedExistingCameraCommand?.Operation == SceneCameraOperation.Move)
        {
            _operationMessage = "相对镜头已按当前画面转换为绝对构图草稿";
        }

        PublishCameraDraft();
        LayoutCameraFrame();
        UpdateInspector();
        if (verifyImmediately)
        {
            VerifyDraftAgainstSelected();
        }
    }

    private void PublishCameraDraft()
    {
        Result<string> result = _cameraResetDraft
            ? _cameraDraftBuilder.BuildReset(
                _cameraDraftDurationMilliseconds,
                _cameraDraftEasing)
            : _cameraDraftState.HasValue
                ? _cameraDraftBuilder.BuildSet(
                    _cameraDraftState.Value,
                    _cameraDraftDurationMilliseconds,
                    _cameraDraftEasing)
                : Result<string>.Fail("No scene camera draft is active.");
        PublishDraftResult(result);
    }

    private void PublishDraftResult(Result<string> result)
    {
        Text? draftText = _draftText;
        if (draftText == null)
        {
            return;
        }

        if (!result.Success || result.Value == null)
        {
            _currentDraftDirective = string.Empty;
            draftText.text = $"草稿错误\n{result.Error}";
            UpdateButtons();
            return;
        }

        _currentDraftDirective = result.Value;
        _verifiedPreview = null;
        EditorCommandDocumentSnapshot? source = _draftSourceDocument;
        if (source == null)
        {
            draftText.text = "来源不可用\n"
                + $"{ShortDiagnostic(_documentReadError)}\n"
                + $"草稿（未保存）\n{result.Value}";
            UpdateButtons();
            return;
        }

        Result<EditorCommandEditPreview> preview = _documentComposer.PreviewUpsert(
            source.AdditionalPrompt,
            source.RevisionSha256,
            result.Value);
        draftText.text = preview.Success && preview.Value != null
            ? $"{SourceSummary(source)}\n草稿（未保存）\n{preview.Value.CanonicalPublicDirective}\n合并预览通过"
            : $"{SourceSummary(source)}\n草稿错误\n{preview.Error}";
        UpdateButtons();
    }

    private void CycleCameraDuration()
    {
        if (!CanEditCamera())
        {
            return;
        }

        int[] values = { 0, 300, 600, 1000, 2000, 2500 };
        int next = Array.FindIndex(values, value => value > _cameraDraftDurationMilliseconds);
        _cameraDraftDurationMilliseconds = next >= 0 ? values[next] : values[0];
        RepublishCameraTimingChange();
    }

    private void CycleCameraEasing()
    {
        if (!CanEditCamera())
        {
            return;
        }

        _cameraDraftEasing = _cameraDraftEasing switch
        {
            CharacterTransformEasing.Linear => CharacterTransformEasing.EaseIn,
            CharacterTransformEasing.EaseIn => CharacterTransformEasing.EaseOut,
            CharacterTransformEasing.EaseOut => CharacterTransformEasing.EaseInOut,
            _ => CharacterTransformEasing.Linear
        };
        RepublishCameraTimingChange();
    }

    private void RepublishCameraTimingChange()
    {
        if (_cameraResetDraft)
        {
            PublishCameraDraft();
            VerifyDraftAgainstSelected();
            return;
        }

        if (_cameraDraftState.HasValue || _selectedExistingCameraCommand != null)
        {
            SetCameraDraft(CurrentCameraState(), verifyImmediately: true);
        }
    }

    private void CreateCameraResetDraft()
    {
        if (!CanEditCamera())
        {
            return;
        }

        BeginDraftSource();
        _cameraDraftState = null;
        _cameraResetDraft = true;
        PublishCameraDraft();
        LayoutCameraFrame();
        VerifyDraftAgainstSelected();
        UpdateInspector();
    }

    private void ClearCameraDraft() => ClearCameraDraft("镜头草稿已清除");

    private void ClearCameraDraft(string message)
    {
        _cameraDraftState = null;
        _cameraResetDraft = false;
        _currentDraftDirective = string.Empty;
        _draftSourceDocument = null;
        _verifiedPreview = null;
        _operationMessage = message;
        LoadExistingCameraCommand(preserveActiveDraft: false);
        if (_draftText is not null)
        {
            _draftText.text = _documentSnapshot == null
                ? message
                : $"{SourceSummary(_documentSnapshot)}\n{message}";
        }

        LayoutCameraFrame();
        UpdateInspector();
        UpdateButtons();
    }

    private VisualCharacterDraftRequest EnsureSetDraft(int slotIndex)
    {
        CharacterSlotSnapshot? snapshot = _slotSnapshots[slotIndex];
        bool occupied = snapshot?.Occupied == true;
        if (_activeDraft != null
            && _activeDraft.PublicSlot == slotIndex + 1
            && _activeDraft.Occupied == occupied
            && _activeDraft.Operation == VisualCharacterDraftOperation.Set)
        {
            return _activeDraft;
        }

        BeginDraftSource();
        if (_selectedExistingDraft != null
            && _selectedExistingDraft.PublicSlot == slotIndex + 1
            && _selectedExistingDraft.Occupied == occupied
            && _selectedExistingDraft.Operation == VisualCharacterDraftOperation.Set)
        {
            return _selectedExistingDraft with
            {
                DurationMilliseconds = occupied ? _draftDurationMilliseconds : 0,
                Easing = occupied ? _draftEasing : CharacterTransformEasing.Linear
            };
        }

        return new VisualCharacterDraftRequest(
            slotIndex + 1,
            occupied,
            VisualCharacterDraftOperation.Set,
            null,
            null,
            null,
            null,
            _draftDurationMilliseconds,
            _draftEasing);
    }

    private void NudgePosition(float deltaX, float deltaY)
    {
        if (!CanEditSelectedSlot())
        {
            return;
        }

        int index = _selectedSlotIndex;
        if (IsOfficialTransitionSlot(index + 1))
        {
            RefuseDraftByOfficialTransition(index);
            return;
        }

        BeginDraftSource();
        VisualEditorPoint position = SelectedPosition(index);
        var target = new VisualEditorPoint(
            position.X + deltaX,
            position.Y + deltaY);
        VisualCharacterDraftRequest basis = EnsureSetDraft(index);
        VisualCharacterDraftRequest request = basis with
        {
            X = deltaX == 0f ? basis.X : target.X,
            Y = deltaY == 0f ? basis.Y : target.Y
        };
        _activeDraft = request;
        _draftPositions[index] = target;
        PublishActiveDraft();
        VerifyDraftAgainstSelected();
    }

    private void NudgeRotation(float deltaDegrees)
    {
        if (!CanEditSelectedSlot())
        {
            return;
        }

        SetRotationDraft(SelectedRotation() + deltaDegrees, verifyImmediately: true);
    }

    private void UpdateRotationFromPointer(VisualEditorPoint pointer)
    {
        float centerX = _rotationDialHitRect.X + (_rotationDialHitRect.Width / 2f);
        float centerY = _rotationDialHitRect.Y + (_rotationDialHitRect.Height / 2f);
        float angle = MathF.Atan2(pointer.Y - centerY, pointer.X - centerX)
            * (180f / MathF.PI)
            - 90f;
        SetRotationDraft(MathF.Round(NormalizeSigned(angle)), verifyImmediately: false);
    }

    private void SetRotationDraft(float degrees, bool verifyImmediately)
    {
        if (!CanEditSelectedSlot())
        {
            return;
        }

        int index = _selectedSlotIndex;
        BeginDraftSource();
        // Deliberate multi-turn spins are authored raw (|value| > 360 skips
        // the shortest-path wrap inside the planner); only the dial pointer
        // keeps its wrap-around ±180 semantics.
        float rounded = MathF.Round(degrees, 1);
        _activeDraft = EnsureSetDraft(index) with
        {
            RotationDegrees = rounded
        };
        _draftRotations[index] = rounded;
        PublishActiveDraft();
        UpdateInspector();
        if (verifyImmediately)
        {
            VerifyDraftAgainstSelected();
        }
    }

    private void ToggleFlip()
    {
        if (!CanEditSelectedSlot())
        {
            return;
        }

        int index = _selectedSlotIndex;
        BeginDraftSource();
        bool target = !SelectedFlip();
        _activeDraft = EnsureSetDraft(index) with
        {
            FlipX = target
        };
        _draftFlips[index] = target;
        PublishActiveDraft();
        VerifyDraftAgainstSelected();
    }

    private void ResetFlip()
    {
        if (!CanEditSelectedSlot())
        {
            return;
        }

        int index = _selectedSlotIndex;
        BeginDraftSource();
        _activeDraft = EnsureSetDraft(index) with
        {
            FlipX = false
        };
        _draftFlips[index] = false;
        PublishActiveDraft();
        VerifyDraftAgainstSelected();
    }

    private void CycleDuration()
    {
        if (!SelectedSlotIsOccupied())
        {
            return;
        }

        int[] values = { 0, 300, 600, 1000, 2000, 2500 };
        int next = Array.FindIndex(values, value => value > _draftDurationMilliseconds);
        _draftDurationMilliseconds = next >= 0 ? values[next] : values[0];
        VisualCharacterDraftRequest? template = _activeDraft ?? _selectedExistingDraft;
        if (template != null)
        {
            BeginDraftSource();
            _activeDraft = template with
            {
                DurationMilliseconds = _draftDurationMilliseconds
            };
            PublishActiveDraft();
            VerifyDraftAgainstSelected();
        }
    }

    private void CycleEasing()
    {
        if (!SelectedSlotIsOccupied())
        {
            return;
        }

        _draftEasing = _draftEasing switch
        {
            CharacterTransformEasing.Linear => CharacterTransformEasing.EaseIn,
            CharacterTransformEasing.EaseIn => CharacterTransformEasing.EaseOut,
            CharacterTransformEasing.EaseOut => CharacterTransformEasing.EaseInOut,
            _ => CharacterTransformEasing.Linear
        };
        VisualCharacterDraftRequest? template = _activeDraft ?? _selectedExistingDraft;
        if (template != null)
        {
            BeginDraftSource();
            _activeDraft = template with { Easing = _draftEasing };
            PublishActiveDraft();
            VerifyDraftAgainstSelected();
        }
    }

    private void CreateResetDraft()
    {
        if (!SelectedSlotIsOccupied())
        {
            return;
        }

        int index = _selectedSlotIndex;
        if (IsOfficialTransitionSlot(index + 1))
        {
            RefuseDraftByOfficialTransition(index);
            return;
        }

        BeginDraftSource();
        _activeDraft = new VisualCharacterDraftRequest(
            index + 1,
            Occupied: true,
            VisualCharacterDraftOperation.Reset,
            X: null,
            Y: null,
            RotationDegrees: null,
            FlipX: null,
            _draftDurationMilliseconds,
            _draftEasing);
        _draftPositions[index] = null;
        _draftRotations[index] = null;
        _draftFlips[index] = null;
        PublishActiveDraft();
        VerifyDraftAgainstSelected();
    }

    private void ClearActiveDraft() => ClearActiveDraft("草稿已清除");

    private void ClearActiveDraft(string message)
    {
        int index = _activeDraft?.PublicSlot - 1 ?? _selectedSlotIndex;
        if (index >= 0 && index < _draftPositions.Length)
        {
            _draftPositions[index] = null;
            _draftRotations[index] = null;
            _draftFlips[index] = null;
        }

        _activeDraft = null;
        _currentDraftDirective = string.Empty;
        _draftSourceDocument = null;
        _verifiedPreview = null;
        _operationMessage = message;
        if (_draftText is not null)
        {
            _draftText.text = _documentSnapshot == null
                ? message
                : $"{SourceSummary(_documentSnapshot)}\n{message}";
        }

        UpdateInspector();
        UpdateButtons();
    }

    private bool HasSelectedSlot() =>
        _selectedSlotIndex >= 0 && _selectedSlotIndex < _slotSnapshots.Length;

    private bool CanEditSelectedSlot() =>
        HasSynchronizedDocument() && HasSelectedSlot() && !_selectedCommandGraphicallyUnsupported;

    private bool HasSynchronizedDocument() =>
        _documentSnapshot is { InputAvailable: true, InputMatchesScript: true }
        && !string.IsNullOrEmpty(_documentSnapshot.RuntimeSelectionKey);

    private bool IsOfficialTransitionSlot(int publicSlot)
    {
        foreach (int slot in _officialTransitionSlots)
        {
            if (slot == publicSlot)
            {
                return true;
            }
        }

        return false;
    }

    private static bool DraftWritesPosition(VisualCharacterDraftRequest request) =>
        request.Operation == VisualCharacterDraftOperation.Reset
        || (request.Operation == VisualCharacterDraftOperation.Set
            && (request.X.HasValue || request.Y.HasValue));

    private void RefuseDraftByOfficialTransition(int slotIndex)
    {
        _operationMessage =
            $"槽位 #{slotIndex + 1} 在该场景存在官方位移过渡："
            + "拖拽、X/Y 与整体重置会被播放索引拒绝；旋转与翻转指令不受影响";
        if (_draftText is not null && string.IsNullOrEmpty(_currentDraftDirective))
        {
            _draftText.text = _documentSnapshot == null
                ? _operationMessage
                : $"{SourceSummary(_documentSnapshot)}\n{_operationMessage}";
        }

        Plugin.Logger.LogInfo(
            "Visual editor refused a position draft for slot #"
            + (slotIndex + 1)
            + " because the scene has an official position transition.");
        EditorNotifications.TryToast(
            $"槽位 #{slotIndex + 1} 有官方位移过渡，位置类指令被拒绝",
            "official-transition-" + (slotIndex + 1));
    }

    private bool SelectedSlotIsOccupied() =>
        CanEditSelectedSlot() && SelectedSlotIsOccupiedRaw();

    private bool SelectedSlotIsOccupiedRaw() =>
        HasSelectedSlot() && _slotSnapshots[_selectedSlotIndex]?.Occupied == true;

    private VisualEditorPoint SelectedPosition(int index)
    {
        if (_draftPositions[index].HasValue)
        {
            return _draftPositions[index]!.Value;
        }

        CharacterTransformState? state = _slotSnapshots[index]?.State;
        VisualEditorPoint baseline = state == null
            ? DefaultPendingPosition()
            : new VisualEditorPoint(state.Position.X, state.Position.Y);
        if (_selectedSlotIndex != index)
        {
            return baseline;
        }

        VisualCharacterDraftRequest? existing =
            _selectedExistingDraft?.Operation == VisualCharacterDraftOperation.Set
                ? _selectedExistingDraft
                : null;
        VisualCharacterDraftRequest? active =
            _activeDraft?.Operation == VisualCharacterDraftOperation.Set
                ? _activeDraft
                : null;
        return new VisualEditorPoint(
            active?.X ?? existing?.X ?? baseline.X,
            active?.Y ?? existing?.Y ?? baseline.Y);
    }

    private float SelectedRotation()
    {
        if (!HasSelectedSlot())
        {
            return 0f;
        }

        if (_activeDraft?.PublicSlot == _selectedSlotIndex + 1
            && _activeDraft.RotationDegrees.HasValue)
        {
            return _activeDraft.RotationDegrees.Value;
        }

        if (_draftRotations[_selectedSlotIndex].HasValue)
        {
            return _draftRotations[_selectedSlotIndex]!.Value;
        }

        if (_selectedExistingDraft?.Operation == VisualCharacterDraftOperation.Set
            && _selectedExistingDraft.RotationDegrees.HasValue)
        {
            return _selectedExistingDraft.RotationDegrees.Value;
        }

        return NormalizeSigned(
            _slotSnapshots[_selectedSlotIndex]?.State?.LocalEulerAngles.Z ?? 0f);
    }

    private bool SelectedFlip()
    {
        if (!HasSelectedSlot())
        {
            return false;
        }

        if (_activeDraft?.PublicSlot == _selectedSlotIndex + 1
            && _activeDraft.FlipX.HasValue)
        {
            return _activeDraft.FlipX.Value;
        }

        if (_draftFlips[_selectedSlotIndex].HasValue)
        {
            return _draftFlips[_selectedSlotIndex]!.Value;
        }

        if (_selectedExistingDraft?.Operation == VisualCharacterDraftOperation.Set
            && _selectedExistingDraft.FlipX.HasValue)
        {
            return _selectedExistingDraft.FlipX.Value;
        }

        float y = _slotSnapshots[_selectedSlotIndex]?.State?.LocalEulerAngles.Y ?? 0f;
        return CharacterScreenRotation.IsHorizontallyFlipped(y);
    }

    private static float NormalizeSigned(float value)
    {
        float normalized = value % 360f;
        if (normalized > 180f)
        {
            normalized -= 360f;
        }
        else if (normalized <= -180f)
        {
            normalized += 360f;
        }

        return normalized == 0f ? 0f : normalized;
    }

    private void UpdateInspector()
    {
        Text? inspector = _inspectorText;
        if (inspector == null)
        {
            return;
        }

        if (_mode == VisualEditorMode.Camera)
        {
            UpdateCameraInspector(inspector);
            return;
        }

        if (_mode == VisualEditorMode.ScreenText)
        {
            UpdateScreenTextInspector(inspector);
            return;
        }

        if (!HasSelectedSlot())
        {
            inspector.text = "请从固定槽位栏选择，或直接点击红框内的人物槽位";
            if (_rotationValueText is not null)
            {
                _rotationValueText.text = "旋转 --";
            }

            UpdateControlButtons();
            return;
        }

        int index = _selectedSlotIndex;
        CharacterSlotSnapshot? snapshot = _slotSnapshots[index];
        VisualEditorPoint position = SelectedPosition(index);
        VisualEditorPoint storyPosition = SlotLocalToStory(index, position);
        float rotation = SelectedRotation();
        bool flip = SelectedFlip();
        string occupancy = snapshot?.Occupied == true ? "已占用" : "待入场";
        string mode;
        if (_selectedCommandGraphicallyUnsupported && _selectedExistingDraft != null)
        {
            mode = "  |  相对移动  |  请用指令编辑";
        }
        else if (_activeDraft?.Operation == VisualCharacterDraftOperation.Reset)
        {
            mode = "  |  重置指令";
        }
        else if (_selectedExistingDraft?.Operation == VisualCharacterDraftOperation.Set)
        {
            mode = "  |  已有定位指令";
        }
        else if (_selectedExistingDraft?.Operation == VisualCharacterDraftOperation.Reset)
        {
            mode = "  |  已有重置指令";
        }
        else
        {
            mode = string.Empty;
        }

        string officialTransition = IsOfficialTransitionSlot(index + 1)
            ? "  |  官方位移"
            : string.Empty;
        inspector.text =
            $"槽位 #{index + 1}  {occupancy}{mode}{officialTransition}\n"
            + $"剧情坐标 X {storyPosition.X:0.0}  Y {storyPosition.Y:0.0}   "
            + $"偏移 X {position.X:0.0}  Y {position.Y:0.0}\n"
            + $"旋转 {rotation:0.0}   翻转 {(flip ? "开" : "关")}";
        if (_selectedCommandGraphicallyUnsupported && _selectedExistingDraft != null)
        {
            inspector.text =
                $"槽位 #{index + 1}  {occupancy}{mode}\n"
                + $"剧情坐标 X {storyPosition.X:0.0}  Y {storyPosition.Y:0.0}   "
                + $"偏移 X {position.X:0.0}  Y {position.Y:0.0}\n"
                + $"位移X {_selectedExistingDraft.X ?? 0f:0.0}   "
                + $"位移Y {_selectedExistingDraft.Y ?? 0f:0.0}   "
                + $"相对旋转 {_selectedExistingDraft.RotationDegrees ?? 0f:0.0}";
        }
        if (_rotationValueText is not null)
        {
            _rotationValueText.text = $"旋转\n{rotation:0.0}";
        }

        if (_rotationHandleRect is not null)
        {
            _rotationHandleRect.localEulerAngles = new Vector3(0f, 0f, rotation);
        }

        UpdateControlButtons();
    }

    private void UpdateCameraInspector(Text inspector)
    {
        if (!_cameraReadAvailable)
        {
            inspector.text = "镜头层暂不可用\n" + ShortDiagnostic(_cameraReadError);
            UpdateControlButtons();
            return;
        }

        SceneCameraState camera = CurrentCameraState();
        string source = _cameraResetDraft
            ? "重置草稿"
            : _cameraDraftState.HasValue
                ? "绝对构图草稿"
                : _selectedExistingCameraCommand?.Operation switch
                {
                    SceneCameraOperation.Set => "已有绝对镜头",
                    SceneCameraOperation.Move => "已有相对镜头（拖动后转为绝对构图）",
                    SceneCameraOperation.Reset => "已有镜头重置",
                    _ => "当前实时镜头"
                };
        inspector.text =
            $"镜头操控  |  {source}\n"
            + $"中心 X {camera.X:0.0}  Y {camera.Y:0.0}   缩放 {camera.Zoom:0.###}\n"
            + "拖动橙色镜头框平移；拖动角点缩放；灰框为固定画布";
        UpdateControlButtons();
    }

    private void UpdateScreenTextInspector(Text inspector)
    {
        EditorCommandDocumentSnapshot? document = _documentSnapshot;
        if (document == null)
        {
            inspector.text = "屏幕文字来源暂不可用\n" + ShortDiagnostic(_documentReadError);
            UpdateControlButtons();
            return;
        }

        if (document.ScreenTextLines.Count > 1)
        {
            inspector.text =
                $"本场存在 {document.ScreenTextLines.Count} 条 #st/#stm\n"
                + "为避免覆盖手写排版，请先在官方额外指令中整理为一条";
            UpdateControlButtons();
            return;
        }

        ScreenTextDirective current = CurrentScreenText();
        string source = _screenTextRemoveDraft
            ? "移除草稿"
            : _screenTextClearAllDraft
                ? "清除全部文字草稿"
            : _screenTextDraft != null
                ? "屏幕文字草稿"
                : _selectedExistingScreenText != null
                    ? "已有官方屏幕文字"
                    : _rememberedScreenTextSettings != null
                        ? "继承上一次排版"
                        : "新建屏幕文字";
        inspector.text =
            $"屏幕文字  |  {source}\n"
            + $"X {current.X:0.0}  Y {current.Y:0.0}   字号 {current.FontSize}   "
            + $"{ScreenTextAlignmentLabel(current.Alignment)} / "
            + ScreenTextRevealLabel(current.RevealMode)
            + $"\n当前实际显示 {_visibleScreenTexts.Count} 条；彩色竖条为各段的官方 X 起点"
            + (document.HasClearScreenTextDirective
                ? "  |  本场已有 #clearST"
                : string.Empty);
        UpdateControlButtons();
    }

    private void UpdateControlButtons()
    {
        UpdateModeVisibility();

        foreach (ControlButton button in _controlButtons.Values)
        {
            button.RefreshAppearance();
        }

        for (int index = 0; index < _slotSelectorButtons.Length; index++)
        {
            SlotSelectorButton? selector = _slotSelectorButtons[index];
            if (selector == null)
            {
                continue;
            }

            bool occupied = _slotSnapshots[index]?.Occupied == true;
            selector.RefreshAppearance(
                selected: index == _selectedSlotIndex,
                occupied: occupied);
        }

        if (_controlButtons.TryGetValue("flip", out ControlButton? flip))
        {
            flip.Label.text = $"左右翻转  {(SelectedFlip() ? "开" : "关")}";
        }

        if (_controlButtons.TryGetValue("duration", out ControlButton? duration))
        {
            duration.Label.text = SelectedSlotIsOccupiedRaw()
                ? $"时长  {_draftDurationMilliseconds}毫秒"
                : "时长  立即";
        }

        if (_controlButtons.TryGetValue("easing", out ControlButton? easing))
        {
            easing.Label.text = $"缓动  {EasingLabel(_draftEasing)}";
        }

        if (_controlButtons.TryGetValue(
                "camera-duration",
                out ControlButton? cameraDuration))
        {
            cameraDuration.Label.text = $"镜头时长  {_cameraDraftDurationMilliseconds}毫秒";
        }

        if (_controlButtons.TryGetValue(
                "camera-easing",
                out ControlButton? cameraEasing))
        {
            cameraEasing.Label.text = $"镜头缓动  {EasingLabel(_cameraDraftEasing)}";
        }


        ScreenTextDirective screenText = CurrentScreenText();
        if (_controlButtons.TryGetValue("text-align", out ControlButton? textAlign))
        {
            textAlign.Label.text = "对齐  " + ScreenTextAlignmentLabel(screenText.Alignment);
        }

        if (_controlButtons.TryGetValue("text-mode", out ControlButton? textMode))
        {
            textMode.Label.text = "显示  " + ScreenTextRevealLabel(screenText.RevealMode);
        }

        if (_controlButtons.TryGetValue(
                "text-align-previous-x",
                out ControlButton? previousX))
        {
            previousX.Label.text = TryGetPreviousVisibleScreenText(
                    out VisibleScreenTextSnapshot? previous)
                && previous != null
                ? $"X 对齐上一段  {previous.Directive.X:0.###}"
                : "X 对齐上一段";
        }
    }

    private static string ScreenTextAlignmentLabel(ScreenTextAlignment alignment) =>
        alignment == ScreenTextAlignment.Center ? "居中" : "左对齐";

    private static string ScreenTextRevealLabel(ScreenTextRevealMode mode) => mode switch
    {
        ScreenTextRevealMode.Instant => "立即",
        ScreenTextRevealMode.Smooth => "渐显",
        ScreenTextRevealMode.Serial => "打字机",
        _ => "立即"
    };

    private static string EasingLabel(CharacterTransformEasing easing) => easing switch
    {
        CharacterTransformEasing.Linear => "LINEAR",
        CharacterTransformEasing.EaseIn => "IN",
        CharacterTransformEasing.EaseOut => "OUT",
        CharacterTransformEasing.EaseInOut => "IN OUT",
        _ => "LINEAR"
    };

    private void RefreshCommandDocument()
    {
        ApiResult<EditorCommandDocumentSnapshot> result =
            Plugin.Api.EditorCommandDocuments.ReadSelectedOnMainThread();
        Text? status = _statusText;
        Text? draft = _draftText;
        if (!result.Success || result.Value == null)
        {
            string error = string.IsNullOrWhiteSpace(result.Error)
                ? "No source diagnostic was returned."
                : result.Error;
            if (!string.Equals(error, _documentReadError, StringComparison.Ordinal))
            {
                _documentReadError = error;
                Plugin.Logger.LogWarning(
                    "Visual editor source read unavailable: " + error);
            }

            ClearSelectionDrafts();
            _documentSnapshot = null;
            _officialTransitionSlots = Array.Empty<int>();
            if (_activeDraft == null)
            {
                _selectedExistingDraft = null;
                _selectedCommandGraphicallyUnsupported = false;
            }
            if (!_cameraDraftState.HasValue && !_cameraResetDraft)
            {
                _selectedExistingCameraCommand = null;
            }
            if (_screenTextDraft == null
                && !_screenTextRemoveDraft
                && !_screenTextClearAllDraft)
            {
                _selectedExistingScreenText = null;
                _screenTextContent = string.Empty;
            }
            if (status is not null)
            {
                status.text = "来源不可用";
            }

            if (draft is not null && string.IsNullOrEmpty(_currentDraftDirective))
            {
                draft.text = "来源不可用\n"
                    + $"{ShortDiagnostic(error)}\n草稿  --";
            }

            UpdateInspector();
            UpdateButtons();
            return;
        }

        if (!string.IsNullOrEmpty(_documentReadError))
        {
            Plugin.Logger.LogInfo(
                "Visual editor source read recovered after resolving the active inspector.");
            _documentReadError = string.Empty;
        }

        if ((_documentSnapshot != null && _documentSnapshot.RuntimeSelectionKey != result.Value.RuntimeSelectionKey)
            || (_draftSourceDocument != null && _draftSourceDocument.RuntimeSelectionKey != result.Value.RuntimeSelectionKey)
            || (_verifiedPreview != null && _verifiedPreview.Source.RuntimeSelectionKey != result.Value.RuntimeSelectionKey))
        {
            ClearSelectionDrafts();
            _operationMessage = "台词选择或内容已变化，旧草稿已清除。";
        }
        _documentSnapshot = result.Value;
        _selectedSlotIndex = _slotPreference.Restore(result.Value.RuntimeContextId) - 1;
        _officialTransitionSlots = result.Value.OfficialPositionTransitionSlots;
        LoadExistingCommandForSelection(preserveActiveDraft: true);
        LoadExistingCameraCommand(preserveActiveDraft: true);
        LoadExistingScreenText(preserveActiveDraft: true);

        if (_verifiedPreview != null
            && (_verifiedPreview.Source.RuntimeSelectionKey != result.Value.RuntimeSelectionKey
                || !string.Equals(
                _verifiedPreview.Source.RevisionSha256,
                result.Value.RevisionSha256,
                StringComparison.OrdinalIgnoreCase)))
        {
            _verifiedPreview = null;
        }

        if (status is not null)
        {
            status.text = !result.Value.InputMatchesScript
                ? "来源不同步"
                : _verifiedPreview != null
                    ? "可以应用"
                    : result.Value.UndoAvailable
                        ? "来源同步  |  撤销就绪"
                        : "来源同步  |  草稿";
        }

        if (draft is not null && string.IsNullOrEmpty(_currentDraftDirective))
        {
            string? playbackError = EditorNotifications.PeekPlaybackErrorText();
            if (playbackError != null)
            {
                draft.text = $"{SourceSummary(result.Value)}\n"
                    + "播放指令错误（已停止应用，修复后自动恢复）\n"
                    + playbackError;
                EditorNotifications.AnnouncePendingPlaybackError();
            }
            else if (EditorNotifications.PeekPlaybackNoticeText() is string playbackNotice)
            {
                // Information, not a failure: a saved project that has not
                // been built yet. The commands resume by themselves once the
                // build rewrites the playback archive.
                draft.text = $"{SourceSummary(result.Value)}\n"
                    + "播放指令等待构建\n"
                    + playbackNotice;
            }
            else
            {
                draft.text = $"{SourceSummary(result.Value)}\n"
                    + (string.IsNullOrEmpty(_operationMessage)
                        ? "草稿  --"
                        : _operationMessage);
            }
        }
        UpdateInspector();
        UpdateButtons();
    }

    private void VerifyDraftAgainstSelected()
    {
        EditorCommandDocumentSnapshot? source = _draftSourceDocument;
        Text? draft = _draftText;
        if (source == null || draft is null || string.IsNullOrEmpty(_currentDraftDirective))
        {
            return;
        }

        ApiResult<EditorCommandEditPreviewSnapshot> verified =
            Plugin.Api.EditorCommandDocuments.PreviewUpsertSelectedOnMainThread(
                source.RevisionSha256,
                _currentDraftDirective,
                source.RuntimeSelectionKey);
        if (!verified.Success || verified.Value == null)
        {
            _verifiedPreview = null;
            draft.text = $"{SourceSummary(source)}\n草稿已过期 / 来源不同步\n{verified.Error}";
            UpdateButtons();
            return;
        }

        _documentSnapshot = verified.Value.Source;
        _verifiedPreview = verified.Value;
        draft.text =
            $"{SourceSummary(verified.Value.Source)}\n草稿（未保存）\n"
            + $"{verified.Value.CanonicalPublicDirective}\n"
            + $"合并预览已校验 -> {ShortRevision(verified.Value.ResultRevisionSha256)}";
        UpdateButtons();
    }

    private void ToggleContinue()
    {
        EditorCommandDocumentSnapshot? source = _documentSnapshot;
        Text? draft = _draftText;
        if (source == null)
        {
            return;
        }

        bool enable = !source.HasContinueDirective;
        ApiResult<EditorCommandContinueApplySnapshot> result =
            Plugin.Api.EditorCommandDocuments.ApplyContinueToggleOnMainThread(enable, source.RuntimeSelectionKey);
        if (!result.Success || result.Value == null)
        {
            _verifiedPreview = null;
            _operationMessage = "连续对话切换失败\n" + result.Error;
            if (draft is not null)
            {
                draft.text = $"{SourceSummary(source)}\n{_operationMessage}";
            }

            EditorNotifications.TryToast(
                "连续对话切换失败：" + EditorNotifications.FirstLine(result.Error ?? string.Empty),
                "continue-failed");
            RefreshCommandDocument();
            UpdateButtons();
            return;
        }

        _documentSnapshot = result.Value.After;
        _officialTransitionSlots = result.Value.After.OfficialPositionTransitionSlots;
        _operationMessage = result.Value.AlreadyPresent
            ? "连续对话已是该状态，无需切换"
            : enable
                ? "连续对话已开启（重播本场景后生效）"
                : "连续对话已关闭";
        if (draft is not null)
        {
            draft.text = $"{SourceSummary(result.Value.After)}\n{_operationMessage}";
        }

        Plugin.Logger.LogInfo(
            "Visual editor toggled #aavt;continue enabled=" + enable
            + " alreadyPresent=" + result.Value.AlreadyPresent);
        UpdateInspector();
        UpdateButtons();
    }

    private void ApplyVerifiedDraft()
    {
        if (_mode == VisualEditorMode.ScreenText)
        {
            ApplyScreenTextDraft();
            return;
        }

        EditorCommandEditPreviewSnapshot? verified = _verifiedPreview;
        Text? draft = _draftText;
        if (verified == null || draft == null || string.IsNullOrEmpty(_currentDraftDirective))
        {
            return;
        }

        SceneCameraState? appliedCameraState = _cameraResetDraft
            ? SceneCameraState.Default
            : _cameraDraftState;

        if (_activeDraft != null
            && DraftWritesPosition(_activeDraft)
            && IsOfficialTransitionSlot(_activeDraft.PublicSlot))
        {
            _verifiedPreview = null;
            _operationMessage =
                $"应用被拒绝：槽位 #{_activeDraft.PublicSlot} 在该场景存在官方位移过渡，"
                + "位置类指令会导致整个播放索引失效";
            draft.text = $"{SourceSummary(verified.Source)}\n{_operationMessage}";
            EditorNotifications.TryToast(
                $"应用被拒绝：槽位 #{_activeDraft.PublicSlot} 有官方位移过渡",
                "apply-blocked-" + _activeDraft.PublicSlot);
            UpdateButtons();
            return;
        }

        ApiResult<EditorCommandApplySnapshot> result =
            Plugin.Api.EditorCommandDocuments.ApplyUpsertSelectedOnMainThread(
                verified.Source.RevisionSha256,
                _currentDraftDirective,
                verified.Source.RuntimeSelectionKey);
        if (!result.Success || result.Value == null)
        {
            _verifiedPreview = null;
            if ((result.Error ?? string.Empty).Contains(
                    "already present",
                    StringComparison.OrdinalIgnoreCase))
            {
                // Idempotent apply guard: the exact directive is already in
                // the scene text, so nothing was written. That is success
                // from the author's point of view, not a failure.
                _operationMessage = "已应用（无变化）：该指令已在场景中，无需重复写入";
                draft.text = $"{SourceSummary(verified.Source)}\n{_operationMessage}";
                RefreshCommandDocument();
                UpdateButtons();
                return;
            }

            _operationMessage = "应用失败\n" + result.Error;
            draft.text = $"{SourceSummary(verified.Source)}\n{_operationMessage}";
            EditorNotifications.TryToast(
                "AAVT 应用失败：" + EditorNotifications.FirstLine(result.Error ?? string.Empty),
                "apply-failed");
            RefreshCommandDocument();
            UpdateButtons();
            return;
        }

        EditorCommandApplySnapshot applied = result.Value;
        _documentSnapshot = applied.After;
        _draftSourceDocument = null;
        _verifiedPreview = null;
        _activeDraft = null;
        _cameraDraftState = null;
        _cameraResetDraft = false;
        if (appliedCameraState.HasValue)
        {
            _cameraState = appliedCameraState.Value;
        }
        _currentDraftDirective = string.Empty;
        LoadExistingCommandForSelection(preserveActiveDraft: false);
        LoadExistingCameraCommand(preserveActiveDraft: false);
        string targetLabel = applied.PublicSlot == 0
            ? "镜头"
            : $"槽位 #{applied.PublicSlot}";
        _operationMessage =
            $"已应用并校验  {targetLabel}\n"
            + $"来源 {ShortRevision(applied.Before.RevisionSha256)} -> "
            + $"{ShortRevision(applied.After.RevisionSha256)}\n"
            + "撤销已就绪 | 重播本场景后生效";
        draft.text = $"{SourceSummary(applied.After)}\n{_operationMessage}";
        UpdateInspector();
        UpdateButtons();
    }

    private void ApplyScreenTextDraft()
    {
        if (_screenTextClearAllDraft)
        {
            ApplyClearScreenTextDraft();
            return;
        }

        EditorCommandDocumentSnapshot? source = _draftSourceDocument;
        Text? draft = _draftText;
        if (source == null
            || draft == null
            || (_screenTextDraft == null && !_screenTextRemoveDraft))
        {
            return;
        }

        ScreenTextDirective? directive = _screenTextRemoveDraft
            ? null
            : _screenTextDraft;
        ApiResult<EditorCommandScreenTextApplySnapshot> result =
            Plugin.Api.EditorCommandDocuments.ApplyScreenTextOnMainThread(
                source.RevisionSha256,
                directive,
                source.RuntimeSelectionKey);
        if (!result.Success || result.Value == null)
        {
            _operationMessage = "屏幕文字应用失败\n" + result.Error;
            draft.text = $"{SourceSummary(source)}\n{_operationMessage}";
            EditorNotifications.TryToast(
                "屏幕文字应用失败："
                    + EditorNotifications.FirstLine(result.Error ?? string.Empty),
                "screen-text-apply-failed");
            RefreshCommandDocument();
            UpdateButtons();
            return;
        }

        EditorCommandScreenTextApplySnapshot applied = result.Value;
        _documentSnapshot = applied.After;
        _draftSourceDocument = null;
        _screenTextDraft = null;
        _screenTextRemoveDraft = false;
        _screenTextClearAllDraft = false;
        _currentDraftDirective = string.Empty;
        _verifiedPreview = null;
        LoadExistingScreenText(preserveActiveDraft: false);
        _operationMessage = applied.AlreadyPresent
            ? "屏幕文字已经是该状态，无需重复写入"
            : applied.Directive == null
                ? "已移除本场 #st/#stm，并完成官方输入回读"
                : "已写入官方屏幕文字指令；重播本场景后查看实际渐显效果";
        draft.text = $"{SourceSummary(applied.After)}\n{_operationMessage}\n"
            + (applied.Directive?.ToOfficialDirective() ?? string.Empty);
        UpdateModeVisibility();
        LayoutScreenTextPreview();
        UpdateInspector();
        UpdateButtons();
    }

    private void ApplyClearScreenTextDraft()
    {
        EditorCommandDocumentSnapshot? source = _draftSourceDocument;
        Text? draft = _draftText;
        if (source == null || draft == null || !_screenTextClearAllDraft)
        {
            return;
        }

        ApiResult<EditorCommandClearScreenTextApplySnapshot> result =
            Plugin.Api.EditorCommandDocuments.ApplyClearScreenTextOnMainThread(
                source.RevisionSha256,
                source.RuntimeSelectionKey);
        if (!result.Success || result.Value == null)
        {
            _operationMessage = "#clearST 应用失败\n" + result.Error;
            draft.text = $"{SourceSummary(source)}\n{_operationMessage}";
            EditorNotifications.TryToast(
                "清除屏幕文字失败："
                    + EditorNotifications.FirstLine(result.Error ?? string.Empty),
                "screen-text-clear-apply-failed");
            RefreshCommandDocument();
            UpdateButtons();
            return;
        }

        EditorCommandClearScreenTextApplySnapshot applied = result.Value;
        _documentSnapshot = applied.After;
        _draftSourceDocument = null;
        _screenTextDraft = null;
        _screenTextRemoveDraft = false;
        _screenTextClearAllDraft = false;
        _currentDraftDirective = string.Empty;
        _verifiedPreview = null;
        LoadExistingScreenText(preserveActiveDraft: false);
        _operationMessage = applied.AlreadyPresent
            ? "本场已经包含 #clearST，无需重复写入"
            : "已写入 #clearST；绿色重播本场景后清除全部屏幕文字";
        draft.text = $"{SourceSummary(applied.After)}\n{_operationMessage}\n#clearST";
        UpdateModeVisibility();
        LayoutScreenTextPreview();
        UpdateInspector();
        UpdateButtons();
    }

    private void UndoLastApply()
    {
        EditorCommandDocumentSnapshot? source = _documentSnapshot;
        Text? draft = _draftText;
        if (source?.UndoAvailable != true || draft == null)
        {
            return;
        }

        ApiResult<EditorCommandUndoSnapshot> result =
            Plugin.Api.EditorCommandDocuments.UndoSelectedOnMainThread(source.RuntimeSelectionKey);
        if (!result.Success || result.Value == null)
        {
            _operationMessage = "撤销失败\n" + result.Error;
            draft.text = $"{SourceSummary(source)}\n{_operationMessage}";
            RefreshCommandDocument();
            UpdateButtons();
            return;
        }

        EditorCommandUndoSnapshot undone = result.Value;
        _documentSnapshot = undone.Restored;
        _draftSourceDocument = null;
        _verifiedPreview = null;
        _activeDraft = null;
        _cameraDraftState = null;
        _cameraResetDraft = false;
        _screenTextDraft = null;
        _screenTextRemoveDraft = false;
        _screenTextClearAllDraft = false;
        _currentDraftDirective = string.Empty;
        Array.Clear(_draftPositions, 0, _draftPositions.Length);

        Array.Clear(_draftRotations, 0, _draftRotations.Length);
        Array.Clear(_draftFlips, 0, _draftFlips.Length);
        LoadExistingCommandForSelection(preserveActiveDraft: false);
        LoadExistingCameraCommand(preserveActiveDraft: false);
        LoadExistingScreenText(preserveActiveDraft: false);
        _operationMessage =
            "撤销已恢复并校验\n"
            + $"来源 {ShortRevision(undone.BeforeUndo.RevisionSha256)} -> "
            + ShortRevision(undone.Restored.RevisionSha256);
        draft.text = $"{SourceSummary(undone.Restored)}\n{_operationMessage}";
        UpdateInspector();
        UpdateButtons();
    }

    private static string SourceSummary(EditorCommandDocumentSnapshot source) =>
        $"来源 {ShortRevision(source.RevisionSha256)} "
        + $"官方={source.OfficialLineCount} AAVT={source.AavtLineCount} "
        + $"界面={(source.InputMatchesScript ? "同步" : "不同步")}";

    private static string ShortRevision(string revision) =>
        revision.Length <= 10 ? revision : revision[..10];

    private static string ShortDiagnostic(string diagnostic)
    {
        if (string.IsNullOrWhiteSpace(diagnostic))
        {
            return "等待当前场景来源";
        }

        string singleLine = diagnostic.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= 88 ? singleLine : singleLine[..85] + "...";
    }

    private void SetExpanded(bool expanded)
    {
        expanded = expanded && ReadEditorPreviewMode() && !_creationFailed;
        bool changed = expanded != _expanded;
        _expanded = expanded;
        if (expanded) _editorPreviewVisible = true;
        CancelPointerInteraction();
        if (!expanded) VisualEditorInputGuard.Clear();
        GameObject? panelObject = _panelObject;
        if (panelObject is not null)
        {
            panelObject.SetActive(expanded);
        }

        SetCanvasActive(expanded && _editorPreviewVisible);
        if (expanded)
        {
            RefreshSlots();
            RefreshCommandDocument();
        }
        UpdateModeVisibility();
        Layout();
        // The panel rectangle only exists after Layout(), and SetCanvasActive published before that: republish so
        // the first frame the editor is on screen already protects its own area.
        if (expanded)
        {
            PublishInputRegion();
        }

        if (changed)
        {
            // One line per visibility change. It names the page state, the canvas state and the rectangle the
            // editor will draw in, which together are everything needed to tell a visibility bug from a
            // positioning one — and this editor cannot be debugged from a screenshot alone.
            Plugin.Logger.LogInfo(
                $"[host] editor {(expanded ? "shown" : "hidden")}; pageShown={_hostShown}; canvas={_canvasActive}"
                + $"; rect={_panelRect.X:F0},{_panelRect.Y:F0} {_panelRect.Width:F0}x{_panelRect.Height:F0} story");
        }
    }

    private void CancelPointerInteraction()
    {
        _pressedSlotIndex = -1;
        _draggingSlotIndex = -1;
        _rotationDragging = false;
        _cameraPressed = false;
        _cameraDragging = false;
        _cameraZoomDragging = false;
        _cameraZoomHandleIndex = -1;
        _screenTextPressed = false;
        _screenTextDragging = false;
    }

    private void Layout()
    {
        CanvasScaler? canvasScaler = _canvasScaler;
        GameObject? panelObject = _panelObject;
        if (canvasScaler is null
            || panelObject is null
            || Screen.width <= 0
            || Screen.height <= 0)
        {
            return;
        }

        _lastScreenWidth = Screen.width;
        _lastScreenHeight = Screen.height;
        _canvasScale = Screen.width / VisualEditorCanvasMath.StoryWidth;
        canvasScaler.scaleFactor = _canvasScale;
        _storyHeight = VisualEditorCanvasMath.LogicalStoryHeight(
            Screen.width,
            Screen.height).Value;

        // The frame is the shared toolbox's now. This editor fills the content rectangle its tool page was handed
        // — the same screen, two units: the page reports physical pixels and everything below is story units, so
        // the division is the whole conversion. Without a rectangle yet (the page is drawn for the first time this
        // frame, and the editor's frame may run before the renderer's) the editor keeps its own natural geometry
        // for one frame rather than laying itself out at zero size.
        bool hostChanged = !_hostPixels.Equals(_laidOutHostPixels);
        if (_hostPixels.IsValid && _canvasScale > 0f)
        {
            _panelRect = new VisualEditorRect(
                _hostPixels.X / _canvasScale,
                _hostPixels.Y / _canvasScale,
                _hostPixels.Width / _canvasScale,
                _hostPixels.Height / _canvasScale);
        }
        else
        {
            _panelRect = new VisualEditorRect(
                ToolDrawerLayout.LeftPanelEdgePixels(Screen.width, Screen.height) / _canvasScale,
                PanelMargin,
                PanelWidth,
                Math.Max(900f, _storyHeight - (PanelMargin * 2f)));
        }

        _laidOutHostPixels = _hostPixels;
        float panelHeight = _panelRect.Height;
        if (hostChanged)
        {
            // One line per rectangle change: this is the only record of what the shared panel actually handed over
            // next to what the editor laid itself out in, and the two have to be the same rectangle.
            Plugin.Logger.LogInfo(
                $"[host] content={_hostPixels.X:F0},{_hostPixels.Y:F0} {_hostPixels.Width:F0}x{_hostPixels.Height:F0}px"
                + $"; story={_panelRect.X:F0},{_panelRect.Y:F0} {_panelRect.Width:F0}x{_panelRect.Height:F0}"
                + $"; scale={_canvasScale:F3}; expanded={_expanded}");
        }
        SetRect(
            panelObject.GetComponent<RectTransform>(),
            _panelRect.X,
            _panelRect.Y,
            _panelRect.Width,
            _panelRect.Height);

        // The editor's own source/status line stays where it always was, just inside the content rectangle: the
        // library draws the title to its left now, so the row reads as one header without either side owning both.
        SetRect(
            _statusText?.GetComponent<RectTransform>(),
            430f,
            panelHeight - 68f,
            260f,
            56f);

        const float modeGap = 12f;
        const float modeHeight = 42f;
        float modeY = panelHeight - 130f;
        float modeWidth = (PanelWidth - 60f - (modeGap * 2f)) / 3f;
        LayoutControl("mode-character", 30f, modeY, modeWidth, modeHeight);
        LayoutControl(
            "mode-camera",
            30f + modeWidth + modeGap,
            modeY,
            modeWidth,
            modeHeight);
        LayoutControl(
            "mode-screen-text",
            30f + ((modeWidth + modeGap) * 2f),
            modeY,
            modeWidth,
            modeHeight);

        const float slotSelectorHeight = 42f;
        const float slotSelectorGap = 8f;
        float slotSelectorY = panelHeight - 180f;
        float slotSelectorWidth =
            (PanelWidth - 60f - (slotSelectorGap * 4f)) / 5f;
        for (int index = 0; index < _slotSelectorButtons.Length; index++)
        {
            SlotSelectorButton? selector = _slotSelectorButtons[index];
            if (selector == null)
            {
                continue;
            }

            float selectorX = 30f + (index * (slotSelectorWidth + slotSelectorGap));
            SetRect(
                selector.Rect,
                selectorX,
                slotSelectorY,
                slotSelectorWidth,
                slotSelectorHeight);
            SetRect(
                selector.Label.GetComponent<RectTransform>(),
                0f,
                0f,
                slotSelectorWidth,
                slotSelectorHeight);
            selector.HitRect = new VisualEditorRect(
                _panelRect.X + selectorX,
                _panelRect.Y + slotSelectorY,
                slotSelectorWidth,
                slotSelectorHeight);
        }

        float frameWidth = PanelWidth - 60f;
        float frameHeight = frameWidth * _storyHeight / VisualEditorCanvasMath.StoryWidth;
        float frameTopOffset = _mode == VisualEditorMode.Character ? 204f : 154f;
        float frameY = panelHeight - frameTopOffset - frameHeight;
        _viewportRect = new VisualEditorRect(
            _panelRect.X + 30f,
            _panelRect.Y + frameY,
            frameWidth,
            frameHeight);
        if (!ReferenceEquals(_frameRoot, null))
        {
            SetRect(_frameRoot, 30f, frameY, frameWidth, frameHeight);
        }

        SetFrameLine(0, 0f, 0f, frameWidth, 4f);
        SetFrameLine(1, 0f, frameHeight - 4f, frameWidth, 4f);
        SetFrameLine(2, 0f, 0f, 4f, frameHeight);
        SetFrameLine(3, frameWidth - 4f, 0f, 4f, frameHeight);
        SetRect(_zeroYGuide, 0f, (frameHeight / 2f) - 1f, frameWidth, 2f);
        SetRect(
            _zeroYLabel?.GetComponent<RectTransform>(),
            8f,
            (frameHeight / 2f) + 3f,
            54f,
            22f);
        for (int index = 0; index < _slotGuideLines.Length; index++)
        {
            VisualEditorPoint anchor = VisualEditorCanvasMath.SlotAnchor(index + 1).Value;
            float guideX = (anchor.X
                    + (VisualEditorCanvasMath.StoryHalfWidth
                        * VisualEditorCanvasMath.ViewportZoomOutFactor))
                / (VisualEditorCanvasMath.StoryWidth
                    * VisualEditorCanvasMath.ViewportZoomOutFactor)
                * frameWidth;
            SetRect(_slotGuideLines[index], guideX - 1f, 0f, 2f, frameHeight);
            SetRect(
                _slotGuideLabels[index]?.GetComponent<RectTransform>(),
                guideX - 38f,
                4f,
                76f,
                22f);
            if (_slotGuideLabels[index] is not null)
            {
                _slotGuideLabels[index]!.text = $"#{index + 1} {anchor.X:0}";
            }
        }

        float inspectorY = frameY - 72f;
        SetRect(
            _inspectorText?.GetComponent<RectTransform>(),
            30f,
            inspectorY,
            PanelWidth - 60f,
            64f);

        const float rowHeight = 42f;
        const float rowGap = 8f;
        float fourColumnWidth = (PanelWidth - 60f - (rowGap * 3f)) / 4f;
        float xRowY = inspectorY - 50f;
        float yRowY = xRowY - 50f;
        string[] xControls = { "x-100", "x-10", "x+10", "x+100" };
        string[] yControls = { "y-100", "y-10", "y+10", "y+100" };
        string[] cameraXControls =
        {
            "camera-x-100", "camera-x-10", "camera-x+10", "camera-x+100"
        };
        string[] cameraYControls =
        {
            "camera-y-100", "camera-y-10", "camera-y+10", "camera-y+100"
        };
        string[] textXControls =
        {
            "text-x-100", "text-x-10", "text-x+10", "text-x+100"
        };
        string[] textYControls =
        {
            "text-y-100", "text-y-10", "text-y+10", "text-y+100"
        };
        for (int i = 0; i < 4; i++)
        {
            float x = 30f + (i * (fourColumnWidth + rowGap));
            LayoutControl(xControls[i], x, xRowY, fourColumnWidth, rowHeight);
            LayoutControl(yControls[i], x, yRowY, fourColumnWidth, rowHeight);
            LayoutControl(cameraXControls[i], x, xRowY, fourColumnWidth, rowHeight);
            LayoutControl(cameraYControls[i], x, yRowY, fourColumnWidth, rowHeight);
            LayoutControl(textXControls[i], x, xRowY, fourColumnWidth, rowHeight);
            LayoutControl(textYControls[i], x, yRowY, fourColumnWidth, rowHeight);
        }

        float dialY = yRowY - 112f;
        const float dialSize = 104f;
        SetRect(_rotationDialRect, 30f, dialY, dialSize, dialSize);
        if (_rotationHandleRect is not null)
        {
            _rotationHandleRect.anchorMin = Vector2.zero;
            _rotationHandleRect.anchorMax = Vector2.zero;
            _rotationHandleRect.pivot = new Vector2(0.5f, 0f);
            _rotationHandleRect.anchoredPosition = new Vector2(dialSize / 2f, dialSize / 2f);
            _rotationHandleRect.sizeDelta = new Vector2(4f, 42f);
            _rotationHandleRect.localScale = Vector3.one;
        }

        SetRect(
            _rotationValueText?.GetComponent<RectTransform>(),
            0f,
            0f,
            dialSize,
            dialSize);
        _rotationDialHitRect = new VisualEditorRect(
            _panelRect.X + 30f,
            _panelRect.Y + dialY,
            dialSize,
            dialSize);

        string[] rotationControls = { "r-15", "r-1", "r+1", "r+15" };
        float rotationStartX = 150f;
        float rotationWidth = (PanelWidth - rotationStartX - 30f - (rowGap * 3f)) / 4f;
        for (int i = 0; i < rotationControls.Length; i++)
        {
            LayoutControl(
                rotationControls[i],
                rotationStartX + (i * (rotationWidth + rowGap)),
                dialY + 31f,
                rotationWidth,
                rowHeight);
        }

        string[] cameraZoomControls =
        {
            "camera-z-0.1", "camera-z-0.01", "camera-z+0.01", "camera-z+0.1"
        };
        for (int i = 0; i < cameraZoomControls.Length; i++)
        {
            LayoutControl(
                cameraZoomControls[i],
                30f + (i * (fourColumnWidth + rowGap)),
                dialY + 31f,
                fourColumnWidth,
                rowHeight);
        }

        string[] screenTextFontControls =
        {
            "text-font-10", "text-font-1", "text-font+1", "text-font+10"
        };
        for (int i = 0; i < screenTextFontControls.Length; i++)
        {
            LayoutControl(
                screenTextFontControls[i],
                30f + (i * (fourColumnWidth + rowGap)),
                dialY + 31f,
                fourColumnWidth,
                rowHeight);
        }

        float optionY = dialY - 50f;
        float optionWidth = (PanelWidth - 60f - (rowGap * 2f)) / 3f;
        LayoutControl("flip", 30f, optionY, optionWidth, rowHeight);
        LayoutControl("duration", 30f + optionWidth + rowGap, optionY, optionWidth, rowHeight);
        LayoutControl("easing", 30f + ((optionWidth + rowGap) * 2f), optionY, optionWidth, rowHeight);
        float cameraOptionWidth = (PanelWidth - 60f - rowGap) / 2f;
        LayoutControl("camera-duration", 30f, optionY, cameraOptionWidth, rowHeight);
        LayoutControl(
            "camera-easing",
            30f + cameraOptionWidth + rowGap,
            optionY,
            cameraOptionWidth,
            rowHeight);
        LayoutControl("text-align", 30f, optionY, cameraOptionWidth, rowHeight);
        LayoutControl(
            "text-mode",
            30f + cameraOptionWidth + rowGap,
            optionY,
            cameraOptionWidth,
            rowHeight);

        float resetRowY = optionY - 50f;
        float actionWidth = (PanelWidth - 60f - rowGap) / 2f;
        LayoutControl("r0", 30f, resetRowY, actionWidth, rowHeight);
        LayoutControl(
            "flipreset",
            30f + actionWidth + rowGap,
            resetRowY,
            actionWidth,
            rowHeight);
        LayoutControl("camera-reset", 30f, resetRowY, actionWidth, rowHeight);
        LayoutControl(
            "camera-clear",
            30f + actionWidth + rowGap,
            resetRowY,
            actionWidth,
            rowHeight);
        float screenTextActionWidth = (PanelWidth - 60f - rowGap) / 2f;
        LayoutControl(
            "text-align-previous-x",
            30f,
            resetRowY,
            screenTextActionWidth,
            rowHeight);
        LayoutControl(
            "text-position-reset",
            30f + screenTextActionWidth + rowGap,
            resetRowY,
            screenTextActionWidth,
            rowHeight);

        float actionY = resetRowY - 50f;
        LayoutControl("reset", 30f, actionY, actionWidth, rowHeight);
        LayoutControl("clear", 30f + actionWidth + rowGap, actionY, actionWidth, rowHeight);
        LayoutControl(
            "text-remove",
            30f,
            actionY,
            screenTextActionWidth,
            rowHeight);
        LayoutControl(
            "text-clear",
            30f + screenTextActionWidth + rowGap,
            actionY,
            screenTextActionWidth,
            rowHeight);
        float screenTextClearY = actionY - 50f;
        LayoutControl(
            "text-clear-all",
            30f,
            screenTextClearY,
            PanelWidth - 60f,
            rowHeight);

        float continueRowY = _mode == VisualEditorMode.Character
            ? actionY - 50f
            : _mode == VisualEditorMode.ScreenText
                ? screenTextClearY - 50f
                : resetRowY - 50f;
        LayoutControl("continue", 30f, continueRowY, PanelWidth - 60f, rowHeight);

        const float buttonY = 30f;
        const float buttonHeight = 64f;
        const float buttonGap = 20f;
        float buttonWidth = (PanelWidth - 60f - buttonGap) / 2f;
        SetRect(_applyButtonRect, 30f, buttonY, buttonWidth, buttonHeight);
        SetRect(
            _undoButtonRect,
            30f + buttonWidth + buttonGap,
            buttonY,
            buttonWidth,
            buttonHeight);
        if (_applyButtonText is not null)
            Rukari.Lib.Runtime.Tools.ToolTheme.CenterButtonLabel(_applyButtonText, buttonWidth, buttonHeight, 6f);
        if (_undoButtonText is not null)
            Rukari.Lib.Runtime.Tools.ToolTheme.CenterButtonLabel(_undoButtonText, buttonWidth, buttonHeight, 6f);
        _applyHitRect = new VisualEditorRect(
            _panelRect.X + 30f,
            _panelRect.Y + buttonY,
            buttonWidth,
            buttonHeight);
        _undoHitRect = new VisualEditorRect(
            _panelRect.X + 30f + buttonWidth + buttonGap,
            _panelRect.Y + buttonY,
            buttonWidth,
            buttonHeight);

        RectTransform? draft = _draftText?.GetComponent<RectTransform>();
        SetRect(draft, 30f, 112f, PanelWidth - 60f, Math.Max(72f, continueRowY - 130f));
        UpdateInspector();
        UpdateButtons();
        LayoutSlots();
        LayoutCameraFrame();
        LayoutVisibleScreenTexts();
        LayoutScreenTextPreview();
        PublishInputRegion();
    }

    private void LayoutCameraFrame()
    {
        if (_mode != VisualEditorMode.Camera
            || !_viewportRect.IsValid
            || _cameraFrameRoot == null)
        {
            _cameraFrameHitRect = default;
            Array.Clear(_cameraZoomHandleHitRects, 0, _cameraZoomHandleHitRects.Length);
            return;
        }

        Result<VisualEditorRect> canvasResult = VisualCameraFrameMath.CameraToViewportFrame(
            SceneCameraState.Default,
            _storyHeight,
            _viewportRect);
        Result<VisualEditorRect> frameResult = VisualCameraFrameMath.CameraToViewportFrame(
            CurrentCameraState(),
            _storyHeight,
            _viewportRect);
        if (!canvasResult.Success || !frameResult.Success)
        {
            _cameraFrameHitRect = default;
            return;
        }

        VisualEditorRect canvas = canvasResult.Value;
        SetCameraLines(
            _cameraCanvasLines,
            canvas.X - _viewportRect.X,
            canvas.Y - _viewportRect.Y,
            canvas.Width,
            canvas.Height,
            3f);

        VisualEditorRect frame = frameResult.Value;
        _cameraFrameHitRect = frame;
        SetRect(
            _cameraFrameRoot,
            frame.X - _viewportRect.X,
            frame.Y - _viewportRect.Y,
            frame.Width,
            frame.Height);
        SetCameraLines(_cameraFrameLines, 0f, 0f, frame.Width, frame.Height, 4f);

        const float handleSize = 18f;
        const float hitSize = 30f;
        VisualEditorPoint[] corners =
        {
            new(0f, 0f),
            new(frame.Width, 0f),
            new(0f, frame.Height),
            new(frame.Width, frame.Height)
        };
        for (int index = 0; index < corners.Length; index++)
        {
            VisualEditorPoint corner = corners[index];
            SetRect(
                _cameraZoomHandles[index],
                corner.X - (handleSize / 2f),
                corner.Y - (handleSize / 2f),
                handleSize,
                handleSize);
            _cameraZoomHandleHitRects[index] = new VisualEditorRect(
                frame.X + corner.X - (hitSize / 2f),
                frame.Y + corner.Y - (hitSize / 2f),
                hitSize,
                hitSize);
            RectTransform? handle = _cameraZoomHandles[index];
            if (handle is not null)
            {
                handle.GetComponent<Image>().color = index == _cameraZoomHandleIndex
                    ? SelectedColor
                    : CameraFrameColor;
            }
        }

        SetRect(
            _cameraFrameLabel?.GetComponent<RectTransform>(),
            MathF.Max(4f, (frame.Width / 2f) - 74f),
            MathF.Max(4f, frame.Height - 32f),
            148f,
            28f);
        if (_cameraFrameLabel is not null)
        {
            _cameraFrameLabel.text = $"镜头 ×{CurrentCameraState().Zoom:0.###}";
        }
    }

    private void LayoutVisibleScreenTexts()
    {
        foreach (RectTransform? root in _visibleScreenTextRects)
        {
            root?.gameObject.SetActive(false);
        }

        foreach (RectTransform? anchor in _visibleScreenTextAnchorRects)
        {
            anchor?.gameObject.SetActive(false);
        }

        if (_mode != VisualEditorMode.ScreenText || !_viewportRect.IsValid)
        {
            return;
        }

        ScreenTextDirective? editableExisting = _screenTextDraft == null
            && !_screenTextRemoveDraft
            && !_screenTextClearAllDraft
                ? _selectedExistingScreenText
                : null;
        bool suppressedEditableDuplicate = false;
        int visualIndex = 0;
        for (int sourceIndex = 0; sourceIndex < _visibleScreenTexts.Count; sourceIndex++)
        {
            VisibleScreenTextSnapshot visible = _visibleScreenTexts[sourceIndex];
            if (!suppressedEditableDuplicate
                && editableExisting != null
                && ScreenTextMatches(
                    visible,
                    editableExisting,
                    _screenTextContent))
            {
                suppressedEditableDuplicate = true;
                continue;
            }

            if (visualIndex >= _visibleScreenTextRects.Length)
            {
                break;
            }

            RectTransform? root = _visibleScreenTextRects[visualIndex];
            RectTransform? startAnchor = _visibleScreenTextAnchorRects[visualIndex];
            Text? label = _visibleScreenTextLabels[visualIndex];
            visualIndex++;
            if (root == null || startAnchor == null || label == null)
            {
                continue;
            }

            Result<VisualEditorPoint> mapped = VisualEditorCanvasMath.StoryToViewport(
                new VisualEditorPoint(visible.Directive.X, visible.Directive.Y),
                _storyHeight,
                _viewportRect);
            if (!mapped.Success)
            {
                continue;
            }

            VisualEditorPoint anchor = mapped.Value;
            float previewWidth = _viewportRect.Width * 0.76f;
            float scale = _viewportRect.Width
                / (VisualEditorCanvasMath.StoryWidth
                    * VisualEditorCanvasMath.ViewportZoomOutFactor);
            int previewFontSize = Math.Clamp(
                (int)MathF.Round(visible.Directive.FontSize * scale),
                8,
                72);
            float previewHeight = Math.Max(36f, previewFontSize * 2.4f);
            float absoluteX = visible.Directive.Alignment == ScreenTextAlignment.Center
                ? anchor.X - (previewWidth / 2f)
                : anchor.X;
            float absoluteY = anchor.Y - (previewHeight / 2f);
            Color sequenceColor = ScreenTextSequenceColor(sourceIndex);
            SetRect(
                root,
                absoluteX - _viewportRect.X,
                absoluteY - _viewportRect.Y,
                previewWidth,
                previewHeight);
            SetRect(
                label.GetComponent<RectTransform>(),
                0f,
                0f,
                previewWidth,
                previewHeight);
            const float startAnchorWidth = 8f;
            SetRect(
                startAnchor,
                anchor.X - _viewportRect.X - (startAnchorWidth / 2f),
                absoluteY - _viewportRect.Y,
                startAnchorWidth,
                previewHeight);
            label.alignment = visible.Directive.Alignment == ScreenTextAlignment.Center
                ? TextAnchor.MiddleCenter
                : TextAnchor.MiddleLeft;
            label.fontSize = previewFontSize;
            label.color = sequenceColor;
            label.text = visible.Text;
            Image? startAnchorImage = startAnchor.GetComponent<Image>();
            if (startAnchorImage is not null)
            {
                startAnchorImage.color = sequenceColor;
            }
            root.gameObject.SetActive(true);
            startAnchor.gameObject.SetActive(true);
        }
    }

    private static bool ScreenTextMatches(
        VisibleScreenTextSnapshot visible,
        ScreenTextDirective directive,
        string text) =>
        MathF.Abs(visible.Directive.X - directive.X) <= 0.01f
        && MathF.Abs(visible.Directive.Y - directive.Y) <= 0.01f
        && visible.Directive.Alignment == directive.Alignment
        && visible.Directive.FontSize == directive.FontSize
        && string.Equals(visible.Text, text, StringComparison.Ordinal);

    private static Color ScreenTextSequenceColor(int index)
    {
        float hue = ScreenTextSequenceVisualMath.MarkerHue(Math.Max(0, index));
        const float saturation = 0.58f;
        const float value = 0.98f;
        float chroma = value * saturation;
        float hueSector = hue * 6f;
        float secondary = chroma * (1f - MathF.Abs((hueSector % 2f) - 1f));
        float red;
        float green;
        float blue;
        switch ((int)MathF.Floor(hueSector))
        {
            case 0:
                red = chroma;
                green = secondary;
                blue = 0f;
                break;
            case 1:
                red = secondary;
                green = chroma;
                blue = 0f;
                break;
            case 2:
                red = 0f;
                green = chroma;
                blue = secondary;
                break;
            case 3:
                red = 0f;
                green = secondary;
                blue = chroma;
                break;
            case 4:
                red = secondary;
                green = 0f;
                blue = chroma;
                break;
            default:
                red = chroma;
                green = 0f;
                blue = secondary;
                break;
        }

        float minimum = value - chroma;
        return new Color(red + minimum, green + minimum, blue + minimum, 0.94f);
    }

    private void LayoutScreenTextPreview()
    {
        if (_mode != VisualEditorMode.ScreenText
            || _screenTextRemoveDraft
            || _screenTextClearAllDraft
            || !_viewportRect.IsValid
            || _screenTextPreviewRect == null
            || _screenTextPreview == null)
        {
            _screenTextHitRect = default;
            return;
        }

        ScreenTextDirective screenText = CurrentScreenText();
        Result<VisualEditorPoint> mapped = VisualEditorCanvasMath.StoryToViewport(
            new VisualEditorPoint(screenText.X, screenText.Y),
            _storyHeight,
            _viewportRect);
        if (!mapped.Success)
        {
            _screenTextHitRect = default;
            return;
        }

        VisualEditorPoint anchor = mapped.Value;
        float previewWidth = _viewportRect.Width * 0.76f;
        float scale = _viewportRect.Width
            / (VisualEditorCanvasMath.StoryWidth
                * VisualEditorCanvasMath.ViewportZoomOutFactor);
        int previewFontSize = Math.Clamp(
            (int)MathF.Round(screenText.FontSize * scale),
            8,
            72);
        float previewHeight = Math.Max(36f, previewFontSize * 2.4f);
        float absoluteX = screenText.Alignment == ScreenTextAlignment.Center
            ? anchor.X - (previewWidth / 2f)
            : anchor.X;
        float absoluteY = anchor.Y - (previewHeight / 2f);
        int sequenceIndex = FindCurrentVisibleScreenTextIndex();
        if (sequenceIndex < 0)
        {
            sequenceIndex = _visibleScreenTexts.Count;
        }

        Color sequenceColor = ScreenTextSequenceColor(sequenceIndex);
        SetRect(
            _screenTextPreviewRect,
            absoluteX - _viewportRect.X,
            absoluteY - _viewportRect.Y,
            previewWidth,
            previewHeight);
        SetRect(
            _screenTextPreview.GetComponent<RectTransform>(),
            0f,
            0f,
            previewWidth,
            previewHeight);
        _screenTextPreview.alignment = screenText.Alignment == ScreenTextAlignment.Center
            ? TextAnchor.MiddleCenter
            : TextAnchor.MiddleLeft;
        _screenTextPreview.fontSize = previewFontSize;
        _screenTextPreview.color = sequenceColor;
        _screenTextPreview.text = string.IsNullOrWhiteSpace(_screenTextContent)
            ? "屏幕文字预览（当前场景无对白）"
            : _screenTextContent;

        const float anchorWidth = 8f;
        SetRect(
            _screenTextAnchorRect,
            anchor.X - _viewportRect.X - (anchorWidth / 2f),
            absoluteY - _viewportRect.Y,
            anchorWidth,
            previewHeight);
        if (_screenTextAnchorImage is not null)
        {
            _screenTextAnchorImage.color = sequenceColor;
        }
        _screenTextHitRect = new VisualEditorRect(
            absoluteX,
            absoluteY,
            Math.Max(previewWidth, 120f),
            Math.Max(previewHeight, 44f));
    }

    private static void SetCameraLines(
        IReadOnlyList<RectTransform?> lines,
        float x,
        float y,
        float width,
        float height,
        float thickness)
    {
        SetRect(lines[0], x, y, width, thickness);
        SetRect(lines[1], x, y + height - thickness, width, thickness);
        SetRect(lines[2], x, y, thickness, height);
        SetRect(lines[3], x + width - thickness, y, thickness, height);
    }

    private void LayoutControl(
        string key,
        float x,
        float y,
        float width,
        float height)
    {
        if (!_controlButtons.TryGetValue(key, out ControlButton? button))
        {
            return;
        }

        SetRect(button.Rect, x, y, width, height);
        Rukari.Lib.Runtime.Tools.ToolTheme.CenterButtonLabel(button.Label, width, height, 6f);
        button.HitRect = new VisualEditorRect(
            _panelRect.X + x,
            _panelRect.Y + y,
            width,
            height);
    }

    private void UpdateButtons()
    {
        bool screenTextApplyEnabled = _mode == VisualEditorMode.ScreenText
            && _draftSourceDocument?.InputMatchesScript == true
            && _documentSnapshot != null
            && _draftSourceDocument.RuntimeSelectionKey == _documentSnapshot.RuntimeSelectionKey
            && (_screenTextDraft != null
                || _screenTextRemoveDraft
                || _screenTextClearAllDraft);
        bool applyEnabled = _expanded
            && (screenTextApplyEnabled
                || (_verifiedPreview != null
                    && _documentSnapshot != null
                    && _verifiedPreview.Source.RuntimeSelectionKey == _documentSnapshot.RuntimeSelectionKey
                    && !string.IsNullOrEmpty(_currentDraftDirective)));
        bool undoEnabled = _expanded
            && _documentSnapshot?.UndoAvailable == true
            && _documentSnapshot.InputMatchesScript;
        if (_applyButtonImage is not null && _applyButtonRect is not null)
        {
            Color text = Rukari.Lib.Runtime.Tools.ToolTheme.ApplyButton(_applyButtonImage,
                _applyButtonRect.sizeDelta.x, _applyButtonRect.sizeDelta.y,
                applyEnabled, false, false, decorated: true, primary: true);
            if (_applyButtonText is not null) _applyButtonText.color = text;
        }

        if (_undoButtonImage is not null && _undoButtonRect is not null)
        {
            Color text = Rukari.Lib.Runtime.Tools.ToolTheme.ApplyButton(_undoButtonImage,
                _undoButtonRect.sizeDelta.x, _undoButtonRect.sizeDelta.y,
                undoEnabled, false, false, decorated: true, primary: false);
            if (_undoButtonText is not null) _undoButtonText.color = text;
        }
    }

    private void LayoutSlots()
    {
        if (!_expanded
            || _mode != VisualEditorMode.Character
            || _storyHeight <= 0f)
        {
            return;
        }

        for (int index = 0; index < _slotVisuals.Length; index++)
        {
            SlotVisual? visual = _slotVisuals[index];
            if (visual == null)
            {
                continue;
            }

            CharacterSlotSnapshot? snapshot = _slotSnapshots[index];
            bool occupied = snapshot?.Occupied == true && snapshot.State != null;
            VisualEditorPoint localPosition = index == _selectedSlotIndex
                ? SelectedPosition(index)
                : _draftPositions[index]
                    ?? (occupied
                        ? new VisualEditorPoint(
                            snapshot!.State!.Position.X,
                            snapshot.State.Position.Y)
                        : DefaultPendingPosition());
            VisualEditorPoint storyPosition = SlotLocalToStory(index, localPosition);
            var mapped = VisualEditorCanvasMath.StoryToViewport(
                storyPosition,
                _storyHeight,
                _viewportRect);
            if (!mapped.Success)
            {
                continue;
            }

            VisualEditorPoint center = mapped.Value;
            float width = occupied
                ? OccupiedStoryWidth
                    / (VisualEditorCanvasMath.StoryWidth
                        * VisualEditorCanvasMath.ViewportZoomOutFactor)
                    * _viewportRect.Width
                : 58f;
            float height = occupied
                ? OccupiedStoryHeight
                    / (_storyHeight * VisualEditorCanvasMath.ViewportZoomOutFactor)
                    * _viewportRect.Height
                : 58f;
            float localX = center.X - _viewportRect.X - (width / 2f);
            float localY = center.Y - _viewportRect.Y - (height / 2f);
            SetRect(visual.Root, localX, localY, width, height);
            float rotation = index == _selectedSlotIndex
                ? SelectedRotation()
                : _draftRotations[index]
                    ?? NormalizeSigned(snapshot?.State?.LocalEulerAngles.Z ?? 0f);
            visual.Root.localEulerAngles = new Vector3(0f, 0f, rotation);
            visual.Center = center;
            visual.HitWidth = occupied
                ? Math.Max(width, OccupiedMinimumHitWidth)
                : Math.Max(width, 58f);
            visual.HitHeight = occupied
                ? Math.Max(height, OccupiedMinimumHitHeight)
                : Math.Max(height, 58f);
            visual.RotationDegrees = rotation;

            bool selected = _selectedSlotIndex == index;
            Color color = selected
                ? SelectedColor
                : occupied ? OccupiedColor : PendingColor;
            visual.SetAppearance(occupied, color);
            bool flip = index == _selectedSlotIndex
                ? SelectedFlip()
                : _draftFlips[index]
                    ?? CharacterScreenRotation.IsHorizontallyFlipped(
                        snapshot?.State?.LocalEulerAngles.Y ?? 0f);
            visual.Label.text = occupied
                ? $"#{index + 1}{(flip ? " F" : string.Empty)}\n{ShortName(snapshot!.OccupantIdentifier)}"
                : $"#{index + 1}{(flip ? " F" : string.Empty)}";
        }
    }

    private void CreateSlotVisual(int slotIndex, RectTransform parent)
    {
        int publicSlot = slotIndex + 1;
        RectTransform root = CreateRect($"Slot{publicSlot}", parent);
        RectTransform[] borders = new RectTransform[4];
        for (int i = 0; i < borders.Length; i++)
        {
            borders[i] = CreateImage($"Border{i}", root, OccupiedColor, rounded: false).Rect;
        }

        RectTransform horizontal = CreateImage("CrossHorizontal", root, PendingColor, rounded: false).Rect;
        RectTransform vertical = CreateImage("CrossVertical", root, PendingColor, rounded: false).Rect;
        Text label = CreateText(
            "Label",
            root,
            $"#{publicSlot}",
            20,
            TextAnchor.MiddleCenter).Text;
        _slotVisuals[slotIndex] =
            new SlotVisual(root, borders, horizontal, vertical, label);
    }

    private void CreateSlotSelectorButton(int slotIndex)
    {
        if (_panelObject is null)
        {
            return;
        }

        int publicSlot = slotIndex + 1;
        UiElement element = CreateImage(
            $"SlotSelector{publicSlot}",
            _panelObject.transform,
            ControlEnabledColor);
        Image image = element.GameObject.GetComponent<Image>();
        Text label = CreateText(
            "Label",
            element.GameObject.transform,
            $"槽位 {publicSlot}",
            18,
            TextAnchor.MiddleCenter).Text;
        _slotSelectorButtons[slotIndex] =
            new SlotSelectorButton(element.Rect, image, label);
    }

    private static VisualEditorPoint DefaultPendingPosition() => new(0f, 0f);

    private static VisualEditorPoint SlotLocalToStory(
        int slotIndex,
        VisualEditorPoint localPosition)
    {
        Result<VisualEditorPoint> result = VisualEditorCanvasMath.SlotLocalToStory(
            slotIndex + 1,
            localPosition);
        return result.Success ? result.Value : localPosition;
    }

    private int HitTestSlot(VisualEditorPoint pointer)
    {
        if (!Contains(_viewportRect, pointer))
        {
            return -1;
        }

        int nearestIndex = -1;
        float nearestDistanceSquared = float.PositiveInfinity;
        for (int index = 0; index < _slotVisuals.Length; index++)
        {
            SlotVisual? visual = _slotVisuals[index];
            if (visual == null
                || !VisualEditorCanvasMath.ContainsRotatedRect(
                    pointer,
                    visual.Center,
                    visual.HitWidth,
                    visual.HitHeight,
                    visual.RotationDegrees))
            {
                continue;
            }

            float deltaX = pointer.X - visual.Center.X;
            float deltaY = pointer.Y - visual.Center.Y;
            float distanceSquared = (deltaX * deltaX) + (deltaY * deltaY);
            if (distanceSquared < nearestDistanceSquared)
            {
                nearestIndex = index;
                nearestDistanceSquared = distanceSquared;
            }
        }

        return nearestIndex;
    }

    private VisualEditorPoint PointerInCanvas()
    {
        Vector3 pointer = Input.mousePosition;
        return new VisualEditorPoint(pointer.x / _canvasScale, pointer.y / _canvasScale);
    }

    private static bool Contains(VisualEditorRect rect, VisualEditorPoint point) =>
        rect.IsValid
        && point.IsFinite
        && point.X >= rect.X
        && point.X <= rect.X + rect.Width
        && point.Y >= rect.Y
        && point.Y <= rect.Y + rect.Height;

    private static VisualEditorRect RectOf(RectTransform rect)
    {
        Vector2 position = rect.anchoredPosition;
        Vector2 size = rect.sizeDelta;
        return new VisualEditorRect(position.x, position.y, size.x, size.y);
    }

    private static string ShortName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "CHAR";
        }

        return value.Length <= 12 ? value : value[..12];
    }

    private void SetCanvasActive(bool active)
    {
        _canvasActive = active;
        if (!active)
        {
            CancelPointerInteraction();
            VisualEditorInputGuard.Clear();
        }
        GameObject? canvasObject = _canvasObject;
        if (canvasObject is not null)
        {
            canvasObject.SetActive(active);
        }

        if (active) PublishInputRegion();
    }

    private void PublishInputRegion()
    {
        // The shared panel publishes the panel rectangle — it drew it, so it owns it. This lease is kept for the
        // one region the library cannot know about: a drag that wanders off the panel has to keep consuming the
        // pointer, or releasing it over the official editor would click whatever is underneath.
        //
        // The panel rectangle is still published as well. It is inside the region the toolbox already covers, so it
        // is redundant rather than competing, and it keeps the editor's own area protected even on the frames where
        // the renderer has replaced its snapshot with the fail-closed strip.
        VisualEditorInputGuard.Publish(new VisualEditorInputRegion(
            Visible: _canvasActive && _editorPreviewVisible && !_creationFailed && _expanded,
            Expanded: _expanded,
            Dragging: _draggingSlotIndex >= 0
                || _rotationDragging
                || _cameraDragging
                || _cameraZoomDragging
                || _screenTextDragging,
            CanvasScale: _canvasScale,
            Panel: _panelRect,
            Handle: default));
    }

    private void SetFrameLine(int index, float x, float y, float width, float height)
    {
        RectTransform? line = _frameLines[index];
        if (!ReferenceEquals(line, null))
        {
            SetRect(line, x, y, width, height);
        }
    }

    /// <summary>Runtime-generated white rounded-rect sprite shared by every
    /// card-like element; Image.color tints it. 9-slice borders keep corners
    /// crisp at any button size.</summary>
    private static Sprite RoundedSprite
    {
        get
        {
            if (_roundedSprite is not null)
            {
                return _roundedSprite;
            }

            const int size = 64;
            const float corner = 5f;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float clampedX = Math.Clamp(x, corner, size - corner);
                    float clampedY = Math.Clamp(y, corner, size - corner);
                    float dx = x - clampedX;
                    float dy = y - clampedY;
                    bool inside = (dx * dx) + (dy * dy) <= corner * corner;
                    texture.SetPixel(
                        x,
                        y,
                        inside ? Color.white : Color.clear);
                }
            }

            texture.Apply();
            _roundedSprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, size, size),
                new Vector2(0.5f, 0.5f),
                100f,
                0,
                SpriteMeshType.FullRect,
                new Vector4(corner, corner, corner, corner));
            return _roundedSprite;
        }
    }

    private UiElement CreateImage(
        string name,
        unitycore::UnityEngine.Transform parent,
        Color color,
        bool rounded = true)
    {
        GameObject gameObject = new(name);
        RectTransform rect = gameObject.AddComponent<RectTransform>();
        rect.SetParent(parent, false);
        Image image = gameObject.AddComponent<Image>();
        if (rounded)
        {
            image.sprite = RoundedSprite;
            image.type = Image.Type.Sliced;
        }

        image.color = color;
        image.raycastTarget = false;
        return new UiElement(gameObject, rect);
    }

    private static Color NativeColor(ToolColor color) => new(color.R, color.G, color.B, 1f);

    private void CreateControlButton(
        string key,
        string label,
        Action action,
        Func<bool>? enabled = null,
        Func<bool>? highlighted = null)
    {
        if (_panelObject is null)
        {
            return;
        }

        UiElement element = CreateImage(
            "Control_" + key,
            _panelObject.transform,
            ButtonDisabledColor);
        Image image = element.GameObject.GetComponent<Image>();
        Text text = CreateText(
            "Label",
            element.GameObject.transform,
            label,
            18,
            TextAnchor.MiddleCenter).Text;
        _controlButtons.Add(
            key,
            new ControlButton(
                element.Rect,
                image,
                text,
                action,
                enabled ?? CanEditSelectedSlot,
                highlighted ?? (() => false),
                navigation: key is "mode-character" or "mode-camera" or "mode-screen-text"));
    }

    private TextElement CreateText(
        string name,
        unitycore::UnityEngine.Transform parent,
        string value,
        int fontSize,
        TextAnchor alignment)
    {
        GameObject gameObject = new(name);
        RectTransform rect = gameObject.AddComponent<RectTransform>();
        rect.SetParent(parent, false);
        Text text = gameObject.AddComponent<Text>();
        text.font = _font;
        text.text = value;
        text.fontSize = fontSize;
        text.alignment = alignment;
        text.color = TextColor;
        text.raycastTarget = false;
        text.supportRichText = false;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Truncate;
        return new TextElement(gameObject, rect, text);
    }

    private static RectTransform CreateRect(
        string name,
        unitycore::UnityEngine.Transform parent)
    {
        GameObject gameObject = new(name);
        RectTransform rect = gameObject.AddComponent<RectTransform>();
        rect.SetParent(parent, false);
        return rect;
    }

    private static void SetRect(
        RectTransform? rect,
        float x,
        float y,
        float width,
        float height)
    {
        if (rect is null)
        {
            return;
        }

        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.zero;
        rect.pivot = Vector2.zero;
        rect.anchoredPosition = new Vector2(x, y);
        rect.sizeDelta = new Vector2(width, height);
        rect.localScale = unitycore::UnityEngine.Vector3.one;
    }

    private static Font CreateFont()
    {
        try
        {
            Font? font =
                Font.CreateDynamicFontFromOSFont(
                    "Microsoft YaHei UI",
                    24);
            if (!ReferenceEquals(font, null))
            {
                return font;
            }
        }
        catch
        {
        }

        return Font.GetDefault();
    }

    private sealed record UiElement(GameObject GameObject, RectTransform Rect);

    private sealed record TextElement(
        GameObject GameObject,
        RectTransform Rect,
        Text Text);

    private sealed class ControlButton
    {
        private readonly Image _image;
        private readonly Action _action;
        private readonly Func<bool> _enabled;
        private readonly Func<bool> _highlighted;
        private readonly bool _navigation;

        public ControlButton(
            RectTransform rect,
            Image image,
            Text label,
            Action action,
            Func<bool> enabled,
            Func<bool> highlighted,
            bool navigation)
        {
            Rect = rect;
            _image = image;
            Label = label;
            _action = action;
            _enabled = enabled;
            _highlighted = highlighted;
            _navigation = navigation;
        }

        public RectTransform Rect { get; }
        public Text Label { get; }
        public VisualEditorRect HitRect { get; set; }
        public bool Visible => Rect.gameObject.activeInHierarchy;

        public bool Enabled() => _enabled();

        public void SetVisible(bool visible) => Rect.gameObject.SetActive(visible);

        public void Invoke()
        {
            if (_enabled())
            {
                _action();
            }
        }

        public void RefreshAppearance()
        {
            bool enabled = _enabled();
            bool selected = _highlighted();
            Label.color = Rukari.Lib.Runtime.Tools.ToolTheme.ApplyButton(_image,
                Rect.sizeDelta.x, Rect.sizeDelta.y, enabled, selected,
                hovered: false, pressed: false, decorated: false, primary: false, navigation: _navigation);
            Label.fontStyle = _navigation && selected ? FontStyle.Bold : FontStyle.Normal;
        }
    }

    private enum VisualEditorMode
    {
        Character = 0,
        Camera = 1,
        ScreenText = 2,
    }

    private sealed class SlotSelectorButton
    {
        private readonly Image _image;

        public SlotSelectorButton(
            RectTransform rect,
            Image image,
            Text label)
        {
            Rect = rect;
            _image = image;
            Label = label;
        }

        public RectTransform Rect { get; }
        public Text Label { get; }
        public VisualEditorRect HitRect { get; set; }

        public void RefreshAppearance(bool selected, bool occupied)
        {
            _image.color = selected
                ? ControlActiveColor
                : occupied ? SlotSelectorOccupiedColor : ControlEnabledColor;
            Label.color = ControlEnabledTextColor;
        }
    }

    private sealed class SlotVisual
    {
        public SlotVisual(
            RectTransform root,
            RectTransform[] borders,
            RectTransform horizontal,
            RectTransform vertical,
            Text label)
        {
            Root = root;
            Borders = borders;
            Horizontal = horizontal;
            Vertical = vertical;
            Label = label;
        }

        public RectTransform Root { get; }
        public RectTransform[] Borders { get; }
        public RectTransform Horizontal { get; }
        public RectTransform Vertical { get; }
        public Text Label { get; }
        public VisualEditorPoint Center { get; set; }
        public float HitWidth { get; set; }
        public float HitHeight { get; set; }
        public float RotationDegrees { get; set; }

        public void SetAppearance(bool occupied, Color color)
        {
            float width = Root.sizeDelta.x;
            float height = Root.sizeDelta.y;
            SetRect(Borders[0], 0f, 0f, width, 4f);
            SetRect(Borders[1], 0f, height - 4f, width, 4f);
            SetRect(Borders[2], 0f, 0f, 4f, height);
            SetRect(Borders[3], width - 4f, 0f, 4f, height);
            foreach (RectTransform border in Borders)
            {
                border.gameObject.SetActive(occupied);
                border.GetComponent<Image>().color = color;
            }

            SetRect(Horizontal, 0f, (height / 2f) - 2f, width, 4f);
            SetRect(Vertical, (width / 2f) - 2f, 0f, 4f, height);
            Horizontal.gameObject.SetActive(!occupied);
            Vertical.gameObject.SetActive(!occupied);
            Horizontal.GetComponent<Image>().color = color;
            Vertical.GetComponent<Image>().color = color;
            SetRect(Label.GetComponent<RectTransform>(), 0f, 0f, width, height);
            Label.color = color;
        }
    }
}
