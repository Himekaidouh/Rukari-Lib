namespace AzureArchive.VideoTools.Core.VisualEditor;

/// <summary>
/// Remembers only a UI target for the currently observed editor line. A read gap does not
/// erase the target; observing a different line does. It carries no edit token or draft.
/// </summary>
public sealed class VisualEditorSlotPreference
{
    private string _contextId = string.Empty;
    private int _publicSlot;

    public int Restore(string? contextId)
    {
        if (string.IsNullOrEmpty(contextId)) return 0;
        if (!string.Equals(_contextId, contextId, StringComparison.Ordinal))
        {
            _contextId = contextId;
            _publicSlot = 0;
        }
        return _publicSlot;
    }

    public void Remember(string? contextId, int publicSlot)
    {
        if (string.IsNullOrEmpty(contextId) || publicSlot is < 1 or > 5) return;
        _contextId = contextId;
        _publicSlot = publicSlot;
    }
}
