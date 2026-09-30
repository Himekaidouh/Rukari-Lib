using System.Globalization;
using AzureArchive.VideoTools.Core.Characters;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Commands;

public sealed class CharacterTransformCommandFamilyCompiler : ICommandFamilyCompiler
{
    public const string CommandTypeId = "character.transform/v1";
    public const string CapabilityId = "Player.CharacterTransform.Dispatch";

    private readonly ICharacterTransformDirectiveParser _parser;

    public CharacterTransformCommandFamilyCompiler(
        ICharacterTransformDirectiveParser? parser = null)
    {
        _parser = parser ?? new CharacterTransformDirectiveParser();
    }

    public string CommandType => CommandTypeId;

    public string RequiredCapability => CapabilityId;

    public Result<CanonicalTimelineCommand> Canonicalize(string directive)
    {
        Result<CharacterTransformCommand> parsed = _parser.Parse(directive);
        if (!parsed.Success || parsed.Value == null)
        {
            return Result<CanonicalTimelineCommand>.Fail(
                $"Character transform directive is invalid: {parsed.Error}");
        }

        CharacterTransformCommand command = parsed.Value;
        var parts = new List<string>
        {
            "#char",
            command.PublicSlot.ToString(CultureInfo.InvariantCulture),
            Operation(command.Operation)
        };

        switch (command.Operation)
        {
            case CharacterTransformOperation.Set:
                AddFloat(parts, "x", command.X);
                AddFloat(parts, "y", command.Y);
                AddFloat(parts, "rotation", command.RotationDegrees);
                if (command.FlipX.HasValue)
                {
                    parts.Add($"flipX={command.FlipX.Value.ToString().ToLowerInvariant()}");
                }

                break;
            case CharacterTransformOperation.Move:
                AddFloat(parts, "dx", command.DeltaX);
                AddFloat(parts, "dy", command.DeltaY);
                AddFloat(parts, "drotation", command.DeltaRotationDegrees);
                break;
            case CharacterTransformOperation.Reset:
                break;
            default:
                return Result<CanonicalTimelineCommand>.Fail(
                    "Character transform operation is unsupported.");
        }

        parts.Add($"duration={command.DurationMilliseconds.ToString(CultureInfo.InvariantCulture)}");
        parts.Add($"easing={Easing(command.Easing)}");

        return Result<CanonicalTimelineCommand>.Ok(new CanonicalTimelineCommand(
            CommandTypeId,
            CapabilityId,
            string.Join(';', parts),
            command.PublicSlot));
    }

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
            CharacterTransformOperation.Reset => "reset",
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    private static string Easing(CharacterTransformEasing easing) => easing switch
    {
        CharacterTransformEasing.Linear => "linear",
        CharacterTransformEasing.EaseIn => "easeIn",
        CharacterTransformEasing.EaseOut => "easeOut",
        CharacterTransformEasing.EaseInOut => "easeInOut",
        _ => throw new ArgumentOutOfRangeException(nameof(easing))
    };
}
