using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Mapping;
using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Core.Results;
using AzureArchive.VideoTools.Core.Workspaces;
using AzureArchive.VideoTools.Formats.Aap;

namespace AzureArchive.VideoTools.Tests;

internal static class CommandTimelineTests
{
    public static void CanonicalizesCharacterTransformDirective()
    {
        var compiler = new CharacterTransformCommandFamilyCompiler();
        var result = compiler.Canonicalize(
            " #CHAR ; 3 ; SET ; easing=EASEINOUT ; flipX=TRUE ; rotation=-0 ; x=0500 ; duration=0800 ; y=-120 ");

        AssertEx.True(result.Success, result.Error);
        CanonicalTimelineCommand command = AssertEx.NotNull(result.Value);
        AssertEx.Equal(CharacterTransformCommandFamilyCompiler.CommandTypeId, command.CommandType);
        AssertEx.Equal(CharacterTransformCommandFamilyCompiler.CapabilityId, command.RequiredCapability);
        AssertEx.Equal(3, command.PublicSlot);
        AssertEx.Equal(
            "#char;3;set;x=500;y=-120;rotation=0;flipX=true;duration=800;easing=easeInOut",
            command.Directive);

        var defaults = compiler.Canonicalize("#char;2;move;dx=-200");
        AssertEx.True(defaults.Success, defaults.Error);
        AssertEx.Equal(
            "#char;2;move;dx=-200;duration=0;easing=linear",
            AssertEx.NotNull(defaults.Value).Directive);
    }

    public static void CompilesStrictShaBoundProjection()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot project = ReadProject(files, "commands.aap", "A", "B", "C");
        PlaybackArchiveSnapshot playback = Playback(
            project,
            new[] { "A", "B", "C" },
            new[] { "compiled A", "compiled B\nline 2", "compiled C" });
        ModWorkspaceManifest workspace = Workspace(project, playback);
        SceneSnapshot[] scenes = project.ScriptNodes.Single().Scenes.ToArray();
        var disabled = new AuthoringCommand(
            Guid.NewGuid().ToString("D"),
            0,
            CommandTimelinePhase.SceneEnter,
            CharacterTransformCommandFamilyCompiler.CommandTypeId,
            "this disabled directive is intentionally not parsed",
            Enabled: false);
        var enabled = new AuthoringCommand(
            Guid.NewGuid().ToString("D"),
            1,
            CommandTimelinePhase.SceneEnter,
            CharacterTransformCommandFamilyCompiler.CommandTypeId,
            "#char;3;set;y=-0;x=500;easing=easeout;duration=250",
            Enabled: true);
        CommandTimelineDocument timeline = Timeline(
            project,
            workspace,
            new CommandTimelineEntry(
                scenes[0].Key,
                Array.AsReadOnly(new[] { disabled, enabled })),
            new CommandTimelineEntry(
                scenes[1].Key,
                Array.AsReadOnly(new[]
                {
                    Command("#char;2;reset;duration=300;easing=easeInOut")
                })));

        var result = new CommandTimelineCompiler().Compile(
            project,
            playback,
            workspace,
            timeline);

