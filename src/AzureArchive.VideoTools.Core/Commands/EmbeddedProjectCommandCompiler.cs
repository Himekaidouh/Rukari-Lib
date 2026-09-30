using System.Security.Cryptography;
using System.Text;
using AzureArchive.VideoTools.Core.Mapping;
using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Core.Results;
using AzureArchive.VideoTools.Core.Workspaces;

namespace AzureArchive.VideoTools.Core.Commands;

public sealed record EmbeddedProjectCommandCompilation(
    CommandTimelineDocument Timeline,
    PlaybackCommandProjection Projection,
    int SceneCount,
    int CommandCount,
    IReadOnlyList<string> ContinueIdentities);

public sealed class EmbeddedProjectCommandCompiler
{
    private readonly EmbeddedAavtDirectiveExtractor _extractor = new();
    private readonly CommandTimelineCompiler _compiler = new();
    private readonly Func<string, string> _promptProjection;

    public EmbeddedProjectCommandCompiler() : this(static text => text) { }

    public EmbeddedProjectCommandCompiler(Func<string, string> promptProjection)
        => _promptProjection = promptProjection ?? throw new ArgumentNullException(nameof(promptProjection));

    public Result<EmbeddedProjectCommandCompilation> Compile(
        ProjectSnapshot project,
        PlaybackArchiveSnapshot playback,
        string pluginVersion,
        bool acceptLegacyCharAlias = true)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(playback);

        for (int recordIndex = 0; recordIndex < playback.Records.Count; recordIndex++)
        {
            EmbeddedAavtExtraction leaked = _extractor.Extract(
                playback.Records[recordIndex].CompiledScript,
                acceptLegacyCharAlias);
            if (leaked.HasEmbeddedDirectives)
            {
                return Result<EmbeddedProjectCommandCompilation>.Fail(
                    $"reason=aas-record-contaminated; record={recordIndex}; "
                    + $"Compiled AAS record {recordIndex} still contains raw AAVT or legacy #char directives. "
                    + "Recompile the AAS while the AAVT ScriptKr sanitizer is active.");
            }
        }

        string workspaceId = DeterministicGuid(
            "AzureArchive.VideoTools/embedded-workspace/v1\n" + project.Source.PathKey);
        var entries = new List<CommandTimelineEntry>();
        var extractions = new Dictionary<string, EmbeddedAavtExtraction>(
            StringComparer.Ordinal);
        int commandCount = 0;

        foreach (StoryNodeSnapshot node in project.ScriptNodes)
        {
            foreach (SceneSnapshot scene in node.Scenes)
            {
                EmbeddedAavtExtraction extracted = _extractor.Extract(
                    _promptProjection(scene.AdditionalPrompt ?? string.Empty),
                    acceptLegacyCharAlias);
                if (extracted.Errors.Count != 0)
                {
                    return Result<EmbeddedProjectCommandCompilation>.Fail(
                        $"reason=embedded-command-invalid; scene={scene.Key.NodeGuid}:{scene.Key.SceneIndex}; "
                        + string.Join(" | ", extracted.Errors));
                }

                extractions[Location(scene.Key)] = extracted;

                if (extracted.Commands.Count == 0)
                {
                    continue;
                }

                AuthoringCommand[] commands = extracted.Commands
                    .Select((command, index) => new AuthoringCommand(
                        DeterministicGuid(
                            $"AzureArchive.VideoTools/embedded-command/v1\n{scene.Key.NodeGuid}\n"
                            + $"{scene.Key.SceneIndex}\n{command.CanonicalDirective}"),
                        index,
                        CommandTimelinePhase.SceneEnter,
                        FamilyTypeIdFor(command.CanonicalDirective),
                        command.CanonicalDirective,
                        Enabled: true))
                    .ToArray();
                entries.Add(new CommandTimelineEntry(
                    scene.Key,
                    Array.AsReadOnly(commands)));
                commandCount += commands.Length;
            }
        }

        var source = new ModWorkspaceSourceBinding(
            project.Source.PathKey,
            project.Source.RevisionSha256,
            playback.Source.PathKey,
            playback.Source.RevisionSha256,
            playback.SchemaName);
        bool hasSlotPendingCommands = entries.Any(entry => entry.Commands.Any(
            command => string.Equals(
                command.CommandType,
                SlotPendingCommandFamilyCompiler.CommandTypeId,
                StringComparison.Ordinal)));
        bool hasCameraCommands = entries.Any(entry => entry.Commands.Any(
            command => string.Equals(
                command.CommandType,
                SceneCameraCommandFamilyCompiler.CommandTypeId,
                StringComparison.Ordinal)));
        bool hasSpineOverlayCommands = entries.Any(entry => entry.Commands.Any(
            command => string.Equals(
                command.CommandType,
                SpineOverlayCommandFamilyCompiler.CommandTypeId,
                StringComparison.Ordinal)));
        bool hasPresetCommands = entries.Any(entry => entry.Commands.Any(
            command => command.CommandType == CharacterPresetCommandFamilyCompiler.CommandTypeId));
        var capabilities = new List<string>
        {
            CharacterTransformCommandFamilyCompiler.CapabilityId
        };
        if (hasSlotPendingCommands)
        {
            capabilities.Add(SlotPendingCommandFamilyCompiler.CapabilityId);
        }

        if (hasCameraCommands)
        {
            capabilities.Add(SceneCameraCommandFamilyCompiler.CapabilityId);
        }

        if (hasPresetCommands) capabilities.Add(CharacterPresetCommandFamilyCompiler.CapabilityId);

