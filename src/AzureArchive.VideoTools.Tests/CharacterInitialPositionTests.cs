using AzureArchive.VideoTools.Core.Characters;
using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.VisualEditor;

namespace AzureArchive.VideoTools.Tests;

internal static class CharacterInitialPositionTests
{
    private static CharacterTransformCommand Parse(string text)
    {
        var result = new CharacterTransformDirectiveParser().Parse(text);
        AssertEx.True(result.Success, result.Error);
        return AssertEx.NotNull(result.Value);
    }

    private static readonly CharacterTransformState Official = new(
        new(0, 20, -3), new(12, 0, 18));
    private static readonly CharacterTransformBaseline Baseline = new("scene", 3, "mika", Official);

    public static void AbsoluteMovementStartsAtAAndEndsAtB()
    {
        CharacterTransformCommand command = Parse(
            "#char;3;set;startX=-900;startY=50;x=400;y=200;duration=600;easing=easeInOut");
        CharacterTransformTarget plan = AssertEx.NotNull(new CharacterTransformPlanner().Plan(command, Official, Baseline).Value);
        AssertEx.Equal<CharacterVector3?>(new(-900, 50, -3), plan.InitialPosition);
        AssertEx.Equal(new CharacterVector3(400, 200, -3), plan.State.Position);
        AssertEx.Equal(Official.LocalEulerAngles, plan.State.LocalEulerAngles);
        AssertEx.True(plan.PositionChanged);
        AssertEx.False(plan.RotationChanged);
        AssertEx.Equal(600, plan.DurationMilliseconds);
    }

    public static void ReplayFromDifferentLivePositionsKeepsExplicitA()
    {
        CharacterTransformCommand command = Parse("#char;3;set;startX=-900;startY=50;x=400;y=200;duration=600");
        var planner = new CharacterTransformPlanner();
        foreach (CharacterTransformState current in new[] { Official, Official with { Position = new(99, 777, -3) },
            Official with { Position = new(400, 200, -3) } })
        {
            CharacterTransformTarget restored = AssertEx.NotNull(planner.PlanReplayRestore(command, current, Baseline).Value);
            AssertEx.Equal(Official.Position, restored.State.Position);
            AssertEx.Equal<CharacterVector3?>(null, restored.InitialPosition);
            CharacterTransformTarget target = AssertEx.NotNull(planner.Plan(command, restored.State, Baseline).Value);
            AssertEx.Equal<CharacterVector3?>(new(-900, 50, -3), target.InitialPosition);
            AssertEx.Equal(new CharacterVector3(400, 200, -3), target.State.Position);
        }
    }

    public static void PartialStartAndStartOnlyPreserveUncontrolledAxes()
    {
        var planner = new CharacterTransformPlanner();
        CharacterTransformCommand command = Parse("#char;3;set;startX=-900;y=200;duration=500");
        CharacterTransformTarget target = AssertEx.NotNull(planner.Plan(command, Official, Baseline).Value);
        AssertEx.Equal<CharacterVector3?>(new(-900, 20, -3), target.InitialPosition);
        AssertEx.Equal(new CharacterVector3(-900, 200, -3), target.State.Position);
        CharacterTransformCommand startOnly = Parse("#char;3;set;startX=-700;duration=0");
        target = AssertEx.NotNull(planner.Plan(startOnly, Official, Baseline).Value);
        AssertEx.Equal(new CharacterVector3(-700, 20, -3), target.State.Position);
        CharacterTransformState live = Official with { Position = new(800, 90, -10) };
        CharacterTransformTarget restored = AssertEx.NotNull(planner.PlanReplayRestore(startOnly, live, Baseline).Value);
        AssertEx.Equal(new CharacterVector3(0, 90, -10), restored.State.Position);
        AssertEx.Equal(live.LocalEulerAngles, restored.State.LocalEulerAngles);
    }

    public static void RelativeMoveUsesExplicitStartInsteadOfLivePosition()
    {
        CharacterTransformCommand command = Parse("#char;3;move;startX=-800;startY=100;dx=300;dy=-20;drotation=10;duration=800");
        CharacterTransformTarget target = AssertEx.NotNull(new CharacterTransformPlanner().Plan(command, Official, Baseline).Value);
        AssertEx.Equal(new CharacterVector3(-500, 80, -3), target.State.Position);
        AssertEx.Equal(new CharacterVector3(12, 0, 28), target.State.LocalEulerAngles);
        AssertEx.Equal<CharacterVector3?>(new(-800, 100, -3), target.InitialPosition);
    }

