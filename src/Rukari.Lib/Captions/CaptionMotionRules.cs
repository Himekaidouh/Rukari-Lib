namespace Rukari.Lib.Captions;

/// <summary>
/// The bounds every caption motion moves inside. The look this reproduces is random-looking but strictly regular:
/// each character drifts on its own phase, yet no character may ever exceed the limits below, so the text stays
/// readable and the line never looks broken.
///
/// The maxima are constants and <see cref="Bounded"/> clamps a hand-written sheet to them. That is deliberate: a
/// style sheet is written by whoever is telling the story, and "slightly too slanted" is the difference between an
/// effect and a defect.
/// </summary>
public sealed record CaptionMotionRules
{
    /// <summary>How far a whole line may lean, in degrees. This is the hard "never too slanted" limit.</summary>
    public const float MaximumLineTiltDegrees = 1.5f;
    /// <summary>How far a single character may lean on top of the line's own lean.</summary>
    public const float MaximumCharacterTiltDegrees = 2f;
    /// <summary>Line lean plus character lean may never exceed this, whatever the sheet asks for.</summary>
    public const float MaximumTotalTiltDegrees = 3.5f;
    /// <summary>How far a single character may wander from its place, in physical pixels.</summary>
    public const float MaximumCharacterOffsetPixels = 1.2f;
    /// <summary>How much a single character's size may breathe, as a fraction of its font size.</summary>
    public const float MaximumCharacterScaleFraction = .02f;
    /// <summary>How far a whole line may be moved from its chosen place, as a fraction of the screen.</summary>
    public const float MaximumLineDriftFraction = .045f;

    /// <summary>Seconds between two motion values. The requirement that named this number was a quarter second.</summary>
    public const double DefaultJitterPeriodSeconds = .25;
    /// <summary>Seconds a line leans towards its next angle. Long, so the lean drifts instead of flipping.</summary>
    public const double DefaultLineTiltPeriodSeconds = 6.0;
    /// <summary>Seconds between two characters appearing when the sheet gives no per-character times.</summary>
    public const double DefaultCharacterRevealSeconds = .045;
    /// <summary>Seconds one character takes to fade in and grow to its size.</summary>
    public const double DefaultCharacterFadeSeconds = .18;
    /// <summary>Seconds the last characters of a line take to fade out.</summary>
    public const double DefaultLineFadeSeconds = .6;
    /// <summary>A character grows from this fraction of its size, which is the small pop as it lands.</summary>
    public const float DefaultPopFromScale = .85f;

    /// <summary>Line lean, in degrees, never above <see cref="MaximumLineTiltDegrees"/>.</summary>
    public float LineTiltDegrees { get; init; } = MaximumLineTiltDegrees;

    /// <summary>Character lean, in degrees, never above <see cref="MaximumCharacterTiltDegrees"/>.</summary>
    public float CharacterTiltDegrees { get; init; } = MaximumCharacterTiltDegrees;

    /// <summary>Character wander, in physical pixels.</summary>
    public float CharacterOffsetPixels { get; init; } = MaximumCharacterOffsetPixels;

    /// <summary>Character breathing, as a fraction of the font size.</summary>
    public float CharacterScaleFraction { get; init; } = MaximumCharacterScaleFraction;

    /// <summary>How far the whole line may drift from where the placement put it, as a fraction of the screen.</summary>
    public float LineDriftFraction { get; init; } = MaximumLineDriftFraction;

    /// <summary>Seconds between two character motion values.</summary>
    public double JitterPeriodSeconds { get; init; } = DefaultJitterPeriodSeconds;

    /// <summary>Seconds a line takes to lean towards its next angle.</summary>
    public double LineTiltPeriodSeconds { get; init; } = DefaultLineTiltPeriodSeconds;

    /// <summary>Seconds between two characters appearing, when the sheet carries no per-character times.</summary>
    public double CharacterRevealSeconds { get; init; } = DefaultCharacterRevealSeconds;

    /// <summary>Seconds one character takes to appear.</summary>
    public double CharacterFadeSeconds { get; init; } = DefaultCharacterFadeSeconds;

    /// <summary>Seconds the line takes to fade out at its end.</summary>
    public double LineFadeSeconds { get; init; } = DefaultLineFadeSeconds;

    /// <summary>The size a character grows from, as a fraction of its own.</summary>
    public float PopFromScale { get; init; } = DefaultPopFromScale;

    /// <summary>How a line leaves: <c>linear</c>, <c>slow</c> (nothing visible until the end) or <c>cut</c>.</summary>
    public string FadeCurve { get; init; } = FadeSlow;

    /// <summary>The line dims steadily from the moment its fade starts.</summary>
    public const string FadeLinear = "linear";
    /// <summary>The line holds its brightness and only drops at the very end, which reads as a memory fading.</summary>
    public const string FadeSlow = "slow";
    /// <summary>The line disappears on the last frame of its own window.</summary>
    public const string FadeCut = "cut";

    /// <summary>The values the layer ships with: the quarter-second breathing and the slow fade.</summary>
    public static CaptionMotionRules Default { get; } = new();

    /// <summary>
    /// This rules set with every value forced inside its limit. A sheet may ask for a wilder effect; it gets the
    /// wildest one that still reads as text.
    /// </summary>
    public CaptionMotionRules Bounded() => new()
    {
        LineTiltDegrees = Clamp(LineTiltDegrees, 0f, MaximumLineTiltDegrees),
        CharacterTiltDegrees = Clamp(CharacterTiltDegrees, 0f, MaximumCharacterTiltDegrees),
        CharacterOffsetPixels = Clamp(CharacterOffsetPixels, 0f, MaximumCharacterOffsetPixels),
        CharacterScaleFraction = Clamp(CharacterScaleFraction, 0f, MaximumCharacterScaleFraction),
        LineDriftFraction = Clamp(LineDriftFraction, 0f, MaximumLineDriftFraction),
        JitterPeriodSeconds = Clamp(JitterPeriodSeconds, .05, 4),
        LineTiltPeriodSeconds = Clamp(LineTiltPeriodSeconds, 1, 60),
        CharacterRevealSeconds = Clamp(CharacterRevealSeconds, .005, 1),
        CharacterFadeSeconds = Clamp(CharacterFadeSeconds, 0, 2),
        LineFadeSeconds = Clamp(LineFadeSeconds, 0, 4),
        PopFromScale = Clamp(PopFromScale, .5f, 1f),
        FadeCurve = FadeCurve
    };

    private static float Clamp(float value, float minimum, float maximum) =>
        float.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : maximum;

    private static double Clamp(double value, double minimum, double maximum) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : maximum;
}
