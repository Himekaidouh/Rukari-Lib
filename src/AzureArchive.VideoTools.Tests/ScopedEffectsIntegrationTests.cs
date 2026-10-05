using AzureArchive.VideoTools.Core.Cameras;
using AzureArchive.VideoTools.Core.Characters;
using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Core.VisualEditor;

namespace AzureArchive.VideoTools.Tests;

internal static class ScopedEffectsIntegrationTests
{
    private const string NodeGuid = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string OverallLine = "  #aavt;camera;set;x=10;zoom=1.2  \r\n";
    private const string BackgroundLine = "#aavt;camera;set;scope=background;x=20;zoom=1.5\r\n";
    private const string OtherLines = "#aavt;char;3;set;rotation=5\r\n#bgshake";
    private const string BothScopes = "#wait;125\r\n" + OverallLine + BackgroundLine + OtherLines;

    public static void ComposerScopeUpsertsPreserveEachOtherAndOfficialText()
    {
        var composer = new EditorCommandDocumentComposer();
        string originalRevision = EditorCommandDocumentComposer.Revision(BothScopes);
        var backgroundResult = composer.PreviewUpsert(BothScopes, originalRevision,
            "#aavt;camera;set;scope=background;x=40;zoom=2;duration=600;easing=easeOut");
        AssertEx.True(backgroundResult.Success, backgroundResult.Error);
        EditorCommandEditPreview background = AssertEx.NotNull(backgroundResult.Value);
        AssertEx.Equal(6, background.PublicSlot);
        AssertEx.True(background.ReplacedExisting);
        AssertEx.Equal("#wait;125\r\n" + OverallLine + background.CanonicalPublicDirective + "\r\n" + OtherLines,
            background.UpdatedText);

        var overallResult = composer.PreviewUpsert(background.UpdatedText, background.ResultRevisionSha256,
            "#aavt;camera;set;scope=overall;y=-60;zoom=1.25;duration=300;easing=linear");
        AssertEx.True(overallResult.Success, overallResult.Error);
        EditorCommandEditPreview overall = AssertEx.NotNull(overallResult.Value);
        AssertEx.Equal(0, overall.PublicSlot);
        AssertEx.True(overall.ReplacedExisting);
        AssertEx.Equal("#wait;125\r\n" + overall.CanonicalPublicDirective + "\r\n"
            + background.CanonicalPublicDirective + "\r\n" + OtherLines, overall.UpdatedText);
        EditorCommandDocument document = AssertEx.NotNull(composer.Read(overall.UpdatedText).Value);
        AssertEx.Equal(3, document.Commands.Count);
        AssertEx.Equal(EditorCommandDocumentComposer.Revision(overall.UpdatedText), overall.ResultRevisionSha256);
        AssertEx.False(composer.PreviewUpsert(overall.UpdatedText, originalRevision,
            "#aavt;camera;reset;scope=background").Success,
            "A draft's old revision cannot acquire authority from the other scope's later save.");
    }

    public static void ComposerScopeRemovalPreservesTheOtherScopeByteForByte()
    {
        var composer = new EditorCommandDocumentComposer();
        string revision = EditorCommandDocumentComposer.Revision(BothScopes);
        EditorCommandEditPreview backgroundRemoved = AssertEx.NotNull(composer.PreviewRemove(
            BothScopes, revision, "#aavt;camera;reset;scope=background").Value);
        AssertEx.Equal("#wait;125\r\n" + OverallLine + OtherLines, backgroundRemoved.UpdatedText);
        AssertEx.Equal(6, backgroundRemoved.PublicSlot);
        AssertEx.True(backgroundRemoved.ReplacedExisting);

        EditorCommandEditPreview overallRemoved = AssertEx.NotNull(composer.PreviewRemove(
            BothScopes, revision, "#aavt;camera;reset").Value);
        AssertEx.Equal("#wait;125\r\n" + BackgroundLine + OtherLines, overallRemoved.UpdatedText);
        AssertEx.Equal(0, overallRemoved.PublicSlot);
        AssertEx.True(overallRemoved.ReplacedExisting);
        AssertEx.Equal(2, Extract(overallRemoved.UpdatedText).Length);
    }

