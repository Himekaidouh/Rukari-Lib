using System.Globalization;
using AzureArchive.VideoTools.Core.Results;
using AzureArchive.VideoTools.Core.Spines;

namespace AzureArchive.VideoTools.Core.Commands;

/// <summary>
/// The <c>#aavt;spine</c> family (2026-09-21): put an animation of the character's own skeleton on
/// one of the reserved overlay tracks, or clear that track again.
/// <para>
/// The canonical form carries every field explicitly, in a fixed order, because the editor preview
/// lease compares the directive it received with the one this compiler produces — a directive that
/// is merely equivalent is not canonical.
/// </para>
/// </summary>
public sealed class SpineOverlayCommandFamilyCompiler : ICommandFamilyCompiler
{
    public const string CommandTypeId = "scene.spineOverlay/v1";
    public const string CapabilityId = "Player.SpineOverlay.Dispatch";
    public const string CanonicalRootToken = SpineOverlayDirectiveParser.CanonicalRootToken;

    /// <summary>What the author writes after <c>#aavt;</c>.</summary>
    public const string PublicNamespaceToken = "spine";

    private readonly ISpineOverlayDirectiveParser _parser;

    public SpineOverlayCommandFamilyCompiler(ISpineOverlayDirectiveParser? parser = null)
    {
        _parser = parser ?? new SpineOverlayDirectiveParser();
    }

    public string CommandType => CommandTypeId;

    public string RequiredCapability => CapabilityId;

    public Result<CanonicalTimelineCommand> Canonicalize(string directive)
    {
        Result<SpineOverlayCommand> parsed = _parser.Parse(directive);
        if (!parsed.Success || parsed.Value == null)
        {
            return Result<CanonicalTimelineCommand>.Fail(
                $"Spine overlay directive is invalid: {parsed.Error}");
        }

        SpineOverlayCommand command = parsed.Value;
        var parts = new List<string>
        {
            CanonicalRootToken,
            command.PublicSlot.ToString(CultureInfo.InvariantCulture)
        };

        if (command.Operation == SpineOverlayOperation.Clear)
        {
            parts.Add("clear");
            parts.Add(Fade(command.FadeMilliseconds));
            if (command.TrackIndex != SpineOverlayTrackPolicy.DefaultTrack)
            {
                // Only a clear that leaves the default track has to say so; the text a plain
                // '#spine;3;clear' produced before this existed stays byte-identical.
                parts.Add($"track={command.TrackIndex.ToString(CultureInfo.InvariantCulture)}");
            }
        }
        else
        {
            parts.Add("play");
            parts.Add($"animation={command.AnimationName}");
            parts.Add($"track={command.TrackIndex.ToString(CultureInfo.InvariantCulture)}");
            parts.Add($"mix={command.MixMilliseconds.ToString(CultureInfo.InvariantCulture)}");
            parts.Add(Fade(command.FadeMilliseconds));
            parts.Add($"loop={(command.Loop ? "true" : "false")}");
            parts.Add($"blend={Blend(command.Blend)}");
            if (command.Hold.HasValue)
            {
                // Only an explicit hold is written: a directive that never mentioned it has to
                // canonicalize to exactly what earlier builds produced.
                parts.Add($"hold={(command.Hold.Value ? "true" : "false")}");
            }
        }

        return Result<CanonicalTimelineCommand>.Ok(new CanonicalTimelineCommand(
            CommandTypeId,
            CapabilityId,
            string.Join(';', parts),
            command.PublicSlot));
    }

    private static string Fade(int milliseconds) =>
        $"fade={milliseconds.ToString(CultureInfo.InvariantCulture)}";

    private static string Blend(SpineOverlayBlend blend) => blend switch
    {
        SpineOverlayBlend.Replace => "replace",
        SpineOverlayBlend.Add => "add",
        _ => throw new ArgumentOutOfRangeException(nameof(blend))
    };
}
