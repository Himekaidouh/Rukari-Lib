using AzureArchive.VideoTools.Core.Characters;
using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Projects;

namespace AzureArchive.VideoTools.Tests;

internal static class PreviewChainTests
{
    private const string NodeA = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string NodeB = "bbbbbbbb-0000-0000-0000-000000000002";
    private const string Selection = "cccccccc-0000-0000-0000-000000000003";
    private const string NodeD = "dddddddd-0000-0000-0000-000000000004";

    public static void SameNodeChainFoldsThroughCommandlessScenes()
    {
        var project = Graph()
            .Add(ScriptNode(NodeA, null,
                Scene(NodeA, 0, slot3Occupant: "mika", directive: "#char;3;move;dx=500"),
                Scene(NodeA, 1, slot3Occupant: "mika"),
                Scene(NodeA, 2, slot3Occupant: "mika")))
            .Build(out var lookup);
        lookup.Add(Key(NodeA, 0), 3, "#char;3;move;dx=500");

        var resolver = new PreviewChainResolver(project);
        var result = resolver.Resolve(Key(NodeA, 2), new[] { 3 }, lookup.Lookup);

        AssertEx.True(result.Success, result.Error);
        PreviewChainSlotResolution slot = AssertEx.NotNull(result.Value).Slots.Single();
        AssertEx.Equal(1, slot.State.FoldedCommandCount);
        AssertEx.True(slot.HasInheritedStart);
        AssertEx.Equal(500f, slot.State.X.RelativeFromOfficial);
        AssertEx.False(slot.State.X.HasAbsoluteValue);
        AssertEx.Equal(PreviewChainStopReason.ProjectStart, slot.StopReason);
    }

    public static void SelectionNodeInheritsFromPreSelectionScene()
    {
        var project = Graph()
            .Add(ScriptNode(NodeA, new[] { Selection },
                Scene(NodeA, 0, slot3Occupant: "mika"),
                Scene(NodeA, 1, slot3Occupant: "mika",
                    directive: "#char;3;set;x=700;duration=0")))
            .Add(SelectionNode(Selection, NodeB))
            .Add(ScriptNode(NodeB, null,
                Scene(NodeB, 0, slot3Occupant: "mika",
                    directive: "#char;3;move;dx=-200;duration=0")))
            .Build(out var lookup);
        lookup.Add(Key(NodeA, 1), 3, "#char;3;set;x=700;duration=0");
        lookup.Add(Key(NodeB, 0), 3, "#char;3;move;dx=-200;duration=0");

        var resolver = new PreviewChainResolver(project);
        var result = resolver.Resolve(Key(NodeB, 0), new[] { 3 }, lookup.Lookup);

        AssertEx.True(result.Success, result.Error);
        PreviewChainSlotResolution slot = AssertEx.NotNull(result.Value).Slots.Single();
        AssertEx.Equal(1, slot.State.FoldedCommandCount);
        AssertEx.True(slot.State.X.HasAbsoluteValue);
        AssertEx.Equal(700f, slot.State.X.AbsoluteValue);
        AssertEx.Equal(PreviewChainStopReason.ProjectStart, slot.StopReason);
    }

    public static void MergeAmbiguityStopsChain()
    {
        var project = Graph()
            .Add(ScriptNode(NodeA, new[] { NodeD }, Scene(NodeA, 0, slot3Occupant: "mika")))
            .Add(ScriptNode(NodeB, new[] { NodeD }, Scene(NodeB, 0, slot3Occupant: "mika")))
            .Add(ScriptNode(NodeD, null,
                Scene(NodeD, 0, slot3Occupant: "mika"),
                Scene(NodeD, 1, slot3Occupant: "mika")))
            .Build(out var lookup);

        var resolver = new PreviewChainResolver(project);
        var result = resolver.Resolve(Key(NodeD, 0), new[] { 3 }, lookup.Lookup);

        AssertEx.True(result.Success, result.Error);
        PreviewChainSlotResolution slot = AssertEx.NotNull(result.Value).Slots.Single();
        AssertEx.Equal(0, slot.State.FoldedCommandCount);
        AssertEx.False(slot.HasInheritedStart);
        AssertEx.Equal(PreviewChainStopReason.AmbiguousGraph, slot.StopReason);
        AssertEx.True(slot.HasLineageIdentity);

        var nextResult = resolver.Resolve(Key(NodeD, 1), new[] { 3 }, lookup.Lookup);
        AssertEx.True(nextResult.Success, nextResult.Error);
        PreviewChainSlotResolution next = AssertEx.NotNull(nextResult.Value).Slots.Single();
        AssertEx.Equal(PreviewChainStopReason.AmbiguousGraph, next.StopReason);
        AssertEx.Equal(slot.LineageIdentity, next.LineageIdentity);
    }

    public static void OfficialPositionMoveTruncatesButKeepsOwnCommand()
    {
        var project = Graph()
            .Add(ScriptNode(NodeA, null,
                Scene(NodeA, 0, slot3Occupant: "mika",
                    directive: "#char;3;move;dx=500"),
                Scene(NodeA, 1,
                    slot3Occupant: "mika",
                    directive: "#char;3;set;rotation=15;duration=0",
                    slot3StartingPos: 3,
                    slot3EndingPos: 4),
                Scene(NodeA, 2, slot3Occupant: "mika")))
            .Build(out var lookup);
        lookup.Add(Key(NodeA, 0), 3, "#char;3;move;dx=500");
        lookup.Add(Key(NodeA, 1), 3, "#char;3;set;rotation=15;duration=0");

        var resolver = new PreviewChainResolver(project);
        var result = resolver.Resolve(Key(NodeA, 2), new[] { 3 }, lookup.Lookup);

        AssertEx.True(result.Success, result.Error);
        PreviewChainSlotResolution slot = AssertEx.NotNull(result.Value).Slots.Single();
        AssertEx.Equal(2, slot.State.FoldedCommandCount);
        AssertEx.True(slot.State.RotationZ.HasAbsoluteValue);
        AssertEx.Equal(15f, slot.State.RotationZ.AbsoluteValue);
        // Position is owned by the official move: uncontrolled past it.
        AssertEx.False(slot.State.X.IsControlled);
        AssertEx.False(slot.State.X.HasAbsoluteValue);
        AssertEx.Equal(0f, slot.State.X.RelativeFromOfficial);
        AssertEx.Equal(PreviewChainStopReason.OfficialPositionMove, slot.StopReason);
    }