    public static void DuplicateScopeResourcesAreRejectedByComposerExtractorAndLease()
    {
        var composer = new EditorCommandDocumentComposer();
        var extractor = new EmbeddedAavtDirectiveExtractor();
        var compiler = new SceneCameraCommandFamilyCompiler();
        foreach (string scope in new[] { "overall", "background" })
        {
            string first = scope == "overall" ? "#aavt;camera;set;zoom=1.2"
                : "#aavt;camera;set;scope=background;zoom=1.2";
            string second = $"#aavt;camera;set;scope={scope};zoom=1.5";
            string invalid = first + "\n" + second;
            AssertEx.True(extractor.Extract(invalid).Errors.Count > 0, "Two commands of one scope must conflict.");
            AssertEx.False(composer.Read(invalid).Success);
            AssertEx.False(composer.PreviewUpsert(invalid, EditorCommandDocumentComposer.Revision(invalid),
                "#aavt;char;3;set;rotationX=20").Success);
            string[] duplicateCanonical =
            {
                AssertEx.NotNull(compiler.Canonicalize(first.Replace("#aavt;camera", "#camera", StringComparison.Ordinal)).Value).Directive,
                AssertEx.NotNull(compiler.Canonicalize(second.Replace("#aavt;camera", "#camera", StringComparison.Ordinal)).Value).Directive
            };
            EditorPreviewSelectionBinding selection = Selection(1);
            AssertEx.False(new EditorPreviewLeaseGate().TryAuthorizeWindow(
                new(selection, duplicateCanonical, false, 9), Live(selection, 10)).Success);
        }
        AssertEx.Equal(0, extractor.Extract(OverallLine + BackgroundLine).Errors.Count,
            "Different scopes remain separate resources despite sharing the camera command family.");
    }

    public static void DualScopeExtractionAndReplayLeaseUseIndependentZeroAndSixResources()
    {
        string[] directives = Extract(OverallLine + BackgroundLine);
        AssertEx.Equal(2, directives.Length);
        var gate = new EditorPreviewLeaseGate();
        EditorPreviewLeaseAuthorization first = Authorize(gate, 1, 10, directives);
        AssertEx.Equal(2, first.Commands.Count);
        AssertEx.Equal(2, first.Footprint.Entries.Count);
        AssertEx.True(first.Commands.All(command => command.Resource.Kind == EditorPreviewResourceKind.SceneCamera
            && command.DispatchImmediately));
        AssertEx.Equal(0, first.Commands[0].Resource.PublicSlot);
        AssertEx.Equal(6, first.Commands[1].Resource.PublicSlot);
        AssertEx.Equal(CommandResourceIdentity.CameraKey, CommandResourceIdentity.KeyFor(
            new EmbeddedAavtDirectiveExtractor().Extract(OverallLine).Commands.Single()));
        AssertEx.Equal(CommandResourceIdentity.BackgroundCameraKey, CommandResourceIdentity.KeyFor(
            new EmbeddedAavtDirectiveExtractor().Extract(BackgroundLine).Commands.Single()));

        EditorPreviewLeaseAuthorization replay = Authorize(gate, 1, 11, directives);
        AssertEx.Equal(first.StableSceneIdentity, replay.StableSceneIdentity);
        AssertEx.Equal(first.Commands[0].Resource, replay.Commands[0].Resource);
        AssertEx.Equal(first.Commands[1].Resource, replay.Commands[1].Resource);
        EditorPreviewResourceCleanupPlan initial = AssertEx.NotNull(new EditorPreviewResourceCleanupPlanner()
            .Plan(null, replay.Footprint).Value);
        AssertEx.Equal(2, initial.Resources.Count);
        AssertEx.True(initial.Resources.All(resource => resource.ApplyCurrentImmediately
            && !resource.RestorePreviousImmediate && !resource.CommandRemoved));
    }

