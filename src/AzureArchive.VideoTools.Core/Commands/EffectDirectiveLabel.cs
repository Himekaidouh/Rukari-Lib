using System.Globalization;
using System.Text;
using AzureArchive.VideoTools.Core.Cameras;
using AzureArchive.VideoTools.Core.Characters;
using AzureArchive.VideoTools.Core.Results;
using Rukari.Lib.Tools;

namespace AzureArchive.VideoTools.Core.Commands;

/// <summary>
/// A short, human label for one effect: what it does, how long it takes, and any authored placement (2026-10-13).
///
/// <para>
/// A layer editor exists so an author can stack effects in order, so a row has to say what the effect <em>is</em>
/// without making the author decode a directive. It is a label, not a second source of truth: everything it prints is
/// read back out of the directive itself, and a directive of a family this build cannot describe shows as itself
/// rather than as a guess.
/// </para>
/// </summary>
public static class EffectDirectiveLabel
{
    /// <summary>Widest label a row gets before it is cut, in panel units.</summary>
    public const float RowLabelWidth = 330f;

    /// <summary>Font size a row label is drawn at; used to measure the cut.</summary>
    public const int RowLabelSize = 15;

    public static string Of(string? canonicalDirective)
    {
        if (string.IsNullOrWhiteSpace(canonicalDirective)) return string.Empty;
        string text = canonicalDirective;
        if (text.StartsWith("#camera;", StringComparison.Ordinal)) return CameraLabel.Of(text);
        if (text.StartsWith(CharacterPresetCommandFamilyCompiler.CanonicalRootToken + ";", StringComparison.Ordinal))
            return PresetLabel.Of(text);
        return Fit(text);
    }

    /// <summary>Whether a label was produced by a family that knows its own directive, for tests and for review.</summary>
    public static bool IsDescribed(string? canonicalDirective)
    {
        if (string.IsNullOrWhiteSpace(canonicalDirective)) return false;
        return canonicalDirective.StartsWith("#camera;", StringComparison.Ordinal)
            || canonicalDirective.StartsWith(
                CharacterPresetCommandFamilyCompiler.CanonicalRootToken + ";",
                StringComparison.Ordinal);
    }

    internal static string Fit(string text) => ToolDrawerLayout.FitSingleLine(text, RowLabelSize, RowLabelWidth);

    /// <summary>The authored queue placement, in the same words on every row that carries one.</summary>
    internal static string Placement(SlotActionPlacement placement)
    {
        if (placement.HasOffset) return $" · at={placement.AtMilliseconds}ms";
        return placement.Interrupt ? " · 接管" : string.Empty;
    }
}

/// <summary>The camera's own label: what the frame does, for how long, and how it eases.</summary>
public static class CameraLabel
{
    public static string Of(string? canonicalDirective)
    {
        if (string.IsNullOrWhiteSpace(canonicalDirective)) return string.Empty;
        Result<SceneCameraCommand> parsed = new SceneCameraDirectiveParser().Parse(canonicalDirective);
        if (!parsed.Success || parsed.Value is null) return EffectDirectiveLabel.Fit(canonicalDirective);

        SceneCameraCommand command = parsed.Value;
        var text = new StringBuilder(Operation(command.Operation));
        AppendFields(text, command);
        if (command.DurationMilliseconds > 0) text.Append(" · ").Append(command.DurationMilliseconds).Append("ms");
        text.Append(" · ").Append(Easing(command.Easing));
        text.Append(EffectDirectiveLabel.Placement(command.Placement));
        return EffectDirectiveLabel.Fit(text.ToString());
    }

    private static void AppendFields(StringBuilder text, SceneCameraCommand command)
    {
        var fields = new List<string>(4);
        if (command.Operation == SceneCameraOperation.Move)
        {
            Add(fields, "Δx", command.DeltaX);
            Add(fields, "Δy", command.DeltaY);
            Add(fields, "Δ缩放", command.DeltaZoom);
        }
        else
        {
            Add(fields, "x", command.X);
            Add(fields, "y", command.Y);
            Add(fields, "缩放", command.Zoom);
        }

        foreach (string field in fields) text.Append(' ').Append(field);
    }