    public static void RotationAndFlipInheritThroughOlderOfficialMove()
    {
        var project = Graph()
            .Add(ScriptNode(NodeA, null,
                Scene(NodeA, 0, slot3Occupant: "mika",
                    directive: "#char;3;set;rotation=-25;flipX=true;duration=0"),
                Scene(NodeA, 1,
                    slot3Occupant: "mika",
                    slot3StartingPos: 3,
                    slot3EndingPos: 4),
                Scene(NodeA, 2, slot3Occupant: "mika")))
            .Build(out var lookup);
        lookup.Add(Key(NodeA, 0), 3, "#char;3;set;rotation=-25;flipX=true;duration=0");

        var resolver = new PreviewChainResolver(project);
        var result = resolver.Resolve(Key(NodeA, 2), new[] { 3 }, lookup.Lookup);

        AssertEx.True(result.Success, result.Error);
        PreviewChainSlotResolution slot = AssertEx.NotNull(result.Value).Slots.Single();
        AssertEx.True(slot.State.RotationZ.HasAbsoluteValue);
        AssertEx.Equal(-25f, slot.State.RotationZ.AbsoluteValue);
        AssertEx.True(slot.State.FlippedFromOfficial);
        AssertEx.True(slot.State.FlipControlled);
        // The official move owns position: the stale offset must not leak.
        AssertEx.False(slot.State.X.IsControlled);
        AssertEx.False(slot.State.Y.IsControlled);
        AssertEx.Equal(PreviewChainStopReason.OfficialPositionMove, slot.StopReason);
    }

    public static void OccupantChangeTruncatesChain()
    {
        var project = Graph()
            .Add(ScriptNode(NodeA, null,
                Scene(NodeA, 0, slot3Occupant: "mika",
                    directive: "#char;3;move;dx=500"),
                Scene(NodeA, 1, slot3Occupant: "nagi"),
                Scene(NodeA, 2, slot3Occupant: "nagi")))
            .Build(out var lookup);
        lookup.Add(Key(NodeA, 0), 3, "#char;3;move;dx=500");

        var resolver = new PreviewChainResolver(project);
        var result = resolver.Resolve(Key(NodeA, 2), new[] { 3 }, lookup.Lookup);

        AssertEx.True(result.Success, result.Error);
        PreviewChainSlotResolution slot = AssertEx.NotNull(result.Value).Slots.Single();
        AssertEx.Equal(0, slot.State.FoldedCommandCount);
        AssertEx.Equal(PreviewChainStopReason.OccupantChange, slot.StopReason);
    }

    public static void OccupantReturnAfterAbsenceStartsFresh()
    {
        // The occupant leaves the slot (empty stretch) and returns later: the
        // fresh spawn at re-entry resets the pose, so the previous stint's
        // directives must not leak across the empty stretch.
        var project = Graph()
            .Add(ScriptNode(NodeA, null,
                Scene(NodeA, 0, slot3Occupant: "mika",
                    directive: "#char;3;set;rotation=-25;flipX=true;duration=0"),
                Scene(NodeA, 1, slot3Occupant: "mika"),
                Scene(NodeA, 2, slot3Occupant: "nagi"),
                Scene(NodeA, 3, slot3Occupant: "nagi"),
                Scene(NodeA, 4, slot3Occupant: "mika"),
                Scene(NodeA, 5, slot3Occupant: "mika")))
            .Build(out var lookup);
        lookup.Add(Key(NodeA, 0), 3, "#char;3;set;rotation=-25;flipX=true;duration=0");

        var resolver = new PreviewChainResolver(project);
        var result = resolver.Resolve(Key(NodeA, 5), new[] { 3 }, lookup.Lookup);

        AssertEx.True(result.Success, result.Error);
        PreviewChainSlotResolution slot = AssertEx.NotNull(result.Value).Slots.Single();
        AssertEx.Equal(0, slot.State.FoldedCommandCount);
        AssertEx.False(slot.HasInheritedStart);
        AssertEx.False(slot.State.RotationZ.IsControlled);
        AssertEx.False(slot.State.FlipControlled);
        AssertEx.Equal(PreviewChainStopReason.OccupantChange, slot.StopReason);
    }