    public static void ExistingCommandsKeepTheirInheritedStartAndResetOrigin()
    {
        var planner = new CharacterTransformPlanner();
        CharacterTransformState inherited = Official with { Position = new(400, 90, -3) };
        CharacterTransformTarget move = AssertEx.NotNull(planner.Plan(Parse("#char;3;move;dx=300"), inherited, Baseline).Value);
        AssertEx.Equal<CharacterVector3?>(null, move.InitialPosition);
        AssertEx.Equal(new CharacterVector3(700, 90, -3), move.State.Position);
        CharacterTransformTarget set = AssertEx.NotNull(planner.Plan(Parse("#char;3;set;x=800"), inherited, Baseline).Value);
        AssertEx.Equal<CharacterVector3?>(null, set.InitialPosition);
        AssertEx.Equal(new CharacterVector3(800, 90, -3), set.State.Position);
        CharacterTransformTarget reset = AssertEx.NotNull(planner.Plan(Parse("#char;3;reset"), move.State, Baseline).Value);
        AssertEx.Equal(Official.Position, reset.State.Position);
    }

    public static void InvalidStartsAndPendingSlotsFailWithoutDroppingProperties()
    {
        var parser = new CharacterTransformDirectiveParser();
        foreach (string text in new[] { "#char;3;set;startX=NaN;x=100", "#char;3;set;startY=Infinity",
            "#char;3;set;startX=1;STARTX=2", "#char;3;reset;startX=0", "#char;3;move;startX=0;drotation=30" })
            AssertEx.False(parser.Parse(text).Success, text);
        AssertEx.False(new SlotPendingCommandFamilyCompiler().ParseCanonical("#charp;3;set;startX=-900;x=100").Success);
        AssertEx.False(SlotPendingTargetPlanner.Compute(Parse("#char;3;set;startX=100"), Official).Success);
        var pending = new VisualCharacterDraftRequest(3, false, VisualCharacterDraftOperation.Set,
            100, null, null, null, 0, CharacterTransformEasing.Linear) { StartX = -900 };
        AssertEx.False(new VisualCharacterDraftBuilder().Build(pending).Success);
        CharacterTransformCommand overflow = Parse($"#char;3;move;startX={float.MaxValue:R};dx={float.MaxValue:R}");
        AssertEx.False(new CharacterTransformPlanner().Plan(overflow, Official, Baseline).Success);
    }

    public static void DraftRoundTripAndDocumentUpsertKeepStartInOneCommand()
    {
        var request = new VisualCharacterDraftRequest(3, true, VisualCharacterDraftOperation.Set,
            400, 200, 15, false, 600, CharacterTransformEasing.EaseInOut)
            { StartX = -900, StartY = 50, RotationXDegrees = 30 };
        var builder = new VisualCharacterDraftBuilder();
        string publicText = AssertEx.NotNull(builder.Build(request).Value);
        string original = "#wait;100\n#aavt;char;3;set;x=10\n#fl;keep";
        var edited = new EditorCommandDocumentComposer().PreviewUpsert(original,
            EditorCommandDocumentComposer.Revision(original), publicText);
        AssertEx.True(edited.Success, edited.Error);
        EditorCommandEditPreview value = AssertEx.NotNull(edited.Value);
        AssertEx.True(value.ReplacedExisting);
        AssertEx.True(value.UpdatedText.StartsWith("#wait;100\n", StringComparison.Ordinal));
        AssertEx.True(value.UpdatedText.EndsWith("\n#fl;keep", StringComparison.Ordinal));
        EmbeddedAavtExtraction extracted = new EmbeddedAavtDirectiveExtractor().Extract(value.UpdatedText);
        AssertEx.Equal(0, extracted.Errors.Count);
        AssertEx.Equal(1, extracted.Commands.Count);
        var read = builder.ReadCanonical(extracted.Commands.Single().CanonicalDirective);
        AssertEx.True(read.Success, read.Error);
        AssertEx.Equal(request, AssertEx.NotNull(read.Value));
        AssertEx.Equal(value.CanonicalPublicDirective, AssertEx.NotNull(builder.Build(read.Value!).Value));
    }
}
