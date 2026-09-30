using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Formats.Aap;

namespace AzureArchive.VideoTools.Tests;

internal static class EmbeddedAavtDirectiveTests
{
    public static void ForeignChildRoutesAreExcludedFromSavedCommandsAndPreviewChains()
    {
        using var files = new TemporaryAapDirectory();
        const string prompt = "#aavt;char;3;set;x=50\n#aavt;camera;set;x=100\n#aavt;continue";
        string path = files.Write("ownership.aap", SyntheticAap.Project(new[]
        {
            SyntheticAap.Scene("A", additionalPrompt: prompt)
        }));
        ProjectSnapshot project = AssertEx.NotNull(new AapProjectReader().Read(path).Value);
        PlaybackArchiveSnapshot playback = Playback(project, "A", "compiled A");
        using var service = new Rukari.Lib.Commands.EmbeddedDirectiveService(static () => true, static () => true, null);
        AssertEx.True(service.Register("rukari.moreeffects", new[] { "#aavt" }, static _ => { }).Success);
        AssertEx.True(service.Register("foreign", new[] { "#aavt;camera", "#aavt;continue" }, static _ => { }).Success);
        var snapshot = service.CaptureSanitizer();
        string Filter(string text) => snapshot.SanitizeExceptOwners(text, new[] { "rukari.moreeffects", "rukari.charactervoice" });
        var result = new EmbeddedProjectCommandCompiler(Filter).Compile(project, playback, "1.1.0");
        AssertEx.True(result.Success, result.Error);
        EmbeddedProjectCommandCompilation compiled = AssertEx.NotNull(result.Value);
        AssertEx.Equal(1, compiled.CommandCount);
        AssertEx.Equal(0, compiled.ContinueIdentities.Count);
        AssertEx.True(compiled.Projection.Batches.Single().Commands.Single().CanonicalDirective.StartsWith("#char;", StringComparison.Ordinal));
        PreviewChainDirectiveIndex chain = PreviewChainDirectiveIndex.Build(project, true, Filter);
        AssertEx.Equal(1, chain.DirectiveCount);
        AssertEx.Equal(0, chain.CameraSceneCount);
        AssertEx.Equal(prompt, project.ScriptNodes.Single().Scenes.Single().AdditionalPrompt);
    }

    public static void ExtractsAavtAndPreservesOfficialDirectives()
    {
        const string input =
            "#wait;100\r\n"
            + " #aavt;char;3;move;dx=500;duration=800;easing=EASEINOUT \r\n"
            + "#char;2;reset\n"
            + "#hidemenu";

        EmbeddedAavtExtraction result =
            new EmbeddedAavtDirectiveExtractor().Extract(input);

        AssertEx.Equal("#wait;100\r\n#hidemenu", result.SanitizedText);
        AssertEx.Equal(2, result.RemovedLineCount);
        AssertEx.Equal(0, result.Errors.Count);
        AssertEx.Equal(2, result.Commands.Count);
        AssertEx.Equal(3, result.Commands[0].PublicSlot);
        AssertEx.Equal(
            "#char;3;move;dx=500;duration=800;easing=easeInOut",
            result.Commands[0].CanonicalDirective);
        AssertEx.Equal(
            "#char;2;reset;duration=0;easing=linear",
            result.Commands[1].CanonicalDirective);
    }

    public static void RejectsMalformedAndDuplicateEmbeddedSlots()
    {
        const string input =
            "#aavt;camera;3;move;dx=1\n"
            + "#aavt;char;3;move;dx=100\n"
            + "#aavt;char;3;set;x=200";

        EmbeddedAavtExtraction result =
            new EmbeddedAavtDirectiveExtractor().Extract(input);

        AssertEx.Equal(string.Empty, result.SanitizedText);
        AssertEx.Equal(3, result.RemovedLineCount);
        AssertEx.Equal(2, result.Errors.Count);
        AssertEx.Equal(1, result.Commands.Count);
    }

