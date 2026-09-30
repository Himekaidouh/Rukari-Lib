using System;
using AzureArchive.VideoTools.Api;
using AzureArchive.VideoTools.Core;

namespace AzureArchive.VideoTools.Interop;

internal sealed class ScenarioPlaybackService : IScenarioPlaybackService
{
    private readonly RuntimeCapabilityService _capabilities;

    public ScenarioPlaybackService(RuntimeCapabilityService capabilities)
    {
        _capabilities = capabilities;
    }

    public PlaybackSnapshot? Current { get; private set; }
    public event Action<PlaybackSnapshot>? ScenarioAdvanced;

    public void Observe(Test owner)
    {
        UILabel? label = InteropMemberAccess.Get<UILabel>(owner, "text");
        int row = InteropMemberAccess.Get<int>(owner, "cur");
        bool hasDialog = InteropMemberAccess.Get<bool>(owner, "hasDialog");
        string visible = label != null ? InteropMemberAccess.Get<string>(label, "text") ?? string.Empty : string.Empty;
        PlaybackSnapshot snapshot = new(row, hasDialog, visible);
        Current = snapshot;
        _capabilities.Verified("Player.Advance", "Test.cur/hasDialog/text read after AdvanceScenario");

        try
        {
            ScenarioAdvanced?.Invoke(snapshot);
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"Playback API subscriber failed: {PatchGuard.Describe(ex)}");
        }
    }
}
