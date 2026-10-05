using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AzureArchive.VideoTools.Core.Characters;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Commands;

public enum PreviewChainStopReason
{
    None = 0,
    ProjectStart = 1,
    AmbiguousGraph = 2,
    CycleGuard = 3,
    DepthLimit = 4,
    OfficialPositionMove = 5,
    OccupantChange = 6,
    CurrentSceneOfficialMove = 7
}

public readonly record struct PreviewChainAxisState(
    float RelativeFromOfficial,
    bool HasAbsoluteValue,
    float AbsoluteValue)
{
    /// <summary>
    /// True only when an earlier command in the folded chain explicitly
    /// touched this axis. An uncontrolled axis must remain at the live state
    /// created by the current official scene.
    /// </summary>
    public bool IsControlled { get; init; }

    public static PreviewChainAxisState Relative(float value) =>
        new PreviewChainAxisState(value, false, 0f) { IsControlled = true };

    public static PreviewChainAxisState Absolute(float value) =>
        new PreviewChainAxisState(0f, true, value) { IsControlled = true };

    /// <summary>An explicit reset target for this axis.</summary>
    public static PreviewChainAxisState Official() =>
        new PreviewChainAxisState(0f, false, 0f) { IsControlled = true };

    public PreviewChainAxisState Add(float delta) => HasAbsoluteValue
        ? new PreviewChainAxisState(RelativeFromOfficial, true, AbsoluteValue + delta)
            { IsControlled = true }
        : new PreviewChainAxisState(RelativeFromOfficial + delta, false, 0f)
            { IsControlled = true };

    /// <summary>An untouched axis. Retained as the compatibility factory.</summary>
    public static PreviewChainAxisState Clear() => default;
}

public sealed record PreviewChainSlotState(
    int PublicSlot,
    PreviewChainAxisState X,
    PreviewChainAxisState Y,
    PreviewChainAxisState RotationZ,
    bool FlippedFromOfficial,
    int FoldedCommandCount)
{
    /// <summary>
    /// Distinguishes an explicit flip/reset target from a chain that never
    /// mentioned horizontal orientation.
    /// </summary>
    public bool FlipControlled { get; init; }

    /// <summary>Local X pitch; omitted by old chains and uncontrolled until explicitly commanded.</summary>
    public PreviewChainAxisState RotationX { get; init; }
}

public sealed record PreviewChainSlotResolution(
    int PublicSlot,
    PreviewChainSlotState State,
    PreviewChainStopReason StopReason,
    IReadOnlyList<string> Diagnostics)
{
    /// <summary>Occupant the AAP expects in this slot for the current scene;
    /// empty when the slot has no character in the project snapshot.</summary>
    public string ExpectedOccupant { get; init; } = string.Empty;

    /// <summary>
    /// Stable opaque identity for the current continuous occupant lineage.
    /// It changes at occupant, official-position, project-revision, and unsafe
    /// graph boundaries, but not on ordinary scene advances inside the chain.
    /// </summary>
    public string LineageIdentity { get; init; } = string.Empty;

    public bool HasInheritedStart => State.FoldedCommandCount != 0;

    public bool HasExpectedOccupant =>
        !string.IsNullOrWhiteSpace(ExpectedOccupant);

    public bool HasLineageIdentity =>
        !string.IsNullOrWhiteSpace(LineageIdentity);
}

public sealed record PreviewChainResolution(
    SceneKey Scene,
    IReadOnlyList<PreviewChainSlotResolution> Slots)
{
    /// <summary>
    /// Overlays the chain is still holding when this scene starts, folded per (slot, track) with the
    /// latest ancestor directive winning and a <c>clear</c> cutting it off. The preview applies them
    /// — at their tail frame — before the scene's own directives, so a card that never mentions an
    /// overlay still shows the pose continuous playback would be in.
    /// </summary>
    public IReadOnlyList<PreviewChainSpineOverlay> SpineOverlays { get; init; } =
        Array.Empty<PreviewChainSpineOverlay>();
}

/// <summary>One overlay the chain holds: the character slot, the reserved track, and what set it.</summary>
public sealed record PreviewChainSpineOverlay(int PublicSlot, int TrackIndex, string CanonicalDirective);

public sealed class PreviewChainResolver
{
    private const int MaximumWalkDepth = 512;

    private readonly ProjectSnapshot _project;
    private readonly StoryGraphPredecessorResolver _graph;
    private readonly IReadOnlyDictionary<string, StoryNodeSnapshot> _nodesByGuid;
    private readonly ICharacterTransformDirectiveParser _parser;
    private readonly SlotPendingCommandFamilyCompiler _slotPendingParser = new();

