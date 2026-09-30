using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Characters;

/// <summary>
/// First official transform observed for one live character instance in one
/// continuous project lineage. Unlike a scene-entry replay baseline, this
/// value intentionally survives ordinary scene changes inside that lineage.
/// </summary>
public sealed record CharacterTransformOriginBaseline(
    string LineageIdentity,
    int PublicSlot,
    string OccupantIdentifier,
    long ManagedCharacterInstanceId,
    CharacterTransformState State)
{
    public bool HasExactLineage => !string.IsNullOrEmpty(LineageIdentity);
}

/// <summary>
/// Holds one active inheritance origin per physical slot. An origin captured
/// before lineage metadata is available uses an empty lineage and can later be
/// claimed by one exact lineage without replacing its numeric state.
/// </summary>
public sealed class CharacterTransformOriginBaselineStore
{
    private readonly Dictionary<int, CharacterTransformOriginBaseline> _origins = new();

    public Result<CharacterTransformOriginBaseline> Capture(
        string lineageIdentity,
        int publicSlot,
        string occupantIdentifier,
        long managedCharacterInstanceId,
        CharacterTransformState state)
    {
        Result identityValidation = ValidateIdentity(
            lineageIdentity,
            publicSlot,
            occupantIdentifier,
            managedCharacterInstanceId,
            out string normalizedLineage);
        if (!identityValidation.Success)
        {
            return Result<CharacterTransformOriginBaseline>.Fail(
                identityValidation.Error);
        }

        if (state == null
            || !state.Position.IsFinite
            || !state.LocalEulerAngles.IsFinite)
        {
            return Result<CharacterTransformOriginBaseline>.Fail(
                "Character transform origin values must be finite.");
        }

        if (_origins.TryGetValue(
                publicSlot,
                out CharacterTransformOriginBaseline? existing)
            && SameLiveCharacter(
                existing,
                occupantIdentifier,
                managedCharacterInstanceId))
        {
            if (string.IsNullOrEmpty(normalizedLineage))
            {
                // Missing metadata must never downgrade or replace an already
                // exact origin for the same live character.
                return Result<CharacterTransformOriginBaseline>.Ok(existing);
            }

            if (string.IsNullOrEmpty(existing.LineageIdentity))
            {
                var claimed = existing with { LineageIdentity = normalizedLineage };
                _origins[publicSlot] = claimed;
                return Result<CharacterTransformOriginBaseline>.Ok(claimed);
            }

            if (string.Equals(
                    existing.LineageIdentity,
                    normalizedLineage,
                    StringComparison.Ordinal))
            {
                return Result<CharacterTransformOriginBaseline>.Ok(existing);
            }
        }

        var captured = new CharacterTransformOriginBaseline(
            normalizedLineage,
            publicSlot,
            occupantIdentifier,
            managedCharacterInstanceId,
            state);
        _origins[publicSlot] = captured;
        return Result<CharacterTransformOriginBaseline>.Ok(captured);
    }

    public Result<CharacterTransformOriginBaseline> Get(
        string lineageIdentity,
        int publicSlot,
        string occupantIdentifier,
        long managedCharacterInstanceId)
    {
        Result identityValidation = ValidateIdentity(
            lineageIdentity,
            publicSlot,
            occupantIdentifier,
            managedCharacterInstanceId,
            out string normalizedLineage);
        if (!identityValidation.Success)
        {
            return Result<CharacterTransformOriginBaseline>.Fail(
                identityValidation.Error);
        }

        if (!_origins.TryGetValue(
                publicSlot,
                out CharacterTransformOriginBaseline? existing))
        {
            return Result<CharacterTransformOriginBaseline>.Fail(
                "No character transform origin exists for the requested slot.");
        }

        if (!SameLiveCharacter(
                existing,
                occupantIdentifier,
                managedCharacterInstanceId))
        {
            return Result<CharacterTransformOriginBaseline>.Fail(
                "Character transform origin does not match the current live character.");
        }

        if (!string.IsNullOrEmpty(normalizedLineage)
            && !string.Equals(
                existing.LineageIdentity,
                normalizedLineage,
                StringComparison.Ordinal))
        {
            return Result<CharacterTransformOriginBaseline>.Fail(
                "Character transform origin does not match the current lineage.");
        }

        return Result<CharacterTransformOriginBaseline>.Ok(existing);
    }

    public void Clear() => _origins.Clear();

    private static bool SameLiveCharacter(
        CharacterTransformOriginBaseline existing,
        string occupantIdentifier,
        long managedCharacterInstanceId) =>
        existing.ManagedCharacterInstanceId == managedCharacterInstanceId
        && string.Equals(
            existing.OccupantIdentifier,
            occupantIdentifier,
            StringComparison.Ordinal);

    private static Result ValidateIdentity(
        string lineageIdentity,
        int publicSlot,
        string occupantIdentifier,
        long managedCharacterInstanceId,
        out string normalizedLineage)
    {
        normalizedLineage = lineageIdentity?.Trim() ?? string.Empty;
        if (lineageIdentity == null)
        {
            return Result.Fail("Character transform lineage identity is required.");
        }

        if (publicSlot < CharacterTransformCommandValidator.MinimumPublicSlot
            || publicSlot > CharacterTransformCommandValidator.MaximumPublicSlot)
        {
            return Result.Fail("Physical character slot must be between 1 and 5.");
        }

        if (string.IsNullOrWhiteSpace(occupantIdentifier))
        {
            return Result.Fail("Character occupant identifier is required for an origin.");
        }

        if (managedCharacterInstanceId == 0)
        {
            return Result.Fail("Managed character instance identity must be non-zero.");
        }

        return Result.Ok();
    }
}
