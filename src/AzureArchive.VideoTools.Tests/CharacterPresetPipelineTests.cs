using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Mapping;
using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Core.Workspaces;

namespace AzureArchive.VideoTools.Tests;

internal static class CharacterPresetPipelineTests
{
    private const string NodeGuid = "11111111-2222-3333-4444-555555555555";

    public static void ExtractsSameSlotPresetTransformAndSpineSeparately()
    {
        const string source = "#wait;100\r\n#aavt;fx;3;sway\r\n#aavt;char;3;set;rotation=25\n#aavt;spine;3;Eye_Blink\n#hidemenu";
        var extractor = new EmbeddedAavtDirectiveExtractor();
        EmbeddedAavtExtraction result = extractor.Extract(source);
        AssertEx.Equal(0, result.Errors.Count);
        AssertEx.Equal(3, result.Commands.Count);
        AssertEx.Equal("#wait;100\r\n#hidemenu", result.SanitizedText);
        AssertEx.Equal(3, result.Commands.Select(CommandResourceIdentity.KeyFor).Distinct().Count());
        AssertEx.Equal("preset:3", CommandResourceIdentity.KeyFor(result.Commands[0]));
        AssertEx.Equal("character:3", CommandResourceIdentity.KeyFor(result.Commands[1]));
        AssertEx.Equal("spine:3:20", CommandResourceIdentity.KeyFor(result.Commands[2]));

        EmbeddedAavtExtraction duplicate = extractor.Extract(source + "\n#aavt;fx;3;spin");
        AssertEx.Equal(1, duplicate.Errors.Count);
        AssertEx.True(duplicate.Errors[0].Contains("preset", StringComparison.Ordinal));
        foreach (string invalid in new[] { "#aavt;fx;3;clear", "#aavt;fx;3;sway;frequency=NaN", "#aavt;fx-ish;3;sway" })
        {
            EmbeddedAavtExtraction rejected = extractor.Extract(invalid);
            AssertEx.Equal(string.Empty, rejected.SanitizedText);
            AssertEx.Equal(1, rejected.Errors.Count);
            AssertEx.Equal(0, rejected.Commands.Count);
        }
    }

    public static void UpsertReplacesOnlyTheMatchingSameSlotResource()
    {
        var composer = new EditorCommandDocumentComposer();
        const string character = "  #aavt;char;3;set;rotation=25  \r\n";
        const string source = "#wait;100\r\n" + character + "#aavt;fx;3;sway\r\n#aavt;spine;3;Eye_Blink";
        var result = composer.PreviewUpsert(source, EditorCommandDocumentComposer.Revision(source), "#aavt;fx;3;squash;frequency=4");
        AssertEx.True(result.Success, result.Error);
        EditorCommandEditPreview edit = AssertEx.NotNull(result.Value);
        AssertEx.True(edit.ReplacedExisting);
        AssertEx.True(edit.UpdatedText.Contains(character, StringComparison.Ordinal));
        AssertEx.True(edit.UpdatedText.EndsWith("#aavt;spine;3;Eye_Blink", StringComparison.Ordinal));
        AssertEx.Equal(3, AssertEx.NotNull(composer.Read(edit.UpdatedText).Value).Commands.Count);
        AssertEx.False(edit.UpdatedText.Contains(";sway", StringComparison.Ordinal));

        EditorCommandEditPreview characterEdit = AssertEx.NotNull(composer.PreviewUpsert(
            edit.UpdatedText, edit.ResultRevisionSha256, "#aavt;char;3;set;x=50").Value);
        AssertEx.True(characterEdit.UpdatedText.Contains(edit.CanonicalPublicDirective, StringComparison.Ordinal));
        AssertEx.Equal(3, AssertEx.NotNull(composer.Read(characterEdit.UpdatedText).Value).Commands.Count);
    }

