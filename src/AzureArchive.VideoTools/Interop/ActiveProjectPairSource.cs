using AzureArchive.VideoTools.Core.Mapping;
using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Core.Results;
using AzureArchive.VideoTools.Runtime;

namespace AzureArchive.VideoTools.Interop;

internal sealed record ActivePairBinding(
    DiscoveredProjectPair Pair,
    ProjectSnapshot Project,
    PlaybackArchiveSnapshot Playback,
    ObservedSceneIdentityResolution Resolution);

/// <summary>The projection and its cache key captured from one immutable registry revision.</summary>
internal sealed record EditorIdentityProjectionSnapshot(Func<string, string> Project, string CacheKey);

/// <summary>
/// AutoAllProjects: discovers same-name AAP/AAS pairs under the game data
/// root and arbitrates which pair the currently observed editor script
/// belongs to. The compiled-script identity is the only anchor; anything
/// other than exactly one trusted mapping fails closed with a stable reason.
/// </summary>
internal static class ActiveProjectPairSource
{
    private static readonly object Gate = new();
    private static readonly ProjectPairScanner Scanner = new();
    private static readonly ArchiveSnapshotCache Cache = new();
    private static readonly HashSet<string> LoggedOnce = new(StringComparer.Ordinal);
    private static Func<string, Result<ProjectSnapshot>>? _projectReader;
    private static Func<string, Result<PlaybackArchiveSnapshot>>? _playbackReader;
    private static Func<string, string>? _compiledScriptProjection;
    private static Func<string>? _projectionCacheKey;
    private static Func<EditorIdentityProjectionSnapshot>? _projectionSnapshot;
    private static bool _enabled;
    private static string _dataRootOverride = string.Empty;
    private static string _persistentDataPath = string.Empty;
    private static DiscoveredProjectPair? _activePair;
    private static bool _pairChangedSinceLastConsume;

    public static bool Enabled
    {
        get { lock (Gate) { return _enabled; } }
    }

    public static bool HasActivePair
    {
        get { lock (Gate) { return _activePair != null; } }
    }

    public static void Configure(
        bool enabled,
        string dataRootOverride,
        Func<string, Result<ProjectSnapshot>> projectReader,
        Func<string, Result<PlaybackArchiveSnapshot>> playbackReader,
        Func<string, string>? compiledScriptProjection,
        Func<string>? projectionCacheKey = null)
    {
        ArgumentNullException.ThrowIfNull(projectReader);
        ArgumentNullException.ThrowIfNull(playbackReader);
        lock (Gate)
        {
            _enabled = enabled;
            _dataRootOverride = dataRootOverride ?? string.Empty;
            _projectReader = projectReader;
            _playbackReader = playbackReader;
            _compiledScriptProjection = compiledScriptProjection;
            _projectionCacheKey = projectionCacheKey;
            _projectionSnapshot = null;
        }

        Plugin.Logger.LogInfo(
            $"Auto project discovery configured: enabled={enabled}; "
            + $"dataRootOverride={(string.IsNullOrWhiteSpace(dataRootOverride) ? "none" : "set")}; "
            + "pairing=same-name-aap-aas; arbitration=compiled-script-identity; "
            + "cache=mtime-and-length-validated; "
            + $"scriptIndex={(projectionCacheKey == null ? "record-scan" : "cached-per-snapshot")}.");
    }

    public static void Configure(
        bool enabled,
        string dataRootOverride,
        Func<string, Result<ProjectSnapshot>> projectReader,
        Func<string, Result<PlaybackArchiveSnapshot>> playbackReader,
        Func<string, string>? compiledScriptProjection,
        Func<string>? projectionCacheKey,
        Func<EditorIdentityProjectionSnapshot> projectionSnapshot)
    {
        ArgumentNullException.ThrowIfNull(projectionSnapshot);
        Configure(enabled, dataRootOverride, projectReader, playbackReader,
            compiledScriptProjection, projectionCacheKey);
        lock (Gate) { _projectionSnapshot = projectionSnapshot; }
    }