    public static void ExtractsContinueMarkerWithoutSlotCost()
    {
        const string input =
            "#aavt;continue\n"
            + "#aavt;char;2;move;dx=100\n"
            + "正文台词";

        EmbeddedAavtExtraction result =
            new EmbeddedAavtDirectiveExtractor().Extract(input);

        AssertEx.True(result.HasContinueDirective);
        AssertEx.Equal(0, result.Errors.Count);
        AssertEx.Equal(2, result.RemovedLineCount);
        AssertEx.Equal(1, result.Commands.Count);
        AssertEx.True(result.SanitizedText.Contains("正文台词", StringComparison.Ordinal));
        AssertEx.False(result.SanitizedText.Contains("continue", StringComparison.Ordinal));

        EmbeddedAavtExtraction markerOnly =
            new EmbeddedAavtDirectiveExtractor().Extract("#AAVT;CONTINUE");
        AssertEx.True(markerOnly.HasContinueDirective);
        AssertEx.Equal(0, markerOnly.Commands.Count);
        AssertEx.Equal(0, markerOnly.Errors.Count);
        AssertEx.Equal(string.Empty, markerOnly.SanitizedText);

        EmbeddedAavtExtraction malformed =
            new EmbeddedAavtDirectiveExtractor().Extract("#aavt;continue;extra=1");
        AssertEx.False(malformed.HasContinueDirective);
        AssertEx.Equal(1, malformed.Errors.Count);
    }

    public static void ClassifiesCommandlessNonVisualCapturesForTheTombstoneGate()
    {
        var extractor = new EmbeddedAavtDirectiveExtractor();

        // A bare marker is deliberate: it is removed, yields no command, and
        // must still own cleanup plus inherited-chain semantics in the editor
        // preview lease (captured as an explicit tombstone).
        EmbeddedAavtExtraction continueOnly = extractor.Extract("#aavt;continue");
        AssertEx.True(continueOnly.HasContinueDirective);
        AssertEx.Equal(0, continueOnly.Commands.Count);
        AssertEx.Equal(0, continueOnly.Errors.Count);
        AssertEx.True(continueOnly.HasOnlyNonVisualDirectives);

        // The mirrored voice line is removed the same way and carries no command.
        EmbeddedAavtExtraction voiceOnly =
            extractor.Extract("#aavt;voice;line_001.wav");
        AssertEx.False(voiceOnly.HasContinueDirective);
        AssertEx.Equal(0, voiceOnly.Commands.Count);
        AssertEx.Equal(0, voiceOnly.Errors.Count);
        AssertEx.True(voiceOnly.HasOnlyNonVisualDirectives);

        EmbeddedAavtExtraction markerAndVoice = extractor.Extract(
            "#aavt;voice;line_001.wav\r\n#aavt;continue");
        AssertEx.True(markerAndVoice.HasOnlyNonVisualDirectives);

        // A real visual command keeps the scene on the ordinary candidate path.
        EmbeddedAavtExtraction withCommand = extractor.Extract(
            "#aavt;continue\n#aavt;char;2;move;dx=100");
        AssertEx.False(withCommand.HasOnlyNonVisualDirectives);

        // Nothing was removed: an unexplained empty capture keeps failing closed.
        EmbeddedAavtExtraction plain = extractor.Extract("正文台词");
        AssertEx.Equal(0, plain.RemovedLineCount);
        AssertEx.False(plain.HasOnlyNonVisualDirectives);

        // A rejected directive is never a tombstone, marker or not.
        EmbeddedAavtExtraction rejected = extractor.Extract(
            "#aavt;continue\n#aavt;continue;extra=1");
        AssertEx.True(rejected.Errors.Count != 0);
        AssertEx.Equal(0, rejected.Commands.Count);
        AssertEx.False(rejected.HasOnlyNonVisualDirectives);

        EmbeddedAavtExtraction rejectedVoice =
            extractor.Extract("#aavt;voice;first\n#aavt;voice;second");
        AssertEx.True(rejectedVoice.Errors.Count != 0);
        AssertEx.False(rejectedVoice.HasOnlyNonVisualDirectives);
    }

