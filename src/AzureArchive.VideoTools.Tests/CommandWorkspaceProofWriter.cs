using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Mapping;
using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Core.Results;
using AzureArchive.VideoTools.Core.Workspaces;
using AzureArchive.VideoTools.Formats.Aap;
using AzureArchive.VideoTools.Formats.Aas;
using AzureArchive.VideoTools.Formats.Workspace;

namespace AzureArchive.VideoTools.Tests;

internal static class CommandWorkspaceProofWriter
{
    private const string ToolVersion = "0.7.20";

    public static int Write(
        string projectPath,
        string playbackPath,
        string workspaceRoot,
        string recordIndexText,
        string directive)
    {
        if (!int.TryParse(recordIndexText, out int recordIndex)
            || recordIndex < 0)
        {
            return Fail("Playback record index must be a non-negative integer.");
        }

        string projectFullPath;
        string playbackFullPath;
        string rootFullPath;
        try
        {
            projectFullPath = Path.GetFullPath(projectPath);
            playbackFullPath = Path.GetFullPath(playbackPath);
            rootFullPath = Path.GetFullPath(workspaceRoot);
        }
        catch (Exception ex)
        {
            return Fail($"Path normalization failed: {ex.Message}");
        }

        if (!string.Equals(
                Path.GetExtension(projectFullPath),
                ".aap",
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                Path.GetExtension(playbackFullPath),
                ".aas",
                StringComparison.OrdinalIgnoreCase)
            || !File.Exists(projectFullPath)
            || !File.Exists(playbackFullPath))
        {
            return Fail("Existing explicit .aap and .aas paths are required.");
        }

        if (!string.Equals(
                Path.GetFileName(rootFullPath),
                "workspaces",
                StringComparison.OrdinalIgnoreCase)
            || !rootFullPath.Contains(
                "AzureArchive.VideoTools",
                StringComparison.OrdinalIgnoreCase))
        {
            return Fail(
                "Proof output must target an AzureArchive.VideoTools/workspaces directory.");
        }

        Result<ProjectSnapshot> projectRead =
            new AapProjectReader().Read(projectFullPath);
        Result<PlaybackArchiveSnapshot> playbackRead =
            new AasScenarioReader().Read(playbackFullPath);
        if (!projectRead.Success || projectRead.Value == null
            || !playbackRead.Success || playbackRead.Value == null)
        {
            return Fail(
                $"Source read failed: AAP={projectRead.Error}; AAS={playbackRead.Error}");
        }

        ProjectSnapshot project = projectRead.Value;
        PlaybackArchiveSnapshot playback = playbackRead.Value;
        if (recordIndex >= playback.Records.Count)
        {
            return Fail(
                $"Playback record {recordIndex} is outside 0..{playback.Records.Count - 1}.");
        }

        var mapper = new ConservativeProjectPlaybackMapper();
        Result<ProjectPlaybackMappingReport> mapping = mapper.Map(project, playback);
        if (!mapping.Success || mapping.Value == null)
        {
            return Fail($"Project/playback mapping failed: {mapping.Error}");
        }

        ScenePlaybackMapping[] mappedScenes = mapping.Value.Scenes
            .Where(scene => scene.PlaybackRecordIndex == recordIndex)
            .ToArray();
        if (mappedScenes.Length != 1)
        {
            return Fail(
                $"Playback record {recordIndex} maps to {mappedScenes.Length} project scenes; exactly one is required.");
        }

        string workspaceId = Guid.NewGuid().ToString("D");
        var source = new ModWorkspaceSourceBinding(
            project.Source.PathKey,
            project.Source.RevisionSha256,
            playback.Source.PathKey,
            playback.Source.RevisionSha256,
            playback.SchemaName);
        var workspace = new ModWorkspaceManifest(
            ModWorkspaceManifest.CurrentSchemaVersion,
            workspaceId,
            ToolVersion,
            ToolVersion,
            source,
            Array.AsReadOnly(new[]
            {
                CharacterTransformCommandFamilyCompiler.CapabilityId
            }));
        var command = new AuthoringCommand(
            Guid.NewGuid().ToString("D"),
            0,
            CommandTimelinePhase.SceneEnter,
            CharacterTransformCommandFamilyCompiler.CommandTypeId,
            directive,
            Enabled: true);
        var timeline = new CommandTimelineDocument(
            CommandTimelineDocument.CurrentSchemaVersion,
            workspaceId,
            project.Source.PathKey,
            project.Source.RevisionSha256,
            Array.AsReadOnly(new[]
            {
                new CommandTimelineEntry(
                    mappedScenes[0].Scene,
                    Array.AsReadOnly(new[] { command }))
            }));

        var compiler = new CommandTimelineCompiler(playbackMapper: mapper);
        Result<PlaybackCommandProjection> compiled = compiler.Compile(
            project,
            playback,
            workspace,
            timeline);
        if (!compiled.Success || compiled.Value == null
            || compiled.Value.Batches.Count != 1)
        {
            return Fail(
                $"Proof timeline did not compile to one batch: {compiled.Error}");
        }

        var context = new ModWorkspaceRuntimeContext(
            ToolVersion,
            source,
            new HashSet<string>(
                new[] { CharacterTransformCommandFamilyCompiler.CapabilityId },
                StringComparer.Ordinal));
        string finalDirectory = Path.Combine(
            rootFullPath,
            project.Source.PathKey.ToUpperInvariant());
        if (Directory.Exists(finalDirectory) || File.Exists(finalDirectory))
        {
            return Fail(
                $"Workspace already exists; refusing to replace it: {finalDirectory}");
        }

        string stagingRoot = Path.Combine(
            rootFullPath,
            ".staging-" + Guid.NewGuid().ToString("N"));
        var workspaceStore = new JsonModWorkspaceStore(stagingRoot);
        var commandStore = new JsonCommandWorkspaceStore(
            stagingRoot,
            compiler: compiler,
            binder: new PlaybackCommandBinder());

        Result<ModWorkspaceSaveSnapshot> manifestSave =
            workspaceStore.SaveAtomic(project.Source.PathKey, workspace, context);
        Result<CommandWorkspaceSaveSnapshot>? timelineSave = manifestSave.Success
            ? commandStore.SaveTimelineAtomic(
                project.Source.PathKey,
                timeline,
                workspace,
                project,
                playback)
            : null;
        Result<CommandWorkspaceSaveSnapshot>? projectionSave =
            timelineSave?.Success == true
                ? commandStore.CompileAndSaveProjectionAtomic(
                    project.Source.PathKey,
                    timeline,
                    workspace,
                    project,
                    playback)
                : null;
        Result<ModWorkspaceLoadSnapshot>? manifestLoad =
            projectionSave?.Success == true
                ? workspaceStore.TryLoad(project.Source.PathKey, context)
                : null;
        Result<CommandTimelineLoadSnapshot>? timelineLoad =
            manifestLoad?.Value?.Manifest != null
                ? commandStore.TryLoadTimeline(
                    project.Source.PathKey,
                    manifestLoad.Value.Manifest,
                    project,
                    playback)
                : null;
        Result<PlaybackCommandProjectionLoadSnapshot>? projectionLoad =
            timelineLoad?.Value?.Timeline != null
                && manifestLoad?.Value?.Manifest != null
                ? commandStore.TryLoadProjection(
                    project.Source.PathKey,
                    timelineLoad.Value.Timeline,
                    manifestLoad.Value.Manifest,
                    project,
                    playback)
                : null;

        if (!manifestSave.Success
            || timelineSave?.Success != true
            || projectionSave?.Success != true
            || manifestLoad?.Value?.Compatibility.CanExecute != true
            || timelineLoad?.Value?.Timeline == null
            || projectionLoad?.Value?.Projection?.Batches.Count != 1)
        {
            return Fail(
                "Staged workspace verification failed: "
                + (manifestSave.Error.Length != 0
                    ? manifestSave.Error
                    : timelineSave?.Error
                      ?? projectionSave?.Error
                      ?? manifestLoad?.Error
                      ?? timelineLoad?.Error
                      ?? projectionLoad?.Error
                      ?? "incomplete staged round trip"));
        }

        string stagedDirectory = Path.Combine(
            stagingRoot,
            project.Source.PathKey.ToUpperInvariant());
        Directory.CreateDirectory(rootFullPath);
        Directory.Move(stagedDirectory, finalDirectory);
        Directory.Delete(stagingRoot);

        PlaybackCommandBatch batch = projectionLoad.Value.Projection.Batches[0];
        Console.WriteLine(
            "COMMAND WORKSPACE PROOF WRITTEN "
            + $"directory={finalDirectory}; workspaceId={workspaceId}; "
            + $"record={batch.PlaybackRecordIndex}; node={batch.Scene.NodeGuid}; "
            + $"scene={batch.Scene.SceneIndex}; commandId={command.CommandId}; "
            + $"directive={batch.Commands[0].CanonicalDirective}; officialWrites=0");
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine("COMMAND WORKSPACE PROOF FAIL " + message);
        return 1;
    }
}