    /// <summary>Must be called on the Unity main thread with a real path.</summary>
    public static void SetPersistentDataPath(string persistentDataPath)
    {
        if (string.IsNullOrWhiteSpace(persistentDataPath))
        {
            return;
        }

        bool announce;
        lock (Gate)
        {
            announce = !string.Equals(
                _persistentDataPath,
                persistentDataPath,
                StringComparison.Ordinal);
            _persistentDataPath = persistentDataPath;
        }

        if (announce)
        {
            Plugin.Logger.LogInfo(
                $"Auto project discovery data root source: persistentDataPath={persistentDataPath}");
        }
    }

    public static DiscoveredProjectPair? ActivePairSnapshot()
    {
        lock (Gate)
        {
            return _activePair;
        }
    }

    public static Result<ActivePairBinding> ResolveActive(
        ObservedCompiledSceneIdentity identity)
    {
        // Timed (2026-09-19): this scan and its arbitration run on the game's thread, and the
        // moment it first runs is when the editor binds a project — the moment the user reports
        // the game going unresponsive. Only slow resolutions reach the diagnostics log.
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        Result<ActivePairBinding> result = ResolveActiveCore(identity);
        long elapsed = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000L
            / System.Diagnostics.Stopwatch.Frequency;
        if (elapsed >= SlowResolutionMilliseconds)
        {
            PlayerCommandObservationLog.Append(
                $"{DateTimeOffset.Now:O} auto-discover slow=true; elapsedMs={elapsed}; "
                + $"success={result.Success}; "
                + (result.Success && result.Value != null
                    ? $"project={result.Value.Pair.Name}"
                    : $"reason={result.Error}"));
        }

        return result;
    }

    /// <summary>Resolution cost worth a line in the diagnostics log (steady state is 3–12 ms,
    /// a cold first resolution measures ~610 ms).</summary>
    private const int SlowResolutionMilliseconds = 50;

