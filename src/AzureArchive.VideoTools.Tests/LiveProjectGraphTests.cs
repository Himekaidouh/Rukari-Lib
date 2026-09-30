using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Tests;

/// <summary>
/// C feature Phase 2 tests: the live editor graph composer must produce a
/// Core snapshot that is isomorphic to a disk-read AAP graph, and the
/// existing chain resolver + directive index must resolve inheritance across
/// unsaved nodes with zero changes.
/// </summary>
internal static class LiveProjectGraphTests
{
    private const string ProjectKey = "0123456789abcdef01234567";
    private const string Entry = "eeeeeeee-0000-0000-0000-00000000000e";
    private const string NodeA = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string NodeB = "bbbbbbbb-0000-0000-0000-000000000002";
    private const string Exit = "xxxxxxxx-0000-0000-0000-00000000000f";

    public static void ComposeBuildsIsomorphicSnapshot()
    {
        Result<ProjectSnapshot> composed = LiveProjectGraphComposer.Compose(
            ProjectKey,
            new List<LiveGraphNodeData>
            {
                Node(Entry, StoryNodeKind.Entry, connections: new[] { NodeA }),
                Node(NodeA, StoryNodeKind.Script, connections: new[] { NodeB },
                    scenes: new List<LiveGraphSceneData?>
                    {
                        Scene(NodeA, 0, occupant: "mika", prompt: "#aavt;char;3;move;dx=500"),
                        Scene(NodeA, 1, occupant: "mika")
                    }),
                Node(NodeB, StoryNodeKind.Script, connections: new[] { Exit },
                    scenes: new List<LiveGraphSceneData?>
                    {
                        Scene(NodeB, 0, occupant: "mika")
                    }),
                Node(Exit, StoryNodeKind.Exit)
            });

        AssertEx.True(composed.Success, composed.Error);
        ProjectSnapshot project = AssertEx.NotNull(composed.Value);
        AssertEx.Equal(LiveProjectGraphComposer.LiveSourceType, project.SourceType);
        AssertEx.Equal(ProjectKey, project.ProjectName);

        StoryNodeSnapshot scriptA = AssertEx.NotNull(
            project.Nodes.Single(node => node.NodeGuid == NodeA));
        AssertEx.Equal(StoryNodeKind.Script, scriptA.Kind);
        AssertEx.True(scriptA.HasPersistentGuid);
        AssertEx.True(
            scriptA.ConnectionGuids.SequenceEqual(new[] { NodeB }),
            $"ConnectionGuids should contain only {NodeB}.");
        AssertEx.Equal(2, scriptA.Scenes.Count);

        SceneSnapshot sceneA0 = scriptA.Scenes[0];
        AssertEx.Equal(NodeA, sceneA0.Key.NodeGuid);
        AssertEx.Equal(0, sceneA0.Key.SceneIndex);
        AssertEx.False(string.IsNullOrEmpty(sceneA0.Key.Fingerprint));
        AssertEx.Equal("#aavt;char;3;move;dx=500", sceneA0.AdditionalPrompt);
        AssertEx.Equal("mika", sceneA0.Characters.Single().Identifier);
        AssertEx.Equal(3, sceneA0.Characters.Single().PhysicalSlot);

        StoryNodeSnapshot entryNode = AssertEx.NotNull(
            project.Nodes.Single(node => node.NodeGuid == Entry));
        AssertEx.Equal(StoryNodeKind.Entry, entryNode.Kind);
        AssertEx.Equal(0, entryNode.Scenes.Count);

        // The predecessor resolver must invert ConnectionGuids correctly on
        // the live snapshot: B's scene 0 walks back through A.
        var resolver = new PreviewChainResolver(project);
        Result<PreviewChainResolution> resolution = resolver.Resolve(
            new SceneKey(NodeB, 0, Fingerprint(NodeB, 0)),
            new[] { 3 },
            (_, _) => null);
        AssertEx.True(resolution.Success, resolution.Error);
        PreviewChainSlotResolution slot =
            AssertEx.NotNull(resolution.Value).Slots.Single();
        AssertEx.Equal("mika", slot.ExpectedOccupant);
        AssertEx.Equal(PreviewChainStopReason.ProjectStart, slot.StopReason);
    }

    public static void ComposeRejectsInvalidScriptNodeGuid()
    {
        Result<ProjectSnapshot> composed = LiveProjectGraphComposer.Compose(
            ProjectKey,
            new List<LiveGraphNodeData>
            {
                Node(Entry, StoryNodeKind.Entry),
                Node("__unidentified_node_1", StoryNodeKind.Script,
                    connections: Array.Empty<string>(),
                    scenes: new List<LiveGraphSceneData>
                    {
                        Scene("__unidentified_node_1", 0)
                    })
            });

        AssertEx.False(composed.Success);
        AssertEx.True(composed.Error.Contains("D-format guid"));
    }

