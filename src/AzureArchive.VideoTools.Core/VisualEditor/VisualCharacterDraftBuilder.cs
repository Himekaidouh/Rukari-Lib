using System.Globalization;
using AzureArchive.VideoTools.Core.Characters;
using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.VisualEditor;

public enum VisualCharacterDraftOperation
{
    Set = 0,
    Move = 1,
    Reset = 2
}

public sealed record VisualCharacterDraftRequest(
    int PublicSlot,
    bool Occupied,
    VisualCharacterDraftOperation Operation,
    float? X,
    float? Y,
    float? RotationDegrees,
    bool? FlipX,
    int DurationMilliseconds,
    CharacterTransformEasing Easing)
{
    public float? RotationXDegrees { get; init; }
}

public sealed class VisualCharacterDraftBuilder
{
    public Result<VisualCharacterDraftRequest> ReadCanonical(
        string canonicalDirective)
    {
        ArgumentNullException.ThrowIfNull(canonicalDirective);
        bool occupied;
        Result<CharacterTransformCommand> parsed;
        if (canonicalDirective.StartsWith(
                SlotPendingCommandFamilyCompiler.CanonicalRootToken + ";",
                StringComparison.Ordinal))
        {
            occupied = false;
            parsed = new SlotPendingCommandFamilyCompiler()
                .ParseCanonical(canonicalDirective);
        }
        else
        {
            occupied = true;
            parsed = new CharacterTransformDirectiveParser().Parse(canonicalDirective);
        }

        if (!parsed.Success || parsed.Value == null)
        {
            return Result<VisualCharacterDraftRequest>.Fail(parsed.Error);
        }

        CharacterTransformCommand command = parsed.Value;
        VisualCharacterDraftOperation operation = command.Operation switch
        {
            CharacterTransformOperation.Set => VisualCharacterDraftOperation.Set,
            CharacterTransformOperation.Move => VisualCharacterDraftOperation.Move,
            CharacterTransformOperation.Reset => VisualCharacterDraftOperation.Reset,
            _ => throw new ArgumentOutOfRangeException(nameof(canonicalDirective))
        };
        return Result<VisualCharacterDraftRequest>.Ok(new VisualCharacterDraftRequest(
            command.PublicSlot,
            occupied,
            operation,
            command.Operation == CharacterTransformOperation.Move ? command.DeltaX : command.X,
            command.Operation == CharacterTransformOperation.Move ? command.DeltaY : command.Y,
            command.Operation == CharacterTransformOperation.Move
                ? command.DeltaRotationDegrees
                : command.RotationDegrees,
            command.FlipX,
            command.DurationMilliseconds,
            command.Easing)
        {
            RotationXDegrees = command.Operation == CharacterTransformOperation.Move
                ? command.DeltaRotationXDegrees : command.RotationXDegrees
        });
    }

    public Result<string> Build(VisualCharacterDraftRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.PublicSlot < CharacterTransformCommandValidator.MinimumPublicSlot
            || request.PublicSlot > CharacterTransformCommandValidator.MaximumPublicSlot)
        {
            return Result<string>.Fail("Physical character slot must be between 1 and 5.");
        }

        if (request.DurationMilliseconds < 0
            || request.DurationMilliseconds > CharacterTransformCommandValidator.MaximumDurationMilliseconds)
        {
            return Result<string>.Fail(
                $"Duration must be between 0 and {CharacterTransformCommandValidator.MaximumDurationMilliseconds} milliseconds.");
        }

        if (!AllFinite(request.X, request.Y, request.RotationDegrees, request.RotationXDegrees))
        {
            return Result<string>.Fail("Character transform values must be finite numbers.");
        }

        if (!request.Occupied && request.Operation == VisualCharacterDraftOperation.Reset)
        {
            return Result<string>.Fail("An empty slot cannot create a reset command.");
        }