    public PreviewChainResolver(
        ProjectSnapshot project,
        ICharacterTransformDirectiveParser? parser = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        _project = project;
        _graph = new StoryGraphPredecessorResolver(project);
        var nodes = new Dictionary<string, StoryNodeSnapshot>(StringComparer.Ordinal);
        foreach (StoryNodeSnapshot node in project.Nodes)
        {
            nodes.TryAdd(node.NodeGuid, node);
        }

        _nodesByGuid = nodes;
        _parser = parser ?? new CharacterTransformDirectiveParser();
    }

    public Result<PreviewChainResolution> Resolve(
        SceneKey sceneKey,
        IEnumerable<int> publicSlots,
        Func<SceneKey, int, string?> sceneDirectiveLookup)
    {
        ArgumentNullException.ThrowIfNull(sceneKey);
        ArgumentNullException.ThrowIfNull(publicSlots);
        ArgumentNullException.ThrowIfNull(sceneDirectiveLookup);

        if (!_nodesByGuid.TryGetValue(sceneKey.NodeGuid, out StoryNodeSnapshot? currentNode))
        {
            return Result<PreviewChainResolution>.Fail(
                $"Current story node was not found: {sceneKey.NodeGuid}.");
        }

        if (sceneKey.SceneIndex < 0 || sceneKey.SceneIndex >= currentNode.Scenes.Count)
        {
            return Result<PreviewChainResolution>.Fail(
                $"Current scene index is outside node '{sceneKey.NodeGuid}': {sceneKey.SceneIndex}.");
        }

        SceneSnapshot currentScene = currentNode.Scenes[sceneKey.SceneIndex];
        var resolutions = new List<PreviewChainSlotResolution>();
        foreach (int slot in publicSlots.Distinct().OrderBy(slot => slot))
        {
            resolutions.Add(ResolveSlot(currentScene, sceneKey, slot, sceneDirectiveLookup));
        }

        return Result<PreviewChainResolution>.Ok(new PreviewChainResolution(
            sceneKey, Array.AsReadOnly(resolutions.ToArray())));
    }

