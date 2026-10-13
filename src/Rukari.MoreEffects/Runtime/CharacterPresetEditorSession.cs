using System.Diagnostics;
using System.Globalization;
using AzureArchive.VideoTools.Api;
using AzureArchive.VideoTools.Core.Characters;
using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.VisualEditor;
using AzureArchive.VideoTools.Interop;

namespace AzureArchive.VideoTools.Runtime;

/// <summary>Managed draft shared by the four parameter sheets. Writes always use the displayed token and revision.</summary>
internal sealed class CharacterPresetEditorSession
{
    internal static readonly CharacterPresetKind[] EditableKinds =
    {
        CharacterPresetKind.Sway, CharacterPresetKind.Spin,
        CharacterPresetKind.Headbutt, CharacterPresetKind.Squash
    };

    private readonly CharacterPresetDirectiveParser _parser = new();
    private readonly CharacterPresetCommandFamilyCompiler _compiler = new();
    private readonly VisualEditorSlotPreference _slotPreference = new();
    private long _lastRead;
    private EditorCommandDocumentSnapshot? _source;
    private int _direction = 1;
    private CharacterPresetSpinAxis _spinAxis = CharacterPresetSpinAxis.Y;
    private int _finiteCycles = 3;
    private bool _loop;
    private bool _dirty;
    private bool _bezierEnabled;
    private bool _customBezier;
    private int _lastGraphicalSlot;
    private string _readError = string.Empty;

    internal CharacterPresetKind Kind { get; private set; }
    internal int PublicSlot { get; private set; } = 1;
    internal List<PresetNumberDraft> Numbers { get; } = new();
    // Curve coordinates never enter the action-number loop: they form one bezier directive value.
    internal List<PresetNumberDraft> CurveNumbers { get; } = new();
    internal bool UsesBezier => _bezierEnabled;
    internal string CurveName
    {
        get
        {
            if (!_bezierEnabled) return "默认动作";
            if (_customBezier || !TryBezier(out CharacterPresetBezier? curve, out _)) return "自定义";
            return curve == CharacterPresetBezier.Linear ? "线性"
                : curve == CharacterPresetBezier.EaseIn ? "缓入"
                : curve == CharacterPresetBezier.EaseOut ? "缓出"
                : curve == CharacterPresetBezier.EaseInOut ? "缓入缓出" : "自定义";
        }
    }
    internal CharacterPresetCommand? Existing { get; private set; }
    internal string Notice { get; private set; } = string.Empty;
    internal int Generation { get; private set; }
    internal bool Loop => _loop;
    internal int Direction => _direction;
    internal CharacterPresetSpinAxis SpinAxis => _spinAxis;
    internal bool Editable => _source?.InputAvailable == true && _source.InputMatchesScript
        && !string.IsNullOrEmpty(_source.RuntimeSelectionKey);
    internal bool CanUndo => Editable && _source?.UndoAvailable == true;
    internal string Dialogue => _source == null ? "请在官方工作台选中一句台词。"
        : string.IsNullOrWhiteSpace(_source.DialogueText) ? "（当前为空台词 / 动作行）"
        : Shorten(_source.DialogueText, 48);
    internal string CurrentEffect => Existing == null ? "本槽尚未设置动作"
        : "本槽已设置：" + Title(Existing.Kind, Existing.SpinAxis);

    internal void Show(CharacterPresetKind kind)
    {
        Kind = kind;
        Read(force: true);
        int graphicalSlot = _source == null ? 0
            : VisualEditorBehaviour.PresetSlotForSelection(_source.RuntimeSelectionKey);
        if (graphicalSlot is >= 1 and <= 5 && graphicalSlot != _lastGraphicalSlot) PublicSlot = graphicalSlot;
        _lastGraphicalSlot = graphicalSlot;
        _slotPreference.Remember(_source?.RuntimeContextId, PublicSlot);
        LoadFromSource();
        Notice = Existing != null && Existing.Kind != Kind
            ? "应用后替换本槽原有动作。" : string.Empty;
    }

    internal void Hide()
    {
        // Closing or changing leaves abandons unfinished numeric input as well as any prior preview authority.
        LoadFromSource();
    }

    internal void Poll() => Read(force: false);