    public static void ScopeDeletionAndTombstonesKeepCleanupResourcesIndependent()
    {
        var gate = new EditorPreviewLeaseGate();
        var planner = new EditorPreviewResourceCleanupPlanner();
        EditorPreviewLeaseAuthorization both = Authorize(gate, 1, 10, Extract(OverallLine + BackgroundLine));
        EditorPreviewLeaseAuthorization overallOnly = Authorize(gate, 2, 11, Extract(OverallLine));
        EditorPreviewResourceCleanupPlan deletion = AssertEx.NotNull(planner.Plan(both.Footprint, overallOnly.Footprint).Value);
        AssertEx.Equal(2, deletion.Resources.Count);
        EditorPreviewResourceTransition background = deletion.Resources.Single(resource => resource.Resource.PublicSlot == 6);
        EditorPreviewResourceTransition overall = deletion.Resources.Single(resource => resource.Resource.PublicSlot == 0);
        AssertEx.True(background.CommandRemoved && background.RestorePreviousImmediate && !background.ApplyCurrentImmediately);
        AssertEx.Equal(EditorPreviewResourceFields.CameraPositionX | EditorPreviewResourceFields.CameraZoom,
            background.RemovedFields);
        AssertEx.Equal(EditorPreviewResourceFields.None, background.CurrentFields);
        AssertEx.False(overall.CommandRemoved);
        AssertEx.True(overall.ApplyCurrentImmediately);
        AssertEx.Equal(EditorPreviewResourceFields.None, overall.RemovedFields);

        EditorPreviewLeaseAuthorization empty = Authorize(gate, 3, 12, Array.Empty<string>(), true);
        EditorPreviewResourceCleanupPlan cleared = AssertEx.NotNull(planner.Plan(both.Footprint, empty.Footprint).Value);
        AssertEx.Equal(2, cleared.Resources.Count);
        AssertEx.True(cleared.Resources.All(resource => resource.CommandRemoved && resource.RestorePreviousImmediate
            && !resource.ApplyCurrentImmediately));
        AssertEx.Equal(0, cleared.Resources[0].Resource.PublicSlot);
        AssertEx.Equal(6, cleared.Resources[1].Resource.PublicSlot);
        EditorPreviewResourceCleanupPlan afterDeletion = AssertEx.NotNull(planner.Plan(overallOnly.Footprint, empty.Footprint).Value);
        AssertEx.Equal(1, afterDeletion.Resources.Count);
        AssertEx.Equal(0, afterDeletion.Resources[0].Resource.PublicSlot);
    }

    public static void CharacterVisualDraftPreservesPitchAndZThroughUpsertAndLease()
    {
        var builder = new VisualCharacterDraftBuilder();
        var request = new VisualCharacterDraftRequest(3, true, VisualCharacterDraftOperation.Set,
            null, null, -15f, null, 800, CharacterTransformEasing.EaseOut) { RotationXDegrees = 120.5f };
        string directive = AssertEx.NotNull(builder.Build(request).Value);
        var composer = new EditorCommandDocumentComposer();
        EditorCommandEditPreview edit = AssertEx.NotNull(composer.PreviewUpsert(
            BothScopes, EditorCommandDocumentComposer.Revision(BothScopes), directive).Value);
        AssertEx.True(edit.ReplacedExisting);
        AssertEx.True(edit.UpdatedText.Contains(OverallLine + BackgroundLine, StringComparison.Ordinal));
        string[] canonical = Extract(edit.UpdatedText);
        string character = canonical.Single(text => text.StartsWith("#char;", StringComparison.Ordinal));
        VisualCharacterDraftRequest read = AssertEx.NotNull(builder.ReadCanonical(character).Value);
        AssertEx.Equal<float?>(120.5f, read.RotationXDegrees);
        AssertEx.Equal<float?>(-15f, read.RotationDegrees);
        AssertEx.Equal(directive, AssertEx.NotNull(builder.Build(read).Value));
        EditorPreviewLeaseAuthorization lease = Authorize(new(), 1, 10, canonical);
        EditorPreviewCommandPlan characterPlan = lease.Commands.Single(command => command.Resource.Kind == EditorPreviewResourceKind.CharacterSlot);
        AssertEx.Equal(EditorPreviewResourceFields.CharacterRotationX | EditorPreviewResourceFields.CharacterScreenRotation,
            characterPlan.Fields);
        AssertEx.Equal(3, lease.Footprint.Entries.Count);
        AssertEx.Equal(2, lease.Commands.Count(command => command.Resource.Kind == EditorPreviewResourceKind.SceneCamera));
    }

