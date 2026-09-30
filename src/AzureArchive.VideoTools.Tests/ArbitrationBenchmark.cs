using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Mapping;
using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Core.Results;
using AzureArchive.VideoTools.Formats.Aap;
using AzureArchive.VideoTools.Formats.Aas;

namespace AzureArchive.VideoTools.Tests;

/// <summary>
/// Offline, read-only benchmark of the production arbitration path against a
/// real game data root. It never writes to the corpus and never starts the
/// game; it exists to localize where an editor selection actually spends its
/// time and to compare each optimization against the previous behaviour on the
/// same warm snapshots. Diagnostic tool, not a test.
/// </summary>
internal static class ArbitrationBenchmark
{
    private const string ProjectionKey = "editor-projection:embedded+legacy-alias";

    /// <summary>
    /// Graph size of every project, which is what a main-thread walk has to
    /// touch per node and per scene. Read-only; reuses the same reader.
    /// </summary>
    public static int ReportGraphSizes(string dataRoot)
    {
        var scanner = new ProjectPairScanner();
        Result<IReadOnlyList<DiscoveredProjectPair>> scanned = scanner.Scan(dataRoot);
        if (!scanned.Success || scanned.Value == null)
        {
            Console.Error.WriteLine($"Scan failed: {scanned.Error}");
            return 1;
        }

        var rows = new List<(string Name, int Nodes, int ScriptNodes, int Scenes, long Bytes)>();
        var reader = new AapProjectReader();
        foreach (DiscoveredProjectPair pair in scanned.Value)
        {
            Result<ProjectSnapshot> read = reader.Read(pair.AapPath);
            if (!read.Success || read.Value == null)
            {
                Console.WriteLine($"{pair.Name,-40} unreadable: {read.Error}");
                continue;
            }

            int nodes = read.Value.Nodes.Count;
            int scriptNodes = read.Value.ScriptNodes.Count();
            int scenes = read.Value.Nodes.Sum(node => node.Scenes.Count);
            rows.Add((pair.Name, nodes, scriptNodes, scenes, new FileInfo(pair.AapPath).Length));
        }

        Console.WriteLine($"projects       : {rows.Count}");
        Console.WriteLine(
            $"total          : nodes={rows.Sum(r => r.Nodes):N0}; "
            + $"scriptNodes={rows.Sum(r => r.ScriptNodes):N0}; "
            + $"scenes={rows.Sum(r => r.Scenes):N0}");
        Console.WriteLine();
        Console.WriteLine("top 10 by scene count:");
        Console.WriteLine($"{"project",-42}{"nodes",8}{"script",8}{"scenes",8}{"KB",8}");
        foreach (var row in rows.OrderByDescending(r => r.Scenes).Take(10))
        {
            Console.WriteLine(
                $"{row.Name,-42}{row.Nodes,8}{row.ScriptNodes,8}{row.Scenes,8}{row.Bytes / 1024,8}");
        }

        return 0;
    }

