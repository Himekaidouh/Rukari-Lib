using AzureArchive.VideoTools.Core.Cameras;
using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Core.Results;
using AzureArchive.VideoTools.Formats.Aap;

namespace AzureArchive.VideoTools.Tests;

internal static class PreviewCameraChainTests
{
    private const string NodeA = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string NodeB = "bbbbbbbb-0000-0000-0000-000000000002";
    private const string NodeD = "dddddddd-0000-0000-0000-000000000004";

    public static void FoldsAncestorCameraThroughCommandlessScenes()
    {
        ProjectSnapshot project = Graph()
            .Add(ScriptNode(NodeA, null,
                Scene(NodeA, 0),
                Scene(NodeA, 1),
                Scene(NodeA, 2)))
            .Build();
        var lookup = new CameraMap();
        lookup.Add(Key(NodeA, 0), "#camera;set;x=400;zoom=1.5;duration=0");

        Result<PreviewCameraChainResolution> result =
            new PreviewCameraChainResolver(project).Resolve(Key(NodeA, 2), lookup.Lookup);

        AssertEx.True(result.Success, result.Error);
        PreviewCameraChainResolution chain = AssertEx.NotNull(result.Value);
        AssertEx.Equal(1, chain.FoldedCommandCount);
        AssertEx.True(chain.HasInheritedStart);
        AssertEx.Equal(PreviewChainStopReason.ProjectStart, chain.StopReason);
        AssertEx.Equal(new SceneCameraState(400f, 0f, 1.5f), chain.InheritedState);
    }

    public static void FoldsSetThenMoveInChronologicalOrder()
    {
        ProjectSnapshot project = Graph()
            .Add(ScriptNode(NodeA, null,
                Scene(NodeA, 0),
                Scene(NodeA, 1),
                Scene(NodeA, 2)))
            .Build();
        var lookup = new CameraMap();
        lookup.Add(Key(NodeA, 0), "#camera;set;x=400;zoom=1.5;duration=0");
        lookup.Add(Key(NodeA, 1), "#camera;move;dx=200;dzoom=0.25;duration=0");

        Result<PreviewCameraChainResolution> result =
            new PreviewCameraChainResolver(project).Resolve(Key(NodeA, 2), lookup.Lookup);

        AssertEx.True(result.Success, result.Error);
        PreviewCameraChainResolution chain = AssertEx.NotNull(result.Value);
        AssertEx.Equal(2, chain.FoldedCommandCount);
        // The relative move must compose on top of the newer-to-older fold in
        // chronological order: 400 + 200 and 1.5 + 0.25.
        AssertEx.Equal(new SceneCameraState(600f, 0f, 1.75f), chain.InheritedState);
    }

    public static void ResetTruncatesEarlierAncestorCamera()
    {
        ProjectSnapshot project = Graph()
            .Add(ScriptNode(NodeA, null,
                Scene(NodeA, 0),
                Scene(NodeA, 1),
                Scene(NodeA, 2)))
            .Build();
        var lookup = new CameraMap();
        lookup.Add(Key(NodeA, 0), "#camera;set;x=400;zoom=1.5;duration=0");
        lookup.Add(Key(NodeA, 1), "#camera;reset;duration=0");

        Result<PreviewCameraChainResolution> result =
            new PreviewCameraChainResolver(project).Resolve(Key(NodeA, 2), lookup.Lookup);

        AssertEx.True(result.Success, result.Error);
        PreviewCameraChainResolution chain = AssertEx.NotNull(result.Value);
        AssertEx.Equal(2, chain.FoldedCommandCount);
        AssertEx.Equal(SceneCameraState.Default, chain.InheritedState);
    }

    public static void SceneWithoutAncestorCameraKeepsTheOfficialComposition()
    {
        ProjectSnapshot project = Graph()
            .Add(ScriptNode(NodeA, null, Scene(NodeA, 0), Scene(NodeA, 1)))
            .Build();

        Result<PreviewCameraChainResolution> result =
            new PreviewCameraChainResolver(project).Resolve(Key(NodeA, 1), _ => null);

        AssertEx.True(result.Success, result.Error);
        PreviewCameraChainResolution chain = AssertEx.NotNull(result.Value);
        AssertEx.Equal(0, chain.FoldedCommandCount);
        AssertEx.False(chain.HasInheritedStart);
        AssertEx.Equal(SceneCameraState.Default, chain.InheritedState);
    }

