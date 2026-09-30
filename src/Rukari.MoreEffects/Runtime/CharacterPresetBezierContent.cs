using AzureArchive.VideoTools.Core.Characters;
using Rukari.Lib.Captions;
using Rukari.Lib.Tools;

namespace AzureArchive.VideoTools.Runtime;

/// <summary>Managed sub-view of one preset draft, with shared keyboard and pointer capture.</summary>
internal sealed class CharacterPresetBezierContent
{
    private readonly CharacterPresetEditorSession _session;
    private readonly CaptionTextEditor _editor = new();
    private PresetNumberDraft? _focused;
    private string _focusOriginal = string.Empty;
    private int _draggedPoint = -1;
    private int _lastDraggedPoint = 1;

    internal CharacterPresetBezierContent(CharacterPresetEditorSession session) => _session = session;

    internal void ClearInput()
    {
        _focused = null;
        _draggedPoint = -1;
    }

    // Returning from this view deliberately does not call Show/Hide: the existing action and curve draft survive.
    internal bool Draw(IToolPanelSurface surface, float reservedTop)
    {
        if (!_session.Editable) ClearInput();
        var input = surface as IToolPanelSurfaceText;
        if (input != null) ReadKeyboard(input);
        float top = surface.Height - Math.Max(0, reservedTop);
        if (surface.Button("preset.curve.back", "返回动作参数", Band(surface, ref top, 38))) return true;
        top -= 6;
        surface.Text("preset.curve.title", "缓动曲线 · " + _session.CurveName, Band(surface, ref top, 24), 18);
        surface.Status("preset.curve.context", "槽位 " + _session.PublicSlot + " · " + _session.Dialogue,
            Band(surface, ref top, 38));
        top -= 6;

        ToolInputRect modes = Band(surface, ref top, 36);
        Mode(surface, "default", "默认动作", surface.Cell(modes, 0, 3), null, false);
        Mode(surface, "linear", "线性", surface.Cell(modes, 1, 3), CharacterPresetBezier.Linear, false);
        Mode(surface, "in", "缓入", surface.Cell(modes, 2, 3), CharacterPresetBezier.EaseIn, false);
        top -= 6;
        modes = Band(surface, ref top, 36);
        Mode(surface, "out", "缓出", surface.Cell(modes, 0, 3), CharacterPresetBezier.EaseOut, false);
        Mode(surface, "inout", "缓入缓出", surface.Cell(modes, 1, 3), CharacterPresetBezier.EaseInOut, false);
        Mode(surface, "custom", "自定义", surface.Cell(modes, 2, 3), null, true);
        top -= 12;

        DrawGraph(surface, Band(surface, ref top, 206));
        top -= 10;
        for (int point = 0; point < 2; point++)
        {
            ToolInputRect row = Band(surface, ref top, 42);
            DrawCoordinate(surface, input, _session.CurveNumbers[point * 2], surface.Cell(row, 0, 2, 10));
            DrawCoordinate(surface, input, _session.CurveNumbers[point * 2 + 1], surface.Cell(row, 1, 2, 10));
            top -= 6;
        }
        surface.Status("preset.curve.explanation",
            "横轴：时间；纵轴：动作进度。拖动控制点或输入 0–1。\n循环每轮套用；头槌的后仰、前顶、复位各自套用。",
            Band(surface, ref top, 48));
        string status = !_session.Editable ? _session.Status : !_session.UsesBezier
            ? "默认动作保留原有节奏，不等于线性。\n返回参数后，点击「应用到当前句」才会保存。"
            : !_session.TryBezier(out _, out string error) ? error
            : "曲线已暂存在当前草稿。\n返回参数后，点击「应用到当前句」才会保存。";
        surface.Status("preset.curve.status", status, new ToolInputRect(0, 24, surface.Width, 72));
        return false;
    }

    private void Mode(IToolPanelSurface surface, string id, string label, ToolInputRect bounds,
        CharacterPresetBezier? curve, bool custom)
    {
        if (!surface.Button("preset.curve.mode." + id, label, bounds, _session.Editable,
            string.Equals(_session.CurveName, label, StringComparison.Ordinal))) return;
        ClearInput();
        _session.SetBezierPreset(curve, custom);
    }