    public static int Run(string dataRoot, string? pairName, int repetitions)    {
        if (!Directory.Exists(dataRoot))
        {
            Console.Error.WriteLine($"Data root was not found: {dataRoot}");
            return 1;
        }

        if (repetitions < 1)
        {
            repetitions = 1;
        }

        var scanner = new ProjectPairScanner();
        Result<IReadOnlyList<DiscoveredProjectPair>> scanned = scanner.Scan(dataRoot);
        if (!scanned.Success || scanned.Value == null)
        {
            Console.Error.WriteLine($"Scan failed: {scanned.Error}");
            return 1;
        }

        IReadOnlyList<DiscoveredProjectPair> pairs = scanned.Value;
        Console.WriteLine($"data-root          : {dataRoot}");
        Console.WriteLine($"discovered-pairs   : {pairs.Count}");
        Console.WriteLine($"usable-pairs       : {pairs.Count(pair => pair.PlaybackPresent)}");
        Console.WriteLine(
            $"scanner-scan       : {TimeBest(repetitions, () => scanner.Scan(dataRoot)):F2} ms "
            + $"(best of {repetitions})");
        Console.WriteLine(
            $"scanner-scan-stamp : {TimeBest(repetitions, () => scanner.Scan(dataRoot, DataRootStampSet.Capture(dataRoot))):F2} ms "
            + $"(best of {repetitions})");
        Console.WriteLine(
            $"stamp-capture      : {TimeBest(repetitions, () => DataRootStampSet.Capture(dataRoot)):F2} ms "
            + "(projects/ + saves/ in one pass each)");

        DiscoveredProjectPair target = SelectTarget(pairs, pairName);
        Console.WriteLine($"target-pair        : {target.Name}");
        Console.WriteLine($"target-aap-bytes   : {new FileInfo(target.AapPath).Length:N0}");
        Console.WriteLine($"target-aas-bytes   : {new FileInfo(target.AasPath).Length:N0}");

        Result<PlaybackArchiveSnapshot> targetPlayback =
            new AasScenarioReader().Read(target.AasPath);
        Result<ProjectSnapshot> targetProject = new AapProjectReader().Read(target.AapPath);
        if (!targetPlayback.Success
            || targetPlayback.Value == null
            || !targetProject.Success
            || targetProject.Value == null)
        {
            Console.Error.WriteLine(
                $"Target pair read failed: AAP={targetProject.Error}; AAS={targetPlayback.Error}");
            return 1;
        }

        PlaybackArchiveSnapshot playback = targetPlayback.Value;
        ProjectSnapshot project = targetProject.Value;
        Console.WriteLine($"target-records     : {playback.Records.Count}");

        var extractor = new EmbeddedAavtDirectiveExtractor();
        string Projection(string script) => extractor.Extract(script, true).SanitizedText;

        var scanResolver = new ObservedSceneIdentityResolver(
            compiledScriptProjection: Projection);

        // Pick an identity the target pair really maps, so the benchmark
        // measures a successful arbitration instead of a blanket rejection.
        ObservedCompiledSceneIdentity? identity = null;
        int identityRecord = -1;
        long pickStart = Stopwatch.GetTimestamp();
        for (int index = 0; index < playback.Records.Count; index++)
        {
            ObservedCompiledSceneIdentity candidate =
                IdentityOf(Projection(playback.Records[index].CompiledScript));
            Result<ObservedSceneIdentityResolution> probe =
                scanResolver.Resolve(candidate, project, playback);
            if (probe.Success
                && probe.Value != null
                && probe.Value.Status == ObservedSceneIdentityStatus.Mapped
                && probe.Value.SelectedSceneTrusted)
            {
                identity = candidate;
                identityRecord = index;
                break;
            }
        }

        Console.WriteLine(
            $"identity-pick      : {(Stopwatch.GetTimestamp() - pickStart) * 1000.0 / Stopwatch.Frequency:F2} ms; "
            + $"record={identityRecord}; records-probed={identityRecord + 1}");
        if (identity == null)
        {
            Console.Error.WriteLine(
                "No trusted mapped record was found in the target pair; "
                + "the benchmark cannot measure a successful arbitration.");
            return 1;
        }

        Console.WriteLine($"identity-len       : {identity.CompiledScriptLength}");
        Console.WriteLine($"identity-lines     : {identity.CompiledScriptLineCount}");
        Console.WriteLine($"identity-sha16     : {identity.CompiledScriptSha256[..16]}");

        // ---- cold: fresh cache, so every read is a real read ----
        var counters = new ReadCounters();
        var sharedCache = new ArchiveSnapshotCache();
        ActiveProjectPairArbiter coldArbiter = BuildArbiter(
            sharedCache,
            counters,
            Projection,
            projectionKey: null,
            stamps: null);
        long coldStart = Stopwatch.GetTimestamp();
        ActivePairArbitration coldResult = coldArbiter.Arbitrate(identity, pairs);
        double coldMs = (Stopwatch.GetTimestamp() - coldStart) * 1000.0 / Stopwatch.Frequency;
        Console.WriteLine(
            $"cold-record-scan   : {coldMs:F2} ms; status={coldResult.Status}; "
            + $"scanned={coldResult.ScannedPairCount}; diagnostics={coldResult.Diagnostics.Count}");
        Console.WriteLine(
            $"cold-io            : aap-reads={counters.ProjectReads} ({counters.ProjectBytes:N0} B); "
            + $"aas-reads={counters.PlaybackReads} ({counters.PlaybackBytes:N0} B)");
        foreach (string line in coldResult.Diagnostics.Take(2))
        {
            Console.WriteLine($"cold-diagnostic    : {line}");
        }

        DataRootStampSet stamps = DataRootStampSet.Capture(dataRoot);
        var warmCounters = new ReadCounters();

        // ---- phase 0: the very first selection after a restart, indexed ----
        var coldIndexCache = new ArchiveSnapshotCache();
        var coldIndexCounters = new ReadCounters();
        double phase0 = TimeFirst(
            () => BuildArbiter(
                coldIndexCache,
                coldIndexCounters,
                Projection,
                ProjectionKey,
                stamps).Arbitrate(identity, pairs));
        Console.WriteLine(
            $"0 first indexed    : {phase0:F2} ms  (cold reads + every index build); "
            + $"aap-reads={coldIndexCounters.ProjectReads} "
            + $"({coldIndexCounters.ProjectBytes:N0} B); "
            + $"aas-reads={coldIndexCounters.PlaybackReads} "
            + $"({coldIndexCounters.PlaybackBytes:N0} B)");

        // ---- phase 1: warm snapshots, per-file validation, record scan ----
        double phase1 = TimeBest(
            repetitions,
            () => BuildArbiter(sharedCache, warmCounters, Projection, null, null)
                .Arbitrate(identity, pairs));
        Console.WriteLine($"1 warm+stat+scan   : {phase1:F2} ms  (previous behaviour)");

        // ---- phase 2: warm snapshots, directory stamps, record scan ----
        double phase2 = TimeBest(
            repetitions,
            () => BuildArbiter(sharedCache, warmCounters, Projection, null, stamps)
                .Arbitrate(identity, pairs));
        Console.WriteLine($"2 warm+stamp+scan  : {phase2:F2} ms  (stamps only)");

        // ---- phase 3: warm snapshots, per-file validation, script index ----
        double phase3First = TimeFirst(
            () => BuildArbiter(sharedCache, warmCounters, Projection, ProjectionKey, null)
                .Arbitrate(identity, pairs));
        double phase3 = TimeBest(
            repetitions,
            () => BuildArbiter(sharedCache, warmCounters, Projection, ProjectionKey, null)
                .Arbitrate(identity, pairs));
        Console.WriteLine(
            $"3 warm+stat+index  : {phase3:F2} ms  (index only; first={phase3First:F2})");

        // ---- phase 4: the full production resolution path ----
        double phase4First = TimeFirst(() => FullResolution(
            sharedCache,
            warmCounters,
            scanner,
            dataRoot,
            identity,
            Projection));
        double phase4 = TimeBest(
            repetitions,
            () => FullResolution(
                sharedCache,
                warmCounters,
                scanner,
                dataRoot,
                identity,
                Projection));
        Console.WriteLine(
            $"4 full resolution  : {phase4:F2} ms  (stamps + scan + index; first={phase4First:F2})");

        ActivePairArbitration finalResult = BuildArbiter(
            sharedCache,
            warmCounters,
            Projection,
            ProjectionKey,
            stamps).Arbitrate(identity, pairs);
        Console.WriteLine(
            $"final              : status={finalResult.Status}; "
            + $"record={finalResult.Resolution?.PlaybackRecordIndex}; "
            + $"pair={finalResult.Pair?.Name}");
        Console.WriteLine(
            $"equivalent-to-cold : {IsEquivalent(coldResult, finalResult)}; "
            + $"io-after-warmup=stat:{warmCounters.ProjectReads + warmCounters.PlaybackReads}");
        if (phase4 > 0)
        {
            Console.WriteLine($"speedup-steady     : {phase1 / phase4:F1}x  (phase1 -> phase4)");
        }

        double fast = TimeBest(
            repetitions * 5,
            () => BuildArbiter(sharedCache, warmCounters, Projection, ProjectionKey, stamps)
                .TryResolvePair(target, identity));
        Console.WriteLine($"fast-path-bound    : {fast:F3} ms  (bound pair only, indexed)");

        // ---- where the cold sweep's time goes ----
        DiscoveredProjectPair[] probes =
            pairs.Where(candidate => candidate.PlaybackPresent).ToArray();
        string projectsDirectory = Path.Combine(dataRoot, "projects");
        double aapOnly = TimeBest(
            repetitions,
            () => ReadAllProjects(probes));
        double aasOnly = TimeBest(
            repetitions,
            () => ReadAllPlaybacks(probes));
        Console.WriteLine(
            $"cold-split-aap     : {aapOnly:F2} ms  ({probes.Length} AAP parses, "
            + $"{probes.Sum(pair => new FileInfo(pair.AapPath).Length):N0} B)");
        Console.WriteLine(
            $"cold-split-aas     : {aasOnly:F2} ms  ({probes.Length} AAS parses, "
            + $"{probes.Sum(pair => new FileInfo(pair.AasPath).Length):N0} B)");
        Console.WriteLine(
            $"cold-split-index   : {TimeBest(repetitions, () => BuildAllIndexes(sharedCache, probes, stamps, Projection)):F2} ms "
            + "(projection+SHA256 for every record)");

        // ---- what a parallel cold warm-up would cost (measurement only) ----
        int dop = Math.Min(8, Math.Max(1, Environment.ProcessorCount - 1));
        Console.WriteLine($"cores              : {Environment.ProcessorCount}; warm-up dop={dop}");
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            var parallelCache = new ArchiveSnapshotCache();
            double parallelMs = TimeOnce(
                () => ParallelWarmUp(parallelCache, probes, stamps, Projection, dop));
            var parallelCounters = new ReadCounters();
            double afterMs = TimeOnce(
                () => BuildArbiter(
                    parallelCache,
                    parallelCounters,
                    Projection,
                    ProjectionKey,
                    stamps).Arbitrate(identity, pairs));
            Console.WriteLine(
                $"parallel-warmup {attempt}  : {parallelMs:F2} ms (parse+index, dop={dop}); "
                + $"then sequential arbitrate={afterMs:F2} ms");
        }