    public static void RemoveHonorsRevisionAndPreservesOtherFamiliesByteForByte()
    {
        var composer = new EditorCommandDocumentComposer();
        const string before = "#wait;100\r\n  #aavt;char;3;set;x=35  \n";
        const string after = "#aavt;spine;3;Eye_Blink\r\n#aavt;continue";
        string source = before + "#aavt;fx;3;headbutt;direction=left\r\n" + after;
        string revision = EditorCommandDocumentComposer.Revision(source);
        AssertEx.False(composer.PreviewRemove(source, new string('A', 64), "#aavt;fx;3;sway").Success);
        AssertEx.False(composer.PreviewRemove(source, revision, "#aavt;fx;3;clear").Success);
        EditorCommandEditPreview removed = AssertEx.NotNull(composer.PreviewRemove(source, revision, "#aavt;fx;3;sway").Value);
        AssertEx.True(removed.ReplacedExisting);
        AssertEx.Equal(before + after, removed.UpdatedText);
        AssertEx.Equal(revision, removed.ExpectedRevisionSha256);
        AssertEx.Equal(EditorCommandDocumentComposer.Revision(removed.UpdatedText), removed.ResultRevisionSha256);
        EditorCommandEditPreview again = AssertEx.NotNull(composer.PreviewRemove(
            removed.UpdatedText, removed.ResultRevisionSha256, "#aavt;fx;3;squash").Value);
        AssertEx.False(again.ReplacedExisting);
        AssertEx.Equal(removed.UpdatedText, again.UpdatedText);
        AssertEx.Equal(removed.ResultRevisionSha256, again.ResultRevisionSha256);
        EditorCommandEditPreview charRemoved = AssertEx.NotNull(composer.PreviewRemove(source, revision, "#aavt;char;3;reset").Value);
        AssertEx.True(charRemoved.UpdatedText.Contains("#aavt;fx;3;headbutt;direction=left\r\n", StringComparison.Ordinal));
        AssertEx.False(charRemoved.UpdatedText.Contains("#aavt;char", StringComparison.Ordinal));
    }

    public static void PreviewLeasesSeparatePresetDeletionAndExplicitTombstones()
    {
        string[] directives = Extract("#aavt;char;3;set;rotation=20\n#aavt;fx;3;squash");
        var gate = new EditorPreviewLeaseGate();
        var selection = Selection(1);
        var live = Live(selection);
        var candidate = new EditorPreviewLeaseCandidate(selection, directives, false);
        EditorPreviewLeaseAuthorization initial = AssertEx.NotNull(gate.TryAuthorize(candidate, live).Value);
        AssertEx.Equal(2, initial.Commands.Count);
        AssertEx.Equal(EditorPreviewResourceKind.CharacterSlot, initial.Commands[0].Resource.Kind);
        AssertEx.Equal(EditorPreviewResourceKind.CharacterPreset, initial.Commands[1].Resource.Kind);
        AssertEx.Equal(EditorPreviewResourceFields.CharacterPresetOverlay, initial.Commands[1].Fields);
        AssertEx.True(initial.Commands[1].DispatchImmediately);
        AssertEx.False(gate.TryAuthorize(candidate, live).Success);

        var secondSelection = Selection(2);
        EditorPreviewLeaseAuthorization noPreset = AssertEx.NotNull(gate.TryAuthorize(
            new EditorPreviewLeaseCandidate(secondSelection, new[] { directives[0] }, false), Live(secondSelection)).Value);
        EditorPreviewResourceCleanupPlan cleanup = AssertEx.NotNull(new EditorPreviewResourceCleanupPlanner()
            .Plan(initial.Footprint, noPreset.Footprint).Value);
        EditorPreviewResourceTransition preset = cleanup.Resources.Single(resource => resource.Resource.Kind == EditorPreviewResourceKind.CharacterPreset);
        AssertEx.True(preset.CommandRemoved && preset.RestorePreviousImmediate);
        AssertEx.Equal(EditorPreviewResourceFields.CharacterPresetOverlay, preset.RemovedFields);
        AssertEx.False(cleanup.Resources.Single(resource => resource.Resource.Kind == EditorPreviewResourceKind.CharacterSlot).CommandRemoved);

        var thirdSelection = Selection(3);
        EditorPreviewLeaseAuthorization empty = AssertEx.NotNull(gate.TryAuthorize(
            new EditorPreviewLeaseCandidate(thirdSelection, Array.Empty<string>(), true), Live(thirdSelection)).Value);
        EditorPreviewResourceCleanupPlan allRemoved = AssertEx.NotNull(new EditorPreviewResourceCleanupPlanner()
            .Plan(initial.Footprint, empty.Footprint).Value);
        AssertEx.True(allRemoved.Resources.All(resource => resource.CommandRemoved));
        AssertEx.False(new EditorPreviewLeaseGate().TryAuthorize(
            new EditorPreviewLeaseCandidate(selection, Array.Empty<string>(), false), live).Success);
        AssertEx.False(new EditorPreviewLeaseGate().TryAuthorize(
            new EditorPreviewLeaseCandidate(selection, new[] { directives[1], directives[1] }, false), live).Success);
        AssertEx.False(new EditorPreviewLeaseGate().TryAuthorize(candidate,
            live with { RuntimeContext = PlayerRuntimeContextSnapshot.FromPreviewMode(false) }).Success);

        EditorPreviewResourceFootprint invalid = initial.Footprint with
        {
            Entries = new[] { initial.Footprint.Entries[1] with
                { Resource = new EditorPreviewResourceKey(EditorPreviewResourceKind.CharacterPreset, 3, 20) } }
        };
        AssertEx.False(new EditorPreviewResourceCleanupPlanner().Plan(null, invalid).Success);
    }

