using System.Text;

namespace Rukari.Lib.Captions;

/// <summary>What one character of a caption looks like at one instant.</summary>
public readonly record struct CaptionCharacterState(
    int Index,
    float Alpha,
    float OffsetX,
    float OffsetY,
    float Scale,
    float RotationDegrees,
    bool Emphasised);

/// <summary>What a whole caption line looks like at one instant.</summary>
public readonly record struct CaptionLineState(
    double Alpha,
    float TiltDegrees,
    float OffsetX,
    float OffsetY,
    int CharacterCount,
    bool Finished);

/// <summary>
/// The caption's clock: given a cue and a moment, it says what every character is doing. It is pure arithmetic with
/// no Unity types, so the whole look — the typewriter, the quarter-second breathing, the fade — can be pinned by
/// tests instead of by screenshots, and the renderer only has to draw the numbers it is handed.
///
/// Two rules make the motion look alive rather than noisy:
/// <list type="bullet">
/// <item>every value is deterministic from (line, character, tick), so the same line always moves the same way and
/// a frame can be reproduced exactly;</item>
/// <item>values are interpolated between ticks with a smoothstep, so nothing ever snaps at a period boundary.</item>
/// </list>
/// </summary>
public static class CaptionTimeline
{
    /// <summary>Seconds the last character of a caption appears after the first one.</summary>
    public static double RevealSeconds(CaptionCue cue, CaptionMotionRules rules, int characterCount)
    {
        if (characterCount <= 0) return 0;
        if (cue.CharacterTimes is { Count: > 0 } times && times.Count >= characterCount)
            return Math.Max(0, times[characterCount - 1]);
        return Math.Max(0, characterCount - 1) * rules.CharacterRevealSeconds;
    }

    /// <summary>
    /// How long the caption is on screen in total: its characters appearing, then its hold, then its fade. An
    /// explicit <see cref="CaptionCue.End"/> from a subtitle file replaces the hold, so an imported line keeps the
    /// timing it was written with.
    /// </summary>
    public static double Duration(CaptionCue cue, CaptionMotionRules rules, int characterCount = 0)
    {
        int count = characterCount > 0 ? characterCount : CharacterCount(cue);
        double reveal = RevealSeconds(cue, rules, count) + rules.CharacterFadeSeconds;
        if (cue.End is { } end && end > cue.At) return Math.Max(0.01, end - cue.At);
        return reveal + Math.Max(0, cue.Hold) + rules.LineFadeSeconds;
    }

    /// <summary>The number of characters a caption draws, counting whole Unicode characters only.</summary>
    public static int CharacterCount(CaptionCue cue) => CharacterCount(cue.Text);

    /// <summary>
    /// Counts the characters a caption draws. A surrogate pair and a combining sequence are one character each, so
    /// the typewriter can never split one in half.
    /// </summary>
    public static int CharacterCount(string? text) => Split(text).Count;

    /// <summary>
    /// Splits a caption into the units it draws them in: one per grapheme-ish cluster, so an emoji or a combining
    /// mark appears in one piece.
    /// </summary>
    public static IReadOnlyList<string> Split(string? text)
    {
        var units = new List<string>();
        if (string.IsNullOrEmpty(text)) return units;
        var current = new System.Text.StringBuilder();
        bool joined = false;
        foreach (Rune rune in text.EnumerateRunes())
        {
            bool zeroWidthJoiner = rune.Value == 0x200D;
            // A combining mark, or anything following a zero-width joiner, continues the character it belongs to.
            bool continues = current.Length > 0 && (joined
                || zeroWidthJoiner
                || Rune.GetUnicodeCategory(rune) is System.Globalization.UnicodeCategory.NonSpacingMark
                    or System.Globalization.UnicodeCategory.EnclosingMark
                    or System.Globalization.UnicodeCategory.SpacingCombiningMark);
            if (!continues && current.Length > 0)
            {
                units.Add(current.ToString());
                current.Clear();
            }
            current.Append(rune.ToString());
            joined = zeroWidthJoiner;
        }
        if (current.Length > 0) units.Add(current.ToString());
        return units;
    }

