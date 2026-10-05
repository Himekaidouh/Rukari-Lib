using System.Globalization;
using AzureArchive.VideoTools.Core.Characters;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Cameras;

public sealed class SceneCameraDirectiveParser : ISceneCameraDirectiveParser
{
    private static readonly HashSet<string> CommonProperties = new(
        new[] { "duration", "easing", "scope" },
        StringComparer.OrdinalIgnoreCase);

    public Result<SceneCameraCommand> Parse(string directive)
    {
        if (string.IsNullOrWhiteSpace(directive))
        {
            return Result<SceneCameraCommand>.Fail("Scene camera directive is empty.");
        }

        string[] parts = directive.Split(';');
        if (parts.Length < 2
            || !string.Equals(parts[0].Trim(), "#camera", StringComparison.OrdinalIgnoreCase))
        {
            return Result<SceneCameraCommand>.Fail(
                "Scene camera directive must start with #camera;<operation>.");
        }

        if (!TryParseOperation(parts[1].Trim(), out SceneCameraOperation operation))
        {
            return Result<SceneCameraCommand>.Fail("Unknown scene camera operation.");
        }

        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 2; index < parts.Length; index++)
        {
            string token = parts[index].Trim();
            int equalsIndex = token.IndexOf('=');
            if (equalsIndex <= 0 || equalsIndex == token.Length - 1)
            {
                return Result<SceneCameraCommand>.Fail(
                    $"Invalid scene camera property '{token}'.");
            }

            string key = token[..equalsIndex].Trim();
            string value = token[(equalsIndex + 1)..].Trim();
            if (!properties.TryAdd(key, value))
            {
                return Result<SceneCameraCommand>.Fail(
                    $"Duplicate scene camera property '{key}'.");
            }
        }

        HashSet<string> allowed = AllowedProperties(operation);
        string? unknown = properties.Keys.FirstOrDefault(key => !allowed.Contains(key));
        if (unknown != null)
        {
            return Result<SceneCameraCommand>.Fail(
                $"Property '{unknown}' is not valid for {operation}.");
        }

        Result<float?> x = ParseOptionalFloat(properties, "x");
        Result<float?> y = ParseOptionalFloat(properties, "y");
        Result<float?> deltaX = ParseOptionalFloat(properties, "dx");
        Result<float?> deltaY = ParseOptionalFloat(properties, "dy");
        Result<float?> zoom = ParseOptionalFloat(properties, "zoom");
        Result<float?> deltaZoom = ParseOptionalFloat(properties, "dzoom");
        Result<int> duration = ParseDuration(properties);
        Result<CharacterTransformEasing> easing = ParseEasing(properties);
        Result<SceneCameraScope> scope = ParseScope(properties);

        string? error = FirstError(x, y, deltaX, deltaY, zoom, deltaZoom, duration, easing, scope);
        if (error != null)
        {
            return Result<SceneCameraCommand>.Fail(error);
        }

        var command = new SceneCameraCommand(
            operation,
            x.Value,
            y.Value,
            deltaX.Value,
            deltaY.Value,
            zoom.Value,
            deltaZoom.Value,
            duration.Value,
            easing.Value) { Scope = scope.Value };
        Result validation = SceneCameraCommandValidator.Validate(command);
        return validation.Success
            ? Result<SceneCameraCommand>.Ok(command)
            : Result<SceneCameraCommand>.Fail(validation.Error);
    }

    private static HashSet<string> AllowedProperties(SceneCameraOperation operation)
    {
        var allowed = new HashSet<string>(CommonProperties, StringComparer.OrdinalIgnoreCase);
        allowed.UnionWith(operation switch
        {
            SceneCameraOperation.Set => new[] { "x", "y", "zoom" },
            SceneCameraOperation.Move => new[] { "dx", "dy", "dzoom" },
            SceneCameraOperation.Reset => Array.Empty<string>(),
            _ => Array.Empty<string>()
        });
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

        return float.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out float value)
            && float.IsFinite(value)
            ? Result<float?>.Ok(value)
            : Result<float?>.Fail($"Property '{key}' must be a finite number.");
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

    private static Result<SceneCameraScope> ParseScope(IReadOnlyDictionary<string, string> properties)
    {
        if (!properties.TryGetValue("scope", out string? text))
            return Result<SceneCameraScope>.Ok(SceneCameraScope.Overall);

        return text.ToLowerInvariant() switch
        {
            "overall" => Result<SceneCameraScope>.Ok(SceneCameraScope.Overall),
            "background" => Result<SceneCameraScope>.Ok(SceneCameraScope.Background),
            _ => Result<SceneCameraScope>.Fail("Scope must be overall or background.")
        };
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

    private static bool TryParseOperation(string text, out SceneCameraOperation operation)
    {
        SceneCameraOperation? parsed = text.ToLowerInvariant() switch
        {
            "set" => SceneCameraOperation.Set,
            "move" => SceneCameraOperation.Move,
            "reset" => SceneCameraOperation.Reset,
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
                Result<int> intResult when !intResult.Success => intResult.Error,
                Result<CharacterTransformEasing> easingResult when !easingResult.Success => easingResult.Error,
                Result<SceneCameraScope> scopeResult when !scopeResult.Success => scopeResult.Error,
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
