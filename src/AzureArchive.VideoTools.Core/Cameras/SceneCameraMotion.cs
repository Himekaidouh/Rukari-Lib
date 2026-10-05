using AzureArchive.VideoTools.Core.Characters;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Cameras;

[Flags]
public enum SceneCameraMotionFields
{
    None = 0,
    OverallPosition = 1,
    OverallZoom = 2,
    BackgroundPosition = 4,
    BackgroundZoom = 8
}

/// <summary>
/// Managed animation state. Each scope and property keeps its own progress;
/// one native writer consumes the resulting composition. No native wrapper or
/// clock is retained here. Times are monotonic seconds supplied by the caller.
/// </summary>
public sealed class SceneCameraMotion
{
    private ScopeMotion _overall;
    private ScopeMotion _background;

    public SceneCameraMotion() : this(SceneCameraComposition.Default) { }

    public SceneCameraMotion(SceneCameraComposition initial)
    {
        Result validation = SceneCameraCommandValidator.ValidateComposition(initial);
        if (!validation.Success) throw new ArgumentException(validation.Error, nameof(initial));
        _overall = ScopeMotion.Immediate(initial.Overall);
        _background = ScopeMotion.Immediate(initial.Background);
    }

    private SceneCameraMotion(ScopeMotion overall, ScopeMotion background)
    {
        _overall = overall;
        _background = background;
    }

    public SceneCameraComposition Target => new(_overall.Target, _background.Target);

    public SceneCameraMotion Copy() => new(_overall, _background);

    public bool IsAnimating(double now) => _overall.IsAnimating(now) || _background.IsAnimating(now);

    public SceneCameraMotionFields ActiveFields(double now) =>
        (_overall.X.IsAnimating(now) || _overall.Y.IsAnimating(now) ? SceneCameraMotionFields.OverallPosition : 0)
        | (_overall.Zoom.IsAnimating(now) ? SceneCameraMotionFields.OverallZoom : 0)
        | (_background.X.IsAnimating(now) || _background.Y.IsAnimating(now) ? SceneCameraMotionFields.BackgroundPosition : 0)
        | (_background.Zoom.IsAnimating(now) ? SceneCameraMotionFields.BackgroundZoom : 0);

    public static SceneCameraMotionFields FieldsFor(SceneCameraCommand command)
    {
        bool position = command.Operation == SceneCameraOperation.Reset || command.X.HasValue
            || command.Y.HasValue || command.DeltaX.HasValue || command.DeltaY.HasValue;
        bool zoom = command.Operation == SceneCameraOperation.Reset || command.Zoom.HasValue || command.DeltaZoom.HasValue;
        return command.Scope == SceneCameraScope.Overall
            ? (position ? SceneCameraMotionFields.OverallPosition : 0) | (zoom ? SceneCameraMotionFields.OverallZoom : 0)
            : (position ? SceneCameraMotionFields.BackgroundPosition : 0) | (zoom ? SceneCameraMotionFields.BackgroundZoom : 0);
    }

    public Result<SceneCameraComposition> Sample(double now)
    {
        if (!double.IsFinite(now))
            return Result<SceneCameraComposition>.Fail("Camera animation time must be finite.");
        var sampled = new SceneCameraComposition(_overall.Sample(now), _background.Sample(now));
        Result validation = SceneCameraCommandValidator.ValidateComposition(sampled);
        return validation.Success
            ? Result<SceneCameraComposition>.Ok(sampled)
            : Result<SceneCameraComposition>.Fail(validation.Error);
    }

    public Result Begin(SceneCameraCommand command, SceneCameraTarget target, double now)
        => BeginCore(command, null, target, now);

    public Result BeginReplay(SceneCameraCommand command, SceneCameraTarget restore, SceneCameraTarget target, double now)
        => BeginCore(command, restore, target, now);

    private Result BeginCore(SceneCameraCommand command, SceneCameraTarget? restore, SceneCameraTarget target, double now)
    {
        Result validation = SceneCameraCommandValidator.Validate(command);
        if (!validation.Success) return validation;
        if (!double.IsFinite(now)) return Result.Fail("Camera animation time must be finite.");
        validation = SceneCameraCommandValidator.ValidateTarget(target.State);
        if (!validation.Success) return validation;
        if (target.DurationMilliseconds is < 0 or > SceneCameraCommandValidator.MaximumDurationMilliseconds
            || !Enum.IsDefined(typeof(CharacterTransformEasing), target.Easing))
            return Result.Fail("Camera animation timing is invalid.");
        if (restore != null)
        {
            validation = SceneCameraCommandValidator.ValidateTarget(restore.State);
            if (!validation.Success) return validation;
            if (restore.DurationMilliseconds != 0)
                return Result.Fail("Camera replay restore must be immediate.");
        }

        ScopeMotion overall = _overall;
        ScopeMotion background = _background;
        if (command.Scope == SceneCameraScope.Overall)
        {
            if (restore != null) overall = overall.With(command, restore, now);
            overall = overall.With(command, target, now);
        }
        else
        {
            if (restore != null) background = background.With(command, restore, now);
            background = background.With(command, target, now);
        }

        validation = ValidateTransition(overall, background, now);
        if (!validation.Success) return validation;

        _overall = overall;
        _background = background;
        return Result.Ok();
    }

    public Result Seed(SceneCameraComposition inherited, bool hasOverall, bool hasBackground, double now)
    {
        if (!double.IsFinite(now)) return Result.Fail("Camera animation time must be finite.");
        ScopeMotion overall = hasOverall ? ScopeMotion.Immediate(inherited.Overall) : _overall;
        ScopeMotion background = hasBackground ? ScopeMotion.Immediate(inherited.Background) : _background;
        Result validation = ValidateTransition(overall, background, now);
        if (!validation.Success) return validation;
        _overall = overall;
        _background = background;
        return Result.Ok();
    }

