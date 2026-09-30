namespace AzureArchive.VideoTools.Core.Commands;

public enum ManagedDispatchPhase
{
    Update = 0,
    LateUpdate = 1
}

/// <summary>
/// Keeps Unity-facing dispatch outside native callbacks while allowing a
/// verified scene command to settle before the same frame is rendered.
/// </summary>
public readonly record struct ManagedDispatchSchedule(
    long EarliestUpdate,
    ManagedDispatchPhase EarliestPhase)
{
    public static ManagedDispatchSchedule SameFrameLateUpdate(long observedUpdate)
    {
        if (observedUpdate < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(observedUpdate));
        }
        return new ManagedDispatchSchedule(
            observedUpdate,
            ManagedDispatchPhase.LateUpdate);
    }

    public static ManagedDispatchSchedule NextUpdate(long observedUpdate)
    {
        if (observedUpdate < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(observedUpdate));
        }
        return new ManagedDispatchSchedule(
            checked(observedUpdate + 1),
            ManagedDispatchPhase.Update);
    }

    public bool IsDue(long currentUpdate, ManagedDispatchPhase currentPhase)
    {
        if (currentUpdate > EarliestUpdate)
        {
            return true;
        }

        return currentUpdate == EarliestUpdate
            && currentPhase >= EarliestPhase;
    }
}
