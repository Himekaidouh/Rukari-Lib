using AzureArchive.VideoTools.Api;
using AzureArchive.VideoTools.Core;

namespace AzureArchive.VideoTools.Interop;

internal sealed class CharacterActionService : ICharacterActionService
{
    private readonly int _mainThreadId = Environment.CurrentManagedThreadId;
    private long _sequence;

    public CharacterActionService(RuntimeCapabilityService capabilities)
    {
        capabilities.Bound(
            "Player.CharacterAction.Enqueue",
            "public Character.EnqueueAction(CharacterAction) metadata bound; live proof pending");
    }

    public ApiResult<CharacterActionExecutionSnapshot> EnqueueOnMainThread(
        CharacterActionRequest request)
    {
        if (Environment.CurrentManagedThreadId != _mainThreadId)
        {
            return ApiResult<CharacterActionExecutionSnapshot>.Fail(
                "Character actions may only execute on the Unity main thread.");
        }

        if (request is null)
        {
            return ApiResult<CharacterActionExecutionSnapshot>.Fail(
                "Character action request is required.");
        }

        if (request.PublicSlot is < 1 or > 5)
        {
            return ApiResult<CharacterActionExecutionSnapshot>.Fail(
                "Physical character slot must be between 1 and 5.");
        }

        if (!Enum.IsDefined(typeof(CharacterActionKind), request.Action))
        {
            return ApiResult<CharacterActionExecutionSnapshot>.Fail(
                "Character action is invalid.");
        }

        if (!Enum.IsDefined(typeof(CharacterCommandSource), request.Source))
        {
            return ApiResult<CharacterActionExecutionSnapshot>.Fail(
                "Character command source is invalid.");
        }

        try
        {
            Test? player = Test.Instance;
            if (ReferenceEquals(player, null))
            {
                return ApiResult<CharacterActionExecutionSnapshot>.Fail(
                    "Story player is not active.");
            }

            var slots = player.slots;
            if (ReferenceEquals(slots, null))
            {
                return ApiResult<CharacterActionExecutionSnapshot>.Fail(
                    "Story player slots are unavailable.");
            }

            int arrayIndex = request.PublicSlot - 1;
            if (arrayIndex >= slots.Length)
            {
                return ApiResult<CharacterActionExecutionSnapshot>.Fail(
                    "Physical character slot is outside the live slot array.");
            }

            Character? character = slots[arrayIndex];
            if (ReferenceEquals(character, null))
            {
                return ApiResult<CharacterActionExecutionSnapshot>.Fail(
                    $"Physical character slot {request.PublicSlot} is empty.");
            }

            string occupantIdentifier = character.identifier ?? string.Empty;
            if (string.IsNullOrWhiteSpace(occupantIdentifier))
            {
                return ApiResult<CharacterActionExecutionSnapshot>.Fail(
                    "Character occupant identifier is unavailable.");
            }

            character.EnqueueAction((global::CharacterAction)(int)request.Action);
            long sequence = ++_sequence;
            return ApiResult<CharacterActionExecutionSnapshot>.Ok(
                new CharacterActionExecutionSnapshot(
                    sequence,
                    request.PublicSlot,
                    occupantIdentifier,
                    request.Action,
                    request.Source));
        }
        catch (Exception ex)
        {
            return ApiResult<CharacterActionExecutionSnapshot>.Fail(
                $"Character action failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