        AssertEx.True(result.Success, result.Error);
        PlaybackCommandProjection projection = AssertEx.NotNull(result.Value);
        AssertEx.Equal(2, projection.Batches.Count);
        AssertEx.Equal(
            AssertEx.NotNull(CommandIdentity.Timeline(timeline).Value),
            projection.SourceTimelineSha256);
        AssertEx.Equal(0, projection.Batches[0].PlaybackRecordIndex);
        AssertEx.Equal(1, projection.Batches[0].CompiledScript.LineCount);
        AssertEx.Equal(1, projection.Batches[0].Commands.Count);
        AssertEx.Equal(0, projection.Batches[0].Commands[0].Order);
        AssertEx.Equal(
            "#char;3;set;x=500;y=0;duration=250;easing=easeOut",
            projection.Batches[0].Commands[0].CanonicalDirective);
        AssertEx.Equal(1, projection.Batches[1].PlaybackRecordIndex);
        AssertEx.Equal(2, projection.Batches[1].CompiledScript.LineCount);
    }

    public static void RejectsEnabledCommandsForSameSlot()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot project = ReadProject(files, "slot-conflict.aap", "A");
        PlaybackArchiveSnapshot playback = Playback(
            project,
            new[] { "A" },
            new[] { "compiled A" });
        ModWorkspaceManifest workspace = Workspace(project, playback);
        SceneSnapshot scene = project.ScriptNodes.Single().Scenes.Single();
        CommandTimelineDocument timeline = Timeline(
            project,
            workspace,
            new CommandTimelineEntry(
                scene.Key,
                Array.AsReadOnly(new[]
                {
                    Command("#char;3;move;dx=100", order: 0),
                    Command("#char;3;set;rotation=10", order: 1)
                })));

        var result = new CommandTimelineCompiler().Compile(
            project,
            playback,
            workspace,
            timeline);

        AssertEx.False(result.Success);
        AssertEx.True(
            result.Error.Contains("more than one enabled command", StringComparison.Ordinal),
            result.Error);
    }

    public static void RejectsPositionCommandDuringOfficialPositionTransition()
    {
        using var files = new TemporaryAapDirectory();
        string path = files.Write(
            "official-position-conflict.aap",
            SyntheticAap.Project(new[]
            {
                SyntheticAap.SceneWithCharacterTransition("A", 3, 3, 4)
            }));
        ProjectSnapshot project = AssertEx.NotNull(
            new AapProjectReader().Read(path).Value);
        PlaybackArchiveSnapshot playback = Playback(
            project,
            new[] { "A" },
            new[] { "compiled A" });
        ModWorkspaceManifest workspace = Workspace(project, playback);
        SceneSnapshot scene = project.ScriptNodes.Single().Scenes.Single();

        var conflict = new CommandTimelineCompiler().Compile(
            project,
            playback,
            workspace,
            Timeline(
                project,
                workspace,
                new CommandTimelineEntry(
                    scene.Key,
                    Array.AsReadOnly(new[]
                    {
                        Command("#char;3;move;dx=100")
                    }))));
        AssertEx.False(conflict.Success);
        AssertEx.True(
            conflict.Error.Contains(
                "official position transition",
                StringComparison.Ordinal),
            conflict.Error);

        var rotationOnly = new CommandTimelineCompiler().Compile(
            project,
            playback,
            workspace,
            Timeline(
                project,
                workspace,
                new CommandTimelineEntry(
                    scene.Key,
                    Array.AsReadOnly(new[]
                    {
                        Command("#char;3;set;rotation=15")
                    }))));
        AssertEx.True(rotationOnly.Success, rotationOnly.Error);
    }

    public static void ResolvesDuplicateCompiledScriptIdentityByPlaybackRow()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot project = ReadProject(files, "duplicate-script.aap", "A", "B", "C");
        // Records 1 and 2 share one compiled script — the empty camera-only card
        // case in the field (there the shared text is the bare newline) — and
        // only record 1 carries a command.
        PlaybackArchiveSnapshot playback = Playback(
            project,
            new[] { "A", "B", "C" },
            new[] { "compiled A", "shared compiled script", "shared compiled script" });
        ModWorkspaceManifest workspace = Workspace(project, playback);
        SceneSnapshot[] scenes = project.ScriptNodes.Single().Scenes.ToArray();

        Result<PlaybackCommandProjection> compiled =
            new CommandTimelineCompiler().Compile(
                project,
                playback,
                workspace,
                Timeline(
                    project,
                    workspace,
                    new CommandTimelineEntry(
                        scenes[1].Key,
                        Array.AsReadOnly(new[] { Command("#char;1;move;dx=100") }))));
        AssertEx.True(compiled.Success, compiled.Error);

        IPlaybackCommandIndex index = AssertEx.NotNull(
            new PlaybackCommandBinder().Bind(playback, compiled.Value).Value);

        // Without a row hint the shared identity stays ambiguous: fail closed.
        Result<PlaybackCommandWindowObservation> noRow =
            new PlaybackCommandWindowObserver().Observe(
                index,
                Array.AsReadOnly(new[] { "shared compiled script" }));
        AssertEx.True(noRow.Success, noRow.Error);
        AssertEx.Equal(
            PlaybackCommandWindowObservationStatus.AmbiguousIdentity,
            AssertEx.NotNull(noRow.Value).Status);

        // The engine row index picks the record that is actually playing.
        Result<PlaybackCommandWindowObservation> atCommand =
            new PlaybackCommandWindowObserver().Observe(
                index,
                Array.AsReadOnly(new[] { "shared compiled script" }),
                playbackRowIndex: 1);
        AssertEx.True(atCommand.Success, atCommand.Error);
        PlaybackCommandWindowObservation commandObservation = AssertEx.NotNull(atCommand.Value);
        AssertEx.Equal(
            PlaybackCommandWindowObservationStatus.CommandBatch,
            commandObservation.Status);
        AssertEx.Equal(1, commandObservation.PlaybackRecordIndex);
        AssertEx.Equal(1, AssertEx.NotNull(commandObservation.Batch).Commands.Count);

        Result<PlaybackCommandWindowObservation> atCommandless =
            new PlaybackCommandWindowObserver().Observe(
                index,
                Array.AsReadOnly(new[] { "shared compiled script" }),
                playbackRowIndex: 2);
        AssertEx.True(atCommandless.Success, atCommandless.Error);
        PlaybackCommandWindowObservation commandlessObservation =
            AssertEx.NotNull(atCommandless.Value);
        AssertEx.Equal(
            PlaybackCommandWindowObservationStatus.OrdinaryRecord,
            commandlessObservation.Status);
        AssertEx.Equal(2, commandlessObservation.PlaybackRecordIndex);
        AssertEx.True(commandlessObservation.Batch == null);

        // A row outside the candidate records cannot resolve anything.
        Result<PlaybackCommandWindowObservation> offRecord =
            new PlaybackCommandWindowObserver().Observe(
                index,
                Array.AsReadOnly(new[] { "shared compiled script" }),
                playbackRowIndex: 0);
        AssertEx.True(offRecord.Success, offRecord.Error);
        AssertEx.Equal(
            PlaybackCommandWindowObservationStatus.AmbiguousIdentity,
            AssertEx.NotNull(offRecord.Value).Status);
    }

    public static void RejectsCommandOrderGap()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot project = ReadProject(files, "order-gap.aap", "A");
        PlaybackArchiveSnapshot playback = Playback(
            project,
            new[] { "A" },
            new[] { "compiled A" });
        ModWorkspaceManifest workspace = Workspace(project, playback);
        SceneSnapshot scene = project.ScriptNodes.Single().Scenes[0];

        var result = new CommandTimelineCompiler().Compile(
            project,
            playback,
            workspace,
            Timeline(
                project,
                workspace,
                new CommandTimelineEntry(
                    scene.Key,
                    Array.AsReadOnly(new[]
                    {
                        Command("#char;1;move;dx=100", order: 1)
                    }))));

        AssertEx.False(result.Success);
        AssertEx.True(
            result.Error.Contains("contiguous order", StringComparison.Ordinal),
            result.Error);
    }

    public static void RejectsMissingWorkspaceCapability()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot project = ReadProject(files, "missing-capability.aap", "A");
        PlaybackArchiveSnapshot playback = Playback(
            project,
            new[] { "A" },
            new[] { "compiled A" });
        ModWorkspaceManifest workspace = Workspace(project, playback) with
        {
            RequiredCapabilities = Array.Empty<string>()
        };
        SceneSnapshot scene = project.ScriptNodes.Single().Scenes[0];

        var result = new CommandTimelineCompiler().Compile(
            project,
            playback,
            workspace,
            Timeline(
                project,
                workspace,
                new CommandTimelineEntry(
                    scene.Key,
                    Array.AsReadOnly(new[] { Command("#char;1;move;dx=100") }))));

        AssertEx.False(result.Success);
        AssertEx.True(
            result.Error.Contains("does not declare required capability", StringComparison.Ordinal),
            result.Error);
    }

    public static void RejectsStaleTimelineRevision()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot project = ReadProject(files, "stale-timeline.aap", "A");
        PlaybackArchiveSnapshot playback = Playback(
            project,
            new[] { "A" },
            new[] { "compiled A" });
        ModWorkspaceManifest workspace = Workspace(project, playback);
        SceneSnapshot scene = project.ScriptNodes.Single().Scenes[0];
        CommandTimelineDocument stale = Timeline(
            project,
            workspace,
            new CommandTimelineEntry(
                scene.Key,
                Array.AsReadOnly(new[] { Command("#char;1;move;dx=100") }))) with
        {
            ProjectRevisionSha256 = new string('0', 64)
        };

        var result = new CommandTimelineCompiler().Compile(
            project,
            playback,
            workspace,
            stale);

        AssertEx.False(result.Success);
        AssertEx.True(
            result.Error.Contains("exact current AAP revision", StringComparison.Ordinal),
            result.Error);
    }

    public static void AllowsOlderPlaybackWhenCommandTargetMapsExactly()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot project = ReadProject(files, "older-exact.aap", "A");
        PlaybackArchiveSnapshot playback = Playback(
            project,
            new[] { "A" },
            new[] { "compiled A" });
        playback = playback with
        {
            Source = playback.Source with
            {
                LastWriteTimeUtc = project.Source.LastWriteTimeUtc
                    - TimeSpan.FromMinutes(1)
            }
        };
        ModWorkspaceManifest workspace = Workspace(project, playback);
        SceneSnapshot scene = project.ScriptNodes.Single().Scenes.Single();

        var result = new CommandTimelineCompiler().Compile(
            project,
            playback,
            workspace,
            Timeline(
                project,
                workspace,
                new CommandTimelineEntry(
                    scene.Key,
                    Array.AsReadOnly(new[]
                    {
                        Command("#char;3;move;dx=100")
                    }))));

        AssertEx.True(result.Success, result.Error);
        AssertEx.Equal(1, AssertEx.NotNull(result.Value).Batches.Count);
    }

    public static void BindsByCompiledScriptAndSnapshotsProjection()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot project = ReadProject(files, "binding.aap", "A", "B");
        PlaybackArchiveSnapshot playback = Playback(
            project,
            new[] { "A", "B" },
            new[] { "compiled A", "compiled B" });
        ModWorkspaceManifest workspace = Workspace(project, playback);
        SceneSnapshot scene = project.ScriptNodes.Single().Scenes[0];
        PlaybackCommandProjection compiled = AssertEx.NotNull(
            new CommandTimelineCompiler().Compile(
                project,
                playback,
                workspace,
                Timeline(
                    project,
                    workspace,
                    new CommandTimelineEntry(
                        scene.Key,
                        Array.AsReadOnly(new[] { Command("#char;4;move;dx=-300") })))).Value);

        var mutableCommands = compiled.Batches[0].Commands.ToList();
        var mutableBatches = new List<PlaybackCommandBatch>
        {
            compiled.Batches[0] with { Commands = mutableCommands }
        };
        PlaybackCommandProjection mutableProjection = compiled with
        {
            Batches = mutableBatches
        };
        var bind = new PlaybackCommandBinder().Bind(playback, mutableProjection);
        AssertEx.True(bind.Success, bind.Error);
        IPlaybackCommandIndex index = AssertEx.NotNull(bind.Value);

        mutableCommands.Clear();
        mutableBatches.Clear();

        PlaybackCommandObservation commandObservation = AssertEx.NotNull(
            index.Observe(Observed(playback.Records[0].CompiledScript)).Value);
        AssertEx.Equal(
            PlaybackCommandObservationStatus.CommandBatch,
            commandObservation.Status);
        AssertEx.Equal(0, commandObservation.PlaybackRecordIndex);
        AssertEx.Equal(1, AssertEx.NotNull(commandObservation.Batch).Commands.Count);

        PlaybackCommandObservation ordinaryObservation = AssertEx.NotNull(
            index.Observe(Observed(playback.Records[1].CompiledScript)).Value);
        AssertEx.Equal(
            PlaybackCommandObservationStatus.OrdinaryRecord,
            ordinaryObservation.Status);
        AssertEx.Equal(1, ordinaryObservation.PlaybackRecordIndex);
    }

    public static void BinderRejectsStaleScriptIdentity()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot project = ReadProject(files, "stale-binding.aap", "A");
        PlaybackArchiveSnapshot playback = Playback(
            project,
            new[] { "A" },
            new[] { "compiled A" });
        ModWorkspaceManifest workspace = Workspace(project, playback);
        SceneSnapshot scene = project.ScriptNodes.Single().Scenes[0];
        PlaybackCommandProjection projection = AssertEx.NotNull(
            new CommandTimelineCompiler().Compile(
                project,
                playback,
                workspace,
                Timeline(
                    project,
                    workspace,
                    new CommandTimelineEntry(
                        scene.Key,
                        Array.AsReadOnly(new[] { Command("#char;1;move;dx=100") })))).Value);
        PlaybackCommandBatch stale = projection.Batches[0] with
        {
            CompiledScript = projection.Batches[0].CompiledScript with
            {
                Sha256 = new string('0', 64)
            }
        };

        var result = new PlaybackCommandBinder().Bind(
            playback,
            projection with { Batches = Array.AsReadOnly(new[] { stale }) });

        AssertEx.False(result.Success);
        AssertEx.True(
            result.Error.Contains("stale compiled-script identity", StringComparison.Ordinal),
            result.Error);
    }

    public static void BinderReportsAmbiguousObservedIdentity()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot project = ReadProject(files, "ambiguous-observation.aap", "A", "B");
        PlaybackArchiveSnapshot playback = Playback(
            project,
            new[] { "A", "B" },
            new[] { "duplicate", "duplicate" });
        IPlaybackCommandIndex index = AssertEx.NotNull(
            new PlaybackCommandBinder().Bind(playback, null).Value);

        PlaybackCommandObservation observation = AssertEx.NotNull(
            index.Observe(Observed("duplicate")).Value);

        AssertEx.Equal(PlaybackCommandObservationStatus.Ambiguous, observation.Status);
        AssertEx.Equal(2, observation.CandidatePlaybackRecordIndices.Count);
        AssertEx.Equal(0, index.BatchCount);
    }

    public static void WindowObserverFindsOneCommandBatchAndIgnoresNoise()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot project = ReadProject(files, "window-command.aap", "A", "B");
        PlaybackArchiveSnapshot playback = Playback(
            project,
            new[] { "A", "B" },
            new[] { "compiled A", "compiled B" });
        ModWorkspaceManifest workspace = Workspace(project, playback);
        PlaybackCommandProjection projection = AssertEx.NotNull(
            new CommandTimelineCompiler().Compile(
                project,
                playback,
                workspace,
                Timeline(
                    project,
                    workspace,
                    new CommandTimelineEntry(
                        project.ScriptNodes.Single().Scenes[0].Key,
                        Array.AsReadOnly(new[]
                        {
                            Command("#char;3;move;dx=500")
                        })))).Value);
        IPlaybackCommandIndex index = AssertEx.NotNull(
            new PlaybackCommandBinder().Bind(playback, projection).Value);

        var result = new PlaybackCommandWindowObserver().Observe(
            index,
            Array.AsReadOnly(new[]
            {
                "unrelated Unity message",
                "compiled A",
                "another unrelated message"
            }));

        AssertEx.True(result.Success, result.Error);
        PlaybackCommandWindowObservation observation = AssertEx.NotNull(result.Value);
        AssertEx.Equal(
            PlaybackCommandWindowObservationStatus.CommandBatch,
            observation.Status);
        AssertEx.Equal(1, observation.ExactRecordCount);
        AssertEx.Equal(0, observation.PlaybackRecordIndex);
        AssertEx.Equal(1, AssertEx.NotNull(observation.Batch).Commands.Count);
    }

    public static void WindowObserverDispatchesUniqueCommandAcrossMultipleRecords()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot project = ReadProject(files, "window-unique-command.aap", "A", "B");
        PlaybackArchiveSnapshot playback = Playback(
            project,
            new[] { "A", "B" },
            new[] { "compiled A", "compiled B" });
        ModWorkspaceManifest workspace = Workspace(project, playback);
        SceneSnapshot[] scenes = project.ScriptNodes.Single().Scenes.ToArray();
        PlaybackCommandProjection projection = AssertEx.NotNull(
            new CommandTimelineCompiler().Compile(
                project,
                playback,
                workspace,
                Timeline(
                    project,
                    workspace,
                    new CommandTimelineEntry(
                        scenes[1].Key,
                        Array.AsReadOnly(new[]
                        {
                            Command("#char;5;set;x=1550;rotation=10")
                        })))).Value);
        IPlaybackCommandIndex index = AssertEx.NotNull(
            new PlaybackCommandBinder().Bind(playback, projection).Value);

        PlaybackCommandWindowObservation observation = AssertEx.NotNull(
            new PlaybackCommandWindowObserver().Observe(
                index,
                Array.AsReadOnly(new[] { "compiled A", "compiled B" })).Value);

        AssertEx.Equal(PlaybackCommandWindowObservationStatus.CommandBatch, observation.Status);
        AssertEx.Equal(2, observation.ExactRecordCount);
        AssertEx.Equal(1, observation.PlaybackRecordIndex);
        AssertEx.Equal(1, AssertEx.NotNull(observation.Batch).Commands.Count);
    }

    public static void WindowObserverRejectsMultipleCommandRecords()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot project = ReadProject(files, "window-multiple-commands.aap", "A", "B");
        PlaybackArchiveSnapshot playback = Playback(
            project,
            new[] { "A", "B" },
            new[] { "compiled A", "compiled B" });
        ModWorkspaceManifest workspace = Workspace(project, playback);
        SceneSnapshot[] scenes = project.ScriptNodes.Single().Scenes.ToArray();
        PlaybackCommandProjection projection = AssertEx.NotNull(
            new CommandTimelineCompiler().Compile(
                project,
                playback,
                workspace,
                Timeline(
                    project,
                    workspace,
                    new CommandTimelineEntry(
                        scenes[0].Key,
                        Array.AsReadOnly(new[] { Command("#char;3;move;dx=100") })),
                    new CommandTimelineEntry(
                        scenes[1].Key,
                        Array.AsReadOnly(new[] { Command("#char;5;set;rotation=10") })))).Value);
        IPlaybackCommandIndex index = AssertEx.NotNull(
            new PlaybackCommandBinder().Bind(playback, projection).Value);

        PlaybackCommandWindowObservation observation = AssertEx.NotNull(
            new PlaybackCommandWindowObserver().Observe(
                index,
                Array.AsReadOnly(new[] { "compiled A", "compiled B" })).Value);

        AssertEx.Equal(PlaybackCommandWindowObservationStatus.MultipleRecords, observation.Status);
        AssertEx.Equal(2, observation.ExactRecordCount);
        AssertEx.True(observation.Batch == null);
    }

    public static void WindowObserverFailsClosedForMultipleOrAmbiguousRecords()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot project = ReadProject(
            files,
            "window-fail-closed.aap",
            "A",
            "B",
            "C",
            "D");
        PlaybackArchiveSnapshot playback = Playback(
            project,
            new[] { "A", "B", "C", "D" },
            new[] { "compiled A", "compiled B", "duplicate", "duplicate" });
        IPlaybackCommandIndex index = AssertEx.NotNull(
            new PlaybackCommandBinder().Bind(playback, null).Value);
        var observer = new PlaybackCommandWindowObserver();

        PlaybackCommandWindowObservation multiple = AssertEx.NotNull(
            observer.Observe(
                index,
                Array.AsReadOnly(new[] { "compiled A", "compiled B" })).Value);
        AssertEx.Equal(
            PlaybackCommandWindowObservationStatus.MultipleRecords,
            multiple.Status);
        AssertEx.Equal(2, multiple.ExactRecordCount);

        PlaybackCommandWindowObservation ambiguous = AssertEx.NotNull(
            observer.Observe(
                index,
                Array.AsReadOnly(new[] { "compiled A", "duplicate" })).Value);
        AssertEx.Equal(
            PlaybackCommandWindowObservationStatus.AmbiguousIdentity,
            ambiguous.Status);
        AssertEx.Equal(1, ambiguous.AmbiguousMessageCount);
        AssertEx.True(ambiguous.Batch == null);
    }

    public static void TimelineEditorUpsertsBySceneAndPhysicalSlot()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot project = ReadProject(files, "editor-upsert.aap", "A");
        PlaybackArchiveSnapshot playback = Playback(
            project,
            new[] { "A" },
            new[] { "compiled A" });
        ModWorkspaceManifest workspace = Workspace(project, playback);
        SceneKey scene = project.ScriptNodes.Single().Scenes[0].Key;
        var editor = new CommandTimelineEditor();
        CommandTimelineDocument empty = Timeline(project, workspace);

        CommandTimelineEditSnapshot added = AssertEx.NotNull(
            editor.UpsertCharacterTransform(
                empty,
                scene,
                "#char;3;move;dx=500").Value);
        AssertEx.False(added.ReplacedExisting);
        AssertEx.Equal(1, added.Timeline.Entries.Count);
        AssertEx.Equal(1, added.Timeline.Entries[0].Commands.Count);

        CommandTimelineEditSnapshot replaced = AssertEx.NotNull(
            editor.UpsertCharacterTransform(
                added.Timeline,
                scene,
                "#char;3;set;x=-400;duration=250").Value);
        AssertEx.True(replaced.ReplacedExisting);
        AssertEx.Equal(added.CommandId, replaced.CommandId);
        AssertEx.Equal(1, replaced.Timeline.Entries[0].Commands.Count);
        AssertEx.Equal(
            "#char;3;set;x=-400;duration=250;easing=linear",
            replaced.Timeline.Entries[0].Commands[0].Directive);

        CommandTimelineEditSnapshot secondSlot = AssertEx.NotNull(
            editor.UpsertCharacterTransform(
                replaced.Timeline,
                scene,
                "#char;2;set;flipX=true").Value);
        AssertEx.Equal(2, secondSlot.Timeline.Entries[0].Commands.Count);
        AssertEx.Equal(0, secondSlot.Timeline.Entries[0].Commands[0].Order);
        AssertEx.Equal(1, secondSlot.Timeline.Entries[0].Commands[1].Order);
    }

    public static void TimelineEditorRemovesAndRenumbersCommands()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot project = ReadProject(files, "editor-remove.aap", "A");
        PlaybackArchiveSnapshot playback = Playback(
            project,
            new[] { "A" },
            new[] { "compiled A" });
        ModWorkspaceManifest workspace = Workspace(project, playback);
        SceneKey scene = project.ScriptNodes.Single().Scenes[0].Key;
        var editor = new CommandTimelineEditor();
        CommandTimelineDocument timeline = AssertEx.NotNull(
            editor.UpsertCharacterTransform(
                Timeline(project, workspace),
                scene,
                "#char;3;move;dx=500").Value).Timeline;
        timeline = AssertEx.NotNull(
            editor.UpsertCharacterTransform(
                timeline,
                scene,
                "#char;2;set;flipX=true").Value).Timeline;

        CommandTimelineEditSnapshot removed = AssertEx.NotNull(
            editor.RemoveCharacterTransform(timeline, scene, 3).Value);
        AssertEx.Equal(1, removed.Timeline.Entries.Count);
        AssertEx.Equal(1, removed.Timeline.Entries[0].Commands.Count);
        AssertEx.Equal(0, removed.Timeline.Entries[0].Commands[0].Order);

        CommandTimelineEditSnapshot emptied = AssertEx.NotNull(
            editor.RemoveCharacterTransform(removed.Timeline, scene, 2).Value);
        AssertEx.Equal(0, emptied.Timeline.Entries.Count);
    }

    private static AuthoringCommand Command(string directive, int order = 0) => new(
        Guid.NewGuid().ToString("D"),
        order,
        CommandTimelinePhase.SceneEnter,
        CharacterTransformCommandFamilyCompiler.CommandTypeId,
        directive,
        Enabled: true);

    private static CommandTimelineDocument Timeline(
        ProjectSnapshot project,
        ModWorkspaceManifest workspace,
        params CommandTimelineEntry[] entries) => new(
        CommandTimelineDocument.CurrentSchemaVersion,
        workspace.WorkspaceId,
        project.Source.PathKey,
        project.Source.RevisionSha256,
        Array.AsReadOnly(entries));

    private static ModWorkspaceManifest Workspace(
        ProjectSnapshot project,
        PlaybackArchiveSnapshot playback) => new(
        ModWorkspaceManifest.CurrentSchemaVersion,
        Guid.NewGuid().ToString("D"),
        "0.7.17",
        "0.7.17",
        new ModWorkspaceSourceBinding(
            project.Source.PathKey,
            project.Source.RevisionSha256,
            playback.Source.PathKey,
            playback.Source.RevisionSha256,
            playback.SchemaName),
        Array.AsReadOnly(new[]
        {
            CharacterTransformCommandFamilyCompiler.CapabilityId
        }));

    private static ProjectSnapshot ReadProject(
        TemporaryAapDirectory files,
        string fileName,
        params string[] dialogue)
    {
        string path = files.Write(
            fileName,
            SyntheticAap.Project(
                dialogue.Select(text => SyntheticAap.Scene(text)).ToArray()));
        var result = new AapProjectReader().Read(path);
        AssertEx.True(result.Success, result.Error);
        return AssertEx.NotNull(result.Value);
    }

    private static PlaybackArchiveSnapshot Playback(
        ProjectSnapshot project,
        IReadOnlyList<string> dialogue,
        IReadOnlyList<string> compiledScripts)
    {
        AssertEx.Equal(dialogue.Count, compiledScripts.Count);
        string fullPath = Path.ChangeExtension(project.Source.FullPath, ".aas");
        var source = new PlaybackArchiveSourceSnapshot(
            fullPath,
            new string('B', 64),
            new string('C', 64),
            dialogue.Count,
            project.Source.LastWriteTimeUtc + TimeSpan.FromSeconds(1));
        PlaybackRecordSnapshot[] records = dialogue
            .Select((text, index) => Record(index, text, compiledScripts[index]))
            .ToArray();
        return new PlaybackArchiveSnapshot(
            source,
            "synthetic/v1",
            Array.AsReadOnly(records));
    }

    private static PlaybackRecordSnapshot Record(
        int index,
        string text,
        string compiledScript) => new(
        index,
        0,
        0,
        0,
        string.Empty,
        0,
        0,
        0,
        string.Empty,
        compiledScript,
        text,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        false,
        index.ToString("X64"));

    private static ObservedCompiledSceneIdentity Observed(string compiledScript)
    {
        CompiledScriptIdentity identity = CommandIdentity.CompiledScript(compiledScript);
        return new ObservedCompiledSceneIdentity(
            identity.Sha256,
            identity.Utf16Length,
            identity.LineCount);
    }
}
