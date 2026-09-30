using System.Globalization;
using AzureArchive.VideoTools.Core.Projects;

namespace AzureArchive.VideoTools.Core.Commands;

/// <summary>
/// Read-only lookup of canonical directives per scene, built from the AAP
/// embedded Additional Prompt text only. Characters are keyed by
/// (node, scene, slot 1..5); the singleton scene camera is keyed by
/// (node, scene) because it is one resource per scene
/// (<see cref="SceneCameraCommandFamilyCompiler.SingletonResourceSlot"/>).
/// Scenes whose extraction produced errors are excluded entirely (fail-closed)
/// so a malformed directive can never feed preview chain inheritance.
/// </summary>
public sealed class PreviewChainDirectiveIndex
{
    private const int MaximumSlotsPerScene = 5;

    private readonly Dictionary<string, IReadOnlyList<IReadOnlyDictionary<int, string>>>
        _scenesByNode = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string?>> _camerasByNode =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string>>>
        _spineByNode = new(StringComparer.Ordinal);

    public int SceneCount { get; private set; }

    public int DirectiveCount { get; private set; }

    public int SkippedSceneCount { get; private set; }

    /// <summary>Scenes carrying a canonical scene camera directive.</summary>
    public int CameraSceneCount { get; private set; }

    /// <summary>Scenes carrying at least one spine overlay directive.</summary>
    public int SpineSceneCount { get; private set; }

    /// <summary>Overlay directives indexed, counted per (slot, track).</summary>
    public int SpineOverlayCount { get; private set; }

    public static PreviewChainDirectiveIndex Build(
        ProjectSnapshot project,
        bool acceptLegacyCharacterAlias)
        => Build(project, acceptLegacyCharacterAlias, static text => text);

    public static PreviewChainDirectiveIndex Build(
        ProjectSnapshot project,
        bool acceptLegacyCharacterAlias,
        Func<string, string> promptProjection)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(promptProjection);

