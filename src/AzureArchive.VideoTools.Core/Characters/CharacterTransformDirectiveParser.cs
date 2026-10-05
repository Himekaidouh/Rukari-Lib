using System.Globalization;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Characters;

public sealed class CharacterTransformDirectiveParser : ICharacterTransformDirectiveParser
{
    private static readonly HashSet<string> CommonProperties = new(
        new[] { "duration", "easing" },
        StringComparer.OrdinalIgnoreCase);

    private readonly string _rootToken;
    private readonly bool _allowTimingProperties;

    public CharacterTransformDirectiveParser(
        string rootToken = "#char",
        bool allowTimingProperties = true)
    {
        _rootToken = rootToken;
        _allowTimingProperties = allowTimingProperties;
    }

    public Result<CharacterTransformCommand> Parse(string directive)
    {
        if (string.IsNullOrWhiteSpace(directive))
        {
            return Result<CharacterTransformCommand>.Fail("Character directive is empty.");
        }

        string[] parts = directive.Split(';');
        if (parts.Length < 3
            || !string.Equals(parts[0].Trim(), _rootToken, StringComparison.OrdinalIgnoreCase))
        {
            return Result<CharacterTransformCommand>.Fail(
                $"Character directive must start with {_rootToken};<slot>;<operation>.");
        }

        if (!int.TryParse(
                parts[1].Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int publicSlot))
        {
            return Result<CharacterTransformCommand>.Fail("Character slot is not a valid integer.");
        }

        if (!TryParseOperation(parts[2].Trim(), out CharacterTransformOperation operation))
        {
            return Result<CharacterTransformCommand>.Fail("Unknown character transform operation.");
        }

        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 3; index < parts.Length; index++)
        {
            string token = parts[index].Trim();
            int equalsIndex = token.IndexOf('=');
            if (equalsIndex <= 0 || equalsIndex == token.Length - 1)
            {
                return Result<CharacterTransformCommand>.Fail(
                    $"Invalid character transform property '{token}'.");
            }

            string key = token[..equalsIndex].Trim();
            string value = token[(equalsIndex + 1)..].Trim();
            if (!properties.TryAdd(key, value))
            {
                return Result<CharacterTransformCommand>.Fail(
                    $"Duplicate character transform property '{key}'.");
            }
        }

        HashSet<string> allowed = AllowedProperties(operation, _allowTimingProperties);
        string? unknown = properties.Keys.FirstOrDefault(key => !allowed.Contains(key));
        if (unknown != null)
        {
            return Result<CharacterTransformCommand>.Fail(
                $"Property '{unknown}' is not valid for {operation}.");
        }

        Result<float?> x = ParseOptionalFloat(properties, "x");
        Result<float?> y = ParseOptionalFloat(properties, "y");
        Result<float?> deltaX = ParseOptionalFloat(properties, "dx");
        Result<float?> deltaY = ParseOptionalFloat(properties, "dy");
        Result<float?> rotation = ParseOptionalFloat(properties, "rotation");
        Result<float?> deltaRotation = ParseOptionalFloat(properties, "drotation");
        Result<float?> rotationX = ParseOptionalFloat(properties, "rotationX");
        Result<float?> deltaRotationX = ParseOptionalFloat(properties, "drotationX");
        Result<bool?> flipX = ParseOptionalBool(properties, "flipX");
        Result<int> duration = ParseDuration(properties);
        Result<CharacterTransformEasing> easing = ParseEasing(properties);

        string? parseError = FirstError(
            x,
            y,
            deltaX,
            deltaY,
            rotation,
            deltaRotation,
            rotationX,
            deltaRotationX,
            flipX,
            duration,
            easing);
        if (parseError != null)
        {
            return Result<CharacterTransformCommand>.Fail(parseError);
        }