    /// <summary>
    /// The layer this page owns: one character's transient presets. The stack is created for the slot the page is
    /// currently targeting and recreated when the target changes, because a layer is identified by its slot
    /// (2026-10-13).
    /// </summary>
    private EffectLayerStack? _layerStack;

    /// <summary>The layer stack of the targeted character's preset layer.</summary>
    internal EffectLayerStack Layers =>
        _layerStack ??= new EffectLayerStack(new[] { EffectLayerCatalog.Preset(PublicSlot) });

    /// <summary>The one line this page shows about the layer it owns, draft included.</summary>
    internal string LayerLine => Layers.LineOf(EffectLayerCatalog.Preset(PublicSlot));

    /// <summary>Whether the stack on screen differs from the row it was read from.</summary>
    internal bool HasPendingLayerChanges => Layers.HasPendingChanges;

    /// <summary>Whether this layer holds anything at all.</summary>
    internal bool HasSegments => Layers.Rows.Count != 0;

    /// <summary>What the segment button reads: which effect the buttons act on, out of how many.</summary>
    internal string SegmentLabel
    {
        get
        {
            EffectLayer layer = EffectLayerCatalog.Preset(PublicSlot);
            int total = Layers.CountOf(layer);
            int selected = string.Equals(
                    Layers.SelectedLayer.ResourceKey,
                    layer.ResourceKey,
                    StringComparison.Ordinal)
                ? Layers.SelectedIndex
                : -1;
            return $"预设段 {Math.Max(0, selected + 1)}/{total}";
        }
    }

    /// <summary>What the delete button reads, naming the effect it would remove.</summary>
    internal string DeleteLabel =>
        Layers.SelectedIndex >= 0 ? $"删除第 {Layers.SelectedIndex + 1} 段" : "删除这段";

    /// <summary>Whether the selected effect can move one place within this layer.</summary>
    internal bool CanMoveSegment(int delta)
    {
        int index = Layers.SelectedIndex;
        return index >= 0
            && index + delta >= 0
            && index + delta < Layers.CountOf(EffectLayerCatalog.Preset(PublicSlot));
    }

    /// <summary>
    /// Takes the row's preset layer as the truth, unless the author has stacked something that is not written yet: a
    /// re-read would silently replace the work being composed.
    /// </summary>
    private void AdoptLayers()
    {
        if (Layers.HasPendingChanges) return;
        if (_source == null)
        {
            Layers.Adopt(EffectLayerRead.Failed(
                _readError.Length != 0 ? _readError : "当前台词不可用，请重新选中台词。"));
            return;
        }

        EffectLayer layer = EffectLayerCatalog.Preset(PublicSlot);
        Layers.Adopt(EffectLayerRead.From(
            new[] { layer },
            _source.AdditionalPrompt,
            _source.RevisionSha256,
            _source.RuntimeSelectionKey,
            RowLabelOf(_source.RuntimeContextId)));
    }

    /// <summary>Appends the draft the parameters describe to this layer, as a pending change.</summary>
    internal void AddCurrentAsSegment()
    {
        if (!Editable) return;
        if (!TryDirective(out string directive, out string error))
        {
            Notice = error;
            return;
        }

        Layers.Add(EffectDraft.FromDirective(directive));
        Notice = Layers.Status;
    }

    /// <summary>Moves the selection to the next effect of this layer.</summary>
    internal void CycleSegment()
    {
        EffectLayer layer = EffectLayerCatalog.Preset(PublicSlot);
        int total = Layers.CountOf(layer);
        if (total == 0)
        {
            Notice = $"{layer.Label}还没有效果。";
            return;
        }

        int next = Layers.SelectedIndex < 0 ? 0 : (Layers.SelectedIndex + 1) % total;
        Layers.Select(layer, next);
        Notice = Layers.HasPendingChanges
            ? $"{layer.Label}第 {next + 1} 段：{Layers.Selected?.Label}"
            : Layers.Status;
    }

    /// <summary>Moves the selected effect one place within this layer.</summary>
    internal void MoveSegment(int delta)
    {
        Layers.MoveSelected(delta);
        Notice = Layers.Status;
    }

    /// <summary>Removes the selected effect of this layer.</summary>
    internal void DeleteSegment()
    {
        Layers.DeleteSelected();
        Notice = Layers.Status;
    }

