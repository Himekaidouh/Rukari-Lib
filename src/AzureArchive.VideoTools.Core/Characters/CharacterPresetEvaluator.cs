namespace AzureArchive.VideoTools.Core.Characters;

public static class CharacterPresetEvaluator
{
    /// <summary>
    /// Samples a relative pose without touching runtime objects or accumulating
    /// frame deltas. Non-finite/negative time is treated as the initial pose.
    /// Invalid commands fail closed with a completed, neutral pose.
    /// </summary>
    public static CharacterPresetFrame Sample(CharacterPresetCommand command, double elapsedSeconds)
    {
        if (!CharacterPresetCommandValidator.Validate(command).Success)
        {
            return CharacterPresetFrame.Neutral(completed: true);
        }

        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0d)
        {
            return CharacterPresetFrame.Neutral();
        }

        if (command.Kind == CharacterPresetKind.Headbutt)
        {
            return SampleHeadbutt(command, elapsedSeconds);
        }

        double frequency = command.FrequencyHz;
        if (command.Cycles > 0 && elapsedSeconds >= command.Cycles / frequency)
        {
            return CharacterPresetFrame.Neutral(completed: true);
        }

        // Reduce time before multiplying so even double.MaxValue is safe for
        // an effect that runs until the next dialogue. There is no time cutoff.
        double phase = command.Cycles > 0
            ? elapsedSeconds * frequency % 1d
            : elapsedSeconds % (1d / frequency) * frequency;
        if (phase < 1e-12d || 1d - phase < 1e-12d)
        {
            phase = 0d;
        }

        if (command.Kind == CharacterPresetKind.Spin)
        {
            // Finite spins expose complete revolutions, so a renderer never
            // interpolates the shortest path between the initial/final pose.
            double turns = command.Cycles > 0 ? elapsedSeconds * frequency : phase;
            if (command.Bezier is { } spinCurve)
            {
                // Warp only the fraction within a revolution. Whole revolutions
                // and the duration selected by frequency/cycles stay unchanged.
                double wholeTurns = command.Cycles > 0 ? Math.Floor(turns) : 0d;
                if (command.Cycles > 0 && phase == 0d && turns % 1d > 0.5d)
                {
                    wholeTurns += 1d;
                }

                turns = wholeTurns + spinCurve.Evaluate(phase);
            }

            return new CharacterPresetFrame(0f, 1f, 1f, false)
            {
                YawDegrees = (float)(command.Direction * turns * 360d)
            };
        }

        // Keep the selected period and peak amplitude, but ease the first and
        // last quarter-cycle so attaching/removing the temporary pose does not
        // introduce a velocity jump. An unbounded effect only has a fade-in;
        // its next-dialogue release belongs to the runtime lifecycle.
        double elapsedCycles = elapsedSeconds * frequency;
        double envelope = SmoothStep(Math.Min(1d, elapsedCycles * 4d));
        if (command.Cycles > 0)
        {
            envelope *= SmoothStep(Math.Min(1d, (command.Cycles - elapsedCycles) * 4d));
        }

        double wavePhase = command.Bezier is { } cycleCurve ? cycleCurve.Evaluate(phase) : phase;
        double wave = phase == 0d ? 0d
            : Math.Sin(wavePhase * 2d * Math.PI) * command.Direction * envelope;
        if (command.Kind == CharacterPresetKind.Sway)
        {
            // Positive Z turns counterclockwise on screen. The public "right"
            // direction tips the head right, so its screen rotation is negative.
            return new CharacterPresetFrame((float)(-command.Amplitude * wave), 1f, 1f, false);
        }

        float scaleY = (float)Math.Exp(command.Amplitude * wave);
        return new CharacterPresetFrame(0f, 1f / scaleY, scaleY, false);
    }

    private static CharacterPresetFrame SampleHeadbutt(CharacterPresetCommand command, double elapsedSeconds)
    {
        double duration = command.DurationMilliseconds / 1000d;
        if (elapsedSeconds >= duration)
        {
            return CharacterPresetFrame.Neutral(completed: true);
        }

        double progress = elapsedSeconds / duration;
        double rotation;
        if (progress <= 0.30d)
        {
            rotation = -command.BackDegrees * HeadbuttTiming(command, progress / 0.30d);
        }
        else if (progress <= 0.45d)
        {
            double strike = (progress - 0.30d) / 0.15d;
            rotation = -command.BackDegrees
                + (command.BackDegrees + command.ForwardDegrees) * HeadbuttTiming(command, strike);
        }
        else
        {
            double recovery = (progress - 0.45d) / 0.55d;
            // A restrained start followed by a smooth release keeps the hit
            // readable while returning exactly to the original angular pose.
            rotation = command.ForwardDegrees * (1d - HeadbuttTiming(command, recovery));
        }

        return new CharacterPresetFrame((float)(-command.Direction * rotation), 1f, 1f, false);
    }

    private static double HeadbuttTiming(CharacterPresetCommand command, double progress) =>
        command.Bezier is { } curve ? curve.Evaluate(progress) : SmoothStep(progress);

    private static double SmoothStep(double value) => value * value * (3d - 2d * value);
}
