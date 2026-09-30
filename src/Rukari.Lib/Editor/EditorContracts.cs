namespace Rukari.Lib.Editor;

/// <summary>A fresh managed view of the selected editor line. Tokens are opaque and temporary.</summary>
public sealed record EditorDocumentSnapshot(string SelectionToken, string ContextId, string Revision,
    string DialogueText, string AdditionalPrompt, bool CanUndo,
    IReadOnlyDictionary<string, string>? NativeFields = null);

/// <summary>An exact replacement guarded by the displayed selection and revision.</summary>
public sealed record EditorDocumentEditRequest(string SelectionToken, string ExpectedRevision, string AdditionalPrompt);

/// <summary>The verified document after an editor operation.</summary>
public sealed record EditorDocumentEditResult(EditorDocumentSnapshot Selection, bool Changed);

/// <summary>Shared main-thread editor transactions. Features own the meaning of their directives.</summary>
public interface IEditorDocumentService
{
    /// <summary>Read the active line only when its official input is synchronized.</summary>
    ModResult<EditorDocumentSnapshot> ReadSelection();
    /// <summary>Replace the additional prompt with exact readback and guarded rollback.</summary>
    ModResult<EditorDocumentEditResult> Replace(EditorDocumentEditRequest request);
    /// <summary>Undo the most recent shared transaction if its line and text still match.</summary>
    ModResult<EditorDocumentEditResult> Undo();
}