    private static void Add(List<string> fields, string name, float? value)
    {
        if (!value.HasValue || !float.IsFinite(value.Value)) return;
        fields.Add(name + "=" + value.Value.ToString("0.###", CultureInfo.InvariantCulture));
    }

    private static string Operation(SceneCameraOperation operation) => operation switch
    {
        SceneCameraOperation.Set => "设置",
        SceneCameraOperation.Move => "移动",
        SceneCameraOperation.Reset => "复位",
        _ => "镜头"
    };

    private static string Easing(CharacterTransformEasing easing) => easing switch
    {
        CharacterTransformEasing.Linear => "匀速",
        CharacterTransformEasing.EaseIn => "渐快",
        CharacterTransformEasing.EaseOut => "渐慢",
        CharacterTransformEasing.EaseInOut => "缓入缓出",
        _ => easing.ToString()
    };
}

/// <summary>
/// One character's transient preset, in the words the preset editor already uses for its own parameters: which
/// action, how strong, how fast, how many cycles, which way — and whether a timing curve was authored.
/// </summary>
public static class PresetLabel
{
    public static string Of(string? canonicalDirective)
    {
        if (string.IsNullOrWhiteSpace(canonicalDirective)) return string.Empty;
        Result<CharacterPresetCommand> parsed =
            new CharacterPresetDirectiveParser().Parse(canonicalDirective);
        if (!parsed.Success || parsed.Value is null) return EffectDirectiveLabel.Fit(canonicalDirective);

        CharacterPresetCommand command = parsed.Value;
        var text = new StringBuilder(Kind(command.Kind));
        switch (command.Kind)
        {
            case CharacterPresetKind.Spin:
                text.Append(command.SpinAxis == CharacterPresetSpinAxis.X ? " X轴" : " Y轴");
                Append(text, "圈", command.Cycles.ToString(CultureInfo.InvariantCulture));
                break;
            case CharacterPresetKind.Headbutt:
                Append(text, "后", Number(command.BackDegrees) + "°");
                Append(text, "前", Number(command.ForwardDegrees) + "°");
                break;
            case CharacterPresetKind.Squash:
                Append(text, "幅度", Number(command.Amplitude));
                Append(text, "频率", Number(command.FrequencyHz) + "Hz");
                AppendCycles(text, command.Cycles);
                break;
            default:
                Append(text, "幅度", Number(command.Amplitude));
                Append(text, "频率", Number(command.FrequencyHz) + "Hz");
                AppendCycles(text, command.Cycles);
                break;
        }

        if (command.Kind is CharacterPresetKind.Sway or CharacterPresetKind.Spin or CharacterPresetKind.Headbutt)
            text.Append(" · ").Append(command.Direction < 0 ? "向左" : "向右");
        if (command.DurationMilliseconds > 0)
            text.Append(" · ").Append(command.DurationMilliseconds.ToString(CultureInfo.InvariantCulture)).Append("ms");
        if (command.Bezier is not null) text.Append(" · 曲线");
        text.Append(EffectDirectiveLabel.Placement(command.Placement));
        return EffectDirectiveLabel.Fit(text.ToString());
    }

    private static void AppendCycles(StringBuilder text, int cycles) =>
        Append(text, "循环", cycles <= 0 ? "持续" : cycles.ToString(CultureInfo.InvariantCulture));

    private static void Append(StringBuilder text, string name, string value) =>
        text.Append(' ').Append(name).Append('=').Append(value);

    private static string Number(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Kind(CharacterPresetKind kind) => kind switch
    {
        CharacterPresetKind.Sway => "摇摆",
        CharacterPresetKind.Spin => "旋转",
        CharacterPresetKind.Headbutt => "头槌",
        CharacterPresetKind.Squash => "压扁",
        _ => "预设"
    };
}
