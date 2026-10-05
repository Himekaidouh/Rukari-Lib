using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Tests;

internal static class LiveProjectGraphTraversalTests
{
    private const string ProjectKey = "0123456789abcdef01234567";

    public static void VisitsSuccessorsInBreadthFirstOrder()
    {
        var entry = new RuntimeNode(1, StoryNodeKind.Entry);
        var first = new RuntimeNode(2);
        var second = new RuntimeNode(3);
        var last = new RuntimeNode(4, StoryNodeKind.Exit);
        entry.Targets.Add(first);
        first.Targets.Add(second);
        second.Targets.Add(last);

        IReadOnlyList<LiveGraphNodeData> nodes = Walk(entry, 4096, out bool capped);

        AssertOrder(nodes, entry, first, second, last);
        AssertEx.False(capped);
        AssertEx.True(nodes.Select(node => node.SourceIndex).SequenceEqual(new[] { 0, 1, 2, 3 }));
        AssertEx.True(nodes[0].ConnectionGuids.SequenceEqual(new[] { first.Guid }));
        AssertEx.True(nodes[1].ConnectionGuids.SequenceEqual(new[] { second.Guid }));
        AssertEx.True(nodes[2].ConnectionGuids.SequenceEqual(new[] { last.Guid }));
        AssertEx.Equal(0, nodes[3].ConnectionGuids.Count);
        AssertReadOnce(entry, first, second, last);
    }

    public static void BranchMergeVisitsSharedNodeOnceAndPreservesBothIncomingEdges()
    {
        var entry = new RuntimeNode(1, StoryNodeKind.Entry);
        var left = new RuntimeNode(2);
        var right = new RuntimeNode(3);
        var merge = new RuntimeNode(4);
        var exit = new RuntimeNode(5, StoryNodeKind.Exit);
        entry.Targets.AddRange(new[] { left, right });
        left.Targets.Add(merge);
        right.Targets.Add(merge);
        merge.Targets.Add(exit);
        merge.Scenes.Add(Scene(merge, 0));

        IReadOnlyList<LiveGraphNodeData> nodes = Walk(entry, 4096, out bool capped);

        AssertOrder(nodes, entry, left, right, merge, exit);
        AssertEx.False(capped);
        AssertEx.True(nodes[0].ConnectionGuids.SequenceEqual(new[] { left.Guid, right.Guid }));
        AssertEx.Equal(merge.Guid, nodes[1].ConnectionGuids.Single());
        AssertEx.Equal(merge.Guid, nodes[2].ConnectionGuids.Single());
        AssertReadOnce(entry, left, right, merge, exit);

        ProjectSnapshot project = Compose(nodes);
        Result<StoryPredecessorResolution> predecessor =
            new StoryGraphPredecessorResolver(project).ResolvePredecessor(merge.Guid, 0);
        AssertEx.True(predecessor.Success, predecessor.Error);
        StoryPredecessorResolution resolution = AssertEx.NotNull(predecessor.Value);
        AssertEx.Equal(StoryPredecessorStatus.AmbiguousIncoming, resolution.Status);
        AssertEx.Equal(2, resolution.IncomingNodeCount);
    }

    public static void CycleTerminatesAndRetainsBackEdge()
    {
        var entry = new RuntimeNode(1, StoryNodeKind.Entry);
        var first = new RuntimeNode(2);
        var second = new RuntimeNode(3);
        entry.Targets.Add(first);
        first.Targets.Add(second);
        second.Targets.Add(entry);

        IReadOnlyList<LiveGraphNodeData> nodes = Walk(entry, 3, out bool capped);

        AssertOrder(nodes, entry, first, second);
        AssertEx.False(capped, "An already scheduled cycle must not trip the node cap.");
        AssertEx.Equal(entry.Guid, nodes[2].ConnectionGuids.Single());
        AssertReadOnce(entry, first, second);
    }

    public static void SelfLoopVisitsRootOnce()
    {
        var entry = new RuntimeNode(1, StoryNodeKind.Entry);
        entry.Targets.Add(entry);

        IReadOnlyList<LiveGraphNodeData> nodes = Walk(entry, 1, out bool capped);

        AssertOrder(nodes, entry);
        AssertEx.False(capped);
        AssertEx.Equal(entry.Guid, nodes.Single().ConnectionGuids.Single());
        AssertReadOnce(entry);
    }

