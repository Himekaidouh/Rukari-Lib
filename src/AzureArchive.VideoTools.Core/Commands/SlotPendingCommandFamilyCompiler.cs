using System.Globalization;
using AzureArchive.VideoTools.Core.Characters;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Commands;

/// <summary>
/// Compiles the independent <c>character.slotPending/v1</c> command family.
/// A pending directive targets a physical slot that is empty when its scene
/// executes; the runtime stores it and applies it instantly when a character
/// later enters that slot. The existing character.transform/v1 semantics are
/// unchanged by this family.
/// </summary>
public sealed class SlotPendingCommandFamilyCompiler : ICommandFamilyCompiler
{
    public const string CommandTypeId = "character.slotPending/v1";
    public const string CapabilityId = "Player.CharacterTransform.SlotPending";
    public const string CanonicalRootToken = "#charp";
    public const string PublicNamespaceToken = "charPending";

    private readonly CharacterTransformDirectiveParser _parser;

    public SlotPendingCommandFamilyCompiler()
    {
        _parser = new CharacterTransformDirectiveParser(
            CanonicalRootToken,
            allowTimingProperties: false);
    }

    public string CommandType => CommandTypeId;

    public string RequiredCapability => CapabilityId;

    public Result<CanonicalTimelineCommand> Canonicalize(string directive)
    {
        Result<CharacterTransformCommand> parsed = _parser.Parse(directive);
        if (!parsed.Success || parsed.Value == null)
        {
            return Result<CanonicalTimelineCommand>.Fail(
                $"Slot pending directive is invalid: {parsed.Error}");
        }

        CharacterTransformCommand command = parsed.Value;
        if (command.Operation == CharacterTransformOperation.Reset)
        {
            return Result<CanonicalTimelineCommand>.Fail(
                "Slot pending directives do not support reset; nothing exists to restore yet.");
        }

        var parts = new List<string>
        {
            CanonicalRootToken,
            command.PublicSlot.ToString(CultureInfo.InvariantCulture),
            Operation(command.Operation)
        };

        switch (command.Operation)
        {
            case CharacterTransformOperation.Set:
                AddFloat(parts, "x", command.X);
                AddFloat(parts, "y", command.Y);
                AddFloat(parts, "rotation", command.RotationDegrees);
                AddFloat(parts, "rotationX", command.RotationXDegrees);
                if (command.FlipX.HasValue)
                {
                    parts.Add($"flipX={command.FlipX.Value.ToString().ToLowerInvariant()}");
                }

                break;
            case CharacterTransformOperation.Move:
                AddFloat(parts, "dx", command.DeltaX);
                AddFloat(parts, "dy", command.DeltaY);
                AddFloat(parts, "drotation", command.DeltaRotationDegrees);
                AddFloat(parts, "drotationX", command.DeltaRotationXDegrees);
                break;
            default:
                return Result<CanonicalTimelineCommand>.Fail(
                    "Slot pending operation is unsupported.");
        }

        return Result<CanonicalTimelineCommand>.Ok(new CanonicalTimelineCommand(
            CommandTypeId,
            CapabilityId,
            string.Join(';', parts),
            command.PublicSlot));
    }

    /// <summary>Parses an already canonical #charp directive back into a command.</summary>
    public Result<CharacterTransformCommand> ParseCanonical(string canonicalDirective) =>
        _parser.Parse(canonicalDirective);

    private static void AddFloat(
        ICollection<string> parts,
        string name,
        float? value)
    {
        if (!value.HasValue)
        {
            return;
        }

        float normalized = value.Value == 0f ? 0f : value.Value;
        parts.Add($"{name}={normalized.ToString("R", CultureInfo.InvariantCulture)}");
    }

    private static string Operation(CharacterTransformOperation operation) =>
        operation switch
        {
            CharacterTransformOperation.Set => "set",
            CharacterTransformOperation.Move => "move",
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
}