    public static void AmbiguousIncomingStopsTheCameraWalk()
    {
        ProjectSnapshot project = Graph()
            .Add(ScriptNode(NodeA, new[] { NodeD }, Scene(NodeA, 0)))
            .Add(ScriptNode(NodeB, new[] { NodeD }, Scene(NodeB, 0)))
            .Add(ScriptNode(NodeD, null, Scene(NodeD, 0), Scene(NodeD, 1)))
            .Build();
        var lookup = new CameraMap();
        lookup.Add(Key(NodeA, 0), "#camera;set;x=400;zoom=2;duration=0");
        lookup.Add(Key(NodeB, 0), "#camera;set;x=-400;zoom=2;duration=0");

        Result<PreviewCameraChainResolution> result =
            new PreviewCameraChainResolver(project).Resolve(Key(NodeD, 0), lookup.Lookup);

        AssertEx.True(result.Success, result.Error);
        PreviewCameraChainResolution chain = AssertEx.NotNull(result.Value);
        AssertEx.Equal(PreviewChainStopReason.AmbiguousGraph, chain.StopReason);
        AssertEx.Equal(0, chain.FoldedCommandCount);
        AssertEx.False(chain.HasInheritedStart);
        AssertEx.Equal(SceneCameraState.Default, chain.InheritedState);
    }

    public static void IndexAndResolverInheritTheCameraFromTheProjectGraph()
    {
        using var files = new TemporaryAapDirectory();
        string path = files.Write(
            "camera-chain.aap",
            SyntheticAap.ProjectWithTwoScriptNodes(
                new[]
                {
                    SyntheticAap.Scene(
                        "A",
                        additionalPrompt: "#aavt;camera;set;x=400;zoom=1.5;duration=800;easing=easeOut"),
                    SyntheticAap.Scene("B")
                },
                new[]
                {
                    SyntheticAap.Scene(
                        "C",
                        additionalPrompt: "#aavt;camera;move;dx=200;dzoom=0.25;duration=800;easing=easeOut")
                }));
        Result<ProjectSnapshot> read = new AapProjectReader().Read(path);
        AssertEx.True(read.Success, read.Error);
        ProjectSnapshot project = AssertEx.NotNull(read.Value);

        PreviewChainDirectiveIndex index = PreviewChainDirectiveIndex.Build(
            project,
            acceptLegacyCharacterAlias: true);
        AssertEx.Equal(2, index.CameraSceneCount);
        AssertEx.True(
            index.TryGetCamera(SyntheticAap.ScriptNodeGuid, 0, out string first),
            "the camera-only scene must be indexed even without character slots");
        AssertEx.Equal(
            "#camera;set;x=400;zoom=1.5;duration=800;easing=easeOut",
            first);
        AssertEx.False(index.TryGetCamera(SyntheticAap.ScriptNodeGuid, 1, out _));
        AssertEx.True(index.TryGetCamera(SyntheticAap.SecondScriptNodeGuid, 0, out string second));
        Result<SceneCameraCommand> parsedSecond = new SceneCameraDirectiveParser().Parse(second);
        AssertEx.True(parsedSecond.Success, parsedSecond.Error);
        AssertEx.Equal(0.25f, AssertEx.NotNull(parsedSecond.Value).DeltaZoom);

        // Scene C of the second node inherits scene A of the first node across
        // the node boundary; scene B in between never touches the camera.
        StoryNodeSnapshot secondNode = project.Nodes.First(
            node => string.Equals(node.NodeGuid, SyntheticAap.SecondScriptNodeGuid, StringComparison.Ordinal));
        Result<PreviewCameraChainResolution> resolved = new PreviewCameraChainResolver(project)
            .Resolve(
                secondNode.Scenes[0].Key,
                key => index.TryGetCamera(key.NodeGuid, key.SceneIndex, out string directive)
                    ? directive
                    : null);

        AssertEx.True(resolved.Success, resolved.Error);
        PreviewCameraChainResolution chain = AssertEx.NotNull(resolved.Value);
        AssertEx.Equal(1, chain.FoldedCommandCount);
        AssertEx.Equal(new SceneCameraState(400f, 0f, 1.5f), chain.InheritedState);
    }

    private sealed class CameraMap
    {
        private readonly Dictionary<SceneKey, string> _map = new();

        public void Add(SceneKey sceneKey, string directive) => _map[sceneKey] = directive;

        public string? Lookup(SceneKey sceneKey) =>
            _map.TryGetValue(sceneKey, out string? directive) ? directive : null;
    }

    private sealed class GraphBuilder
    {
        private readonly List<StoryNodeSnapshot> _nodes = new();

        public GraphBuilder Add(StoryNodeSnapshot node)
        {
            _nodes.Add(node);
            return this;
        }

        public ProjectSnapshot Build() => new(
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

    private static GraphBuilder Graph() => new();

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

    private static SceneSnapshot Scene(string nodeGuid, int sceneIndex) => new(
        Key(nodeGuid, sceneIndex),
        "ScriptData, Assembly-CSharp",
        $"scene {sceneIndex}",
        true,
        string.Empty,
        0,
        0,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        0,
        0,
        0);

    private static SceneKey Key(string nodeGuid, int sceneIndex) =>
        new(nodeGuid, sceneIndex, Fingerprint(nodeGuid, sceneIndex));

    private static string Fingerprint(string nodeGuid, int sceneIndex)
    {
        string seed = $"{nodeGuid}:{sceneIndex}";
        return seed.PadRight(64, '0')[..64];
    }
}