    public static void ContaminationScanStillRejectsRawContinueMarkerInPlayback()
    {
        using var files = new TemporaryAapDirectory();
        string path = files.Write(
            "contaminated-continue.aap",
            SyntheticAap.Project(new[] { SyntheticAap.Scene("A") }));
        ProjectSnapshot project = AssertEx.NotNull(new AapProjectReader().Read(path).Value);
        PlaybackArchiveSnapshot playback = PlaybackArchive(
            project,
            new[] { "#na;;1\n#aavt;continue" });

        var result = new EmbeddedProjectCommandCompiler().Compile(
            project,
            playback,
            "0.7.38");

        AssertEx.False(result.Success);
        AssertEx.True(
            (AssertEx.NotNull(result)).Error.Contains(
                "aas-record-contaminated",
                StringComparison.Ordinal),
            result.Error);
    }

    public static void ContinueIdentitySnapshotTracksCurrentProjectAndDeletion()
    {
        using var files = new TemporaryAapDirectory();
        string path = files.Write(
            "continue-snapshot.aap",
            SyntheticAap.Project(new[]
            {
                SyntheticAap.Scene("A"),
                SyntheticAap.Scene("B", additionalPrompt: "#aavt;continue")
            }));
        ProjectSnapshot marked = AssertEx.NotNull(new AapProjectReader().Read(path).Value);
        PlaybackArchiveSnapshot playback = PlaybackArchive(
            marked,
            new[] { (Dialogue: "A", Script: "compiled A"), (Dialogue: "B", Script: "compiled B") });

        var markedResult = new EmbeddedProjectCommandCompiler().Compile(
            marked,
            playback,
            "0.7.42");
        AssertEx.True(markedResult.Success, markedResult.Error);
        EmbeddedProjectCommandCompilation markedCompilation =
            AssertEx.NotNull(markedResult.Value);
        AssertEx.Equal(1, markedCompilation.ContinueIdentities.Count);
        AssertEx.Equal(
            CommandIdentity.CompiledScript("compiled B").Sha256,
            markedCompilation.ContinueIdentities[0]);

        files.Write(
            "continue-snapshot.aap",
            SyntheticAap.Project(new[]
            {
                SyntheticAap.Scene("A"),
                SyntheticAap.Scene("B")
            }));
        ProjectSnapshot deleted = AssertEx.NotNull(new AapProjectReader().Read(path).Value);
        var deletedResult = new EmbeddedProjectCommandCompiler().Compile(
            deleted,
            playback,
            "0.7.42");
        AssertEx.True(deletedResult.Success, deletedResult.Error);
        AssertEx.Equal(
            0,
            AssertEx.NotNull(deletedResult.Value).ContinueIdentities.Count);
    }

    public static void ContinueIdentitySnapshotRejectsSharedMarkedAndUnmarkedScript()
    {
        using var files = new TemporaryAapDirectory();
        string path = files.Write(
            "continue-conflict.aap",
            SyntheticAap.Project(new[]
            {
                SyntheticAap.Scene("A"),
                SyntheticAap.Scene("B", additionalPrompt: "#aavt;continue")
            }));
        ProjectSnapshot project = AssertEx.NotNull(new AapProjectReader().Read(path).Value);
        PlaybackArchiveSnapshot playback = PlaybackArchive(
            project,
            new[] { (Dialogue: "A", Script: "same compiled script"), (Dialogue: "B", Script: "same compiled script") });

        var result = new EmbeddedProjectCommandCompiler().Compile(
            project,
            playback,
            "0.7.42");

        AssertEx.False(result.Success);
        AssertEx.True(
            result.Error.Contains("shared by continue and non-continue", StringComparison.Ordinal),
            result.Error);
    }

