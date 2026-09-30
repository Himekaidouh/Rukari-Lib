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
        // A page carries at most 8 actions, so the cleanup action takes the slot the folder toggle held; the third
        // row shows the catalogue again after an import.
        Equal(8, imported.Buttons.Count);
        True(imported.Buttons.Any(button => button.Id == "cleanup-voices"));
        True(!imported.Buttons.Any(button => button.Id == "recursive"));
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
        internal VoiceEditRequest? LastRequest;
        public ModResult<IReadOnlyList<VoiceResource>> ReadCatalog() => ModResult<IReadOnlyList<VoiceResource>>.Ok(Catalog.ToArray());
        public ModResult<VoiceSelection> ReadSelection() => ModResult<VoiceSelection>.Ok(new("line-a-token", "revision-a", "台词", null));
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