        // ---- filesystem metadata cost breakdown ----
        Console.WriteLine($"io-probe-files     : {probes.Length} pairs");
        Console.WriteLine(
            $"io-file-exists     : {TimeBest(repetitions * 2, () => CountExisting(probes)):F2} ms "
            + $"for {probes.Length} stats");
        Console.WriteLine(
            $"io-fileinfo-stat   : {TimeBest(repetitions * 2, () => SumFileInfo(probes)):F2} ms "
            + $"for {probes.Length} length+writetime reads");
        Console.WriteLine(
            $"io-getfullpath     : {TimeBest(repetitions * 2, () => SumFullPaths(probes)):F2} ms "
            + $"for {probes.Length * 2} normalizations");
        Console.WriteLine(
            $"io-dir-enumerate   : {TimeBest(repetitions * 2, () => SumDirectory(projectsDirectory)):F2} ms "
            + "for one enumeration of projects/ with length+writetime");
        Console.WriteLine(
            $"cache-stat-validate: {TimeBest(repetitions, () => WarmValidate(sharedCache, probes, null)):F2} ms "
            + $"for {probes.Length * 2} per-file validated lookups");
        Console.WriteLine(
            $"cache-stamp-valid  : {TimeBest(repetitions, () => WarmValidate(sharedCache, probes, stamps)):F2} ms "
            + $"for {probes.Length * 2} stamp-validated lookups");

