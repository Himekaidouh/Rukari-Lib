using Rukari.CharacterVoice.Core;
using Rukari.CharacterVoice.Runtime;
using Rukari.Lib;
using Rukari.Lib.Editor;
using Rukari.Lib.Tools;
using Rukari.Lib.Voices;

internal static class VoiceToolPageTests
{
    internal static void ProjectSwitchCancelsFolderImportWithoutApplyingToAnotherLine()
    {
        string projectA = Path.Combine(Path.GetTempPath(), "voice-ui-project-a");
        string projectB = Path.Combine(Path.GetTempPath(), "voice-ui-project-b");
        string currentProject = projectA;
        long clock = 0;
        var voice = new TestVoice();
        using var folders = ReadyController();
        var completion = new TaskCompletionSource<VoiceImportResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken importCancellation = default;
        string? importProject = null;
        using var page = new VoiceToolPage(voice, new TestEditor(),
            () => ModResult<VoiceImportProject>.Ok(new(currentProject)),
            (project, scan, token) => { importProject = project.Identity; importCancellation = token; return completion.Task; }, folders, () => clock);
        page.Handle(new("choose-folder"));
        AwaitFolder(page, folders);
        page.Handle(new("import-folder"));
        Equal(projectA, importProject);
        currentProject = projectB;
        clock += 250;
        page.Tick();
        True(importCancellation.IsCancellationRequested);
        True(folders.Current is null);
        completion.SetResult(new(projectA, 1, 0, 0, 0, 0, 1));
        page.Tick();
        Equal(0, voice.Applies);
        True(!page.Snapshot().Buttons.Any(button => button.Id == "import-folder"));
    }

    internal static void SuccessfulFolderImportRetainsExistingSoundsAndRequiresExplicitBinding()
    {
        string project = Path.Combine(Path.GetTempPath(), "voice-ui-project");
        var voice = new TestVoice();
        using var folders = ReadyController();
        using var page = new VoiceToolPage(voice, new TestEditor(),
            () => ModResult<VoiceImportProject>.Ok(new(project)),
            (captured, scan, token) =>
            {
                voice.Catalog.Add(new("rukari-import/test", "hello.wav"));
                return Task.FromResult(new VoiceImportResult(captured.Identity, 1, 0, 0, 0, 0, 1));
            }, folders);
        page.Handle(new("choose-folder"));
        AwaitFolder(page, folders);
        var preview = page.Snapshot();
        Equal(8, preview.Buttons.Count);
        True(preview.Buttons.Any(button => button.Id == "recursive"));
        True(preview.Items!.Single().Label == "hello.wav");
        True(!preview.Items!.Single().Enabled); // Scanning is a preview, not an implicit binding.
        page.Handle(new("import-folder"));
        page.Tick();
        ToolPageSnapshot imported = page.Snapshot();
        // No native resource cleanup action is exposed after importing.
        Equal(8, imported.Buttons.Count);
        True(!imported.Buttons.Any(button => button.Id == "cleanup-voices"));
        True(imported.Buttons.Any(button => button.Id == "recursive" && !button.Enabled));
        True(imported.Items!.Any(item => item.Id == "legacy/manual"));
        True(imported.Items!.Any(item => item.Id == "rukari-import/test"));
        Equal(0, voice.Applies);
        page.Handle(new("select", "rukari-import/test"));
        page.Handle(new("apply"));
        Equal(1, voice.Applies);
        Equal("rukari-import/test", voice.LastRequest?.ResourceId);
        Equal("line-a-token", voice.LastRequest?.SelectionToken);
    }

    internal static void DisposedVoicePageCancelsWorkersAndIgnoresTheirResults()
    {
        string project = Path.Combine(Path.GetTempPath(), "voice-ui-project");
        var voice = new TestVoice();
        using var folders = ReadyController();
        var completion = new TaskCompletionSource<VoiceImportResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken importCancellation = default;
        var page = new VoiceToolPage(voice, new TestEditor(),
            () => ModResult<VoiceImportProject>.Ok(new(project)),
            (_, _, token) => { importCancellation = token; return completion.Task; }, folders);
        page.Handle(new("choose-folder"));
        AwaitFolder(page, folders);
        page.Handle(new("import-folder"));
        page.Dispose();
        True(importCancellation.IsCancellationRequested);
        completion.SetResult(new(project, 1, 0, 0, 0, 0, 1));
        page.Tick();
        page.Handle(new("apply"));
        Equal(0, voice.Applies);
    }