    public static void SlotPendingSeedsOccupiedPreviewAcrossEmptyEntryScenes()
    {
        var project = Graph()
            .Add(ScriptNode(NodeA, null,
                Scene(NodeA, 0,
                    directive: "#aavt;charPending;3;set;x=1750;rotation=0"),
                Scene(NodeA, 1),
                Scene(NodeA, 2, slot3Occupant: "hoshino"),
                Scene(NodeA, 3, slot3Occupant: "hoshino",
                    directive: "#aavt;char;3;set;x=1550;rotation=10;duration=600;easing=easeOut")))
            .Build(out _);
        PreviewChainDirectiveIndex index = PreviewChainDirectiveIndex.Build(
            project,
            acceptLegacyCharacterAlias: true);

        var resolver = new PreviewChainResolver(project);
        var result = resolver.Resolve(
            Key(NodeA, 3),
            new[] { 3 },
            (key, slot) => index.TryGet(key.NodeGuid, key.SceneIndex, slot, out string directive)
                ? directive
                : null);

        AssertEx.True(result.Success, result.Error);
        PreviewChainSlotResolution slot = AssertEx.NotNull(result.Value).Slots.Single();
        AssertEx.Equal(1, slot.State.FoldedCommandCount);
        AssertEx.True(slot.State.X.HasAbsoluteValue);
        AssertEx.Equal(1750f, slot.State.X.AbsoluteValue);
        AssertEx.True(slot.State.RotationZ.HasAbsoluteValue);
        AssertEx.Equal(0f, slot.State.RotationZ.AbsoluteValue);
        AssertEx.Equal("hoshino", slot.ExpectedOccupant);
        AssertEx.Equal(PreviewChainStopReason.OccupantChange, slot.StopReason);
        AssertEx.True(slot.Diagnostics.Contains("slot-pending-entry=true"));
    }

    public static void CurrentSceneOfficialMoveOwnsPositionOnly()
    {
        var project = Graph()
            .Add(ScriptNode(NodeA, null,
                Scene(NodeA, 0, slot3Occupant: "mika",
                    directive: "#char;3;set;rotation=-10;flipX=true;duration=0"),
                Scene(NodeA, 1,
                    slot3Occupant: "mika",
                    slot3StartingPos: 3,
                    slot3EndingPos: 4)))
            .Build(out var lookup);
        lookup.Add(Key(NodeA, 0), 3, "#char;3;set;rotation=-10;flipX=true;duration=0");

        var resolver = new PreviewChainResolver(project);
        var result = resolver.Resolve(Key(NodeA, 1), new[] { 3 }, lookup.Lookup);

        AssertEx.True(result.Success, result.Error);
        PreviewChainSlotResolution slot = AssertEx.NotNull(result.Value).Slots.Single();
        // Rotation and flip persist through the official move on the live
        // transform, so the preview must keep inheriting them.
        AssertEx.Equal(1, slot.State.FoldedCommandCount);
        AssertEx.True(slot.State.RotationZ.HasAbsoluteValue);
        AssertEx.Equal(-10f, slot.State.RotationZ.AbsoluteValue);
        AssertEx.True(slot.State.FlippedFromOfficial);
        AssertEx.True(slot.State.FlipControlled);
        // Position belongs to the official move and stays uncontrolled.
        AssertEx.False(slot.State.X.IsControlled);
        AssertEx.False(slot.State.Y.IsControlled);
        AssertEx.Equal(PreviewChainStopReason.CurrentSceneOfficialMove, slot.StopReason);
    }

    public static void CyclicGraphFailsClosed()
    {
        var project = Graph()
            .Add(ScriptNode(NodeA, new[] { NodeB },
                Scene(NodeA, 0, slot3Occupant: "mika"),
                Scene(NodeA, 1, slot3Occupant: "mika")))
            .Add(ScriptNode(NodeB, new[] { NodeA },
                Scene(NodeB, 0, slot3Occupant: "mika")))
            .Build(out var lookup);

        var resolver = new PreviewChainResolver(project);
        var result = resolver.Resolve(Key(NodeA, 0), new[] { 3 }, lookup.Lookup);

        AssertEx.True(result.Success, result.Error);
        PreviewChainSlotResolution slot = AssertEx.NotNull(result.Value).Slots.Single();
        AssertEx.Equal(0, slot.State.FoldedCommandCount);
        AssertEx.Equal(PreviewChainStopReason.CycleGuard, slot.StopReason);
        AssertEx.True(slot.HasLineageIdentity);

        var otherResult = resolver.Resolve(Key(NodeB, 0), new[] { 3 }, lookup.Lookup);
        AssertEx.True(otherResult.Success, otherResult.Error);
        PreviewChainSlotResolution other = AssertEx.NotNull(otherResult.Value).Slots.Single();
        AssertEx.True(other.HasLineageIdentity);
        AssertEx.False(string.Equals(
            slot.LineageIdentity,
            other.LineageIdentity,
            StringComparison.Ordinal));
    }

    public static void LineageIdentityChangesOnlyAtCharacterBoundaries()
    {
        var project = Graph()
            .Add(ScriptNode(NodeA, null,
                Scene(NodeA, 0, slot3Occupant: "mika"),
                Scene(NodeA, 1, slot3Occupant: "mika"),
                Scene(
                    NodeA,
                    2,
                    slot3Occupant: "mika",
                    slot3StartingPos: 3,
                    slot3EndingPos: 4),
                Scene(NodeA, 3, slot3Occupant: "mika"),
                Scene(NodeA, 4, slot3Occupant: "nagi"),
                Scene(NodeA, 5, slot3Occupant: "nagi")))
            .Build(out var lookup);
        var resolver = new PreviewChainResolver(project);

        PreviewChainSlotResolution mikaStart = ResolveSlot(resolver, lookup, NodeA, 0);
        PreviewChainSlotResolution mikaNext = ResolveSlot(resolver, lookup, NodeA, 1);
        PreviewChainSlotResolution officialMove = ResolveSlot(resolver, lookup, NodeA, 2);
        PreviewChainSlotResolution afterOfficialMove = ResolveSlot(resolver, lookup, NodeA, 3);
        PreviewChainSlotResolution nagiStart = ResolveSlot(resolver, lookup, NodeA, 4);
        PreviewChainSlotResolution nagiNext = ResolveSlot(resolver, lookup, NodeA, 5);

        AssertEx.Equal(mikaStart.LineageIdentity, mikaNext.LineageIdentity);
        AssertEx.False(string.Equals(
            mikaStart.LineageIdentity,
            officialMove.LineageIdentity,
            StringComparison.Ordinal));
        AssertEx.Equal(officialMove.LineageIdentity, afterOfficialMove.LineageIdentity);
        AssertEx.False(string.Equals(
            afterOfficialMove.LineageIdentity,
            nagiStart.LineageIdentity,
            StringComparison.Ordinal));
        AssertEx.Equal(nagiStart.LineageIdentity, nagiNext.LineageIdentity);
        AssertEx.Equal("mika", officialMove.ExpectedOccupant);
        AssertEx.True(officialMove.HasLineageIdentity);
    }

