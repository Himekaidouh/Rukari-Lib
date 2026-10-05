using System.Globalization;
using AzureArchive.VideoTools.Core.Cameras;
using AzureArchive.VideoTools.Core.Characters;
using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.VisualEditor;

public sealed class VisualCameraDraftBuilder
{
    private readonly SceneCameraDirectiveParser _parser = new();
    private readonly SceneCameraCommandFamilyCompiler _compiler = new();

    public Result<string> BuildSet(
        SceneCameraState state,
        int durationMilliseconds,
        CharacterTransformEasing easing) => Build(new SceneCameraCommand(
            SceneCameraOperation.Set,
            state.X,
            state.Y,
            DeltaX: null,
            DeltaY: null,
            state.Zoom,
            DeltaZoom: null,
            durationMilliseconds,
            easing));

    public Result<string> BuildReset(
        int durationMilliseconds,
        CharacterTransformEasing easing) => Build(new SceneCameraCommand(
            SceneCameraOperation.Reset,
            X: null,
            Y: null,
            DeltaX: null,
            DeltaY: null,
            Zoom: null,
            DeltaZoom: null,
            durationMilliseconds,
            easing));

    public Result<string> BuildSet(
        SceneCameraScope scope,
        SceneCameraState state,
        int durationMilliseconds,
        CharacterTransformEasing easing) => Build(new SceneCameraCommand(
            SceneCameraOperation.Set, state.X, state.Y, null, null,
            state.Zoom, null, durationMilliseconds, easing) { Scope = scope });

    public Result<string> BuildReset(
        SceneCameraScope scope,
        int durationMilliseconds,
        CharacterTransformEasing easing) => Build(new SceneCameraCommand(
            SceneCameraOperation.Reset, null, null, null, null, null, null,
            durationMilliseconds, easing) { Scope = scope });

    public Result<string> Build(SceneCameraCommand command)
    {
        Result validation = SceneCameraCommandValidator.Validate(command);
        if (!validation.Success)
        {
            return Result<string>.Fail(validation.Error);
        }

        var parts = new List<string>
        {
            SceneCameraCommandFamilyCompiler.CanonicalRootToken,
            Operation(command.Operation)
        };
        if (command.Scope == SceneCameraScope.Background) parts.Add("scope=background");
        if (command.Operation == SceneCameraOperation.Set)
        {
            AddFloat(parts, "x", command.X);
            AddFloat(parts, "y", command.Y);
            AddFloat(parts, "zoom", command.Zoom);
        }
        else if (command.Operation == SceneCameraOperation.Move)
        {
            AddFloat(parts, "dx", command.DeltaX);
            AddFloat(parts, "dy", command.DeltaY);
            AddFloat(parts, "dzoom", command.DeltaZoom);
        }

        parts.Add($"duration={command.DurationMilliseconds.ToString(CultureInfo.InvariantCulture)}");
        parts.Add($"easing={Easing(command.Easing)}");
        Result<CanonicalTimelineCommand> canonical = _compiler.Canonicalize(
            string.Join(';', parts));
        if (!canonical.Success || canonical.Value == null)
        {
            return Result<string>.Fail(canonical.Error);
        }

        const string prefix = "#camera;";
        return !canonical.Value.Directive.StartsWith(prefix, StringComparison.Ordinal)
            ? Result<string>.Fail("Canonical scene camera directive has an unexpected root token.")
            : Result<string>.Ok(
                "#aavt;"
                + SceneCameraCommandFamilyCompiler.PublicNamespaceToken
                + ";"
                + canonical.Value.Directive[prefix.Length..]);
    }

    public Result<SceneCameraCommand> ReadCanonical(string canonicalDirective) =>
        _parser.Parse(canonicalDirective);

    private static void AddFloat(
        ICollection<string> parts,
        string name,
        float? value)
    {
        if (value.HasValue)
        {
            float normalized = value.Value == 0f ? 0f : value.Value;
            parts.Add($"{name}={normalized.ToString("R", CultureInfo.InvariantCulture)}");
        }
    }

    private static string Operation(SceneCameraOperation operation) => operation switch
    {
        SceneCameraOperation.Set => "set",
        SceneCameraOperation.Move => "move",
        SceneCameraOperation.Reset => "reset",
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
