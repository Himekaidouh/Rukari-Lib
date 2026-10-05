using System.Globalization;
using AzureArchive.VideoTools.Core.Cameras;
using AzureArchive.VideoTools.Core.Characters;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Commands;

public sealed class SceneCameraCommandFamilyCompiler : ICommandFamilyCompiler
{
    public const string CommandTypeId = "scene.camera/v1";
    public const string CapabilityId = "Player.SceneCamera.Dispatch";
    public const string CanonicalRootToken = "#camera";
    public const string PublicNamespaceToken = "camera";
    public const int SingletonResourceSlot = 0;
    public const int BackgroundResourceSlot = 6;

    public static int ResourceSlotFor(SceneCameraScope scope) => scope switch
    {
        SceneCameraScope.Overall => SingletonResourceSlot,
        SceneCameraScope.Background => BackgroundResourceSlot,
        _ => throw new ArgumentOutOfRangeException(nameof(scope))
    };

    public static bool IsCameraResourceSlot(int slot) =>
        slot is SingletonResourceSlot or BackgroundResourceSlot;

    private readonly ISceneCameraDirectiveParser _parser;

    public SceneCameraCommandFamilyCompiler(ISceneCameraDirectiveParser? parser = null)
    {
        _parser = parser ?? new SceneCameraDirectiveParser();
    }

    public string CommandType => CommandTypeId;

    public string RequiredCapability => CapabilityId;

    public Result<CanonicalTimelineCommand> Canonicalize(string directive)
    {
        Result<SceneCameraCommand> parsed = _parser.Parse(directive);
        if (!parsed.Success || parsed.Value == null)
        {
            return Result<CanonicalTimelineCommand>.Fail(
                $"Scene camera directive is invalid: {parsed.Error}");
        }

        SceneCameraCommand command = parsed.Value;
        var parts = new List<string> { CanonicalRootToken, Operation(command.Operation) };
        // Legacy overall text stays byte-for-byte canonical as before.
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
        return Result<CanonicalTimelineCommand>.Ok(new(
            CommandTypeId,
            CapabilityId,
            string.Join(';', parts),
            ResourceSlotFor(command.Scope)));
    }

    private static void AddFloat(ICollection<string> parts, string name, float? value)
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