        var command = new CharacterTransformCommand(
            publicSlot,
            operation,
            x.Value,
            y.Value,
            deltaX.Value,
            deltaY.Value,
            rotation.Value,
            deltaRotation.Value,
            flipX.Value,
            duration.Value,
            easing.Value)
        {
            RotationXDegrees = rotationX.Value,
            DeltaRotationXDegrees = deltaRotationX.Value
        };
        Result validation = CharacterTransformCommandValidator.Validate(command);
        return validation.Success
            ? Result<CharacterTransformCommand>.Ok(command)
            : Result<CharacterTransformCommand>.Fail(validation.Error);
    }

    private static HashSet<string> AllowedProperties(
        CharacterTransformOperation operation,
        bool allowTimingProperties)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (allowTimingProperties)
        {
            allowed.UnionWith(CommonProperties);
        }

        IEnumerable<string> operationProperties = operation switch
        {
            CharacterTransformOperation.Set => new[] { "x", "y", "rotation", "rotationX", "flipX" },
            CharacterTransformOperation.Move => new[] { "dx", "dy", "drotation", "drotationX" },
            CharacterTransformOperation.Reset => Array.Empty<string>(),
            _ => Array.Empty<string>()
        };

        allowed.UnionWith(operationProperties);
        return allowed;
    }

    private static Result<float?> ParseOptionalFloat(
        IReadOnlyDictionary<string, string> properties,
        string key)
    {
        if (!properties.TryGetValue(key, out string? text))
        {
            return Result<float?>.Ok(null);
        }

        if (!float.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out float value)
            || !float.IsFinite(value))
        {
            return Result<float?>.Fail($"Property '{key}' must be a finite number.");
        }

        return Result<float?>.Ok(value);
    }

    private static Result<bool?> ParseOptionalBool(
        IReadOnlyDictionary<string, string> properties,
        string key)
    {
        if (!properties.TryGetValue(key, out string? text))
        {
            return Result<bool?>.Ok(null);
        }

        return bool.TryParse(text, out bool value)
            ? Result<bool?>.Ok(value)
            : Result<bool?>.Fail($"Property '{key}' must be true or false.");
    }

    private static Result<int> ParseDuration(IReadOnlyDictionary<string, string> properties)
    {
        if (!properties.TryGetValue("duration", out string? text))
        {
            return Result<int>.Ok(0);
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            ? Result<int>.Ok(value)
            : Result<int>.Fail("Property 'duration' must be a non-negative integer.");
    }

    private static Result<CharacterTransformEasing> ParseEasing(
        IReadOnlyDictionary<string, string> properties)
    {
        if (!properties.TryGetValue("easing", out string? text))
        {
            return Result<CharacterTransformEasing>.Ok(CharacterTransformEasing.Linear);
        }

        CharacterTransformEasing? easing = text.ToLowerInvariant() switch
        {
            "linear" => CharacterTransformEasing.Linear,
            "easein" => CharacterTransformEasing.EaseIn,
            "easeout" => CharacterTransformEasing.EaseOut,
            "easeinout" => CharacterTransformEasing.EaseInOut,
            _ => null
        };

        return easing.HasValue
            ? Result<CharacterTransformEasing>.Ok(easing.Value)
            : Result<CharacterTransformEasing>.Fail("Property 'easing' is not supported.");
    }

    private static bool TryParseOperation(
        string text,
        out CharacterTransformOperation operation)
    {
        CharacterTransformOperation? parsed = text.ToLowerInvariant() switch
        {
            "set" => CharacterTransformOperation.Set,
            "move" => CharacterTransformOperation.Move,
            "reset" => CharacterTransformOperation.Reset,
            _ => null
        };
        operation = parsed.GetValueOrDefault();
        return parsed.HasValue;
    }

    private static string? FirstError(params object[] results)
    {
        foreach (object result in results)
        {
            string? error = result switch
            {
                Result<float?> floatResult when !floatResult.Success => floatResult.Error,
                Result<bool?> boolResult when !boolResult.Success => boolResult.Error,
                Result<int> intResult when !intResult.Success => intResult.Error,
                Result<CharacterTransformEasing> easingResult when !easingResult.Success => easingResult.Error,
                _ => null
            };
            if (error != null)
            {
                return error;
            }
        }

        return null;
    }
}
