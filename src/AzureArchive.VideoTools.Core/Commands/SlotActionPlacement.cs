using System.Globalization;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Commands;

/// <summary>
/// Where one directive sits on its row's action timeline, as authored.
/// <paramref name="AtMilliseconds"/> is a negative value when the author asked for
/// no explicit offset, which is the default append behaviour.
/// <para>
/// These two parameters are the only author-facing timing controls the queue adds.
/// They are deliberately part of the command records instead of the directive text
/// so every family validates them the same way.
/// </para>
/// </summary>
public sealed record SlotActionPlacement(int AtMilliseconds, bool Interrupt)
{
    public static readonly SlotActionPlacement None = new(-1, false);

    public bool HasOffset => AtMilliseconds >= 0;

    /// <summary>True when the author wrote nothing, so canonical text must stay unchanged.</summary>
    public bool IsNone => !HasOffset && !Interrupt;
}

/// <summary>
/// Parses and re-emits the queue placement parameters (<c>at=</c>, <c>interrupt=</c>).
/// Values are validated, never silently ignored: a malformed <c>at</c> or
/// <c>interrupt</c> fails the whole directive, exactly like a malformed
/// <c>duration</c> does.
/// <para>
/// The two parameters are mutually exclusive. "Take over and start now" and "start
/// at this offset" have no honest combined meaning, so authoring both fails and the
/// error names both properties. An author who wants that shape uses two rows.
/// </para>
/// </summary>
public static class SlotActionPlacementParser
{
    public const string AtKey = "at";
    public const string InterruptKey = "interrupt";

    /// <summary>Names both properties so the author sees what to remove.</summary>
    public const string MutuallyExclusiveError =
        "at= and interrupt=true cannot be combined; use one or the other.";

    public static IReadOnlyList<string> Keys { get; } = new[] { AtKey, InterruptKey };

    public static bool IsPlacementKey(string key) =>
        string.Equals(key, AtKey, StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, InterruptKey, StringComparison.OrdinalIgnoreCase);

    /// <summary>Applies one authored token to a placement being built.</summary>
    public static Result<SlotActionPlacement> Apply(
        SlotActionPlacement current,
        string key,
        string value)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (string.Equals(key, AtKey, StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(
                    value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int milliseconds))
            {
                return Result<SlotActionPlacement>.Fail(
                    "Property 'at' must be a non-negative integer number of milliseconds.");
            }

            // Checked in both directions: the property tables iterate in a fixed order
            // while the preset parser applies the author's own token order.
            if (current.Interrupt)
            {
                return Result<SlotActionPlacement>.Fail(MutuallyExclusiveError);
            }

            return Result<SlotActionPlacement>.Ok(current with { AtMilliseconds = milliseconds });
        }

        if (string.Equals(key, InterruptKey, StringComparison.OrdinalIgnoreCase))
        {
            if (!bool.TryParse(value, out bool interrupt))
            {
                return Result<SlotActionPlacement>.Fail("Property 'interrupt' must be true or false.");
            }

            // interrupt=false is the default and still normalizes away next to at=.
            if (interrupt && current.HasOffset)
            {
                return Result<SlotActionPlacement>.Fail(MutuallyExclusiveError);
            }

            return Result<SlotActionPlacement>.Ok(current with { Interrupt = interrupt });
        }

        return Result<SlotActionPlacement>.Fail($"Property '{key}' is not a queue placement parameter.");
    }

    /// <summary>Reads both parameters from an already-tokenized property table.</summary>
    public static Result<SlotActionPlacement> Parse(
        IReadOnlyDictionary<string, string> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        SlotActionPlacement placement = SlotActionPlacement.None;
        foreach (string key in Keys)
        {
            if (!properties.TryGetValue(key, out string? value))
            {
                continue;
            }

            Result<SlotActionPlacement> applied = Apply(placement, key, value);
            if (!applied.Success || applied.Value == null)
            {
                return Result<SlotActionPlacement>.Fail(applied.Error);
            }

            placement = applied.Value;
        }

        return Result<SlotActionPlacement>.Ok(placement);
    }

    /// <summary>
    /// Appends the parameters to a canonical directive. Nothing is appended when the
    /// author wrote neither, which is what keeps every existing canonical directive
    /// byte-for-byte identical.
    /// </summary>
    public static void AppendCanonical(ICollection<string> parts, SlotActionPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (placement == null || placement.IsNone)
        {
            return;
        }

        if (placement.HasOffset)
        {
            parts.Add($"{AtKey}={placement.AtMilliseconds.ToString(CultureInfo.InvariantCulture)}");
        }

        if (placement.Interrupt)
        {
            parts.Add($"{InterruptKey}=true");
        }
    }

    /// <summary>Visible form for the queue diagnostics.</summary>
    public static string Describe(SlotActionPlacement placement)
    {
        if (placement == null || placement.IsNone)
        {
            return "append";
        }

        if (placement.HasOffset && placement.Interrupt)
        {
            return $"at={placement.AtMilliseconds}+interrupt";
        }

        return placement.HasOffset ? $"at={placement.AtMilliseconds}" : "interrupt";
    }
}
