using System.Globalization;

namespace AzureArchive.VideoTools.Core.Characters;

/// <summary>
/// Cubic timing curve from (0,0) to (1,1). The control points stay in the unit
/// square, so time is monotonic and the resulting progress cannot overshoot.
/// </summary>
public readonly record struct CharacterPresetBezier(float X1, float Y1, float X2, float Y2)
{
    public static CharacterPresetBezier Linear => new(0f, 0f, 1f, 1f);
    public static CharacterPresetBezier EaseIn => new(0.42f, 0f, 1f, 1f);
    public static CharacterPresetBezier EaseOut => new(0f, 0f, 0.58f, 1f);
    public static CharacterPresetBezier EaseInOut => new(0.42f, 0f, 0.58f, 1f);

    public bool IsValid => IsCoordinate(X1) && IsCoordinate(Y1)
        && IsCoordinate(X2) && IsCoordinate(Y2);

    /// <summary>
    /// Inverts x(u) to find the curve parameter, then evaluates y(u). A bounded
    /// bisection also handles zero endpoint slopes and reversed X control points.
    /// Invalid curves and non-finite progress fail closed at the initial value.
    /// </summary>
    public double Evaluate(double progress)
    {
        if (!IsValid || !double.IsFinite(progress) || progress <= 0d)
        {
            return 0d;
        }

        if (progress >= 1d)
        {
            return 1d;
        }

        if (X1 == Y1 && X2 == Y2)
        {
            return progress;
        }

        double lower = 0d;
        double upper = 1d;
        double parameter = progress;
        for (int iteration = 0; iteration < 56; iteration++)
        {
            double time = Coordinate(parameter, X1, X2);
            if (time == progress)
            {
                break;
            }

            if (time < progress)
            {
                lower = parameter;
            }
            else
            {
                upper = parameter;
            }

            parameter = (lower + upper) * 0.5d;
        }

        return Math.Clamp(Coordinate(parameter, Y1, Y2), 0d, 1d);
    }

    /// <summary>Invariant, round-trippable control points for a directive.</summary>
    public string ToDirectiveValue() => string.Join(",", Format(X1), Format(Y1), Format(X2), Format(Y2));

    private static bool IsCoordinate(float value) => float.IsFinite(value) && value is >= 0f and <= 1f;

    private static string Format(float value) =>
        (value == 0f ? 0f : value).ToString("R", CultureInfo.InvariantCulture);

    private static double Coordinate(double parameter, double first, double second)
    {
        double inverse = 1d - parameter;
        return 3d * inverse * inverse * parameter * first
            + 3d * inverse * parameter * parameter * second
            + parameter * parameter * parameter;
    }
}
