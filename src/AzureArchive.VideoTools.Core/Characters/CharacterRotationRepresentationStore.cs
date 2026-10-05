namespace AzureArchive.VideoTools.Core.Characters;

/// <summary>
/// Scalar Euler representation hints for one live character root per slot.
/// A hint grants no write authority: Observe always returns an orientation
/// equivalent to the current measured Euler values, including external writes.
/// </summary>
public sealed class CharacterRotationRepresentationStore
{
    private readonly Dictionary<int, Entry> _entries = new();

    public CharacterVector3 Observe(int publicSlot, long characterId, long transformId,
        string occupant, CharacterVector3 observed)
    {
        if (!ValidIdentity(publicSlot, characterId, transformId, occupant) || !observed.IsFinite)
        {
            _entries.Remove(publicSlot);
            return observed;
        }

        CharacterVector3 result = observed;
        if (_entries.TryGetValue(publicSlot, out Entry? previous)
            && previous.CharacterId == characterId && previous.TransformId == transformId
            && string.Equals(previous.Occupant, occupant, StringComparison.Ordinal))
        {
            result = CharacterEulerRepresentation.NearestEquivalent(observed, previous.PhysicalEuler);
        }

        _entries[publicSlot] = new Entry(characterId, transformId, occupant, result);
        return result;
    }

    public void Remember(int publicSlot, long characterId, long transformId,
        string occupant, CharacterVector3 physicalTarget)
    {
        if (!ValidIdentity(publicSlot, characterId, transformId, occupant) || !physicalTarget.IsFinite)
        {
            _entries.Remove(publicSlot);
            return;
        }

        _entries[publicSlot] = new Entry(characterId, transformId, occupant, physicalTarget);
    }

    public void Remove(int publicSlot) => _entries.Remove(publicSlot);
    public void Clear() => _entries.Clear();

    private static bool ValidIdentity(int slot, long characterId, long transformId, string occupant) =>
        slot is >= CharacterTransformCommandValidator.MinimumPublicSlot and <= CharacterTransformCommandValidator.MaximumPublicSlot
        && characterId != 0 && transformId != 0 && !string.IsNullOrWhiteSpace(occupant);

    private sealed record Entry(long CharacterId, long TransformId, string Occupant, CharacterVector3 PhysicalEuler);
}
