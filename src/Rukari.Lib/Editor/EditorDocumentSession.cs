using System.Security.Cryptography;
using System.Text;

namespace Rukari.Lib.Editor;

internal sealed record EditorBackendDocument(string ContextId, string DialogueText, string AdditionalPrompt,
    IReadOnlyDictionary<string, string>? NativeFields = null);

internal interface IEditorDocumentBackend
{
    ModResult<EditorBackendDocument> Read();
    // The backend performs a fresh identity/text comparison before writing, verifies exact readback,
    // and rolls back only while the original context remains active.
    ModResult<EditorBackendDocument> Write(EditorBackendDocument expected, string text);
}

internal sealed class EditorDocumentSession : IEditorDocumentService, IEditorSelectionInvalidation
{
    private readonly IEditorDocumentBackend _backend;
    private readonly Func<bool> _ready;
    private readonly Func<bool> _mainThread;
    private EditorBackendDocument? _observed;
    private string _token = string.Empty;
    private UndoRecord? _undo;
    private bool _writing;
    private sealed record UndoRecord(string Context, string Dialogue, string Before, string After);

    internal EditorDocumentSession(IEditorDocumentBackend backend, Func<bool> ready, Func<bool> mainThread)
        => (_backend, _ready, _mainThread) = (backend, ready, mainThread);

    internal void InvalidateSelection() { _observed = null; _token = string.Empty; }

    ModResult<bool> IEditorSelectionInvalidation.InvalidateSelection()
    {
        var error = Guard();
        if (error != null) return ModResult<bool>.Fail(error);
        InvalidateSelection();
        return ModResult<bool>.Ok(true);
    }

    public ModResult<EditorDocumentSnapshot> ReadSelection()
    {
        var error = Guard();
        if (error != null) return ModResult<EditorDocumentSnapshot>.Fail(error);
        try
        {
            var read = _backend.Read();
            if (!read.Success || read.Value == null)
            {
                InvalidateSelection();
                return ModResult<EditorDocumentSnapshot>.Fail(read.Error!);
            }
            return ModResult<EditorDocumentSnapshot>.Ok(Snapshot(read.Value));
        }
        catch (Exception e) { InvalidateSelection(); return ModResult<EditorDocumentSnapshot>.Fail(ModErrorCode.ProviderFailed, e.Message); }
    }

    public ModResult<EditorDocumentEditResult> Replace(EditorDocumentEditRequest request)
    {
        if (request == null || request.AdditionalPrompt == null || string.IsNullOrEmpty(request.SelectionToken))
            return ModResult<EditorDocumentEditResult>.Fail(ModErrorCode.InvalidArgument, "缺少台词或指令内容。");
        var current = ReadSelection();
        if (!current.Success || current.Value == null) return ModResult<EditorDocumentEditResult>.Fail(current.Error!);
        if (current.Value.SelectionToken != request.SelectionToken || current.Value.Revision != request.ExpectedRevision)
            return ModResult<EditorDocumentEditResult>.Fail(ModErrorCode.Conflict, "台词或指令已变化，请重新生成草稿。");
        return Write(_observed!, request.AdditionalPrompt, undo: false);
    }

    public ModResult<EditorDocumentEditResult> Undo()
    {
        var current = ReadSelection();
        if (!current.Success || current.Value == null) return ModResult<EditorDocumentEditResult>.Fail(current.Error!);
        if (!CanUndo(_observed!))
            return ModResult<EditorDocumentEditResult>.Fail(ModErrorCode.Conflict, "当前台词已变化，无法撤销上次操作。");
        return Write(_observed!, _undo!.Before, undo: true);
    }

    private ModResult<EditorDocumentEditResult> Write(EditorBackendDocument before, string text, bool undo)
    {
        if (before.AdditionalPrompt == text) return ModResult<EditorDocumentEditResult>.Ok(new(Snapshot(before), false));
        _writing = true;
        try
        {
            var result = _backend.Write(before, text);
            if (!result.Success || result.Value == null)
            { InvalidateSelection(); return ModResult<EditorDocumentEditResult>.Fail(result.Error!); }
            var after = result.Value;
            if (after.ContextId != before.ContextId || after.DialogueText != before.DialogueText || after.AdditionalPrompt != text)
            { InvalidateSelection(); return ModResult<EditorDocumentEditResult>.Fail(ModErrorCode.ProviderFailed, "写入结果未通过核对。"); }
            _undo = undo ? null : new(before.ContextId, before.DialogueText, before.AdditionalPrompt, text);
            return ModResult<EditorDocumentEditResult>.Ok(new(Snapshot(after), true));
        }
        catch (Exception e) { InvalidateSelection(); return ModResult<EditorDocumentEditResult>.Fail(ModErrorCode.ProviderFailed, e.Message); }
        finally { _writing = false; }
    }

    private EditorDocumentSnapshot Snapshot(EditorBackendDocument document)
    {
        if (_observed == null || _observed.ContextId != document.ContextId || _observed.DialogueText != document.DialogueText
            || _observed.AdditionalPrompt != document.AdditionalPrompt || string.IsNullOrEmpty(_token))
            _token = Guid.NewGuid().ToString("N");
        _observed = document;
        return new(_token, document.ContextId, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(document.AdditionalPrompt))),
            document.DialogueText, document.AdditionalPrompt, CanUndo(document), document.NativeFields);
    }

    private bool CanUndo(EditorBackendDocument d) => _undo != null && _undo.Context == d.ContextId
        && _undo.Dialogue == d.DialogueText && _undo.After == d.AdditionalPrompt;

    private ModError? Guard() => !_ready() ? new(ModErrorCode.NotReady, "编辑器服务尚未就绪。")
        : !_mainThread() ? new(ModErrorCode.WrongThread, "编辑器操作必须在主线程进行。")
        : _writing ? new(ModErrorCode.Busy, "另一个编辑器操作尚未完成。") : null;
}
