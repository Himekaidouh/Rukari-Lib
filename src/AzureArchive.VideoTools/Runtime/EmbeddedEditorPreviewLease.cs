using System;
using System.Collections.Generic;
using System.Linq;
using AzureArchive.VideoTools.Api;
using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Interop;

namespace AzureArchive.VideoTools.Runtime;

internal enum EmbeddedEditorPreviewClaimStatus
{
    NoClaim = 0,
    ClaimedNoOp = 1,
    Authorized = 2
}

/// <summary>Lifecycle phase of one DataList preview generation.</summary>
internal enum PreviewGenerationPhase
{
    Collecting = 0,
    AwaitingWindow = 1,
    Closed = 2
}

/// <summary>
/// FIX D surface, declared for the next-stage runtime integrator only. It is
/// intentionally inert until PlayerCommandObservationRuntime consumes it.
/// </summary>
public readonly record struct DataListGenerationScope(
    long GenerationId,
    long MinimumWindowSequenceExclusive,
    bool CollectingCandidates,
    bool ChainApplied);

/// <summary>
/// One per-generation editor preview lease. A lease is born at the DataList
/// postfix together with a managed snapshot of the selected scene, fed by
/// top-level continuous-compile captures, optionally confirmed by the
/// OnChildSelect identity, and consumed exactly once by the first matching
/// editor-preview window.
/// </summary>
internal sealed record EmbeddedEditorPreviewLease(
    long DataListGeneration,
    long MinimumWindowSequenceExclusive,
    int DataListRequestId,
    CompiledScriptIdentity CompiledScript,
    SceneAddress Scene,
    IReadOnlyList<string> CanonicalDirectives,
    bool IsTombstone,
    string StableSceneIdentity)
{
    /// <summary>
    /// Observation sequence of the OnChildSelect identity that confirmed this
    /// generation, or 0 when the lease was never identity-confirmed (a green
    /// preview without OnChildSelect).
    /// </summary>
    public long ConfirmedObservationSequence { get; init; }

    /// <summary>Player window sequence that claimed this lease.</summary>
    public long ClaimedWindowSequence { get; init; }

    // ------------------------------------------------------------------
    // Compatibility shims: PlayerCommandObservationRuntime.cs is owned by
    // the next-stage agent and must keep compiling untouched against these
    // legacy member names.
    // ------------------------------------------------------------------
    public long SelectionGeneration => DataListGeneration;
    public int SelectionRequestId => DataListRequestId;
    public long ObservationSequence => ConfirmedObservationSequence;
}

/// <summary>
/// One DataList generation: the cutoff snapshot, the captured scene address,
/// its compile candidates, and its one-shot lifecycle phase.
/// </summary>
internal sealed record PreviewGeneration(
    long GenerationId,
    int DataListRequestId,
    long MinimumWindowSequenceExclusive,
    SceneAddress? CapturedSceneAddress,
    bool CapturedSceneValid,
    string CapturedSceneError,
    bool SceneConfirmedBySelection,
    CompiledScriptIdentity? ConfirmedScript,
    long ConfirmedObservationSequence,
    Dictionary<CompiledScriptIdentity, EmbeddedEditorPreviewLeaseCache.CaptureCandidate> Candidates,
    PreviewGenerationPhase Phase)
{
    public bool Open => Phase != PreviewGenerationPhase.Closed;

    /// <summary>
    /// Window sequences already authorized under this generation. The green
    /// replay button does NOT raise DataList, so no new generation is born for
    /// repeats: authorization is deduplicated per window sequence instead of
    /// one-shot per generation, keeping sibling/stale windows single-execution
    /// while letting every genuine replay claim again.
    /// </summary>
    public HashSet<long> ClaimedWindowSequences { get; } = new();

    /// <summary>
    /// Canonical live scene address adopted at claim time when the generation
    /// was born before auto-discovery rotated the project key. The pre-rotation
    /// capture carries a non-canonical ProjectKey; adopting the live key keeps
    /// that startup window working instead of failing closed forever.
    /// </summary>
    public SceneAddress? AdoptedLiveAddress { get; set; }

    /// <summary>Effective capture: the adopted live address when present.</summary>
    public SceneAddress? EffectiveCapturedAddress => AdoptedLiveAddress ?? CapturedSceneAddress;
}

