using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Commands;

/// <summary>
/// One copied character record of a live editor scene, shaped exactly like
/// <see cref="ProjectCharacterSnapshot"/> inputs. Produced by the interop
/// walker on the Unity main thread; consumed by the pure composer below.
/// </summary>
public sealed record LiveGraphCharacterData(
    int PhysicalSlot,
    string Identifier,
    string FaceId,
    int StartingPosition,
    int EndingPosition,
    int Emoticon,
    int Action,
    int Effect,
    int Appear,
    int ShapeOverride);

/// <summary>
/// One copied scene of a live editor ScriptNode. Field semantics mirror the
/// AAP JSON members that <c>AapProjectReader</c> reads, so the composed
/// snapshot is isomorphic to the disk graph.
/// </summary>
public sealed record LiveGraphSceneData(
    int SceneIndex,
    string Fingerprint,
    string SourceType,
    string DialogueText,
    bool IsDialogue,
    string PopupFileName,
    uint BackgroundEffect,
    uint BackgroundName,
    string AdditionalPrompt,
    string PlaceText,
    string BackgroundFriendlyName,
    string SoundReference,
    string VoiceReference,
    uint Transition,
    long BgmId,
    long SelectionGroup,
    int SpeakerSlot,
    IReadOnlyList<int> HighlightedSlots,
    IReadOnlyList<LiveGraphCharacterData> Characters);

/// <summary>One copied node of the live editor graph.</summary>
public sealed record LiveGraphNodeData(
    int SourceIndex,
    StoryNodeKind Kind,
    string NodeGuid,
    string Name,
    IReadOnlyList<string> ConnectionGuids,
    IReadOnlyList<LiveGraphSceneData> Scenes);

/// <summary>
/// Composes a live editor graph into a Core <see cref="ProjectSnapshot"/>
/// that is structurally indistinguishable from a disk-read snapshot, so
/// <see cref="PreviewChainResolver"/>, the directive index, and the
/// predecessor resolver work on it without any change.
/// <para>
/// The source descriptor is deliberately synthetic ("live://editor-node-graph")
/// so no consumer can mistake it for a file; the revision hash covers every
/// scene fingerprint in walk order and therefore changes whenever any unsaved
/// scene content or ordering changes.
/// </para>
/// </summary>
public static class LiveProjectGraphComposer
{
    public const string LiveSourceType = "live-editor-graph";
    public const string LivePathKey = "live-editor";

