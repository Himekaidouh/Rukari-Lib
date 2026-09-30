using AzureArchive.VideoTools.Api;
using AzureArchive.VideoTools.Core;
using AzureArchive.VideoTools.Core.VisualEditor;

namespace AzureArchive.VideoTools.Interop;

/// <summary>
/// Takes managed snapshots of the official player's currently retained screen
/// texts. IL2CPP wrappers are resolved and discarded inside each main-thread
/// call; no wrapper crosses the API boundary.
/// </summary>
internal sealed class ScreenTextRuntimeService : IScreenTextRuntimeService
{
    private const int MaximumVisibleScreenTexts = 64;

    private readonly RuntimeCapabilityService _capabilities;
    private readonly int _mainThreadId = Environment.CurrentManagedThreadId;

    public ScreenTextRuntimeService(RuntimeCapabilityService capabilities)
    {
        _capabilities = capabilities;
        capabilities.Bound(
            "Player.ScreenTextRead",
            "main-thread Test.currentSTs managed snapshot service bound; wrapperCached=false");
    }

    public ApiResult<IReadOnlyList<VisibleScreenTextSnapshot>> ReadVisibleOnMainThread()
    {
        if (Environment.CurrentManagedThreadId != _mainThreadId)
        {
            return ApiResult<IReadOnlyList<VisibleScreenTextSnapshot>>.Fail(
                "Visible screen texts may only be read on the Unity main thread.");
        }

        try
        {
            Test? player = Test.Instance;
            if (ReferenceEquals(player, null))
            {
                return ApiResult<IReadOnlyList<VisibleScreenTextSnapshot>>.Ok(
                    Array.Empty<VisibleScreenTextSnapshot>());
            }

            var active = player.currentSTs;
            if (ReferenceEquals(active, null))
            {
                return ApiResult<IReadOnlyList<VisibleScreenTextSnapshot>>.Ok(
                    Array.Empty<VisibleScreenTextSnapshot>());
            }

            int count = Math.Min(active.Count, MaximumVisibleScreenTexts);
            var snapshots = new List<VisibleScreenTextSnapshot>(count);
            for (int index = 0; index < count; index++)
            {
                ScenarioAnimation.ScreenTextAnimation? animation = active[index];
                if (ReferenceEquals(animation, null))
                {
                    continue;
                }

                var position = animation.position;
                int fontSize = animation.fontSize;
                if (!float.IsFinite(position.x)
                    || !float.IsFinite(position.y)
                    || fontSize < ScreenTextDirectiveCodec.MinimumFontSize
                    || fontSize > ScreenTextDirectiveCodec.MaximumFontSize)
                {
                    continue;
                }

                ScreenTextRevealMode revealMode = ReadRevealMode(
                    animation.fadeInType.ToString());
                snapshots.Add(new VisibleScreenTextSnapshot(
                    new ScreenTextDirective(
                        position.x,
                        position.y,
                        animation.isMiddle
                            ? ScreenTextAlignment.Center
                            : ScreenTextAlignment.Left,
                        revealMode,
                        fontSize),
                    animation.targetTxt ?? string.Empty));
            }

            _capabilities.Verified(
                "Player.ScreenTextRead",
                $"Test.currentSTs read on main thread; active={active.Count}; snapshots={snapshots.Count}; wrapperCached=false");
            return ApiResult<IReadOnlyList<VisibleScreenTextSnapshot>>.Ok(
                snapshots.AsReadOnly());
        }
        catch (Exception ex)
        {
            string detail =
                $"Test.currentSTs read failed: {ex.GetType().Name}: {ex.Message}";
            _capabilities.Degraded("Player.ScreenTextRead", detail);
            return ApiResult<IReadOnlyList<VisibleScreenTextSnapshot>>.Fail(detail);
        }
    }

    private static ScreenTextRevealMode ReadRevealMode(string value) =>
        value?.ToUpperInvariant() switch
        {
            "SMOOTH" => ScreenTextRevealMode.Smooth,
            "SERIAL" => ScreenTextRevealMode.Serial,
            _ => ScreenTextRevealMode.Instant
        };
}
