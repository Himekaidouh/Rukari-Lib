using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Mapping;

public enum ScenePlaybackMappingStatus
{
    Unmapped = 0,
    UniqueDialogueTextMatch = 1,
    AnchoredNodeSequenceMatch = 2,
    Ambiguous = 3
}

public enum ProjectPlaybackMappingIssueSeverity
{
    Information = 0,
    Warning = 1,
    Error = 2
}

public sealed record ScenePlaybackMapping(
    SceneKey Scene,
    ScenePlaybackMappingStatus Status,
    int? PlaybackRecordIndex,
    IReadOnlyList<int> CandidateRecordIndices);

public sealed record ProjectPlaybackMappingIssue(
    ProjectPlaybackMappingIssueSeverity Severity,
    string Code,
    string Message);

public sealed record ProjectPlaybackMappingReport(
    ProjectSourceSnapshot Project,
    PlaybackArchiveSourceSnapshot Playback,
    IReadOnlyList<ScenePlaybackMapping> Scenes,
    IReadOnlyList<int> UnclaimedPlaybackRecordIndices,
    IReadOnlyList<ProjectPlaybackMappingIssue> Issues)
{
    public int UniqueMappingCount => Scenes.Count(
        scene => scene.Status is ScenePlaybackMappingStatus.UniqueDialogueTextMatch
            or ScenePlaybackMappingStatus.AnchoredNodeSequenceMatch);
}

public interface IProjectPlaybackMapper
{
    Result<ProjectPlaybackMappingReport> Map(
        ProjectSnapshot project,
        PlaybackArchiveSnapshot playback);
}

public sealed class ConservativeProjectPlaybackMapper : IProjectPlaybackMapper
{
    private static readonly TimeSpan StaleTimestampTolerance = TimeSpan.FromSeconds(5);

    public Result<ProjectPlaybackMappingReport> Map(
        ProjectSnapshot project,
        PlaybackArchiveSnapshot playback)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(playback);

        if (project.Nodes == null)
        {
            return Result<ProjectPlaybackMappingReport>.Fail(
                "Project snapshot has no node collection.");
        }

        if (playback.Records == null)
        {
            return Result<ProjectPlaybackMappingReport>.Fail(
                "Playback snapshot has no record collection.");
        }

        SourceScene[] scenes = project.ScriptNodes
            .OrderBy(node => node.SourceIndex)
            .SelectMany(node => node.Scenes
                .OrderBy(scene => scene.Key.SceneIndex)
                .Select(scene => new SourceScene(node, scene)))
            .ToArray();
        PlaybackRecordSnapshot[] records = playback.Records.ToArray();
        for (int index = 0; index < records.Length; index++)
        {
            if (records[index] == null
                || records[index].RecordIndex != index
                || records[index].TextJp == null)
            {
                return Result<ProjectPlaybackMappingReport>.Fail(
                    $"Playback record {index} is null or has an invalid positional index/text contract.");
            }
        }

        if (scenes.Any(entry => entry.Scene.DialogueText == null))
        {
            return Result<ProjectPlaybackMappingReport>.Fail(
                "Project snapshot contains a scene with null dialogue text.");
        }

        Dictionary<string, int> sourceTextCounts = scenes
            .Where(entry => entry.Scene.DialogueText.Length > 0)
            .GroupBy(entry => entry.Scene.DialogueText, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        Dictionary<string, int[]> playbackIndicesByText = records
            .Where(record => record.TextJp.Length > 0)
            .GroupBy(record => record.TextJp, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(record => record.RecordIndex).ToArray(),
                StringComparer.Ordinal);

        var candidatesByScene = new List<int>[scenes.Length];
        var proposedIndices = new int?[scenes.Length];
        var proposedStatuses = new ScenePlaybackMappingStatus[scenes.Length];
        for (int sceneIndex = 0; sceneIndex < scenes.Length; sceneIndex++)
        {
            string text = scenes[sceneIndex].Scene.DialogueText;
            int[] textCandidates = text.Length > 0
                && playbackIndicesByText.TryGetValue(text, out int[]? indices)
                    ? indices
                    : Array.Empty<int>();
            candidatesByScene[sceneIndex] = textCandidates.ToList();

            if (text.Length > 0
                && sourceTextCounts[text] == 1
                && textCandidates.Length == 1)
            {
                proposedIndices[sceneIndex] = textCandidates[0];
                proposedStatuses[sceneIndex] =
                    ScenePlaybackMappingStatus.UniqueDialogueTextMatch;
            }
        }

        AddAnchoredNodeSequenceProposals(
            scenes,
            records,
            proposedIndices,
            proposedStatuses,
            candidatesByScene);

        int[] proposalClaimants = new int[records.Length];
        foreach (int recordIndex in proposedIndices.Where(index => index.HasValue)
                     .Select(index => index!.Value))
        {
            proposalClaimants[recordIndex]++;
        }

        var mappings = new ScenePlaybackMapping[scenes.Length];
        var claimedRecords = new HashSet<int>();
        for (int sceneIndex = 0; sceneIndex < scenes.Length; sceneIndex++)
        {
            int? proposal = proposedIndices[sceneIndex];
            bool unique = proposal.HasValue
                && proposalClaimants[proposal.Value] == 1;
            if (proposal.HasValue
                && !candidatesByScene[sceneIndex].Contains(proposal.Value))
            {
                candidatesByScene[sceneIndex].Add(proposal.Value);
                candidatesByScene[sceneIndex].Sort();
            }

            IReadOnlyList<int> candidates = candidatesByScene[sceneIndex].AsReadOnly();
            ScenePlaybackMappingStatus status = unique
                ? proposedStatuses[sceneIndex]
                : candidates.Count == 0
                    ? ScenePlaybackMappingStatus.Unmapped
                    : ScenePlaybackMappingStatus.Ambiguous;
            int? mappedIndex = unique ? proposal : null;
            if (mappedIndex.HasValue)
            {
                claimedRecords.Add(mappedIndex.Value);
            }

            mappings[sceneIndex] = new ScenePlaybackMapping(
                scenes[sceneIndex].Scene.Key,
                status,
                mappedIndex,
                candidates);
        }

        int[] unclaimed = Enumerable.Range(0, records.Length)
            .Where(index => !claimedRecords.Contains(index))
            .ToArray();
        IReadOnlyList<ProjectPlaybackMappingIssue> issues = BuildIssues(
            project,
            playback,
            mappings,
            unclaimed);

        return Result<ProjectPlaybackMappingReport>.Ok(new ProjectPlaybackMappingReport(
            project.Source,
            playback.Source,
            Array.AsReadOnly(mappings),
            Array.AsReadOnly(unclaimed),
            issues));
    }