    internal static void HiddenIdlePageDoesNotPollNativeProjectStateEveryFrame()
    {
        string project = Path.Combine(Path.GetTempPath(), "voice-ui-project");
        int captures = 0;
        using var folders = ReadyController();
        using var page = new VoiceToolPage(new TestVoice(), new TestEditor(),
            () => { captures++; return ModResult<VoiceImportProject>.Ok(new(project)); },
            (captured, scan, token) => Task.FromResult(new VoiceImportResult(captured.Identity, 0, 0, 0, 0, 0, 0)), folders);
        page.Handle(new("choose-folder"));
        AwaitFolder(page, folders);
        int before = captures;
        for (int frame = 0; frame < 200; frame++) page.Tick();
        Equal(before, captures);
        page.Snapshot();
        Equal(before + 1, captures);
    }

    internal static void CatalogNavigationFollowsProjectWithoutFollowingDialogueTokens()
    {
        string project = Path.Combine(Path.GetTempPath(), "voice-navigation-project");
        int captures = 0;
        var voice = new TestVoice();
        using var page = new VoiceToolPage(voice, new TestEditor(),
            () => { captures++; return ModResult<VoiceImportProject>.Ok(new(project)); },
            (_, _, _) => throw new InvalidOperationException("Navigation must never import audio."));
        ToolPageSnapshot first = page.Snapshot();
        True(first.ListNavigation is not null);
        Equal(ToolListSearchMode.Locate, first.ListNavigation!.SearchMode);
        True(first.ListNavigation.ContextId.Contains(project, StringComparison.OrdinalIgnoreCase));
        Equal(1, captures);
        for (int frame = 0; frame < 200; frame++) page.Tick();
        Equal(1, captures); // The hidden, idle page does not add native project polling.
        voice.Selection = new("line-b-token", "revision-b", "另一句台词", null);
        ToolPageSnapshot next = page.Snapshot();
        Equal(first.ListNavigation, next.ListNavigation);
        Equal(2, captures); // One project capture per visible snapshot; no extra capture for context.
        Equal(1, voice.CatalogReads);
        Equal(0, voice.Applies);
    }

    internal static void CatalogProjectSwitchChangesNavigationAndDiscardsOldChoices()
    {
        string projectA = Path.Combine(Path.GetTempPath(), "voice-navigation-project-a");
        string projectB = Path.Combine(Path.GetTempPath(), "voice-navigation-project-b");
        string currentProject = projectA;
        var voice = new TestVoice();
        using var page = new VoiceToolPage(voice, new TestEditor(),
            () => ModResult<VoiceImportProject>.Ok(new(currentProject)),
            (_, _, _) => throw new InvalidOperationException("Navigation must never import audio."));
        ToolPageSnapshot first = page.Snapshot();
        page.Handle(new("select", "legacy/manual"));
        True(page.Snapshot().Items!.Single().Selected);
        voice.Catalog.Clear();
        voice.Catalog.Add(new("project-b/voice", "新项目语音"));
        currentProject = projectB;
        ToolPageSnapshot switched = page.Snapshot();
        True(first.ListNavigation != switched.ListNavigation);
        True(switched.ListNavigation!.ContextId.Contains(projectB, StringComparison.OrdinalIgnoreCase));
        Equal("project-b/voice", switched.Items!.Single().Id);
        True(!switched.Items!.Single().Selected);
        True(!switched.Buttons.Single(button => button.Id == "apply").Enabled);
        page.Handle(new("apply"));
        Equal(0, voice.Applies);
        Equal(2, voice.CatalogReads);
        currentProject = projectB.ToUpperInvariant();
        Equal(switched.ListNavigation, page.Snapshot().ListNavigation);
        Equal(2, voice.CatalogReads); // Equivalent Windows path casing does not reread the catalog.
    }

    internal static void UnavailableProjectUsesEmptyNavigationAndCannotBindOldCatalog()
    {
        string project = Path.Combine(Path.GetTempPath(), "voice-navigation-recovery");
        bool available = true;
        var voice = new TestVoice();
        using var page = new VoiceToolPage(voice, new TestEditor(),
            () => available ? ModResult<VoiceImportProject>.Ok(new(project))
                : ModResult<VoiceImportProject>.Fail(ModErrorCode.NotReady, "项目暂不可用。"),
            (_, _, _) => throw new InvalidOperationException("Navigation must never import audio."));
        ToolPageSnapshot first = page.Snapshot();
        page.Handle(new("select", "legacy/manual"));
        available = false;
        ToolPageSnapshot unavailable = page.Snapshot();
        True(first.ListNavigation != unavailable.ListNavigation);
        Equal(ToolListSearchMode.Locate, unavailable.ListNavigation!.SearchMode);
        Equal(0, unavailable.Items!.Count);
        True(!unavailable.Buttons.Single(button => button.Id == "apply").Enabled);
        page.Handle(new("select", "legacy/manual"));
        page.Handle(new("apply"));
        page.Handle(new("refresh"));
        Equal(0, voice.Applies);
        Equal(1, voice.CatalogReads);
        available = true;
        ToolPageSnapshot recovered = page.Snapshot();
        Equal(first.ListNavigation, recovered.ListNavigation);
        Equal("legacy/manual", recovered.Items!.Single().Id);
        True(!recovered.Items!.Single().Selected);
        Equal(1, voice.CatalogReads); // Temporary unavailability preserves the verified catalog.
    }

