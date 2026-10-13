using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Tests;

/// <summary>
/// Track assignment of one row's directives: one character track per public slot, one
/// screen track shared by every camera directive, and one preset track per public slot.
/// A preset keeps its own track, so it may cross the character track in time (the
/// authored keyframe model); two presets on one slot still append, because the runtime
/// keeps exactly one active preset per slot.
/// </summary>
internal static class SlotActionChannelQueueTests
{
    private static SlotActionQueuePlan Plan(params string[] directives)
    {
        Result<SlotActionQueuePlan> plan = SlotActionQueuePlanner.PlanDirectives(directives);
        AssertEx.True(plan.Success, plan.Error);
        return AssertEx.NotNull(plan.Value);
    }

    private static void Near(double expected, double actual, string? message = null)
    {
        AssertEx.True(
            Math.Abs(expected - actual) < 0.001,
            message ?? $"Expected <{expected}> but found <{actual}>.");
    }

    public static void TwoCameraScopesShareOneScreenTrackAndAppend()
    {
        SlotActionQueuePlan plan = Plan(
            "#camera;move;dx=200;dzoom=0.25;duration=300;easing=easeOut",
            "#camera;move;scope=background;dx=20;dzoom=0.5;duration=250;easing=easeOut");
        AssertEx.Equal(2, plan.Segments.Count);
        AssertEx.Equal(plan.Segments[0].SlotKey, plan.Segments[1].SlotKey);
        AssertEx.Equal("camera", plan.Segments[0].SlotKey);
        Near(0d, plan.Segments[0].StartMs);
        // The second camera directive waits for the first one even though the scopes
        // differ: both compose into the same Back/Spine transforms every frame.
        Near(300d, plan.Segments[1].StartMs);
        Near(550d, plan.TotalMs);
    }

    public static void PresetTrackCrossesTheCharacterTrackWhileTheMoveRuns()
    {
        SlotActionQueuePlan plan = Plan(
            "#char;3;move;dx=100;duration=600;easing=linear",
            "#fx;3;sway;amplitude=25;frequency=2;cycles=2;direction=right");
        AssertEx.Equal(2, plan.Segments.Count);
        AssertEx.Equal("char:3", plan.Segments[0].SlotKey);
        AssertEx.Equal("preset:3", plan.Segments[1].SlotKey);
        // The preset starts at the row origin instead of waiting 600 ms, so the sway
        // can ride across the move.
        Near(0d, plan.Segments[0].StartMs);
        Near(0d, plan.Segments[1].StartMs);
        Near(1000d, plan.Segments[1].DurationMs);
        Near(1000d, plan.TotalMs);
    }

    public static void SquashPresetAlsoKeepsItsOwnTrackAndRunsParallel()
    {
        SlotActionQueuePlan plan = Plan(
            "#char;3;set;x=10;y=20;duration=600;easing=linear",
            "#fx;3;squash;amplitude=0.3;frequency=4;cycles=5");
        AssertEx.Equal(2, plan.Segments.Count);
        AssertEx.Equal("char:3", plan.Segments[0].SlotKey);
        AssertEx.Equal("preset:3", plan.Segments[1].SlotKey);
        Near(0d, plan.Segments[0].StartMs);
        Near(0d, plan.Segments[1].StartMs);
        Near(1250d, plan.Segments[1].DurationMs);
        Near(1250d, plan.TotalMs);
    }

    public static void TwoPresetsOnOneSlotStillAppend()
    {
        SlotActionQueuePlan plan = Plan(
            "#fx;3;sway;amplitude=25;frequency=2;cycles=2;direction=right",
            "#fx;3;squash;amplitude=0.3;frequency=4;cycles=5");
        AssertEx.Equal(2, plan.Segments.Count);
        AssertEx.Equal("preset:3", plan.Segments[0].SlotKey);
        AssertEx.Equal("preset:3", plan.Segments[1].SlotKey);
        Near(0d, plan.Segments[0].StartMs);
        // Only one preset can be active per slot at a time, so these two append even
        // though they live on the preset track.
        Near(1000d, plan.Segments[1].StartMs);
        Near(2250d, plan.TotalMs);
    }

    public static void SingleActionRowKeepsOneTrackAtOrigin()
    {
        SlotActionQueuePlan plan = Plan("#char;3;move;dx=10;duration=800;easing=linear");
        AssertEx.Equal(1, plan.Segments.Count);
        AssertEx.Equal("char:3", plan.Segments[0].SlotKey);
        Near(0d, plan.Segments[0].StartMs);
        Near(800d, plan.Segments[0].DurationMs);
        Near(800d, plan.TotalMs);
        AssertEx.False(plan.OverBudget);
        AssertEx.Equal(0, plan.Diagnostics.Count);
    }
}
