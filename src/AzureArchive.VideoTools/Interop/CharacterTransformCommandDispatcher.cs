using AzureArchive.VideoTools.Api;
using AzureArchive.VideoTools.Core;
using AzureArchive.VideoTools.Core.Characters;
using AzureArchive.VideoTools.Core.Commands;

namespace AzureArchive.VideoTools.Interop;

internal sealed class CharacterTransformCommandDispatcher : ICharacterTransformCommandDispatcher
{
    private readonly CharacterTransformDirectiveParser _parser = new();
    private readonly CharacterTransformService _service;
    private long _sequence;

    public CharacterTransformCommandDispatcher(
        RuntimeCapabilityService capabilities,
        CharacterTransformService service)
    {
        _service = service;
        capabilities.Verified(
            "Player.CharacterTransform.Dispatch",
            "0.7.15 manual slot selection and ordered dispatch verified by user");
        capabilities.Bound(
            "Player.CharacterTransform.SlotPending",
            "character.slotPending/v1 family bound; empty-slot storage and arrival consumption proof pending");
    }

    public ApiResult<CharacterTransformDispatchSnapshot> DispatchOnMainThread(
        CharacterTransformDispatchRequest request)
    {
        if (request is null)
        {
            return ApiResult<CharacterTransformDispatchSnapshot>.Fail(
                "Character transform dispatch request is required.");
        }

        if (!Enum.IsDefined(typeof(CharacterCommandSource), request.Source))
        {
            return ApiResult<CharacterTransformDispatchSnapshot>.Fail(
                "Character transform command source is invalid.");
        }

        if (request.PublicSlotOverride is < 1 or > 5)
        {
            return ApiResult<CharacterTransformDispatchSnapshot>.Fail(
                "Manual physical character slot must be between 1 and 5.");
        }

        var parsed = _parser.Parse(request.Directive);
        if (!parsed.Success || parsed.Value == null)
        {
            return ApiResult<CharacterTransformDispatchSnapshot>.Fail(parsed.Error);
        }

        CharacterTransformCommand command = parsed.Value;
        if (request.PublicSlotOverride.HasValue)
        {
            command = command with { PublicSlot = request.PublicSlotOverride.Value };
        }

        ApiResult<CharacterTransformExecutionSnapshot> executed =
            request.Source == CharacterCommandSource.PlaybackSidecar
                ? _service.ReplayCommandOnMainThread(
                    request.SceneIdentity,
                    command,
                    request.OriginIdentity ?? string.Empty)
                : _service.ExecuteCommandWithOriginOnMainThread(
                    request.SceneIdentity,
                    command,
                    request.OriginIdentity ?? string.Empty);
        if (!executed.Success || executed.Value == null)
        {
            return ApiResult<CharacterTransformDispatchSnapshot>.Fail(executed.Error);
        }

        long sequence = ++_sequence;
        return ApiResult<CharacterTransformDispatchSnapshot>.Ok(
            new CharacterTransformDispatchSnapshot(
                sequence,
                request.Source,
                executed.Value));
    }

    public ApiResult<CharacterTransformInheritedStartSnapshot> ApplyInheritedStartOnMainThread(
        string sceneIdentity,
        int publicSlot,
        string expectedOccupantIdentifier,
        PreviewChainSlotState inheritedStart)
    {
        return _service.ApplyInheritedStartOnMainThread(
            sceneIdentity,
            publicSlot,
            expectedOccupantIdentifier,
            inheritedStart,
            string.Empty);
    }

    internal ApiResult<CharacterTransformInheritedStartSnapshot>
        ApplyInheritedStartWithOriginOnMainThread(
            string sceneIdentity,
            int publicSlot,
            string expectedOccupantIdentifier,
            PreviewChainSlotState inheritedStart,
            string originIdentity) =>
        _service.ApplyInheritedStartOnMainThread(
            sceneIdentity,
            publicSlot,
            expectedOccupantIdentifier,
            inheritedStart,
            originIdentity);

    public ApiResult<SlotPendingStoredSnapshot> StoreSlotPendingOnMainThread(
        string sceneIdentity,
        string canonicalDirective,
        long createdWindowSequence)
    {
        return _service.StoreSlotPendingOnMainThread(
            sceneIdentity,
            canonicalDirective,
            createdWindowSequence);
    }

    public ApiResult<IReadOnlyList<SlotPendingOutcome>> ScanSlotPendingsOnMainThread(
        string? currentSceneIdentity,
        long currentWindowSequence)
    {
        return _service.ScanSlotPendingsOnMainThread(
            currentSceneIdentity,
            currentWindowSequence);
    }

    public ApiResult<bool> ClearSlotPendings()
    {
        return _service.ClearSlotPendings();
    }
}