        CharacterTransformOperation operation = request.Operation switch
        {
            VisualCharacterDraftOperation.Set => CharacterTransformOperation.Set,
            VisualCharacterDraftOperation.Move => CharacterTransformOperation.Move,
            VisualCharacterDraftOperation.Reset => CharacterTransformOperation.Reset,
            _ => throw new ArgumentOutOfRangeException(nameof(request))
        };
        var command = new CharacterTransformCommand(
            request.PublicSlot,
            operation,
            operation == CharacterTransformOperation.Set ? request.X : null,
            operation == CharacterTransformOperation.Set ? request.Y : null,
            operation == CharacterTransformOperation.Move ? request.X : null,
            operation == CharacterTransformOperation.Move ? request.Y : null,
            operation == CharacterTransformOperation.Set ? request.RotationDegrees : null,
            operation == CharacterTransformOperation.Move ? request.RotationDegrees : null,
            operation == CharacterTransformOperation.Set ? request.FlipX : null,
            request.Occupied ? request.DurationMilliseconds : 0,
            request.Occupied ? request.Easing : CharacterTransformEasing.Linear)
        {
            RotationXDegrees = operation == CharacterTransformOperation.Set ? request.RotationXDegrees : null,
            DeltaRotationXDegrees = operation == CharacterTransformOperation.Move ? request.RotationXDegrees : null
        };
        Result validation = CharacterTransformCommandValidator.Validate(command);
        if (!validation.Success)
        {
            return Result<string>.Fail(validation.Error);
        }

        var parts = new List<string>
        {
            "#aavt",
            request.Occupied ? "char" : "charPending",
            request.PublicSlot.ToString(CultureInfo.InvariantCulture),
            request.Operation switch
            {
                VisualCharacterDraftOperation.Set => "set",
                VisualCharacterDraftOperation.Move => "move",
                VisualCharacterDraftOperation.Reset => "reset",
                _ => throw new ArgumentOutOfRangeException(nameof(request))
            }
        };
        if (request.Operation == VisualCharacterDraftOperation.Set)
        {
            AddFloat(parts, "x", request.X);
            AddFloat(parts, "y", request.Y);
            AddFloat(parts, "rotation", request.RotationDegrees);
            AddFloat(parts, "rotationX", request.RotationXDegrees);
            if (request.FlipX.HasValue)
            {
                parts.Add($"flipX={request.FlipX.Value.ToString().ToLowerInvariant()}");
            }
        }
        else if (request.Operation == VisualCharacterDraftOperation.Move)
        {
            AddFloat(parts, "dx", request.X);
            AddFloat(parts, "dy", request.Y);
            AddFloat(parts, "drotation", request.RotationDegrees);
            AddFloat(parts, "drotationX", request.RotationXDegrees);
        }

        if (request.Occupied)
        {
            parts.Add($"duration={request.DurationMilliseconds.ToString(CultureInfo.InvariantCulture)}");
            parts.Add($"easing={EasingName(request.Easing)}");
        }

        return Result<string>.Ok(string.Join(';', parts));
    }

    public Result<string> BuildAbsolutePosition(
        int publicSlot,
        bool occupied,
        float x,
        float y,
        int durationMilliseconds,
        CharacterTransformEasing easing)
    {
        return Build(new VisualCharacterDraftRequest(
            publicSlot,
            occupied,
            VisualCharacterDraftOperation.Set,
            x,
            y,
            null,
            null,
            durationMilliseconds,
            easing));
    }

    private static void AddFloat(ICollection<string> parts, string name, float? value)
    {
        if (value.HasValue)
        {
            parts.Add($"{name}={Format(value.Value)}");
        }
    }

    private static bool AllFinite(params float?[] values) =>
        values.All(value => !value.HasValue || float.IsFinite(value.Value));

    private static string Format(float value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string EasingName(CharacterTransformEasing easing) => easing switch
    {
        CharacterTransformEasing.Linear => "linear",
        CharacterTransformEasing.EaseIn => "easeIn",
        CharacterTransformEasing.EaseOut => "easeOut",
        CharacterTransformEasing.EaseInOut => "easeInOut",
        _ => "linear"
    };
}
