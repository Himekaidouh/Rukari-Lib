using AzureArchive.VideoTools.Core.Compilation;
using AzureArchive.VideoTools.Core.Continuity;
using AzureArchive.VideoTools.Core.Mapping;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Formats.Aap;
using AzureArchive.VideoTools.Formats.Aas;
using AzureArchive.VideoTools.Formats.Sidecar;

namespace AzureArchive.VideoTools.Tests;

internal static class MappingSmokeVerifier
{
    public static int VerifyDirectories(string projectDirectory, string saveDirectory)
    {
        string projects = Path.GetFullPath(projectDirectory);
        string saves = Path.GetFullPath(saveDirectory);
        if (!Directory.Exists(projects) || !Directory.Exists(saves))
        {
            Console.Error.WriteLine("MAPPING SMOKE FAIL project or save directory does not exist.");
            return 1;
        }

        var projectReader = new AapProjectReader();
        var playbackReader = new AasScenarioReader();
        var mapper = new ConservativeProjectPlaybackMapper();
        var projectionCompiler = new ContinuationProjectionCompiler();
        using var projectionFiles = new TemporaryAapDirectory();
        var projectionStore = new JsonContinuationProjectionStore(
            Path.Combine(projectionFiles.Root, "real-projection-smoke"));
        int pairs = 0;
        int failures = 0;
        int sourceScenes = 0;
        int playbackRecords = 0;
        int unique = 0;
        int ambiguous = 0;
        int unmapped = 0;
        int unclaimed = 0;
        int olderPairs = 0;
        int globallyUniqueText = 0;
        int orderedNonEmptyText = 0;
        int freshPairs = 0;
        int freshSourceScenes = 0;
        int freshOrderedNonEmptyText = 0;
        int freshUnique = 0;
        int freshAmbiguous = 0;
        int freshUnmapped = 0;
        int freshUnclaimed = 0;
        int eligibleTransitions = 0;
        int freshEligibleTransitions = 0;
        int invalidIdentityTransitions = 0;
        int projectionPairs = 0;
        int projectionInstructions = 0;
        int projectionFailures = 0;
        int projectionStoreRoundTrips = 0;
        int projectionStoreFailures = 0;

        foreach (string projectPath in Directory.GetFiles(
                     projects,
                     "*.aap",
                     SearchOption.TopDirectoryOnly))
        {
            string playbackPath = Path.Combine(
                saves,
                Path.GetFileNameWithoutExtension(projectPath) + ".aas");
            if (!File.Exists(playbackPath))
            {
                continue;
            }

            pairs++;
            var projectResult = projectReader.Read(projectPath);
            var playbackResult = playbackReader.Read(playbackPath);
            if (!projectResult.Success
                || projectResult.Value == null
                || !playbackResult.Success
                || playbackResult.Value == null)
            {
                failures++;
                Console.Error.WriteLine(
                    $"MAPPING SMOKE FAIL {Path.GetFileNameWithoutExtension(projectPath)}: "
                    + $"AAP={projectResult.Error} AAS={playbackResult.Error}");
                continue;
            }

            var mappingResult = mapper.Map(projectResult.Value, playbackResult.Value);
            if (!mappingResult.Success || mappingResult.Value == null)
            {
                failures++;
                Console.Error.WriteLine(
                    $"MAPPING SMOKE FAIL {Path.GetFileNameWithoutExtension(projectPath)}: "
                    + mappingResult.Error);
                continue;
            }

            ProjectPlaybackMappingReport report = mappingResult.Value;
            ContinuationRule[] eligibleRules = FindEligibleRules(
                projectResult.Value,
                report,
                out int invalidIdentities);
            eligibleTransitions += eligibleRules.Length;
            invalidIdentityTransitions += invalidIdentities;
            sourceScenes += report.Scenes.Count;
            playbackRecords += playbackResult.Value.Records.Count;
            int reportAmbiguous = report.Scenes.Count(
                scene => scene.Status == ScenePlaybackMappingStatus.Ambiguous);
            int reportUnmapped = report.Scenes.Count(
                scene => scene.Status == ScenePlaybackMappingStatus.Unmapped);
            string[] sourceTexts = projectResult.Value.ScriptNodes
                .OrderBy(node => node.SourceIndex)
                .SelectMany(node => node.Scenes.OrderBy(scene => scene.Key.SceneIndex))
                .Select(scene => scene.DialogueText)
                .ToArray();
            string[] playbackTexts = playbackResult.Value.Records
                .Select(record => record.TextJp)
                .ToArray();
            globallyUniqueText += CountGloballyUniqueNonEmptyText(
                sourceTexts,
                playbackTexts);
            int orderedMatches = LongestCommonSubsequenceLength(
                sourceTexts.Where(text => text.Length > 0).ToArray(),
                playbackTexts.Where(text => text.Length > 0).ToArray());
            orderedNonEmptyText += orderedMatches;

            bool fresh = Math.Abs(
                    (playbackResult.Value.Source.LastWriteTimeUtc
                     - projectResult.Value.Source.LastWriteTimeUtc).TotalSeconds) <= 5;
            if (fresh)
            {
                freshPairs++;
                freshSourceScenes += sourceTexts.Length;
                freshOrderedNonEmptyText += orderedMatches;
                freshUnique += report.UniqueMappingCount;
                freshAmbiguous += reportAmbiguous;
                freshUnmapped += reportUnmapped;
                freshUnclaimed += report.UnclaimedPlaybackRecordIndices.Count;
                freshEligibleTransitions += eligibleRules.Length;
            }
            unique += report.UniqueMappingCount;
            ambiguous += reportAmbiguous;
            unmapped += reportUnmapped;
            unclaimed += report.UnclaimedPlaybackRecordIndices.Count;
            bool playbackOlder = report.Issues.Any(
                issue => issue.Code == "PlaybackOlderThanProject");
            if (playbackOlder)
            {
                olderPairs++;
            }

            if (!playbackOlder)
            {
                projectionPairs++;
                var document = new ContinuationDocument(
                    ContinuationDocument.CurrentSchemaVersion,
                    "00000000-0000-0000-0000-000000000001",
                    projectResult.Value.Source.PathKey,
                    projectResult.Value.Source.RevisionSha256,
                    Array.AsReadOnly(eligibleRules));
                var projectionResult = projectionCompiler.Compile(
                    projectResult.Value,
                    playbackResult.Value,
                    document);
                if (!projectionResult.Success
                    || projectionResult.Value == null
                    || projectionResult.Value.Instructions.Count != eligibleRules.Length)
                {
                    projectionFailures++;
                    Console.Error.WriteLine(
                        $"PROJECTION SMOKE FAIL {Path.GetFileNameWithoutExtension(projectPath)}: "
                        + (projectionResult.Success
                            ? $"expected {eligibleRules.Length} instructions but found "
                              + $"{projectionResult.Value?.Instructions.Count ?? -1}"
                            : projectionResult.Error));
                }
                else
                {
                    projectionInstructions += projectionResult.Value.Instructions.Count;
                    string projectionPath =
                        $"pairs/{projectionPairs:D4}.aavt.playback.json";
                    var saveResult = projectionStore.SaveAtomic(
                        projectionPath,
                        projectionResult.Value,
                        projectResult.Value,
                        playbackResult.Value);
                    var loadResult = saveResult.Success
                        ? projectionStore.TryLoad(
                            projectionPath,
                            projectResult.Value,
                            playbackResult.Value)
                        : null;
                    if (!saveResult.Success
                        || loadResult == null
                        || !loadResult.Success
                        || loadResult.Value?.Projection == null
                        || !ProjectionsEqual(
                            projectionResult.Value,
                            loadResult.Value.Projection))
                    {
                        projectionStoreFailures++;
                        Console.Error.WriteLine(
                            $"PROJECTION STORE SMOKE FAIL {Path.GetFileNameWithoutExtension(projectPath)}: "
                            + (!saveResult.Success
                                ? saveResult.Error
                                : loadResult?.Error
                                  ?? "loaded projection differs from the compiled projection"));
                    }
                    else
                    {
                        projectionStoreRoundTrips++;
                    }
                }
            }
        }

        Console.WriteLine(
            $"MAPPING SMOKE SUMMARY pairs={pairs} failures={failures} "
            + $"sourceScenes={sourceScenes} playbackRecords={playbackRecords} "
            + $"unique={unique} ambiguous={ambiguous} unmapped={unmapped} "
            + $"unclaimedPlayback={unclaimed} olderPairs={olderPairs} "
            + $"globallyUniqueNonEmptyText={globallyUniqueText} "
            + $"orderedNonEmptyText={orderedNonEmptyText} freshPairs={freshPairs} "
            + $"freshSourceScenes={freshSourceScenes} "
            + $"freshOrderedNonEmptyText={freshOrderedNonEmptyText} "
            + $"freshUnique={freshUnique} freshAmbiguous={freshAmbiguous} "
            + $"freshUnmapped={freshUnmapped} freshUnclaimedPlayback={freshUnclaimed}");
        Console.WriteLine(
            $"PROJECTION SMOKE SUMMARY eligibleTransitions={eligibleTransitions} "
            + $"freshEligibleTransitions={freshEligibleTransitions} "
            + $"invalidIdentityTransitions={invalidIdentityTransitions} "
            + $"projectionPairs={projectionPairs} "
            + $"projectionInstructions={projectionInstructions} "
            + $"projectionFailures={projectionFailures} "
            + $"storeRoundTrips={projectionStoreRoundTrips} "
            + $"storeFailures={projectionStoreFailures}");
        return failures == 0
            && projectionFailures == 0
            && projectionStoreFailures == 0
            ? 0
            : 1;
    }