    public static Result<ProjectSnapshot> Compose(
        string projectKey,
        IReadOnlyList<LiveGraphNodeData> nodes)
    {
        if (string.IsNullOrWhiteSpace(projectKey))
        {
            return Result<ProjectSnapshot>.Fail("Live graph compose requires a project key.");
        }

        if (nodes == null || nodes.Count == 0)
        {
            return Result<ProjectSnapshot>.Fail("Live graph has no reachable nodes.");
        }

        var diagnostics = new List<ProjectDiagnostic>();
        var storyNodes = new List<StoryNodeSnapshot>(nodes.Count);
        var revision = new System.Text.StringBuilder();
        var seenGuids = new HashSet<string>(StringComparer.Ordinal);
        int sceneTotal = 0;

        foreach (LiveGraphNodeData? node in nodes)
        {
            if (node == null)
            {
                diagnostics.Add(new ProjectDiagnostic(
                    ProjectDiagnosticSeverity.Warning,
                    "LiveNullNode",
                    "A live graph node record was null and was skipped."));
                continue;
            }

            if (!System.Guid.TryParseExact(node.NodeGuid, "D", out _))
            {
                if (node.Kind == StoryNodeKind.Script)
                {
                    return Result<ProjectSnapshot>.Fail(
                        $"Live script node has no valid D-format guid: '{node.NodeGuid}'.");
                }

                diagnostics.Add(new ProjectDiagnostic(
                    ProjectDiagnosticSeverity.Warning,
                    "LiveNodeGuidInvalid",
                    $"Live {node.Kind} node guid is not D-format: '{node.NodeGuid}'.",
                    node.NodeGuid));
                continue;
            }

            if (!seenGuids.Add(node.NodeGuid))
            {
                diagnostics.Add(new ProjectDiagnostic(
                    ProjectDiagnosticSeverity.Warning,
                    "LiveDuplicateNodeGuid",
                    $"Duplicate live node guid was skipped: {node.NodeGuid}.",
                    node.NodeGuid));
                continue;
            }

            var scenes = new List<SceneSnapshot>(node.Scenes.Count);
            foreach (LiveGraphSceneData? scene in node.Scenes)
            {
                if (scene == null)
                {
                    // Index alignment matters more than content: lease
                    // addresses reference scenes by index, so a dead read
                    // still emits one placeholder at its position.
                    diagnostics.Add(new ProjectDiagnostic(
                        ProjectDiagnosticSeverity.Warning,
                        "LiveNullScene",
                        "A live scene record was null; an empty placeholder keeps indices stable.",
                        node.NodeGuid));
                    scenes.Add(CreatePlaceholderScene(node.NodeGuid, scenes.Count));
                    continue;
                }

                var key = new SceneKey(node.NodeGuid, scene.SceneIndex, scene.Fingerprint);
                scenes.Add(new SceneSnapshot(
                    key,
                    scene.SourceType,
                    scene.DialogueText,
                    scene.IsDialogue,
                    scene.PopupFileName,
                    scene.BackgroundEffect,
                    scene.BackgroundName,
                    scene.AdditionalPrompt,
                    scene.PlaceText,
                    scene.BackgroundFriendlyName,
                    scene.SoundReference,
                    scene.VoiceReference,
                    scene.Transition,
                    scene.BgmId,
                    scene.SelectionGroup)
                {
                    SpeakerSlot = scene.SpeakerSlot,
                    HighlightedSlots = scene.HighlightedSlots,
                    Characters = scene.Characters
                        .Select(character => new ProjectCharacterSnapshot(
                            character.PhysicalSlot,
                            character.Identifier,
                            character.FaceId,
                            character.StartingPosition,
                            character.EndingPosition,
                            character.Emoticon,
                            character.Action,
                            character.Effect,
                            character.Appear,
                            character.ShapeOverride))
                        .ToArray()
                });
                sceneTotal++;
                revision.Append(node.NodeGuid)
                    .Append('|').Append(scene.SceneIndex)
                    .Append('|').Append(scene.Fingerprint)
                    .Append('\n');
            }

            storyNodes.Add(new StoryNodeSnapshot(
                node.SourceIndex,
                node.Kind,
                LiveSourceType,
                node.NodeGuid,
                HasPersistentGuid: true,
                node.Name,
                node.ConnectionGuids,
                scenes.AsReadOnly()));
        }

        if (storyNodes.Count == 0)
        {
            return Result<ProjectSnapshot>.Fail(
                "Live graph produced no usable nodes (all records were null or invalid).");
        }

        string revisionSha256 = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(revision.ToString())));
        var source = new ProjectSourceSnapshot(
            "live://editor-node-graph",
            LivePathKey,
            revisionSha256,
            sceneTotal,
            DateTimeOffset.UtcNow);
        var preview = new ProjectPreviewSnapshot(null, string.Empty, string.Empty);

        return Result<ProjectSnapshot>.Ok(new ProjectSnapshot(
            source,
            LiveSourceType,
            projectKey,
            preview,
            storyNodes.AsReadOnly(),
            diagnostics.AsReadOnly()));
    }

    private static SceneSnapshot CreatePlaceholderScene(string nodeGuid, int index) =>
        new(
            new SceneKey(nodeGuid, index, PlaceholderFingerprint(nodeGuid, index)),
            LiveSourceType,
            string.Empty,
            IsDialogue: false,
            string.Empty,
            0u,
            0u,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            0u,
            0L,
            0L);

    private static string PlaceholderFingerprint(string nodeGuid, int index) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes($"live-placeholder\n{nodeGuid}\n{index}")))
            .ToLowerInvariant();
}