    public static void AllTwentyOneResourcesCompileBindDispatchAndStayOutOfPresetInheritance()
    {
        var lines = new List<string> { "#aavt;camera;set;zoom=1.2" };
        for (int slot = 1; slot <= 5; slot++)
        {
            lines.Add($"#aavt;fx;{slot};sway");
            lines.Add($"#aavt;char;{slot};set;rotation={slot * 5}");
        }
        for (int track = 20; track <= 29; track++) lines.Add($"#aavt;spine;1;Part_{track};track={track}");
        var (project, playback) = Pair(string.Join('\n', lines), "#aavt;fx;2;spin;cycles=0");
        EmbeddedProjectCommandCompilation compilation = Compile(project, playback);
        AssertEx.Equal(22, compilation.CommandCount);
        AssertEx.Equal(2, compilation.SceneCount);
        AssertEx.Equal(21, compilation.Projection.Batches[0].Commands.Count);
        IPlaybackCommandIndex index = AssertEx.NotNull(new PlaybackCommandBinder().Bind(playback, compilation.Projection).Value);
        AssertEx.Equal(2, index.BatchCount);
        var dispatch = new PlaybackSceneDispatchGate();
        for (int row = 0; row < 2; row++)
        {
            PlaybackCommandBatch expected = compilation.Projection.Batches[row];
            var identity = expected.CompiledScript;
            PlaybackCommandObservation observed = AssertEx.NotNull(index.Observe(new ObservedCompiledSceneIdentity(
                identity.Sha256, identity.Utf16Length, identity.LineCount) { PlaybackRowIndex = row }).Value);
            AssertEx.Equal(PlaybackCommandObservationStatus.CommandBatch, observed.Status);
            PlaybackCommandBatch actual = AssertEx.NotNull(observed.Batch);
            AssertEx.Equal(row, actual.PlaybackRecordIndex);
            for (int order = 0; order < actual.Commands.Count; order++) AssertEx.Equal(order, actual.Commands[order].Order);
            AssertEx.True(dispatch.TryAuthorize(row + 1, actual).Success);
        }
        var preview = new EditorPreviewLeaseGate().TryAuthorize(new EditorPreviewLeaseCandidate(
            Selection(1), Extract(string.Join('\n', lines)), false), Live(Selection(1)));
        AssertEx.True(preview.Success, preview.Error);
        PreviewChainDirectiveIndex chain = PreviewChainDirectiveIndex.Build(project, true);
        AssertEx.Equal(5, chain.DirectiveCount);
        AssertEx.Equal(10, chain.SpineOverlayCount);
        AssertEx.Equal(1, chain.CameraSceneCount);
        AssertEx.True(chain.TryGet(NodeGuid, 0, 2, out string inherited));
        AssertEx.True(inherited.StartsWith("#char;", StringComparison.Ordinal));
        AssertEx.False(chain.TryGet(NodeGuid, 1, 2, out _), "A transient preset must not become an ancestor transform.");

        var composed = new PlaybackCommandProjectionComposer().Compose(compilation.Projection, compilation.Projection);
        AssertEx.True(composed.Success, composed.Error);
        AssertEx.Equal(21, AssertEx.NotNull(composed.Value).Batches[0].Commands.Count);
        AssertEx.True(new PlaybackCommandBinder().Bind(playback, composed.Value).Success);

        // The ten-track scene budget is the same in authoring, preview, saved
        // binding and playback admission, even when no character commands exist.
        string[] tooManyOverlays = Enumerable.Range(20, 10).Select(track => $"#aavt;spine;1;Part_{track};track={track}")
            .Append("#aavt;spine;2;Extra;track=20").ToArray();
        AssertEx.Equal(1, new EmbeddedAavtDirectiveExtractor().Extract(string.Join('\n', tooManyOverlays)).Errors.Count);
        string[] canonicalOverlays = tooManyOverlays.Select(line => Extract(line).Single()).ToArray();
        AssertEx.False(new EditorPreviewLeaseGate().TryAuthorize(new EditorPreviewLeaseCandidate(
            Selection(1), canonicalOverlays, false), Live(Selection(1))).Success);
        PlaybackCommandBatch overBudget = compilation.Projection.Batches[0] with
        {
            Commands = canonicalOverlays.Select((directive, order) => new PlaybackCommandInstruction(
                Guid.NewGuid().ToString("D"), order, CommandTimelinePhase.SceneEnter,
                SpineOverlayCommandFamilyCompiler.CommandTypeId, SpineOverlayCommandFamilyCompiler.CapabilityId,
                directive)).ToArray()
        };
        AssertEx.False(new PlaybackSceneDispatchGate().TryAuthorize(1, overBudget).Success);
        AssertEx.False(new PlaybackCommandBinder().Bind(playback,
            compilation.Projection with { Batches = new[] { overBudget } }).Success);
    }

