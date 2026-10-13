using AzureArchive.VideoTools.Core.Playback;

namespace AzureArchive.VideoTools.Core.Commands;

/// <summary>Queued mutations belong to the mode that authorized them, including retries.</summary>
public static class ManagedDispatchContextPolicy
{
    public static bool CanDispatch(PlayerRuntimeMode origin, PlayerRuntimeContextSnapshot? current)
        => origin is PlayerRuntimeMode.EditorPreview or PlayerRuntimeMode.Playback
            && current?.PlayerAvailable == true
            && current.Mode == origin
            && current.PreviewMode == (origin == PlayerRuntimeMode.EditorPreview);
}