    /// <summary>Empties this layer, as a pending change: 应用 is still what writes it.</summary>
    internal void ClearLayer()
    {
        Layers.Clear(EffectLayerCatalog.Preset(PublicSlot));
        Notice = Layers.Status;
    }

    /// <summary>
    /// Writes the whole layer in one transaction: what this page owns, in the order the author stacked it. This is
    /// the preset family's equivalent of the camera window's write, and the reason the one-command-per-resource
    /// upsert is no longer used here at all (2026-10-13).
    /// </summary>
    internal void ApplyLayer()
    {
        EditorCommandDocumentSnapshot? displayed = _source;
        if (!GuardDisplayedSource(displayed)) return;
        if (!Layers.HasPendingChanges)
        {
            Notice = "没有需要写入的改动。";
            return;
        }

        if (!Layers.CanWrite)
        {
            Notice = "这一句还没有读取成功，请重新选中台词后再试。";
            return;
        }

        if (Plugin.Api.EditorCommandDocuments is not EditorCommandDocumentService service)
        {
            Notice = "当前编辑服务暂不支持整层写入。";
            return;
        }

        EffectLayerApply applied = service.ApplyLayersOnMainThread(
            displayed!.RevisionSha256,
            displayed.RuntimeSelectionKey,
            Layers.BuildEdits());
        if (!applied.Success)
        {
            ReportFailure("应用失败，请重新选择当前句后重试。", applied.Error);
            return;
        }

        Layers.Adopt(applied.Read, "已写入当前句；" + Layers.Consequence());
        Read(force: true);
        Notice = Layers.Status;
    }

    /// <summary>
    /// A short name for the row, taken from the transaction's context identity. It ends in the row's index, which is
    /// the one piece of identity the shared read offers.
    /// </summary>
    private static string RowLabelOf(string? contextId)
    {
        if (string.IsNullOrWhiteSpace(contextId)) return "台词";
        int colon = contextId.LastIndexOf(':');
        if (colon <= 0 || colon + 1 >= contextId.Length) return "台词";
        return int.TryParse(contextId[(colon + 1)..], out int index) ? $"台词 #{index + 1}" : "台词";
    }
    private void Read(bool force)
    {
        long now = Stopwatch.GetTimestamp();
        if (!force && now - _lastRead < Stopwatch.Frequency / 4) return;
        _lastRead = now;
        ApiResult<EditorCommandDocumentSnapshot> read = Plugin.Api.EditorCommandDocuments.ReadSelectedOnMainThread();
        if (!read.Success || read.Value == null || string.IsNullOrEmpty(read.Value.RuntimeSelectionKey))
        {
            if (_source != null) { _source = null; LoadFromSource(); }
            _readError = "当前台词不可用，请重新选中台词。";
            return;
        }

        EditorCommandDocumentSnapshot next = read.Value;
        bool selectionChanged = _source == null
            || !string.Equals(_source.RuntimeSelectionKey, next.RuntimeSelectionKey, StringComparison.Ordinal);
        bool revisionChanged = _source != null
            && !string.Equals(_source.RevisionSha256, next.RevisionSha256, StringComparison.OrdinalIgnoreCase);
        bool lostSync = _source?.InputMatchesScript == true && !next.InputMatchesScript;
        _source = next;
        _readError = next.InputAvailable && next.InputMatchesScript ? string.Empty
            : "官方输入尚未同步，请先结束当前编辑。";
        if (!selectionChanged && !revisionChanged && !lostSync) return;
        bool hadDraft = _dirty;
        if (selectionChanged)
        {
            int graphicalSlot = VisualEditorBehaviour.PresetSlotForSelection(next.RuntimeSelectionKey);
            int rememberedSlot = _slotPreference.Restore(next.RuntimeContextId);
            PublicSlot = rememberedSlot is >= 1 and <= 5 ? rememberedSlot
                : graphicalSlot is >= 1 and <= 5 ? graphicalSlot : 1;
            _lastGraphicalSlot = graphicalSlot;
            _slotPreference.Remember(next.RuntimeContextId, PublicSlot);
        }
        LoadFromSource();
        Notice = hadDraft ? "台词或内容已变化，旧草稿已清除。" : string.Empty;
    }

    internal void SelectSlot(int slot)
    {
        if (!Editable || slot is < 1 or > 5 || slot == PublicSlot) return;
        PublicSlot = slot;
        _slotPreference.Remember(_source!.RuntimeContextId, slot);
        _layerStack = null;
        LoadFromSource();
        Notice = Existing != null && Existing.Kind != Kind
            ? "应用后替换本槽原有动作。" : string.Empty;
    }