    private static bool ProjectionsEqual(
        ContinuationPlaybackProjection expected,
        ContinuationPlaybackProjection actual)
    {
        if (expected.SchemaVersion != actual.SchemaVersion
            || !string.Equals(expected.ProjectId, actual.ProjectId, StringComparison.Ordinal)
            || !string.Equals(expected.ProjectPathKey, actual.ProjectPathKey, StringComparison.Ordinal)
            || !string.Equals(expected.ProjectRevisionSha256, actual.ProjectRevisionSha256, StringComparison.Ordinal)
            || !string.Equals(expected.PlaybackPathKey, actual.PlaybackPathKey, StringComparison.Ordinal)
            || !string.Equals(expected.PlaybackRevisionSha256, actual.PlaybackRevisionSha256, StringComparison.Ordinal)
            || !string.Equals(expected.PlaybackSchemaName, actual.PlaybackSchemaName, StringComparison.Ordinal)
            || expected.Instructions.Count != actual.Instructions.Count)
        {
            return false;
        }

        for (int index = 0; index < expected.Instructions.Count; index++)
        {
            if (expected.Instructions[index] != actual.Instructions[index])
            {
                return false;
            }
        }

        return true;
    }

    private static ContinuationRule[] FindEligibleRules(
        ProjectSnapshot project,
        ProjectPlaybackMappingReport report,
        out int invalidIdentityTransitions)
    {
        Dictionary<string, ScenePlaybackMapping> mappingByLocation = report.Scenes
            .GroupBy(mapping => Location(mapping.Scene), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(
                group => group.Key,
                group => group.Single(),
                StringComparer.OrdinalIgnoreCase);
        var rules = new List<ContinuationRule>();
        invalidIdentityTransitions = 0;
        foreach (StoryNodeSnapshot node in project.ScriptNodes)
        {
            SceneSnapshot[] scenes = node.Scenes
                .OrderBy(scene => scene.Key.SceneIndex)
                .ToArray();
            for (int index = 1; index < scenes.Length; index++)
            {
                if (!TryGetUniqueRecord(
                        mappingByLocation,
                        scenes[index - 1].Key,
                        out int previousRecord)
                    || !TryGetUniqueRecord(
                        mappingByLocation,
                        scenes[index].Key,
                        out int currentRecord)
                    || currentRecord != previousRecord + 1)
                {
                    continue;
                }

                if (!Guid.TryParseExact(scenes[index].Key.NodeGuid, "D", out _))
                {
                    invalidIdentityTransitions++;
                    continue;
                }

                rules.Add(new ContinuationRule(scenes[index].Key));
            }
        }

        return rules.ToArray();
    }

    private static bool TryGetUniqueRecord(
        IReadOnlyDictionary<string, ScenePlaybackMapping> mappingByLocation,
        SceneKey scene,
        out int recordIndex)
    {
        if (mappingByLocation.TryGetValue(
                Location(scene),
                out ScenePlaybackMapping? mapping)
            && mapping.Status is ScenePlaybackMappingStatus.UniqueDialogueTextMatch
                or ScenePlaybackMappingStatus.AnchoredNodeSequenceMatch
            && mapping.PlaybackRecordIndex.HasValue)
        {
            recordIndex = mapping.PlaybackRecordIndex.Value;
            return true;
        }

        recordIndex = -1;
        return false;
    }

    private static string Location(SceneKey scene) =>
        $"{scene.NodeGuid.Trim().ToLowerInvariant()}:{scene.SceneIndex}";

    private static int CountGloballyUniqueNonEmptyText(
        IReadOnlyCollection<string> source,
        IReadOnlyCollection<string> playback)
    {
        Dictionary<string, int> sourceCounts = source
            .Where(text => text.Length > 0)
            .GroupBy(text => text, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        Dictionary<string, int> playbackCounts = playback
            .Where(text => text.Length > 0)
            .GroupBy(text => text, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        return sourceCounts.Count(pair =>
            pair.Value == 1
            && playbackCounts.TryGetValue(pair.Key, out int count)
            && count == 1);
    }

    private static int LongestCommonSubsequenceLength(
        IReadOnlyList<string> source,
        IReadOnlyList<string> playback)
    {
        var previous = new int[playback.Count + 1];
        var current = new int[playback.Count + 1];
        for (int sourceIndex = 0; sourceIndex < source.Count; sourceIndex++)
        {
            for (int playbackIndex = 0; playbackIndex < playback.Count; playbackIndex++)
            {
                current[playbackIndex + 1] = string.Equals(
                        source[sourceIndex],
                        playback[playbackIndex],
                        StringComparison.Ordinal)
                    ? previous[playbackIndex] + 1
                    : Math.Max(
                        previous[playbackIndex + 1],
                        current[playbackIndex]);
            }

            (previous, current) = (current, previous);
            Array.Clear(current);
        }

        return previous[playback.Count];
    }
}
