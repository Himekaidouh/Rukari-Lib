using AzureArchive.VideoTools.Core.Characters;
using Rukari.Lib.Captions;
using Rukari.Lib.Tools;

namespace AzureArchive.VideoTools.Runtime;

/// <summary>One small detail sheet. The library owns its frame, skin, pointer capture and keyboard capture.</summary>
internal sealed class CharacterPresetToolContent : IToolPanelContent, IToolPanelSizing, IToolPanelLifecycle
{
    private readonly CharacterPresetEditorSession _session;
    private readonly CharacterPresetKind _kind;
    private readonly CaptionTextEditor _editor = new();
    private readonly CharacterPresetBezierContent _bezier;
    private bool _showBezier;
    private PresetNumberDraft? _focused;
    private string _focusOriginal = string.Empty;
    private int _generation;
    private float _top;

    internal CharacterPresetToolContent(CharacterPresetEditorSession session, CharacterPresetKind kind)
    {
        _session = session;
        _kind = kind;
        _bezier = new CharacterPresetBezierContent(session);
    }

    public float PreferredWidth => 500f;
    public float PreferredHeight => 780f;

    public void OnShown()
    {
        _session.Show(_kind);
        _focused = null;
        _showBezier = false;
        _bezier.ClearInput();
        _generation = _session.Generation;
    }

    public void OnHidden()
    {
        _focused = null;
        _showBezier = false;
        _bezier.ClearInput();
        _session.Hide();
    }

    public void Draw(IToolPanelSurface surface) => Draw(surface, 0f);

    // The group reserves a navigation band in this same surface, preserving its optional input capabilities.
    internal void Draw(IToolPanelSurface surface, float reservedTop)
    {
        _session.Poll();
        if (_generation != _session.Generation)
        {
            _generation = _session.Generation;
            _focused = null;
            _bezier.ClearInput();
        }
        if (!_session.Editable) _focused = null;
        if (_showBezier)
        {
            if (_bezier.Draw(surface, reservedTop))
            {
                _showBezier = false;
                _bezier.ClearInput();
            }
            return;
        }
        var input = surface as IToolPanelSurfaceText;
        if (input != null) ReadKeyboard(input);

        _top = surface.Height - Math.Max(0f, reservedTop);
        surface.Text("preset.selection", "当前台词", Next(surface, 24f), 18);
        surface.Text("preset.dialogue", _session.Dialogue, Next(surface, 42f), 17);
        surface.Text("preset.target", "目标角色 · 槽位 " + _session.PublicSlot,
            Next(surface, 27f), 18);
        ToolInputRect slots = Next(surface, 40f);
        for (int slot = 1; slot <= 5; slot++)
        {
            if (surface.Button("preset.slot." + slot, "槽位 " + slot,
                surface.Cell(slots, slot - 1, 5, 5f), _session.Editable, _session.PublicSlot == slot))
            {
                _focused = null;
                _session.SelectSlot(slot);
                return;
            }
        }
        Gap(9f);
        surface.Status("preset.existing", _session.CurrentEffect, Next(surface, 22f));
        surface.Text("preset.description", CharacterPresetEditorSession.Description(_kind, _session.SpinAxis),
            Next(surface, 34f), 17);
        Gap(5f);

        foreach (PresetNumberDraft number in _session.Numbers)
        {
            bool enabled = _session.Editable && !(number.Key == "cycles" && _session.Loop);
            DrawNumber(surface, input, number, Next(surface, 42f), enabled);
            Gap(6f);
        }

        if (_kind == CharacterPresetKind.Spin)
        {
            ToolInputRect axis = Next(surface, 38f);
            surface.Text("preset.axis.label", "旋转轴", Part(axis, 0, 116), 17);
            ToolInputRect buttons = Part(axis, 122, Math.Max(1, axis.Width - 122));
            if (surface.Button("preset.axis.y", "Y 轴", surface.Cell(buttons, 0, 2),
                _session.Editable, _session.SpinAxis == CharacterPresetSpinAxis.Y))
            {
                _focused = null;
                _session.SetSpinAxis(CharacterPresetSpinAxis.Y);
            }
            if (surface.Button("preset.axis.x", "X 轴", surface.Cell(buttons, 1, 2),
                _session.Editable, _session.SpinAxis == CharacterPresetSpinAxis.X))
            {
                _focused = null;
                _session.SetSpinAxis(CharacterPresetSpinAxis.X);
            }
            Gap(6f);
        }

        if (_kind is CharacterPresetKind.Sway or CharacterPresetKind.Spin or CharacterPresetKind.Headbutt)
        {
            ToolInputRect direction = Next(surface, 38f);
            string label = _kind == CharacterPresetKind.Headbutt ? "前倾方向"
                : _kind == CharacterPresetKind.Spin ? "转身方向" : "动作方向";
            surface.Text("preset.direction.label", label, Part(direction, 0, 116), 17);
            ToolInputRect buttons = Part(direction, 122, Math.Max(1, direction.Width - 122));
            bool pitch = _kind == CharacterPresetKind.Spin && _session.SpinAxis == CharacterPresetSpinAxis.X;
            if (surface.Button("preset.direction.left", pitch ? "反向" : "向左", surface.Cell(buttons, 0, 2),
                _session.Editable, _session.Direction < 0))
            {
                _focused = null;
                _session.SetDirection(-1);
            }
            if (surface.Button("preset.direction.right", pitch ? "正向" : "向右", surface.Cell(buttons, 1, 2),
                _session.Editable, _session.Direction > 0))
            {
                _focused = null;
                _session.SetDirection(1);
            }
            Gap(6f);
        }

        if (_kind != CharacterPresetKind.Headbutt)
        {
            if (surface.Button("preset.loop", "循环到下一句" + (_session.Loop ? "  开" : "  关"),
                Next(surface, 38f), _session.Editable, _session.Loop))
            {
                _focused = null;
                _session.SetLoop(!_session.Loop);
            }
        }

        Gap(6f);
        if (surface.Button("preset.bezier.open", "缓动曲线：" + _session.CurveName,
            Next(surface, 38f), _session.Editable))
        {
            _focused = null;
            _bezier.ClearInput();
            _showBezier = true;
            return;
        }

        // Footer is anchored above a deliberate 24-unit blank strip. Every button uses the shared centered label.
        surface.Status("preset.status", _session.Status, new ToolInputRect(0, 138, surface.Width, 50));
        ToolInputRect tools = new(0, 83, surface.Width, 40);
        if (surface.Button("preset.defaults", "恢复默认参数", surface.Cell(tools, 0, 2), _session.Editable))
        {
            _focused = null;
            _session.ResetDefaults();
            return;
        }
        if (surface.Button("preset.clear", "清除本槽动作", surface.Cell(tools, 1, 2),
            _session.Editable && _session.Existing != null))
        {
            _focused = null;
            _session.ClearPreset();
            return;
        }
        ToolInputRect actions = new(0, 24, surface.Width, 45);
        bool valid = _session.Editable && _session.TryDirective(out _, out _);
        if (Button(surface, "preset.apply", "应用到当前句", surface.Cell(actions, 0, 2),
            ToolSurfaceStyle.Primary, valid))
        {
            _focused = null;
            _session.Apply();
            return;
        }
        if (Button(surface, "preset.undo", "撤销上次修改", surface.Cell(actions, 1, 2),
            ToolSurfaceStyle.Undo, _session.CanUndo))
        {
            _focused = null;
            _session.Undo();
        }
    }

