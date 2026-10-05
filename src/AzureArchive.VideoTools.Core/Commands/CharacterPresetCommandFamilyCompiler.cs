using System.Globalization;
using AzureArchive.VideoTools.Core.Characters;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Commands;

public sealed class CharacterPresetCommandFamilyCompiler : ICommandFamilyCompiler
{
    public const string CommandTypeId = "scene.characterPreset/v1";
    public const string CapabilityId = "Player.CharacterPreset.Dispatch";
    public const string CanonicalRootToken = "#fx";
    public const string PublicNamespaceToken = "fx";
    private readonly ICharacterPresetDirectiveParser _parser;

    public CharacterPresetCommandFamilyCompiler(ICharacterPresetDirectiveParser? parser = null)
    {
        _parser = parser ?? new CharacterPresetDirectiveParser();
    }

    public string CommandType => CommandTypeId;
    public string RequiredCapability => CapabilityId;

    public Result<CanonicalTimelineCommand> Canonicalize(string directive)
    {
        Result<CharacterPresetCommand> parsed = _parser.Parse(directive);
        if (!parsed.Success || parsed.Value == null)
        {
            return Result<CanonicalTimelineCommand>.Fail($"Character preset directive is invalid: {parsed.Error}");
        }

        CharacterPresetCommand command = parsed.Value;
        Result validation = CharacterPresetCommandValidator.Validate(command);
        if (!validation.Success)
        {
            return Result<CanonicalTimelineCommand>.Fail(validation.Error);
        }

        var parts = new List<string>
        {
            CanonicalRootToken,
            command.PublicSlot.ToString(CultureInfo.InvariantCulture),
            command.Kind.ToString().ToLowerInvariant()
        };
        if (command.Kind == CharacterPresetKind.Headbutt)
        {
            AddNumber(parts, "back", command.BackDegrees);
            AddNumber(parts, "forward", command.ForwardDegrees);
            parts.Add($"duration={command.DurationMilliseconds.ToString(CultureInfo.InvariantCulture)}");
        }
        else
        {
            if (command.Kind != CharacterPresetKind.Spin)
            {
                AddNumber(parts, "amplitude", command.Amplitude);
            }

            AddNumber(parts, "frequency", command.FrequencyHz);
            parts.Add($"cycles={command.Cycles.ToString(CultureInfo.InvariantCulture)}");
        }

        if (command.Kind == CharacterPresetKind.Spin && command.SpinAxis == CharacterPresetSpinAxis.X)
        {
            parts.Add("axis=x");
        }

        parts.Add(command.Direction < 0 ? "direction=left" : "direction=right");
        if (command.Bezier is { } bezier)
        {
            parts.Add($"bezier={bezier.ToDirectiveValue()}");
        }

        return Result<CanonicalTimelineCommand>.Ok(new CanonicalTimelineCommand(
            CommandTypeId, CapabilityId, string.Join(';', parts), command.PublicSlot));
    }

    private static void AddNumber(ICollection<string> parts, string name, float value)
    {
        float normalized = value == 0f ? 0f : value;
        parts.Add($"{name}={normalized.ToString("R", CultureInfo.InvariantCulture)}");
    }
}