    public static void SidecarCompositionOverridesOnlyTheSamePresetKey()
    {
        var (project, playback) = Pair("#aavt;char;3;set;rotation=20\n#aavt;fx;3;sway\n#aavt;fx;2;headbutt");
        PlaybackCommandProjection original = Compile(project, playback).Projection;
        string spin = Extract("#aavt;fx;3;spin").Single();
        PlaybackCommandInstruction replacement = original.Batches[0].Commands[1] with
        {
            Order = 0, CommandId = Guid.NewGuid().ToString("D"), CanonicalDirective = spin
        };
        PlaybackCommandProjection embedded = original with
        {
            SourceTimelineSha256 = new string('9', 64),
            Batches = new[] { original.Batches[0] with { Commands = new[] { replacement } } }
        };
        var result = new PlaybackCommandProjectionComposer().Compose(original, embedded);
        AssertEx.True(result.Success, result.Error);
        PlaybackCommandProjection merged = AssertEx.NotNull(result.Value);
        IReadOnlyList<PlaybackCommandInstruction> commands = merged.Batches[0].Commands;
        AssertEx.Equal(3, commands.Count);
        AssertEx.Equal(original.Batches[0].Commands[0], commands[0]);
        AssertEx.Equal(spin, commands[1].CanonicalDirective);
        AssertEx.Equal(original.Batches[0].Commands[2], commands[2]);
        AssertEx.True(new PlaybackCommandBinder().Bind(playback, merged).Success);
        AssertEx.True(new PlaybackSceneDispatchGate().TryAuthorize(1, merged.Batches[0]).Success);
    }

