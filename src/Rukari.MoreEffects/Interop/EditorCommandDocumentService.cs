using System;
using System.Linq;
using AzureArchive.VideoTools.Api;
using AzureArchive.VideoTools.Core;
using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Mapping;
using AzureArchive.VideoTools.Core.Results;
using AzureArchive.VideoTools.Core.VisualEditor;
using AzureArchive.VideoTools.Formats.Aap;
using Rukari.Lib.Commands;
using ProjectSnapshot = AzureArchive.VideoTools.Core.Projects.ProjectSnapshot;
using StoryNodeSnapshot = AzureArchive.VideoTools.Core.Projects.StoryNodeSnapshot;
using DiscoveredProjectPair = AzureArchive.VideoTools.Core.Projects.DiscoveredProjectPair;

namespace AzureArchive.VideoTools.Interop;

internal sealed class EditorCommandDocumentService : IEditorCommandDocumentService
{
    private readonly RuntimeCapabilityService _capabilities;
    private readonly EditorSceneService _editor;
    private readonly EditorCommandDocumentComposer _composer = new();
    private readonly ArchiveSnapshotCache _projectCache = new();

    public EditorCommandDocumentService(
        RuntimeCapabilityService capabilities,
        EditorSceneService editor)
    {
        _capabilities = capabilities;
        _editor = editor;
    }

    public ApiResult<EditorCommandDocumentSnapshot> ReadSelectedOnMainThread()
    {
        try
        {
            if (!_editor.TryGetInternalSelection(
                    out EditorSceneService.InternalSceneSelection selection,
                    out string selectionError))
            {
                return ApiResult<EditorCommandDocumentSnapshot>.Fail(selectionError);
            }

            return ReadSelection(selection);
        }
        catch (Exception ex)
        {
            string detail = PatchGuard.Describe(ex);
            _capabilities.Degraded("Editor.AdditionalPromptRead", detail);
            return ApiResult<EditorCommandDocumentSnapshot>.Fail(detail);
        }
    }

    public ApiResult<EditorCommandEditPreviewSnapshot> PreviewUpsertSelectedOnMainThread(
        string expectedRevisionSha256,
        string directive,
        string? expectedSelectionToken = null)
    {
        try
        {
            if (!_editor.TryGetInternalSelection(
                    out EditorSceneService.InternalSceneSelection selection,
                    out string selectionError))
            {
                return ApiResult<EditorCommandEditPreviewSnapshot>.Fail(selectionError);
            }

            ApiResult<EditorCommandDocumentSnapshot> current = ReadSelection(selection);
            if (!current.Success || current.Value == null)
            {
                return ApiResult<EditorCommandEditPreviewSnapshot>.Fail(current.Error);
            }
            if (!MatchesSelectionToken(current.Value, expectedSelectionToken))
                return ApiResult<EditorCommandEditPreviewSnapshot>.Fail("台词选择已变化，请重新生成草稿。");

            return Preview(current.Value, expectedRevisionSha256, directive);
        }
        catch (Exception ex)
        {
            return ApiResult<EditorCommandEditPreviewSnapshot>.Fail(PatchGuard.Describe(ex));
        }
    }