    private static PlaybackArchiveSnapshot PlaybackArchive(
        ProjectSnapshot project,
        IReadOnlyList<string> compiledScripts)
    {
        string fullPath = Path.ChangeExtension(project.Source.FullPath, ".aas");
        var source = new PlaybackArchiveSourceSnapshot(
            fullPath,
            new string('B', 64),
            new string('C', 64),
            compiledScripts.Count,
            project.Source.LastWriteTimeUtc + TimeSpan.FromSeconds(1));
        PlaybackRecordSnapshot[] records = compiledScripts
            .Select((script, index) => Record(index, script))
            .ToArray();
        return new PlaybackArchiveSnapshot(
            source,
            "synthetic/v1",
            Array.AsReadOnly(records));
    }

    private static PlaybackArchiveSnapshot PlaybackArchive(
        ProjectSnapshot project,
        IReadOnlyList<(string Dialogue, string Script)> records)
    {
        string fullPath = Path.ChangeExtension(project.Source.FullPath, ".aas");
        var source = new PlaybackArchiveSourceSnapshot(
            fullPath,
            new string('B', 64),
            new string('C', 64),
            records.Count,
            project.Source.LastWriteTimeUtc + TimeSpan.FromSeconds(1));
        PlaybackRecordSnapshot[] snapshots = records
            .Select((record, index) => Record(index, record.Script, record.Dialogue))
            .ToArray();
        return new PlaybackArchiveSnapshot(
            source,
            "synthetic/v1",
            Array.AsReadOnly(snapshots));
    }

    private static PlaybackRecordSnapshot Record(
        int index,
        string compiledScript,
        string textJp = "") => new(
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
        textJp,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        false,
        index.ToString("X64"));

    public static void CompilesProjectPromptsAndOverridesSameSlotSidecar()
    {
        using var files = new TemporaryAapDirectory();
        string projectPath = files.Write(
            "embedded.aap",
            SyntheticAap.Project(new[]
            {
                SyntheticAap.Scene(
                    "A",
                    additionalPrompt:
                        "#wait;20\n#aavt;char;3;move;dx=500;duration=800;easing=easeInOut")
            }));
        var projectRead = new AapProjectReader().Read(projectPath);
        AssertEx.True(projectRead.Success, projectRead.Error);
        ProjectSnapshot project = AssertEx.NotNull(projectRead.Value);
        PlaybackArchiveSnapshot playback = Playback(project, "A", "compiled A\n#wait;20");

        var embeddedResult = new EmbeddedProjectCommandCompiler().Compile(
            project,
            playback,
            "0.7.34");
        AssertEx.True(embeddedResult.Success, embeddedResult.Error);
        EmbeddedProjectCommandCompilation embedded = AssertEx.NotNull(embeddedResult.Value);
        AssertEx.Equal(1, embedded.SceneCount);
        AssertEx.Equal(1, embedded.CommandCount);

        PlaybackCommandBatch batch = embedded.Projection.Batches.Single();
        PlaybackCommandInstruction sidecarCommand = batch.Commands[0] with
        {
            CommandId = Guid.NewGuid().ToString("D"),
            CanonicalDirective = "#char;3;move;dx=100;duration=0;easing=linear"
        };
        PlaybackCommandProjection sidecar = embedded.Projection with
        {
            WorkspaceId = Guid.NewGuid().ToString("D"),
            SourceTimelineSha256 = new string('D', 64),
            Batches = Array.AsReadOnly(new[]
            {
                batch with
                {
                    Commands = Array.AsReadOnly(new[] { sidecarCommand })
                }
            })
        };

        var composedResult = new PlaybackCommandProjectionComposer().Compose(
            sidecar,
            embedded.Projection);
        AssertEx.True(composedResult.Success, composedResult.Error);
        PlaybackCommandProjection composed = AssertEx.NotNull(composedResult.Value);
        AssertEx.Equal(sidecar.WorkspaceId, composed.WorkspaceId);
        AssertEx.Equal(1, composed.Batches.Count);
        AssertEx.Equal(1, composed.Batches[0].Commands.Count);
        AssertEx.Equal(
            "#char;3;move;dx=500;duration=800;easing=easeInOut",
            composed.Batches[0].Commands[0].CanonicalDirective);

        var bound = new PlaybackCommandBinder().Bind(playback, composed);
        AssertEx.True(bound.Success, bound.Error);
    }

