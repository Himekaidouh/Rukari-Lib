using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Characters;

public sealed class CharacterTransformBaselineStore : ICharacterTransformBaselineStore
{
    private readonly Dictionary<int, CharacterTransformBaseline> _baselines = new();
    private string? _sceneIdentity;

    public Result<CharacterTransformBaseline> Capture(
        string sceneIdentity,
        int publicSlot,
        string occupantIdentifier,
        CharacterTransformState state)
    {
        Result identityValidation = ValidateIdentity(
            sceneIdentity,
            publicSlot,
            occupantIdentifier);
        if (!identityValidation.Success)
        {
            return Result<CharacterTransformBaseline>.Fail(identityValidation.Error);
        }

        if (!state.Position.IsFinite || !state.LocalEulerAngles.IsFinite)
        {
            return Result<CharacterTransformBaseline>.Fail(
                "Character transform baseline values must be finite.");
        }

        EnsureScene(sceneIdentity);
        if (_baselines.TryGetValue(publicSlot, out CharacterTransformBaseline? existing)
            && string.Equals(
                existing.OccupantIdentifier,
                occupantIdentifier,
                StringComparison.Ordinal))
        {
            return Result<CharacterTransformBaseline>.Ok(existing);
        }

        var baseline = new CharacterTransformBaseline(
            sceneIdentity,
            publicSlot,
            occupantIdentifier,
            state);
        _baselines[publicSlot] = baseline;
        return Result<CharacterTransformBaseline>.Ok(baseline);
    }

    public Result<CharacterTransformBaseline> Get(
        string sceneIdentity,
        int publicSlot,
        string occupantIdentifier)
    {
        Result identityValidation = ValidateIdentity(
            sceneIdentity,
            publicSlot,
            occupantIdentifier);
        if (!identityValidation.Success)
        {
            return Result<CharacterTransformBaseline>.Fail(identityValidation.Error);
        }

        if (!string.Equals(_sceneIdentity, sceneIdentity, StringComparison.Ordinal))
        {
            return Result<CharacterTransformBaseline>.Fail(
                "No character transform baseline exists for the current scene.");
        }

        if (!_baselines.TryGetValue(publicSlot, out CharacterTransformBaseline? baseline))
        {
            return Result<CharacterTransformBaseline>.Fail(
                "No character transform baseline exists for the requested slot.");
        }

        if (!string.Equals(
                baseline.OccupantIdentifier,
                occupantIdentifier,
                StringComparison.Ordinal))
        {
            return Result<CharacterTransformBaseline>.Fail(
                "Character transform baseline occupant does not match the current slot.");
        }

        return Result<CharacterTransformBaseline>.Ok(baseline);
    }

    public void Clear()
    {
        _sceneIdentity = null;
        _baselines.Clear();
    }

    private void EnsureScene(string sceneIdentity)
    {
        if (string.Equals(_sceneIdentity, sceneIdentity, StringComparison.Ordinal))
        {
            return;
        }

        _sceneIdentity = sceneIdentity;
        _baselines.Clear();
    }

    private static Result ValidateIdentity(
        string sceneIdentity,
        int publicSlot,
        string occupantIdentifier)
    {
        if (string.IsNullOrWhiteSpace(sceneIdentity))
        {
            return Result.Fail("Scene identity is required for a character transform baseline.");
        }

        if (publicSlot < CharacterTransformCommandValidator.MinimumPublicSlot
            || publicSlot > CharacterTransformCommandValidator.MaximumPublicSlot)
        {
            return Result.Fail("Physical character slot must be between 1 and 5.");
        }

        if (string.IsNullOrWhiteSpace(occupantIdentifier))
        {
            return Result.Fail("Character occupant identifier is required for a baseline.");
        }

        return Result.Ok();
    }
}