    private void LoadFromSource()
    {
        Existing = null;
        if (_source != null)
        {
            foreach (string line in _source.CanonicalDirectives)
            {
                string canonical = ToCanonical(line);
                if (!canonical.StartsWith("#fx;", StringComparison.OrdinalIgnoreCase)) continue;
                var parsed = _parser.Parse(canonical);
                if (parsed.Success && parsed.Value?.PublicSlot == PublicSlot)
                {
                    Existing = parsed.Value;
                    break;
                }
            }
        }
        CharacterPresetCommand command = Existing?.Kind == Kind ? Existing : Defaults();
        SetNumbers(command);
        _dirty = false;
        Generation++;
        AdoptLayers();
    }

    private CharacterPresetCommand Defaults() => _parser.Parse(
        "#fx;" + PublicSlot.ToString(CultureInfo.InvariantCulture) + ";" + Kind.ToString().ToLowerInvariant()).Value!;

    internal void ResetDefaults()
    {
        if (!Editable) return;
        SetNumbers(Defaults());
        Changed();
        Generation++;
    }

    private void SetNumbers(CharacterPresetCommand command)
    {
        Numbers.Clear();
        LoadBezier(command.Bezier);
        _direction = command.Direction;
        _spinAxis = command.SpinAxis;
        _loop = command.Cycles == 0;
        _finiteCycles = command.Cycles > 0 ? command.Cycles : Kind == CharacterPresetKind.Spin ? 2 : 3;
        if (Kind == CharacterPresetKind.Headbutt)
        {
            Numbers.Add(new("back", "后仰角度（°）", command.BackDegrees, 0, 90, 5));
            Numbers.Add(new("forward", "前倾角度（°）", command.ForwardDegrees, 0, 120, 5));
            Numbers.Add(new("duration", "时长（毫秒）", command.DurationMilliseconds, 100, 10000, 50, true));
        }
        else
        {
            if (Kind != CharacterPresetKind.Spin)
                Numbers.Add(Kind == CharacterPresetKind.Sway
                    ? new("amplitude", "摆动幅度（°）", command.Amplitude, 0, 90, 1)
                    : new("amplitude", "拉伸幅度", command.Amplitude, 0, .7f, .05f));
            Numbers.Add(new("frequency", Kind == CharacterPresetKind.Spin ? "频率（圈/秒）" : "频率（次/秒）",
                command.FrequencyHz, .1f,
                Kind == CharacterPresetKind.Spin ? 5 : 10, .1f));
            Numbers.Add(new("cycles", Kind == CharacterPresetKind.Spin ? "转身圈数" : "循环次数",
                _finiteCycles, 1, 100, 1, true));
        }
    }

    private void LoadBezier(CharacterPresetBezier? curve)
    {
        _bezierEnabled = curve.HasValue;
        _customBezier = false;
        CharacterPresetBezier points = curve ?? CharacterPresetBezier.EaseInOut;
        CurveNumbers.Clear();
        AddCurveNumber("x1", "X1", points.X1);
        AddCurveNumber("y1", "Y1", points.Y1);
        AddCurveNumber("x2", "X2", points.X2);
        AddCurveNumber("y2", "Y2", points.Y2);
    }

    private void AddCurveNumber(string key, string label, float value) => CurveNumbers.Add(
        new PresetNumberDraft(key, label, value, 0, 1, .05f)
        { Text = value.ToString("R", CultureInfo.InvariantCulture) });

    internal void SetBezierPreset(CharacterPresetBezier? curve, bool custom)
    {
        if (!Editable) return;
        // Entering custom preserves the current control points; an invalid draft stays visible for correction.
        if (!custom) LoadBezier(curve);
        else _bezierEnabled = true;
        _customBezier = custom;
        Changed();
    }

    internal void SetCurveNumber(PresetNumberDraft number, string text)
    {
        if (!Editable || !_bezierEnabled || !CurveNumbers.Contains(number)) return;
        number.Text = text;
        _customBezier = true;
        Changed();
    }