    /// <summary>
    /// Evaluates the caption at <paramref name="seconds"/> seconds after its own start, writing one state per
    /// character into <paramref name="destination"/> and returning how many were written. Nothing is allocated, so
    /// the renderer can call this every frame.
    /// </summary>
    public static int Evaluate(
        CaptionCue cue,
        CaptionMotionRules rules,
        int characterCount,
        double seconds,
        Span<CaptionCharacterState> destination,
        out CaptionLineState line)
    {
        int count = Math.Max(0, Math.Min(characterCount, destination.Length));
        double duration = Duration(cue, rules, characterCount);
        uint seed = SeedOf(cue.SeedKey);
        if (seconds < 0 || seconds > duration)
        {
            line = new CaptionLineState(0, 0, 0, 0, count, true);
            for (int i = 0; i < count; i++) destination[i] = new CaptionCharacterState(i, 0, 0, 0, 1, 0, false);
            return count;
        }

        double fadeStart = Math.Max(0, duration - rules.LineFadeSeconds);
        double lineAlpha = seconds <= fadeStart ? 1 : Fade(rules.FadeCurve, (seconds - fadeStart) / Math.Max(.0001, duration - fadeStart));

        // Every amplitude is clamped here rather than trusted: a style sheet is written by hand, and a line that
        // leans thirty degrees is a defect, not an effect.
        float tiltLimit = Limit(rules.LineTiltDegrees, CaptionMotionRules.MaximumLineTiltDegrees);
        float characterTiltLimit = Limit(rules.CharacterTiltDegrees, CaptionMotionRules.MaximumCharacterTiltDegrees);
        float offsetLimit = Limit(rules.CharacterOffsetPixels, CaptionMotionRules.MaximumCharacterOffsetPixels);
        float scaleLimit = Limit(rules.CharacterScaleFraction, CaptionMotionRules.MaximumCharacterScaleFraction);
        float driftLimit = Limit(rules.LineDriftFraction, CaptionMotionRules.MaximumLineDriftFraction);
        float popFrom = Math.Clamp(rules.PopFromScale, .5f, 1f);
        double jitterPeriod = Math.Max(.05, rules.JitterPeriodSeconds);
        double fadeSeconds = Math.Max(0, rules.CharacterFadeSeconds);

        // The line leans and drifts on its own long period, so consecutive captions share a direction instead of
        // each throwing new dice.
        float tilt = tiltLimit * Drift(seed, ChannelTilt, seconds, rules.LineTiltPeriodSeconds);
        float driftX = 0;
        float driftY = 0;
        if (driftLimit > 0)
        {
            driftX = driftLimit * Drift(seed, ChannelDriftX, seconds, rules.LineTiltPeriodSeconds);
            driftY = driftLimit * Drift(seed, ChannelDriftY, seconds, rules.LineTiltPeriodSeconds);
        }

        for (int i = 0; i < count; i++)
        {
            double appears = AppearsAt(cue, rules, i);
            double progress = fadeSeconds <= 0 ? 1 : (seconds - appears) / fadeSeconds;
            if (progress <= 0)
            {
                // A character that has not appeared yet still reports what it will be: the emphasis belongs to the
                // line, not to the moment, and the renderer draws the first frame of it as soon as it arrives.
                destination[i] = new CaptionCharacterState(i, 0, 0, 0, popFrom, 0, IsEmphasised(cue, i, count));
                continue;
            }
            float entered = (float)Math.Clamp(progress, 0, 1);
            float eased = Smoothstep(entered);
            float alpha = (float)(lineAlpha * entered);
            float scale = popFrom + (1f - popFrom) * eased;
            float characterTilt = characterTiltLimit * Jitter(seed, i, ChannelTilt, seconds, jitterPeriod);
            float offsetX = offsetLimit * Jitter(seed, i, ChannelOffsetX, seconds, jitterPeriod);
            float offsetY = offsetLimit * Jitter(seed, i, ChannelOffsetY, seconds, jitterPeriod);
            scale += scaleLimit * Jitter(seed, i, ChannelScale, seconds, jitterPeriod);
            // The total lean is what the eye actually judges, so it is clamped after both parts are combined.
            float totalTilt = Math.Clamp(tilt + characterTilt, -CaptionMotionRules.MaximumTotalTiltDegrees,
                CaptionMotionRules.MaximumTotalTiltDegrees);
            destination[i] = new CaptionCharacterState(i, alpha, offsetX, offsetY,
                Math.Max(.1f, scale), totalTilt, IsEmphasised(cue, i, count));
        }
        line = new CaptionLineState(lineAlpha, tilt, driftX, driftY, count, false);
        return count;
    }

    private static float Limit(float value, float maximum) =>
        float.IsFinite(value) ? Math.Clamp(value, 0f, maximum) : maximum;

