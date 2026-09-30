using AzureArchive.VideoTools.Api;
using AzureArchive.VideoTools.Core;
using AzureArchive.VideoTools.Core.Playback;

namespace AzureArchive.VideoTools.Interop;

internal sealed class PlayerRuntimeContextService : IPlayerRuntimeContextService
{
    private readonly RuntimeCapabilityService _capabilities;
    private readonly int _mainThreadId = Environment.CurrentManagedThreadId;

    public PlayerRuntimeContextService(RuntimeCapabilityService capabilities)
    {
        _capabilities = capabilities;
    }

    public ApiResult<PlayerRuntimeContextSnapshot> ReadOnMainThread()
    {
        if (Environment.CurrentManagedThreadId != _mainThreadId)
        {
            return ApiResult<PlayerRuntimeContextSnapshot>.Fail(
                "Player runtime context may only be read on the Unity main thread.");
        }

        try
        {
            Test? player = Test.Instance;
            if (ReferenceEquals(player, null))
            {
                _capabilities.Verified(
                    "Player.RuntimeContextRead",
                    "Test.Instance read succeeded and returned no active player; wrapperCached=false");
                return ApiResult<PlayerRuntimeContextSnapshot>.Ok(
                    PlayerRuntimeContextSnapshot.Unavailable);
            }

            bool previewMode = player.previewMode;
            _capabilities.Verified(
                "Player.RuntimeContextRead",
                "Test.previewMode read succeeded on the Unity main thread; wrapperCached=false");
            return ApiResult<PlayerRuntimeContextSnapshot>.Ok(
                PlayerRuntimeContextSnapshot.FromPreviewMode(previewMode));
        }
        catch (Exception ex)
        {
            string detail =
                $"Test.previewMode read failed: {ex.GetType().Name}: {ex.Message}";
            _capabilities.Degraded("Player.RuntimeContextRead", detail);
            return ApiResult<PlayerRuntimeContextSnapshot>.Fail(detail);
        }
    }
}
