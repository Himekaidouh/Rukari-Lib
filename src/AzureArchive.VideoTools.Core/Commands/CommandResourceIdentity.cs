using System.Globalization;
using AzureArchive.VideoTools.Core.Spines;

namespace AzureArchive.VideoTools.Core.Commands;

/// <summary>
/// What one canonical command occupies inside a scene (2026-09-21).
/// <para>
/// The camera is a singleton resource. A character transform, transient preset and spine overlay are separate
/// resources that happen to be addressed by the same public slot number, so a scene may carry both
/// <c>#aavt;char;3;…</c>, <c>#aavt;fx;3;…</c> and <c>#aavt;spine;3;…</c>.
/// </para>
/// <para>
/// An overlay is keyed by its track as well as its slot: the point of the reserved range is that a
/// card can drive several parts of one character at once (eyes on 20, arms on 21, a prop on 22),
/// which is only a conflict when two directives claim the <em>same</em> track. The key lives here
/// rather than in each validator because the extractor and the dispatch gate must agree exactly: a
/// rule that exists twice drifts.
/// </para>
/// </summary>
public static class CommandResourceIdentity
{
    public const string CameraKey = "camera";

    /// <summary>Shared scene budget for extraction, saved indexes and preview leases.</summary>
    internal static bool IsWithinSceneBudget(IEnumerable<string> commandTypes)
    {
        int count = 0;
        int overlays = 0;
        foreach (string commandType in commandTypes)
        {
            count++;
            if (commandType == SpineOverlayCommandFamilyCompiler.CommandTypeId) overlays++;
            if (count > EmbeddedAavtDirectiveExtractor.MaxCommandsPerScene || overlays > 10) return false;
        }

        return true;
    }

    public static string KeyFor(CanonicalTimelineCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.Equals(
                command.CommandType,
                SceneCameraCommandFamilyCompiler.CommandTypeId,
                StringComparison.Ordinal))
        {
            return CameraKey;
        }

        string slot = command.PublicSlot.ToString(CultureInfo.InvariantCulture);
        if (command.CommandType == CharacterPresetCommandFamilyCompiler.CommandTypeId)
            return "preset:" + slot;
        if (!string.Equals(
                command.CommandType,
                SpineOverlayCommandFamilyCompiler.CommandTypeId,
                StringComparison.Ordinal))
        {
            return "character:" + slot;
        }

        int track = SpineTrackOf(command.Directive);
        return track < 0
            ? "spine:" + slot
            : "spine:" + slot + ":" + track.ToString(CultureInfo.InvariantCulture);
    }

    public static string KeyFor(EmbeddedAavtCommand command) => KeyFor(new CanonicalTimelineCommand(
        EmbeddedProjectCommandCompiler.FamilyTypeIdFor(command.CanonicalDirective),
        string.Empty, command.CanonicalDirective, command.PublicSlot));

    /// <summary>
    /// The track an overlay directive claims, or -1 when it cannot be read.
    /// <para>
    /// The canonical text is the only place the track survives to this point, so it is read back
    /// from there. The preview lease needs the number itself (two overlays of one character are two
    /// resources); <see cref="KeyFor(CanonicalTimelineCommand)"/> needs only the key, and treats an
    /// unreadable track as the slot-only key, which conflicts with every other overlay on that slot
    /// — the fail-closed direction.
    /// </para>
    /// </summary>
    public static int SpineTrackOf(string canonicalDirective)
    {
        var parsed = new SpineOverlayDirectiveParser().Parse(canonicalDirective);
        return parsed.Success && parsed.Value != null ? parsed.Value.TrackIndex : -1;
    }
}