    public static void NullSceneRecordKeepsIndexAlignment()
    {
        Result<ProjectSnapshot> composed = LiveProjectGraphComposer.Compose(
            ProjectKey,
            new List<LiveGraphNodeData>
            {
                Node(NodeA, StoryNodeKind.Script,
                    scenes: new List<LiveGraphSceneData?>
                    {
                        Scene(NodeA, 0, occupant: "mika"),
                        null!,
                        Scene(NodeA, 2, occupant: "mika")
                    })
            });

        AssertEx.True(composed.Success, composed.Error);
        ProjectSnapshot project = AssertEx.NotNull(composed.Value);
        StoryNodeSnapshot node = AssertEx.NotNull(
            project.Nodes.Single(candidate => candidate.NodeGuid == NodeA));
        AssertEx.Equal(3, node.Scenes.Count);
        AssertEx.Equal(0, node.Scenes[0].Key.SceneIndex);
        AssertEx.Equal(1, node.Scenes[1].Key.SceneIndex);
        AssertEx.Equal(2, node.Scenes[2].Key.SceneIndex);
        AssertEx.False(node.Scenes[1].IsDialogue);
        AssertEx.True(node.Scenes[2].IsDialogue);
        AssertEx.True(project.Diagnostics.Any(diagnostic =>
            string.Equals(diagnostic.Code, "LiveNullScene", StringComparison.Ordinal)));
    }

    public static void RevisionChangesWhenAnySceneFingerprintChanges()
    {
        IReadOnlyList<LiveGraphNodeData> graph = new List<LiveGraphNodeData>
        {
            Node(NodeA, StoryNodeKind.Script,
                scenes: new List<LiveGraphSceneData>
                {
                    Scene(NodeA, 0, fingerprint: "fp-original"),
                    Scene(NodeA, 1, fingerprint: "fp-second")
                })
        };

        Result<ProjectSnapshot> first = LiveProjectGraphComposer.Compose(ProjectKey, graph);
        AssertEx.True(first.Success, first.Error);

        var edited = new List<LiveGraphNodeData>
        {
            Node(NodeA, StoryNodeKind.Script,
                scenes: new List<LiveGraphSceneData>
                {
                    Scene(NodeA, 0, fingerprint: "fp-edited"),
                    Scene(NodeA, 1, fingerprint: "fp-second")
                })
        };
        Result<ProjectSnapshot> second = LiveProjectGraphComposer.Compose(ProjectKey, edited);
        AssertEx.True(second.Success, second.Error);

        Result<ProjectSnapshot> repeat = LiveProjectGraphComposer.Compose(ProjectKey, graph);
        AssertEx.True(repeat.Success, repeat.Error);

        AssertEx.False(string.Equals(
            AssertEx.NotNull(first.Value).Source.RevisionSha256,
            AssertEx.NotNull(second.Value).Source.RevisionSha256,
            StringComparison.Ordinal));
        AssertEx.Equal(
            AssertEx.NotNull(first.Value).Source.RevisionSha256,
            AssertEx.NotNull(repeat.Value).Source.RevisionSha256);
    }

    /// <summary>
    /// THE acceptance scenario for true WYSIWYG: an unsaved middle node (not
    /// present in any disk graph) inherits from saved ancestors and passes
    /// its own directives to later unsaved scenes — resolved entirely from
    /// the live-shaped snapshot with the unmodified resolver and directive
    /// index.
    /// </summary>
    public static void ChainResolvesAcrossUnsavedMiddleNodeEndToEnd()
    {
        Result<ProjectSnapshot> composed = LiveProjectGraphComposer.Compose(
            ProjectKey,
            new List<LiveGraphNodeData>
            {
                Node(Entry, StoryNodeKind.Entry, connections: new[] { NodeA }),
                Node(NodeA, StoryNodeKind.Script, connections: new[] { NodeB },
                    scenes: new List<LiveGraphSceneData?>
                    {
                        Scene(NodeA, 0, occupant: "mika",
                            prompt: "#wait;300\n#aavt;char;3;set;x=700;duration=600"),
                        Scene(NodeA, 1, occupant: "mika")
                    }),
                Node(NodeB, StoryNodeKind.Script, connections: Array.Empty<string>(),
                    scenes: new List<LiveGraphSceneData?>
                    {
                        Scene(NodeB, 0, occupant: "mika"),
                        Scene(NodeB, 1, occupant: "mika",
                            prompt: "#aavt;char;3;move;dx=-200")
                    })
            });
        AssertEx.True(composed.Success, composed.Error);
        ProjectSnapshot liveProject = AssertEx.NotNull(composed.Value);

        PreviewChainDirectiveIndex directives =
            PreviewChainDirectiveIndex.Build(liveProject, acceptLegacyCharacterAlias: false);
        AssertEx.Equal(2, directives.DirectiveCount);

        var resolver = new PreviewChainResolver(liveProject);
        Result<PreviewChainResolution> resolution = resolver.Resolve(
            new SceneKey(NodeB, 1, Fingerprint(NodeB, 1)),
            new[] { 3 },
            (key, slot) => directives.TryGet(key.NodeGuid, key.SceneIndex, slot, out string directive)
                ? directive
                : null);

        AssertEx.True(resolution.Success, resolution.Error);
        PreviewChainSlotResolution slotResolution =
            AssertEx.NotNull(resolution.Value).Slots.Single();
        // A.0 set x=700 folds in; B.1's own move stays the live command and
        // does not participate as an inherited start.
        AssertEx.Equal(1, slotResolution.State.FoldedCommandCount);
        AssertEx.True(slotResolution.HasInheritedStart);
        AssertEx.True(slotResolution.State.X.HasAbsoluteValue);
        AssertEx.Equal(700f, slotResolution.State.X.AbsoluteValue);
        AssertEx.Equal("mika", slotResolution.ExpectedOccupant);
        AssertEx.Equal(PreviewChainStopReason.ProjectStart, slotResolution.StopReason);
    }

