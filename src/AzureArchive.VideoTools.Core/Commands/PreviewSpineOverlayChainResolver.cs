using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Core.Results;
using AzureArchive.VideoTools.Core.Spines;

namespace AzureArchive.VideoTools.Core.Commands;

/// <summary>Overlays one scene inherits from its ancestors, per (slot, track).</summary>
public sealed record PreviewSpineOverlayChainResolution(
    IReadOnlyList<PreviewChainSpineOverlay> Overlays,
    int FoldedSceneCount,
    PreviewChainStopReason StopReason,
    IReadOnlyList<string> Diagnostics)
{
    /// <summary>True when at least one ancestor scene carried an overlay.</summary>
    public bool HasInheritedOverlays => Overlays.Count != 0;
}

/// <summary>
/// Folds ancestor <c>#aavt;spine</c> directives into the overlays a scene is already holding in
/// sequential playback (2026-09-21).
/// <para>
/// Playback inherits overlays for free: the skeletons of a scene are not rebuilt between its cards,
/// so an overlay set three cards ago is still on its track. The editor preview rebuilds the scene on
/// every click, so the same card previewed from a bare idle pose. This resolver reproduces the
/// playback state statically from the project graph, exactly as the camera resolver does.
/// </para>
/// <para>
/// Boundaries follow the camera chain (project start, ambiguous incoming, cycle guard, depth limit)
/// and deliberately not the character rules: an occupant change or an official position move does
/// not stop an overlay, because neither clears a track during playback. Folding is per
/// <c>(slot, track)</c>: a later directive replaces an earlier one, and a <c>clear</c> removes the
/// key entirely. The current scene's own directives are not folded here — the runtime applies them
/// after the inherited ones, which is what makes them win.
/// </para>
/// </summary>
public sealed class PreviewSpineOverlayChainResolver
{
    private const int MaximumWalkDepth = 512;

    private readonly StoryGraphPredecessorResolver _graph;
    private readonly SpineOverlayDirectiveParser _parser = new();

    public PreviewSpineOverlayChainResolver(ProjectSnapshot project)
    {
        ArgumentNullException.ThrowIfNull(project);
        _graph = new StoryGraphPredecessorResolver(project);
    }

    public Result<PreviewSpineOverlayChainResolution> Resolve(
        SceneKey sceneKey,
        Func<SceneKey, IReadOnlyDictionary<string, string>?> sceneOverlayLookup)
    {
        ArgumentNullException.ThrowIfNull(sceneKey);
        ArgumentNullException.ThrowIfNull(sceneOverlayLookup);

        // Ancestors are visited newest-first, so their overlay sets are collected here and folded in
        // reverse: that is the order the player would have walked them, and it is what makes the
        // newest directive the one that survives.
        var collected = new List<IReadOnlyDictionary<string, string>>();
        var diagnostics = new List<string>();
        var visitedScenes = new HashSet<SceneKey> { sceneKey };
        SceneKey cursor = sceneKey;
        PreviewChainStopReason stopReason = PreviewChainStopReason.ProjectStart;
        for (int depth = 0; depth < MaximumWalkDepth; depth++)
        {
            Result<StoryPredecessorResolution> predecessor =
                _graph.ResolvePredecessor(cursor.NodeGuid, cursor.SceneIndex);
            if (!predecessor.Success || predecessor.Value == null)
            {
                diagnostics.Add(predecessor.Error);
                stopReason = PreviewChainStopReason.CycleGuard;
                break;
            }

            if (predecessor.Value.Status == StoryPredecessorStatus.ProjectStart)
            {
                stopReason = PreviewChainStopReason.ProjectStart;
                break;
            }

            if (predecessor.Value.Status == StoryPredecessorStatus.AmbiguousIncoming)
            {
                diagnostics.Add($"incoming-node-count={predecessor.Value.IncomingNodeCount}");
                stopReason = PreviewChainStopReason.AmbiguousGraph;
                break;
            }

            SceneKey ancestorKey = predecessor.Value.Predecessor!;
            if (!visitedScenes.Add(ancestorKey))
            {
                stopReason = PreviewChainStopReason.CycleGuard;
                break;
            }

            IReadOnlyDictionary<string, string>? overlays = sceneOverlayLookup(ancestorKey);
            if (overlays is { Count: > 0 })
            {
                collected.Add(overlays);
            }

            cursor = ancestorKey;
        }

        var folded = new Dictionary<string, PreviewChainSpineOverlay>(StringComparer.Ordinal);
        for (int index = collected.Count - 1; index >= 0; index--)
        {
            foreach (KeyValuePair<string, string> entry in collected[index])
            {
                int separator = entry.Key.IndexOf(':');
                if (separator <= 0) continue;
                if (!int.TryParse(entry.Key[..separator], out int publicSlot)) continue;
                if (!int.TryParse(entry.Key[(separator + 1)..], out int trackIndex)) continue;

                Result<SpineOverlayCommand> parsed = _parser.Parse(entry.Value);
                if (!parsed.Success || parsed.Value == null)
                {
                    diagnostics.Add($"overlay directive could not be folded: {parsed.Error}");
                    continue;
                }

                if (parsed.Value.Operation == SpineOverlayOperation.Clear)
                {
                    // A clear ends the overlay for that track: everything older is irrelevant.
                    folded.Remove(entry.Key);
                    continue;
                }

                folded[entry.Key] = new PreviewChainSpineOverlay(
                    publicSlot,
                    trackIndex,
                    entry.Value);
            }
        }

        PreviewChainSpineOverlay[] ordered = folded.Values
            .OrderBy(overlay => overlay.PublicSlot)
            .ThenBy(overlay => overlay.TrackIndex)
            .ToArray();
        return Result<PreviewSpineOverlayChainResolution>.Ok(new(
            Array.AsReadOnly(ordered),
            collected.Count,
            stopReason,
            Array.AsReadOnly(diagnostics.ToArray())));
    }
}