    private PreviewChainSlotResolution ResolveSlot(
        SceneSnapshot currentScene,
        SceneKey sceneKey,
        int publicSlot,
        Func<SceneKey, int, string?> sceneDirectiveLookup)
    {
        string currentOccupant = OccupantIdentifier(currentScene, publicSlot);
        // An official position transition only animates the slot position;
        // rotation and flip persist through it on the live transform. The
        // position lineage therefore stops at the newest official move while
        // the orientation lineage continues to the ordinary boundaries.
        bool currentSceneOfficialMove = currentScene.Characters.Any(character =>
            character.PhysicalSlot == publicSlot
            && character.HasOfficialPositionTransition);

        // Ancestors are visited newest-first, so directives are collected here
        // and folded in reverse to preserve chronological semantics for
        // order-sensitive operations such as reset.
        var collectedDirectives = new List<string>();
        var diagnostics = new List<string>();

        var visitedScenes = new HashSet<SceneKey> { sceneKey };
        SceneKey cursor = sceneKey;
        SceneKey oldestContinuousScene = sceneKey;
        SceneKey lineageBoundaryScene = sceneKey;
        PreviewChainStopReason stopReason = currentSceneOfficialMove
            ? PreviewChainStopReason.CurrentSceneOfficialMove
            : PreviewChainStopReason.ProjectStart;
        bool foldCollected = true;
        bool seekingPendingAcrossEmptyEntry = false;
        // Count of collected directives newer than or on the newest official
        // move; -1 while no official move participates in the chain. Position
        // is uncontrolled past the boundary unless a newer directive sets it.
        int positionBoundaryCount = currentSceneOfficialMove ? 0 : -1;
        for (int depth = 0; depth < MaximumWalkDepth; depth++)
        {
            Result<StoryPredecessorResolution> predecessor =
                _graph.ResolvePredecessor(cursor.NodeGuid, cursor.SceneIndex);
            if (!predecessor.Success || predecessor.Value == null)
            {
                diagnostics.Add(predecessor.Error);
                if (positionBoundaryCount < 0)
                {
                    stopReason = PreviewChainStopReason.CycleGuard;
                    lineageBoundaryScene = sceneKey;
                }

                foldCollected = true;
                break;
            }

            switch (predecessor.Value.Status)
            {
                case StoryPredecessorStatus.ProjectStart:
                    if (positionBoundaryCount < 0)
                    {
                        stopReason = seekingPendingAcrossEmptyEntry
                            ? PreviewChainStopReason.OccupantChange
                            : PreviewChainStopReason.ProjectStart;
                        lineageBoundaryScene = oldestContinuousScene;
                    }

                    goto FoldAndReturn;
                case StoryPredecessorStatus.AmbiguousIncoming:
                    diagnostics.Add($"incoming-node-count={predecessor.Value.IncomingNodeCount}");
                    if (positionBoundaryCount < 0)
                    {
                        stopReason = PreviewChainStopReason.AmbiguousGraph;
                        lineageBoundaryScene = oldestContinuousScene;
                    }

                    goto FoldAndReturn;
            }

            SceneKey ancestorKey = predecessor.Value.Predecessor!;
            if (!visitedScenes.Add(ancestorKey))
            {
                if (positionBoundaryCount < 0)
                {
                    stopReason = PreviewChainStopReason.CycleGuard;
                    lineageBoundaryScene = sceneKey;
                }

                goto FoldAndReturn;
            }

            StoryNodeSnapshot ancestorNode = _nodesByGuid[ancestorKey.NodeGuid];
            SceneSnapshot ancestorScene =
                ancestorNode.Scenes[ancestorKey.SceneIndex];

            string ancestorOccupant = OccupantIdentifier(ancestorScene, publicSlot);
            if (!string.Equals(ancestorOccupant, currentOccupant, StringComparison.Ordinal))
            {
                if (!string.IsNullOrEmpty(currentOccupant)
                    && string.IsNullOrEmpty(ancestorOccupant))
                {
                    seekingPendingAcrossEmptyEntry = true;
                    string? pendingDirective = sceneDirectiveLookup(ancestorKey, publicSlot);
                    if (IsSlotPendingDirective(pendingDirective))
                    {
                        collectedDirectives.Add(pendingDirective!);
                        diagnostics.Add("slot-pending-entry=true");
                        if (positionBoundaryCount < 0)
                        {
                            stopReason = PreviewChainStopReason.OccupantChange;
                            lineageBoundaryScene = oldestContinuousScene;
                        }

                        goto FoldAndReturn;
                    }

                    if (pendingDirective != null)
                    {
                        diagnostics.Add("ignored-non-pending-empty-slot-directive=true");
                    }

                    cursor = ancestorKey;
                    continue;
                }

                diagnostics.Add($"ancestor-occupant={ancestorOccupant}");
                if (positionBoundaryCount < 0)
                {
                    stopReason = PreviewChainStopReason.OccupantChange;
                    lineageBoundaryScene = oldestContinuousScene;
                }

                goto FoldAndReturn;
            }

            oldestContinuousScene = ancestorKey;

            if (seekingPendingAcrossEmptyEntry)
            {
                // The occupant re-enters this slot after an absence: the
                // fresh spawn at re-entry resets the pose, so directives from
                // the previous stint in this slot must not leak across the
                // empty stretch.
                if (positionBoundaryCount < 0)
                {
                    stopReason = PreviewChainStopReason.OccupantChange;
                    lineageBoundaryScene = oldestContinuousScene;
                }

                goto FoldAndReturn;
            }

            if (ancestorScene.Characters.Any(character =>
                    character.PhysicalSlot == publicSlot
                    && character.HasOfficialPositionTransition))
            {
                // The official move takes over this slot's position from this
                // scene forward, so older directives cannot carry position.
                // Rotation and flip persist through the move on the live
                // transform, so the walk continues for the orientation
                // lineage; this scene's own command still runs after the move.
                string? ownDirective = sceneDirectiveLookup(ancestorKey, publicSlot);
                if (ownDirective != null)
                {
                    collectedDirectives.Add(ownDirective);
                }

                if (positionBoundaryCount < 0)
                {
                    positionBoundaryCount = collectedDirectives.Count;
                    stopReason = PreviewChainStopReason.OfficialPositionMove;
                    lineageBoundaryScene = ancestorKey;
                }

                cursor = ancestorKey;
                continue;
            }

            string? directive = sceneDirectiveLookup(ancestorKey, publicSlot);
            if (directive != null && !IsSlotPendingDirective(directive))
            {
                collectedDirectives.Add(directive);
            }
            else if (directive != null)
            {
                diagnostics.Add("ignored-pending-on-occupied-slot=true");
            }

            cursor = ancestorKey;
        }

        if (positionBoundaryCount < 0)
        {
            stopReason = PreviewChainStopReason.DepthLimit;
            lineageBoundaryScene = sceneKey;
        }

        FoldAndReturn:
        var x = PreviewChainAxisState.Clear();
        var y = PreviewChainAxisState.Clear();
        var rotationX = PreviewChainAxisState.Clear();
        var rotationZ = PreviewChainAxisState.Clear();
        bool flipped = false;
        bool flipControlled = false;
        int foldedCommands = 0;
        if (foldCollected)
        {
            for (int index = collectedDirectives.Count - 1; index >= 0; index--)
            {
                if (Fold(
                        collectedDirectives[index],
                        publicSlot,
                        ref x,
                        ref y,
                        ref rotationX,
                        ref rotationZ,
                        ref flipped,
                        ref flipControlled,
                        diagnostics))
                {
                    foldedCommands++;
                }
            }
        }

        if (positionBoundaryCount >= 0)
        {
            // Position is owned by the newest official move: only directives
            // from that scene forward may set it; otherwise the axis stays
            // uncontrolled so the official move and the live scene state keep
            // authority over the position.
            var moveX = PreviewChainAxisState.Clear();
            var moveY = PreviewChainAxisState.Clear();
            for (int index = positionBoundaryCount - 1; index >= 0; index--)
            {
                FoldPosition(
                    collectedDirectives[index],
                    publicSlot,
                    ref moveX,
                    ref moveY);
            }

            x = moveX.IsControlled ? moveX : PreviewChainAxisState.Clear();
            y = moveY.IsControlled ? moveY : PreviewChainAxisState.Clear();
        }

        return State(
            publicSlot,
            x,
            y,
            rotationX,
            rotationZ,
            flipped,
            flipControlled,
            foldedCommands,
            stopReason,
            diagnostics,
            currentOccupant,
            CreateLineageIdentity(
                publicSlot,
                currentOccupant,
                stopReason,
                lineageBoundaryScene));
    }