    public static void HoshinoContinuousLineageResetReturnsToOrigin()
    {
        var project = Graph()
            .Add(ScriptNode(NodeA, null,
                Scene(NodeA, 0, slot3Occupant: "hoshino",
                    directive: "#char;3;set;x=500;rotation=12;duration=350"),
                Scene(NodeA, 1, slot3Occupant: "hoshino",
                    directive: "#char;3;set;x=600;rotation=12;duration=350"),
                Scene(NodeA, 2, slot3Occupant: "hoshino",
                    directive: "#char;3;set;x=200;rotation=0;duration=350"),
                Scene(NodeA, 3, slot3Occupant: "hoshino",
                    directive: "#char;3;set;x=300;rotation=-7;duration=500"),
                Scene(NodeA, 4, slot3Occupant: "hoshino"),
                Scene(NodeA, 5, slot3Occupant: "hoshino",
                    directive: "#char;3;reset;duration=600;easing=easeInOut")))
            .Build(out var lookup);
        lookup.Add(Key(NodeA, 0), 3, "#char;3;set;x=500;rotation=12;duration=350");
        lookup.Add(Key(NodeA, 1), 3, "#char;3;set;x=600;rotation=12;duration=350");
        lookup.Add(Key(NodeA, 2), 3, "#char;3;set;x=200;rotation=0;duration=350");
        lookup.Add(Key(NodeA, 3), 3, "#char;3;set;x=300;rotation=-7;duration=500");
        lookup.Add(Key(NodeA, 5), 3, "#char;3;reset;duration=600;easing=easeInOut");
        var resolver = new PreviewChainResolver(project);

        PreviewChainSlotResolution firstScene = ResolveSlot(resolver, lookup, NodeA, 0);
        PreviewChainSlotResolution resetScene = ResolveSlot(resolver, lookup, NodeA, 5);
        AssertEx.Equal(firstScene.LineageIdentity, resetScene.LineageIdentity);
        AssertEx.Equal(4, resetScene.State.FoldedCommandCount);

        CharacterTransformState official = State(0, 0, -1, 0, 0, 0);
        var originStore = new CharacterTransformOriginBaselineStore();
        CharacterTransformOriginBaseline origin = AssertEx.NotNull(
            originStore.Capture(
                firstScene.LineageIdentity,
                3,
                "hoshino",
                404,
                official).Value);
        var officialForScene = new CharacterTransformBaseline(
            "scene-reset",
            3,
            "hoshino",
            official);
        CharacterInheritedStartTarget inherited = AssertEx.NotNull(
            new CharacterInheritedStartPlanner().Plan(
                resetScene.State,
                official,
                officialForScene).Value);
        AssertEx.Equal(300f, inherited.State.Position.X);
        AssertEx.Equal(-7f, inherited.State.LocalEulerAngles.Z);

        CharacterTransformOriginBaseline retained = AssertEx.NotNull(
            originStore.Capture(
                resetScene.LineageIdentity,
                3,
                "hoshino",
                404,
                inherited.State).Value);
        AssertEx.Equal(origin.State, retained.State);
        CharacterTransformCommand reset = AssertEx.NotNull(
            new CharacterTransformDirectiveParser().Parse(
                "#char;3;reset;duration=600;easing=easeInOut").Value);
        var originForReset = new CharacterTransformBaseline(
            "scene-reset",
            retained.PublicSlot,
            retained.OccupantIdentifier,
            retained.State);
        CharacterTransformTarget resetTarget = AssertEx.NotNull(
            new CharacterTransformPlanner().Plan(
                reset,
                inherited.State,
                originForReset).Value);

        AssertEx.Equal(0f, resetTarget.State.Position.X);
        AssertEx.Equal(0f, CharacterScreenRotation.ToScreenDegrees(
            resetTarget.State.LocalEulerAngles.Z,
            resetTarget.State.LocalEulerAngles.Y));
    }