        if (hasSpineOverlayCommands)
        {
            capabilities.Add(SpineOverlayCommandFamilyCompiler.CapabilityId);
        }
        var workspace = new ModWorkspaceManifest(
            ModWorkspaceManifest.CurrentSchemaVersion,
            workspaceId,
            pluginVersion,
            pluginVersion,
            source,
            Array.AsReadOnly(capabilities.ToArray()));
        var timeline = new CommandTimelineDocument(
            CommandTimelineDocument.CurrentSchemaVersion,
            workspaceId,
            project.Source.PathKey,
            project.Source.RevisionSha256,
            Array.AsReadOnly(entries.ToArray()));
        Result<PlaybackCommandProjection> compiled = _compiler.Compile(
            project,
            playback,
            workspace,
            timeline);
        if (!compiled.Success || compiled.Value == null)
        {
            return Result<EmbeddedProjectCommandCompilation>.Fail(
                $"reason=embedded-projection-compile-failed; "
                + $"Embedded project commands did not compile: {compiled.Error}");
        }

        Result<IReadOnlyList<string>> continueIdentities = CompileContinueIdentities(
            project,
            playback,
            extractions);
        if (!continueIdentities.Success || continueIdentities.Value == null)
        {
            return Result<EmbeddedProjectCommandCompilation>.Fail(
                $"reason=embedded-continue-identity-failed; {continueIdentities.Error}");
        }

        return Result<EmbeddedProjectCommandCompilation>.Ok(new(
            timeline,
            compiled.Value,
            entries.Count,
            commandCount,
            continueIdentities.Value));
    }

    private static Result<IReadOnlyList<string>> CompileContinueIdentities(
        ProjectSnapshot project,
        PlaybackArchiveSnapshot playback,
        IReadOnlyDictionary<string, EmbeddedAavtExtraction> extractions)
    {
        if (!extractions.Values.Any(extraction => extraction.HasContinueDirective))
        {
            return Result<IReadOnlyList<string>>.Ok(Array.Empty<string>());
        }

        Result<ProjectPlaybackMappingReport> mapped =
            new ConservativeProjectPlaybackMapper().Map(project, playback);
        if (!mapped.Success || mapped.Value == null)
        {
            return Result<IReadOnlyList<string>>.Fail(
                $"project/playback mapping failed: {mapped.Error}");
        }

        var markersByIdentity = new Dictionary<string, bool>(
            StringComparer.OrdinalIgnoreCase);
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ScenePlaybackMapping sceneMapping in mapped.Value.Scenes)
        {
            string location = Location(sceneMapping.Scene);
            if (!extractions.TryGetValue(location, out EmbeddedAavtExtraction? extraction))
            {
                continue;
            }

            bool hasMarker = extraction.HasContinueDirective;
            bool uniquelyMapped = sceneMapping.Status is
                    ScenePlaybackMappingStatus.UniqueDialogueTextMatch
                    or ScenePlaybackMappingStatus.AnchoredNodeSequenceMatch
                && sceneMapping.PlaybackRecordIndex.HasValue;
            if (!uniquelyMapped)
            {
                if (hasMarker)
                {
                    return Result<IReadOnlyList<string>>.Fail(
                        $"continue scene {location} is not uniquely mapped to the AAS");
                }

                continue;
            }

            int recordIndex = sceneMapping.PlaybackRecordIndex!.Value;
            if (recordIndex < 0 || recordIndex >= playback.Records.Count)
            {
                return Result<IReadOnlyList<string>>.Fail(
                    $"continue scene {location} mapped outside the AAS record range");
            }

            string identity = CommandIdentity.CompiledScript(
                playback.Records[recordIndex].CompiledScript).Sha256;
            if (markersByIdentity.TryGetValue(identity, out bool existing)
                && existing != hasMarker)
            {
                return Result<IReadOnlyList<string>>.Fail(
                    $"compiled script identity {identity[..16]} is shared by continue and non-continue scenes");
            }

            markersByIdentity[identity] = hasMarker;
            if (hasMarker)
            {
                identities.Add(identity);
            }
        }

        return Result<IReadOnlyList<string>>.Ok(Array.AsReadOnly(
            identities.OrderBy(identity => identity, StringComparer.Ordinal).ToArray()));
    }

    private static string Location(SceneKey scene) =>
        $"{scene.NodeGuid}:{scene.SceneIndex}";

    internal static string DeterministicGuid(string input)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        byte[] guidBytes = digest[..16];
        guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);
        return new Guid(guidBytes).ToString("D");
    }

    internal static string FamilyTypeIdFor(string canonicalDirective) =>
        canonicalDirective.StartsWith(CharacterPresetCommandFamilyCompiler.CanonicalRootToken + ";", StringComparison.Ordinal)
            ? CharacterPresetCommandFamilyCompiler.CommandTypeId
            : canonicalDirective.StartsWith(
            SceneCameraCommandFamilyCompiler.CanonicalRootToken + ";",
            StringComparison.Ordinal)
            ? SceneCameraCommandFamilyCompiler.CommandTypeId
            : canonicalDirective.StartsWith(
            SpineOverlayCommandFamilyCompiler.CanonicalRootToken + ";",
            StringComparison.Ordinal)
            ? SpineOverlayCommandFamilyCompiler.CommandTypeId
            : canonicalDirective.StartsWith(
            SlotPendingCommandFamilyCompiler.CanonicalRootToken + ";",
            StringComparison.Ordinal)
            ? SlotPendingCommandFamilyCompiler.CommandTypeId
            : CharacterTransformCommandFamilyCompiler.CommandTypeId;
}