    private bool Fold(
        string directive,
        int expectedSlot,
        ref PreviewChainAxisState x,
        ref PreviewChainAxisState y,
        ref PreviewChainAxisState rotationX,
        ref PreviewChainAxisState rotationZ,
        ref bool flipped,
        ref bool flipControlled,
        ICollection<string> diagnostics)
    {
        Result<CharacterTransformCommand> parsed = IsSlotPendingDirective(directive)
            ? _slotPendingParser.ParseCanonical(directive)
            : _parser.Parse(directive);
        if (!parsed.Success || parsed.Value == null)
        {
            diagnostics.Add($"unparsable-directive={directive}");
            return false;
        }

        CharacterTransformCommand command = parsed.Value;
        if (command.PublicSlot != expectedSlot)
        {
            diagnostics.Add($"slot-mismatch={command.PublicSlot}");
            return false;
        }

        switch (command.Operation)
        {
            case CharacterTransformOperation.Reset:
                // Reset explicitly controls every public character axis and
                // returns it to the official origin. Later commands in the
                // same chain must still fold on top of these controlled
                // targets instead of falling back to uncontrolled state.
                x = PreviewChainAxisState.Official();
                y = PreviewChainAxisState.Official();
                rotationX = PreviewChainAxisState.Official();
                rotationZ = PreviewChainAxisState.Official();
                flipped = false;
                flipControlled = true;
                return true;
            case CharacterTransformOperation.Set:
                if (command.X.HasValue)
                {
                    x = PreviewChainAxisState.Absolute(command.X.Value);
                }

                if (command.Y.HasValue)
                {
                    y = PreviewChainAxisState.Absolute(command.Y.Value);
                }

                if (command.RotationDegrees.HasValue)
                {
                    rotationZ = PreviewChainAxisState.Absolute(command.RotationDegrees.Value);
                }

                if (command.RotationXDegrees.HasValue)
                {
                    rotationX = PreviewChainAxisState.Absolute(command.RotationXDegrees.Value);
                }

                if (command.FlipX.HasValue)
                {
                    flipped = command.FlipX.Value;
                    flipControlled = true;
                }

                return true;
            case CharacterTransformOperation.Move:
                if (command.DeltaX.HasValue)
                {
                    x = x.Add(command.DeltaX.Value);
                }

                if (command.DeltaY.HasValue)
                {
                    y = y.Add(command.DeltaY.Value);
                }

                if (command.DeltaRotationDegrees.HasValue)
                {
                    rotationZ = rotationZ.Add(command.DeltaRotationDegrees.Value);
                }

                if (command.DeltaRotationXDegrees.HasValue)
                {
                    rotationX = rotationX.Add(command.DeltaRotationXDegrees.Value);
                }

                return true;
            default:
                diagnostics.Add($"unknown-operation={command.Operation}");
                return false;
        }
    }