    internal void StepCurveNumber(PresetNumberDraft number, int direction)
    {
        if (!Editable || !_bezierEnabled || !CurveNumbers.Contains(number)) return;
        if (!number.TryValue(out float value)) value = number.Minimum;
        SetCurveNumber(number, Math.Clamp(value + direction * number.Step, 0f, 1f)
            .ToString("0.###", CultureInfo.InvariantCulture));
    }

    internal void MoveCurvePoint(int point, float x, float y)
    {
        if (!Editable || !_bezierEnabled || point is < 0 or > 1) return;
        SetCurveNumber(CurveNumbers[point * 2], Math.Clamp(x, 0f, 1f).ToString("0.###", CultureInfo.InvariantCulture));
        SetCurveNumber(CurveNumbers[point * 2 + 1], Math.Clamp(y, 0f, 1f).ToString("0.###", CultureInfo.InvariantCulture));
    }

    internal bool TryBezier(out CharacterPresetBezier? curve, out string error)
    {
        curve = null;
        error = string.Empty;
        if (!_bezierEnabled) return true;
        if (CurveNumbers.Count != 4 || !CurveNumbers[0].TryValue(out float x1)
            || !CurveNumbers[1].TryValue(out float y1) || !CurveNumbers[2].TryValue(out float x2)
            || !CurveNumbers[3].TryValue(out float y2))
        {
            error = "曲线坐标 X1、Y1、X2、Y2 必须为 0–1 之间的数字。";
            return false;
        }
        curve = new CharacterPresetBezier(x1, y1, x2, y2);
        return true;
    }

    internal void SetNumber(PresetNumberDraft number, string value)
    {
        if (!Editable || !Numbers.Contains(number)) return;
        number.Text = value;
        Changed();
    }

    internal void StepNumber(PresetNumberDraft number, int direction)
    {
        if (!Editable || !Numbers.Contains(number)) return;
        if (!number.TryValue(out float current)) current = number.Minimum;
        float next = Math.Clamp(current + number.Step * direction, number.Minimum, number.Maximum);
        number.Text = next.ToString("0.###", CultureInfo.InvariantCulture);
        Changed();
    }

    internal void SetDirection(int direction)
    {
        if (!Editable) return;
        _direction = direction < 0 ? -1 : 1;
        Changed();
    }

    internal void SetLoop(bool enabled)
    {
        if (!Editable) return;
        _loop = enabled;
        Changed();
    }

    internal void SetSpinAxis(CharacterPresetSpinAxis axis)
    {
        if (!Editable || Kind != CharacterPresetKind.Spin
            || axis is not (CharacterPresetSpinAxis.X or CharacterPresetSpinAxis.Y)) return;
        _spinAxis = axis;
        Changed();
    }

    private void Changed() { _dirty = true; Notice = "设置尚未应用。"; }

    internal bool TryDirective(out string directive, out string error)
    {
        directive = string.Empty;
        error = string.Empty;
        var parts = new List<string> { "#fx", PublicSlot.ToString(CultureInfo.InvariantCulture),
            Kind.ToString().ToLowerInvariant() };
        foreach (PresetNumberDraft number in Numbers)
        {
            if (number.Key == "cycles" && _loop) { parts.Add("cycles=0"); continue; }
            if (!number.TryValue(out float value))
            {
                error = number.Label + "应为" + number.Range + (number.Integer ? "的整数。" : "之间的数字。");
                return false;
            }
            parts.Add(number.Key + "=" + value.ToString("R", CultureInfo.InvariantCulture));
        }
        parts.Add(_direction < 0 ? "direction=left" : "direction=right");
        if (Kind == CharacterPresetKind.Spin && _spinAxis == CharacterPresetSpinAxis.X)
            parts.Add("axis=x");
        if (!TryBezier(out CharacterPresetBezier? curve, out error)) return false;
        if (curve.HasValue) parts.Add("bezier=" + curve.Value.ToDirectiveValue());
        var compiled = _compiler.Canonicalize(string.Join(";", parts));
        if (!compiled.Success || compiled.Value == null)
        {
            error = "参数无效，请检查数值。";
            return false;
        }
        directive = "#aavt;" + compiled.Value.Directive[1..];
        return true;
    }

    internal string Status
    {
        get
        {
            if (!string.IsNullOrEmpty(_readError)) return _readError;
            if (!TryDirective(out _, out string error)) return error;
            return string.IsNullOrEmpty(Notice) ? CurrentEffect : Notice;
        }
    }

