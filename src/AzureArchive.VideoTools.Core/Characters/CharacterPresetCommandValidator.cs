using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Characters;

public static class CharacterPresetCommandValidator
{
    public const float MinimumFrequencyHz = 0.1f;
    public const float MaximumFrequencyHz = 10f;
    public const float MaximumSpinFrequencyHz = 5f;
    public const int MaximumCycles = 100;
    public const float MaximumSwayAmplitude = 90f;
    public const float MaximumSquashAmplitude = 0.7f;
    public const float MaximumBackDegrees = 90f;
    public const float MaximumForwardDegrees = 120f;
    public const int MinimumHeadbuttDurationMilliseconds = 100;
    public const int MaximumHeadbuttDurationMilliseconds = 10000;

    public static Result Validate(CharacterPresetCommand? command)
    {
        if (command == null)
        {
            return Result.Fail("Character preset command is missing.");
        }

        if (command.PublicSlot is < 1 or > 5)
        {
            return Result.Fail("Character preset slot must be between 1 and 5.");
        }

        if (command.Kind is not (CharacterPresetKind.Sway or CharacterPresetKind.Spin
            or CharacterPresetKind.Headbutt or CharacterPresetKind.Squash))
        {
            return Result.Fail("Unknown character preset kind.");
        }

        if (command.Direction is not (-1 or 1))
        {
            return Result.Fail("Character preset direction must be left or right.");
        }

        if (command.SpinAxis is not (CharacterPresetSpinAxis.Y or CharacterPresetSpinAxis.X))
        {
            return Result.Fail("Character preset spin axis must be X or Y.");
        }

        if (command.Kind != CharacterPresetKind.Spin && command.SpinAxis != CharacterPresetSpinAxis.Y)
        {
            return Result.Fail("A custom spin axis is valid only for the spin preset.");
        }

        if (command.Bezier is { IsValid: false })
        {
            return Result.Fail("Character preset Bezier coordinates must be finite numbers between 0 and 1.");
        }

        if (!float.IsFinite(command.Amplitude) || !float.IsFinite(command.FrequencyHz)
            || !float.IsFinite(command.BackDegrees) || !float.IsFinite(command.ForwardDegrees))
        {
            return Result.Fail("Character preset values must be finite numbers.");
        }

        if (command.Kind == CharacterPresetKind.Headbutt)
        {
            if (command.BackDegrees is < 0f or > MaximumBackDegrees
                || command.ForwardDegrees is < 0f or > MaximumForwardDegrees)
            {
                return Result.Fail("Headbutt back angle must be 0..90 and forward angle 0..120 degrees.");
            }

            return command.DurationMilliseconds is >= MinimumHeadbuttDurationMilliseconds
                and <= MaximumHeadbuttDurationMilliseconds
                ? Result.Ok()
                : Result.Fail("Headbutt duration must be between 100 and 10000 milliseconds.");
        }

        float maximumFrequency = command.Kind == CharacterPresetKind.Spin
            ? MaximumSpinFrequencyHz : MaximumFrequencyHz;
        if (command.FrequencyHz < MinimumFrequencyHz || command.FrequencyHz > maximumFrequency)
        {
            return Result.Fail(command.Kind == CharacterPresetKind.Spin
                ? "Spin frequency must be between 0.1 and 5 Hz."
                : "Character preset frequency must be between 0.1 and 10 Hz.");
        }

        if (command.Cycles is < 0 or > MaximumCycles)
        {
            return Result.Fail("Character preset cycles must be between 0 and 100 (0 means until next dialogue).");
        }

        if (command.Kind == CharacterPresetKind.Sway
            && command.Amplitude is < 0f or > MaximumSwayAmplitude)
        {
            return Result.Fail("Sway amplitude must be between 0 and 90 degrees.");
        }

        if (command.Kind == CharacterPresetKind.Squash
            && command.Amplitude is < 0f or > MaximumSquashAmplitude)
        {
            return Result.Fail("Squash amplitude must be between 0 and 0.7.");
        }

        return Result.Ok();
    }
}