    private static Result ValidateTransition(ScopeMotion overall, ScopeMotion background, double now)
    {
        Result validation = SceneCameraCommandValidator.ValidateComposition(new(overall.Target, background.Target));
        if (!validation.Success) return validation;

        // Independent durations can temporarily multiply to a larger/smaller
        // zoom than either endpoint. A conservative envelope proves every
        // intermediate product safe without timing-dependent sampling guesses.
        (double minimumG, double maximumG) = overall.Zoom.RangeFrom(now);
        (double minimumB, double maximumB) = background.Zoom.RangeFrom(now);
        // The physical composition is written as float. Match its boundary
        // rounding (for example 1.6f * 5f == 8f), while retaining double time
        // and interpolation math inside the individual channels.
        if ((float)(minimumG * minimumB) < SceneCameraCommandValidator.MinimumZoom
            || (float)(maximumG * maximumB) > SceneCameraCommandValidator.MaximumZoom)
            return Result.Fail("Camera animation may exceed the combined zoom range during its transition.");

        double maximumBackX = MaximumMagnitude(overall.X, now)
            + maximumG * MaximumMagnitude(background.X, now);
        double maximumBackY = MaximumMagnitude(overall.Y, now)
            + maximumG * MaximumMagnitude(background.Y, now);
        if (maximumBackX > float.MaxValue || maximumBackY > float.MaxValue)
            return Result.Fail("Camera animation position may overflow during its transition.");

        if (MaximumMagnitude(overall.X, now) / minimumG > float.MaxValue
            || MaximumMagnitude(overall.Y, now) / minimumG > float.MaxValue
            || MaximumMagnitude(background.X, now) / minimumB > float.MaxValue
            || MaximumMagnitude(background.Y, now) / minimumB > float.MaxValue)
            return Result.Fail("Camera animation logical position may overflow during its transition.");

        return Result.Ok();
    }

    private static double MaximumMagnitude(Channel channel, double now)
    {
        (double minimum, double maximum) = channel.RangeFrom(now);
        return Math.Max(Math.Abs(minimum), Math.Abs(maximum));
    }

    private readonly record struct ScopeMotion(SceneCameraState Target, Channel X, Channel Y, Channel Zoom)
    {
        // Position channels interpolate physical local offsets, as the legacy
        // TweenPosition did. Interpolating logical X and Zoom separately would
        // change a combined move/zoom into a quadratic physical trajectory.
        public SceneCameraState Sample(double now)
        {
            double zoom = Zoom.Sample(now);
            return new((float)(X.Sample(now) / zoom), (float)(Y.Sample(now) / zoom), (float)zoom);
        }
        public bool IsAnimating(double now) => X.IsAnimating(now) || Y.IsAnimating(now) || Zoom.IsAnimating(now);
        public static ScopeMotion Immediate(SceneCameraState state) => new(
            state, Channel.Immediate((double)state.X * state.Zoom),
            Channel.Immediate((double)state.Y * state.Zoom), Channel.Immediate(state.Zoom));

        public ScopeMotion With(SceneCameraCommand command, SceneCameraTarget target, double now)
        {
            bool reset = command.Operation == SceneCameraOperation.Reset;
            bool position = reset || command.X.HasValue || command.Y.HasValue
                || command.DeltaX.HasValue || command.DeltaY.HasValue
                || command.Zoom.HasValue || command.DeltaZoom.HasValue;
            return new(
                target.State,
                position ? X.To((double)target.State.X * target.State.Zoom,
                    target.DurationMilliseconds, target.Easing, now) : X,
                position ? Y.To((double)target.State.Y * target.State.Zoom,
                    target.DurationMilliseconds, target.Easing, now) : Y,
                reset || command.Zoom.HasValue || command.DeltaZoom.HasValue
                    ? Zoom.To(target.State.Zoom, target.DurationMilliseconds, target.Easing, now) : Zoom);
        }
    }

    private readonly record struct Channel(
        double Start, double Target, double StartedAt, int DurationMilliseconds, CharacterTransformEasing Easing)
    {
        public static Channel Immediate(double value) => new(value, value, 0, 0, CharacterTransformEasing.Linear);
        public bool IsAnimating(double now) => DurationMilliseconds > 0 && Start != Target
            && now < StartedAt + DurationMilliseconds / 1000d;

        public Channel To(double value, int duration, CharacterTransformEasing easing, double now) =>
            duration == 0 ? Immediate(value) : new(Sample(now), value, now, duration, easing);

        public (double Minimum, double Maximum) RangeFrom(double now)
        {
            double current = Sample(now);
            return (Math.Min(current, Target), Math.Max(current, Target));
        }

        public double Sample(double now)
        {
            if (DurationMilliseconds == 0 || now >= StartedAt + DurationMilliseconds / 1000d) return Target;
            double t = Math.Clamp((now - StartedAt) * 1000d / DurationMilliseconds, 0d, 1d);
            t = Easing switch
            {
                // NGUI's published UITweener.Sample default methods:
                // https://github.com/tasharen/ngui/blob/master/Assets/NGUI/Scripts/Tweening/UITweener.cs
                // This does not claim live AA steeperCurves/timeScale settings.
                CharacterTransformEasing.EaseIn => 1d - Math.Sin(Math.PI * 0.5d * (1d - t)),
                CharacterTransformEasing.EaseOut => Math.Sin(Math.PI * 0.5d * t),
                CharacterTransformEasing.EaseInOut => t - Math.Sin(t * Math.PI * 2d) / (Math.PI * 2d),
                _ => t
            };
            return Start + (Target - Start) * t;
        }
    }
}
