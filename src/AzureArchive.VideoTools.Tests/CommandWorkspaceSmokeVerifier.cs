using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Mapping;
using AzureArchive.VideoTools.Core.Workspaces;
using AzureArchive.VideoTools.Formats.Aap;
using AzureArchive.VideoTools.Formats.Aas;
using AzureArchive.VideoTools.Formats.Workspace;

namespace AzureArchive.VideoTools.Tests;

internal static class CommandWorkspaceSmokeVerifier
{
    public static int VerifyDirectories(
        string projectDirectory,
        string saveDirectory)
    {
        string projects = Path.GetFullPath(projectDirectory);
        string saves = Path.GetFullPath(saveDirectory);
        if (!Directory.Exists(projects) || !Directory.Exists(saves))
        {
            Console.Error.WriteLine(
                "COMMAND WORKSPACE SMOKE FAIL project or save directory does not exist.");
            return 1;
        }

        var projectReader = new AapProjectReader();
        var playbackReader = new AasScenarioReader();
        var mapper = new ConservativeProjectPlaybackMapper();
        var compiler = new CommandTimelineCompiler(playbackMapper: mapper);
        var binder = new PlaybackCommandBinder();
        using var temporary = new TemporaryAapDirectory();
        var store = new JsonCommandWorkspaceStore(
            Path.Combine(temporary.Root, "command-workspaces"),
            compiler: compiler,
            binder: binder);

        int pairs = 0;
        int eligible = 0;
        int older = 0;
        int roundTrips = 0;
        int failures = 0;
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
                    $"COMMAND WORKSPACE SMOKE FAIL {Path.GetFileNameWithoutExtension(projectPath)}: "
                    + $"AAP={projectResult.Error} AAS={playbackResult.Error}");
                continue;
            }

            var mapping = mapper.Map(projectResult.Value, playbackResult.Value);
            if (!mapping.Success || mapping.Value == null)
            {
                failures++;
                Console.Error.WriteLine(
                    $"COMMAND WORKSPACE SMOKE FAIL {Path.GetFileNameWithoutExtension(projectPath)}: {mapping.Error}");
                continue;
            }

            if (mapping.Value.Issues.Any(issue =>
                    issue.Code == "PlaybackOlderThanProject"))
            {
                older++;
                continue;
            }

            eligible++;
            string workspaceId = Guid.NewGuid().ToString("D");
            var workspace = new ModWorkspaceManifest(
                ModWorkspaceManifest.CurrentSchemaVersion,
                workspaceId,
                "0.7.17",
                "0.7.17",
                new ModWorkspaceSourceBinding(
                    projectResult.Value.Source.PathKey,
                    projectResult.Value.Source.RevisionSha256,
                    playbackResult.Value.Source.PathKey,
                    playbackResult.Value.Source.RevisionSha256,
                    playbackResult.Value.SchemaName),
                Array.AsReadOnly(new[]
                {
                    CharacterTransformCommandFamilyCompiler.CapabilityId
                }));
            var timeline = new CommandTimelineDocument(
                CommandTimelineDocument.CurrentSchemaVersion,
                workspaceId,
                projectResult.Value.Source.PathKey,
                projectResult.Value.Source.RevisionSha256,
                Array.Empty<CommandTimelineEntry>());

            var timelineSave = store.SaveTimelineAtomic(
                projectResult.Value.Source.PathKey,
                timeline,
                workspace,
                projectResult.Value,
                playbackResult.Value);
            var projectionSave = timelineSave.Success
                ? store.CompileAndSaveProjectionAtomic(
                    projectResult.Value.Source.PathKey,
                    timeline,
                    workspace,
                    projectResult.Value,
                    playbackResult.Value)
                : null;
            var timelineLoad = projectionSave?.Success == true
                ? store.TryLoadTimeline(
                    projectResult.Value.Source.PathKey,
                    workspace,
                    projectResult.Value,
                    playbackResult.Value)
                : null;
            var projectionLoad = timelineLoad?.Success == true
                ? store.TryLoadProjection(
                    projectResult.Value.Source.PathKey,
                    timeline,
                    workspace,
                    projectResult.Value,
                    playbackResult.Value)
                : null;

            if (!timelineSave.Success
                || projectionSave?.Success != true
                || timelineLoad?.Value?.Timeline == null
                || projectionLoad?.Value?.Projection == null
                || projectionLoad.Value.Projection.Batches.Count != 0)
            {
                failures++;
                Console.Error.WriteLine(
                    $"COMMAND WORKSPACE SMOKE FAIL {Path.GetFileNameWithoutExtension(projectPath)}: "
                    + (!timelineSave.Success
                        ? timelineSave.Error
                        : projectionSave?.Error
                          ?? timelineLoad?.Error
                          ?? projectionLoad?.Error
                          ?? "round-trip result was incomplete"));
                continue;
            }

            roundTrips++;
        }

        Console.WriteLine(
            $"COMMAND WORKSPACE SMOKE SUMMARY pairs={pairs} eligible={eligible} "
            + $"older={older} roundTrips={roundTrips} failures={failures} "
            + "writes=user-files:0,temp-only:true");
        return failures == 0 ? 0 : 1;
    }
}