    private static void AddAnchoredNodeSequenceProposals(
        IReadOnlyList<SourceScene> scenes,
        IReadOnlyList<PlaybackRecordSnapshot> records,
        int?[] proposedIndices,
        ScenePlaybackMappingStatus[] proposedStatuses,
        IReadOnlyList<List<int>> candidatesByScene)
    {
        foreach (IGrouping<int, (SourceScene Scene, int FlatIndex)> nodeGroup in scenes
                     .Select((scene, index) => (Scene: scene, FlatIndex: index))
                     .GroupBy(item => item.Scene.Node.SourceIndex))
        {
            int[] offsets = nodeGroup
                .Where(item => proposedStatuses[item.FlatIndex]
                    == ScenePlaybackMappingStatus.UniqueDialogueTextMatch)
                .Select(item =>
                    proposedIndices[item.FlatIndex]!.Value
                    - item.Scene.Scene.Key.SceneIndex)
                .Distinct()
                .ToArray();
            if (offsets.Length != 1)
            {
                continue;
            }

            int offset = offsets[0];
            bool entireNodeMatches = nodeGroup.All(item =>
            {
                int expectedIndex = offset + item.Scene.Scene.Key.SceneIndex;
                return expectedIndex >= 0
                    && expectedIndex < records.Count
                    && string.Equals(
                        item.Scene.Scene.DialogueText,
                        records[expectedIndex].TextJp,
                        StringComparison.Ordinal);
            });
            if (!entireNodeMatches)
            {
                continue;
            }

            foreach ((SourceScene scene, int flatIndex) in nodeGroup)
            {
                int recordIndex = offset + scene.Scene.Key.SceneIndex;
                if (!candidatesByScene[flatIndex].Contains(recordIndex))
                {
                    candidatesByScene[flatIndex].Add(recordIndex);
                }

                if (!proposedIndices[flatIndex].HasValue)
                {
                    proposedIndices[flatIndex] = recordIndex;
                    proposedStatuses[flatIndex] =
                        ScenePlaybackMappingStatus.AnchoredNodeSequenceMatch;
                }
            }
        }
    }

    private static IReadOnlyList<ProjectPlaybackMappingIssue> BuildIssues(
        ProjectSnapshot project,
        PlaybackArchiveSnapshot playback,
        IReadOnlyCollection<ScenePlaybackMapping> mappings,
        IReadOnlyCollection<int> unclaimed)
    {
        var issues = new List<ProjectPlaybackMappingIssue>();
        string projectName = Path.GetFileNameWithoutExtension(project.Source.FullPath);
        string playbackName = Path.GetFileNameWithoutExtension(playback.Source.FullPath);
        if (!string.Equals(projectName, playbackName, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new ProjectPlaybackMappingIssue(
                ProjectPlaybackMappingIssueSeverity.Warning,
                "FileNameMismatch",
                "Project and playback archive file names differ; caller pairing must be verified explicitly."));
        }

        if (playback.Source.LastWriteTimeUtc + StaleTimestampTolerance
            < project.Source.LastWriteTimeUtc)
        {
            issues.Add(new ProjectPlaybackMappingIssue(
                ProjectPlaybackMappingIssueSeverity.Warning,
                "PlaybackOlderThanProject",
                "The playback archive is older than the project and may not represent its current scenes."));
        }

        int ambiguous = mappings.Count(
            mapping => mapping.Status == ScenePlaybackMappingStatus.Ambiguous);
        int missing = mappings.Count(
            mapping => mapping.Status == ScenePlaybackMappingStatus.Unmapped);
        if (ambiguous > 0)
        {
            issues.Add(new ProjectPlaybackMappingIssue(
                ProjectPlaybackMappingIssueSeverity.Warning,
                "AmbiguousScenes",
                $"{ambiguous} source scenes have non-unique dialogue playback candidates."));
        }

        if (missing > 0)
        {
            issues.Add(new ProjectPlaybackMappingIssue(
                ProjectPlaybackMappingIssueSeverity.Warning,
                "UnmappedScenes",
                $"{missing} source scenes have no verified dialogue or anchored node-sequence match."));
        }

        if (unclaimed.Count > 0)
        {
            issues.Add(new ProjectPlaybackMappingIssue(
                ProjectPlaybackMappingIssueSeverity.Information,
                "UnclaimedPlaybackRecords",
                $"{unclaimed.Count} playback records are synthetic, stale, or not uniquely claimed."));
        }

        return issues.AsReadOnly();
    }

    private sealed record SourceScene(
        StoryNodeSnapshot Node,
        SceneSnapshot Scene);
}