    public static void RepeatedEdgesScheduleOneVisitAndKeepTheirOrder()
    {
        var entry = new RuntimeNode(1, StoryNodeKind.Entry);
        var first = new RuntimeNode(2);
        var second = new RuntimeNode(3);
        entry.Targets.AddRange(new[] { first, second, first, second, first });

        IReadOnlyList<LiveGraphNodeData> nodes = Walk(entry, 3, out bool capped);

        AssertOrder(nodes, entry, first, second);
        AssertEx.False(capped);
        AssertEx.True(nodes[0].ConnectionGuids.SequenceEqual(
            new[] { first.Guid, second.Guid, first.Guid, second.Guid, first.Guid }));
        AssertReadOnce(entry, first, second);
    }

    public static void NodeCapCountsCopiedNodesAndStopsBeforeReadingRemainder()
    {
        var entry = new RuntimeNode(1, StoryNodeKind.Entry);
        var unreadable = new RuntimeNode(2) { Guid = string.Empty };
        var dead = new RuntimeNode(3) { Alive = false };
        var duplicateGuid = new RuntimeNode(4) { Guid = entry.Guid };
        var script = new RuntimeNode(5);
        var remainder = new RuntimeNode(6);
        entry.Targets.AddRange(new[] { unreadable, dead, duplicateGuid, script });
        script.Targets.Add(remainder);

        IReadOnlyList<LiveGraphNodeData> nodes = Walk(entry, 2, out bool capped);

        AssertOrder(nodes, entry, script);
        AssertEx.True(capped);
        AssertReadOnce(entry, script);
        foreach (RuntimeNode skipped in new[] { unreadable, dead, duplicateGuid, remainder })
        {
            AssertEx.Equal(0, skipped.TargetReads);
            AssertEx.Equal(0, skipped.Copies);
        }
        AssertEx.Equal(2, unreadable.GuidReads,
            "Unreadable targets must still be checked again when dequeued for diagnostics.");
        AssertEx.Equal(1, remainder.GuidReads,
            "The node cap must stop before the queued remainder's guid or scenes are read.");

        var exactEntry = new RuntimeNode(10, StoryNodeKind.Entry);
        var exactScript = new RuntimeNode(11);
        exactEntry.Targets.Add(exactScript);
        AssertOrder(Walk(exactEntry, 2, out bool exactCapped), exactEntry, exactScript);
        AssertEx.False(exactCapped, "Exactly reaching the cap with no remainder is a complete walk.");
    }

    public static void DefaultRuntimeEqualityStillDeduplicatesEquivalentWrappers()
    {
        var entry = new RuntimeNode(1, StoryNodeKind.Entry);
        var firstWrapper = new RuntimeNode(2, runtimeIdentity: 42);
        var equivalentWrapper = new RuntimeNode(2, runtimeIdentity: 42);
        entry.Targets.AddRange(new[] { firstWrapper, equivalentWrapper });

        IReadOnlyList<LiveGraphNodeData> nodes = Walk(entry, 4096, out bool capped);

        AssertOrder(nodes, entry, firstWrapper);
        AssertEx.False(capped);
        AssertReadOnce(entry, firstWrapper);
        AssertEx.Equal(0, equivalentWrapper.TargetReads);
        AssertEx.Equal(0, equivalentWrapper.Copies);
        AssertEx.Equal(1, equivalentWrapper.GuidReads,
            "Default runtime equality must deduplicate at scheduling, before a second dequeue.");
        AssertEx.True(nodes[0].ConnectionGuids.SequenceEqual(new[] { firstWrapper.Guid, firstWrapper.Guid }));
    }

    public static void WalkedUnsavedNodesComposeAndDonatePreviewInheritance()
    {
        var entry = new RuntimeNode(1, StoryNodeKind.Entry);
        var ancestor = new RuntimeNode(2);
        var unsavedMiddle = new RuntimeNode(3);
        var unsavedCurrent = new RuntimeNode(4);
        entry.Targets.Add(ancestor);
        ancestor.Targets.Add(unsavedMiddle);
        unsavedMiddle.Targets.Add(unsavedCurrent);
        ancestor.Scenes.Add(Scene(ancestor, 0, "#aavt;char;3;set;x=700;duration=600"));
        ancestor.Scenes.Add(Scene(ancestor, 1));
        unsavedMiddle.Scenes.Add(Scene(unsavedMiddle, 0));
        unsavedCurrent.Scenes.Add(Scene(unsavedCurrent, 0, "#aavt;char;3;move;dx=-200"));

        IReadOnlyList<LiveGraphNodeData> walked = Walk(entry, 4096, out bool capped);
        AssertEx.False(capped);
        AssertOrder(walked, entry, ancestor, unsavedMiddle, unsavedCurrent);
        ProjectSnapshot project = Compose(walked);
        AssertEx.Equal(4, project.Nodes.Count);
        AssertEx.Equal(4, project.Nodes.Sum(node => node.Scenes.Count));
        PreviewChainDirectiveIndex directives =
            PreviewChainDirectiveIndex.Build(project, acceptLegacyCharacterAlias: false);
        AssertEx.Equal(2, directives.DirectiveCount);

        SceneKey currentKey = project.Nodes.Single(node => node.NodeGuid == unsavedCurrent.Guid).Scenes[0].Key;
        Result<PreviewChainResolution> result = new PreviewChainResolver(project).Resolve(
            currentKey,
            new[] { 3 },
            (key, slot) => directives.TryGet(key.NodeGuid, key.SceneIndex, slot, out string directive)
                ? directive
                : null);
        AssertEx.True(result.Success, result.Error);
        PreviewChainSlotResolution resolution = AssertEx.NotNull(result.Value).Slots.Single();
        AssertEx.Equal("mika", resolution.ExpectedOccupant);
        AssertEx.True(resolution.HasInheritedStart);
        AssertEx.Equal(1, resolution.State.FoldedCommandCount);
        AssertEx.True(resolution.State.X.HasAbsoluteValue);
        AssertEx.Equal(700f, resolution.State.X.AbsoluteValue);
        AssertEx.Equal(PreviewChainStopReason.ProjectStart, resolution.StopReason);
        AssertReadOnce(entry, ancestor, unsavedMiddle, unsavedCurrent);
    }