    public static void ForeignPresetRoutesCannotLeakIntoSavedProjection()
    {
        const string prompt = "#aavt;fx;3;sway\n#aavt;char;3;set;rotation=20";
        var (project, playback) = Pair(prompt);
        using var service = new Rukari.Lib.Commands.EmbeddedDirectiveService(static () => true, static () => true, null);
        AssertEx.True(service.Register("rukari.moreeffects", new[] { "#aavt" }, static _ => { }).Success);
        AssertEx.True(service.Register("foreign", new[] { "#aavt;fx" }, static _ => { }).Success);
        var sanitizer = service.CaptureSanitizer();
        string Filter(string text) => sanitizer.SanitizeExceptOwners(text, new[] { "rukari.moreeffects" });
        var result = new EmbeddedProjectCommandCompiler(Filter).Compile(project, playback, "1.2.0");
        AssertEx.True(result.Success, result.Error);
        AssertEx.Equal(1, AssertEx.NotNull(result.Value).CommandCount);
        AssertEx.Equal(CharacterTransformCommandFamilyCompiler.CommandTypeId,
            AssertEx.NotNull(result.Value).Projection.Batches[0].Commands[0].CommandType);
        AssertEx.Equal(prompt, project.ScriptNodes.Single().Scenes.Single().AdditionalPrompt);
    }

    public static void CapabilitiesCanonicalTextAndDuplicatePresetKeysFailClosed()
    {
        var (project, playback) = Pair("#aavt;fx;3;sway\n#aavt;char;3;set;rotation=20");
        EmbeddedProjectCommandCompilation compilation = Compile(project, playback);
        PlaybackCommandBatch batch = compilation.Projection.Batches[0];
        PlaybackCommandInstruction fx = batch.Commands[0];
        PlaybackCommandInstruction[] bad =
        {
            fx with { RequiredCapability = CharacterTransformCommandFamilyCompiler.CapabilityId },
            fx with { CanonicalDirective = "#fx;3;sway" },
            fx with { CanonicalDirective = Extract("#aavt;fx;3;sway").Single().Replace("amplitude=12", "amplitude=NaN") },
            fx with { CommandType = CharacterTransformCommandFamilyCompiler.CommandTypeId }
        };
        foreach (PlaybackCommandInstruction command in bad)
        {
            PlaybackCommandBatch malformed = batch with { Commands = new[] { command } };
            AssertEx.False(new PlaybackCommandBinder().Bind(playback,
                compilation.Projection with { Batches = new[] { malformed } }).Success);
            AssertEx.False(new PlaybackSceneDispatchGate().TryAuthorize(1, malformed).Success);
        }
        PlaybackCommandBatch duplicate = batch with { Commands = new[]
        {
            fx, fx with { Order = 1, CommandId = Guid.NewGuid().ToString("D"), CanonicalDirective = Extract("#aavt;fx;3;spin").Single() }
        } };
        AssertEx.False(new PlaybackCommandBinder().Bind(playback,
            compilation.Projection with { Batches = new[] { duplicate } }).Success);
        AssertEx.False(new PlaybackSceneDispatchGate().TryAuthorize(1, duplicate).Success);

        var workspace = new ModWorkspaceManifest(ModWorkspaceManifest.CurrentSchemaVersion,
            compilation.Timeline.WorkspaceId, "1.2.0", "1.2.0", new ModWorkspaceSourceBinding(
                project.Source.PathKey, project.Source.RevisionSha256, playback.Source.PathKey,
                playback.Source.RevisionSha256, playback.SchemaName),
            new[] { CharacterTransformCommandFamilyCompiler.CapabilityId });
        var compiler = new CommandTimelineCompiler();
        var missing = compiler.Compile(project, playback, workspace, compilation.Timeline);
        AssertEx.False(missing.Success);
        AssertEx.True(missing.Error.Contains(CharacterPresetCommandFamilyCompiler.CapabilityId, StringComparison.Ordinal));
        AssertEx.True(compiler.Compile(project, playback, workspace with
        {
            RequiredCapabilities = new[] { CharacterTransformCommandFamilyCompiler.CapabilityId, CharacterPresetCommandFamilyCompiler.CapabilityId }
        }, compilation.Timeline).Success);
    }