/// <summary>
/// Per-generation lease cache for unsaved editor previews. A generation is
/// opened by the DataList postfix, collects continuous-compile candidates,
/// may be scene-confirmed by the selection event, and is claimed once by a
/// single matching preview window. Nothing here binds to a persisted AAP/AAS
/// mapping; the overlay store is keyed by the canonical live project key and
/// is wiped whenever that key rotates.
/// </summary>
internal static class EmbeddedEditorPreviewLeaseCache
{
    private const int MaximumRetainedGenerations = 2;
    private const int MaximumCandidatesPerGeneration = 16;
    private static readonly object Gate = new();
    private static long _selectionGeneration;
    private static readonly List<PreviewGeneration> Generations = new();
    private static string _overlayProjectKey = string.Empty;
    private static readonly Dictionary<OverlaySceneKey, SceneOverlay> Overlays = new();

    /// <summary>
    /// Opens a fresh generation from the DataList postfix. The caller captures
    /// the selected scene first, on the Unity main thread. Superseding closes
    /// prior generations; the last two are retained for cutoff bookkeeping.
    /// <para>
    /// <paramref name="minimumWindowSequenceExclusive"/> is the caller-supplied
    /// CLOSED-window watermark: DataList fires mid-preview-cascade after this
    /// click's windows already opened, so eligibility uses "window closed after
    /// the generation began" rather than "opened after". Windows that were
    /// still open remain claimable; everything fully closed earlier stays
    /// stale.
    /// </para>
    /// </summary>
    public static void BeginPreviewGeneration(
        int requestId,
        ApiResult<SceneSnapshot> capturedScene,
        long minimumWindowSequenceExclusive)
    {
        ArgumentNullException.ThrowIfNull(capturedScene);
        bool captured = capturedScene.Success && capturedScene.Value != null;
        SceneAddress? address = captured ? capturedScene.Value!.Address : null;
        string captureError = captured ? string.Empty : capturedScene.Error;
        long generationId;
        lock (Gate)
        {
            CloseAllGenerationsNoLock();
            generationId = ++_selectionGeneration;
            Generations.Add(new PreviewGeneration(
                generationId,
                requestId,
                Math.Max(0, minimumWindowSequenceExclusive),
                address,
                captured,
                captureError,
                SceneConfirmedBySelection: false,
                ConfirmedScript: null,
                ConfirmedObservationSequence: 0,
                new Dictionary<CompiledScriptIdentity, CaptureCandidate>(),
                PreviewGenerationPhase.Collecting));
            TrimGenerationsNoLock();
        }

        PlayerCommandObservationLog.Append(
            $"{DateTimeOffset.Now:O} editor-preview-lease event=BEGIN; "
            + $"generation={generationId}; request={requestId}; "
            + $"cutoff={Math.Max(0, minimumWindowSequenceExclusive)}; "
            + $"capture={(captured ? "ok" : Escape(captureError))}");
    }

    public static void Capture(
        CompiledScriptIdentity identity,
        EmbeddedAavtExtraction extraction,
        string source)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(extraction);
        if (!string.Equals(source, "CompileScriptContinuous", StringComparison.Ordinal))
        {
            return;
        }

