using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Characters;

public sealed record SlotPendingEntry(
    string SceneIdentity,
    int PublicSlot,
    CharacterTransformCommand Command,
    long CreatedWindowSequence);

/// <summary>
/// Pure-managed store for pending empty-slot transforms. Entries are keyed by
/// physical slot, replaced last-write-wins, consumed exactly once, expire
/// after a bounded number of observed scene windows without consumption, and
/// are cleared together with the rest of the runtime session state.
/// </summary>
public sealed class SlotPendingStore
{
    public const int DefaultExpiryWindowCount = 16;

    private readonly Dictionary<int, SlotPendingEntry> _entries = new();
    private readonly int _expiryWindowCount;

    public SlotPendingStore(int expiryWindowCount = DefaultExpiryWindowCount)
    {
        if (expiryWindowCount < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expiryWindowCount),
                "The pending expiry window count must be positive.");
        }

        _expiryWindowCount = expiryWindowCount;
    }

    public int Count => _entries.Count;

    public Result<SlotPendingEntry> Store(
        string sceneIdentity,
        CharacterTransformCommand command,
        long createdWindowSequence)
    {
        if (string.IsNullOrWhiteSpace(sceneIdentity))
        {
            return Result<SlotPendingEntry>.Fail(
                "Scene identity is required to store a slot pending transform.");
        }

        if (command.PublicSlot < CharacterTransformCommandValidator.MinimumPublicSlot
            || command.PublicSlot > CharacterTransformCommandValidator.MaximumPublicSlot)
        {
            return Result<SlotPendingEntry>.Fail(
                "Physical character slot must be between 1 and 5.");
        }

        var entry = new SlotPendingEntry(
            sceneIdentity,
            command.PublicSlot,
            command,
            createdWindowSequence);
        _entries[command.PublicSlot] = entry;
        return Result<SlotPendingEntry>.Ok(entry);
    }

    public bool TryPeek(int publicSlot, out SlotPendingEntry entry) =>
        _entries.TryGetValue(publicSlot, out entry!);

    /// <summary>Removes and returns the pending entry for the slot, if any.</summary>
    public bool TryConsume(int publicSlot, out SlotPendingEntry entry)
    {
        if (_entries.TryGetValue(publicSlot, out entry!)
            && Remove(publicSlot))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Drops entries that were stored more than the configured number of
    /// observed scene windows ago and were never consumed.
    /// </summary>
    public IReadOnlyList<SlotPendingEntry> ExpireBefore(long currentWindowSequence)
    {
        List<SlotPendingEntry>? expired = null;
        foreach (KeyValuePair<int, SlotPendingEntry> pair in _entries)
        {
            if (currentWindowSequence - pair.Value.CreatedWindowSequence
                > _expiryWindowCount)
            {
                expired ??= new List<SlotPendingEntry>();
                expired.Add(pair.Value);
            }
        }

        if (expired != null)
        {
            foreach (SlotPendingEntry entry in expired)
            {
                _entries.Remove(entry.PublicSlot);
            }
        }

        return expired?.ToArray() ?? (IReadOnlyList<SlotPendingEntry>)Array.Empty<SlotPendingEntry>();
    }

    public void Clear() => _entries.Clear();

    private bool Remove(int publicSlot) => _entries.Remove(publicSlot);
}
