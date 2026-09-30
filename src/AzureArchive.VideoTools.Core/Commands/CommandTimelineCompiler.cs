using AzureArchive.VideoTools.Core.Mapping;
using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Core.Results;
using AzureArchive.VideoTools.Core.Workspaces;
using AzureArchive.VideoTools.Core.Characters;

namespace AzureArchive.VideoTools.Core.Commands;

public sealed class CommandTimelineCompiler : ICommandTimelineCompiler
{
    private readonly IReadOnlyDictionary<string, ICommandFamilyCompiler> _families;
    private readonly IProjectPlaybackMapper _playbackMapper;

    public CommandTimelineCompiler(
        IEnumerable<ICommandFamilyCompiler>? families = null,
        IProjectPlaybackMapper? playbackMapper = null)
    {
        ICommandFamilyCompiler[] available = (families
                ?? new ICommandFamilyCompiler[]
                {
                    new CharacterTransformCommandFamilyCompiler(),
                    new SlotPendingCommandFamilyCompiler(),
                    new SceneCameraCommandFamilyCompiler(),
                    new SpineOverlayCommandFamilyCompiler(),
                    new CharacterPresetCommandFamilyCompiler()
                })
            .ToArray();
        if (available.Any(family => family == null))
        {
            throw new ArgumentException("Command family collection contains null.", nameof(families));
        }

        try
        {
            _families = available.ToDictionary(
                family => family.CommandType,
                StringComparer.Ordinal);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException(
                "Command family types must be unique.",
                nameof(families),
                exception);
        }

        _playbackMapper = playbackMapper ?? new ConservativeProjectPlaybackMapper();
    }

    public Result<PlaybackCommandProjection> Compile(
        ProjectSnapshot project,
        PlaybackArchiveSnapshot playback,
        ModWorkspaceManifest workspace,
        CommandTimelineDocument timeline)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(playback);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(timeline);

        Result<InputState> validation = ValidateInputs(
            project,
            playback,
            workspace,
            timeline);
        if (!validation.Success || validation.Value == null)
        {
            return Result<PlaybackCommandProjection>.Fail(validation.Error);
        }

        Result<ProjectPlaybackMappingReport> mappingResult =
            _playbackMapper.Map(project, playback);
        if (!mappingResult.Success || mappingResult.Value == null)
        {
            return Result<PlaybackCommandProjection>.Fail(
                $"Project-to-playback mapping failed: {mappingResult.Error}");
        }

        ProjectPlaybackMappingReport mapping = mappingResult.Value;
        ProjectPlaybackMappingIssue? pairingIssue = mapping.Issues.FirstOrDefault(
            issue => issue.Code == "FileNameMismatch");
        if (pairingIssue != null)
        {
            return Result<PlaybackCommandProjection>.Fail(
                $"Playback archive pairing is not current: {pairingIssue.Code}. {pairingIssue.Message}");
        }

        Result<IReadOnlyList<PlaybackCommandBatch>> batches = CompileBatches(
            timeline,
            playback,
            mapping,
            validation.Value);
        if (!batches.Success || batches.Value == null)
        {
            return Result<PlaybackCommandProjection>.Fail(batches.Error);
        }

        Result<string> timelineIdentity = CommandIdentity.Timeline(timeline);
        if (!timelineIdentity.Success || timelineIdentity.Value == null)
        {
            return Result<PlaybackCommandProjection>.Fail(timelineIdentity.Error);
        }