    /// <summary>The same evaluation for a caller that would rather be handed an array than fill a buffer.</summary>
    public static (CaptionLineState Line, CaptionCharacterState[] Characters) Evaluate(
        CaptionCue cue, CaptionMotionRules rules, int characterCount, double seconds)
    {
        var buffer = new CaptionCharacterState[Math.Max(0, characterCount)];
        Evaluate(cue, rules, characterCount, seconds, buffer, out CaptionLineState line);
        return (line, buffer);
    }

    /// <summary>Seconds after the caption's start at which a character appears.</summary>
    public static double AppearsAt(CaptionCue cue, CaptionMotionRules rules, int index)
    {
        if (cue.CharacterTimes is { Count: > 0 } times && index >= 0 && index < times.Count)
            return Math.Max(0, times[index]);
        return Math.Max(0, index) * rules.CharacterRevealSeconds;
    }

    /// <summary>
    /// The alpha a line has <paramref name="progress"/> of the way through its fade, by the curve it chose. The slow
    /// curve holds the line bright and drops it at the very end, which is the one that reads as a memory leaving.
    /// </summary>
    public static double Fade(string? curve, double progress)
    {
        double t = Math.Clamp(progress, 0, 1);
        if (string.Equals(curve, CaptionMotionRules.FadeCut, StringComparison.OrdinalIgnoreCase)) return t < 1 ? 1 : 0;
        if (string.Equals(curve, CaptionMotionRules.FadeSlow, StringComparison.OrdinalIgnoreCase)) return 1 - t * t * t;
        return 1 - t;
    }

    private static bool IsEmphasised(CaptionCue cue, int index, int count)
    {
        if (!cue.Emphasis) return false;
        if (cue.EmphasisCharacters is not { Count: > 0 } chosen) return true;
        return chosen.Contains(index) && index < count;
    }

    // --- deterministic motion ----------------------------------------------------------------------------------
    //
    // Every value below comes from a hash of (line, character, channel, tick). Nothing here reads a random number
    // generator, so the same caption always moves the same way: a frame can be reproduced from the log, and the
    // tests can assert the limits instead of describing them.

    private const int ChannelTilt = 1;
    private const int ChannelOffsetX = 2;
    private const int ChannelOffsetY = 3;
    private const int ChannelScale = 4;
    private const int ChannelDriftX = 5;
    private const int ChannelDriftY = 6;

    /// <summary>A value in -1..1 for one character, interpolated smoothly between this tick and the next.</summary>
    private static float Jitter(uint seed, int character, int channel, double seconds, double period)
    {
        if (period <= 0) return Noise(seed, character, channel, 0);
        double tick = seconds / period;
        int index = (int)Math.Floor(tick);
        float fraction = (float)(tick - index);
        float from = Noise(seed, character, channel, index);
        float to = Noise(seed, character, channel, index + 1);
        return from + (to - from) * Smoothstep(fraction);
    }

    /// <summary>
    /// A value in -1..1 for the whole line, on a long period. Consecutive captions therefore lean in related
    /// directions instead of each rolling its own dice, which is what keeps a run of lines feeling like one scene.
    /// </summary>
    private static float Drift(uint seed, int channel, double seconds, double period)
    {
        if (period <= 0) return Noise(seed, 0, channel, 0);
        double tick = seconds / period;
        int index = (int)Math.Floor(tick);
        float fraction = (float)(tick - index);
        float from = Noise(seed, 7, channel, index);
        float to = Noise(seed, 7, channel, index + 1);
        return from + (to - from) * Smoothstep(fraction);
    }

    private static float Noise(uint seed, int character, int channel, int tick)
    {
        unchecked
        {
            uint hash = seed;
            hash = (hash ^ (uint)character) * 16777619u;
            hash = (hash ^ (uint)channel) * 16777619u;
            hash = (hash ^ (uint)tick) * 16777619u;
            hash ^= hash >> 13;
            hash *= 0x5BD1E995u;
            hash ^= hash >> 15;
            return (hash & 0xFFFF) / 32767.5f - 1f;
        }
    }

    /// <summary>A stable seed for a line, so the same text always moves the same way.</summary>
    public static uint SeedOf(string? key)
    {
        unchecked
        {
            uint hash = 2166136261u;
            foreach (char character in key ?? string.Empty)
                hash = (hash ^ character) * 16777619u;
            return hash == 0 ? 2166136261u : hash;
        }
    }

    private static float Smoothstep(float value)
    {
        float t = Math.Clamp(value, 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
