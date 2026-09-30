using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Commands;

public sealed record EditorCommandUndoRecord(
    string SelectionToken,
    string BeforeText,
    string BeforeRevisionSha256,
    string AppliedText,
    string AppliedRevisionSha256);

public sealed class EditorCommandUndoStore
{
    private readonly object _gate = new();
    private EditorCommandUndoRecord? _record;

    public Result<EditorCommandUndoRecord> Arm(
        string selectionToken,
        string beforeText,
        string appliedText)
    {
        if (string.IsNullOrWhiteSpace(selectionToken))
        {
            return Result<EditorCommandUndoRecord>.Fail(
                "An editor-command undo record requires a selection token.");
        }

        ArgumentNullException.ThrowIfNull(beforeText);
        ArgumentNullException.ThrowIfNull(appliedText);
        if (string.Equals(beforeText, appliedText, StringComparison.Ordinal))
        {
            return Result<EditorCommandUndoRecord>.Fail(
                "An editor-command undo record requires an actual text change.");
        }

        var record = new EditorCommandUndoRecord(
            selectionToken,
            beforeText,
            EditorCommandDocumentComposer.Revision(beforeText),
            appliedText,
            EditorCommandDocumentComposer.Revision(appliedText));
        lock (_gate)
        {
            _record = record;
        }

        return Result<EditorCommandUndoRecord>.Ok(record);
    }

    public Result<EditorCommandUndoRecord> Require(
        string selectionToken,
        string currentRevisionSha256)
    {
        lock (_gate)
        {
            if (_record == null)
            {
                return Result<EditorCommandUndoRecord>.Fail(
                    "There is no AAVT editor-command change to undo in this session.");
            }

            if (!string.Equals(
                    _record.SelectionToken,
                    selectionToken,
                    StringComparison.Ordinal))
            {
                return Result<EditorCommandUndoRecord>.Fail(
                    "The last AAVT change belongs to another scene.");
            }

            if (!string.Equals(
                    _record.AppliedRevisionSha256,
                    currentRevisionSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Result<EditorCommandUndoRecord>.Fail(
                    "The additional-prompt text changed after the last AAVT apply.");
            }

            return Result<EditorCommandUndoRecord>.Ok(_record);
        }
    }

    public bool CanUndo(string selectionToken, string currentRevisionSha256)
    {
        Result<EditorCommandUndoRecord> result = Require(
            selectionToken,
            currentRevisionSha256);
        return result.Success;
    }

    public void Consume(EditorCommandUndoRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            if (ReferenceEquals(_record, record))
            {
                _record = null;
            }
        }
    }
}