    private static IReadOnlyList<LiveGraphNodeData> Walk(RuntimeNode entry, int maximumNodes, out bool capped) =>
        LiveProjectGraphTraversal.Walk(
            entry,
            maximumNodes,
            node => node.Alive,
            node =>
            {
                node.GuidReads++;
                return node.Guid;
            },
            node => node.Kind,
            node =>
            {
                node.TargetReads++;
                return node.Targets;
            },
            (node, sourceIndex, kind, guid, connections) =>
            {
                node.Copies++;
                return new LiveGraphNodeData(sourceIndex, kind, guid, "live-test", connections, node.Scenes);
            },
            out capped);

    private static ProjectSnapshot Compose(IReadOnlyList<LiveGraphNodeData> nodes)
    {
        Result<ProjectSnapshot> result = LiveProjectGraphComposer.Compose(ProjectKey, nodes);
        AssertEx.True(result.Success, result.Error);
        return AssertEx.NotNull(result.Value);
    }

    private static void AssertOrder(IReadOnlyList<LiveGraphNodeData> actual, params RuntimeNode[] expected) =>
        AssertEx.True(actual.Select(node => node.NodeGuid).SequenceEqual(expected.Select(node => node.Guid)),
            "The reachable graph must contain each scheduled node once in breadth-first order.");

    private static void AssertReadOnce(params RuntimeNode[] nodes)
    {
        foreach (RuntimeNode node in nodes)
        {
            AssertEx.Equal(1, node.TargetReads, $"Targets for {node.Guid} must be read once.");
            AssertEx.Equal(1, node.Copies, $"Node {node.Guid} must be copied once.");
        }
    }

    private static LiveGraphSceneData Scene(RuntimeNode node, int index, string prompt = "") => new(
        index,
        $"live:{node.Guid}:{index}",
        LiveProjectGraphComposer.LiveSourceType,
        DialogueText: $"scene {index}",
        IsDialogue: true,
        PopupFileName: string.Empty,
        BackgroundEffect: 0,
        BackgroundName: 0,
        AdditionalPrompt: prompt,
        PlaceText: string.Empty,
        BackgroundFriendlyName: string.Empty,
        SoundReference: string.Empty,
        VoiceReference: string.Empty,
        Transition: 0,
        BgmId: 0,
        SelectionGroup: 0,
        SpeakerSlot: 3,
        HighlightedSlots: Array.Empty<int>(),
        Characters: new[]
        {
            new LiveGraphCharacterData(3, "mika", "03", 0, 0, 0, 0, 0, 0, 0)
        });

    private sealed class RuntimeNode
    {
        private readonly int _runtimeIdentity;

        public RuntimeNode(int id, StoryNodeKind kind = StoryNodeKind.Script, int? runtimeIdentity = null)
        {
            _runtimeIdentity = runtimeIdentity ?? id;
            Guid = $"{id:x8}-0000-0000-0000-000000000001";
            Kind = kind;
        }

        public string Guid { get; init; }
        public StoryNodeKind Kind { get; }
        public bool Alive { get; init; } = true;
        public List<RuntimeNode> Targets { get; } = new();
        public List<LiveGraphSceneData> Scenes { get; } = new();
        public int GuidReads { get; set; }
        public int TargetReads { get; set; }
        public int Copies { get; set; }

        public override bool Equals(object? obj) =>
            obj is RuntimeNode other && _runtimeIdentity == other._runtimeIdentity;

        public override int GetHashCode() => _runtimeIdentity;
    }
}