    private void DrawGraph(IToolPanelSurface surface, ToolInputRect band)
    {
        var plot = new ToolInputRect(band.X + 24, band.Y + 24, Math.Max(1, band.Width - 42), band.Height - 38);
        surface.Plate("preset.curve.graph.background", plot, ToolSurfaceStyle.Panel);
        for (int i = 0; i <= 4; i++)
        {
            float ratio = i / 4f;
            Paint(surface, "preset.curve.grid.x" + i, "#BFCBD4", new ToolInputRect(plot.X + plot.Width * ratio, plot.Y, 1, plot.Height));
            Paint(surface, "preset.curve.grid.y" + i, "#BFCBD4", new ToolInputRect(plot.X, plot.Y + plot.Height * ratio, plot.Width, 1));
        }
        surface.Status("preset.curve.axis.zero", "0", new ToolInputRect(0, plot.Y - 22, 22, 22));
        surface.Status("preset.curve.axis.one", "1", new ToolInputRect(plot.X + plot.Width - 16, plot.Y - 22, 22, 22));
        surface.Status("preset.curve.axis.progress", "1", new ToolInputRect(0, plot.Y + plot.Height - 18, 22, 22));

        if (!_session.TryBezier(out CharacterPresetBezier? nullable, out _) || !nullable.HasValue)
        {
            surface.Status("preset.curve.graph.empty", _session.UsesBezier ? "请先修正曲线坐标" : "选择曲线后可查看和拖动控制点",
                new ToolInputRect(plot.X + 12, plot.Y + plot.Height / 2 - 18, plot.Width - 24, 36));
            _draggedPoint = -1;
            return;
        }

        CharacterPresetBezier curve = nullable.Value;
        DrawGuide(surface, "one", "#73CFDD", plot, 0, 0, curve.X1, curve.Y1);
        DrawGuide(surface, "two", "#D9A3BF", plot, 1, 1, curve.X2, curve.Y2);
        // 64 bounded segments form the actual cubic path; all IDs are stable so the renderer reuses its pool.
        float oldX = plot.X, oldY = plot.Y;
        for (int i = 1; i <= 64; i++)
        {
            float t = i / 64f, u = 1 - t;
            float x = plot.X + plot.Width * (3 * u * u * t * curve.X1 + 3 * u * t * t * curve.X2 + t * t * t);
            float y = plot.Y + plot.Height * (3 * u * u * t * curve.Y1 + 3 * u * t * t * curve.Y2 + t * t * t);
            Paint(surface, "preset.curve.path." + i, "#2D9CC9", new ToolInputRect(
                Math.Min(oldX, x) - 1, Math.Min(oldY, y) - 1, Math.Abs(x - oldX) + 2, Math.Abs(y - oldY) + 2));
            oldX = x; oldY = y;
        }
        DrawPoint(surface, "one", "#26BDCE", plot, curve.X1, curve.Y1, "1");
        DrawPoint(surface, "two", "#D264A0", plot, curve.X2, curve.Y2, "2");

        // This is last over the graph's swatches, so the shared host captures the complete drag even outside it.
        const string dragId = "preset.curve.graph.drag";
        bool held = surface.Area(dragId, new ToolInputRect(plot.X - 10, plot.Y - 10,
            plot.Width + 20, plot.Height + 20), _session.Editable);
        ToolPanelPointer pointer = surface.Pointer;
        if (!held) { _draggedPoint = -1; return; }
        if (pointer.Pressed)
        {
            _focused = null;
            float d1 = DistanceSquared(pointer, plot, curve.X1, curve.Y1);
            float d2 = DistanceSquared(pointer, plot, curve.X2, curve.Y2);
            // Overlapping handles remain individually reachable: a tied press alternates between them.
            _draggedPoint = Math.Min(d1, d2) <= 24 * 24
                ? Math.Abs(d1 - d2) < .01f ? 1 - _lastDraggedPoint : d1 <= d2 ? 0 : 1
                : -1;
            if (_draggedPoint >= 0) _lastDraggedPoint = _draggedPoint;
        }
        if (_draggedPoint >= 0)
            _session.MoveCurvePoint(_draggedPoint, (pointer.X - plot.X) / plot.Width, (pointer.Y - plot.Y) / plot.Height);
    }

