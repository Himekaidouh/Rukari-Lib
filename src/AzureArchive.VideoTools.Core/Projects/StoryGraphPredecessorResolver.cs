using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Projects;

public enum StoryPredecessorStatus
{
    PredecessorScene = 0,
    ProjectStart = 1,
    AmbiguousIncoming = 2
}

public sealed record StoryPredecessorResolution(
    StoryPredecessorStatus Status,
    SceneKey? Predecessor,
    int IncomingNodeCount)
{
    public static StoryPredecessorResolution PredecessorScene(SceneKey key, int incoming) => new(
        StoryPredecessorStatus.PredecessorScene, key, incoming);

    public static StoryPredecessorResolution ProjectStart() => new(
        StoryPredecessorStatus.ProjectStart, null, 0);

    public static StoryPredecessorResolution Ambiguous(int incoming) => new(
        StoryPredecessorStatus.AmbiguousIncoming, null, incoming);
}

public sealed class StoryGraphPredecessorResolver
{
    private readonly IReadOnlyDictionary<string, StoryNodeSnapshot> _nodes;
    private readonly IReadOnlyDictionary<string, List<string>> _incoming;

    public StoryGraphPredecessorResolver(ProjectSnapshot project)
    {
        ArgumentNullException.ThrowIfNull(project);

        var nodes = new Dictionary<string, StoryNodeSnapshot>(StringComparer.Ordinal);
        var incoming = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (StoryNodeSnapshot node in project.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.NodeGuid))
            {
                continue;
            }

            nodes.TryAdd(node.NodeGuid, node);
            foreach (string connection in node.ConnectionGuids)
            {
                if (string.IsNullOrWhiteSpace(connection))
                {
                    continue;
                }

                if (!incoming.TryGetValue(connection, out List<string>? sources))
                {
                    incoming[connection] = sources = new List<string>();
                }

                sources.Add(node.NodeGuid);
            }
        }

        _nodes = nodes;
        _incoming = incoming;
    }

    public Result<StoryPredecessorResolution> ResolvePredecessor(string nodeGuid, int sceneIndex)
    {
        if (string.IsNullOrWhiteSpace(nodeGuid)
            || !_nodes.TryGetValue(nodeGuid, out StoryNodeSnapshot? node))
        {
            return Result<StoryPredecessorResolution>.Fail(
                $"Story node was not found: {nodeGuid}.");
        }

        if (node.Scenes.Count == 0
            || sceneIndex < 0
            || sceneIndex >= node.Scenes.Count)
        {
            return Result<StoryPredecessorResolution>.Fail(
                $"Scene index {sceneIndex} is outside node '{nodeGuid}' with {node.Scenes.Count} scenes.");
        }

        if (sceneIndex > 0)
        {
            return Result<StoryPredecessorResolution>.Ok(
                StoryPredecessorResolution.PredecessorScene(
                    node.Scenes[sceneIndex - 1].Key, 0));
        }

        return Result<StoryPredecessorResolution>.Ok(WalkUp(node));
    }

    private StoryPredecessorResolution WalkUp(StoryNodeSnapshot start)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal) { start.NodeGuid };
        StoryNodeSnapshot current = start;
        while (true)
        {
            if (!_incoming.TryGetValue(current.NodeGuid, out List<string>? sources)
                || sources.Count == 0)
            {
                return StoryPredecessorResolution.ProjectStart();
            }

            if (sources.Count != 1)
            {
                return StoryPredecessorResolution.Ambiguous(sources.Count);
            }

            if (!_nodes.TryGetValue(sources[0], out StoryNodeSnapshot? parent)
                || !visited.Add(parent.NodeGuid))
            {
                return StoryPredecessorResolution.Ambiguous(Math.Max(sources.Count, 1));
            }

            if (parent.Scenes.Count > 0)
            {
                return StoryPredecessorResolution.PredecessorScene(
                    parent.Scenes[^1].Key, 1);
            }

            current = parent;
        }
    }
}
