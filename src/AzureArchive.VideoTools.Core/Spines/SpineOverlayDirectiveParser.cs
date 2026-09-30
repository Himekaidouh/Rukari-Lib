using System.Globalization;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Spines;

/// <summary>
/// Parses the internal overlay directive the <c>#aavt;spine</c> line becomes (2026-09-21):
/// <code>
/// #spine;&lt;slot&gt;;&lt;animation&gt;[;track=20..29][;mix=&lt;ms&gt;][;fade=&lt;ms&gt;][;loop=true|false][;blend=replace|add]
/// #spine;&lt;slot&gt;;clear[;fade=&lt;ms&gt;]
/// </code>
/// <para>
/// The animation name stays positional because that is what the author types
/// (<c>#aavt;spine;3;Add_Arm</c>), and it is kept case-sensitive because Spine animation names
/// are. <c>clear</c> is therefore a reserved word; an asset that really has an animation called
/// <c>clear</c> can still be played by naming it in the <c>animation=</c> property.
/// </para>
/// </summary>
public sealed class SpineOverlayDirectiveParser : ISpineOverlayDirectiveParser
{
    public const string CanonicalRootToken = "#spine";

    private const string ClearToken = "clear";

    private static readonly string[] PlayProperties =
    {
        "animation", "track", "mix", "fade", "loop", "blend", "hold"
    };

    private static readonly string[] ClearProperties = { "fade", "track" };

    public Result<SpineOverlayCommand> Parse(string directive)
    {
        if (string.IsNullOrWhiteSpace(directive))
        {
            return Result<SpineOverlayCommand>.Fail("Spine overlay directive is empty.");
        }

        string[] parts = directive.Split(';');
        if (parts.Length < 3
            || !string.Equals(parts[0].Trim(), CanonicalRootToken, StringComparison.OrdinalIgnoreCase))
        {
            return Result<SpineOverlayCommand>.Fail(
                "Spine overlay directive must start with #spine;<slot>;<animation|clear>.");
        }

        if (!int.TryParse(parts[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int slot))
        {
            return Result<SpineOverlayCommand>.Fail("Spine overlay slot must be a positive integer.");
        }

        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 3; index < parts.Length; index++)
        {
            string token = parts[index].Trim();
            int equalsIndex = token.IndexOf('=');
            if (equalsIndex <= 0 || equalsIndex == token.Length - 1)
            {
                return Result<SpineOverlayCommand>.Fail(
                    $"Invalid spine overlay property '{token}'.");
            }

            string key = token[..equalsIndex].Trim();
            string value = token[(equalsIndex + 1)..].Trim();
            if (!properties.TryAdd(key, value))
            {
                return Result<SpineOverlayCommand>.Fail(
                    $"Duplicate spine overlay property '{key}'.");
            }
        }

        // The animation= property exists so a name that collides with the reserved clear token is
        // still reachable; when present it is the name, whatever the positional token said.
        bool named = properties.TryGetValue("animation", out string? explicitName);
        string operationToken = parts[2].Trim();
        bool isClear = !named
            && string.Equals(operationToken, ClearToken, StringComparison.OrdinalIgnoreCase);
        string animation = named ? explicitName! : isClear ? string.Empty : operationToken;

        string[] allowed = isClear ? ClearProperties : PlayProperties;
        string? unknown = properties.Keys.FirstOrDefault(
            key => !allowed.Contains(key, StringComparer.OrdinalIgnoreCase));
        if (unknown != null)
        {
            return Result<SpineOverlayCommand>.Fail(
                $"Property '{unknown}' is not valid for spine overlay "
                + (isClear ? "clear." : "play."));
        }

        Result<int> track = ParseInteger(properties, "track", SpineOverlayTrackPolicy.DefaultTrack);
        if (!track.Success) return Result<SpineOverlayCommand>.Fail(track.Error);
        Result<int> mix = ParseInteger(properties, "mix", 0);
        if (!mix.Success) return Result<SpineOverlayCommand>.Fail(mix.Error);
        Result<int> fade = ParseInteger(
            properties,
            "fade",
            SpineOverlayTrackPolicy.DefaultFadeMilliseconds);
        if (!fade.Success) return Result<SpineOverlayCommand>.Fail(fade.Error);
        Result<bool> loop = ParseLoop(properties);
        if (!loop.Success) return Result<SpineOverlayCommand>.Fail(loop.Error);
        Result<SpineOverlayBlend> blend = ParseBlend(properties);
        if (!blend.Success) return Result<SpineOverlayCommand>.Fail(blend.Error);
        Result<bool?> hold = ParseHold(properties);
        if (!hold.Success) return Result<SpineOverlayCommand>.Fail(hold.Error);

        var command = new SpineOverlayCommand(
            slot,
            isClear ? SpineOverlayOperation.Clear : SpineOverlayOperation.Play,
            animation,
            track.Value,
            mix.Value,
            fade.Value,
            loop.Value,
            blend.Value,
            hold.Value);
        Result validation = SpineOverlayCommandValidator.Validate(command);
        return validation.Success
            ? Result<SpineOverlayCommand>.Ok(command)
            : Result<SpineOverlayCommand>.Fail(validation.Error);
    }

    private static Result<int> ParseInteger(
        IReadOnlyDictionary<string, string> properties,
        string key,
        int fallback)
    {
        if (!properties.TryGetValue(key, out string? text))
        {
            return Result<int>.Ok(fallback);
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            ? Result<int>.Ok(value)
            : Result<int>.Fail($"Property '{key}' must be a non-negative integer.");
    }

    private static Result<bool> ParseLoop(IReadOnlyDictionary<string, string> properties)
    {
        if (!properties.TryGetValue("loop", out string? text))
        {
            // Sticky is the contract: an overlay stays until it is cleared or the scene rebuilds,
            // so the default entry loops rather than ending on its own.
            return Result<bool>.Ok(true);
        }

        return text.ToLowerInvariant() switch
        {
            "true" => Result<bool>.Ok(true),
            "false" => Result<bool>.Ok(false),
            _ => Result<bool>.Fail("Property 'loop' must be true or false.")
        };
    }

    /// <summary>
    /// Whether a non-looping overlay keeps its last frame. Absent means "the provider decides",
    /// which keeps a directive written before this switch existed byte-identical to itself.
    /// </summary>
    private static Result<bool?> ParseHold(IReadOnlyDictionary<string, string> properties)
    {
        if (!properties.TryGetValue("hold", out string? text))
        {
            return Result<bool?>.Ok(null);
        }

        return text.ToLowerInvariant() switch
        {
            "true" => Result<bool?>.Ok(true),
            "false" => Result<bool?>.Ok(false),
            _ => Result<bool?>.Fail("Property 'hold' must be true or false.")
        };
    }

    private static Result<SpineOverlayBlend> ParseBlend(IReadOnlyDictionary<string, string> properties)    {
        if (!properties.TryGetValue("blend", out string? text))
        {
            return Result<SpineOverlayBlend>.Ok(SpineOverlayBlend.Replace);
        }

        return text.ToLowerInvariant() switch
        {
            "replace" => Result<SpineOverlayBlend>.Ok(SpineOverlayBlend.Replace),
            "add" => Result<SpineOverlayBlend>.Ok(SpineOverlayBlend.Add),
            _ => Result<SpineOverlayBlend>.Fail("Property 'blend' must be replace or add.")
        };
    }
}