    public static void PendingVisualDraftsRoundTripPitchAndZWithoutImmediateDispatch()
    {
        var builder = new VisualCharacterDraftBuilder();
        foreach (VisualCharacterDraftOperation operation in new[] { VisualCharacterDraftOperation.Set, VisualCharacterDraftOperation.Move })
        {
            var request = new VisualCharacterDraftRequest(3, false, operation, null, null,
                -5f, null, 800, CharacterTransformEasing.EaseOut) { RotationXDegrees = 20f };
            string directive = AssertEx.NotNull(builder.Build(request).Value);
            string[] canonical = Extract(directive);
            VisualCharacterDraftRequest read = AssertEx.NotNull(builder.ReadCanonical(canonical.Single()).Value);
            AssertEx.False(read.Occupied);
            AssertEx.Equal(operation, read.Operation);
            AssertEx.Equal<float?>(20f, read.RotationXDegrees);
            AssertEx.Equal<float?>(-5f, read.RotationDegrees);
            AssertEx.Equal(0, read.DurationMilliseconds);
            AssertEx.Equal(CharacterTransformEasing.Linear, read.Easing);
            AssertEx.Equal(directive, AssertEx.NotNull(builder.Build(read).Value));
            EditorPreviewCommandPlan plan = Authorize(new(), 1, 10, canonical).Commands.Single();
            AssertEx.Equal(EditorPreviewDispatchMode.DeferredSlotPending, plan.DispatchMode);
            AssertEx.False(plan.DispatchImmediately);
            AssertEx.Equal(EditorPreviewResourceFields.CharacterRotationX | EditorPreviewResourceFields.CharacterScreenRotation, plan.Fields);
        }
    }

    public static void CharacterResetFootprintIncludesPitchAndDeletionRestoresEveryCharacterField()
    {
        var builder = new VisualCharacterDraftBuilder();
        var gate = new EditorPreviewLeaseGate();
        var planner = new EditorPreviewResourceCleanupPlanner();
        string zeroPitch = AssertEx.NotNull(builder.Build(new(3, true, VisualCharacterDraftOperation.Set,
            null, null, null, null, 0, CharacterTransformEasing.Linear) { RotationXDegrees = 0f }).Value);
        EditorPreviewLeaseAuthorization zero = Authorize(gate, 1, 10, Extract(zeroPitch));
        AssertEx.Equal(EditorPreviewResourceFields.CharacterRotationX, zero.Commands.Single().Fields,
            "Explicit zero is still pitch ownership.");
        string reset = AssertEx.NotNull(builder.Build(new(3, true, VisualCharacterDraftOperation.Reset,
            null, null, null, null, 300, CharacterTransformEasing.EaseInOut)).Value);
        EditorPreviewLeaseAuthorization resetLease = Authorize(gate, 2, 11, Extract(reset));
        const EditorPreviewResourceFields all = EditorPreviewResourceFields.CharacterPositionX
            | EditorPreviewResourceFields.CharacterPositionY | EditorPreviewResourceFields.CharacterScreenRotation
            | EditorPreviewResourceFields.CharacterRotationX | EditorPreviewResourceFields.CharacterHorizontalFlip;
        AssertEx.Equal(all, resetLease.Commands.Single().Fields);
        EditorPreviewResourceTransition zeroToReset = AssertEx.NotNull(planner.Plan(zero.Footprint, resetLease.Footprint).Value).Resources.Single();
        AssertEx.Equal(EditorPreviewResourceFields.None, zeroToReset.RemovedFields);
        EditorPreviewLeaseAuthorization empty = Authorize(gate, 3, 12, Array.Empty<string>(), true);
        EditorPreviewResourceTransition deleted = AssertEx.NotNull(planner.Plan(resetLease.Footprint, empty.Footprint).Value).Resources.Single();
        AssertEx.True(deleted.CommandRemoved && deleted.RestorePreviousImmediate);
        AssertEx.Equal(all, deleted.RemovedFields);
        AssertEx.Equal(EditorPreviewResourceFields.None, deleted.CurrentFields);
    }

    public static void AuthorizedEmptyScenesNeverConsultTheSavedDirectiveLookup()
    {
        int savedCalls = 0;
        string? Saved() { savedCalls++; return "saved"; }
        AssertEx.Equal<string?>(null, PreviewDirectiveAuthority.Resolve(true, null, Saved));
        AssertEx.Equal(string.Empty, PreviewDirectiveAuthority.Resolve(true, string.Empty, Saved));
        AssertEx.Equal("overlay", PreviewDirectiveAuthority.Resolve(true, "overlay", Saved));
        AssertEx.Equal(0, savedCalls);
        AssertEx.Equal("saved", PreviewDirectiveAuthority.Resolve(false, "untrusted overlay", Saved));
        AssertEx.Equal(1, savedCalls);
        AssertEx.Equal<string?>(null, PreviewDirectiveAuthority.Resolve(false, null, () => { savedCalls++; return null; }));
        AssertEx.Equal(2, savedCalls);
        AssertEx.Equal<string?>(null, PreviewDirectiveAuthority.Resolve(true, null,
            () => throw new InvalidOperationException("An authorized deletion must never invoke this saved provider.")));
    }