        string[] directives = extraction.Commands
            .Select(command => command.CanonicalDirective)
            .ToArray();
        string validationError = extraction.Errors.Count == 0
            ? string.Empty
            : string.Join(" | ", extraction.Errors);
        // A compile whose only AAVT lines are non-visual (#aavt;continue, the
        // mirrored voice line) carries no canonical command, yet it is an
        // explicit, authored empty effect set. Capturing it as a tombstone
        // keeps that scene's cleanup and inherited-chain semantics. Captured as
        // an ordinary empty candidate it failed closed instead
        // (empty-non-tombstone-candidate-has-no-authority), so the line
        // dispatched nothing, inherited nothing, cleaned nothing, and reported
        // nothing in game.
        bool isTombstone = extraction.HasOnlyNonVisualDirectives;
        StoreCaptureCandidate(identity, directives, extraction.HasContinueDirective,
            validationError, isTombstone);
    }

    public static void CaptureTombstone(
        CompiledScriptIdentity identity,
        string source)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!string.Equals(source, "CompileScriptContinuous", StringComparison.Ordinal))
        {
            return;
        }

        StoreCaptureCandidate(
            identity,
            Array.Empty<string>(),
            hasContinueDirective: false,
            validationError: string.Empty,
            isTombstone: true);
    }

    /// <summary>
    /// Selection-event confirmation (replaces the old bind point). A matching
    /// requestId whose live address equals the DataList capture confirms the
    /// generation; an address mismatch invalidates it. This method never
    /// binds a lease and never authorizes anything.
    /// </summary>
    public static void RefreshGenerationScene(
        EditorSceneIdentitySnapshot identity,
        ApiResult<SceneSnapshot> selectedSceneResult)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(selectedSceneResult);
        lock (Gate)
        {
            PreviewGeneration? generation = Generations.LastOrDefault(item => item.Open);
            if (generation == null || generation.DataListRequestId != identity.SelectionRequestId)
            {
                return;
            }

            bool liveAvailable = selectedSceneResult.Success && selectedSceneResult.Value != null;
            if (!liveAvailable)
            {
                PlayerCommandObservationLog.Append(
                    $"{DateTimeOffset.Now:O} editor-preview-lease event=SCENE_UNAVAILABLE; "
                    + $"generation={generation.GenerationId}; request={identity.SelectionRequestId}; "
                    + $"error={Escape(selectedSceneResult.Error)}");
                return;
            }

            SceneAddress live = selectedSceneResult.Value!.Address;
            bool sameAsCapture = generation.CapturedSceneValid
                && generation.CapturedSceneAddress != null
                && SameScene(generation.CapturedSceneAddress, live);
            if (!sameAsCapture)
            {
                SetPhaseNoLock(generation, PreviewGenerationPhase.Closed);
                PlayerCommandObservationLog.Append(
                    $"{DateTimeOffset.Now:O} editor-preview-lease event=INVALID; "
                    + $"generation={generation.GenerationId}; request={identity.SelectionRequestId}; "
                    + "reason=selection-scene-mismatch-drift-from-data-list-capture");
                return;
            }

            var confirmedScript = new CompiledScriptIdentity(
                identity.CompiledScriptSha256,
                identity.CompiledScriptLength,
                identity.CompiledScriptLineCount);
            Generations[Generations.IndexOf(generation)] = generation with
            {
                SceneConfirmedBySelection = true,
                ConfirmedScript = confirmedScript,
                ConfirmedObservationSequence = identity.ObservationSequence,
                Phase = PreviewGenerationPhase.AwaitingWindow
            };
            PlayerCommandObservationLog.Append(
                $"{DateTimeOffset.Now:O} editor-preview-lease event=SCENE_CONFIRMED; "
                + $"generation={generation.GenerationId}; request={identity.SelectionRequestId}; "
                + $"observation={identity.ObservationSequence}");
        }
    }

    /// <summary>
    /// One-shot claim of the open generation by a closed advance window.
    /// Validation order: open generation, cutoff, unique window/candidate
    /// intersection, candidate health, selection-identity agreement, and a
    /// final live re-verify against the generation's captured scene. Every
    /// rejection past the cutoff consumes the generation.
    /// </summary>
    public static EmbeddedEditorPreviewClaimStatus TryClaim(
        long windowSequence,
        IReadOnlyList<string> managedUnityMessages,
        PlayerRuntimeContextSnapshot context,
        EditorSceneIdentitySnapshot? currentIdentity,
        ApiResult<SceneSnapshot> currentScene,
        out EmbeddedEditorPreviewLease? lease,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(managedUnityMessages);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(currentScene);
        lease = null;
        error = string.Empty;

        lock (Gate)
        {
            var windowIdentityCounts = new Dictionary<CompiledScriptIdentity, int>();
            foreach (string message in managedUnityMessages)
            {
                var identity = CommandIdentity.CompiledScript(message);
                windowIdentityCounts[identity] =
                    windowIdentityCounts.TryGetValue(identity, out int count) ? count + 1 : 1;
            }

            CompiledScriptIdentity? currentScript = currentIdentity == null
                ? null
                : new CompiledScriptIdentity(
                    currentIdentity.CompiledScriptSha256,
                    currentIdentity.CompiledScriptLength,
                    currentIdentity.CompiledScriptLineCount);

            // (1) An open generation must exist.
            PreviewGeneration? generation = Generations.LastOrDefault(item => item.Open);
            if (generation == null)
            {
                // A closed-but-retained generation still suppresses any
                // window that references it so sibling previews can never
                // fall through to stale cached-directive fallbacks.
                PreviewGeneration? consumed = Generations.LastOrDefault();
                bool referencesConsumed = consumed != null
                    && (windowIdentityCounts.Keys.Any(consumed.Candidates.ContainsKey)
                        || (currentScript != null
                            && windowIdentityCounts.ContainsKey(currentScript)));
                if (!referencesConsumed)
                {
                    return EmbeddedEditorPreviewClaimStatus.NoClaim;
                }

                error = "editor preview lease was already consumed";
                return EmbeddedEditorPreviewClaimStatus.ClaimedNoOp;
            }

            // (2) Stale sibling suppression: windows issued before this
            // generation never consume it.
            if (windowSequence <= generation.MinimumWindowSequenceExclusive)
            {
                error = "matching editor preview window predates the current selection generation";
                PlayerCommandObservationLog.Append(
                    $"{DateTimeOffset.Now:O} editor-preview-lease event=STALE_WINDOW; "
                    + $"generation={generation.GenerationId}; sequence={windowSequence}; "
                    + $"request={generation.DataListRequestId}; reason={Escape(error)}");
                return EmbeddedEditorPreviewClaimStatus.ClaimedNoOp;
            }

            // (2b) Per-window dedup: the same window sequence can never be
            // authorized twice. Replays arrive with NEW sequences, so the
            // green replay button keeps working without a fresh DataList.
            if (generation.ClaimedWindowSequences.Contains(windowSequence))
            {
                error = "editor preview window was already claimed";
                return EmbeddedEditorPreviewClaimStatus.ClaimedNoOp;
            }

            // (3) Intersect window message identities with generation
            // candidates. Zero intersection only means the window does not
            // claim this generation when nothing about it references the
            // current editor script either.
            List<CompiledScriptIdentity> intersections = windowIdentityCounts.Keys
                .Where(candidateKey => generation.Candidates.ContainsKey(candidateKey))
                .ToList();
            bool windowClaimsSelection = intersections.Count != 0
                || (currentScript != null && windowIdentityCounts.ContainsKey(currentScript));
            if (!windowClaimsSelection)
            {
                return EmbeddedEditorPreviewClaimStatus.NoClaim;
            }

            CompiledScriptIdentity? matched = intersections.Count == 1
                && windowIdentityCounts[intersections[0]] == 1
                    ? intersections[0]
                    : null;
            if (matched == null)
            {
                // Rejections no longer consume the generation: each window
                // sequence is naturally unique, so per-window dedup (2b)
                // already prevents double execution while genuine replays
                // keep the generation serviceable.
                error = "ambiguous-or-missing-window-intersection";
                EmitBindLine(generation, matchedScriptSha: string.Empty, commands: 0,
                    tombstone: false, hasContinueDirective: null, accepted: false,
                    reason: error);
                return EmbeddedEditorPreviewClaimStatus.ClaimedNoOp;
            }

            // (4) Candidate health.
            CaptureCandidate candidate = generation.Candidates[matched];
            string? healthError =
                candidate.Conflict ? "conflicting-continuous-compile-candidates"
                : candidate.ValidationError.Length != 0
                    ? "invalid-embedded-directives: " + candidate.ValidationError
                    : candidate.CanonicalDirectives.Count
                        > EmbeddedAavtDirectiveExtractor.MaxCommandsPerScene
                        ? "invalid-embedded-directives: candidate exceeds the scene directive budget"
                        : candidate.CanonicalDirectives.Count == 0 && !candidate.IsTombstone
                            ? "empty-non-tombstone-candidate-has-no-authority"
                            : null;
            if (healthError != null)
            {
                error = healthError;
                EmitBindLine(generation, matched.Sha256, candidate.CanonicalDirectives.Count,
                    candidate.IsTombstone, candidate.HasContinueDirective,
                    accepted: false, reason: error);
                return EmbeddedEditorPreviewClaimStatus.ClaimedNoOp;
            }

            // (5) When OnChildSelect confirmed this generation, the window's
            // matched compiled script must equal the confirmed identity.
            if (generation.SceneConfirmedBySelection
                && generation.ConfirmedScript != null
                && generation.ConfirmedScript != matched)
            {
                error = "selection-identity-drift";
                EmitBindLine(generation, matched.Sha256, candidate.CanonicalDirectives.Count,
                    candidate.IsTombstone, candidate.HasContinueDirective,
                    accepted: false, reason: error);
                return EmbeddedEditorPreviewClaimStatus.ClaimedNoOp;
            }

            // (6) Live re-verify against the generation's own capture. Only the
            // three CONTENT fields (NodeGuid/SceneIndex/Fingerprint) decide
            // scene identity. ProjectKey may differ legitimately: auto-discovery
            // rotates it seconds after boot, and before that rotation BOTH the
            // capture and the live address carry the non-canonical sentinel.
            // Adopting the live address keeps the startup window working; a key
            // rotation afterwards wipes overlays and generations wholesale, so
            // a pre-discovery authorization cannot leak into the discovered
            // namespace. Content mismatches still fail closed.
            bool liveVerified = false;
            SceneAddress? adoptedLiveAddress = null;
            if (currentScene.Success
                && currentScene.Value != null
                && generation.CapturedSceneValid)
            {
                SceneAddress? captured = generation.EffectiveCapturedAddress;
                SceneAddress live = currentScene.Value.Address;
                if (captured != null)
                {
                    bool sceneIdentityMatches = string.Equals(
                        captured.NodeGuid,
                        live.NodeGuid,
                        StringComparison.Ordinal)
                        && captured.SceneIndex == live.SceneIndex
                        && string.Equals(
                            captured.Fingerprint,
                            live.Fingerprint,
                            StringComparison.Ordinal);
                    if (sceneIdentityMatches)
                    {
                        adoptedLiveAddress = live;
                        liveVerified = true;
                    }
                }
            }
            if (!liveVerified)
            {
                error = "live-editor-scene-address-drift-from-generation-capture";
                EmitBindLine(generation, matched.Sha256, candidate.CanonicalDirectives.Count,
                    candidate.IsTombstone, candidate.HasContinueDirective,
                    accepted: false, reason: error);
                return EmbeddedEditorPreviewClaimStatus.ClaimedNoOp;
            }

            // (7) Claim this window. The generation stays open so the green
            // replay button (which never raises DataList) can claim again with
            // each new window sequence; supersession, index reload, and key
            // rotation remain the only closers.
            if (adoptedLiveAddress != null)
            {
                generation.AdoptedLiveAddress = adoptedLiveAddress;
            }

            generation.ClaimedWindowSequences.Add(windowSequence);

            SceneAddress effectiveCapture = adoptedLiveAddress
                ?? generation.EffectiveCapturedAddress!;

            lease = new EmbeddedEditorPreviewLease(
                generation.GenerationId,
                generation.MinimumWindowSequenceExclusive,
                generation.DataListRequestId,
                matched,
                effectiveCapture,
                candidate.CanonicalDirectives,
                candidate.IsTombstone,
                StableSceneIdentity(effectiveCapture))
            {
                ConfirmedObservationSequence = generation.ConfirmedObservationSequence,
                ClaimedWindowSequence = windowSequence
            };

            // (9) Overlay write moved behind full authorization. A tombstone
            // stores an EMPTY directive dictionary that wholesale-replaces the
            // previous entry so TryGetOverlayDirective reports
            // sceneIsAuthoritative=true with a null canonical directive.
            UpdateOverlay(effectiveCapture, candidate);

            EmitBindLine(generation, matched.Sha256, candidate.CanonicalDirectives.Count,
                candidate.IsTombstone, candidate.HasContinueDirective,
                accepted: true, reason: string.Empty);
            return EmbeddedEditorPreviewClaimStatus.Authorized;
        }
    }

    /// <summary>
    /// Dispatch-time currency re-check. Signature is fixed by the downstream
    /// runtime file. Identity equality is enforced only for leases that were
    /// actually confirmed by OnChildSelect; an unconfirmed lease stays valid
    /// as long as the live scene still equals the captured scene.
    /// </summary>
    public static bool IsCurrent(
        EmbeddedEditorPreviewLease lease,
        PlayerRuntimeContextSnapshot context,
        EditorSceneIdentitySnapshot? currentIdentity,
        ApiResult<SceneSnapshot> currentScene,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(currentScene);
        lock (Gate)
        {
            if (context.Mode != PlayerRuntimeMode.EditorPreview
                || !context.PlayerAvailable
                || !context.PreviewMode)
            {
                error = "player left EditorPreview mode";
                return false;
            }

            if (!currentScene.Success
                || currentScene.Value == null
                || !SameScene(lease.Scene, currentScene.Value.Address))
            {
                error = "editor preview lease is no longer bound to the current selection";
                return false;
            }

            if (lease.ConfirmedObservationSequence > 0)
            {
                if (currentIdentity == null
                    || currentIdentity.ObservationSequence != lease.ConfirmedObservationSequence
                    || currentIdentity.SelectionRequestId != lease.DataListRequestId
                    || new CompiledScriptIdentity(
                        currentIdentity.CompiledScriptSha256,
                        currentIdentity.CompiledScriptLength,
                        currentIdentity.CompiledScriptLineCount) != lease.CompiledScript)
                {
                    error = "editor preview lease is no longer bound to the current selection";
                    return false;
                }
            }
            else if (currentIdentity != null
                && currentIdentity.SelectionRequestId == lease.DataListRequestId
                && new CompiledScriptIdentity(
                    currentIdentity.CompiledScriptSha256,
                    currentIdentity.CompiledScriptLength,
                    currentIdentity.CompiledScriptLineCount) != lease.CompiledScript)
            {
                error = "editor preview lease is no longer bound to the current selection";
                return false;
            }

            error = string.Empty;
            return true;
        }
    }

    /// <summary>
    /// Rotation fan-out: the effective live project key changed, so every
    /// overlay entry, pending capture, and cached embedded-directive identity
    /// is stale. Wipes everything; idempotent because callers fire only on the
    /// rotation edge.
    /// </summary>
    public static void OnProjectKeyRotated(string reason, string effectiveKey16)
    {
        lock (Gate)
        {
            Overlays.Clear();
            _overlayProjectKey = string.Empty;
            CloseAllGenerationsNoLock();
        }

        EmbeddedEditorDirectiveCache.Clear("project-key-rotated");
        Plugin.Logger.LogInfo(
            $"editor-project-key event=ROTATE key16={effectiveKey16}; "
            + $"reason={reason}; hasActivePair={ActiveProjectPairSource.HasActivePair}");
    }

    public static void OnDirectiveRegistryChanged()
    {
        lock (Gate)
        {
            Overlays.Clear();
            CloseAllGenerationsNoLock();
        }
        EmbeddedEditorDirectiveCache.Clear("directive-registry-changed");
    }

    /// <summary>FIX D surface: inert declaration for the next-stage integrator.</summary>
    public static bool TryGetCurrentGeneration(out DataListGenerationScope scope)
    {
        lock (Gate)
        {
            PreviewGeneration? generation = Generations.LastOrDefault();
            if (generation == null)
            {
                scope = default;
                return false;
            }

            scope = new DataListGenerationScope(
                generation.GenerationId,
                generation.MinimumWindowSequenceExclusive,
                generation.Phase == PreviewGenerationPhase.Collecting,
                _chainAppliedGenerations.Contains(generation.GenerationId));
            return true;
        }
    }

    /// <summary>
    /// FIX D surface: atomic first-wins chain-applied marker. With a window
    /// sequence the marker is per (generation, window) so every genuine replay
    /// rebuilds its chain; without one it stays per-generation for chain-only
    /// coverage.
    /// </summary>
    public static bool TryMarkGenerationChainApplied(long generationId, long windowSequence = 0)
    {
        lock (Gate)
        {
            if (!Generations.Any(item => item.GenerationId == generationId))
            {
                return false;
            }

            return windowSequence == 0
                ? _chainAppliedGenerations.Add(generationId)
                : _chainAppliedWindowSequences.Add((generationId, windowSequence));
        }
    }

    private static readonly HashSet<long> _chainAppliedGenerations = new();
    private static readonly HashSet<(long GenerationId, long WindowSequence)> _chainAppliedWindowSequences = new();

    public static bool TryGetOverlayDirective(
        string projectKey,
        string nodeGuid,
        int sceneIndex,
        int publicSlot,
        out bool sceneIsAuthoritative,
        out string? canonicalDirective)
    {
        lock (Gate)
        {
            sceneIsAuthoritative = false;
            canonicalDirective = null;
            if (!string.Equals(_overlayProjectKey, projectKey, StringComparison.Ordinal)
                || !Overlays.TryGetValue(
                    new OverlaySceneKey(nodeGuid, sceneIndex),
                    out SceneOverlay? overlay))
            {
                return false;
            }

            sceneIsAuthoritative = true;
            overlay.CharacterDirectives.TryGetValue(publicSlot, out canonicalDirective);
            return canonicalDirective != null;
        }
    }

    private static void StoreCaptureCandidate(
        CompiledScriptIdentity identity,
        IReadOnlyList<string> directives,
        bool hasContinueDirective,
        string validationError,
        bool isTombstone)
    {
        lock (Gate)
        {
            PreviewGeneration? generation = Generations.LastOrDefault(item => item.Open);
            if (generation == null)
            {
                return;
            }

            var candidate = new CaptureCandidate(
                identity,
                Array.AsReadOnly(directives.ToArray()),
                hasContinueDirective,
                validationError,
                isTombstone,
                Conflict: false);
            if (generation.Candidates.TryGetValue(identity, out CaptureCandidate? existing))
            {
                bool same = existing.HasContinueDirective == candidate.HasContinueDirective
                    && existing.IsTombstone == candidate.IsTombstone
                    && string.Equals(
                        existing.ValidationError,
                        candidate.ValidationError,
                        StringComparison.Ordinal)
                    && existing.CanonicalDirectives.SequenceEqual(
                        candidate.CanonicalDirectives,
                        StringComparer.Ordinal);
                if (!same)
                {
                    generation.Candidates[identity] = existing with { Conflict = true };
                }
            }
            else
            {
                generation.Candidates.Add(identity, candidate);
                if (generation.Candidates.Count > MaximumCandidatesPerGeneration)
                {
                    SetPhaseNoLock(generation, PreviewGenerationPhase.Closed);
                    PlayerCommandObservationLog.Append(
                        $"{DateTimeOffset.Now:O} editor-preview-lease event=GENERATION_INVALID; "
                        + $"generation={generation.GenerationId}; "
                        + "reason=candidate-cap-exceeded");
                }
            }
        }
    }

    private static void SetPhaseNoLock(
        PreviewGeneration generation,
        PreviewGenerationPhase phase)
    {
        int index = Generations.IndexOf(generation);
        if (index >= 0)
        {
            Generations[index] = generation with { Phase = phase };
        }
    }

    private static void CloseAllGenerationsNoLock()
    {
        for (int index = 0; index < Generations.Count; index++)
        {
            if (Generations[index].Open)
            {
                Generations[index] = Generations[index] with
                {
                    Phase = PreviewGenerationPhase.Closed
                };
            }
        }
    }

    private static void TrimGenerationsNoLock()
    {
        while (Generations.Count > MaximumRetainedGenerations)
        {
            Generations.RemoveAt(0);
        }
    }

    private static void EmitBindLine(
        PreviewGeneration generation,
        string matchedScriptSha,
        int commands,
        bool tombstone,
        bool? hasContinueDirective,
        bool accepted,
        string reason)
    {
        // Nullable: an ambiguous-intersection rejection has no single candidate
        // to read the flag from and must not claim a value it never observed.
        string continueField = hasContinueDirective switch
        {
            true => "True",
            false => "False",
            _ => "unknown"
        };
        PlayerCommandObservationLog.Append(
            $"{DateTimeOffset.Now:O} editor-preview-lease event=BIND; "
            + $"generation={generation.GenerationId}; "
            + $"observation={generation.ConfirmedObservationSequence}; "
            + $"request={generation.DataListRequestId}; "
            + $"scriptSha16={ShortSha(matchedScriptSha)}; "
            + $"commands={commands}; "
            + $"tombstone={tombstone}; "
            + $"continue={continueField}; accepted={accepted}; "
            + $"reason={Escape(reason)}");
    }

    private static void UpdateOverlay(
        SceneAddress address,
        CaptureCandidate candidate)
    {
        if (!string.Equals(_overlayProjectKey, address.ProjectKey, StringComparison.Ordinal))
        {
            _overlayProjectKey = address.ProjectKey;
            Overlays.Clear();
        }

        var directives = new Dictionary<int, string>();
        var characterParser =
            new AzureArchive.VideoTools.Core.Characters.CharacterTransformDirectiveParser();
        var pendingParser = new SlotPendingCommandFamilyCompiler();
        foreach (string directive in candidate.CanonicalDirectives)
        {
            if (directive.StartsWith(
                    "#char;",
                    StringComparison.Ordinal))
            {
                var parsed = characterParser.Parse(directive);
                if (parsed.Success && parsed.Value != null)
                {
                    directives[parsed.Value.PublicSlot] = directive;
                }
            }
            else if (directive.StartsWith(
                         SlotPendingCommandFamilyCompiler.CanonicalRootToken + ";",
                         StringComparison.Ordinal))
            {
                var parsed = pendingParser.ParseCanonical(directive);
                if (parsed.Success && parsed.Value != null)
                {
                    directives[parsed.Value.PublicSlot] = directive;
                }
            }
            else if (directive.StartsWith(
                         SceneCameraCommandFamilyCompiler.CanonicalRootToken + ";",
                         StringComparison.Ordinal))
            {
                // The scene camera is the singleton resource of its scene, so it
                // is stored under the reserved slot 0. The camera inheritance
                // chain reads it back from here, which makes an authorized scene
                // authoritative for its own camera exactly like it already is for
                // its character slots.
                directives[SceneCameraCommandFamilyCompiler.SingletonResourceSlot] = directive;
            }
        }

        Overlays[new OverlaySceneKey(address.NodeGuid, address.SceneIndex)] =
            new SceneOverlay(directives);
    }

    private static string StableSceneIdentity(SceneAddress scene)
        => EditorPreviewLeaseGate.StableSceneIdentity(new EditorPreviewSceneAddress(
            scene.ProjectKey,
            scene.NodeGuid,
            scene.SceneIndex,
            scene.Fingerprint));

    private static bool SameScene(SceneAddress left, SceneAddress right) =>
        string.Equals(left.ProjectKey, right.ProjectKey, StringComparison.Ordinal)
        && string.Equals(left.NodeGuid, right.NodeGuid, StringComparison.Ordinal)
        && left.SceneIndex == right.SceneIndex
        && string.Equals(left.Fingerprint, right.Fingerprint, StringComparison.OrdinalIgnoreCase);

    private static bool IsCanonicalAddress(SceneAddress address) =>
        IsCanonical24Hex(address.ProjectKey)
        && Guid.TryParseExact(address.NodeGuid, "D", out _)
        && address.SceneIndex >= 0
        && IsCanonical24Hex(address.Fingerprint);

    private static bool IsCanonical24Hex(string value)
    {
        if (value == null || value.Length != 24)
        {
            return false;
        }

        for (int index = 0; index < value.Length; index++)
        {
            if (!Uri.IsHexDigit(value[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static string ShortSha(string value) =>
        value.Length <= 16 ? value : value[..16];

    private static string Escape(string value) =>
        value.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    internal sealed record CaptureCandidate(
        CompiledScriptIdentity CompiledScript,
        IReadOnlyList<string> CanonicalDirectives,
        bool HasContinueDirective,
        string ValidationError,
        bool IsTombstone,
        bool Conflict);

    private readonly record struct OverlaySceneKey(string NodeGuid, int SceneIndex);

    private sealed record SceneOverlay(IReadOnlyDictionary<int, string> CharacterDirectives);
}