    public static void EmbeddedPreviewGuardRejectsStaleRecordAndSelection()
    {
        PlaybackArchiveSnapshot playback = Playback(
            SyntheticProject(),
            "A",
            "compiled A");
        PlaybackCommandBatch batch = Batch(playback, recordIndex: 0);
        var exactSelection = new EmbeddedPreviewSelectionBinding(
            12,
            true,
            true,
            0,
            batch.Scene.NodeGuid,
            batch.Scene.SceneIndex,
            batch.Scene.Fingerprint);
        var guard = new EmbeddedPreviewCommandGuard();

        AssertEx.True(
            guard.Validate(batch, batch.CompiledScript, 12, exactSelection).Success);

        CompiledScriptIdentity staleEditorScript =
            CommandIdentity.CompiledScript("different editor scene");
        AssertEx.False(
            guard.Validate(batch, staleEditorScript, 12, exactSelection).Success);

        EmbeddedPreviewSelectionBinding staleObservation = exactSelection with
        {
            ObservationSequence = 11
        };
        AssertEx.False(
            guard.Validate(batch, batch.CompiledScript, 12, staleObservation).Success);

        EmbeddedPreviewSelectionBinding staleSelection = exactSelection with
        {
            PlaybackRecordIndex = 6
        };
        AssertEx.False(
            guard.Validate(batch, batch.CompiledScript, 12, staleSelection).Success);
    }

    public static void RejectsPlaybackContainingRawEmbeddedDirective()
    {
        ProjectSnapshot project = SyntheticProject();
        PlaybackArchiveSnapshot contaminated = Playback(
            project,
            "A",
            "compiled A\n#aavt;char;3;move;dx=500");

        var result = new EmbeddedProjectCommandCompiler().Compile(
            project,
            contaminated,
            "0.7.34");

        AssertEx.False(result.Success);
        AssertEx.True(result.Error.Contains("Compiled AAS record 0"));
        AssertEx.True(result.Error.Contains("Recompile"));
    }

    private static PlaybackArchiveSnapshot Playback(
        ProjectSnapshot project,
        string dialogue,
        string compiledScript)
    {
        var source = new PlaybackArchiveSourceSnapshot(
            Path.ChangeExtension(project.Source.FullPath, ".aas"),
            new string('B', 64),
            new string('C', 64),
            1,
            project.Source.LastWriteTimeUtc + TimeSpan.FromSeconds(1));
        var record = new PlaybackRecordSnapshot(
            0,
            0,
            0,
            0,
            string.Empty,
            0,
            0,
            0,
            string.Empty,
            compiledScript,
            dialogue,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            false,
            new string('E', 64));
        return new PlaybackArchiveSnapshot(
            source,
            "synthetic/v1",
            Array.AsReadOnly(new[] { record }));
    }

    private static ProjectSnapshot SyntheticProject()
    {
        using var files = new TemporaryAapDirectory();
        string path = files.Write(
            "preview-guard.aap",
            SyntheticAap.Project(new[] { SyntheticAap.Scene("A") }));
        var read = new AapProjectReader().Read(path);
        AssertEx.True(read.Success, read.Error);
        return AssertEx.NotNull(read.Value);
    }

    private static PlaybackCommandBatch Batch(
        PlaybackArchiveSnapshot playback,
        int recordIndex)
    {
        var scene = new SceneKey(
            Guid.NewGuid().ToString("D"),
            6,
            new string('F', 64));
        var command = new PlaybackCommandInstruction(
            Guid.NewGuid().ToString("D"),
            0,
            CommandTimelinePhase.SceneEnter,
            CharacterTransformCommandFamilyCompiler.CommandTypeId,
            CharacterTransformCommandFamilyCompiler.CapabilityId,
            "#char;3;move;dx=500;duration=800;easing=easeInOut");
        return new PlaybackCommandBatch(
            scene,
            recordIndex,
            playback.Records[0].Fingerprint,
            CommandIdentity.CompiledScript(playback.Records[0].CompiledScript),
            Array.AsReadOnly(new[] { command }));
    }
}