    public static void FoldComposesAbsoluteRelativeAndFlip()
    {
        var project = Graph()
            .Add(ScriptNode(NodeA, null,
                Scene(NodeA, 0, slot3Occupant: "mika"),
                Scene(NodeA, 1, slot3Occupant: "mika",
                    directive: "#char;3;set;x=700;duration=0"),
                Scene(NodeA, 2, slot3Occupant: "mika",
                    directive: "#char;3;move;dx=100;duration=0"),
                Scene(NodeA, 3, slot3Occupant: "mika",
                    directive: "#char;3;move;dy=50;duration=0"),
                Scene(NodeA, 4, slot3Occupant: "mika",
                    directive: "#char;3;set;rotation=15;flipX=true;duration=0"),
                Scene(NodeA, 5, slot3Occupant: "mika",
                    directive: "#char;3;move;drotation=-10;duration=0")))
            .Build(out var lookup);
        lookup.Add(Key(NodeA, 1), 3, "#char;3;set;x=700;duration=0");
        lookup.Add(Key(NodeA, 2), 3, "#char;3;move;dx=100;duration=0");
        lookup.Add(Key(NodeA, 3), 3, "#char;3;move;dy=50;duration=0");
        lookup.Add(Key(NodeA, 4), 3, "#char;3;set;rotation=15;flipX=true;duration=0");
        lookup.Add(Key(NodeA, 5), 3, "#char;3;move;drotation=-10;duration=0");

        var resolver = new PreviewChainResolver(project);
        var result = resolver.Resolve(Key(NodeA, 5), new[] { 3 }, lookup.Lookup);

        AssertEx.True(result.Success, result.Error);
        PreviewChainSlotResolution slot = AssertEx.NotNull(result.Value).Slots.Single();
        AssertEx.Equal(4, slot.State.FoldedCommandCount);
        AssertEx.True(slot.State.X.HasAbsoluteValue);
        AssertEx.Equal(800f, slot.State.X.AbsoluteValue);
        AssertEx.False(slot.State.Y.HasAbsoluteValue);
        AssertEx.Equal(50f, slot.State.Y.RelativeFromOfficial);
        AssertEx.True(slot.State.RotationZ.HasAbsoluteValue);
        AssertEx.Equal(15f, slot.State.RotationZ.AbsoluteValue);
        AssertEx.True(slot.State.FlippedFromOfficial);
        AssertEx.True(slot.State.FlipControlled);
        AssertEx.True(slot.State.X.IsControlled);
        AssertEx.True(slot.State.Y.IsControlled);
        AssertEx.True(slot.State.RotationZ.IsControlled);
    }

    public static void FoldResetClearsAccumulatedChain()
    {
        var project = Graph()
            .Add(ScriptNode(NodeA, null,
                Scene(NodeA, 0, slot3Occupant: "mika",
                    directive: "#char;3;set;x=700;flipX=true;duration=0"),
                Scene(NodeA, 1, slot3Occupant: "mika",
                    directive: "#char;3;reset;duration=0"),
                Scene(NodeA, 2, slot3Occupant: "mika")))
            .Build(out var lookup);
        lookup.Add(Key(NodeA, 0), 3, "#char;3;set;x=700;flipX=true;duration=0");
        lookup.Add(Key(NodeA, 1), 3, "#char;3;reset;duration=0");

        var resolver = new PreviewChainResolver(project);
        var result = resolver.Resolve(Key(NodeA, 2), new[] { 3 }, lookup.Lookup);

        AssertEx.True(result.Success, result.Error);
        PreviewChainSlotResolution slot = AssertEx.NotNull(result.Value).Slots.Single();
        AssertEx.Equal(2, slot.State.FoldedCommandCount);
        AssertEx.False(slot.State.X.HasAbsoluteValue);
        AssertEx.Equal(0f, slot.State.X.RelativeFromOfficial);
        AssertEx.False(slot.State.FlippedFromOfficial);
        AssertEx.True(slot.State.X.IsControlled);
        AssertEx.True(slot.State.Y.IsControlled);
        AssertEx.True(slot.State.RotationZ.IsControlled);
        AssertEx.True(slot.State.FlipControlled);
    }

    public static void SetOnlyMarksTouchedAxesAsControlled()
    {
        var project = Graph()
            .Add(ScriptNode(NodeA, null,
                Scene(NodeA, 0, slot3Occupant: "mika",
                    directive: "#char;3;set;x=600;rotation=12;duration=350"),
                Scene(NodeA, 1, slot3Occupant: "mika")))
            .Build(out var lookup);
        lookup.Add(Key(NodeA, 0), 3, "#char;3;set;x=600;rotation=12;duration=350");

        var resolver = new PreviewChainResolver(project);
        var result = resolver.Resolve(Key(NodeA, 1), new[] { 3 }, lookup.Lookup);

        AssertEx.True(result.Success, result.Error);
        PreviewChainSlotResolution slot = AssertEx.NotNull(result.Value).Slots.Single();
        AssertEx.Equal(1, slot.State.FoldedCommandCount);
        AssertEx.True(slot.State.X.IsControlled);
        AssertEx.True(slot.State.RotationZ.IsControlled);
        // The chain never mentioned Y or horizontal orientation, so the
        // planner must keep the live values of the current official scene.
        AssertEx.False(slot.State.Y.IsControlled);
        AssertEx.False(slot.State.FlipControlled);
        AssertEx.False(slot.State.FlippedFromOfficial);
    }

    public static void FoldPreservesAxisControlAcrossSetMoveReset()
    {
        var project = Graph()
            .Add(ScriptNode(NodeA, null,
                Scene(NodeA, 0, slot3Occupant: "mika",
                    directive: "#char;3;set;x=500;flipX=true;duration=0"),
                Scene(NodeA, 1, slot3Occupant: "mika",
                    directive: "#char;3;move;dx=100;drotation=-20;duration=0"),
                Scene(NodeA, 2, slot3Occupant: "mika",
                    directive: "#char;3;reset;duration=0"),
                Scene(NodeA, 3, slot3Occupant: "mika",
                    directive: "#char;3;move;dy=70;duration=0"),
                Scene(NodeA, 4, slot3Occupant: "mika")))
            .Build(out var lookup);
        lookup.Add(Key(NodeA, 0), 3, "#char;3;set;x=500;flipX=true;duration=0");
        lookup.Add(Key(NodeA, 1), 3, "#char;3;move;dx=100;drotation=-20;duration=0");
        lookup.Add(Key(NodeA, 2), 3, "#char;3;reset;duration=0");
        lookup.Add(Key(NodeA, 3), 3, "#char;3;move;dy=70;duration=0");

        var resolver = new PreviewChainResolver(project);
        var result = resolver.Resolve(Key(NodeA, 4), new[] { 3 }, lookup.Lookup);

        AssertEx.True(result.Success, result.Error);
        PreviewChainSlotResolution slot = AssertEx.NotNull(result.Value).Slots.Single();
        AssertEx.Equal(4, slot.State.FoldedCommandCount);

        // Reset returned X and rotation to explicit official-origin control,
        // the later dy move controls only Y, and the early flip stays
        // controlled with its value cleared back to the official orientation.
        AssertEx.True(slot.State.X.IsControlled);
        AssertEx.False(slot.State.X.HasAbsoluteValue);
        AssertEx.Equal(0f, slot.State.X.RelativeFromOfficial);
        AssertEx.True(slot.State.RotationZ.IsControlled);
        AssertEx.True(slot.State.Y.IsControlled);
        AssertEx.Equal(70f, slot.State.Y.RelativeFromOfficial);
        AssertEx.True(slot.State.FlipControlled);
        AssertEx.False(slot.State.FlippedFromOfficial);
    }