    public static void RawPresetLinesInPlaybackRemainContaminationErrors()
    {
        var (project, playback) = Pair("#aavt;fx;3;sway");
        PlaybackArchiveSnapshot contaminated = playback with
        {
            Records = new[] { playback.Records[0] with { CompiledScript = "compiled\n#aavt;fx;3;sway" } }
        };
        var result = new EmbeddedProjectCommandCompiler().Compile(project, contaminated, "1.2.0");
        AssertEx.False(result.Success);
        AssertEx.True(result.Error.Contains("aas-record-contaminated", StringComparison.Ordinal));
    }

    private static string[] Extract(string publicDirectives)
    {
        var extraction = new EmbeddedAavtDirectiveExtractor().Extract(publicDirectives);
        AssertEx.Equal(0, extraction.Errors.Count, string.Join(" | ", extraction.Errors));
        return extraction.Commands.Select(command => command.CanonicalDirective).ToArray();
    }

    private static EditorPreviewSelectionBinding Selection(long generation) => new(
        generation, 1, 1, CommandIdentity.CompiledScript("compiled preview"),
        new EditorPreviewSceneAddress(new string('A', 24), NodeGuid, 0, new string('B', 24)));

    private static EditorPreviewLiveObservation Live(EditorPreviewSelectionBinding selection) => new(
        PlayerRuntimeContextSnapshot.FromPreviewMode(true), selection, 1);

    private static EmbeddedProjectCommandCompilation Compile(ProjectSnapshot project, PlaybackArchiveSnapshot playback)
    {
        var result = new EmbeddedProjectCommandCompiler().Compile(project, playback, "1.2.0");
        AssertEx.True(result.Success, result.Error);
        return AssertEx.NotNull(result.Value);
    }

    // Entirely in-memory snapshots: these tests never read or write an author project.
    private static (ProjectSnapshot Project, PlaybackArchiveSnapshot Playback) Pair(params string[] prompts)
    {
        var now = DateTimeOffset.Parse("2026-09-29T00:00:00Z");
        SceneSnapshot[] scenes = prompts.Select((prompt, index) => new SceneSnapshot(
            new SceneKey(NodeGuid, index, new string((char)('A' + index), 64)),
            "synthetic", "dialogue " + index, true, string.Empty, 0, 0, prompt,
            string.Empty, string.Empty, string.Empty, string.Empty, 0, 0, 0)).ToArray();
        var node = new StoryNodeSnapshot(0, StoryNodeKind.Script, "synthetic", NodeGuid, true,
            "presets", Array.Empty<string>(), scenes);
        var project = new ProjectSnapshot(
            new ProjectSourceSnapshot(@"C:\synthetic\presets.aap", new string('1', 64), new string('2', 64), 1, now),
            "synthetic", "presets", new ProjectPreviewSnapshot(null, string.Empty, string.Empty),
            new[] { node }, Array.Empty<ProjectDiagnostic>());
        PlaybackRecordSnapshot[] records = scenes.Select((scene, index) => new PlaybackRecordSnapshot(
            index, 0, 0, 0, string.Empty, 0, 0, 0, string.Empty, "compiled " + scene.DialogueText,
            scene.DialogueText, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
            false, new string((char)('C' + index), 64))).ToArray();
        var playback = new PlaybackArchiveSnapshot(
            new PlaybackArchiveSourceSnapshot(@"C:\synthetic\presets.aas", new string('3', 64), new string('4', 64), 1, now.AddSeconds(1)),
            "synthetic/v1", records);
        return (project, playback);
    }
}