    private static Result<ActivePairBinding> ResolveActiveCore(
        ObservedCompiledSceneIdentity identity)
    {
        bool enabled;
        string dataRootOverride;
        lock (Gate)
        {
            enabled = _enabled;
            dataRootOverride = _dataRootOverride;
        }

        if (!enabled)
        {
            return Result<ActivePairBinding>.Fail(
                "reason=auto-disabled; auto discovery is not enabled.");
        }

        if (!TryResolveDataRoot(dataRootOverride, out string dataRoot))
        {
            return Result<ActivePairBinding>.Fail(
                "reason=auto-data-root-unavailable; no data root override is configured "
                + "and the game persistent data path has not been captured yet.");
        }

        // One directory enumeration stamps every archive under this root for
        // the whole resolution. Validating each of the ~230 files by itself
        // costs a separate file system call per file, which dominates an
        // otherwise cheap sweep on machines with a filter driver in the path.
        DataRootStampSet stamps = DataRootStampSet.Capture(dataRoot);
        ActiveProjectPairArbiter arbiter = BuildArbiter(stamps);

        Result<IReadOnlyList<DiscoveredProjectPair>> scanned = Scanner.Scan(
            dataRoot,
            stamps);
        if (!scanned.Success || scanned.Value == null)
        {
            return Result<ActivePairBinding>.Fail(
                $"reason=auto-scan-failed; {scanned.Error}");
        }

        // Sticky fast path: once a pair is bound, its own scenes resolve
        // inside that pair alone. Generic scripts (a bare "#3;h" scene exists
        // in dozens of projects) can never arbitrate across projects, so the
        // full scan must not be the first resort for an already-bound editor.
        DiscoveredProjectPair? preferred = ActivePairSnapshot();
        if (preferred != null
            && scanned.Value.Any(candidate =>
                string.Equals(
                    candidate.AapPath,
                    preferred.AapPath,
                    StringComparison.OrdinalIgnoreCase)))
        {
            Result<ObservedSceneIdentityResolution> fast =
                arbiter.TryResolvePair(preferred, identity);
            if (fast.Success
                && fast.Value != null
                && fast.Value.Status == ObservedSceneIdentityStatus.Mapped
                && fast.Value.SelectedSceneTrusted)
            {
                return MakeBinding(preferred, fast.Value, stamps);
            }
        }

        ActivePairArbitration arbitration = arbiter.Arbitrate(
            identity,
            scanned.Value);
        if (arbitration.Status != ActivePairArbitrationStatus.Unique)
        {
            // NotFound or AmbiguousCrossProject. If a pair is already bound,
            // the editor is almost certainly still inside that project and
            // merely clicked a scene whose script is too generic (or too new
            // for a stale AAS) to arbitrate across projects. Unbinding here
            // would break every later scene, so the binding is kept and the
            // per-scene trust decision is left to the existing guards.
            DiscoveredProjectPair? kept = ActivePairSnapshot();
            if (kept == null)
            {
                if (arbitration.Status == ActivePairArbitrationStatus.AmbiguousCrossProject)
                {
                    LogOnce(
                        "auto-ambiguous",
                        "Auto project discovery is ambiguous: the observed scene script maps "
                        + "into more than one project pair; refusing to bind. "
                        + string.Join(" | ", arbitration.Diagnostics));
                    return Result<ActivePairBinding>.Fail(
                        "reason=auto-ambiguous-cross-project; "
                        + string.Join(" | ", arbitration.Diagnostics));
                }

                string noCandidateDetail = arbitration.Status
                    == ActivePairArbitrationStatus.NoUsableCandidates
                    ? (arbitration.Diagnostics.Count > 0
                        ? arbitration.Diagnostics[0]
                        : "no diagnostics")
                    : $"scannedPairs={arbitration.ScannedPairCount}; "
                        + string.Join(" | ", arbitration.Diagnostics.Take(3));
                return Result<ActivePairBinding>.Fail(
                    $"reason=auto-not-found; {noCandidateDetail}");
            }

            LogOnce(
                $"auto-kept-{kept.Name}",
                "Auto project discovery kept the active binding '" + kept.Name
                + "' because this scene's script could not be uniquely arbitrated ("
                + arbitration.Status + "). Scenes with unique dialogue re-confirm it.");
            return ResolveWithinKeptPair(arbiter, kept, identity, stamps);
        }

        DiscoveredProjectPair pair = arbitration.Pair!;
        bool changed;
        lock (Gate)
        {
            changed = _activePair == null
                || !string.Equals(
                    _activePair.AapPath,
                    pair.AapPath,
                    StringComparison.OrdinalIgnoreCase);
            _activePair = pair;
            if (changed)
            {
                _pairChangedSinceLastConsume = true;
            }
        }

        if (changed)
        {
            PlayerCommandObservationLog.Append(
                $"{DateTimeOffset.Now:O} auto-discover status=READY; project={pair.Name}; "
                + $"scannedPairs={arbitration.ScannedPairCount}; "
                + $"aap={pair.AapPath}; aas={pair.AasPath}");
            Plugin.Logger.LogInfo(
                $"Auto project discovery bound the active pair: project={pair.Name}; "
                + $"scannedPairs={arbitration.ScannedPairCount}.");
        }

        return MakeBinding(pair, arbitration.Resolution!, stamps);
    }

