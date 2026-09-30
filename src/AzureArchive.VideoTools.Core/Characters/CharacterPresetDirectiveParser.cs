using System.Globalization;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Characters;

public sealed class CharacterPresetDirectiveParser : ICharacterPresetDirectiveParser
{
    public Result<CharacterPresetCommand> Parse(string directive)
    {
        if (string.IsNullOrWhiteSpace(directive))
        {
            return Result<CharacterPresetCommand>.Fail("Character preset directive is empty.");
        }

        string[] parts = directive.Split(';');
        if (parts.Length < 3
            || !string.Equals(parts[0].Trim(), "#fx", StringComparison.OrdinalIgnoreCase))
        {
            return Result<CharacterPresetCommand>.Fail(
                "Character preset directive must start with #fx;<slot>;<preset>.");
        }

        if (!int.TryParse(parts[1].Trim(), NumberStyles.None,
                CultureInfo.InvariantCulture, out int publicSlot))
        {
            return Result<CharacterPresetCommand>.Fail("Character preset slot must be an integer.");
        }

        CharacterPresetKind? kind = parts[2].Trim().ToLowerInvariant() switch
        {
            "sway" => CharacterPresetKind.Sway,
            "spin" => CharacterPresetKind.Spin,
            "headbutt" => CharacterPresetKind.Headbutt,
            "squash" => CharacterPresetKind.Squash,
            _ => null
        };
        if (!kind.HasValue)
        {
            return Result<CharacterPresetCommand>.Fail("Unknown character preset kind.");
        }

        CharacterPresetCommand command = Defaults(publicSlot, kind.Value);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 3; index < parts.Length; index++)
        {
            string token = parts[index].Trim();
            int equals = token.IndexOf('=');
            if (equals <= 0 || equals == token.Length - 1)
            {
                return Result<CharacterPresetCommand>.Fail($"Invalid character preset property '{token}'.");
            }

            string key = token[..equals].Trim().ToLowerInvariant();
            string value = token[(equals + 1)..].Trim();
            if (!seen.Add(key))
            {
                return Result<CharacterPresetCommand>.Fail($"Duplicate character preset property '{key}'.");
            }

            if (!IsAllowed(kind.Value, key))
            {
                return Result<CharacterPresetCommand>.Fail($"Property '{key}' is not valid for {kind.Value}.");
            }

            if (key == "bezier")
            {
                string[] coordinates = value.Split(',');
                if (coordinates.Length != 4
                    || !TryCoordinate(coordinates[0], out float x1)
                    || !TryCoordinate(coordinates[1], out float y1)
                    || !TryCoordinate(coordinates[2], out float x2)
                    || !TryCoordinate(coordinates[3], out float y2))
                {
                    return Result<CharacterPresetCommand>.Fail(
                        "Property 'bezier' must contain four finite coordinates between 0 and 1: x1,y1,x2,y2.");
                }

                command = command with { Bezier = new CharacterPresetBezier(x1, y1, x2, y2) };
            }
            else if (key == "direction")
            {
                int direction = value.ToLowerInvariant() switch { "left" => -1, "right" => 1, _ => 0 };
                if (direction == 0)
                {
                    return Result<CharacterPresetCommand>.Fail("Property 'direction' must be left or right.");
                }

                command = command with { Direction = direction };
            }
            else if (key is "cycles" or "duration")
            {
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number))
                {
                    return Result<CharacterPresetCommand>.Fail($"Property '{key}' must be a non-negative integer.");
                }

                command = key == "cycles"
                    ? command with { Cycles = number }
                    : command with { DurationMilliseconds = number };
            }
            else
            {
                if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float number)
                    || !float.IsFinite(number))
                {
                    return Result<CharacterPresetCommand>.Fail($"Property '{key}' must be a finite number.");
                }

                command = key switch
                {
                    "amplitude" => command with { Amplitude = number },
                    "frequency" => command with { FrequencyHz = number },
                    "back" => command with { BackDegrees = number },
                    "forward" => command with { ForwardDegrees = number },
                    _ => command
                };
            }
        }

        Result validation = CharacterPresetCommandValidator.Validate(command);
        return validation.Success
            ? Result<CharacterPresetCommand>.Ok(command)
            : Result<CharacterPresetCommand>.Fail(validation.Error);
    }

    private static bool TryCoordinate(string text, out float value) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && float.IsFinite(value) && value is >= 0f and <= 1f;

    private static bool IsAllowed(CharacterPresetKind kind, string key) => key == "bezier"
        || (kind == CharacterPresetKind.Headbutt
            ? key is "back" or "forward" or "duration" or "direction"
            : key is "frequency" or "cycles" or "direction"
                || (key == "amplitude" && kind != CharacterPresetKind.Spin));

    private static CharacterPresetCommand Defaults(int publicSlot, CharacterPresetKind kind) =>
        kind switch
        {
            CharacterPresetKind.Sway => new(publicSlot, kind, 12f, 3f, 3, 1, 0f, 0f, 0),
            CharacterPresetKind.Spin => new(publicSlot, kind, 0f, 1f, 2, 1, 0f, 0f, 0),
            CharacterPresetKind.Headbutt => new(publicSlot, kind, 0f, 0f, 1, 1, 20f, 45f, 650),
            CharacterPresetKind.Squash => new(publicSlot, kind, 0.15f, 2f, 3, 1, 0f, 0f, 0),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
}
