using AzureArchive.VideoTools.Core.Spines;

namespace AzureArchive.VideoTools.Tests;

internal static class SpineLobbyThumbnailFitTests
{
    public static void KeiLobbyBoundsFitInsidePortraitCard()
    {
        // The converted CH0335 lobby has an offset origin and includes its background.
        SpineLobbyThumbnailLayout layout = AssertEx.NotNull(SpineLobbyThumbnailFit.Calculate(
            -1767.87f, -1231.19f, 4343.96f, 3484.19f, 528f, 1340f, 60f, 60f));

        AssertNear(496f, 4343.96f * layout.ScaleX);
        AssertNear(layout.ScaleX, layout.ScaleY);
        AssertContainedAndCentered(layout, -1767.87f, -1231.19f, 4343.96f, 3484.19f, 496f, 1308f);
    }

    public static void WideAndTallBoundsUseTheLimitingCardDimension()
    {
        SpineLobbyThumbnailLayout wide = AssertEx.NotNull(SpineLobbyThumbnailFit.Calculate(
            200f, -100f, 10000f, 10f, 200f, 400f, 2f, 1f, 10f));
        AssertNear(180f, 10000f * wide.ScaleX);
        AssertNear(2f, wide.ScaleX / wide.ScaleY);
        AssertContainedAndCentered(wide, 200f, -100f, 10000f, 10f, 180f, 380f);

        SpineLobbyThumbnailLayout tall = AssertEx.NotNull(SpineLobbyThumbnailFit.Calculate(
            -100f, 200f, 10f, 10000f, 200f, 400f, 1f, 2f, 10f));
        AssertNear(380f, 10000f * tall.ScaleY);
        AssertNear(2f, tall.ScaleY / tall.ScaleX);
        AssertContainedAndCentered(tall, -100f, 200f, 10f, 10000f, 180f, 380f);
    }

    public static void AlreadyFittingBoundsAreCenteredWithoutEnlarging()
    {
        SpineLobbyThumbnailLayout layout = AssertEx.NotNull(SpineLobbyThumbnailFit.Calculate(
            100f, -80f, 20f, 30f, 200f, 400f, 0.5f, 0.75f, 10f));

        AssertEx.Equal(0.5f, layout.ScaleX);
        AssertEx.Equal(0.75f, layout.ScaleY);
        AssertEx.Equal(-55f, layout.PositionX);
        AssertEx.Equal(48.75f, layout.PositionY);
        AssertContainedAndCentered(layout, 100f, -80f, 20f, 30f, 180f, 380f);
    }

    public static void RepeatedInitializationDoesNotKeepShrinkingTheThumbnail()
    {
        SpineLobbyThumbnailLayout first = AssertEx.NotNull(SpineLobbyThumbnailFit.Calculate(
            -1767.87f, -1231.19f, 4343.96f, 3484.19f, 360f, 920f, 0.3f, 0.3f));
        SpineLobbyThumbnailLayout next = first;
        for (int repeat = 0; repeat < 100; repeat++)
        {
            next = AssertEx.NotNull(SpineLobbyThumbnailFit.Calculate(
                -1767.87f, -1231.19f, 4343.96f, 3484.19f, 360f, 920f, next.ScaleX, next.ScaleY));
        }

        AssertNear(first.ScaleX, next.ScaleX, 0.000001f);
        AssertNear(first.ScaleY, next.ScaleY, 0.000001f);
        AssertNear(first.PositionX, next.PositionX);
        AssertNear(first.PositionY, next.PositionY);
    }

    public static void FlippedSkeletonsKeepTheirOrientationAndFit()
    {
        foreach ((float scaleX, float scaleY) in new[] { (-2f, 1f), (2f, -1f), (-2f, -1f) })
        {
            SpineLobbyThumbnailLayout layout = AssertEx.NotNull(SpineLobbyThumbnailFit.Calculate(
                40f, -80f, 500f, 300f, 200f, 400f, scaleX, scaleY, 10f));
            AssertEx.Equal(Math.Sign(scaleX), Math.Sign(layout.ScaleX));
            AssertEx.Equal(Math.Sign(scaleY), Math.Sign(layout.ScaleY));
            AssertNear(scaleX / scaleY, layout.ScaleX / layout.ScaleY);
            AssertContainedAndCentered(layout, 40f, -80f, 500f, 300f, 180f, 380f);
        }
    }

    public static void InvalidGeometryDoesNotProduceATransform()
    {
        float[] valid = { -100f, -200f, 100f, 200f, 360f, 920f, 1f, 1f, 16f };
        for (int index = 0; index < valid.Length; index++)
        {
            foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            {
                float[] values = (float[])valid.Clone();
                values[index] = invalid;
                AssertEx.True(Calculate(values) is null, $"Nonfinite input at index {index} must be rejected.");
            }
        }

        foreach (int index in new[] { 2, 3, 4, 5, 6, 7 })
        {
            float[] values = (float[])valid.Clone();
            values[index] = 0f;
            AssertEx.True(Calculate(values) is null, $"Zero dimension or scale at index {index} must be rejected.");
        }

        foreach (int index in new[] { 2, 3, 4, 5, 8 })
        {
            float[] values = (float[])valid.Clone();
            values[index] = -1f;
            AssertEx.True(Calculate(values) is null, $"Negative size or padding at index {index} must be rejected.");
        }

        AssertEx.True(SpineLobbyThumbnailFit.Calculate(0f, 0f, 100f, 100f, 32f, 100f, 1f, 1f) is null);
        AssertEx.True(SpineLobbyThumbnailFit.Calculate(0f, 0f, 100f, 100f, 100f, 20f, 1f, 1f) is null);
        AssertEx.True(SpineLobbyThumbnailFit.Calculate(float.MaxValue, 0f, 1f, 1f,
            100f, 100f, float.MaxValue, 1f, 0f) is null);
    }

    private static SpineLobbyThumbnailLayout? Calculate(float[] values) =>
        SpineLobbyThumbnailFit.Calculate(values[0], values[1], values[2], values[3], values[4],
            values[5], values[6], values[7], values[8]);

    private static void AssertContainedAndCentered(SpineLobbyThumbnailLayout layout,
        float x, float y, float width, float height, float availableWidth, float availableHeight)
    {
        float left = Math.Min(x * layout.ScaleX, (x + width) * layout.ScaleX) + layout.PositionX;
        float right = Math.Max(x * layout.ScaleX, (x + width) * layout.ScaleX) + layout.PositionX;
        float bottom = Math.Min(y * layout.ScaleY, (y + height) * layout.ScaleY) + layout.PositionY;
        float top = Math.Max(y * layout.ScaleY, (y + height) * layout.ScaleY) + layout.PositionY;
        AssertEx.True(left >= -availableWidth / 2f - 0.001f && right <= availableWidth / 2f + 0.001f,
            "Both horizontal edges must stay inside the padded card.");
        AssertEx.True(bottom >= -availableHeight / 2f - 0.001f && top <= availableHeight / 2f + 0.001f,
            "Both vertical edges must stay inside the padded card.");
        AssertNear(0f, left + right);
        AssertNear(0f, bottom + top);
    }

    private static void AssertNear(float expected, float actual, float tolerance = 0.001f) =>
        AssertEx.True(float.IsFinite(actual) && Math.Abs(expected - actual) <= tolerance,
            $"Expected {expected} within {tolerance}, found {actual}.");
}