    /// <summary>Folds only the position axes for the post-official-move era.
    /// Parse failures are silent here: the full fold already reports them.</summary>
    private bool FoldPosition(
        string directive,
        int expectedSlot,
        ref PreviewChainAxisState x,
        ref PreviewChainAxisState y)
    {
        Result<CharacterTransformCommand> parsed = IsSlotPendingDirective(directive)
            ? _slotPendingParser.ParseCanonical(directive)
            : _parser.Parse(directive);
        if (!parsed.Success || parsed.Value == null)
        {
            return false;
        }

        CharacterTransformCommand command = parsed.Value;
        if (command.PublicSlot != expectedSlot)
        {
            return false;
        }

        switch (command.Operation)
        {
            case CharacterTransformOperation.Reset:
                x = PreviewChainAxisState.Official();
                y = PreviewChainAxisState.Official();
                return true;
            case CharacterTransformOperation.Set:
                if (command.X.HasValue)
                {
                    x = PreviewChainAxisState.Absolute(command.X.Value);
                }

                if (command.Y.HasValue)
                {
                    y = PreviewChainAxisState.Absolute(command.Y.Value);
                }

                return true;
            case CharacterTransformOperation.Move:
                if (command.DeltaX.HasValue)
                {
                    x = x.Add(command.DeltaX.Value);
                }

                if (command.DeltaY.HasValue)
                {
                    y = y.Add(command.DeltaY.Value);
                }

                return true;
            default:
                return false;
        }
    }

    private static string OccupantIdentifier(SceneSnapshot scene, int publicSlot) =>
        scene.Characters.FirstOrDefault(character => character.PhysicalSlot == publicSlot)
            ?.Identifier ?? string.Empty;

    private static bool IsSlotPendingDirective(string? directive) =>
        directive != null
        && directive.StartsWith(
            SlotPendingCommandFamilyCompiler.CanonicalRootToken + ";",
            StringComparison.Ordinal);

    private static PreviewChainSlotResolution State(
        int publicSlot,
        PreviewChainAxisState x,
        PreviewChainAxisState y,
        PreviewChainAxisState rotationX,
        PreviewChainAxisState rotationZ,
        bool flipped,
        bool flipControlled,
        int foldedCommands,
        PreviewChainStopReason stopReason,
        IReadOnlyList<string> diagnostics,
        string expectedOccupant,
        string lineageIdentity) => new(
            publicSlot,
            new PreviewChainSlotState(
                publicSlot, x, y, rotationZ, flipped, foldedCommands)
            {
                FlipControlled = flipControlled,
                RotationX = rotationX
            },
            stopReason,
            diagnostics)
    {
        ExpectedOccupant = expectedOccupant,
        LineageIdentity = lineageIdentity
    };

    private string CreateLineageIdentity(
        int publicSlot,
        string occupantIdentifier,
        PreviewChainStopReason stopReason,
        SceneKey boundaryScene)
    {
        if (string.IsNullOrWhiteSpace(occupantIdentifier))
        {
            return string.Empty;
        }

        string boundaryKind = stopReason switch
        {
            PreviewChainStopReason.OfficialPositionMove => "official-position",
            PreviewChainStopReason.CurrentSceneOfficialMove => "official-position",
            PreviewChainStopReason.OccupantChange => "occupant",
            PreviewChainStopReason.AmbiguousGraph => "ambiguous",
            PreviewChainStopReason.CycleGuard => "cycle",
            PreviewChainStopReason.DepthLimit => "depth-limit",
            PreviewChainStopReason.ProjectStart => "project-start",
            _ => "unknown"
        };
        string material = string.Join(
            '\n',
            "character-lineage/v1",
            _project.Source.PathKey,
            _project.Source.RevisionSha256.ToUpperInvariant(),
            publicSlot.ToString(CultureInfo.InvariantCulture),
            occupantIdentifier,
            boundaryKind,
            boundaryScene.NodeGuid,
            boundaryScene.SceneIndex.ToString(CultureInfo.InvariantCulture),
            boundaryScene.Fingerprint.ToUpperInvariant());
        string sha256 = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(material)));
        return $"character-lineage/v1:{sha256}";
    }
}