        // ---- how many records one sweep really walks ----
        long totalRecords = 0;
        int pairsWithPlayback = 0;
        foreach (DiscoveredProjectPair pair in probes)
        {
            Result<PlaybackArchiveSnapshot> read = sharedCache.GetOrReadPlayback(
                pair.AasPath,
                stamps,
                path => new AasScenarioReader().Read(path));
            if (read.Success && read.Value != null)
            {
                totalRecords += read.Value.Records.Count;
                pairsWithPlayback++;
            }
        }

        Console.WriteLine(
            $"records-per-sweep  : {totalRecords:N0} across {pairsWithPlayback} pairs");
        if (totalRecords > 0)
        {
            Console.WriteLine(
                $"per-record-cost    : {Math.Max(phase2 - phase4, 0) * 1000.0 / totalRecords:F2} us/record "
                + "(scan minus index)");
        }

        // ---- isolate the per-record pipeline on the target pair ----
        long projectionStart = Stopwatch.GetTimestamp();
        long projectedChars = 0;
        foreach (PlaybackRecordSnapshot record in playback.Records)
        {
            projectedChars += Projection(record.CompiledScript).Length;
        }

        Console.WriteLine(
            $"projection-only    : "
            + $"{(Stopwatch.GetTimestamp() - projectionStart) * 1000.0 / Stopwatch.Frequency:F2} ms "
            + $"for {playback.Records.Count} records ({projectedChars:N0} chars)");

