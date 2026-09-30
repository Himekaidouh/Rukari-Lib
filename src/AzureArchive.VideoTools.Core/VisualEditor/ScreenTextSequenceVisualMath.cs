namespace AzureArchive.VideoTools.Core.VisualEditor;

/// <summary>
/// Pure sequence rules shared by the screen-text GUI. The runtime list is in
/// display order, so its ordinal can drive both stable marker colors and the
/// meaning of "previous text" without retaining any Unity objects.
/// </summary>
public static class ScreenTextSequenceVisualMath
{
    private const float GoldenRatioConjugate = 0.61803398875f;

    public static float MarkerHue(int sequenceIndex)
    {
        if (sequenceIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequenceIndex));
        }

        return (0.08f + (sequenceIndex * GoldenRatioConjugate)) % 1f;
    }

    public static int PreviousVisibleIndex(
        int visibleCount,
        int currentVisibleIndex)
    {
        if (visibleCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(visibleCount));
        }

        if (currentVisibleIndex < -1 || currentVisibleIndex >= visibleCount)
        {
            throw new ArgumentOutOfRangeException(nameof(currentVisibleIndex));
        }

        return currentVisibleIndex >= 0
            ? currentVisibleIndex - 1
            : visibleCount - 1;
    }

    public static ScreenTextDirective ResolveTemplate(
        ScreenTextDirective? currentScene,
        ScreenTextDirective? remembered) =>
        currentScene
        ?? remembered
        ?? new ScreenTextDirective(
            0f,
            0f,
            ScreenTextAlignment.Left,
            ScreenTextRevealMode.Instant,
            64);
}