        return Result<PlaybackCommandProjection>.Ok(new PlaybackCommandProjection(
            PlaybackCommandProjection.CurrentSchemaVersion,
            timeline.WorkspaceId,
            timelineIdentity.Value,
            project.Source.PathKey,
            project.Source.RevisionSha256,
            playback.Source.PathKey,
            playback.Source.RevisionSha256,
            playback.SchemaName,
            batches.Value));
    }

    private Result<InputState> ValidateInputs(
        ProjectSnapshot project,
        PlaybackArchiveSnapshot playback,
        ModWorkspaceManifest workspace,
        CommandTimelineDocument timeline)
    {
        Result manifest = ModWorkspaceCompatibilityGate.ValidateManifest(workspace);
        if (!manifest.Success)
        {
            return Result<InputState>.Fail(manifest.Error);
        }

        if (workspace.SchemaVersion != ModWorkspaceManifest.CurrentSchemaVersion)
        {
            return Result<InputState>.Fail(
                $"Workspace schema {workspace.SchemaVersion} must be migrated before command compilation.");
        }

        if (!ValidProjectSource(project)
            || !ValidPlaybackSource(playback))
        {
            return Result<InputState>.Fail(
                "Project or playback snapshot has an invalid source identity.");
        }

        if (!SourceMatches(workspace.Source, project, playback))
        {
            return Result<InputState>.Fail(
                "Workspace source hashes do not match the exact current AAP/AAS pair.");
        }

        if (timeline.SchemaVersion != CommandTimelineDocument.CurrentSchemaVersion)
        {
            return Result<InputState>.Fail(
                $"Unsupported command timeline schema {timeline.SchemaVersion}.");
        }

        if (!Guid.TryParseExact(timeline.WorkspaceId, "D", out _)
            || !string.Equals(
                timeline.WorkspaceId,
                workspace.WorkspaceId,
                StringComparison.OrdinalIgnoreCase))
        {
            return Result<InputState>.Fail(
                "Command timeline workspace ID is invalid or belongs to another workspace.");
        }

        if (!SameSha(timeline.ProjectPathKey, project.Source.PathKey)
            || !SameSha(timeline.ProjectRevisionSha256, project.Source.RevisionSha256))
        {
            return Result<InputState>.Fail(
                "Command timeline must be reconciled to the exact current AAP revision.");
        }

        if (timeline.Entries == null || project.Nodes == null || playback.Records == null)
        {
            return Result<InputState>.Fail(
                "Command timeline, project, or playback collection is missing.");
        }

        Result<IReadOnlyDictionary<string, SceneSnapshot>> scenesResult =
            BuildProjectScenes(project);
        if (!scenesResult.Success || scenesResult.Value == null)
        {
            return Result<InputState>.Fail(scenesResult.Error);
        }

        Result<CompiledScriptIdentity[]> identitiesResult =
            BuildPlaybackIdentities(playback);
        if (!identitiesResult.Success || identitiesResult.Value == null)
        {
            return Result<InputState>.Fail(identitiesResult.Error);
        }

        var entryLocations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var commandIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var enabledCapabilities = new HashSet<string>(StringComparer.Ordinal);
        for (int entryIndex = 0; entryIndex < timeline.Entries.Count; entryIndex++)
        {
            CommandTimelineEntry? entry = timeline.Entries[entryIndex];
            if (entry?.Scene == null || entry.Commands == null)
            {
                return Result<InputState>.Fail(
                    $"Command timeline entry {entryIndex} is null or incomplete.");
            }

            string location = Location(entry.Scene);
            if (!entryLocations.Add(location))
            {
                return Result<InputState>.Fail(
                    $"Command timeline contains duplicate scene location {location}.");
            }

            if (!scenesResult.Value.TryGetValue(location, out SceneSnapshot? current)
                || !SameSha(entry.Scene.Fingerprint, current.Key.Fingerprint))
            {
                return Result<InputState>.Fail(
                    $"Command timeline scene {location} is stale or absent from the current AAP.");
            }

            for (int commandIndex = 0; commandIndex < entry.Commands.Count; commandIndex++)
            {
                AuthoringCommand? command = entry.Commands[commandIndex];
                if (command == null)
                {
                    return Result<InputState>.Fail(
                        $"Command timeline scene {location} contains a null command.");
                }

                if (!Guid.TryParseExact(command.CommandId, "D", out _)
                    || !commandIds.Add(command.CommandId))
                {
                    return Result<InputState>.Fail(
                        $"Command ID at {location}:{commandIndex} is invalid or duplicated.");
                }

                if (command.Order != commandIndex)
                {
                    return Result<InputState>.Fail(
                        $"Commands in scene {location} must use contiguous order values starting at zero.");
                }

                if (command.Phase != CommandTimelinePhase.SceneEnter)
                {
                    return Result<InputState>.Fail(
                        $"Command {command.CommandId} uses an unsupported timeline phase.");
                }

                if (command.Directive == null
                    || !_families.TryGetValue(
                        command.CommandType,
                        out ICommandFamilyCompiler? family))
                {
                    return Result<InputState>.Fail(
                        $"Command {command.CommandId} uses an unsupported command type.");
                }

                if (command.Enabled)
                {
                    enabledCapabilities.Add(family.RequiredCapability);
                }
            }
        }

        foreach (string capability in enabledCapabilities)
        {
            if (!workspace.RequiredCapabilities.Contains(
                    capability,
                    StringComparer.Ordinal))
            {
                return Result<InputState>.Fail(
                    $"Workspace manifest does not declare required capability '{capability}'.");
            }
        }

        return Result<InputState>.Ok(new InputState(
            scenesResult.Value,
            identitiesResult.Value,
            CountIdentities(identitiesResult.Value)));
    }

    private Result<IReadOnlyList<PlaybackCommandBatch>> CompileBatches(
        CommandTimelineDocument timeline,
        PlaybackArchiveSnapshot playback,
        ProjectPlaybackMappingReport mapping,
        InputState input)
    {
        var mappings = new Dictionary<string, ScenePlaybackMapping>(
            StringComparer.OrdinalIgnoreCase);
        foreach (ScenePlaybackMapping sceneMapping in mapping.Scenes)
        {
            if (!mappings.TryAdd(Location(sceneMapping.Scene), sceneMapping))
            {
                return Result<IReadOnlyList<PlaybackCommandBatch>>.Fail(
                    "Project/playback mapping contains a duplicate scene location.");
            }
        }

        var batches = new List<PlaybackCommandBatch>();
        var claimedRecords = new HashSet<int>();
        foreach (CommandTimelineEntry entry in timeline.Entries)
        {
            AuthoringCommand[] enabled = entry.Commands
                .Where(command => command.Enabled)
                .OrderBy(command => command.Order)
                .ToArray();
            if (enabled.Length == 0)
            {
                continue;
            }

            if (!CommandResourceIdentity.IsWithinSceneBudget(enabled.Select(command => command.CommandType)))
            {
                return Result<IReadOnlyList<PlaybackCommandBatch>>.Fail(
                    "Enabled scene commands exceed the supported resource count.");
            }

            string location = Location(entry.Scene);
            if (!mappings.TryGetValue(location, out ScenePlaybackMapping? sceneMapping)
                || sceneMapping.Status is not (
                    ScenePlaybackMappingStatus.UniqueDialogueTextMatch
                    or ScenePlaybackMappingStatus.AnchoredNodeSequenceMatch)
                || !sceneMapping.PlaybackRecordIndex.HasValue)
            {
                return Result<IReadOnlyList<PlaybackCommandBatch>>.Fail(
                    $"Enabled commands at scene {location} require a unique playback mapping.");
            }

            int recordIndex = sceneMapping.PlaybackRecordIndex.Value;
            if (!claimedRecords.Add(recordIndex))
            {
                return Result<IReadOnlyList<PlaybackCommandBatch>>.Fail(
                    $"Multiple command entries target playback record {recordIndex}.");
            }

            PlaybackRecordSnapshot record = playback.Records[recordIndex];
            CompiledScriptIdentity identity = input.PlaybackIdentities[recordIndex];
            if (identity.Utf16Length == 0)
            {
                return Result<IReadOnlyList<PlaybackCommandBatch>>.Fail(
                    $"Enabled commands cannot target empty compiled script at playback record {recordIndex}.");
            }

            // 2026-09-18: a non-unique compiled-script identity is no longer a
            // compile-time failure. Cards whose dialogue is identical — empty
            // camera-only cards above all — compile to the same script, and the
            // engine row index observed in the advance window resolves which
            // record is playing at runtime (ObservedCompiledSceneIdentity.
            // PlaybackRowIndex → PlaybackCommandIndex.Observe). Refusing here
            // used to disable command dispatch for the WHOLE archive; an
            // unresolvable duplicate now fails closed per window instead.

            var occupiedSlots = new HashSet<string>(StringComparer.Ordinal);
            var compiled = new List<PlaybackCommandInstruction>();
            SceneSnapshot projectScene = input.ProjectScenes[location];
            for (int commandIndex = 0; commandIndex < enabled.Length; commandIndex++)
            {
                AuthoringCommand source = enabled[commandIndex];
                ICommandFamilyCompiler family = _families[source.CommandType];
                Result<CanonicalTimelineCommand> canonical =
                    family.Canonicalize(source.Directive);
                if (!canonical.Success || canonical.Value == null)
                {
                    return Result<IReadOnlyList<PlaybackCommandBatch>>.Fail(
                        $"Command {source.CommandId} failed canonicalization: {canonical.Error}");
                }

                if (!occupiedSlots.Add(CommandResourceIdentity.KeyFor(canonical.Value)))
                {
                    return Result<IReadOnlyList<PlaybackCommandBatch>>.Fail(
                        $"Scene {location} contains more than one enabled command for public slot {canonical.Value.PublicSlot}.");
                }

                if (WritesPosition(canonical.Value)
                    && projectScene.Characters.Any(character =>
                        character.PhysicalSlot == canonical.Value.PublicSlot
                        && character.HasOfficialPositionTransition))
                {
                    return Result<IReadOnlyList<PlaybackCommandBatch>>.Fail(
                        $"Scene {location} has an official position transition for public slot {canonical.Value.PublicSlot}; a Mod position command is rejected.");
                }

                compiled.Add(new PlaybackCommandInstruction(
                    source.CommandId,
                    commandIndex,
                    source.Phase,
                    canonical.Value.CommandType,
                    canonical.Value.RequiredCapability,
                    canonical.Value.Directive));
            }

            batches.Add(new PlaybackCommandBatch(
                entry.Scene,
                recordIndex,
                record.Fingerprint,
                identity,
                compiled.AsReadOnly()));
        }

        PlaybackCommandBatch[] ordered = batches
            .OrderBy(batch => batch.PlaybackRecordIndex)
            .ToArray();
        return Result<IReadOnlyList<PlaybackCommandBatch>>.Ok(
            Array.AsReadOnly(ordered));
    }

    private static bool WritesPosition(CanonicalTimelineCommand canonical)
    {
        bool isTransformFamily = string.Equals(
            canonical.CommandType,
            CharacterTransformCommandFamilyCompiler.CommandTypeId,
            StringComparison.Ordinal);
        bool isSlotPendingFamily = string.Equals(
            canonical.CommandType,
            SlotPendingCommandFamilyCompiler.CommandTypeId,
            StringComparison.Ordinal);
        if (!isTransformFamily && !isSlotPendingFamily)
        {
            return false;
        }

        CharacterTransformDirectiveParser parser = isSlotPendingFamily
            ? new CharacterTransformDirectiveParser(
                SlotPendingCommandFamilyCompiler.CanonicalRootToken,
                allowTimingProperties: false)
            : new CharacterTransformDirectiveParser();
        Result<CharacterTransformCommand> parsed = parser.Parse(canonical.Directive);
        if (!parsed.Success || parsed.Value == null)
        {
            return true;
        }

        CharacterTransformCommand command = parsed.Value;
        return command.Operation == CharacterTransformOperation.Reset
            || command.X.HasValue
            || command.Y.HasValue
            || command.DeltaX.HasValue
            || command.DeltaY.HasValue;
    }

    private static Result<IReadOnlyDictionary<string, SceneSnapshot>> BuildProjectScenes(
        ProjectSnapshot project)
    {
        var scenes = new Dictionary<string, SceneSnapshot>(
            StringComparer.OrdinalIgnoreCase);
        for (int nodeIndex = 0; nodeIndex < project.Nodes.Count; nodeIndex++)
        {
            StoryNodeSnapshot? node = project.Nodes[nodeIndex];
            if (node == null)
            {
                return Result<IReadOnlyDictionary<string, SceneSnapshot>>.Fail(
                    $"Project node {nodeIndex} is null.");
            }

            if (node.Kind != StoryNodeKind.Script)
            {
                continue;
            }

            if (node.Scenes == null)
            {
                return Result<IReadOnlyDictionary<string, SceneSnapshot>>.Fail(
                    $"Project script node {nodeIndex} has no scene collection.");
            }

            foreach (SceneSnapshot? scene in node.Scenes)
            {
                if (scene?.Key == null
                    || !Guid.TryParseExact(scene.Key.NodeGuid, "D", out _)
                    || scene.Key.SceneIndex < 0
                    || !CommandIdentity.TryNormalizeSha256(
                        scene.Key.Fingerprint,
                        out _)
                    || scene.DialogueText == null)
                {
                    return Result<IReadOnlyDictionary<string, SceneSnapshot>>.Fail(
                        $"Project script node {nodeIndex} contains an invalid scene.");
                }

                if (!scenes.TryAdd(Location(scene.Key), scene))
                {
                    return Result<IReadOnlyDictionary<string, SceneSnapshot>>.Fail(
                        "Project contains duplicate scene locations.");
                }
            }
        }

        return Result<IReadOnlyDictionary<string, SceneSnapshot>>.Ok(scenes);
    }

    private static Result<CompiledScriptIdentity[]> BuildPlaybackIdentities(
        PlaybackArchiveSnapshot playback)
    {
        var identities = new CompiledScriptIdentity[playback.Records.Count];
        for (int index = 0; index < playback.Records.Count; index++)
        {
            PlaybackRecordSnapshot? record = playback.Records[index];
            if (record == null
                || record.RecordIndex != index
                || record.TextJp == null
                || record.CompiledScript == null
                || !CommandIdentity.TryNormalizeSha256(record.Fingerprint, out _))
            {
                return Result<CompiledScriptIdentity[]>.Fail(
                    $"Playback record {index} violates its positional, text, script, or fingerprint contract.");
            }

            identities[index] = CommandIdentity.CompiledScript(record.CompiledScript);
        }

        return Result<CompiledScriptIdentity[]>.Ok(identities);
    }

    private static Dictionary<CompiledIdentityKey, int> CountIdentities(
        IEnumerable<CompiledScriptIdentity> identities) => identities
        .GroupBy(IdentityKey)
        .ToDictionary(group => group.Key, group => group.Count());

    private static bool ValidProjectSource(ProjectSnapshot project) =>
        project.Source != null
        && !string.IsNullOrWhiteSpace(project.Source.FullPath)
        && CommandIdentity.TryNormalizeSha256(project.Source.PathKey, out _)
        && CommandIdentity.TryNormalizeSha256(project.Source.RevisionSha256, out _);

    private static bool ValidPlaybackSource(PlaybackArchiveSnapshot playback) =>
        playback.Source != null
        && !string.IsNullOrWhiteSpace(playback.Source.FullPath)
        && CommandIdentity.TryNormalizeSha256(playback.Source.PathKey, out _)
        && CommandIdentity.TryNormalizeSha256(playback.Source.RevisionSha256, out _)
        && !string.IsNullOrWhiteSpace(playback.SchemaName);

    private static bool SourceMatches(
        ModWorkspaceSourceBinding source,
        ProjectSnapshot project,
        PlaybackArchiveSnapshot playback) =>
        SameSha(source.ProjectPathKey, project.Source.PathKey)
        && SameSha(source.ProjectRevisionSha256, project.Source.RevisionSha256)
        && SameSha(source.PlaybackPathKey, playback.Source.PathKey)
        && SameSha(source.PlaybackRevisionSha256, playback.Source.RevisionSha256)
        && string.Equals(
            source.PlaybackSchemaName,
            playback.SchemaName,
            StringComparison.Ordinal);

    private static bool SameSha(string? left, string? right) =>
        CommandIdentity.TryNormalizeSha256(left, out string normalizedLeft)
        && CommandIdentity.TryNormalizeSha256(right, out string normalizedRight)
        && string.Equals(normalizedLeft, normalizedRight, StringComparison.Ordinal);

    private static string Location(SceneKey scene) =>
        $"{scene.NodeGuid.Trim().ToLowerInvariant()}:{scene.SceneIndex}";

    private static CompiledIdentityKey IdentityKey(CompiledScriptIdentity identity) =>
        new(identity.Sha256.ToUpperInvariant(), identity.Utf16Length, identity.LineCount);

    private sealed record InputState(
        IReadOnlyDictionary<string, SceneSnapshot> ProjectScenes,
        CompiledScriptIdentity[] PlaybackIdentities,
        IReadOnlyDictionary<CompiledIdentityKey, int> IdentityCounts);

    private readonly record struct CompiledIdentityKey(
        string Sha256,
        int Utf16Length,
        int LineCount);
}