    public static void MixedCameraChainDeletionCannotResurrectTheSavedOtherScope()
    {
        ProjectSnapshot project = Project(
            "#aavt;camera;set;x=100;zoom=2\n#aavt;camera;set;scope=background;x=10;zoom=1.5",
            "#aavt;camera;set;x=900;zoom=4\n#aavt;camera;set;scope=background;x=800;zoom=2",
            "#aavt;camera;move;dx=20", string.Empty);
        PreviewChainDirectiveIndex saved = PreviewChainDirectiveIndex.Build(project, true);
        var savedCalls = new List<(int Scene, SceneCameraScope Scope)>();
        PreviewCameraChainResolution chain = AssertEx.NotNull(new PreviewCameraChainResolver(project).Resolve(
            project.Nodes.Single().Scenes[3].Key,
            (key, scope) => PreviewDirectiveAuthority.Resolve(key.SceneIndex == 1,
                key.SceneIndex == 1 && scope == SceneCameraScope.Background
                    ? "#camera;set;scope=background;x=30;zoom=1.25;duration=0;easing=linear" : null,
                () =>
                {
                    savedCalls.Add((key.SceneIndex, scope));
                    return saved.TryGetCamera(key.NodeGuid, key.SceneIndex, scope, out string directive) ? directive : null;
                })).Value);
        AssertEx.Equal(new SceneCameraState(120, 0, 2), chain.InheritedState);
        AssertEx.Equal(new SceneCameraState(30, 0, 1.25f), chain.BackgroundInheritedState);
        AssertEx.Equal(4, chain.FoldedCommandCount);
        AssertEx.Equal(2, chain.OverallFoldedCommandCount);
        AssertEx.Equal(2, chain.BackgroundFoldedCommandCount);
        AssertEx.Equal(4, savedCalls.Count);
        AssertEx.False(savedCalls.Any(call => call.Scene == 1),
            "The scene owns an overall deletion and a background replacement; neither may borrow saved directives.");
        AssertEx.True(savedCalls.Any(call => call.Scene == 0 && call.Scope == SceneCameraScope.Overall));
        AssertEx.True(savedCalls.Any(call => call.Scene == 2 && call.Scope == SceneCameraScope.Overall),
            "An uncovered ancestor still obtains its saved move, preserving mixed-chain inheritance.");
    }

    private static string[] Extract(string text)
    {
        EmbeddedAavtExtraction result = new EmbeddedAavtDirectiveExtractor().Extract(text);
        AssertEx.Equal(0, result.Errors.Count, string.Join(" | ", result.Errors));
        return result.Commands.Select(command => command.CanonicalDirective).ToArray();
    }

    private static EditorPreviewSelectionBinding Selection(long generation) => new(generation, 17, generation + 10,
        CommandIdentity.CompiledScript("managed integration preview"),
        new EditorPreviewSceneAddress(new string('A', 24), NodeGuid, 0, new string('B', 24)));

    private static EditorPreviewLiveObservation Live(EditorPreviewSelectionBinding selection, long window) =>
        new(PlayerRuntimeContextSnapshot.FromPreviewMode(true), selection, 1, window);

    private static EditorPreviewLeaseAuthorization Authorize(EditorPreviewLeaseGate gate, long generation, long window,
        IReadOnlyList<string> directives, bool tombstone = false)
    {
        EditorPreviewSelectionBinding selection = Selection(generation);
        var result = gate.TryAuthorizeWindow(new(selection, directives, tombstone, window - 1), Live(selection, window));
        AssertEx.True(result.Success, result.Error);
        return AssertEx.NotNull(result.Value);
    }

    private static ProjectSnapshot Project(params string[] prompts)
    {
        SceneSnapshot[] scenes = prompts.Select((prompt, index) => new SceneSnapshot(
            new SceneKey(NodeGuid, index, new string((char)('A' + index), 64)), "managed-fixture", $"scene {index}", true,
            string.Empty, 0, 0, prompt, string.Empty, string.Empty, string.Empty, string.Empty, 0, 0, 0)).ToArray();
        var node = new StoryNodeSnapshot(0, StoryNodeKind.Script, "managed-fixture", NodeGuid, true,
            "test", Array.Empty<string>(), scenes);
        return new ProjectSnapshot(new("managed-fixture", "key", new string('F', 64), 0,
            DateTimeOffset.Parse("2026-10-04T00:00:00Z")), "managed-fixture", "test",
            new(null, string.Empty, string.Empty), new[] { node }, Array.Empty<ProjectDiagnostic>());
    }
}
