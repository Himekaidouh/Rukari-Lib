using AzureArchive.VideoTools.Core.Continuity;
using AzureArchive.VideoTools.Core.Mapping;
using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Compilation;

public sealed class ContinuationProjectionCompiler : IContinuationProjectionCompiler
{
    private readonly IContinuationEngine _continuity;
    private readonly IProjectPlaybackMapper _playbackMapper;

    public ContinuationProjectionCompiler(
        IContinuationEngine? continuity = null,
        IProjectPlaybackMapper? playbackMapper = null)
    {
        _continuity = continuity ?? new ContinuationEngine();
        _playbackMapper = playbackMapper ?? new ConservativeProjectPlaybackMapper();
    }

    public Result<ContinuationPlaybackProjection> Compile(
        ProjectSnapshot project,
        PlaybackArchiveSnapshot playback,
        ContinuationDocument metadata)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(playback);
        ArgumentNullException.ThrowIfNull(metadata);

        Result inputValidation = ValidateInputs(project, playback, metadata);
        if (!inputValidation.Success)
        {
            return Result<ContinuationPlaybackProjection>.Fail(inputValidation.Error);
        }

        Result<ContinuationPlan> planResult = _continuity.Build(project, metadata);
        if (!planResult.Success || planResult.Value == null)
        {
            return Result<ContinuationPlaybackProjection>.Fail(
                $"Continuation plan failed: {planResult.Error}");
        }

        ContinuationPlan plan = planResult.Value;
        ContinuationIssue[] blockingPlanIssues = plan.Issues
            .Where(issue => issue.Severity != ContinuationIssueSeverity.Information)
            .ToArray();
        if (blockingPlanIssues.Length > 0)
        {
            return Result<ContinuationPlaybackProjection>.Fail(
                "Continuation plan contains blocking issues: "
                + string.Join(
                    ", ",
                    blockingPlanIssues.Select(issue => issue.Code)));
        }

        int enabledRuleCount = metadata.Rules.Count(rule => rule.Enabled);
        DialoguePlanEntry[] appendEntries = plan.Entries
            .Where(entry => entry.Operation == DialogueOperation.Append)
            .ToArray();
        if (appendEntries.Length != enabledRuleCount)
        {
            return Result<ContinuationPlaybackProjection>.Fail(
                "Not every enabled continuation rule resolved to one append plan entry.");
        }

        Result<ProjectPlaybackMappingReport> mappingResult =
            _playbackMapper.Map(project, playback);
        if (!mappingResult.Success || mappingResult.Value == null)
        {
            return Result<ContinuationPlaybackProjection>.Fail(
                $"Project-to-playback mapping failed: {mappingResult.Error}");
        }

        ProjectPlaybackMappingReport mapping = mappingResult.Value;
        ProjectPlaybackMappingIssue? pairingIssue = mapping.Issues.FirstOrDefault(
            issue => issue.Code is "FileNameMismatch" or "PlaybackOlderThanProject");
        if (pairingIssue != null)
        {
            return Result<ContinuationPlaybackProjection>.Fail(
                $"Playback archive pairing is not current: {pairingIssue.Code}. {pairingIssue.Message}");
        }

        Result<IReadOnlyList<ContinuationPlaybackInstruction>> instructionResult =
            CompileInstructions(plan, mapping, playback);
        if (!instructionResult.Success || instructionResult.Value == null)
        {
            return Result<ContinuationPlaybackProjection>.Fail(instructionResult.Error);
        }