    private bool GuardDisplayedSource(EditorCommandDocumentSnapshot? displayed)
    {
        Read(force: true);
        if (displayed == null || !Editable || _source == null) return false;
        if (!string.Equals(displayed.RuntimeSelectionKey, _source.RuntimeSelectionKey, StringComparison.Ordinal)
            || !string.Equals(displayed.RevisionSha256, _source.RevisionSha256, StringComparison.OrdinalIgnoreCase))
        {
            Notice = "台词或内容已变化，请检查新设置后再应用。";
            return false;
        }
        return true;
    }


    /// <summary>
    /// Undoes the last shared transaction, which is this page's own layer write as much as anybody else's: the write
    /// goes through the same transaction, so one undo button covers both kinds of change.
    /// </summary>
    internal void Undo()
    {
        EditorCommandDocumentSnapshot? displayed = _source;
        if (!GuardDisplayedSource(displayed) || !CanUndo) return;
        var result = Plugin.Api.EditorCommandDocuments.UndoSelectedOnMainThread(displayed!.RuntimeSelectionKey);
        if (!result.Success || result.Value == null)
        {
            ReportFailure("撤销失败，请重新选择当前句后重试。", result.Error);
            return;
        }
        _source = result.Value.Restored;
        LoadFromSource();
        Notice = "已撤销当前句的上次修改。";
    }

    private void ReportFailure(string message, string error)
    {
        Plugin.Logger.LogWarning("[character-preset-ui] " + message + " " + error);
        Read(force: true);
        Notice = message;
    }

    internal static string Title(CharacterPresetKind kind) => kind switch
    {
        CharacterPresetKind.Sway => "左右摇晃",
        CharacterPresetKind.Spin => "原地转身",
        CharacterPresetKind.Headbutt => "头槌",
        CharacterPresetKind.Squash => "弹性拉伸",
        _ => "动作预设"
    };

    internal static string Description(CharacterPresetKind kind) => kind switch
    {
        CharacterPresetKind.Sway => "左右倾斜摇晃，结束后恢复原姿势。",
        CharacterPresetKind.Spin => "可选 X 或 Y 轴旋转，结束后恢复原朝向。",
        CharacterPresetKind.Headbutt => "先后仰，再向前顶出，最后恢复原姿势。",
        CharacterPresetKind.Squash => "横向伸长时纵向压扁，反向亦然，结束后恢复。",
        _ => string.Empty
    };

    internal static string Title(CharacterPresetKind kind, CharacterPresetSpinAxis axis) =>
        kind == CharacterPresetKind.Spin ? Title(kind) + (axis == CharacterPresetSpinAxis.X ? " · X 轴" : " · Y 轴")
            : Title(kind);

    internal static string Description(CharacterPresetKind kind, CharacterPresetSpinAxis axis) =>
        kind == CharacterPresetKind.Spin
            ? "绕 " + (axis == CharacterPresetSpinAxis.X ? "X" : "Y") + " 轴原地旋转，结束后恢复原朝向。"
            : Description(kind);

    private static string ToCanonical(string directive) => directive.StartsWith("#aavt;", StringComparison.OrdinalIgnoreCase)
        ? "#" + directive[6..] : directive;

    private static string Shorten(string text, int maximum)
    {
        string single = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return single.Length <= maximum ? single : single[..maximum] + "…";
    }
}

internal sealed class PresetNumberDraft
{
    internal PresetNumberDraft(string key, string label, float value, float minimum, float maximum,
        float step, bool integer = false)
    {
        Key = key; Label = label; Minimum = minimum; Maximum = maximum; Step = step; Integer = integer;
        Text = value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    internal string Key { get; }
    internal string Label { get; }
    internal float Minimum { get; }
    internal float Maximum { get; }
    internal float Step { get; }
    internal bool Integer { get; }
    internal string Text { get; set; }
    internal string Range => Minimum.ToString("0.###", CultureInfo.InvariantCulture) + "–"
        + Maximum.ToString("0.###", CultureInfo.InvariantCulture);
    internal bool TryValue(out float value) => float.TryParse(Text, NumberStyles.Float,
        CultureInfo.InvariantCulture, out value) && float.IsFinite(value) && value >= Minimum && value <= Maximum
        && (!Integer || value == MathF.Truncate(value));
}