    public static void DirectiveIndexBuildsCanonicalLookupFromAdditionalPrompt()
    {
        var project = Graph()
            .Add(ScriptNode(NodeA, null,
                Scene(NodeA, 0,
                    slot3Occupant: "mika",
                    directive: "普通台词\n#aavt;char;3;move;dx=500"),
                Scene(NodeA, 1,
                    slot3Occupant: "mika",
                    directive: "\n#char;4;set;x=700"),
                Scene(NodeA, 2, slot3Occupant: "mika")))
            .Build(out _);

        var index = PreviewChainDirectiveIndex.Build(project, acceptLegacyCharacterAlias: true);

        AssertEx.Equal(2, index.SceneCount);
        AssertEx.Equal(2, index.DirectiveCount);
        AssertEx.True(index.TryGet(NodeA, 0, 3, out string move));
        AssertEx.Equal("#char;3;move;dx=500;duration=0;easing=linear", move);
        AssertEx.True(index.TryGet(NodeA, 1, 4, out string set));
        AssertEx.Equal("#char;4;set;x=700;duration=0;easing=linear", set);
        AssertEx.False(index.TryGet(NodeA, 0, 2, out _));
        AssertEx.False(index.TryGet(NodeA, 1, 3, out _));
        AssertEx.False(index.TryGet("missing-node", 0, 3, out _));
    }

    public static void DirectiveIndexIgnoresCameraResources()
    {
        string directives = string.Join('\n', new[]
        {
            "#aavt;char;1;set;x=100",
            "#aavt;char;2;set;x=200",
            "#aavt;char;3;set;x=300",
            "#aavt;char;4;set;x=400",
            "#aavt;char;5;set;x=500",
            "#aavt;camera;set;zoom=1.5"
        });
        var project = Graph()
            .Add(ScriptNode(NodeA, null,
                Scene(NodeA, 0, slot3Occupant: "mika", directive: directives)))
            .Build(out _);

        var index = PreviewChainDirectiveIndex.Build(
            project,
            acceptLegacyCharacterAlias: true);

        AssertEx.Equal(0, index.SkippedSceneCount);
        AssertEx.Equal(1, index.SceneCount);
        AssertEx.Equal(5, index.DirectiveCount);
        for (int slot = 1; slot <= 5; slot++)
        {
            AssertEx.True(index.TryGet(NodeA, 0, slot, out _));
        }

        AssertEx.False(index.TryGet(NodeA, 0, 0, out _));
    }

    public static void DirectiveIndexSkipsScenesWithMalformedDirectives()
    {
        var project = Graph()
            .Add(ScriptNode(NodeA, null,
                Scene(NodeA, 0,
                    slot3Occupant: "mika",
                    directive: "#aavt;camera;set;zoom=0\n#char;3;move;dx=500")))
            .Build(out _);

        var index = PreviewChainDirectiveIndex.Build(project, acceptLegacyCharacterAlias: true);

        AssertEx.Equal(1, index.SkippedSceneCount);
        AssertEx.Equal(0, index.SceneCount);
        AssertEx.False(index.TryGet(NodeA, 0, 3, out _));
    }

    public static void DirectiveIndexFeedsResolverEndToEnd()
    {
        var project = Graph()
            .Add(ScriptNode(NodeA, null,
                Scene(NodeA, 0,
                    slot3Occupant: "mika",
                    directive: "#char;3;move;dx=500"),
                Scene(NodeA, 1, slot3Occupant: "mika",
                    directive: "#char;3;move;dx=100"),
                Scene(NodeA, 2, slot3Occupant: "mika")))
            .Build(out _);
        var index = PreviewChainDirectiveIndex.Build(project, acceptLegacyCharacterAlias: true);

        var resolver = new PreviewChainResolver(project);
        var result = resolver.Resolve(
            Key(NodeA, 2),
            new[] { 3 },
            (key, slot) => index.TryGet(key.NodeGuid, key.SceneIndex, slot, out string directive)
                ? directive
                : null);

        AssertEx.True(result.Success, result.Error);
        PreviewChainSlotResolution slot = AssertEx.NotNull(result.Value).Slots.Single();
        AssertEx.Equal(2, slot.State.FoldedCommandCount);
        AssertEx.Equal(600f, slot.State.X.RelativeFromOfficial);
        AssertEx.Equal("mika", slot.ExpectedOccupant);
        AssertEx.True(slot.HasExpectedOccupant);
    }

    public static void ResolverExposesEmptyOccupantForUnoccupiedSlots()
    {
        var project = Graph()
            .Add(ScriptNode(NodeA, null,
                Scene(NodeA, 0, slot3Occupant: "mika",
                    directive: "#char;3;move;dx=500"),
                Scene(NodeA, 1, slot3Occupant: "mika")))
            .Build(out _);

        var resolver = new PreviewChainResolver(project);
        var result = resolver.Resolve(Key(NodeA, 1), new[] { 4 }, (_, _) => null);

        AssertEx.True(result.Success, result.Error);
        PreviewChainSlotResolution slot = AssertEx.NotNull(result.Value).Slots.Single();
        AssertEx.Equal(0, slot.State.FoldedCommandCount);
        AssertEx.Equal(string.Empty, slot.ExpectedOccupant);
        AssertEx.False(slot.HasExpectedOccupant);
    }