    private static float DistanceSquared(ToolPanelPointer pointer, ToolInputRect plot, float x, float y)
    {
        float dx = pointer.X - plot.X - x * plot.Width;
        float dy = pointer.Y - plot.Y - y * plot.Height;
        return dx * dx + dy * dy;
    }

    private static void DrawGuide(IToolPanelSurface surface, string id, string colour, ToolInputRect plot,
        float fromX, float fromY, float toX, float toY)
    {
        for (int i = 1; i <= 8; i++)
        {
            float t = i / 9f;
            Paint(surface, "preset.curve.guide." + id + i, colour, new ToolInputRect(
                plot.X + (fromX + (toX - fromX) * t) * plot.Width - 1,
                plot.Y + (fromY + (toY - fromY) * t) * plot.Height - 1, 3, 3));
        }
    }

    private static void DrawPoint(IToolPanelSurface surface, string id, string colour, ToolInputRect plot,
        float x, float y, string label)
    {
        float px = plot.X + x * plot.Width, py = plot.Y + y * plot.Height;
        Paint(surface, "preset.curve.point." + id, colour, new ToolInputRect(px - 6, py - 6, 12, 12));
        surface.Text("preset.curve.point.label." + id, label,
            new ToolInputRect(Math.Clamp(px + 8, plot.X + 3, plot.X + plot.Width - 20),
                Math.Clamp(py - 8, plot.Y + 2, plot.Y + plot.Height - 23), 20, 23), 16);
    }

    private static void Paint(IToolPanelSurface surface, string id, string colour, ToolInputRect bounds)
    {
        if (surface is IToolPanelSurfaceColours colours) colours.Swatch(id, colour, bounds);
        else surface.Plate(id, bounds, ToolSurfaceStyle.Selected);
    }

    private void DrawCoordinate(IToolPanelSurface surface, IToolPanelSurfaceText? input,
        PresetNumberDraft number, ToolInputRect row)
    {
        bool enabled = _session.Editable && _session.UsesBezier;
        string id = "preset.curve.number." + number.Key;
        surface.Text(id + ".label", number.Label, Part(row, 0, 28), 17);
        ToolInputRect minus = Part(row, 32, 26);
        ToolInputRect value = Part(row, 62, Math.Max(30, row.Width - 92));
        ToolInputRect plus = Part(row, row.Width - 26, 26);
        if (surface.Button(id + ".minus", "−", minus, enabled))
        {
            ClearInput();
            _session.StepCurveNumber(number, -1);
        }
        if (input != null && enabled)
        {
            bool focused = ReferenceEquals(_focused, number);
            if (input.TextField(id, focused ? _editor.Display(input.Composition) : number.Text, value, focused))
            {
                _focused = number;
                _focusOriginal = number.Text;
                _editor.Set(number.Text);
                _editor.SelectAllText();
            }
        }
        else surface.Plate(id, value, ToolSurfaceStyle.Panel, _session.UsesBezier ? number.Text : "—");
        if (surface.Button(id + ".plus", "+", plus, enabled))
        {
            ClearInput();
            _session.StepCurveNumber(number, 1);
        }
    }

    private void ReadKeyboard(IToolPanelSurfaceText input)
    {
        PresetNumberDraft? focused = _focused;
        if (focused == null) return;
        if (input.Cancelled)
        {
            _session.SetCurveNumber(focused, _focusOriginal);
            ClearInput();
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
            string numeric = new(input.TypedText.Where(c => c is >= '0' and <= '9' or '.' or '-' or '+' or 'e' or 'E').ToArray());
            if (_editor.Text.Length + numeric.Length <= 18 || _editor.SelectAll)
                _editor.Insert(numeric.Length <= 18 ? numeric : numeric[..18]);
        }
        if (!string.Equals(before, _editor.Text, StringComparison.Ordinal)) _session.SetCurveNumber(focused, _editor.Text);
        if (input.Submitted) _focused = null;
    }

    private static ToolInputRect Band(IToolPanelSurface surface, ref float top, float height)
    {
        top -= height;
        return new ToolInputRect(0, top, surface.Width, height);
    }

    private static ToolInputRect Part(ToolInputRect row, float x, float width) => new(row.X + x, row.Y, width, row.Height);
}