        return Result<ContinuationPlaybackProjection>.Ok(
            new ContinuationPlaybackProjection(
                ContinuationPlaybackProjection.CurrentSchemaVersion,
                metadata.ProjectId,
                project.Source.PathKey,
                project.Source.RevisionSha256,
                playback.Source.PathKey,
                playback.Source.RevisionSha256,
                playback.SchemaName,
                instructionResult.Value));
    }

    private static Result ValidateInputs(
        ProjectSnapshot project,
        PlaybackArchiveSnapshot playback,
        ContinuationDocument metadata)
    {
        if (project.Source == null
            || string.IsNullOrWhiteSpace(project.Source.FullPath)
            || !IsSha256(project.Source.PathKey)
            || !IsSha256(project.Source.RevisionSha256))
        {
            return Result.Fail("Project snapshot has invalid source identity.");
        }

        if (playback.Source == null
            || string.IsNullOrWhiteSpace(playback.Source.FullPath)
            || !IsSha256(playback.Source.PathKey)
            || !IsSha256(playback.Source.RevisionSha256)
            || string.IsNullOrWhiteSpace(playback.SchemaName))
        {
            return Result.Fail("Playback snapshot has invalid source identity or schema.");
        }

        Result projectStructureValidation = ValidateProjectStructure(project);
        if (!projectStructureValidation.Success)
        {
            return projectStructureValidation;
        }

        if (metadata.SchemaVersion != ContinuationDocument.CurrentSchemaVersion)
        {
            return Result.Fail(
                $"Unsupported continuation schema version {metadata.SchemaVersion}.");
        }

        if (!Guid.TryParseExact(metadata.ProjectId, "D", out _))
        {
            return Result.Fail("Continuation project ID must be a canonical UUID.");
        }

        if (!string.Equals(
                metadata.ProjectPathKey,
                project.Source.PathKey,
                StringComparison.OrdinalIgnoreCase))
        {
            return Result.Fail("Continuation metadata belongs to a different project path key.");
        }

        if (!string.Equals(
                metadata.ProjectRevisionSha256,
                project.Source.RevisionSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return Result.Fail(
                "Continuation metadata must be reconciled to the exact current AAP revision before playback projection.");
        }

        if (metadata.Rules == null)
        {
            return Result.Fail("Continuation metadata has no rules collection.");
        }

        var ruleLocations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < metadata.Rules.Count; index++)
        {
            ContinuationRule? rule = metadata.Rules[index];
            if (rule?.Scene == null
                || !Guid.TryParseExact(rule.Scene.NodeGuid, "D", out _)
                || rule.Scene.SceneIndex <= 0
                || !IsSha256(rule.Scene.Fingerprint))
            {
                return Result.Fail($"Continuation rule {index} has an invalid scene identity.");
            }

            string location = $"{rule.Scene.NodeGuid}:{rule.Scene.SceneIndex}";
            if (!ruleLocations.Add(location))
            {
                return Result.Fail(
                    $"Continuation metadata contains duplicate rules for {location}.");
            }
        }

        if (playback.Records == null)
        {
            return Result.Fail("Playback snapshot has no record collection.");
        }

        for (int index = 0; index < playback.Records.Count; index++)
        {
            PlaybackRecordSnapshot? record = playback.Records[index];
            if (record == null
                || record.RecordIndex != index
                || record.TextJp == null
                || !IsSha256(record.Fingerprint))
            {
                return Result.Fail(
                    $"Playback record {index} has an invalid positional identity or fingerprint.");
            }
        }

        return Result.Ok();
    }

    private static Result ValidateProjectStructure(ProjectSnapshot project)
    {
        if (project.Nodes == null)
        {
            return Result.Fail("Project snapshot has no node collection.");
        }

        for (int nodeIndex = 0; nodeIndex < project.Nodes.Count; nodeIndex++)
        {
            StoryNodeSnapshot? node = project.Nodes[nodeIndex];
            if (node == null)
            {
                return Result.Fail($"Project node {nodeIndex} is null.");
            }

            if (node.Kind != StoryNodeKind.Script)
            {
                continue;
            }

            if (node.Scenes == null)
            {
                return Result.Fail($"Project script node {nodeIndex} has no scene collection.");
            }

            for (int sceneIndex = 0; sceneIndex < node.Scenes.Count; sceneIndex++)
            {
                SceneSnapshot? scene = node.Scenes[sceneIndex];
                if (scene?.Key == null
                    || scene.Key.SceneIndex < 0
                    || string.IsNullOrWhiteSpace(scene.Key.NodeGuid)
                    || scene.DialogueText == null)
                {
                    return Result.Fail(
                        $"Project script node {nodeIndex} scene {sceneIndex} has an invalid identity or text contract.");
                }
            }
        }

        return Result.Ok();
    }

    private static Result<IReadOnlyList<ContinuationPlaybackInstruction>> CompileInstructions(
        ContinuationPlan plan,
        ProjectPlaybackMappingReport mapping,
        PlaybackArchiveSnapshot playback)
    {
        var planByLocation = new Dictionary<string, DialoguePlanEntry>(
            StringComparer.OrdinalIgnoreCase);
        foreach (DialoguePlanEntry entry in plan.Entries)
        {
            string location = Location(entry.Scene);
            if (!planByLocation.TryAdd(location, entry))
            {
                return Result<IReadOnlyList<ContinuationPlaybackInstruction>>.Fail(
                    $"Continuation plan contains duplicate scene location {location}.");
            }
        }

        var mappingByLocation = new Dictionary<string, ScenePlaybackMapping>(
            StringComparer.OrdinalIgnoreCase);
        foreach (ScenePlaybackMapping sceneMapping in mapping.Scenes)
        {
            string location = Location(sceneMapping.Scene);
            if (!mappingByLocation.TryAdd(location, sceneMapping))
            {
                return Result<IReadOnlyList<ContinuationPlaybackInstruction>>.Fail(
                    $"Playback mapping contains duplicate scene location {location}.");
            }
        }

        var instructions = new List<ContinuationPlaybackInstruction>();
        var claimedInstructionRecords = new HashSet<int>();
        foreach (DialoguePlanEntry appendEntry in plan.Entries.Where(
                     entry => entry.Operation == DialogueOperation.Append))
        {
            Result<IReadOnlyList<ChainMember>> chainResult = ResolveChain(
                appendEntry,
                planByLocation,
                mappingByLocation,
                playback);
            if (!chainResult.Success || chainResult.Value == null)
            {
                return Result<IReadOnlyList<ContinuationPlaybackInstruction>>.Fail(
                    chainResult.Error);
            }

            IReadOnlyList<ChainMember> chain = chainResult.Value;
            ChainMember current = chain[^1];
            ChainMember previous = chain[^2];
            ChainMember chainStart = chain[0];
            if (!claimedInstructionRecords.Add(current.Record.RecordIndex))
            {
                return Result<IReadOnlyList<ContinuationPlaybackInstruction>>.Fail(
                    $"Multiple continuation instructions target playback record {current.Record.RecordIndex}.");
            }

            instructions.Add(new ContinuationPlaybackInstruction(
                current.Entry.Scene,
                previous.Entry.Scene,
                chainStart.Entry.Scene,
                current.Record.RecordIndex,
                current.Record.Fingerprint,
                previous.Record.RecordIndex,
                chainStart.Record.RecordIndex,
                current.Entry.LockedPrefix,
                current.Entry.Suffix,
                current.Entry.VisibleText,
                current.Entry.TypewriterStartCharacterIndex));
        }

        ContinuationPlaybackInstruction[] ordered = instructions
            .OrderBy(instruction => instruction.PlaybackRecordIndex)
            .ToArray();
        return Result<IReadOnlyList<ContinuationPlaybackInstruction>>.Ok(
            Array.AsReadOnly(ordered));
    }

    private static Result<IReadOnlyList<ChainMember>> ResolveChain(
        DialoguePlanEntry appendEntry,
        IReadOnlyDictionary<string, DialoguePlanEntry> planByLocation,
        IReadOnlyDictionary<string, ScenePlaybackMapping> mappingByLocation,
        PlaybackArchiveSnapshot playback)
    {
        if (appendEntry.ChainStartSceneIndex < 0
            || appendEntry.ChainStartSceneIndex >= appendEntry.Scene.SceneIndex)
        {
            return ChainFailure(
                appendEntry,
                "has an invalid chain start scene index");
        }

        var chain = new List<ChainMember>();
        string expectedVisibleText = string.Empty;
        int? previousRecordIndex = null;
        for (int sceneIndex = appendEntry.ChainStartSceneIndex;
             sceneIndex <= appendEntry.Scene.SceneIndex;
             sceneIndex++)
        {
            string location = Location(appendEntry.Scene.NodeGuid, sceneIndex);
            if (!planByLocation.TryGetValue(location, out DialoguePlanEntry? entry))
            {
                return ChainFailure(
                    appendEntry,
                    $"is missing source scene {sceneIndex}");
            }

            if (!Guid.TryParseExact(entry.Scene.NodeGuid, "D", out _)
                || !IsSha256(entry.Scene.Fingerprint))
            {
                return ChainFailure(
                    appendEntry,
                    $"has no stable scene identity at source scene {sceneIndex}");
            }

            DialogueOperation expectedOperation = sceneIndex == appendEntry.ChainStartSceneIndex
                ? DialogueOperation.Replace
                : DialogueOperation.Append;
            if (entry.Operation != expectedOperation
                || !string.Equals(entry.LockedPrefix, expectedVisibleText, StringComparison.Ordinal))
            {
                return ChainFailure(
                    appendEntry,
                    $"has inconsistent dialogue semantics at source scene {sceneIndex}");
            }

            expectedVisibleText = expectedOperation == DialogueOperation.Replace
                ? entry.Suffix
                : expectedVisibleText + entry.Suffix;
            if (!string.Equals(entry.VisibleText, expectedVisibleText, StringComparison.Ordinal))
            {
                return ChainFailure(
                    appendEntry,
                    $"has inconsistent visible text at source scene {sceneIndex}");
            }

            if (!mappingByLocation.TryGetValue(location, out ScenePlaybackMapping? sceneMapping)
                || sceneMapping.Status is not (
                    ScenePlaybackMappingStatus.UniqueDialogueTextMatch
                    or ScenePlaybackMappingStatus.AnchoredNodeSequenceMatch)
                || !sceneMapping.PlaybackRecordIndex.HasValue)
            {
                return ChainFailure(
                    appendEntry,
                    $"requires a unique playback mapping for source scene {sceneIndex}");
            }

            int recordIndex = sceneMapping.PlaybackRecordIndex.Value;
            if (recordIndex < 0 || recordIndex >= playback.Records.Count)
            {
                return ChainFailure(
                    appendEntry,
                    $"maps source scene {sceneIndex} outside the playback archive");
            }

            PlaybackRecordSnapshot record = playback.Records[recordIndex];
            if (previousRecordIndex.HasValue && recordIndex != previousRecordIndex.Value + 1)
            {
                return ChainFailure(
                    appendEntry,
                    $"crosses a non-contiguous playback record before source scene {sceneIndex}");
            }

            if (!string.Equals(record.TextJp, entry.Suffix, StringComparison.Ordinal))
            {
                return ChainFailure(
                    appendEntry,
                    $"has a source/playback text mismatch at source scene {sceneIndex}");
            }

            chain.Add(new ChainMember(entry, record));
            previousRecordIndex = recordIndex;
        }

        if (chain.Count < 2)
        {
            return ChainFailure(appendEntry, "does not contain a previous scene");
        }

        return Result<IReadOnlyList<ChainMember>>.Ok(chain.AsReadOnly());
    }

    private static Result<IReadOnlyList<ChainMember>> ChainFailure(
        DialoguePlanEntry entry,
        string reason) =>
        Result<IReadOnlyList<ChainMember>>.Fail(
            $"Continuation scene {entry.Scene.NodeGuid}:{entry.Scene.SceneIndex} {reason}.");

    private static string Location(SceneKey scene) =>
        Location(scene.NodeGuid, scene.SceneIndex);

    private static string Location(string nodeGuid, int sceneIndex) =>
        $"{nodeGuid.Trim().ToLowerInvariant()}:{sceneIndex}";

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private sealed record ChainMember(
        DialoguePlanEntry Entry,
        PlaybackRecordSnapshot Record);
}