    public static void PitchChainFoldsSetMoveAndResetInChronologicalOrder()
    {
        var project = Graph().Add(ScriptNode(NodeA, null,
            Scene(NodeA, 0, slot3Occupant: "mika", directive: "#char;3;set;rotationX=30;rotation=15"),
            Scene(NodeA, 1, slot3Occupant: "mika", directive: "#char;3;move;drotationX=10"),
            Scene(NodeA, 2, slot3Occupant: "mika", directive: "#char;3;reset"),
            Scene(NodeA, 3, slot3Occupant: "mika", directive: "#char;3;move;drotationX=20"),
            Scene(NodeA, 4, slot3Occupant: "mika")))
            .Build(out var lookup);
        lookup.Add(Key(NodeA, 0), 3, "#char;3;set;rotationX=30;rotation=15");
        lookup.Add(Key(NodeA, 1), 3, "#char;3;move;drotationX=10");
        lookup.Add(Key(NodeA, 2), 3, "#char;3;reset");
        lookup.Add(Key(NodeA, 3), 3, "#char;3;move;drotationX=20");
        var resolver = new PreviewChainResolver(project);
        PreviewChainSlotResolution beforeReset = ResolveSlot(resolver, lookup, NodeA, 2);
        AssertEx.Equal(40f, beforeReset.State.RotationX.AbsoluteValue);
        AssertEx.True(beforeReset.State.RotationX.IsControlled);
        AssertEx.Equal(15f, beforeReset.State.RotationZ.AbsoluteValue);
        PreviewChainSlotResolution afterReset = ResolveSlot(resolver, lookup, NodeA, 4);
        AssertEx.Equal(4, afterReset.State.FoldedCommandCount);
        AssertEx.False(afterReset.State.RotationX.HasAbsoluteValue);
        AssertEx.Equal(20f, afterReset.State.RotationX.RelativeFromOfficial);
        AssertEx.True(afterReset.State.RotationX.IsControlled);
        AssertEx.Equal(0f, afterReset.State.RotationZ.RelativeFromOfficial);
        AssertEx.True(afterReset.State.RotationZ.IsControlled);
        CharacterTransformState official = State(0, 0, -1, 4, 10, 15);
        CharacterInheritedStartTarget inherited = AssertEx.NotNull(new CharacterInheritedStartPlanner().Plan(
            afterReset.State, State(20, 30, -1, 200, 55, -25),
            new CharacterTransformBaseline("scene", 3, "mika", official)).Value);
        AssertEx.Equal(24f, inherited.State.LocalEulerAngles.X);
    }

    public static void PitchSurvivesOfficialPositionMovesButStopsAtOccupantChanges()
    {
        var project = Graph().Add(ScriptNode(NodeA, null,
            Scene(NodeA, 0, slot3Occupant: "mika", directive: "#char;3;set;x=500;rotationX=120"),
            Scene(NodeA, 1, slot3Occupant: "mika", slot3StartingPos: 3, slot3EndingPos: 4),
            Scene(NodeA, 2, slot3Occupant: "mika"),
            Scene(NodeA, 3, slot3Occupant: "nagi"),
            Scene(NodeA, 4, slot3Occupant: "nagi")))
            .Build(out var lookup);
        lookup.Add(Key(NodeA, 0), 3, "#char;3;set;x=500;rotationX=120");
        var resolver = new PreviewChainResolver(project);
        foreach (int index in new[] { 1, 2 })
        {
            PreviewChainSlotResolution slot = ResolveSlot(resolver, lookup, NodeA, index);
            AssertEx.True(slot.State.RotationX.IsControlled);
            AssertEx.Equal(120f, slot.State.RotationX.AbsoluteValue);
            AssertEx.False(slot.State.X.IsControlled);
        }
        PreviewChainSlotResolution replacement = ResolveSlot(resolver, lookup, NodeA, 4);
        AssertEx.False(replacement.State.RotationX.IsControlled);
        AssertEx.Equal(0, replacement.State.FoldedCommandCount);
        AssertEx.Equal(PreviewChainStopReason.OccupantChange, replacement.StopReason);
    }

    public static void PitchDirectiveIndexPreservesEmptySlotEntryAndCommandlessCards()
    {
        var project = Graph().Add(ScriptNode(NodeA, null,
            Scene(NodeA, 0, directive: "#aavt;charPending;3;set;rotationX=120;rotation=15"),
            Scene(NodeA, 1),
            Scene(NodeA, 2, slot3Occupant: "mika"),
            Scene(NodeA, 3, slot3Occupant: "mika")))
            .Build(out _);
        var index = PreviewChainDirectiveIndex.Build(project, acceptLegacyCharacterAlias: true);
        AssertEx.True(index.TryGet(NodeA, 0, 3, out string directive));
        AssertEx.True(directive.Contains("rotationX=120", StringComparison.Ordinal));
        var resolver = new PreviewChainResolver(project);
        var result = resolver.Resolve(Key(NodeA, 3), new[] { 3 },
            (key, slot) => index.TryGet(key.NodeGuid, key.SceneIndex, slot, out string canonical) ? canonical : null);
        AssertEx.True(result.Success, result.Error);
        PreviewChainSlotResolution inherited = AssertEx.NotNull(result.Value).Slots.Single();
        AssertEx.Equal(1, inherited.State.FoldedCommandCount);
        AssertEx.Equal(120f, inherited.State.RotationX.AbsoluteValue);
        AssertEx.True(inherited.State.RotationX.IsControlled);
        AssertEx.Equal(15f, inherited.State.RotationZ.AbsoluteValue);
        AssertEx.True(inherited.Diagnostics.Contains("slot-pending-entry=true"));
    }