    public static void OfficialTransitionSurvivesLiveCharacterMapping()
    {
        Result<ProjectSnapshot> composed = LiveProjectGraphComposer.Compose(
            ProjectKey,
            new List<LiveGraphNodeData>
            {
                Node(NodeA, StoryNodeKind.Script,
                    scenes: new List<LiveGraphSceneData>
                    {
                        Scene(NodeA, 0, occupant: "mika",
                            characterStartingPos: 3,
                            characterEndingPos: 4),
                        Scene(NodeA, 1, occupant: "mika")
                    })
            });
        AssertEx.True(composed.Success, composed.Error);
        ProjectSnapshot project = AssertEx.NotNull(composed.Value);

        var resolver = new PreviewChainResolver(project);
        Result<PreviewChainResolution> resolution = resolver.Resolve(
            new SceneKey(NodeA, 1, Fingerprint(NodeA, 1)),
            new[] { 3 },
            (_, _) => null);

        AssertEx.True(resolution.Success, resolution.Error);
        PreviewChainSlotResolution slot =
            AssertEx.NotNull(resolution.Value).Slots.Single();
        // The official move owns position from scene 0 onward: position is
        // uncontrolled past it and the walk stops at the official boundary.
        AssertEx.Equal(PreviewChainStopReason.OfficialPositionMove, slot.StopReason);
        AssertEx.False(slot.State.X.IsControlled);
    }

    private static LiveGraphNodeData Node(
        string guid,
        StoryNodeKind kind,
        IReadOnlyList<string>? connections = null,
        IReadOnlyList<LiveGraphSceneData?>? scenes = null) => new(
        0,
        kind,
        guid,
        $"live-{kind}",
        connections ?? Array.Empty<string>(),
        (scenes ?? Array.Empty<LiveGraphSceneData?>())
            .Select(item => item!)
            .ToArray());

    private static LiveGraphSceneData Scene(
        string nodeGuid,
        int sceneIndex,
        string occupant = "",
        string prompt = "",
        string? fingerprint = null,
        int characterStartingPos = 0,
        int characterEndingPos = 0)
    {
        var characters = new List<LiveGraphCharacterData>();
        if (!string.IsNullOrEmpty(occupant))
        {
            characters.Add(new LiveGraphCharacterData(
                3,
                occupant,
                "03",
                characterStartingPos,
                characterEndingPos,
                Emoticon: 0,
                Action: 0,
                Effect: 0,
                Appear: 0,
                ShapeOverride: 0));
        }

        return new LiveGraphSceneData(
            sceneIndex,
            fingerprint ?? Fingerprint(nodeGuid, sceneIndex),
            LiveProjectGraphComposer.LiveSourceType,
            DialogueText: $"scene {sceneIndex}",
            IsDialogue: true,
            PopupFileName: string.Empty,
            BackgroundEffect: 0u,
            BackgroundName: 0u,
            AdditionalPrompt: prompt,
            PlaceText: string.Empty,
            BackgroundFriendlyName: string.Empty,
            SoundReference: string.Empty,
            VoiceReference: string.Empty,
            Transition: 0u,
            BgmId: 0L,
            SelectionGroup: 0L,
            SpeakerSlot: 0,
            HighlightedSlots: Array.Empty<int>(),
            Characters: characters);
    }

    private static string Fingerprint(string nodeGuid, int sceneIndex)
    {
        string seed = $"live:{nodeGuid}:{sceneIndex}";
        return seed.PadRight(24, '0')[..24];
    }
}
