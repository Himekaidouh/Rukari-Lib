extern alias unitycore;

using System;
using MonoBehaviour = unitycore::UnityEngine.MonoBehaviour;

namespace AzureArchive.VideoTools.Runtime;

public sealed class PlayerCommandObservationBehaviour : MonoBehaviour
{
    public PlayerCommandObservationBehaviour(IntPtr pointer)
        : base(pointer)
    {
    }

    public void Update()
    {
        PlayerCommandObservationRuntime.Update();
    }

    public void LateUpdate()
    {
        PlayerCommandObservationRuntime.LateUpdate();
    }

    // The runtime's stall detector measures gaps between its own updates, so an inactive
    // heartbeat must be distinguishable from a blocked main thread: these two events are rare
    // and make that difference explicit in the log.
    public void OnEnable()
    {
        PlayerCommandObservationRuntime.ReportHeartbeatAvailability(true);
    }

    public void OnDisable()
    {
        PlayerCommandObservationRuntime.ReportHeartbeatAvailability(false);
    }

    public void OnDestroy()
    {
        PlayerCommandObservationRuntime.StopCharacterPresets("heartbeat-destroyed");
    }
}