    public static void AmbiguousPredecessorsCannotDonatePitchEvidence()
    {
        var project = Graph()
            .Add(ScriptNode(NodeA, new[] { NodeD }, Scene(NodeA, 0, slot3Occupant: "mika", directive: "#char;3;set;rotationX=120")))
            .Add(ScriptNode(NodeB, new[] { NodeD }, Scene(NodeB, 0, slot3Occupant: "mika", directive: "#char;3;set;rotationX=-30")))
            .Add(ScriptNode(NodeD, null, Scene(NodeD, 0, slot3Occupant: "mika")))
            .Build(out var lookup);
        lookup.Add(Key(NodeA, 0), 3, "#char;3;set;rotationX=120");
        lookup.Add(Key(NodeB, 0), 3, "#char;3;set;rotationX=-30");
        PreviewChainSlotResolution slot = ResolveSlot(new PreviewChainResolver(project), lookup, NodeD, 0);
        AssertEx.Equal(PreviewChainStopReason.AmbiguousGraph, slot.StopReason);
        AssertEx.False(slot.State.RotationX.IsControlled);
        AssertEx.Equal(0, slot.State.FoldedCommandCount);
    }

    private static PreviewChainSlotResolution ResolveSlot(
        PreviewChainResolver resolver,
        DirectiveMap lookup,
        string nodeGuid,
        int sceneIndex)
    {
        var result = resolver.Resolve(
            Key(nodeGuid, sceneIndex),
            new[] { 3 },
            lookup.Lookup);
        AssertEx.True(result.Success, result.Error);
        return AssertEx.NotNull(result.Value).Slots.Single();
    }

    private static CharacterTransformState State(
        float x,
        float y,
        float z,
        float rotationX,
        float rotationY,
        float rotationZ) => new(
            new CharacterVector3(x, y, z),
            new CharacterVector3(rotationX, rotationY, rotationZ));

    private static SceneKey Key(string nodeGuid, int sceneIndex) =>
        new(nodeGuid, sceneIndex, Fingerprint(nodeGuid, sceneIndex));

    private static string Fingerprint(string nodeGuid, int sceneIndex)
    {
        string seed = $"{nodeGuid}:{sceneIndex}";
        return seed.PadRight(64, '0')[..64];
    }

    private static GraphBuilder Graph() => new();

    private sealed class DirectiveMap
    {
        private readonly Dictionary<SceneKey, Dictionary<int, string>> _map = new();

        public void Add(SceneKey sceneKey, int slot, string directive)
        {
            if (!_map.TryGetValue(sceneKey, out Dictionary<int, string>? slots))
            {
                _map[sceneKey] = slots = new Dictionary<int, string>();
            }

            slots[slot] = directive;
        }

        public string? Lookup(SceneKey sceneKey, int slot) =>
            _map.TryGetValue(sceneKey, out Dictionary<int, string>? slots)
            && slots.TryGetValue(slot, out string? directive)
                ? directive
                : null;
    }

    private sealed class GraphBuilder
    {
        private readonly List<StoryNodeSnapshot> _nodes = new();

        public GraphBuilder Add(StoryNodeSnapshot node)
        {
            _nodes.Add(node);
            return this;
        }

        public ProjectSnapshot Build(out DirectiveMap directives)
        {
            directives = new DirectiveMap();
            return new ProjectSnapshot(
                new ProjectSourceSnapshot(
                    "synthetic.aap",
                    "path-key",
                    new string('A', 64),
                    1,
                    DateTimeOffset.UtcNow),
                "ProjectData, Assembly-CSharp",
                "Synthetic",
                new ProjectPreviewSnapshot(null, string.Empty, string.Empty),
                Array.AsReadOnly(_nodes.ToArray()),
                Array.Empty<ProjectDiagnostic>());
        }
    }

    private static StoryNodeSnapshot ScriptNode(
        string nodeGuid,
        IReadOnlyList<string>? outgoingConnections,
        params SceneSnapshot[] scenes) => new(
        0,
        StoryNodeKind.Script,
        "ScriptNodeData, Assembly-CSharp",
        nodeGuid,
        true,
        "Node",
        outgoingConnections ?? Array.Empty<string>(),
        Array.AsReadOnly(scenes));

    private static StoryNodeSnapshot SelectionNode(string nodeGuid, string target) => new(
        0,
        StoryNodeKind.Selection,
        "SelectionNodeData, Assembly-CSharp",
        nodeGuid,
        true,
        "Sel.",
        new[] { target },
        Array.Empty<SceneSnapshot>());

    private static SceneSnapshot Scene(
        string nodeGuid,
        int sceneIndex,
        string slot3Occupant = "",
        string directive = "",
        int slot3StartingPos = 0,
        int slot3EndingPos = 0)
    {
        var characters = new List<ProjectCharacterSnapshot>();
        if (!string.IsNullOrEmpty(slot3Occupant))
        {
            characters.Add(new ProjectCharacterSnapshot(
                3,
                slot3Occupant,
                "03",
                slot3StartingPos,
                slot3EndingPos,
                0,
                0,
                0,
                0,
                0));
        }

        return new SceneSnapshot(
            Key(nodeGuid, sceneIndex),
            "ScriptData, Assembly-CSharp",
            $"scene {sceneIndex}",
            true,
            string.Empty,
            0,
            0,
            directive,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            0,
            0,
            0)
        {
            Characters = Array.AsReadOnly(characters.ToArray())
        };
    }
}
