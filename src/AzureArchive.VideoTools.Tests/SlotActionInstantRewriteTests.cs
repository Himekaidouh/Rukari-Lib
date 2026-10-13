using AzureArchive.VideoTools.Core.Cameras;
using AzureArchive.VideoTools.Core.Characters;
using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Tests;

/// <summary>
/// The forced-finish rewrite: what a still-queued segment becomes when the engine
/// has already moved on. The endpoint must land immediately, and a relative
/// destination must keep its value so the rewritten segments still accumulate to
/// the same place.
/// </summary>
internal static class SlotActionInstantRewriteTests
{
    private static string Instant(string directive)
    {
        Result<string> rewritten = SlotActionInstantRewrite.ToInstant(directive);
        AssertEx.True(rewritten.Success, rewritten.Error);
        return AssertEx.NotNull(rewritten.Value);
    }

    private static int DurationOf(string characterDirective)
    {
        Result<CharacterTransformCommand> parsed =
            new CharacterTransformDirectiveParser().Parse(characterDirective);
        AssertEx.True(parsed.Success, parsed.Error);
        return AssertEx.NotNull(parsed.Value).DurationMilliseconds;
    }

    public static void CharacterTransformLosesOnlyItsDuration()
    {
        const string authored = "#char;3;set;x=-748.283;y=-60.672;duration=600;easing=easeInOut";
        string rewritten = Instant(authored);
        AssertEx.Equal("#char;3;set;x=-748.283;y=-60.672;duration=0;easing=easeInOut", rewritten);
        AssertEx.Equal(0, DurationOf(rewritten));

        CharacterTransformCommand before = AssertEx.NotNull(
            new CharacterTransformDirectiveParser().Parse(authored).Value);
        CharacterTransformCommand after = AssertEx.NotNull(
            new CharacterTransformDirectiveParser().Parse(rewritten).Value);
        AssertEx.Equal(before.PublicSlot, after.PublicSlot);
        AssertEx.Equal(before.Operation, after.Operation);
        AssertEx.Equal(before.X, after.X);
        AssertEx.Equal(before.Y, after.Y);
        AssertEx.Equal(before.RotationDegrees, after.RotationDegrees);
        AssertEx.Equal(before.Easing, after.Easing);
    }

    public static void RelativeMovesKeepTheirDeltaSoForcedSegmentsStillAccumulate()
    {
        // The queue's last segment is the authored endpoint. Rewriting must not touch
        // dx, otherwise the accumulated position would no longer be that endpoint.
        string[] queued =
        {
            "#char;3;move;dx=100;duration=600;easing=linear",
            "#char;3;move;dx=200;duration=600;easing=linear"
        };
        double totalDx = 0;
        foreach (string directive in queued)
        {
            string rewritten = Instant(directive);
            AssertEx.Equal(0, DurationOf(rewritten));
            AssertEx.True(rewritten.Contains(";move;", StringComparison.Ordinal), rewritten);
            CharacterTransformCommand command = AssertEx.NotNull(
                new CharacterTransformDirectiveParser().Parse(rewritten).Value);
            totalDx += command.DeltaX ?? 0f;
        }

        AssertEx.Equal(300f, (float)totalDx);
    }

    public static void CameraScopesAndTargetsSurviveTheRewrite()
    {
        const string authored = "#camera;set;scope=background;zoom=1.5;duration=250;easing=easeOut";
        string rewritten = Instant(authored);
        AssertEx.Equal("#camera;set;scope=background;zoom=1.5;duration=0;easing=easeOut", rewritten);
        SceneCameraCommand after = AssertEx.NotNull(
            new SceneCameraDirectiveParser().Parse(rewritten).Value);
        AssertEx.Equal(0, after.DurationMilliseconds);
        AssertEx.Equal(SceneCameraScope.Background, after.Scope);
        AssertEx.Equal(1.5f, after.Zoom ?? 0f);

        string reset = Instant("#camera;reset;duration=500;easing=linear");
        AssertEx.Equal("#camera;reset;duration=0;easing=linear", reset);
    }

    public static void AlreadyInstantaneousKeepsItsDirectiveAndUnsupportedFamiliesFailOpen()
    {
        const string instant = "#char;3;set;x=10;duration=0;easing=linear";
        AssertEx.Equal(instant, Instant(instant));

        // A preset IS its motion, so there is no honest instantaneous form: the
        // caller keeps the authored directive rather than inventing one.
        AssertEx.False(SlotActionInstantRewrite
            .ToInstant("#fx;3;sway;amplitude=25;frequency=2;cycles=4;direction=right").Success);
        AssertEx.False(SlotActionInstantRewrite.ToInstant("#wait;100").Success);
        AssertEx.False(SlotActionInstantRewrite.ToInstant("#charp;3;set;x=10").Success);
    }
}
