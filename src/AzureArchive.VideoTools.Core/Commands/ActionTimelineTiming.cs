using AzureArchive.VideoTools.Core.Characters;
using AzureArchive.VideoTools.Core.Cameras;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Commands;

/// <summary>Durations of parallel finite actions. Persistent/looping overlays never hold the next card.</summary>
public static class ActionTimelineTiming
{
    public static double Seconds(CharacterPresetCommand command) => command.Kind == CharacterPresetKind.Headbutt
        ? command.DurationMilliseconds / 1000d
        : command.Cycles == 0 ? 0d : command.Cycles / (double)command.FrequencyHz;

    public static double Seconds(CharacterTransformCommand command) =>
        command.Operation == CharacterTransformOperation.Set
        && command.X is null && command.Y is null && command.RotationDegrees is null
        && command.RotationXDegrees is null && command.FlipX is null ? 0d : command.DurationMilliseconds / 1000d;

    public static Result<double> ParallelDuration(IEnumerable<string> canonicalDirectives)
    {
        ArgumentNullException.ThrowIfNull(canonicalDirectives);
        double longest = 0;
        foreach (string line in canonicalDirectives)
        {
            double duration = 0;
            if (line.StartsWith(CharacterPresetCommandFamilyCompiler.CanonicalRootToken + ";", StringComparison.Ordinal))
            {
                var parsed = new CharacterPresetDirectiveParser().Parse(line);
                if (!parsed.Success || parsed.Value is null) return Result<double>.Fail(parsed.Error);
                duration = Seconds(parsed.Value);
            }
            else if (line.StartsWith("#char;", StringComparison.Ordinal))
            {
                var parsed = new CharacterTransformDirectiveParser().Parse(line);
                if (!parsed.Success || parsed.Value is null) return Result<double>.Fail(parsed.Error);
                duration = Seconds(parsed.Value);
            }
            else if (line.StartsWith("#camera;", StringComparison.Ordinal))
            {
                var parsed = new SceneCameraDirectiveParser().Parse(line);
                if (!parsed.Success || parsed.Value is null) return Result<double>.Fail(parsed.Error);
                duration = parsed.Value.DurationMilliseconds / 1000d;
            }
            // charPending is instantaneous; Spine tracks may be indefinite and remain outside this gate.
            if (!double.IsFinite(duration) || duration < 0 || duration > 1000d)
                return Result<double>.Fail("Action duration is outside the supported finite range.");
            longest = Math.Max(longest, duration);
        }
        return Result<double>.Ok(longest);
    }
}

public enum ActionTimelineStatus { Idle, AwaitingDispatch, Running, Completed, DispatchTimeout, CompletionTimeout, Cancelled }

/// <summary>One bounded action lease. Deadlines start at actual execution, not compilation or indexing.</summary>
public sealed class ActionTimelineCompletionGate
{
    public const double DispatchTimeoutSeconds = 10d;
    public const double CompletionGraceSeconds = 3d;
    public long Sequence { get; private set; }
    public ActionTimelineStatus Status { get; private set; }
    public double DueAt { get; private set; }
    public bool IsSealed => _sealed;
    private double _dispatchBy;
    private bool _sealed;

    public void Arm(long sequence, double now)
    {
        if (sequence <= 0 || !double.IsFinite(now)) throw new ArgumentOutOfRangeException(nameof(sequence));
        Sequence = sequence; Status = ActionTimelineStatus.AwaitingDispatch;
        _dispatchBy = now + DispatchTimeoutSeconds; DueAt = now; _sealed = false;
    }

    public bool BeginDelivery(long sequence, double now)
    {
        if (Sequence != sequence || Status != ActionTimelineStatus.AwaitingDispatch || !double.IsFinite(now)) return false;
        if (now >= _dispatchBy) { Status = ActionTimelineStatus.DispatchTimeout; return false; }
        Status = ActionTimelineStatus.Running; DueAt = now;
        return true;
    }

    public bool AddAction(long sequence, double now, double seconds)
    {
        if (Sequence != sequence || Status != ActionTimelineStatus.Running || _sealed
            || !double.IsFinite(now) || !double.IsFinite(seconds) || seconds < 0 || seconds > 1000) return false;
        DueAt = Math.Max(DueAt, now + seconds);
        return true;
    }

    public void Seal(long sequence) { if (sequence == Sequence && Status == ActionTimelineStatus.Running) _sealed = true; }

    public ActionTimelineStatus Tick(double now, bool nativeActionsPending)
    {
        if (!double.IsFinite(now)) { Cancel(); return Status; }
        if (Status == ActionTimelineStatus.AwaitingDispatch && now >= _dispatchBy) Status = ActionTimelineStatus.DispatchTimeout;
        else if (Status == ActionTimelineStatus.Running && _sealed && now >= DueAt)
        {
            if (!nativeActionsPending) Status = ActionTimelineStatus.Completed;
            else if (now >= DueAt + CompletionGraceSeconds) Status = ActionTimelineStatus.CompletionTimeout;
        }
        return Status;
    }

    public void Cancel() => Status = ActionTimelineStatus.Cancelled;
}
