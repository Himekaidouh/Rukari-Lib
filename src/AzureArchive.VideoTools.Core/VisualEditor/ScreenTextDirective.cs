using System.Globalization;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.VisualEditor;

public enum ScreenTextAlignment
{
    Left = 0,
    Center = 1
}

public enum ScreenTextRevealMode
{
    Instant = 0,
    Smooth = 1,
    Serial = 2
}

public sealed record ScreenTextDirective(
    float X,
    float Y,
    ScreenTextAlignment Alignment,
    ScreenTextRevealMode RevealMode,
    int FontSize)
{
    public string ToOfficialDirective()
    {
        string root = Alignment == ScreenTextAlignment.Center ? "#stm" : "#st";
        string mode = RevealMode switch
        {
            ScreenTextRevealMode.Instant => "instant",
            ScreenTextRevealMode.Smooth => "smooth",
            ScreenTextRevealMode.Serial => "serial",
            _ => throw new InvalidOperationException("Unknown screen-text reveal mode.")
        };
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{root};[{Format(X)},{Format(Y)}];{mode};{FontSize};");
    }

    private static string Format(float value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);
}

public sealed record ScreenTextLine(
    int LineNumber,
    ScreenTextDirective Directive);

public static class ScreenTextDirectiveCodec
{
    public const int MinimumFontSize = 1;
    public const int MaximumFontSize = 1000;
    public const float MaximumCoordinateMagnitude = 10000f;

    public static Result<ScreenTextDirective> ParseLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        string trimmed = line.Trim();
        bool centered;
        string remainder;
        if (trimmed.StartsWith("#stm;", StringComparison.OrdinalIgnoreCase))
        {
            centered = true;
            remainder = trimmed[5..];
        }
        else if (trimmed.StartsWith("#st;", StringComparison.OrdinalIgnoreCase))
        {
            centered = false;
            remainder = trimmed[4..];
        }
        else
        {
            return Result<ScreenTextDirective>.Fail(
                "Screen text must start with #st; or #stm;.");
        }

        if (!remainder.EndsWith(';'))
        {
            return Result<ScreenTextDirective>.Fail(
                "Screen text must keep the final semicolon.");
        }

        string[] tokens = remainder.Split(';');
        if (tokens.Length != 4 || tokens[^1].Length != 0)
        {
            return Result<ScreenTextDirective>.Fail(
                "Screen text must be #st[m];[x,y];mode;fontSize;.");
        }

        string coordinates = tokens[0].Trim();
        if (coordinates.Length < 5
            || coordinates[0] != '['
            || coordinates[^1] != ']')
        {
            return Result<ScreenTextDirective>.Fail(
                "Screen-text coordinates must be enclosed in [x,y].");
        }

        string[] values = coordinates[1..^1].Split(',');
        if (values.Length != 2
            || !TryFloat(values[0], out float x)
            || !TryFloat(values[1], out float y)
            || MathF.Abs(x) > MaximumCoordinateMagnitude
            || MathF.Abs(y) > MaximumCoordinateMagnitude)
        {
            return Result<ScreenTextDirective>.Fail(
                "Screen-text coordinates must be two finite values within +/-10000.");
        }

        ScreenTextRevealMode revealMode;
        switch (tokens[1].Trim().ToLowerInvariant())
        {
            case "instant":
                revealMode = ScreenTextRevealMode.Instant;
                break;
            case "smooth":
                revealMode = ScreenTextRevealMode.Smooth;
                break;
            case "serial":
                revealMode = ScreenTextRevealMode.Serial;
                break;
            default:
                return Result<ScreenTextDirective>.Fail(
                    "Screen-text mode must be instant, smooth, or serial.");
        }

        if (!int.TryParse(
                tokens[2].Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int fontSize)
            || fontSize < MinimumFontSize
            || fontSize > MaximumFontSize)
        {
            return Result<ScreenTextDirective>.Fail(
                $"Screen-text font size must be {MinimumFontSize}..{MaximumFontSize}.");
        }

        return Result<ScreenTextDirective>.Ok(new ScreenTextDirective(
            x,
            y,
            centered ? ScreenTextAlignment.Center : ScreenTextAlignment.Left,
            revealMode,
            fontSize));
    }

    public static Result<IReadOnlyList<ScreenTextLine>> ReadDocument(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var lines = new List<ScreenTextLine>();
        int lineNumber = 1;
        foreach (string line in EnumerateLines(text))
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith("#st;", StringComparison.OrdinalIgnoreCase)
                && !trimmed.StartsWith("#stm;", StringComparison.OrdinalIgnoreCase))
            {
                lineNumber++;
                continue;
            }

            Result<ScreenTextDirective> parsed = ParseLine(trimmed);
            if (!parsed.Success || parsed.Value == null)
            {
                return Result<IReadOnlyList<ScreenTextLine>>.Fail(
                    $"Invalid official screen-text directive at line {lineNumber}: {parsed.Error}");
            }

            lines.Add(new ScreenTextLine(lineNumber, parsed.Value));
            lineNumber++;
        }

        return Result<IReadOnlyList<ScreenTextLine>>.Ok(lines.AsReadOnly());
    }

    private static bool TryFloat(string token, out float value) =>
        float.TryParse(
            token.Trim(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value)
        && float.IsFinite(value);

    private static IEnumerable<string> EnumerateLines(string text)
    {
        int offset = 0;
        while (offset < text.Length)
        {
            int contentEnd = offset;
            while (contentEnd < text.Length
                && text[contentEnd] != '\r'
                && text[contentEnd] != '\n')
            {
                contentEnd++;
            }

            yield return text[offset..contentEnd];
            if (contentEnd < text.Length && text[contentEnd] == '\r')
            {
                contentEnd++;
            }

            if (contentEnd < text.Length && text[contentEnd] == '\n')
            {
                contentEnd++;
            }

            offset = contentEnd;
        }
    }
}