    internal static void FolderPreviewAndCatalogUseIndependentNavigationContexts()
    {
        string project = Path.Combine(Path.GetTempPath(), "voice-navigation-folder");
        using var folders = ReadyController();
        using var page = new VoiceToolPage(new TestVoice(), new TestEditor(),
            () => ModResult<VoiceImportProject>.Ok(new(project)),
            (_, _, _) => throw new InvalidOperationException("Navigation must never import audio."), folders);
        ToolPageSnapshot catalog = page.Snapshot();
        page.Handle(new("choose-folder"));
        AwaitFolder(page, folders);
        ToolPageSnapshot directFolder = page.Snapshot();
        True(directFolder.ListNavigation != catalog.ListNavigation);
        Equal(ToolListSearchMode.Locate, directFolder.ListNavigation!.SearchMode);
        True(directFolder.ListNavigation.ContextId.Contains(folders.Current!.RootPath, StringComparison.OrdinalIgnoreCase));
        page.Handle(new("show-source"));
        Equal(catalog.ListNavigation, page.Snapshot().ListNavigation);
        page.Handle(new("show-source"));
        Equal(directFolder.ListNavigation, page.Snapshot().ListNavigation);
        page.Handle(new("recursive"));
        AwaitFolder(page, folders);
        ToolPageSnapshot recursiveFolder = page.Snapshot();
        True(recursiveFolder.ListNavigation != directFolder.ListNavigation);
        True(recursiveFolder.Items!.All(item => !item.Enabled)); // Locating a preview cannot bind it.
    }

    private static VoiceFolderController ReadyController() => new(new Picker(), (folder, recursive, cancellation) =>
        new(folder, recursive, new[] { new VoiceFolderFile(Path.Combine(folder, "hello.wav"), "hello.wav", 44, ".wav") }, 0));

    private static void AwaitFolder(VoiceToolPage page, VoiceFolderController folders)
    {
        True(SpinWait.SpinUntil(() => { page.Tick(); return !folders.IsBusy; }, TimeSpan.FromSeconds(5)));
        True(folders.Current is not null);
    }

    private sealed class Picker : IVoiceFolderPicker
    {
        public Task<string?> PickAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(Path.GetTempPath());
    }

    private sealed class TestVoice : IVoiceAuthoringService
    {
        internal readonly List<VoiceResource> Catalog = new() { new("legacy/manual", "手动导入的声音") };
        internal int Applies;
        internal int CatalogReads;
        internal VoiceEditRequest? LastRequest;
        internal VoiceSelection Selection = new("line-a-token", "revision-a", "台词", null);
        public ModResult<IReadOnlyList<VoiceResource>> ReadCatalog()
        {
            CatalogReads++;
            return ModResult<IReadOnlyList<VoiceResource>>.Ok(Catalog.ToArray());
        }
        public ModResult<VoiceSelection> ReadSelection() => ModResult<VoiceSelection>.Ok(Selection);
        public ModResult<VoiceEditResult> Apply(VoiceEditRequest request)
        {
            Applies++;
            LastRequest = request;
            return ModResult<VoiceEditResult>.Ok(new(new("line-a-written", "revision-b", "台词", request.ResourceId), true));
        }
    }

    private sealed class TestEditor : IEditorDocumentService
    {
        public ModResult<EditorDocumentSnapshot> ReadSelection() =>
            ModResult<EditorDocumentSnapshot>.Ok(new("line-a", "context", "revision-a", "台词", "", false));
        public ModResult<EditorDocumentEditResult> Replace(EditorDocumentEditRequest request) =>
            ModResult<EditorDocumentEditResult>.Fail(ModErrorCode.NotReady, "Not used by folder import.");
        public ModResult<EditorDocumentEditResult> Undo() =>
            ModResult<EditorDocumentEditResult>.Fail(ModErrorCode.NotReady, "Not used by folder import.");
    }

    private static void True(bool value) { if (!value) throw new InvalidOperationException("Expected true."); }
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, actual {actual}.");
    }
}