        var extractor = new EmbeddedAavtDirectiveExtractor();
        var index = new PreviewChainDirectiveIndex();
        foreach (StoryNodeSnapshot node in project.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.NodeGuid)
                || index._scenesByNode.ContainsKey(node.NodeGuid))
            {
                continue;
            }

            var scenes = new IReadOnlyDictionary<int, string>[node.Scenes.Count];
            var cameras = new string?[node.Scenes.Count];
            var overlays = new IReadOnlyDictionary<string, string>[node.Scenes.Count];
            for (int sceneIndex = 0; sceneIndex < node.Scenes.Count; sceneIndex++)
            {
                EmbeddedAavtExtraction extraction = extractor.Extract(
                    promptProjection(node.Scenes[sceneIndex].AdditionalPrompt),
                    acceptLegacyCharacterAlias);
                if (extraction.Errors.Count != 0)
                {
                    if (extraction.Commands.Count != 0)
                    {
                        index.SkippedSceneCount++;
                    }

                    continue;
                }

                EmbeddedAavtCommand[] characterCommands = extraction.Commands
                    .Where(command =>
                        command.PublicSlot is >= 1 and <= MaximumSlotsPerScene
                        && (command.CanonicalDirective.StartsWith(
                                "#char;",
                                StringComparison.Ordinal)
                            || command.CanonicalDirective.StartsWith(
                                SlotPendingCommandFamilyCompiler.CanonicalRootToken + ";",
                                StringComparison.Ordinal)))
                    .ToArray();
                if (characterCommands.Length != 0)
                {
                    var slots = new Dictionary<int, string>();
                    foreach (EmbeddedAavtCommand command in characterCommands)
                    {
                        slots[command.PublicSlot] = command.CanonicalDirective;
                    }

                    scenes[sceneIndex] = slots;
                    index.SceneCount++;
                    index.DirectiveCount += slots.Count;
                }

                // At most one camera directive per scene is enforced by the
                // extractor's occupied-slot set, so the first match is the one.
                EmbeddedAavtCommand? cameraCommand = extraction.Commands.FirstOrDefault(
                    command => command.CanonicalDirective.StartsWith(
                        SceneCameraCommandFamilyCompiler.CanonicalRootToken + ";",
                        StringComparison.Ordinal));
                if (cameraCommand != null)
                {
                    cameras[sceneIndex] = cameraCommand.CanonicalDirective;
                    index.CameraSceneCount++;
                }

                // Spine overlays are keyed by (slot, track): a card may drive several tracks of one
                // character at once, so unlike the camera they are not a singleton, and unlike a
                // character transform they survive into later scenes until something clears them.
                // A later directive on the same key replaces the earlier one in this scene, exactly
                // as the runtime would on one card.
                var spine = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (EmbeddedAavtCommand command in extraction.Commands)
                {
                    if (!command.CanonicalDirective.StartsWith(
                            SpineOverlayCommandFamilyCompiler.CanonicalRootToken + ";",
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    int track = CommandResourceIdentity.SpineTrackOf(command.CanonicalDirective);
                    if (track < 0) continue;
                    spine[command.PublicSlot + ":" + track.ToString(CultureInfo.InvariantCulture)] =
                        command.CanonicalDirective;
                }

                if (spine.Count != 0)
                {
                    overlays[sceneIndex] = spine;
                    index.SpineSceneCount++;
                    index.SpineOverlayCount += spine.Count;
                }
            }

            index._scenesByNode[node.NodeGuid] = Array.AsReadOnly(scenes);
            index._camerasByNode[node.NodeGuid] = Array.AsReadOnly(cameras);
            index._spineByNode[node.NodeGuid] = Array.AsReadOnly(overlays);
        }

        return index;
    }

    public bool TryGet(
        string nodeGuid,
        int sceneIndex,
        int publicSlot,
        out string directive)
    {
        directive = string.Empty;
        if (string.IsNullOrWhiteSpace(nodeGuid)
            || !_scenesByNode.TryGetValue(nodeGuid, out IReadOnlyList<IReadOnlyDictionary<int, string>>? scenes)
            || sceneIndex < 0
            || sceneIndex >= scenes.Count
            || scenes[sceneIndex] == null
            || !scenes[sceneIndex].TryGetValue(publicSlot, out string? canonical))
        {
            return false;
        }

        directive = canonical;
        return true;
    }

    /// <summary>
    /// Canonical scene camera directive of one scene, when that scene declared
    /// one. A scene without a camera directive is not an error: the camera
    /// chain folds straight through it, exactly like playback keeps the live
    /// camera state across a scene that never touches it.
    /// </summary>
    public bool TryGetCamera(
        string nodeGuid,
        int sceneIndex,
        out string directive)
    {
        directive = string.Empty;
        if (string.IsNullOrWhiteSpace(nodeGuid)
            || !_camerasByNode.TryGetValue(nodeGuid, out IReadOnlyList<string?>? cameras)
            || sceneIndex < 0
            || sceneIndex >= cameras.Count
            || cameras[sceneIndex] is not { Length: > 0 } canonical)
        {
            return false;
        }

        directive = canonical;
        return true;
    }

    /// <summary>
    /// Overlay directives one scene declared, keyed by <c>"&lt;slot&gt;:&lt;track&gt;"</c>. A scene
    /// without any is not an error: the chain folds straight through it, exactly like playback keeps
    /// a held overlay across a scene that never mentions it.
    /// </summary>
    public bool TryGetSpineOverlays(
        string nodeGuid,
        int sceneIndex,
        out IReadOnlyDictionary<string, string> overlays)
    {
        overlays = EmptyOverlays;
        if (string.IsNullOrWhiteSpace(nodeGuid)
            || !_spineByNode.TryGetValue(
                nodeGuid,
                out IReadOnlyList<IReadOnlyDictionary<string, string>>? scenes)
            || sceneIndex < 0
            || sceneIndex >= scenes.Count
            || scenes[sceneIndex] is not { Count: > 0 } found)
        {
            return false;
        }

        overlays = found;
        return true;
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyOverlays =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