    public ApiResult<EditorCommandApplySnapshot> ApplyUpsertSelectedOnMainThread(
        string expectedRevisionSha256,
        string directive,
        string? expectedSelectionToken = null)
    {
        try
        {
            if (!_editor.TryGetInternalSelection(
                    out EditorSceneService.InternalSceneSelection selection,
                    out string selectionError))
            {
                return ApiResult<EditorCommandApplySnapshot>.Fail(selectionError);
            }

            ApiResult<EditorCommandDocumentSnapshot> currentResult = ReadSelection(selection);
            if (!currentResult.Success || currentResult.Value == null)
            {
                return ApiResult<EditorCommandApplySnapshot>.Fail(currentResult.Error);
            }

            EditorCommandDocumentSnapshot before = currentResult.Value;
            if (!MatchesSelectionToken(before, expectedSelectionToken))
                return ApiResult<EditorCommandApplySnapshot>.Fail("台词选择已变化，请重新生成草稿。");
            ApiResult<EditorCommandEditPreviewSnapshot> previewResult = Preview(
                before,
                expectedRevisionSha256,
                directive);
            if (!previewResult.Success || previewResult.Value == null)
            {
                return ApiResult<EditorCommandApplySnapshot>.Fail(previewResult.Error);
            }

            EditorCommandEditPreviewSnapshot preview = previewResult.Value;
            if (string.Equals(
                    before.RevisionSha256,
                    preview.ResultRevisionSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                return ApiResult<EditorCommandApplySnapshot>.Fail(
                    "The verified draft is already present; no editor text change is required.");
            }

            ApiResult<EditorCommandDocumentSnapshot> write = WriteExact(
                selection,
                before.Address,
                preview.UpdatedAdditionalPrompt,
                preview.ResultRevisionSha256,
                expectedSelectionToken!, before.RevisionSha256);
            if (!write.Success || write.Value == null)
            {
                string rollback = RollBack(
                    selection,
                    before.Address,
                    before.AdditionalPrompt,
                    before.RevisionSha256);
                return ApiResult<EditorCommandApplySnapshot>.Fail(
                    $"AAVT apply verification failed: {write.Error} {rollback}");
            }


            EditorCommandDocumentSnapshot after = write.Value with
            {
                UndoAvailable = true
            };
            _capabilities.Verified(
                "Editor.AdditionalPromptWrite",
                "UIInput.Set(text, false) -> ScriptNodeInspector.SetAdditionalPrompt() -> exact Script/UI readback");
            return ApiResult<EditorCommandApplySnapshot>.Ok(new(
                before,
                after,
                preview.CanonicalPublicDirective,
                preview.PublicSlot,
                preview.ReplacedExisting));
        }
        catch (Exception ex)
        {
            string detail = PatchGuard.Describe(ex);
            _capabilities.Degraded("Editor.AdditionalPromptWrite", detail);
            return ApiResult<EditorCommandApplySnapshot>.Fail(detail);
        }
    }

    // A module-internal operation; the shared public editor contracts keep their ABI.
    // Remove only this slot's preset resource through the same guarded transaction as upsert.
    internal ApiResult<EditorCommandApplySnapshot> ApplyRemovePresetSelectedOnMainThread(
        string expectedRevisionSha256,
        int publicSlot,
        string expectedSelectionToken)
    {
        try
        {
            if (!_editor.TryGetInternalSelection(
                    out EditorSceneService.InternalSceneSelection selection,
                    out string selectionError))
                return ApiResult<EditorCommandApplySnapshot>.Fail(selectionError);

            var current = ReadSelection(selection);
            if (!current.Success || current.Value == null)
                return ApiResult<EditorCommandApplySnapshot>.Fail(current.Error);

            EditorCommandDocumentSnapshot before = current.Value;
            if (!MatchesSelectionToken(before, expectedSelectionToken))
                return ApiResult<EditorCommandApplySnapshot>.Fail("台词选择已变化，请重新生成草稿。");
            if (!before.InputAvailable || !before.InputMatchesScript)
                return ApiResult<EditorCommandApplySnapshot>.Fail("请先完成官方指令输入框中的编辑，再清除预设。");

            var edit = _composer.PreviewRemove(before.AdditionalPrompt, expectedRevisionSha256,
                "#aavt;fx;" + publicSlot.ToString(System.Globalization.CultureInfo.InvariantCulture) + ";sway");
            if (!edit.Success || edit.Value == null)
                return ApiResult<EditorCommandApplySnapshot>.Fail(edit.Error);

            EditorCommandEditPreview preview = edit.Value;
            if (string.Equals(before.RevisionSha256, preview.ResultRevisionSha256, StringComparison.Ordinal))
                return ApiResult<EditorCommandApplySnapshot>.Ok(new(before, before,
                    preview.CanonicalPublicDirective, publicSlot, false));

            var write = WriteExact(selection, before.Address,
                preview.UpdatedText, preview.ResultRevisionSha256,
                expectedSelectionToken, before.RevisionSha256);
            if (!write.Success || write.Value == null)
            {
                string rollback = RollBack(selection, before.Address,
                    before.AdditionalPrompt, before.RevisionSha256);
                return ApiResult<EditorCommandApplySnapshot>.Fail(
                    $"预设移除后校验未通过：{write.Error} {rollback}");
            }

            return ApiResult<EditorCommandApplySnapshot>.Ok(new(before,
                write.Value with { UndoAvailable = true },
                preview.CanonicalPublicDirective, publicSlot, preview.ReplacedExisting));
        }
        catch (Exception ex)
        {
            return ApiResult<EditorCommandApplySnapshot>.Fail(PatchGuard.Describe(ex));
        }
    }

    public ApiResult<EditorCommandContinueApplySnapshot> ApplyContinueToggleOnMainThread(
        bool enabled,
        string? expectedSelectionToken = null)
    {
        try
        {
            if (!_editor.TryGetInternalSelection(
                    out EditorSceneService.InternalSceneSelection selection,
                    out string selectionError))
            {
                return ApiResult<EditorCommandContinueApplySnapshot>.Fail(selectionError);
            }

            ApiResult<EditorCommandDocumentSnapshot> currentResult = ReadSelection(selection);
            if (!currentResult.Success || currentResult.Value == null)
            {
                return ApiResult<EditorCommandContinueApplySnapshot>.Fail(currentResult.Error);
            }

            EditorCommandDocumentSnapshot before = currentResult.Value;
            if (!MatchesSelectionToken(before, expectedSelectionToken))
                return ApiResult<EditorCommandContinueApplySnapshot>.Fail("台词选择已变化，请重新操作。");
            if (!before.InputAvailable || !before.InputMatchesScript)
            {
                return ApiResult<EditorCommandContinueApplySnapshot>.Fail(
                    "Official additional-prompt input is not synchronized with the selected Script.");
            }

            EditorCommandContinueEdit edit;
            try
            {
                edit = _composer.SetContinue(
                    before.AdditionalPrompt,
                    before.RevisionSha256,
                    enabled);
            }
            catch (InvalidOperationException ex)
            {
                return ApiResult<EditorCommandContinueApplySnapshot>.Fail(ex.Message);
            }

            if (edit.AlreadyPresent)
            {
                return ApiResult<EditorCommandContinueApplySnapshot>.Ok(new(
                    before,
                    before,
                    enabled,
                    AlreadyPresent: true));
            }

            ApiResult<EditorCommandDocumentSnapshot> write = WriteExact(
                selection,
                before.Address,
                edit.UpdatedText,
                edit.ResultRevisionSha256,
                expectedSelectionToken!, before.RevisionSha256);
            if (!write.Success || write.Value == null)
            {
                string rollback = RollBack(
                    selection,
                    before.Address,
                    before.AdditionalPrompt,
                    before.RevisionSha256);
                return ApiResult<EditorCommandContinueApplySnapshot>.Fail(
                    $"AAVT continue toggle verification failed: {write.Error} {rollback}");
            }


            EditorCommandDocumentSnapshot after = write.Value with
            {
                UndoAvailable = true
            };
            _capabilities.Verified(
                "Editor.ContinueToggle",
                enabled
                    ? "#aavt;continue appended through the official input path and read back"
                    : "#aavt;continue removed through the official input path and read back");
            return ApiResult<EditorCommandContinueApplySnapshot>.Ok(new(
                before,
                after,
                enabled,
                AlreadyPresent: false));
        }
        catch (Exception ex)
        {
            string detail = PatchGuard.Describe(ex);
            _capabilities.Degraded("Editor.ContinueToggle", detail);
            return ApiResult<EditorCommandContinueApplySnapshot>.Fail(detail);
        }
    }

    public ApiResult<EditorCommandScreenTextApplySnapshot> ApplyScreenTextOnMainThread(
        string expectedRevisionSha256,
        ScreenTextDirective? directive,
        string? expectedSelectionToken = null)
    {
        try
        {
            if (!_editor.TryGetInternalSelection(
                    out EditorSceneService.InternalSceneSelection selection,
                    out string selectionError))
            {
                return ApiResult<EditorCommandScreenTextApplySnapshot>.Fail(selectionError);
            }

            ApiResult<EditorCommandDocumentSnapshot> currentResult = ReadSelection(selection);
            if (!currentResult.Success || currentResult.Value == null)
            {
                return ApiResult<EditorCommandScreenTextApplySnapshot>.Fail(
                    currentResult.Error);
            }

            EditorCommandDocumentSnapshot before = currentResult.Value;
            if (!MatchesSelectionToken(before, expectedSelectionToken))
                return ApiResult<EditorCommandScreenTextApplySnapshot>.Fail("台词选择已变化，请重新生成草稿。");
            if (!before.InputAvailable || !before.InputMatchesScript)
            {
                return ApiResult<EditorCommandScreenTextApplySnapshot>.Fail(
                    "Official additional-prompt input is not synchronized with the selected Script.");
            }

            Result<EditorCommandScreenTextEdit> editResult = _composer.SetScreenText(
                before.AdditionalPrompt,
                expectedRevisionSha256,
                directive);
            if (!editResult.Success || editResult.Value == null)
            {
                return ApiResult<EditorCommandScreenTextApplySnapshot>.Fail(
                    editResult.Error);
            }

            EditorCommandScreenTextEdit edit = editResult.Value;
            if (edit.AlreadyPresent)
            {
                return ApiResult<EditorCommandScreenTextApplySnapshot>.Ok(new(
                    before,
                    before,
                    directive,
                    edit.ReplacedExisting,
                    AlreadyPresent: true));
            }

            ApiResult<EditorCommandDocumentSnapshot> write = WriteExact(
                selection,
                before.Address,
                edit.UpdatedText,
                edit.ResultRevisionSha256,
                expectedSelectionToken!, before.RevisionSha256);
            if (!write.Success || write.Value == null)
            {
                string rollback = RollBack(
                    selection,
                    before.Address,
                    before.AdditionalPrompt,
                    before.RevisionSha256);
                return ApiResult<EditorCommandScreenTextApplySnapshot>.Fail(
                    $"Official screen-text apply verification failed: {write.Error} {rollback}");
            }


            EditorCommandDocumentSnapshot after = write.Value with
            {
                UndoAvailable = true
            };
            _capabilities.Verified(
                "Editor.ScreenTextWrite",
                directive == null
                    ? "Official #st/#stm line removed through the official input path and read back"
                    : "Official #st/#stm line upserted through the official input path and read back");
            return ApiResult<EditorCommandScreenTextApplySnapshot>.Ok(new(
                before,
                after,
                directive,
                edit.ReplacedExisting,
                AlreadyPresent: false));
        }
        catch (Exception ex)
        {
            string detail = PatchGuard.Describe(ex);
            _capabilities.Degraded("Editor.ScreenTextWrite", detail);
            return ApiResult<EditorCommandScreenTextApplySnapshot>.Fail(detail);
        }
    }

    public ApiResult<EditorCommandClearScreenTextApplySnapshot>
        ApplyClearScreenTextOnMainThread(string expectedRevisionSha256, string? expectedSelectionToken = null)
    {
        try
        {
            if (!_editor.TryGetInternalSelection(
                    out EditorSceneService.InternalSceneSelection selection,
                    out string selectionError))
            {
                return ApiResult<EditorCommandClearScreenTextApplySnapshot>.Fail(
                    selectionError);
            }

            ApiResult<EditorCommandDocumentSnapshot> currentResult = ReadSelection(selection);
            if (!currentResult.Success || currentResult.Value == null)
            {
                return ApiResult<EditorCommandClearScreenTextApplySnapshot>.Fail(
                    currentResult.Error);
            }

            EditorCommandDocumentSnapshot before = currentResult.Value;
            if (!MatchesSelectionToken(before, expectedSelectionToken))
                return ApiResult<EditorCommandClearScreenTextApplySnapshot>.Fail("台词选择已变化，请重新生成草稿。");
            if (!before.InputAvailable || !before.InputMatchesScript)
            {
                return ApiResult<EditorCommandClearScreenTextApplySnapshot>.Fail(
                    "Official additional-prompt input is not synchronized with the selected Script.");
            }

            Result<EditorCommandClearScreenTextEdit> editResult =
                _composer.AddClearScreenText(
                    before.AdditionalPrompt,
                    expectedRevisionSha256);
            if (!editResult.Success || editResult.Value == null)
            {
                return ApiResult<EditorCommandClearScreenTextApplySnapshot>.Fail(
                    editResult.Error);
            }

            EditorCommandClearScreenTextEdit edit = editResult.Value;
            if (edit.AlreadyPresent)
            {
                return ApiResult<EditorCommandClearScreenTextApplySnapshot>.Ok(new(
                    before,
                    before,
                    AlreadyPresent: true));
            }

            ApiResult<EditorCommandDocumentSnapshot> write = WriteExact(
                selection,
                before.Address,
                edit.UpdatedText,
                edit.ResultRevisionSha256,
                expectedSelectionToken!, before.RevisionSha256);
            if (!write.Success || write.Value == null)
            {
                string rollback = RollBack(
                    selection,
                    before.Address,
                    before.AdditionalPrompt,
                    before.RevisionSha256);
                return ApiResult<EditorCommandClearScreenTextApplySnapshot>.Fail(
                    $"Official #clearST apply verification failed: {write.Error} {rollback}");
            }


            EditorCommandDocumentSnapshot after = write.Value with
            {
                UndoAvailable = true
            };
            _capabilities.Verified(
                "Editor.ScreenTextClearWrite",
                "Official #clearST appended through the official input path and read back");
            return ApiResult<EditorCommandClearScreenTextApplySnapshot>.Ok(new(
                before,
                after,
                AlreadyPresent: false));
        }
        catch (Exception ex)
        {
            string detail = PatchGuard.Describe(ex);
            _capabilities.Degraded("Editor.ScreenTextClearWrite", detail);
            return ApiResult<EditorCommandClearScreenTextApplySnapshot>.Fail(detail);
        }
    }

    public ApiResult<EditorCommandUndoSnapshot> UndoSelectedOnMainThread(string? expectedSelectionToken = null)
    {
        var before = ReadSelectedOnMainThread();
        if (!before.Success || before.Value == null) return ApiResult<EditorCommandUndoSnapshot>.Fail(before.Error);
        if (!MatchesSelectionToken(before.Value, expectedSelectionToken))
            return ApiResult<EditorCommandUndoSnapshot>.Fail("台词或内容已变化，请先刷新撤销状态。");
        var service = Rukari.Lib.ModServices.Current?.GetService<Rukari.Lib.Editor.IEditorDocumentService>();
        if (service?.Success != true || service.Value == null)
            return ApiResult<EditorCommandUndoSnapshot>.Fail("共享编辑器服务尚未就绪。");
        var current = service.Value.ReadSelection();
        if (!current.Success || current.Value == null || current.Value.SelectionToken != expectedSelectionToken)
            return ApiResult<EditorCommandUndoSnapshot>.Fail("台词或内容已变化，请先刷新撤销状态。");
        var result = service.Value.Undo();
        if (!result.Success) return ApiResult<EditorCommandUndoSnapshot>.Fail(result.Error?.Message ?? "共享编辑器操作失败。");
        var after = ReadSelectedOnMainThread();
        return after.Success && after.Value != null
            ? ApiResult<EditorCommandUndoSnapshot>.Ok(new(before.Value, after.Value))
            : ApiResult<EditorCommandUndoSnapshot>.Fail(after.Error);
    }

    /// <summary>
    /// Slots of the selected scene whose official authored content moves the
    /// character (StartingPosition != EndingPosition). Read from the same AAP
    /// snapshot the playback index compiler uses, cached by mtime+length so
    /// GUI polling never re-parses the archive.
    /// </summary>
    private IReadOnlyList<int> ResolveOfficialTransitionSlots(SceneAddress address)
    {
        DiscoveredProjectPair? pair = ActiveProjectPairSource.ActivePairSnapshot();
        string aapPath = pair?.AapPath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(aapPath))
        {
            return Array.Empty<int>();
        }

        Result<ProjectSnapshot> project = _projectCache.GetOrReadProject(
            aapPath,
            path => new AapProjectReader().Read(path));
        if (!project.Success || project.Value == null)
        {
            return Array.Empty<int>();
        }

        foreach (StoryNodeSnapshot node in project.Value.ScriptNodes)
        {
            if (string.IsNullOrWhiteSpace(node?.NodeGuid)
                || !string.Equals(
                    node.NodeGuid,
                    address.NodeGuid,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var scene in node.Scenes)
            {
                if (scene?.Key is null || scene.Key.SceneIndex != address.SceneIndex)
                {
                    continue;
                }

                return scene.Characters
                    .Where(character => character.HasOfficialPositionTransition)
                    .Select(character => character.PhysicalSlot)
                    .ToArray();
            }
        }

        return Array.Empty<int>();
    }

    private ApiResult<EditorCommandDocumentSnapshot> ReadSelection(
        EditorSceneService.InternalSceneSelection selection)
    {
        ApiResult<SceneSnapshot> scene = _editor.ReadInternalSelection(selection);
        if (!scene.Success || scene.Value == null)
        {
            return ApiResult<EditorCommandDocumentSnapshot>.Fail(scene.Error);
        }

        string additionalPrompt =
            InteropMemberAccess.Get<string>(selection.Current, "additionalPrompt")
            ?? string.Empty;
        string? ownershipError = DirectiveOwnershipError(additionalPrompt);
        if (ownershipError != null)
            return ApiResult<EditorCommandDocumentSnapshot>.Fail(ownershipError);
        Result<EditorCommandDocument> parsed = _composer.Read(additionalPrompt);
        if (!parsed.Success || parsed.Value == null)
        {
            return ApiResult<EditorCommandDocumentSnapshot>.Fail(parsed.Error);
        }

        UIInput? input = InteropMemberAccess.Get<UIInput>(
            selection.Inspector,
            "additionalPromptInput");
        bool inputAvailable = InteropObjectGuard.IsAlive(input);
        string inputValue = inputAvailable
            ? InteropMemberAccess.Get<string>(input!, "value") ?? string.Empty
            : string.Empty;
        bool inputMatchesScript = inputAvailable
            && string.Equals(inputValue, additionalPrompt, StringComparison.Ordinal);

        _capabilities.Verified(
            "Editor.AdditionalPromptRead",
            "Selected Script.additionalPrompt returned a managed string; no Script wrapper was retained");
        if (inputAvailable)
        {
            _capabilities.Verified(
                "Editor.AdditionalPromptInputRead",
                inputMatchesScript
                    ? "additionalPromptInput.value exactly matched Script.additionalPrompt"
                    : "additionalPromptInput.value was readable but differed from Script.additionalPrompt");
        }

        EditorCommandDocument document = parsed.Value;
        var common = Rukari.Lib.ModServices.Current?.GetService<Rukari.Lib.Editor.IEditorDocumentService>();
        var sharedDocument = common?.Success == true ? common.Value?.ReadSelection() : null;
        if (sharedDocument?.Success != true || sharedDocument.Value == null)
            return ApiResult<EditorCommandDocumentSnapshot>.Fail(sharedDocument?.Error?.Message ?? "共享编辑器服务尚未就绪。");
        var shared = sharedDocument.Value;
        string expectedContext = $"{selection.Inspector.Pointer.ToInt64():X}:{selection.Node.Pointer.ToInt64():X}:{selection.Current.Pointer.ToInt64():X}:{selection.Index}";
        if (shared.ContextId != expectedContext || shared.DialogueText != scene.Value.DialogueText
            || shared.AdditionalPrompt != document.Text
            || !string.Equals(shared.Revision, document.RevisionSha256, StringComparison.OrdinalIgnoreCase))
            return ApiResult<EditorCommandDocumentSnapshot>.Fail("读取期间台词或内容已变化，请刷新。");
        bool undoAvailable = shared.CanUndo;
        return ApiResult<EditorCommandDocumentSnapshot>.Ok(new(
            scene.Value.Address,
            scene.Value.DialogueText,
            document.Text,
            document.RevisionSha256,
            document.OfficialLineCount,
            document.AavtLineCount,
            document.HasContinueDirective,
            document.HasClearScreenTextDirective,
            inputAvailable,
            inputMatchesScript,
            undoAvailable,
            Array.AsReadOnly(document.Commands
                .Select(command => command.CanonicalDirective)
                .ToArray()),
            Array.AsReadOnly(document.ScreenTextLines.ToArray()),
            Array.AsReadOnly(
                ResolveOfficialTransitionSlots(scene.Value.Address).ToArray()))
        {
            RuntimeSelectionKey = shared.SelectionToken
        });
    }

    private ApiResult<EditorCommandEditPreviewSnapshot> Preview(
        EditorCommandDocumentSnapshot current,
        string expectedRevisionSha256,
        string directive)
    {
        if (!current.InputAvailable || !current.InputMatchesScript)
        {
            return ApiResult<EditorCommandEditPreviewSnapshot>.Fail(
                "Official additional-prompt input is not synchronized with the selected Script.");
        }

        Result<EditorCommandEditPreview> preview = _composer.PreviewUpsert(
            current.AdditionalPrompt,
            expectedRevisionSha256,
            directive);
        if (!preview.Success || preview.Value == null)
        {
            return ApiResult<EditorCommandEditPreviewSnapshot>.Fail(preview.Error);
        }

        EditorCommandEditPreview edit = preview.Value;
        string? ownershipError = DirectiveOwnershipError(edit.UpdatedText);
        if (ownershipError != null)
            return ApiResult<EditorCommandEditPreviewSnapshot>.Fail(ownershipError);
        return ApiResult<EditorCommandEditPreviewSnapshot>.Ok(new(
            current,
            edit.ResultRevisionSha256,
            edit.UpdatedText,
            edit.CanonicalPublicDirective,
            edit.PublicSlot,
            edit.ReplacedExisting));
    }

    private ApiResult<EditorCommandDocumentSnapshot> WriteExact(
        EditorSceneService.InternalSceneSelection selection,
        SceneAddress expectedAddress,
        string targetText,
        string targetRevisionSha256,
        string expectedSelectionToken,
        string expectedRevisionSha256)
    {
        try
        {
            string? ownershipError = DirectiveOwnershipError(targetText);
            if (ownershipError != null)
                return ApiResult<EditorCommandDocumentSnapshot>.Fail(ownershipError);
            var common = Rukari.Lib.ModServices.Current?.GetService<Rukari.Lib.Editor.IEditorDocumentService>();
            if (common?.Success != true || common.Value == null)
                return ApiResult<EditorCommandDocumentSnapshot>.Fail("共享编辑器服务尚未就绪。");
            var current = common.Value.ReadSelection();
            if (!current.Success || current.Value == null)
                return ApiResult<EditorCommandDocumentSnapshot>.Fail(current.Error?.Message ?? "共享编辑器读取失败。");
            string expectedContext = $"{selection.Inspector.Pointer.ToInt64():X}:{selection.Node.Pointer.ToInt64():X}:{selection.Current.Pointer.ToInt64():X}:{selection.Index}";
            if (current.Value.ContextId != expectedContext
                || current.Value.SelectionToken != expectedSelectionToken
                || !string.Equals(current.Value.Revision, expectedRevisionSha256, StringComparison.OrdinalIgnoreCase))
                return ApiResult<EditorCommandDocumentSnapshot>.Fail("当前台词实例已变化。");
            var scene = _editor.ReadInternalSelection(selection);
            if (!scene.Success || scene.Value?.Address != expectedAddress)
                return ApiResult<EditorCommandDocumentSnapshot>.Fail("当前台词地址已变化。");
            // Carry the token/revision from the displayed draft all the way to the shared write.
            // A newly observed token must never grant authority to an older draft.
            var result = common.Value.Replace(new(expectedSelectionToken, expectedRevisionSha256, targetText));
            if (!result.Success || result.Value == null)
                return ApiResult<EditorCommandDocumentSnapshot>.Fail(result.Error?.Message ?? "共享编辑器操作失败。");
            if (!string.Equals(result.Value.Selection.Revision, targetRevisionSha256, StringComparison.OrdinalIgnoreCase))
                return ApiResult<EditorCommandDocumentSnapshot>.Fail("共享编辑器写回校验不一致。");
            return ReadSelection(selection);
        }
        catch (Exception ex) { return ApiResult<EditorCommandDocumentSnapshot>.Fail(PatchGuard.Describe(ex)); }
    }

    private static string? DirectiveOwnershipError(string text)
    {
        var service = Rukari.Lib.ModServices.Current?.GetService<IEmbeddedDirectiveService>();
        return service is { Success: true, Value: not null }
            ? AavtDirectiveOwnershipPolicy.FindEditorConflict(text, service.Value.CaptureSanitizer())
            : "共享指令服务尚未就绪，请先启用 Rukari Lib。";
    }

    private string RollBack(EditorSceneService.InternalSceneSelection selection, SceneAddress expectedAddress,
        string originalText, string originalRevisionSha256)
        => "共享编辑器事务已处理写回与回滚；功能模块不重复写入。";

    private static bool MatchesSelectionToken(EditorCommandDocumentSnapshot current, string? expected)
        => !string.IsNullOrEmpty(expected) && string.Equals(current.RuntimeSelectionKey, expected, StringComparison.Ordinal);
}
