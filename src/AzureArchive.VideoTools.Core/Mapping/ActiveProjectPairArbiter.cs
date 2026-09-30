using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Mapping;

public enum ActivePairArbitrationStatus
{
    Unique = 0,
    NotFound = 1,
    AmbiguousCrossProject = 2,
    NoUsableCandidates = 3
}

public sealed record ActivePairArbitration(
    ActivePairArbitrationStatus Status,
    DiscoveredProjectPair? Pair,
    ObservedSceneIdentityResolution? Resolution,
    int ScannedPairCount,
    IReadOnlyList<string> Diagnostics)
{
    public bool Success =>
        Status == ActivePairArbitrationStatus.Unique
        && Pair != null
        && Resolution != null;
}

/// <summary>
/// Decides which discovered AAP/AAS pair the currently observed editor script
/// belongs to. The compiled-script identity is the arbitration anchor: a scene
/// script maps into at most one project, so exactly one trusted Mapped hit is
/// required and anything else fails closed.
///
/// A sweep reads each pair's playback archive first and its project archive
/// only when that playback could carry the script, because project archives
/// dominate the cost of a first sweep. Both reads still go through the caller's
/// readers, so caching and freshness rules are unchanged.
/// </summary>
public sealed class ActiveProjectPairArbiter
{
    private readonly Func<string, Result<ProjectSnapshot>> _projectReader;
    private readonly Func<string, Result<PlaybackArchiveSnapshot>> _playbackReader;
    private readonly IObservedSceneIdentityResolver _resolver;
    private readonly IPlaybackScriptPreFilter? _preFilter;

    public ActiveProjectPairArbiter(
        Func<string, Result<ProjectSnapshot>> projectReader,
        Func<string, Result<PlaybackArchiveSnapshot>> playbackReader,
        IObservedSceneIdentityResolver? resolver = null)
    {
        _projectReader = projectReader ?? throw new ArgumentNullException(nameof(projectReader));
        _playbackReader = playbackReader ?? throw new ArgumentNullException(nameof(playbackReader));
        _resolver = resolver ?? new ObservedSceneIdentityResolver();
        _preFilter = _resolver as IPlaybackScriptPreFilter;
    }

    /// <summary>
    /// Resolves the identity within one specific pair without cross-project
    /// arbitration. Used for the sticky active-pair fast path: once a project
    /// is bound, its own scenes must keep working even when their scripts are
    /// too generic to be unique across projects.
    /// </summary>
    public Result<ObservedSceneIdentityResolution> TryResolvePair(
        DiscoveredProjectPair pair,
        ObservedCompiledSceneIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(pair);
        ArgumentNullException.ThrowIfNull(identity);

        Result<ProjectSnapshot> project = _projectReader(pair.AapPath);
        if (!project.Success || project.Value == null)
        {
            return Result<ObservedSceneIdentityResolution>.Fail(
                $"{pair.Name}: aap-unreadable");
        }

        Result<PlaybackArchiveSnapshot> playback = _playbackReader(pair.AasPath);
        if (!playback.Success || playback.Value == null)
        {
            return Result<ObservedSceneIdentityResolution>.Fail(
                $"{pair.Name}: aas-unreadable");
        }

        return _resolver.Resolve(identity, project.Value, playback.Value);
    }

    public ActivePairArbitration Arbitrate(
        ObservedCompiledSceneIdentity identity,
        IReadOnlyList<DiscoveredProjectPair> candidates)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(candidates);

        var diagnostics = new List<string>();
        List<DiscoveredProjectPair> usable = candidates
            .Where(pair => pair.PlaybackPresent)
            .ToList();
        if (usable.Count == 0)
        {
            return new ActivePairArbitration(
                ActivePairArbitrationStatus.NoUsableCandidates,
                null,
                null,
                candidates.Count,
                new[]
                {
                    $"none of {candidates.Count} discovered pairs has a same-name AAS playback file."
                });
        }

        DiscoveredProjectPair? hitPair = null;
        ObservedSceneIdentityResolution? hitResolution = null;
        foreach (DiscoveredProjectPair pair in usable)
        {
            // Playback archives are compact binary and project archives are
            // JSON, so the project archive is parsed last and only for a pair
            // whose playback could actually carry the observed script. When no
            // pre-filter is available this simply keeps reading both.
            Result<PlaybackArchiveSnapshot> playback = _playbackReader(pair.AasPath);
            if (!playback.Success || playback.Value == null)
            {
                diagnostics.Add($"{pair.Name}: aas-unreadable");
                continue;
            }

            if (_preFilter != null)
            {
                Result<bool> possible = _preFilter.MayContainScript(identity, playback.Value);
                if (!possible.Success)
                {
                    diagnostics.Add($"{pair.Name}: {possible.Error}");
                    continue;
                }

                if (!possible.Value)
                {
                    diagnostics.Add(
                        $"{pair.Name}: status={ObservedSceneIdentityStatus.NotFound}");
                    continue;
                }
            }

            Result<ProjectSnapshot> project = _projectReader(pair.AapPath);
            if (!project.Success || project.Value == null)
            {
                diagnostics.Add($"{pair.Name}: aap-unreadable");
                continue;
            }

            Result<ObservedSceneIdentityResolution> resolved = _resolver.Resolve(
                identity,
                project.Value,
                playback.Value);
            if (!resolved.Success || resolved.Value == null)
            {
                diagnostics.Add($"{pair.Name}: {resolved.Error}");
                continue;
            }

            ObservedSceneIdentityResolution resolution = resolved.Value;
            if (resolution.Status != ObservedSceneIdentityStatus.Mapped
                || !resolution.SelectedSceneTrusted)
            {
                diagnostics.Add($"{pair.Name}: status={resolution.Status}");
                continue;
            }

            if (hitPair != null)
            {
                diagnostics.Add($"also-matched={pair.Name}");
                return new ActivePairArbitration(
                    ActivePairArbitrationStatus.AmbiguousCrossProject,
                    null,
                    null,
                    usable.Count,
                    diagnostics);
            }

            hitPair = pair;
            hitResolution = resolution;
        }

        if (hitPair == null || hitResolution == null)
        {
            return new ActivePairArbitration(
                ActivePairArbitrationStatus.NotFound,
                null,
                null,
                usable.Count,
                diagnostics);
        }

        return new ActivePairArbitration(
            ActivePairArbitrationStatus.Unique,
            hitPair,
            hitResolution,
            usable.Count,
            diagnostics);
    }
}