    private void DrawNumber(IToolPanelSurface surface, IToolPanelSurfaceText? input,
        PresetNumberDraft number, ToolInputRect row, bool enabled)
    {
        surface.Text("preset.label." + number.Key, number.Label, Part(row, 0, 116), 17,
            enabled ? ToolSurfaceStyle.Normal : ToolSurfaceStyle.Disabled);
        const float buttonWidth = 34;
        const float gap = 5;
        const float start = 122;
        float valueWidth = Math.Max(30, row.Width - start - 2 * (buttonWidth + gap));
        ToolInputRect minus = Part(row, start, buttonWidth);
        ToolInputRect value = Part(row, start + buttonWidth + gap, valueWidth);
        ToolInputRect plus = Part(row, row.Width - buttonWidth, buttonWidth);
        if (surface.Button("preset.minus." + number.Key, "−", minus, enabled))
        {
            _focused = null;
            _session.StepNumber(number, -1);
        }
        if (input != null && enabled)
        {
            bool focused = ReferenceEquals(_focused, number);
            string display = focused ? _editor.Display(input.Composition) : number.Text;
            if (input.TextField("preset.number." + number.Key, display, value, focused))
            {
                _focused = number;
                _focusOriginal = number.Text;
                _editor.Set(number.Text);
                _editor.SelectAllText();
            }
        }
        else surface.Plate("preset.number." + number.Key, value, ToolSurfaceStyle.Panel,
            number.Key == "cycles" && _session.Loop ? "持续循环" : number.Text);
        if (surface.Button("preset.plus." + number.Key, "+", plus, enabled))
        {
            _focused = null;
            _session.StepNumber(number, 1);
        }
    }

    private void ReadKeyboard(IToolPanelSurfaceText input)
    {
        PresetNumberDraft? focused = _focused;
        if (focused == null) return;
        if (input.Cancelled)
        {
            _session.SetNumber(focused, _focusOriginal);
            _focused = null;
            return;
        }
        string before = _editor.Text;
        if (input.SelectAll) _editor.SelectAllText();
        if (input.MoveLeft) _editor.MoveCaret(-1);
        if (input.MoveRight) _editor.MoveCaret(1);
        if (input.MoveHome) _editor.Home();
        if (input.MoveEnd) _editor.End();
        if (input.Backspace) _editor.Backspace();
        if (input.Delete) _editor.Delete();
        if (!string.IsNullOrEmpty(input.TypedText))
        {
            // Numeric fields have a bounded, invariant-culture buffer; unfinished signs/decimals stay drafts.
            string numeric = new(input.TypedText.Where(c => c is >= '0' and <= '9' or '.' or '-' or '+').ToArray());
            if (_editor.Text.Length + numeric.Length <= 18 || _editor.SelectAll)
                _editor.Insert(numeric.Length <= 18 ? numeric : numeric[..18]);
        }
        if (!string.Equals(before, _editor.Text, StringComparison.Ordinal))
            _session.SetNumber(focused, _editor.Text);
        if (input.Submitted) _focused = null;
    }

    private ToolInputRect Next(IToolPanelSurface surface, float height)
    {
        _top -= height;
        return new ToolInputRect(0, _top, surface.Width, height);
    }

    private void Gap(float height) => _top -= height;
    private static ToolInputRect Part(ToolInputRect row, float x, float width) =>
        new(row.X + x, row.Y, width, row.Height);
    private static bool Button(IToolPanelSurface surface, string id, string label, ToolInputRect bounds,
        ToolSurfaceStyle style, bool enabled) => surface is IToolPanelSurfaceStyledButtons styled
        ? styled.StyledButton(id, label, bounds, style, enabled, false)
        : surface.Button(id, label, bounds, enabled);
}