    /// <summary>
    /// Built per resolution so its readers close over that resolution's stamp
    /// set: the expensive state (parsed snapshots and projected-script indexes)
    /// lives in the shared caches, not in this object.
    /// </summary>
    private static ActiveProjectPairArbiter BuildArbiter(DataRootStampSet stamps)
    {
        Func<string, Result<ProjectSnapshot>> projectReader;
        Func<string, Result<PlaybackArchiveSnapshot>> playbackReader;
        Func<string, string>? projection;
        Func<string>? cacheKey;
        Func<EditorIdentityProjectionSnapshot>? captureProjection;
        lock (Gate)
        {
            projectReader = _projectReader!;
            playbackReader = _playbackReader!;
            projection = _compiledScriptProjection;
            cacheKey = _projectionCacheKey;
            captureProjection = _projectionSnapshot;
        }

        // Capture once for this entire arbitration, including the cheap prefilter and every
        // candidate project. Never label a cache built with a different registration revision.
        if (captureProjection != null)
        {
            EditorIdentityProjectionSnapshot snapshot = captureProjection();
            projection = snapshot.Project;
            cacheKey = () => snapshot.CacheKey;
        }

        return new ActiveProjectPairArbiter(
            path => Cache.GetOrReadProject(path, stamps, projectReader),
            path => Cache.GetOrReadPlayback(path, stamps, playbackReader),
            new ObservedSceneIdentityResolver(
                playbackMapper: null,
                compiledScriptProjection: projection,
                projectionCacheKey: cacheKey));
    }

    private static Result<ActivePairBinding> ResolveWithinKeptPair(
        ActiveProjectPairArbiter arbiter,
        DiscoveredProjectPair kept,
        ObservedCompiledSceneIdentity identity,
        DataRootStampSet stamps)
    {
        Result<ObservedSceneIdentityResolution> resolution =
            arbiter.TryResolvePair(kept, identity);
        if (!resolution.Success || resolution.Value == null)
        {
            return Result<ActivePairBinding>.Fail(
                $"reason=auto-active-pair-unresolved; kept={kept.Name}; {resolution.Error}");
        }

        return MakeBinding(kept, resolution.Value, stamps);
    }

    private static Result<ActivePairBinding> MakeBinding(
        DiscoveredProjectPair pair,
        ObservedSceneIdentityResolution resolution,
        DataRootStampSet stamps)
    {
        Result<ProjectSnapshot> project = Cache.GetOrReadProject(
            pair.AapPath,
            stamps,
            _projectReader!);
        Result<PlaybackArchiveSnapshot> playback = Cache.GetOrReadPlayback(
            pair.AasPath,
            stamps,
            _playbackReader!);
        if (!project.Success || project.Value == null
            || !playback.Success || playback.Value == null)
        {
            return Result<ActivePairBinding>.Fail(
                "reason=auto-snapshot-unavailable; "
                + $"AAP={project.Error}; AAS={playback.Error}");
        }

        return Result<ActivePairBinding>.Ok(new ActivePairBinding(
            pair,
            project.Value,
            playback.Value,
            resolution));
    }

    public static bool ConsumePairChanged()
    {
        lock (Gate)
        {
            if (!_pairChangedSinceLastConsume)
            {
                return false;
            }

            _pairChangedSinceLastConsume = false;
            return _activePair != null;
        }
    }

    private static bool TryResolveDataRoot(
        string dataRootOverride,
        out string dataRoot)
    {
        dataRoot = string.Empty;
        try
        {
            if (!string.IsNullOrWhiteSpace(dataRootOverride))
            {
                dataRoot = Path.GetFullPath(dataRootOverride.Trim());
                return Directory.Exists(dataRoot);
            }

            string persistent;
            lock (Gate)
            {
                persistent = _persistentDataPath;
            }

            if (string.IsNullOrWhiteSpace(persistent))
            {
                return false;
            }

            dataRoot = Path.Combine(persistent, "data");
            return Directory.Exists(dataRoot);
        }
        catch
        {
            dataRoot = string.Empty;
            return false;
        }
    }

    private static void LogOnce(string key, string message)
    {
        lock (LoggedOnce)
        {
            if (!LoggedOnce.Add(key))
            {
                return;
            }
        }

        Plugin.Logger.LogWarning(message);
    }
}