        long hashStart = Stopwatch.GetTimestamp();
        foreach (PlaybackRecordSnapshot record in playback.Records)
        {
            _ = IdentityOf(Projection(record.CompiledScript));
        }

        Console.WriteLine(
            $"projection+hash    : "
            + $"{(Stopwatch.GetTimestamp() - hashStart) * 1000.0 / Stopwatch.Frequency:F2} ms "
            + $"for {playback.Records.Count} records");

        var mapper = new ConservativeProjectPlaybackMapper();
        Console.WriteLine(
            $"mapper-map         : {TimeBest(repetitions * 5, () => mapper.Map(project, playback)):F3} ms "
            + $"(best of {repetitions * 5})");

        return IsEquivalent(coldResult, finalResult) ? 0 : 1;
    }

    /// <summary>
    /// Everything ResolveActive does before it can answer, minus the sticky
    /// fast path: capture stamps, scan pairs, arbitrate.
    /// </summary>
    private static ActivePairArbitration FullResolution(
        ArchiveSnapshotCache cache,
        ReadCounters counters,
        ProjectPairScanner scanner,
        string dataRoot,
        ObservedCompiledSceneIdentity identity,
        Func<string, string> projection)
    {
        DataRootStampSet stamps = DataRootStampSet.Capture(dataRoot);
        Result<IReadOnlyList<DiscoveredProjectPair>> scanned = scanner.Scan(dataRoot, stamps);
        return BuildArbiter(cache, counters, projection, ProjectionKey, stamps)
            .Arbitrate(identity, scanned.Value!);
    }

    /// <summary>
    /// Whether two arbitrations agree on everything the caller can observe.
    /// </summary>
    private static bool IsEquivalent(
        ActivePairArbitration left,
        ActivePairArbitration right) =>
        left.Status == right.Status
        && left.ScannedPairCount == right.ScannedPairCount
        && left.Resolution?.PlaybackRecordIndex == right.Resolution?.PlaybackRecordIndex
        && left.Resolution?.Status == right.Resolution?.Status
        && left.Diagnostics.Count == right.Diagnostics.Count
        && string.Equals(
            left.Pair?.AapPath,
            right.Pair?.AapPath,
            StringComparison.OrdinalIgnoreCase);

    private static int ReadAllProjects(DiscoveredProjectPair[] pairs)
    {
        int ok = 0;
        foreach (DiscoveredProjectPair pair in pairs)
        {
            if (new AapProjectReader().Read(pair.AapPath).Success)
            {
                ok++;
            }
        }

        return ok;
    }

    private static int ReadAllPlaybacks(DiscoveredProjectPair[] pairs)
    {
        int ok = 0;
        foreach (DiscoveredProjectPair pair in pairs)
        {
            if (new AasScenarioReader().Read(pair.AasPath).Success)
            {
                ok++;
            }
        }

        return ok;
    }

    private static int BuildAllIndexes(
        ArchiveSnapshotCache cache,
        DiscoveredProjectPair[] pairs,
        DataRootStampSet stamps,
        Func<string, string> projection)
    {
        int built = 0;
        foreach (DiscoveredProjectPair pair in pairs)
        {
            Result<PlaybackArchiveSnapshot> playback = cache.GetOrReadPlayback(
                pair.AasPath,
                stamps,
                path => new AasScenarioReader().Read(path));
            if (playback.Success
                && playback.Value != null
                && PlaybackScriptIndex.Build(playback.Value, projection).Success)
            {
                built++;
            }
        }

        return built;
    }

    /// <summary>
    /// Measurement only, not production code: the same reads, parses and index
    /// builds the cold sweep performs, spread over a few threads, into its own
    /// cache so the shared one is untouched.
    /// </summary>
    private static int ParallelWarmUp(
        ArchiveSnapshotCache cache,
        DiscoveredProjectPair[] pairs,
        DataRootStampSet stamps,
        Func<string, string> projection,
        int dop)
    {
        int warm = 0;
        Parallel.ForEach(
            pairs,
            new ParallelOptions { MaxDegreeOfParallelism = dop },
            pair =>
            {
                Result<ProjectSnapshot> project = cache.GetOrReadProject(
                    pair.AapPath,
                    stamps,
                    path => new AapProjectReader().Read(path));
                Result<PlaybackArchiveSnapshot> playback = cache.GetOrReadPlayback(
                    pair.AasPath,
                    stamps,
                    path => new AasScenarioReader().Read(path));
                if (project.Success
                    && playback.Success
                    && playback.Value != null
                    && PlaybackScriptIndex.Build(playback.Value, projection).Success)
                {
                    Interlocked.Increment(ref warm);
                }
            });
        return warm;
    }

    private static int CountExisting(DiscoveredProjectPair[] pairs)
    {
        int count = 0;
        foreach (DiscoveredProjectPair pair in pairs)
        {
            if (File.Exists(pair.AasPath))
            {
                count++;
            }
        }

        return count;
    }

    private static long SumFileInfo(DiscoveredProjectPair[] pairs)
    {
        long total = 0;
        foreach (DiscoveredProjectPair pair in pairs)
        {
            FileInfo info = new(pair.AapPath);
            total += info.Length + info.LastWriteTimeUtc.Ticks;
        }

        return total;
    }

    private static long SumFullPaths(DiscoveredProjectPair[] pairs)
    {
        long total = 0;
        foreach (DiscoveredProjectPair pair in pairs)
        {
            total += Path.GetFullPath(pair.AapPath).Length;
            total += Path.GetFullPath(pair.AasPath).Length;
        }

        return total;
    }

    private static long SumDirectory(string projectsDirectory)
    {
        long total = 0;
        var directory = new DirectoryInfo(projectsDirectory);
        foreach (FileInfo file in directory.EnumerateFiles("*.aap"))
        {
            total += file.Length + file.LastWriteTimeUtc.Ticks;
        }

        return total;
    }

    private static int WarmValidate(
        ArchiveSnapshotCache cache,
        DiscoveredProjectPair[] pairs,
        DataRootStampSet? stamps)
    {
        int hits = 0;
        foreach (DiscoveredProjectPair pair in pairs)
        {
            if (cache.GetOrReadProject(
                pair.AapPath,
                stamps,
                path => new AapProjectReader().Read(path)).Success)
            {
                hits++;
            }

            if (cache.GetOrReadPlayback(
                pair.AasPath,
                stamps,
                path => new AasScenarioReader().Read(path)).Success)
            {
                hits++;
            }
        }

        return hits;
    }

    private static ActiveProjectPairArbiter BuildArbiter(
        ArchiveSnapshotCache cache,
        ReadCounters counters,
        Func<string, string> projection,
        string? projectionKey,
        DataRootStampSet? stamps) =>
        new(
            path => cache.GetOrReadProject(
                path,
                stamps,
                p =>
                {
                    counters.RecordProject(p);
                    return new AapProjectReader().Read(p);
                }),
            path => cache.GetOrReadPlayback(
                path,
                stamps,
                p =>
                {
                    counters.RecordPlayback(p);
                    return new AasScenarioReader().Read(p);
                }),
            projectionKey == null
                ? new ObservedSceneIdentityResolver(compiledScriptProjection: projection)
                : new ObservedSceneIdentityResolver(
                    playbackMapper: null,
                    compiledScriptProjection: projection,
                    projectionCacheKey: () => projectionKey));

    private sealed class ReadCounters
    {
        private int _projectReads;
        private long _projectBytes;
        private int _playbackReads;
        private long _playbackBytes;

        public int ProjectReads => _projectReads;

        public long ProjectBytes => _projectBytes;

        public int PlaybackReads => _playbackReads;

        public long PlaybackBytes => _playbackBytes;

        public void RecordProject(string path)
        {
            Interlocked.Increment(ref _projectReads);
            Interlocked.Add(ref _projectBytes, LengthOf(path));
        }

        public void RecordPlayback(string path)
        {
            Interlocked.Increment(ref _playbackReads);
            Interlocked.Add(ref _playbackBytes, LengthOf(path));
        }

        private static long LengthOf(string path)
        {
            try
            {
                return new FileInfo(path).Length;
            }
            catch (IOException)
            {
                return 0;
            }
        }
    }

    private static ObservedCompiledSceneIdentity IdentityOf(string sanitized)
    {
        string sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sanitized)));
        return new ObservedCompiledSceneIdentity(
            sha256,
            sanitized.Length,
            sanitized.Count(character => character == '\n') + 1);
    }

    private static DiscoveredProjectPair SelectTarget(
        IReadOnlyList<DiscoveredProjectPair> pairs,
        string? pairName)
    {
        if (!string.IsNullOrWhiteSpace(pairName))
        {
            DiscoveredProjectPair? named = pairs.FirstOrDefault(pair =>
                string.Equals(pair.Name, pairName, StringComparison.OrdinalIgnoreCase));
            if (named != null)
            {
                return named;
            }

            Console.WriteLine($"requested pair '{pairName}' was not found; using the largest AAS");
        }

        DiscoveredProjectPair? best = null;
        long bestLength = -1;
        foreach (DiscoveredProjectPair pair in pairs.Where(candidate => candidate.PlaybackPresent))
        {
            long length = new FileInfo(pair.AasPath).Length;
            if (length > bestLength)
            {
                bestLength = length;
                best = pair;
            }
        }

        return best ?? pairs[0];
    }

    private static double TimeOnce<T>(Func<T> action)
    {
        long start = Stopwatch.GetTimestamp();
        _ = action();
        return (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
    }

    private static double TimeFirst<T>(Func<T> action) => TimeOnce(action);
    private static double TimeBest<T>(int repetitions, Func<T> action)
    {
        double best = double.MaxValue;
        for (int index = 0; index < repetitions; index++)
        {
            long start = Stopwatch.GetTimestamp();
            _ = action();
            double elapsed = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            if (elapsed < best)
            {
                best = elapsed;
            }
        }

        return best;
    }
}
