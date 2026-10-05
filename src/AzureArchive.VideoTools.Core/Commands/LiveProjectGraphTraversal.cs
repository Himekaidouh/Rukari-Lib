using AzureArchive.VideoTools.Core.Projects;

namespace AzureArchive.VideoTools.Core.Commands;

/// <summary>
/// Walks reachable live nodes and keeps only copied leaf values. Runtime
/// access stays in the supplied readers, so the traversal can run without
/// Unity or IL2CPP and is shared by the editor walker and its regression tests.
/// </summary>
public static class LiveProjectGraphTraversal
{
    public static IReadOnlyList<LiveGraphNodeData> Walk<TNode>(
        TNode rootNode,
        int maximumNodes,
        Func<TNode, bool> isAlive,
        Func<TNode, string> readGuid,
        Func<TNode, StoryNodeKind> detectKind,
        Func<TNode, IReadOnlyList<TNode>> readTargets,
        Func<TNode, int, StoryNodeKind, string, IReadOnlyList<string>, LiveGraphNodeData> copyNode,
        out bool nodeLimitReached)
        where TNode : notnull
    {
        ArgumentNullException.ThrowIfNull(rootNode);
        ArgumentNullException.ThrowIfNull(isAlive);
        ArgumentNullException.ThrowIfNull(readGuid);
        ArgumentNullException.ThrowIfNull(detectKind);
        ArgumentNullException.ThrowIfNull(readTargets);
        ArgumentNullException.ThrowIfNull(copyNode);
        if (maximumNodes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumNodes));
        }

        var nodes = new List<LiveGraphNodeData>();
        var nodeGuids = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<TNode>();
        // Keep the runtime's existing default equality semantics. Mark every
        // node at enqueue time only, including the root: dequeuing must not
        // try to mark it again and skip an already scheduled successor.
        var enqueued = new HashSet<TNode> { rootNode };
        pending.Enqueue(rootNode);
        nodeLimitReached = false;

        while (pending.TryDequeue(out TNode? node))
        {
            if (!isAlive(node))
            {
                continue;
            }

            if (nodes.Count >= maximumNodes)
            {
                nodeLimitReached = true;
                break;
            }

            string guid = readGuid(node);
            if (string.IsNullOrEmpty(guid) || !nodeGuids.Add(guid))
            {
                continue;
            }

            StoryNodeKind kind = detectKind(node);
            var connectionGuids = new List<string>();
            foreach (TNode target in readTargets(node))
            {
                string targetGuid = readGuid(target);
                if (!string.IsNullOrEmpty(targetGuid))
                {
                    connectionGuids.Add(targetGuid);
                }

                // Preserve edge order and duplicates in copied data, but
                // schedule each runtime node once even in cycles or merges.
                // Unreadable guids still reach the per-node diagnostic reader.
                if (enqueued.Add(target))
                {
                    pending.Enqueue(target);
                }
            }

            nodes.Add(copyNode(node, nodes.Count, kind, guid, connectionGuids.AsReadOnly()));
        }

        return nodes.AsReadOnly();
    }
}
