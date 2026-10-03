using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AzureArchive.VideoTools.Api;
using AzureArchive.VideoTools.Core.Characters;
using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Mapping;
using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Core.Results;
using AzureArchive.VideoTools.Core.Spines;
using AzureArchive.VideoTools.Core.Workspaces;
using AzureArchive.VideoTools.Formats.Aap;
using AzureArchive.VideoTools.Formats.Aas;
using AzureArchive.VideoTools.Formats.Workspace;
using AzureArchive.VideoTools.Interop;
using Rukari.Lib;
using Rukari.Lib.Commands;
using Rukari.Lib.Spines;
using EditorSceneSnapshot = AzureArchive.VideoTools.Api.SceneSnapshot;

namespace AzureArchive.VideoTools.Runtime;

internal static class PlayerCommandObservationRuntime
{
    private const int MaximumEventsPerUpdate = 128;
    private const int MaximumQueuedPrefixOnlyEvents = 4096;
    private const int MaximumPendingEmbeddedPreviews = 8;
    private const int EmbeddedPreviewRetryUpdates = 90;
    private const int IndexLoadRetryCooldownUpdates = 300;
    private const int ArchiveChangePollIntervalUpdates = 60;
    private const string PublishPendingNoticeText =
        "AAVT：已保存但尚未构建，播放指令暂不生效；构建后自动恢复。";
    private static readonly ConcurrentQueue<long> PrefixOnlyEvents = new();
    private static readonly Queue<DeferredCommandBatch> DeferredBatches = new();
    private static readonly Queue<DeferredEditorPreview> DeferredEditorPreviews = new();
    private static readonly Queue<PendingEmbeddedPreview> PendingEmbeddedPreviews = new();
    private static readonly IPlaybackCommandWindowObserver WindowObserver =
        new PlaybackCommandWindowObserver();
    private static readonly CharacterTransformDirectiveParser CharacterParser = new();
    private static readonly EditorPreviewLeaseGate EditorPreviewGate = new();
    private static PlayerCommandObservationOptions? _options;
    private static Task<ObservationIndexLoadResult>? _indexLoadTask;
    private static IPlaybackCommandIndex? _index;
    private static bool _indexLoadConsumed;
    private static long _lastFailedIndexLoadUpdate;
    private static string? _lastFailedIndexLoadSignature;
    // Publish-pending state (2026-09-18). The guard chain rewrites the .aas
    // ~100-400 ms (up to 2.5 s) after a save, so the first
    // embedded-commands-invalid failure following a save describes the normal
    // mid-cycle state of the pair rather than a defect. Two fields keep that
    // window honest:
    //
    //  _publishPendingSignature — the pair state that was already tried and
    //    found unindexable, so the loader does not re-attempt it on every
    //    managed update; the archive-change poll clears it when the disk moves,
    //    because that publish is the recovery event.
    //
    //  _publishGraceUntilTicks — the deadline of the ONE window this load cycle
    //    is given. It is opened at the first such failure and never moved
    //    afterwards, and only a successful load clears it, so a genuinely
    //    unmappable archive is reported as soon as the window closes even if
    //    the user keeps saving.
    private static string? _publishPendingSignature;
    private static long _publishGraceUntilTicks;
    private static string? _loadedArchiveSignature;
    private static string? _loggedArchiveChangeSignature;
    private static Task<PreviewChainSourceLoadResult>? _chainSourceLoadTask;
    private static PreviewChainSource? _chainSource;
    private static bool _chainSourceConsumed;
    // C feature (true WYSIWYG): live editor graph as the preferred chain
    // source for editor-preview resolutions. Never consulted for formal
    // playback; any build failure leaves the disk source in charge.
    private static PreviewChainSource? _liveChainSource;
    private static long _liveChainLastBuildTick = long.MinValue / 2;
    private static long _directiveRegistryRevision = -1;
    private static bool _liveChainWasAvailable;
    // FIX D bookkeeping, scoped per DataList generation instead of the editor
    // observation sequence (which repeated green previews without
    // OnChildSelect never advance). The lease reservation records the
    // generation whose chain coverage an authorized-but-not-yet-dispatched
    // lease owns; the enqueue marker records the generation that already has
    // a chain-only pending. Generation ids themselves are marked applied via
    // EmbeddedEditorPreviewLeaseCache.TryMarkGenerationChainApplied.
    private static long _leaseReservedChainGenerationId;
    private static long _chainOnlyEnqueuedGenerationId;
    // Same-drain dedup for the superseded-window admission: the last
    // (managed update, DataList generation) pair that authorized a lease. One
    // cascade may carry the same compiled identity in more than one window, so
    // a second authorization inside the same update is dropped. Green replays
    // arrive in a later update with the same generation and stay authorized.
    private static long _lastAuthorizedLeaseUpdate = -1;
    private static long _lastAuthorizedLeaseGeneration = -1;
    // Main-thread stall detector (2026-09-19): gaps between consecutive managed updates, and how
    // much of the previous gap our own update already accounts for.
    private static long _lastUpdateTimestamp;
    private static long _previousUpdateDurationMs;
    private static string _previousUpdateStage = "none";
    private static string _updateStage = "none";
    private static long _worstStallMilliseconds;
    private static readonly object _logOnceGate = new();
    private static readonly HashSet<string> _logOnceKeys = new(StringComparer.Ordinal);
    private static bool _firstUpdate = true;
    private static bool _logFailureReported;
    private static long _updateSequence;
    private static int _queuedPrefixOnlyEvents;
    private static int _droppedPrefixOnlyEvents;
    private static int _indexReloadRequested;
    private static PlaybackDispatchCanaryGate? _dispatchCanaryGate;
    private static PlaybackSceneDispatchGate? _sceneDispatchGate;
    private static volatile bool _embeddedEditorCommandsRuntimeReady;
    private static ActiveEditorPreview? _activeEditorPreview;
    private static PlayerRuntimeMode _lastObservedPlayerMode = PlayerRuntimeMode.Unavailable;
    private static long _lastPresetSelectionGeneration;

    internal static void StopCharacterPresets(string reason) =>
        Plugin.Host.CharacterPresetsInternal.StopAll(reason);

    public static bool Initialize(PlayerCommandObservationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            _options = Normalize(options);
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError(
                $"Player command observation configuration rejected: {ex.GetType().Name}: {ex.Message}");
            return false;
        }

        if (!Enum.IsDefined(typeof(PlayerCommandObservationStage), _options.Stage))
        {
            Plugin.Logger.LogError(
                $"Player command observation stage is invalid: {(int)_options.Stage}.");
            return false;
        }

        if (_options.Stage == PlayerCommandObservationStage.Disabled)
        {
            Plugin.Logger.LogInfo(
                "Player command observation disabled; no player Harmony patch or listener installed.");
            return false;
        }

        if (IsDispatchCanaryStage(_options.Stage))
        {
            int requiredMaximum =
                _options.Stage == PlayerCommandObservationStage.DispatchCanary
                    ? 1
                    : 2;
            if (_options.DispatchCanaryMaximumExecutions != requiredMaximum)
            {
                Plugin.Logger.LogError(
                    "Player dispatch canary configuration rejected: "
                    + $"stage {_options.Stage} requires "
                    + $"DispatchCanaryMaximumExecutions={requiredMaximum}.");
                return false;
            }

            Result<PlaybackDispatchCanaryGate> canary =
                PlaybackDispatchCanaryGate.Create(
                    new PlaybackDispatchCanaryPolicy(
                        _options.DispatchCanaryEnabled,
                        _options.DispatchCanaryRecordIndex,
                        _options.DispatchCanaryCommandId,
                        _options.DispatchCanaryDirective,
                        _options.DispatchCanaryMaximumExecutions));
            if (!canary.Success || canary.Value == null)
            {
                Plugin.Logger.LogError(
                    $"Player dispatch canary configuration rejected: {canary.Error}");
                return false;
            }

            _dispatchCanaryGate = canary.Value;
        }

        if (_options.Stage == PlayerCommandObservationStage.SceneCommandDispatch)
        {
            if (!_options.SceneCommandDispatchEnabled)
            {
                Plugin.Logger.LogError(
                    "Scene command dispatch requires SceneCommandDispatchEnabled=true.");
                return false;
            }

            _sceneDispatchGate = new PlaybackSceneDispatchGate();
        }

        string logPath;
        try
        {
            logPath = PlayerCommandObservationLog.Initialize(_options);
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError(
                $"Player command observation log could not be initialized: {ex.GetType().Name}: {ex.Message}");
            return false;
        }

        bool installed = PlayerAdvanceScenarioObservationPatch.Install(_options.Stage);
        if (!installed)
        {
            PlayerCommandObservationLog.Append(
                $"{Stamp()} startup-failed reason=patch-installation");
            return false;
        }

        if (_options.Stage >= PlayerCommandObservationStage.WindowCapture)
        {
            PlayerManagedUnityLogObservationProbe.Install();
        }

        Plugin.Logger.LogWarning(
            $"Player command observation candidate enabled: stage={_options.Stage}; "
            + $"log={logPath}; "
            + $"observationOnly={!IsRealDispatchStage(_options.Stage)}; "
            + $"dispatcherCalls={(_options.Stage == PlayerCommandObservationStage.SceneCommandDispatch ? "validated-scene-window" : IsDispatchCanaryStage(_options.Stage) ? "guarded-canary" : "false")}.");
        return true;
    }

    public static void SetEmbeddedEditorCommandsRuntimeReady(bool ready)
    {
        _embeddedEditorCommandsRuntimeReady = ready;
        Plugin.Logger.LogInfo(
            $"Embedded Environment command runtime ready={ready}; "
            + "AAP projection loading follows the ScriptKr sanitizer state.");
    }

    public static void ObservePrefixOnly(long sequence)
    {
        int queued = System.Threading.Interlocked.Increment(
            ref _queuedPrefixOnlyEvents);
        if (queued > MaximumQueuedPrefixOnlyEvents)
        {
            System.Threading.Interlocked.Decrement(ref _queuedPrefixOnlyEvents);
            System.Threading.Interlocked.Increment(ref _droppedPrefixOnlyEvents);
            return;
        }

        PrefixOnlyEvents.Enqueue(sequence);
    }

    public static bool RequestIndexReload()
    {
        PlayerCommandObservationOptions? options = _options;
        if (options == null
            || options.Stage < PlayerCommandObservationStage.IdentityOnly)
        {
            return false;
        }

        System.Threading.Interlocked.Exchange(ref _indexReloadRequested, 1);
        return true;
    }

    /// <summary>
    /// Watches the active pair's AAP/AAS files for on-disk changes (project
    /// save, playback re-record) and requests an automatic index reload so
    /// newly authored content works without a game restart. The signature
    /// baseline is captured at every settled index load — success, publish
    /// pending and failure alike; the stat poll is throttled to once per
    /// ArchiveChangePollIntervalUpdates updates.
    /// <para>
    /// The guard is the baseline's presence, not the index's: a publish-pending
    /// or failed pair has no index by design, and the rewrite of the <c>.aas</c>
    /// is the only thing that can change the verdict. Because the baseline moves
    /// with every attempt, "baseline exists" also guarantees the poll can only
    /// fire on a real change — never as a busy retry loop. A load that failed
    /// before the pair could even be described leaves no baseline and keeps the
    /// poll out of the picture, exactly as it was before this change.
    /// </para>
    /// </summary>
    private static void PollArchiveChanges(
        PlayerCommandObservationStage stage,
        long update)
    {
        if (stage < PlayerCommandObservationStage.IdentityOnly
            || update % ArchiveChangePollIntervalUpdates != 0
            || _loadedArchiveSignature == null)
        {
            return;
        }

        DiscoveredProjectPair? pair = ActiveProjectPairSource.ActivePairSnapshot();
        if (pair == null)
        {
            return;
        }

        string signature = DescribeArchiveSignature(pair.AapPath, pair.AasPath);
        if (string.Equals(signature, _loadedArchiveSignature, StringComparison.Ordinal))
        {
            return;
        }

        if (!string.Equals(
                signature,
                _loggedArchiveChangeSignature,
                StringComparison.Ordinal))
        {
            _loggedArchiveChangeSignature = signature;
            Plugin.Logger.LogInfo(
                "Player command archive changed on disk; requesting automatic index reload.");
            PlayerCommandObservationLog.Append(
                $"{Stamp()} archive-change detected=true; requesting index reload; "
                + $"signature={signature}");
        }

        EmitPublishReceipt(pair);
        RequestIndexReload();
    }

    /// <summary>
    /// One receipt line per artifact of the save → compile → publish chain
    /// (2026-09-18). The times come from the file mtimes, so they are exact no
    /// matter how coarse this poll is; the single poll-dependent number is the
    /// reported poll lag, which is precisely what this cadence costs. Log only:
    /// nothing here changes behaviour.
    /// </summary>
    private static void EmitPublishReceipt(DiscoveredProjectPair pair)
    {
        long nowTicks = DateTime.UtcNow.Ticks;
        DateTime aapWritten = FileWriteTimeUtc(pair.AapPath);
        (string Name, DateTime Written) build = NewestBuildDirectory(pair.AapPath);
        DateTime aasWritten = FileWriteTimeUtc(pair.AasPath);
        _receiptPublishDetectedTicks = nowTicks;

        PlayerCommandObservationLog.Append(
            $"{Stamp()} publish-receipt stage=AAP; file={Escape(Path.GetFileName(pair.AapPath))}; "
            + $"written={FormatTime(aapWritten)}; "
            + $"pollLagMs={LagMilliseconds(nowTicks, aapWritten)}");

        DateTime previous = aapWritten;
        if (build.Written != DateTime.MinValue)
        {
            PlayerCommandObservationLog.Append(
                $"{Stamp()} publish-receipt stage=BUILD; dir={Escape(build.Name)}; "
                + $"written={FormatTime(build.Written)}; "
                + $"sincePreviousMs={DeltaMilliseconds(build.Written, previous)}");
            previous = build.Written;
        }

        if (aasWritten != DateTime.MinValue)
        {
            PlayerCommandObservationLog.Append(
                $"{Stamp()} publish-receipt stage=PUBLISH; file={Escape(Path.GetFileName(pair.AasPath))}; "
                + $"written={FormatTime(aasWritten)}; "
                + $"sincePreviousMs={DeltaMilliseconds(aasWritten, previous)}; "
                + $"sinceAapMs={DeltaMilliseconds(aasWritten, aapWritten)}; "
                + $"pollLagMs={LagMilliseconds(nowTicks, aasWritten)}");
        }
    }

    private static long _receiptPublishDetectedTicks;

    private static string FormatTime(DateTime value) =>
        value == DateTime.MinValue ? "missing" : value.ToString("O", CultureInfo.InvariantCulture);

    private static long DeltaMilliseconds(DateTime later, DateTime earlier) =>
        earlier == DateTime.MinValue || later == DateTime.MinValue
            ? -1
            : (long)Math.Round((later - earlier).TotalMilliseconds);

    private static long LagMilliseconds(long nowTicks, DateTime written) =>
        written == DateTime.MinValue
            ? -1
            : (nowTicks - written.Ticks) / TimeSpan.TicksPerMillisecond;

    private static long SinceMilliseconds(long sinceTicks) =>
        sinceTicks <= 0
            ? -1
            : (DateTime.UtcNow.Ticks - sinceTicks) / TimeSpan.TicksPerMillisecond;

    /// <summary>Milliseconds since a <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/>
    /// reading, for main-thread costs that must be attributable to one call.</summary>
    private static long ElapsedMilliseconds(long startedTimestamp) =>
        (System.Diagnostics.Stopwatch.GetTimestamp() - startedTimestamp) * 1000L
        / System.Diagnostics.Stopwatch.Frequency;

    private static DateTime FileWriteTimeUtc(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue;
        }
        catch (Exception)
        {
            return DateTime.MinValue;
        }
    }

    private static (string Name, DateTime Written) NewestBuildDirectory(string aapPath)
    {
        try
        {
            string builds = Path.ChangeExtension(aapPath, ".builds");
            if (!Directory.Exists(builds))
            {
                return (string.Empty, DateTime.MinValue);
            }

            DirectoryInfo? newest = new DirectoryInfo(builds)
                .EnumerateDirectories()
                .OrderByDescending(directory => directory.LastWriteTimeUtc)
                .FirstOrDefault();
            return newest == null
                ? (string.Empty, DateTime.MinValue)
                : (newest.Name, newest.LastWriteTimeUtc);
        }
        catch (Exception)
        {
            return (string.Empty, DateTime.MinValue);
        }
    }

    private static string DescribeArchiveSignature(string aapPath, string aasPath) =>
        $"{DescribeFileSignature(aapPath)}|{DescribeFileSignature(aasPath)}";

    /// <summary>
    /// Main-thread stall detector (2026-09-19). The user reports the game going unresponsive,
    /// concentrated around opening a project, and nothing measured which call was holding the
    /// thread. The wall-clock gap between consecutive managed updates is attribution-free — a
    /// block from native project loading, the garbage collector, another mod or our own code all
    /// become the same number — and <c>ours</c> then splits the blame by asking how much of that
    /// gap the previous update itself took. Rule: <see cref="MainThreadStallPolicy"/>.
    /// </summary>
    private static void ReportMainThreadStall(long updateStartedTimestamp)
    {
        long previous = _lastUpdateTimestamp;
        _lastUpdateTimestamp = updateStartedTimestamp;
        if (previous == 0)
        {
            return;
        }

        long gap = (updateStartedTimestamp - previous) * 1000L / System.Diagnostics.Stopwatch.Frequency;
        if (!MainThreadStallPolicy.ShouldReport(gap, _worstStallMilliseconds))
        {
            return;
        }

        if (gap > _worstStallMilliseconds)
        {
            _worstStallMilliseconds = gap;
        }

        PlayerCommandObservationLog.Append(
            $"{Stamp()} main-thread stall={gap}ms; previousUpdateMs={_previousUpdateDurationMs}; "
            + $"previousStage={_previousUpdateStage}; update={_updateSequence}; "
            + $"worstMs={_worstStallMilliseconds}; "
            + $"ours={MainThreadStallPolicy.IsOurUpdateDominant(_previousUpdateDurationMs, gap)}");
    }

    private static string DescribeFileSignature(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists
                ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}"
                : "missing";
        }
        catch (Exception ex)
        {
            return $"unreadable:{ex.GetType().Name}";
        }
    }

    /// <summary>
    /// Logs when the managed heartbeat starts or stops being called at all, so a gap that the
    /// stall detector reports can never be misread as a blocked thread when the truth is that
    /// this behaviour was inactive. Rare by construction.
    /// </summary>
    public static void ReportHeartbeatAvailability(bool available)
    {
        PlayerCommandObservationLog.Append(
            $"{Stamp()} managed-heartbeat available={available}; update={_updateSequence}; "
            + $"worstStallMs={_worstStallMilliseconds}");
        if (available)
        {
            // The gap across an inactive period is not a stall; restart the measurement instead of
            // reporting the disabled time as one.
            _lastUpdateTimestamp = 0;
        }
        else
        {
            StopCharacterPresets("heartbeat-disabled");
        }
    }

    public static void Update()
    {
        try
        {
            // Remove our previous rendered overlay before official/Tween updates
            // and before command dispatch captures any authored baseline.
            Plugin.Host.CharacterPresetsInternal.Update();
            PlayerCommandObservationOptions? options = _options;
            if (options == null
                || options.Stage == PlayerCommandObservationStage.Disabled)
            {
                StopCharacterPresets("observation-disabled");
                return;
            }

            long updateStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            ReportMainThreadStall(updateStarted);
            long update = ++_updateSequence;
            if (_firstUpdate)
            {
                _firstUpdate = false;
                PlayerCommandObservationLog.Append(
                    $"{Stamp()} managed-update first=true; gameObjectAccess=false");
            }

            _updateStage = "editor-preview-mode";
            MaintainEditorPreviewMode(options.Stage);
            _updateStage = "reload-reset";
            RefreshDirectiveRegistry();
            ConsumeIndexReloadRequest(options.Stage, update);
            _updateStage = "archive-poll";
            PollArchiveChanges(options.Stage, update);
            _updateStage = "pending-embedded-previews";
            ManagedUnityLogSelectionProbe.PumpOnMainThread();
            DrainPendingEmbeddedPreviews(options.Stage, update);
            DrainPrefixOnlyEvents(options.Stage);
            _updateStage = "index-load";
            StartOrConsumeIndexLoad(options, update);
            StartOrConsumeChainSourceLoad(options);
            _updateStage = "live-chain";
            RefreshLiveChainSource(options);
            _updateStage = "window-drain";
            DrainClosedWindows(options.Stage, update);
            ScanSlotPendings(options);
            _updateStage = "editor-preview-dispatch";
            DrainDeferredEditorPreviews(
                options.Stage,
                update,
                ManagedDispatchPhase.Update);
            _updateStage = "batch-dispatch";
            DrainDeferredBatches(
                options.Stage,
                update,
                ManagedDispatchPhase.Update);
            // Recorded before the final tick so the reported duration and the reported stage
            // describe exactly the same piece of work.
            _previousUpdateDurationMs = ElapsedMilliseconds(updateStarted);
            _previousUpdateStage = _updateStage;
            _updateStage = "continue-dialogue";
            ContinueDialogueRuntime.UpdateTick();
        }
        catch (Exception ex)
        {
            _previousUpdateDurationMs = 0;
            _previousUpdateStage = _updateStage + "-threw";
            if (_logFailureReported)
            {
                return;
            }

            _logFailureReported = true;
            Plugin.Logger.LogError(
                $"Player command observation Update failed: {ex.GetType().Name}: {ex.Message}");
            PlayerCommandObservationLog.Append(
                $"{Stamp()} update-failed type={ex.GetType().Name}; message={Escape(ex.Message)}");
        }
    }

    public static void LateUpdate()
    {
        try
        {
            PlayerCommandObservationOptions? options = _options;
            long update = _updateSequence;
            if (options == null
                || options.Stage == PlayerCommandObservationStage.Disabled
                || update <= 0)
            {
                StopCharacterPresets("observation-disabled");
                ContinueDialogueRuntime.UpdateTick();
                return;
            }

            // AdvanceScenario and editor selection may run after this
            // behaviour's Update. Drain their managed observations here,
            // then settle pending/inherited state before Unity renders.
            ManagedUnityLogSelectionProbe.PumpOnMainThread();
            DrainClosedWindows(options.Stage, update);
            DrainPendingEmbeddedPreviews(options.Stage, update);
            ScanSlotPendings(options);
            MaintainEditorPreviewMode(options.Stage);
            DrainDeferredEditorPreviews(
                options.Stage,
                update,
                ManagedDispatchPhase.LateUpdate);
            DrainDeferredBatches(
                options.Stage,
                update,
                ManagedDispatchPhase.LateUpdate);
            Plugin.Host.CharacterPresetsInternal.LateUpdate();
            ContinueDialogueRuntime.UpdateTick();
        }
        catch (Exception ex)
        {
            if (_logFailureReported)
            {
                return;
            }

            _logFailureReported = true;
            Plugin.Logger.LogError(
                $"Player command observation LateUpdate failed: {ex.GetType().Name}: {ex.Message}");
            PlayerCommandObservationLog.Append(
                $"{Stamp()} late-update-failed type={ex.GetType().Name}; message={Escape(ex.Message)}");
        }
    }

    private static void ConsumeIndexReloadRequest(
        PlayerCommandObservationStage stage,
        long update)
    {
        if (System.Threading.Interlocked.Exchange(ref _indexReloadRequested, 0) == 0)
        {
            return;
        }

        int discardedBatches = DeferredBatches.Count;
        int discardedEditorPreviews = DeferredEditorPreviews.Count;
        DeferredBatches.Clear();
        DeferredEditorPreviews.Clear();
        PendingEmbeddedPreviews.Clear();
        _index = null;
        _indexLoadTask = null;
        _indexLoadConsumed = false;
        _lastFailedIndexLoadUpdate = 0;
        _lastFailedIndexLoadSignature = null;
        _publishPendingSignature = null;
        EditorNotifications.ClearPlaybackNotice();
        _chainSource = null;
        _chainSourceLoadTask = null;
        _chainSourceConsumed = false;
        _liveChainSource = null;
        _liveChainLastBuildTick = long.MinValue / 2;
        LiveProjectGraphService.Invalidate("index-reload");
        // FIX D: reset the file-local generation reservations. Applied marks
        // held inside EmbeddedEditorPreviewLeaseCache survive a reload by
        // design; the next green preview click opens a new generation id that
        // reopens chain application regardless.
        _leaseReservedChainGenerationId = 0;
        _chainOnlyEnqueuedGenerationId = 0;
        _lastAuthorizedLeaseUpdate = -1;
        _lastAuthorizedLeaseGeneration = -1;
        ApiResult<bool> pendingsCleared =
            Plugin.Api.CharacterCommands.ClearSlotPendings();
        PlayerCommandObservationLog.Append(
            $"{Stamp()} index-reload accepted=true; stage={stage}; update={update}; "
            + $"discardedDeferredBatches={discardedBatches}; oldIndexExecutable=false; "
            + $"discardedEditorPreviews={discardedEditorPreviews}; "
            + "previewChainSourceDiscarded=true; "
            + $"slotPendingsCleared={pendingsCleared.Success}");
        Plugin.Logger.LogInfo(
            "Player command index reload accepted; the old index and deferred batches were discarded.");
    }

    private static void RefreshDirectiveRegistry()
    {
        var service = ModServices.Current?.GetService<IEmbeddedDirectiveService>();
        long revision = service is { Success: true, Value: not null } ? service.Value.Revision : -1;
        if (revision == _directiveRegistryRevision) return;
        bool hadRevision = _directiveRegistryRevision >= 0;
        _directiveRegistryRevision = revision;
        if (!hadRevision) return;
        EmbeddedEditorPreviewLeaseCache.OnDirectiveRegistryChanged();
        ContinueDialogueRuntime.ReplaceContinueIdentities(Array.Empty<string>(), "directive-registry-changed");
        RequestIndexReload();
    }

    private static Func<string, string> CaptureOwnedPromptProjection()
    {
        var service = ModServices.Current?.GetService<IEmbeddedDirectiveService>();
        if (service is not { Success: true, Value: not null }) return static text => text;
        IEmbeddedDirectiveSanitizer snapshot = service.Value.CaptureSanitizer();
        string[] owners = { Plugin.Guid, "rukari.charactervoice" };
        return text => snapshot.SanitizeExceptOwners(text, owners);
    }

    private static void DrainPrefixOnlyEvents(PlayerCommandObservationStage stage)
    {
        if (stage != PlayerCommandObservationStage.PrefixOnly)
        {
            return;
        }

        var lines = new List<string>();
        int dropped = System.Threading.Interlocked.Exchange(
            ref _droppedPrefixOnlyEvents,
            0);
        if (dropped != 0)
        {
            lines.Add($"{Stamp()} prefix-queue-overflow droppedEvents={dropped}");
        }

        while (lines.Count < MaximumEventsPerUpdate
            && PrefixOnlyEvents.TryDequeue(out long sequence))
        {
            System.Threading.Interlocked.Decrement(ref _queuedPrefixOnlyEvents);
            lines.Add(
                $"{Stamp()} advance-prefix sequence={sequence}; callbackArguments=zero; gameObjectAccess=false");
        }

        if (lines.Count == 0)
        {
            return;
        }

        Plugin.Host.CapabilitiesInternal.Verified(
            "Player.AdvanceObservationPrefix",
            "zero-argument Test.AdvanceScenario Prefix reached managed Update");
        PlayerCommandObservationLog.Append(lines);
    }

    private static void StartOrConsumeIndexLoad(
        PlayerCommandObservationOptions options,
        long update)
    {
        if (options.Stage < PlayerCommandObservationStage.IdentityOnly)
        {
            return;
        }

        if (_indexLoadTask == null)
        {
            if (ActiveProjectPairSource.Enabled
                && !ActiveProjectPairSource.HasActivePair)
            {
                LogOnce(
                    "index-load-waiting-auto-pair",
                    $"{Stamp()} index-load status=WAITING; reason=auto-discover-no-active-pair; "
                    + "the index loads after the first editor selection identifies the open project.");
                return;
            }

            if (_publishPendingSignature != null)
            {
                if (ProjectPublicationConsistency.IsInsideGrace(
                        _publishGraceUntilTicks,
                        DateTime.UtcNow.Ticks))
                {
                    // A save whose publish has not landed yet: this exact pair
                    // was already tried, so there is nothing new to learn until
                    // the compile/publish step rewrites the .aas. The
                    // archive-change poll re-arms the attempt at that moment.
                    return;
                }

                // The window closed with the pair unchanged: let one attempt
                // through, which either indexes it or reports the failure the
                // user needs to see instead of waiting for another save.
                _publishPendingSignature = null;
            }

            if (_lastFailedIndexLoadSignature != null
                && update - _lastFailedIndexLoadUpdate < IndexLoadRetryCooldownUpdates)
            {
                return;
            }

            PlayerCommandObservationOptions snapshot = options with { };
            bool retryAfterFailure = _lastFailedIndexLoadSignature != null;
            _indexLoadConsumed = false;
            _indexLoadTask = Task.Run(() => LoadIndex(snapshot));
            PlayerCommandObservationLog.Append(
                $"{Stamp()} index-load queued=true; worker=managed-background-read-only; stage={options.Stage}; "
                + $"retryAfterFailure={retryAfterFailure}");
            return;
        }

        if (_indexLoadConsumed || !_indexLoadTask.IsCompleted)
        {
            return;
        }

        _indexLoadConsumed = true;
        ObservationIndexLoadResult result;
        try
        {
            result = _indexLoadTask.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            result = ObservationIndexLoadResult.Fail(
                "index-worker-unhandled-exception",
                $"Unhandled index worker failure: {ex.GetType().Name}: {ex.Message}",
                DescribeArchiveContext(null, null));
        }

        if (ProjectPublicationConsistency.MayWaitForPublish(
                result.PublicationState,
                _publishGraceUntilTicks,
                DateTime.UtcNow.Ticks))
        {
            long nowTicks = DateTime.UtcNow.Ticks;
            _indexLoadTask = null;
            _indexLoadConsumed = false;
            _lastFailedIndexLoadUpdate = 0;
            _lastFailedIndexLoadSignature = null;
            // Baseline the poll on the signature the worker observed, not on
            // one taken now: a build landing mid-load may then only cost one
            // redundant attempt, never leave the pending state waiting for a
            // change that already happened.
            _publishPendingSignature = result.ArchiveSignature;
            _loadedArchiveSignature = result.ArchiveSignature;
            _loggedArchiveChangeSignature = result.ArchiveSignature;
            if (!ProjectPublicationConsistency.IsInsideGrace(_publishGraceUntilTicks, nowTicks))
            {
                // First such failure of this load cycle: open the one window.
                // A later failure inside it reuses the same deadline, so
                // repeated saves can never extend it.
                _publishGraceUntilTicks =
                    ProjectPublicationConsistency.GraceDeadlineTicks(nowTicks);
            }

            Plugin.Host.CapabilitiesInternal.Unavailable(
                "Player.CommandObservationIndex",
                $"publish-pending: {result.Diagnostic}");
            LogOnce(
                $"index-load-pending:{result.ArchiveSignature}",
                "Player command observation index is waiting for the build: the project "
                + "was saved after the last publish, so the playback archive is one "
                + "revision behind and cannot be indexed yet. Playback commands resume "
                + "automatically once the project is built; if it is not, the failure is "
                + "reported when the wait expires.");
            PlayerCommandObservationLog.Append(
                $"{Stamp()} index-load status=PENDING; reason=publish-pending; "
                + $"graceUntilMs={ProjectPublicationConsistency.GraceMilliseconds}; "
                + $"diagnostic={Escape(result.Diagnostic)}; "
                + $"signature={result.ArchiveSignature}; {result.ArchiveContext}");
            EditorNotifications.ReportPlaybackNotice(
                "publish-pending",
                PublishPendingNoticeText);
            return;
        }

        if (!result.Success || result.Index == null)
        {
            _lastFailedIndexLoadUpdate = update;
            _lastFailedIndexLoadSignature = $"{result.Reason}: {result.Diagnostic}";
            _indexLoadTask = null;
            _indexLoadConsumed = false;
            // Baseline the poll on the pair this attempt actually judged, so a
            // failure is only re-attempted when the archive really changes. An
            // empty signature means the failure happened before the pair could
            // be described: leave no baseline and let the retry cooldown govern,
            // as it always did.
            _loadedArchiveSignature = result.ArchiveSignature.Length == 0
                ? null
                : result.ArchiveSignature;
            Plugin.Host.CapabilitiesInternal.Degraded(
                "Player.CommandObservationIndex",
                $"{result.Reason}: {result.Diagnostic}");
            LogOnce(
                $"index-load-failed:{_lastFailedIndexLoadSignature}",
                $"Player command observation index unavailable (retry every "
                + $"{IndexLoadRetryCooldownUpdates} updates while the project files stay invalid): "
                + $"{result.Reason}: {result.Diagnostic}");
            // publishGraceOver records the case the user must act on: the failure
            // looks like a save without a build, but the wait for the publish has
            // already expired, so this is the real report rather than a
            // mid-cycle reading.
            string graceNote =
                result.PublicationState == ProjectPublicationState.AasStaleAfterSave
                    ? $"; publishGraceOver=true; aasOlderThanAap=true"
                    : string.Empty;
            PlayerCommandObservationLog.Append(
                $"{Stamp()} index-load status=FAILED; reason={result.Reason}; "
                + $"diagnostic={Escape(result.Diagnostic)}; {result.ArchiveContext}{graceNote}");
            _publishGraceUntilTicks = 0;
            EditorNotifications.ReportPlaybackError(
                "index-load",
                FriendlyIndexLoadToast(result.Reason, result.Diagnostic),
                $"reason={result.Reason}\n{result.Diagnostic}");
            return;
        }

        _index = result.Index;
        DiscoveredProjectPair? loadedPair = ActiveProjectPairSource.ActivePairSnapshot();
        // The baseline must describe the pair the index was actually built from,
        // so the signature the worker observed before its reads wins: a fresh
        // capture could hide a publish that landed mid-load and leave the index
        // one revision stale until the next save.
        _loadedArchiveSignature = result.ArchiveSignature.Length != 0
            ? result.ArchiveSignature
            : loadedPair == null
                ? null
                : DescribeArchiveSignature(loadedPair.AapPath, loadedPair.AasPath);
        PlayerCommandObservationLog.Append(
            $"{Stamp()} publish-receipt stage=INDEX; records={_index.RecordCount}; "
            + $"batches={_index.BatchCount}; "
            + $"sincePublishDetectedMs={SinceMilliseconds(_receiptPublishDetectedTicks)}");
        EditorNotifications.ClearPlaybackError();
        EditorNotifications.ClearPlaybackNotice();
        if (options.ContinueDialogueEnabled && result.ContinueIdentities != null)        {
            ContinueDialogueRuntime.ReplaceContinueIdentities(
                result.ContinueIdentities,
                "current-aap-aas-index");
        }
        _lastFailedIndexLoadUpdate = 0;
        _lastFailedIndexLoadSignature = null;
        _publishPendingSignature = null;
        // This load cycle is settled, so the next save may open a fresh window.
        _publishGraceUntilTicks = 0;
        Plugin.Host.CapabilitiesInternal.Verified(
            "Player.CommandObservationIndex",
            $"records={_index.RecordCount}; batches={_index.BatchCount}; stage={options.Stage}");
        Plugin.Logger.LogInfo(
            $"Player command observation index ready: records={_index.RecordCount}; "
            + $"batches={_index.BatchCount}; stage={options.Stage}; source=managed-read-only.");
        PlayerCommandObservationLog.Append(
            $"{Stamp()} index-load status=READY; records={_index.RecordCount}; "
            + $"batches={_index.BatchCount}; playbackSha16={_index.PlaybackRevisionSha256.Substring(0, 16)}; "
            + $"projectionLoaded={result.ProjectionLoaded}; workspaceSource={result.WorkspaceSource}; "
            + $"{result.ArchiveContext}");
    }

    /// <summary>Human-actionable toast for known index-load failures. The
    /// official-transition shape names the scene and slot, which is exactly
    /// what the user must act on; everything else keeps the raw first line.</summary>
    private static string FriendlyIndexLoadToast(string reason, string diagnostic)
    {
        if (string.Equals(reason, "embedded-commands-invalid", StringComparison.Ordinal))
        {
            System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(
                diagnostic,
                "Scene [0-9a-fA-F-]+:(?<scene>[0-9]+) has an official position transition for public slot (?<slot>[0-9]+)");
            if (match.Success)
            {
                return $"AAVT 播放停用：记录 {int.Parse(match.Groups["scene"].Value, System.Globalization.CultureInfo.InvariantCulture) + 1} "
                    + $"槽位 {match.Groups["slot"].Value} 有官方位移过渡——请移除该记录的 AAVT 位置/整体重置指令";
            }
        }

        return "AAVT 播放索引加载失败（全部播放指令停用）："
            + EditorNotifications.FirstLine(diagnostic);
    }

    private static void StartOrConsumeChainSourceLoad(
        PlayerCommandObservationOptions options)
    {
        if (options.Stage != PlayerCommandObservationStage.SceneCommandDispatch
            || !options.PreviewChainEnabled)
        {
            return;
        }

        if (_chainSourceLoadTask == null)
        {
            if (ActiveProjectPairSource.Enabled
                && !ActiveProjectPairSource.HasActivePair)
            {
                LogOnce(
                    "chain-source-load-waiting-auto-pair",
                    $"{Stamp()} chain-source-load status=WAITING; reason=auto-discover-no-active-pair");
                return;
            }

            PlayerCommandObservationOptions snapshot = options with { };
            _chainSourceLoadTask = Task.Run(() => LoadPreviewChainSource(snapshot));
            PlayerCommandObservationLog.Append(
                $"{Stamp()} chain-source-load queued=true; worker=managed-background-read-only");
            return;
        }

        if (_chainSourceConsumed || !_chainSourceLoadTask.IsCompleted)
        {
            return;
        }

        _chainSourceConsumed = true;
        PreviewChainSourceLoadResult result;
        try
        {
            result = _chainSourceLoadTask.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            result = PreviewChainSourceLoadResult.Fail(
                "chain-source-worker-unhandled-exception",
                $"Unhandled chain source worker failure: {ex.GetType().Name}: {ex.Message}");
        }

        if (!result.Success || result.Source == null)
        {
            Plugin.Logger.LogWarning(
                $"Preview chain source unavailable: {result.Reason}: {result.Diagnostic}");
            PlayerCommandObservationLog.Append(
                $"{Stamp()} chain-source-load status=FAILED; reason={result.Reason}; "
                + $"diagnostic={Escape(result.Diagnostic)}");
            return;
        }

        _chainSource = result.Source;
        Plugin.Logger.LogInfo(
            "Preview chain source ready: read-only AAP graph and directive index loaded.");
        PlayerCommandObservationLog.Append(
            $"{Stamp()} chain-source-load status=READY; "
            + $"aapRevision16={Short16(result.Source.Project.Source.RevisionSha256)}; "
            + $"directiveScenes={result.Source.Directives.SceneCount}; "
            + $"directives={result.Source.Directives.DirectiveCount}; "
            + $"skippedScenes={result.Source.Directives.SkippedSceneCount}");
    }

    /// <summary>
    /// C feature: rebuilds the live editor chain source when invalidated and
    /// the debounce window has elapsed. Runs on the managed main thread (the
    /// Update pump); any failure clears the live source so resolutions fall
    /// back to the disk graph. Formal playback never consults this source.
    /// </summary>
    private static void RefreshLiveChainSource(PlayerCommandObservationOptions options)
    {
        if (!options.PreviewChainEnabled || !options.LiveEditorChainEnabled)
        {
            return;
        }

        // Debounce gate runs BEFORE consuming the flag so an invalidation
        // that lands inside the window survives until the window elapses.
        long now = Environment.TickCount64;
        if (now - _liveChainLastBuildTick < LiveProjectGraphService.RebuildDebounceMilliseconds)
        {
            return;
        }

        if (!LiveProjectGraphService.ConsumeInvalidation())
        {
            return;
        }

        _liveChainLastBuildTick = now;
        // Timed (2026-09-19): this walks the live editor graph on the managed update
        // thread, and the walk it replaces is the one that runs when a project is first
        // opened — the moment the user reports the game going unresponsive.
        long liveGraphStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            if (!LiveProjectGraphService.TryBuildSnapshot(
                    out ProjectSnapshot liveProject,
                    out string error))
            {
                if (_liveChainWasAvailable || _liveChainSource != null)
                {
                    PlayerCommandObservationLog.Append(
                        $"{Stamp()} live-chain-source status=FALLBACK_TO_DISK; "
                        + $"reason={Escape(error)}");
                }

                _liveChainSource = null;
                _liveChainWasAvailable = false;
                return;
            }

            // Content-addressed short circuit: compiles fire constantly while
            // dialogue advances, so identical graph revisions keep the current
            // source and stay silent instead of churning every 500ms.
            if (_liveChainSource != null
                && string.Equals(
                    _liveChainSource.Project.Source.RevisionSha256,
                    liveProject.Source.RevisionSha256,
                    StringComparison.Ordinal))
            {
                return;
            }

            PreviewChainDirectiveIndex liveDirectives =
                PreviewChainDirectiveIndex.Build(
                    liveProject,
                    options.AcceptLegacyCharacterDirectiveAlias,
                    CaptureOwnedPromptProjection());
            _liveChainSource = new PreviewChainSource(
                liveProject,
                new PreviewChainResolver(liveProject),
                liveDirectives,
                new PreviewCameraChainResolver(liveProject));
            _liveChainWasAvailable = true;
            PlayerCommandObservationLog.Append(
                $"{Stamp()} live-chain-source status=READY; "
                + $"revision16={Short16(liveProject.Source.RevisionSha256)}; "
                + $"nodes={liveProject.Nodes.Count}; "
                + $"directiveScenes={liveDirectives.SceneCount}; "
                + $"directives={liveDirectives.DirectiveCount}; "
                + $"skippedScenes={liveDirectives.SkippedSceneCount}; "
                + $"elapsedMs={ElapsedMilliseconds(liveGraphStarted)}");
        }
        catch (Exception ex)
        {
            _liveChainSource = null;
            _liveChainWasAvailable = false;
            Plugin.Logger.LogWarning(
                $"Live chain source refresh failed: {ex.GetType().Name}: {ex.Message}");
            PlayerCommandObservationLog.Append(
                $"{Stamp()} live-chain-source status=FAILED; "
                + $"error={Escape(ex.Message)}");
        }
    }

    /// <summary>
    /// Chain source selector: editor-preview resolutions prefer the live
    /// graph when it is fresh; formal playback always receives the disk
    /// source (red line: playback executes only saved content).
    /// </summary>
    private static PreviewChainSource? EffectiveChainSource(bool preferLiveEditorGraph) =>
        preferLiveEditorGraph ? _liveChainSource ?? _chainSource : _chainSource;

    private static PreviewChainSourceLoadResult LoadPreviewChainSource(
        PlayerCommandObservationOptions options)
    {
        Result<string> projectPath = ResolveArchivePath(
            options.ProjectPath,
            ".aap",
            "project");
        if (!projectPath.Success || projectPath.Value == null)
        {
            return PreviewChainSourceLoadResult.Fail(
                "chain-project-path-invalid",
                projectPath.Error);
        }

        Result<ProjectSnapshot> projectRead =
            new AapProjectReader().Read(projectPath.Value);
        if (!projectRead.Success || projectRead.Value == null)
        {
            return PreviewChainSourceLoadResult.Fail(
                "chain-aap-read-failed",
                projectRead.Error);
        }

        ProjectSnapshot project = projectRead.Value;
        PreviewChainDirectiveIndex directives =
            PreviewChainDirectiveIndex.Build(
                project,
                options.AcceptLegacyCharacterDirectiveAlias,
                CaptureOwnedPromptProjection());
        return PreviewChainSourceLoadResult.Ready(new PreviewChainSource(
            project,
            new PreviewChainResolver(project),
            directives,
            new PreviewCameraChainResolver(project)));
    }

    private static void DrainClosedWindows(
        PlayerCommandObservationStage stage,
        long update)
    {
        if (stage < PlayerCommandObservationStage.WindowCapture)
        {
            return;
        }

        var lines = new List<string>();
        int dropped = PlayerAdvanceObservationWindow.TakeDroppedWindowCount();
        if (dropped != 0)
        {
            lines.Add($"{Stamp()} queue-overflow droppedWindows={dropped}");
        }

        int drained = 0;
        bool contextRead = false;
        ApiResult<PlayerRuntimeContextSnapshot>? contextResult = null;
        while (drained < MaximumEventsPerUpdate
            && PlayerAdvanceObservationWindow.TryDequeue(
                out ClosedAdvanceWindow? window))
        {
            drained++;
            if (window == null)
            {
                continue;
            }

            if (stage is PlayerCommandObservationStage.PlaybackContextProbe
                    or PlayerCommandObservationStage.SceneCommandDispatch
                && !contextRead)
            {
                contextRead = true;
                contextResult = Plugin.Api.PlayerContext.ReadOnMainThread();
            }

            ObserveWindow(stage, update, window, contextResult, lines);
        }

        if (lines.Count != 0)
        {
            PlayerCommandObservationLog.Append(lines);
        }
    }

    private static long _lastObservedWindowSequence;

    internal static long LastObservedWindowSequence =>
        System.Threading.Interlocked.Read(ref _lastObservedWindowSequence);

    internal static string CreateStamp() => Stamp();

    private static void TrackObservedWindowSequence(ClosedAdvanceWindow window)
    {
        if (window.Sequence > _lastObservedWindowSequence)
        {
            _lastObservedWindowSequence = window.Sequence;
        }
    }

    private static void ScanSlotPendings(PlayerCommandObservationOptions options)
    {
        if (options.Stage != PlayerCommandObservationStage.SceneCommandDispatch
            || !options.SlotPendingEnabled
            || _index == null)
        {
            if (options.Stage == PlayerCommandObservationStage.SceneCommandDispatch
                && options.SlotPendingEnabled
                && _index == null)
            {
                LogOnce(
                    "slot-pending-scan-gated-no-index",
                    $"{Stamp()} slot-pending scan gated: observation index unavailable; "
                    + "stored charPending directives will not apply until the index loads.");
            }

            return;
        }

        ConfiguredSceneResolutionSnapshot? selected = Plugin.Api.SceneResolution.Current;
        string? sceneIdentity = null;
        if (selected != null
            && selected.Status == ConfiguredSceneResolutionStatus.Mapped
            && selected.SelectedSceneTrusted
            && !string.IsNullOrWhiteSpace(selected.NodeGuid)
            && selected.SceneIndex.HasValue
            && IsSha256(selected.SceneFingerprint))
        {
            sceneIdentity =
                $"playback-sidecar:{selected.NodeGuid}:{selected.SceneIndex.Value}:{selected.SceneFingerprint}";
        }

        ApiResult<IReadOnlyList<SlotPendingOutcome>> scan =
            Plugin.Api.CharacterCommands.ScanSlotPendingsOnMainThread(
                sceneIdentity,
                _lastObservedWindowSequence);
        if (!scan.Success || scan.Value == null || scan.Value.Count == 0)
        {
            return;
        }

        var lines = new List<string>();
        foreach (SlotPendingOutcome outcome in scan.Value)
        {
            switch (outcome.Status)
            {
                case SlotPendingOutcomeStatus.Applied:
                    lines.Add(
                        $"{Stamp()} SLOT_PENDING_APPLIED slot={outcome.PublicSlot}; "
                        + $"occupant={Escape(outcome.OccupantIdentifier ?? string.Empty)}; "
                        + $"beforeX={FormatOptionalX(outcome.BeforeX)}; "
                        + $"appliedX={FormatOptionalX(outcome.AppliedX)}; "
                        + "durationMs=0; baselineCaptured=true");
                    break;
                case SlotPendingOutcomeStatus.Expired:
                    lines.Add(
                        $"{Stamp()} SLOT_PENDING_EXPIRED slot={outcome.PublicSlot}; "
                        + $"reason={Escape(outcome.Reason ?? string.Empty)}");
                    break;
                default:
                    lines.Add(
                        $"{Stamp()} SLOT_PENDING_FAILED slot={outcome.PublicSlot}; "
                        + $"reason={Escape(outcome.Reason ?? string.Empty)}; action=dropped");
                    break;
            }
        }

        PlayerCommandObservationLog.Append(lines);
    }

    private static string FormatOptionalX(float? value) =>
        value.HasValue ? value.Value.ToString("R", CultureInfo.InvariantCulture) : "none";

    private static string FamilyTypeIdFor(string canonicalDirective) =>
        canonicalDirective.StartsWith(CharacterPresetCommandFamilyCompiler.CanonicalRootToken + ";", StringComparison.Ordinal)
            ? CharacterPresetCommandFamilyCompiler.CommandTypeId
            :
        canonicalDirective.StartsWith(
            SceneCameraCommandFamilyCompiler.CanonicalRootToken + ";",
            StringComparison.Ordinal)
            ? SceneCameraCommandFamilyCompiler.CommandTypeId
            : canonicalDirective.StartsWith(
                SlotPendingCommandFamilyCompiler.CanonicalRootToken + ";",
                StringComparison.Ordinal)
                ? SlotPendingCommandFamilyCompiler.CommandTypeId
                : CharacterTransformCommandFamilyCompiler.CommandTypeId;

    private static string FamilyCapabilityFor(string canonicalDirective) =>
        canonicalDirective.StartsWith(CharacterPresetCommandFamilyCompiler.CanonicalRootToken + ";", StringComparison.Ordinal)
            ? CharacterPresetCommandFamilyCompiler.CapabilityId
            :
        canonicalDirective.StartsWith(
            SceneCameraCommandFamilyCompiler.CanonicalRootToken + ";",
            StringComparison.Ordinal)
            ? SceneCameraCommandFamilyCompiler.CapabilityId
            : canonicalDirective.StartsWith(
                SlotPendingCommandFamilyCompiler.CanonicalRootToken + ";",
                StringComparison.Ordinal)
                ? SlotPendingCommandFamilyCompiler.CapabilityId
                 : CharacterTransformCommandFamilyCompiler.CapabilityId;

    private static string OriginIdentityFor(
        PreviewChainResolution? chain,
        string canonicalDirective)
    {
        Result<CharacterTransformCommand> parsed =
            CharacterParser.Parse(canonicalDirective);
        return parsed.Success && parsed.Value != null
            ? OriginIdentityFor(chain, parsed.Value.PublicSlot)
            : string.Empty;
    }

    private static string OriginIdentityFor(
        PreviewChainResolution? chain,
        int publicSlot) =>
        chain?.Slots.FirstOrDefault(slot => slot.PublicSlot == publicSlot)
            ?.LineageIdentity ?? string.Empty;

    private static void ObserveWindow(
        PlayerCommandObservationStage stage,
        long update,
        ClosedAdvanceWindow window,
        ApiResult<PlayerRuntimeContextSnapshot>? contextResult,
        ICollection<string> lines)
    {
        lines.Add(
            $"{Stamp()} advance-window sequence={window.Sequence}; close={window.CloseReason}; "
            + $"managedUnityStrings={window.ManagedUnityMessages.Count}; "
            + $"rejectedStrings={window.RejectedMessageCount}; overflow={window.CaptureOverflowed}; "
            + $"playbackRow={window.PlaybackRowIndex}; rowReadBefore={window.PlaybackRowReadBefore}");
        TrackObservedWindowSequence(window);

        if (stage is PlayerCommandObservationStage.PlaybackContextProbe
                or PlayerCommandObservationStage.SceneCommandDispatch)
        {
            AppendPlayerContext(window.Sequence, contextResult, lines);
        }

        AdvanceWindowAdmission admission = AdvanceWindowAdmissionPolicy.Classify(
            window.CloseReason switch
            {
                AdvanceWindowCloseReason.Completed => AdvanceWindowCloseKind.Completed,
                AdvanceWindowCloseReason.ReplacedByOverlappingPrefix =>
                    AdvanceWindowCloseKind.SupersededByOverlappingPrefix,
                _ => AdvanceWindowCloseKind.PrefixWithoutOpen
            },
            window.ManagedUnityMessages.Count,
            window.CaptureOverflowed,
            window.EmbeddedCommandConflict);
        if (admission == AdvanceWindowAdmission.SupersededLeaseOnly)
        {
            // The engine advanced again inside this click cascade, so the window
            // that actually captured the compiled-script identity never
            // completed. Cards with empty or very short dialogue do this on
            // every click; admitting the capture is the difference between a
            // working preview and a silent no-op.
            lines.Add(
                $"{Stamp()} window-admission sequence={window.Sequence}; "
                + "verdict=superseded-lease-only; "
                + "reason=superseded-by-overlapping-prefix-carrying-compiled-identity; "
                + $"managedUnityStrings={window.ManagedUnityMessages.Count}");
        }

        if (admission == AdvanceWindowAdmission.Reject)
        {
            lines.Add(
                $"{Stamp()} observation sequence={window.Sequence}; status=FAIL_CLOSED; "
                + $"reason={(window.EmbeddedCommandConflict ? "conflicting-embedded-command-captures" : "invalid-window")}");
            return;
        }

        if (stage == PlayerCommandObservationStage.WindowCapture)
        {
            Plugin.Host.CapabilitiesInternal.Verified(
                "Player.AdvanceObservationWindow",
                "zero-argument Prefix/Postfix window reached managed Update");
            return;
        }

        // A valid playback boundary ends temporary effects even if this row has
        // no directives, no visible dialogue, or no saved command index. This
        // grants cleanup only; it does not authorize any new command execution.
        if (stage == PlayerCommandObservationStage.SceneCommandDispatch
            && admission == AdvanceWindowAdmission.Standard
            && contextResult is { Success: true, Value.Mode: PlayerRuntimeMode.Playback })
        {
            Plugin.Host.CharacterPresetsInternal.ObservePlaybackDialogueBoundary(
                window.Sequence, window.PlaybackRowReadBefore, window.PlaybackRowIndex,
                window.ManagedUnityMessages.Count);
        }

        // An unsaved editor lease is authoritative for this exact selection,
        // including a zero-command deletion tombstone. It must be considered
        // before any persisted embedded/project batch with the same sanitized
        // official script identity.
        if (TryHandleEditorPreviewLease(
                stage,
                update,
                window,
                contextResult,
                lines))
        {
            return;
        }

        if (admission == AdvanceWindowAdmission.SupersededLeaseOnly)
        {
            // A superseded window that carried an identity the authoritative
            // lease did not claim stays a no-op: the cached, embedded-batch,
            // chain-only and project-index fallbacks keep requiring a completed
            // window, so one cascade can never dispatch twice through them.
            lines.Add(
                $"{Stamp()} observation sequence={window.Sequence}; status=FAIL_CLOSED; "
                + "reason=superseded-window-without-lease-claim; action=NO_OP");
            return;
        }

        if (window.EmbeddedCommandBatch != null)
        {
            PlaybackCommandBatch embedded = window.EmbeddedCommandBatch;
            ConfiguredSceneResolutionSnapshot? selected =
                Plugin.Api.SceneResolution.Current;
            EditorSceneIdentitySnapshot? editorIdentity =
                Plugin.Api.SceneIdentity.Current;
            var selectedBinding = new EmbeddedPreviewSelectionBinding(
                selected?.ObservationSequence ?? -1,
                selected?.Status == ConfiguredSceneResolutionStatus.Mapped,
                selected?.SelectedSceneTrusted == true,
                selected?.PlaybackRecordIndex,
                selected?.NodeGuid ?? string.Empty,
                selected?.SceneIndex,
                selected?.SceneFingerprint ?? string.Empty);
            var currentEditorScript = new CompiledScriptIdentity(
                editorIdentity?.CompiledScriptSha256 ?? string.Empty,
                editorIdentity?.CompiledScriptLength ?? -1,
                editorIdentity?.CompiledScriptLineCount ?? -1);
            Result<int> guarded = new EmbeddedPreviewCommandGuard().Validate(
                embedded,
                currentEditorScript,
                editorIdentity?.ObservationSequence ?? -1,
                selectedBinding);
            if (!guarded.Success)
            {
                lines.Add(
                    $"{Stamp()} observation sequence={window.Sequence}; status=FAIL_CLOSED; "
                    + $"reason=embedded-editor-identity-or-selection-drift; detail={Escape(guarded.Error)}; "
                    + $"provisionalRecord={embedded.PlaybackRecordIndex}; "
                    + $"selectedRecord={selected?.PlaybackRecordIndex?.ToString(CultureInfo.InvariantCulture) ?? "none"}; action=NO_OP");
                return;
            }

            int exactMessages = window.ManagedUnityMessages.Count(message =>
                CommandIdentity.CompiledScript(message) == embedded.CompiledScript);
            if (exactMessages == 0)
            {
                lines.Add(
                    $"{Stamp()} observation sequence={window.Sequence}; status=FAIL_CLOSED; "
                    + "reason=embedded-sanitized-script-not-observed; action=NO_OP");
                return;
            }

            lines.Add(
                $"{Stamp()} observation sequence={window.Sequence}; status=EMBEDDED_COMMAND_BATCH; "
                + $"record={embedded.PlaybackRecordIndex}; exactMessages={exactMessages}; "
                + $"action={StageAction(stage)}");
            HandleObservedBatch(
                stage,
                update,
                window.Sequence,
                embedded,
                lines,
                "EMBEDDED",
                contextResult?.Value?.PreviewMode == true);
            return;
        }

        if (TryHandleCachedEmbeddedPreview(
                stage,
                update,
                window,
                contextResult,
                lines))
        {
            return;
        }

        // Preview windows without cached embedded directives belong to
        // commandless scenes. In real playback characters persist across such
        // scenes; the editor preview instead resets them to official positions.
        // Queue a chain-only application so inherited end states survive here
        // too. Deduplication happens per editor observation sequence.
        TryEnqueueChainOnlyPreview(
            stage,
            update,
            window,
            contextResult?.Value?.PreviewMode == true,
            lines);

        // Playback-side pair discovery: without an editor selection the index
        // waited forever, so a session that goes straight into playback
        // dispatched nothing (2026-09-18).
        TryDiscoverPairFromPlaybackWindow(contextResult, window, lines);

        IPlaybackCommandIndex? index = _index;
        if (index == null)
        {
            lines.Add(
                $"{Stamp()} observation sequence={window.Sequence}; status=NO_INDEX; action=NO_OP");
            return;
        }

        // Row-indexed disambiguation is a playback affordance only: during an
        // editor preview the engine row bears no relation to the loaded archive.
        int playbackRowHint = contextResult?.Value?.PreviewMode == true
            ? -1
            : window.PlaybackRowIndex;
        Result<PlaybackCommandWindowObservation> observed = WindowObserver.Observe(
            index,
            window.ManagedUnityMessages,
            playbackRowHint);
        if (!observed.Success || observed.Value == null)
        {
            lines.Add(
                $"{Stamp()} observation sequence={window.Sequence}; status=FAIL_CLOSED; "
                + $"reason={Escape(observed.Error)}; action=NO_OP");
            return;
        }

        PlaybackCommandWindowObservation match = observed.Value;
        lines.Add(
            $"{Stamp()} observation sequence={window.Sequence}; status={match.Status}; "
            + $"exactRecords={match.ExactRecordCount}; ambiguousMessages={match.AmbiguousMessageCount}; "
            + $"row={playbackRowHint}; "
            + $"action={(match.Batch == null ? "NO_OP" : StageAction(stage))}");

        if (match.Status != PlaybackCommandWindowObservationStatus.CommandBatch
            || match.Batch == null)
        {
            return;
        }

        HandleObservedBatch(
            stage,
            update,
            window.Sequence,
            match.Batch,
            lines,
            "SIDECAR_OR_PROJECT",
            contextResult?.Value?.PreviewMode == true);
    }

    private static bool TryHandleEditorPreviewLease(
        PlayerCommandObservationStage stage,
        long update,
        ClosedAdvanceWindow window,
        ApiResult<PlayerRuntimeContextSnapshot>? contextResult,
        ICollection<string> lines)
    {
        if (stage != PlayerCommandObservationStage.SceneCommandDispatch
            || contextResult?.Success != true
            || contextResult.Value == null
            || contextResult.Value.Mode != PlayerRuntimeMode.EditorPreview)
        {
            return false;
        }

        ApiResult<EditorSceneSnapshot> currentScene = Plugin.Api.Editor.GetSelectedScene();
        EmbeddedEditorPreviewClaimStatus claim =
            EmbeddedEditorPreviewLeaseCache.TryClaim(
                window.Sequence,
                window.ManagedUnityMessages,
                contextResult.Value,
                Plugin.Api.SceneIdentity.Current,
                currentScene,
                out EmbeddedEditorPreviewLease? lease,
                out string claimError);
        if (claim == EmbeddedEditorPreviewClaimStatus.NoClaim)
        {
            return false;
        }

        if (claim != EmbeddedEditorPreviewClaimStatus.Authorized || lease == null)
        {
            lines.Add(
                $"{Stamp()} observation sequence={window.Sequence}; status=EDITOR_PREVIEW_LEASE_REJECTED; "
                + $"reason={Escape(claimError)}; action=NO_OP; persistedFallback=false");
            return true;
        }

        // One cascade, one dispatch. A superseded window shares its drain pass
        // with the completed sibling that carries the same identity, so the
        // second authorization of the same generation inside the same managed
        // update is dropped here instead of queuing a duplicate dispatch.
        if (AdvanceWindowAdmissionPolicy.IsSameDrainDuplicate(
                _lastAuthorizedLeaseUpdate,
                _lastAuthorizedLeaseGeneration,
                update,
                lease.DataListGeneration))
        {
            lines.Add(
                $"{Stamp()} observation sequence={window.Sequence}; status=EDITOR_PREVIEW_LEASE_SUPERSEDED; "
                + $"reason=same-generation-already-authorized-in-this-update; "
                + $"generation={lease.DataListGeneration}; action=NO_OP; persistedFallback=false");
            return true;
        }

        EditorSceneIdentitySnapshot? currentIdentity = Plugin.Api.SceneIdentity.Current;
        string deferredIdentityError = string.Empty;
        // Global Current survives a DataList change. It may belong to an older
        // row even when its request/hash happen to match this one. An
        // unconfirmed generation must prove its own real first managed log.
        if (lease.ConfirmedObservationSequence <= 0)
        {
            if (!ManagedUnityLogSelectionProbe.ConfirmDeferredFromPreview(
                lease,
                window,
                contextResult.Value,
                out EditorSceneIdentitySnapshot? confirmedIdentity,
                out ApiResult<EditorSceneSnapshot>? confirmedScene,
                out deferredIdentityError))
            {
                lines.Add(
                    $"{Stamp()} observation sequence={window.Sequence}; status=EDITOR_PREVIEW_LEASE_REJECTED; "
                    + "reason=no-current-generation-selection-confirmation; "
                    + $"deferredConfirmation={Escape(deferredIdentityError)}; action=NO_OP; persistedFallback=false");
                return true;
            }
            currentIdentity = confirmedIdentity;
            currentScene = confirmedScene!;
            // RefreshGenerationScene published the real first-log observation
            // into the cache. Carry it on this already claimed local lease so
            // the dispatch-time IsCurrent check also rejects identity drift.
            lease = lease with
            {
                ConfirmedObservationSequence = currentIdentity!.ObservationSequence
            };
        }
        if (currentIdentity == null || currentScene.Value == null)
        {
            lines.Add(
                $"{Stamp()} observation sequence={window.Sequence}; status=EDITOR_PREVIEW_LEASE_REJECTED; "
                + "reason=current-editor-identity-or-scene-unavailable; "
                + $"deferredConfirmation={Escape(deferredIdentityError)}; action=NO_OP; persistedFallback=false");
            return true;
        }

        static EditorPreviewSceneAddress CoreScene(SceneAddress scene) => new(
            scene.ProjectKey,
            scene.NodeGuid,
            scene.SceneIndex,
            scene.Fingerprint);
        var capturedSelection = new EditorPreviewSelectionBinding(
            lease.SelectionGeneration,
            lease.SelectionRequestId,
            lease.ObservationSequence,
            lease.CompiledScript,
            CoreScene(lease.Scene));
        var liveSelection = new EditorPreviewSelectionBinding(
            lease.SelectionGeneration,
            currentIdentity.SelectionRequestId,
            currentIdentity.ObservationSequence,
            new CompiledScriptIdentity(
                currentIdentity.CompiledScriptSha256,
                currentIdentity.CompiledScriptLength,
                currentIdentity.CompiledScriptLineCount),
            CoreScene(currentScene.Value.Address));
        int exactMessages = window.ManagedUnityMessages.Count(message =>
            CommandIdentity.CompiledScript(message) == lease.CompiledScript);
        Result<EditorPreviewLeaseAuthorization> guarded = EditorPreviewGate.TryAuthorizeWindow(
            new EditorPreviewLeaseCandidate(
                capturedSelection,
                lease.CanonicalDirectives,
                lease.IsTombstone,
                lease.MinimumWindowSequenceExclusive),
            new EditorPreviewLiveObservation(
                contextResult.Value,
                liveSelection,
                exactMessages,
                window.Sequence));
        if (!guarded.Success || guarded.Value == null
            || !string.Equals(
                guarded.Value.StableSceneIdentity,
                lease.StableSceneIdentity,
                StringComparison.Ordinal))
        {
            lines.Add(
                $"{Stamp()} observation sequence={window.Sequence}; status=EDITOR_PREVIEW_LEASE_REJECTED; "
                + $"reason={Escape(guarded.Success ? "stable editor scene identity mismatch" : guarded.Error)}; "
                + "action=NO_OP; persistedFallback=false");
            return true;
        }

        if (!EmbeddedEditorPreviewLeaseCache.CommitAuthorizedOverlay(
                lease, guarded.Value, contextResult.Value, currentIdentity,
                currentScene, out string overlayError))
        {
            lines.Add(
                $"{Stamp()} observation sequence={window.Sequence}; status=EDITOR_PREVIEW_LEASE_REJECTED; "
                + $"reason=overlay-commit-rejected:{Escape(overlayError)}; action=NO_OP; persistedFallback=false");
            return true;
        }

        // Rejected siblings cannot reserve this drain: the actual first-log
        // window may still be waiting later in the same cascade.
        _lastAuthorizedLeaseUpdate = update;
        _lastAuthorizedLeaseGeneration = lease.DataListGeneration;

        EditorPreviewCommand[] commands = guarded.Value.Commands
            .Select(command => new EditorPreviewCommand(
                command.Order,
                command.DispatchMode == EditorPreviewDispatchMode.DeferredSlotPending
                    ? EditorPreviewCommandKind.SlotPending
                    : string.Equals(
                        command.CommandType,
                        SceneCameraCommandFamilyCompiler.CommandTypeId,
                        StringComparison.Ordinal)
                            ? EditorPreviewCommandKind.Camera
                            : string.Equals(
                                command.CommandType,
                                SpineOverlayCommandFamilyCompiler.CommandTypeId,
                                StringComparison.Ordinal)
                                ? EditorPreviewCommandKind.SpineOverlay
                                : string.Equals(command.CommandType,
                                    CharacterPresetCommandFamilyCompiler.CommandTypeId, StringComparison.Ordinal)
                                    ? EditorPreviewCommandKind.CharacterPreset
                                    : EditorPreviewCommandKind.Character,
                command.Resource.PublicSlot,
                command.CanonicalDirective,
                command.Resource.TrackIndex))
            .ToArray();
        EditorPreviewResource[] resources = guarded.Value.Footprint.Entries
            .Where(entry => entry.DispatchMode == EditorPreviewDispatchMode.Immediate)
            .Select(entry => new EditorPreviewResource(
                entry.Resource.Kind
                    == AzureArchive.VideoTools.Core.Commands.EditorPreviewResourceKind.SceneCamera
                        ? RuntimeEditorPreviewResourceKind.Camera
                        : entry.Resource.Kind
                            == AzureArchive.VideoTools.Core.Commands.EditorPreviewResourceKind.SpineOverlay
                                ? RuntimeEditorPreviewResourceKind.SpineOverlay
                                : entry.Resource.Kind
                                    == AzureArchive.VideoTools.Core.Commands.EditorPreviewResourceKind.CharacterPreset
                                    ? RuntimeEditorPreviewResourceKind.CharacterPreset
                                    : RuntimeEditorPreviewResourceKind.Character,
                entry.Resource.PublicSlot,
                entry.Resource.TrackIndex))
            .ToArray();

        DeferredEditorPreviews.Enqueue(new DeferredEditorPreview(
            window.Sequence,
            ManagedDispatchSchedule.SameFrameLateUpdate(update),
            lease,
            Array.AsReadOnly(commands),
            Array.AsReadOnly(resources)));
        // FIX D: reserve this generation's chain coverage for the leased
        // preview so chain-only fallback windows of the same click cannot
        // double-apply between queue and same-frame dispatch. The generation
        // itself is marked applied only after DispatchEditorPreview actually
        // applies the chain.
        _leaseReservedChainGenerationId = lease.DataListGeneration;
        lines.Add(
            $"{Stamp()} observation sequence={window.Sequence}; status=EDITOR_PREVIEW_LEASE_QUEUED; "
            + $"commands={commands.Length}; resources={resources.Length}; "
            + $"tombstone={lease.IsTombstone}; earliestPhase=LateUpdate; "
            + "mappingRequired=false; persistedFallback=false");
        return true;
    }

    private static bool TryHandleCachedEmbeddedPreview(
        PlayerCommandObservationStage stage,
        long update,
        ClosedAdvanceWindow window,
        ApiResult<PlayerRuntimeContextSnapshot>? contextResult,
        ICollection<string> lines)
    {
        if (contextResult?.Success != true
            || contextResult.Value == null
            || !contextResult.Value.PreviewMode)
        {
            return false;
        }

        if (!EmbeddedEditorDirectiveCache.TryMatch(
                window.ManagedUnityMessages,
                out CachedEmbeddedEditorDirectives? cached,
                out int exactMessages,
                out string cacheError))
        {
            if (cacheError.Length != 0)
            {
                lines.Add(
                    $"{Stamp()} embedded-preview-cache event=CACHE_MISS; "
                    + $"observation={window.Sequence}; reason=ambiguous; "
                    + $"managedMessages={window.ManagedUnityMessages.Count}");
                lines.Add(
                    $"{Stamp()} observation sequence={window.Sequence}; status=FAIL_CLOSED; "
                    + $"reason=embedded-preview-cache-ambiguous; detail={Escape(cacheError)}; action=NO_OP");
                return true;
            }

            lines.Add(
                $"{Stamp()} embedded-preview-cache event=CACHE_MISS; "
                + $"observation={window.Sequence}; reason=no-exact-script-identity; "
                + $"managedMessages={window.ManagedUnityMessages.Count}");
            return false;
        }

        lines.Add(
            $"{Stamp()} embedded-preview-cache event=CACHE_HIT; "
            + $"observation={window.Sequence}; "
            + $"scriptSha16={cached!.CompiledScript.Sha256[..16]}; "
            + $"exactMessages={exactMessages}; commands={cached.CanonicalDirectives.Count}");
        if (!cached!.IsValid)
        {
            lines.Add(
                $"{Stamp()} observation sequence={window.Sequence}; status=FAIL_CLOSED; "
                + $"reason=embedded-preview-directive-invalid; detail={Escape(cached.ValidationError)}; action=NO_OP");
            return true;
        }

        EditorSceneIdentitySnapshot? editorIdentity = Plugin.Api.SceneIdentity.Current;
        ConfiguredSceneResolutionSnapshot? selected = Plugin.Api.SceneResolution.Current;
        // The background scene-resolution worker may lag one observation behind
        // the just-published editor identity. Building the provisional batch
        // from a stale mapping would pair this scene's directives with the
        // previous scene's record and fingerprint, which can never validate.
        // Queue the raw cached directives instead; the retry drain rebuilds
        // the batch from the CURRENT mapping on every attempt.
        if (editorIdentity != null
            && (selected == null || selected.ObservationSequence < editorIdentity.ObservationSequence))
        {
            EnqueuePendingEmbeddedPreview(
                update,
                window.Sequence,
                editorIdentity.ObservationSequence,
                cached.CompiledScript,
                cached.CanonicalDirectives,
                cached.PutTimestampUtcTicks,
                chainGenerationId: 0);
            lines.Add(
                $"{Stamp()} observation sequence={window.Sequence}; status=EMBEDDED_PREVIEW_RETRY_QUEUED; "
                + $"commands={cached.CanonicalDirectives.Count}; identityObservation={editorIdentity.ObservationSequence}; "
                + $"resolutionObservation={selected?.ObservationSequence ?? -1}; "
                + $"retryBudgetUpdates={EmbeddedPreviewRetryUpdates}; action=REBUILD_ON_LATER_UPDATES");
            return true;
        }

        var selectedBinding = new EmbeddedPreviewSelectionBinding(
            selected!.ObservationSequence,
            selected.Status == ConfiguredSceneResolutionStatus.Mapped,
            selected.SelectedSceneTrusted == true,
            selected.PlaybackRecordIndex,
            selected.NodeGuid,
            selected.SceneIndex,
            selected.SceneFingerprint);
        if (!TryBuildCachedEmbeddedBatch(
                cached!,
                selected,
                out PlaybackCommandBatch? batch,
                out string buildError))
        {
            lines.Add(
                $"{Stamp()} observation sequence={window.Sequence}; status=EMBEDDED_PREVIEW_WAITING; "
                + $"reason={Escape(buildError)}; exactMessages={exactMessages}; action=NO_OP_REPLAY_AFTER_MAPPING");
            return true;
        }

        var currentEditorScript = new CompiledScriptIdentity(
            editorIdentity?.CompiledScriptSha256 ?? string.Empty,
            editorIdentity?.CompiledScriptLength ?? -1,
            editorIdentity?.CompiledScriptLineCount ?? -1);
        Result<int> guarded = new EmbeddedPreviewCommandGuard().Validate(
            batch!,
            currentEditorScript,
            editorIdentity?.ObservationSequence ?? -1,
            selectedBinding);
        if (!guarded.Success)
        {
            lines.Add(
                $"{Stamp()} observation sequence={window.Sequence}; status=FAIL_CLOSED; "
                + $"reason=embedded-preview-identity-or-selection-drift; detail={Escape(guarded.Error)}; action=NO_OP");
            return true;
        }

        lines.Add(
            $"{Stamp()} observation sequence={window.Sequence}; status=EMBEDDED_PREVIEW_COMMAND_BATCH; "
            + $"record={batch!.PlaybackRecordIndex}; editorObservation={selected!.ObservationSequence}; "
            + $"exactMessages={exactMessages}; action={StageAction(stage)}");
        HandleObservedBatch(
            stage,
            update,
            window.Sequence,
            batch,
            lines,
            "EMBEDDED_EDITOR_CACHE",
            previewMode: true);
        return true;
    }

    private static bool TryBuildCachedEmbeddedBatch(
        CachedEmbeddedEditorDirectives cached,
        ConfiguredSceneResolutionSnapshot? selected,
        out PlaybackCommandBatch? batch,
        out string error)
    {
        batch = null;
        error = string.Empty;
        if (selected == null
            || selected.Status != ConfiguredSceneResolutionStatus.Mapped
            || !selected.SelectedSceneTrusted
            || !selected.PlaybackRecordIndex.HasValue
            || !selected.SceneIndex.HasValue
            || !Guid.TryParseExact(selected.NodeGuid, "D", out _)
            || !IsSha256(selected.SceneFingerprint))
        {
            error = "the current editor observation does not yet have a trusted AAP/AAS scene mapping";
            return false;
        }

        PlaybackCommandInstruction[] commands = cached.CanonicalDirectives
            .Select((directive, index) => new PlaybackCommandInstruction(
                Guid.NewGuid().ToString("D"),
                index,
                CommandTimelinePhase.SceneEnter,
                FamilyTypeIdFor(directive),
                FamilyCapabilityFor(directive),
                directive))
            .ToArray();
        if (commands.Length < 1 || commands.Length > EmbeddedAavtDirectiveExtractor.MaxCommandsPerScene)
        {
            error = "the cached embedded command count must be between one and "
                + EmbeddedAavtDirectiveExtractor.MaxCommandsPerScene + " resources";
            return false;
        }

        ConfiguredSceneResolutionSnapshot selectedSnapshot = selected;
        batch = new PlaybackCommandBatch(
            new SceneKey(
                selectedSnapshot.NodeGuid,
                selectedSnapshot.SceneIndex!.Value,
                selectedSnapshot.SceneFingerprint),
            selectedSnapshot.PlaybackRecordIndex!.Value,
            selectedSnapshot.SceneFingerprint,
            cached.CompiledScript,
            Array.AsReadOnly(commands));
        return true;
    }

    private static bool IsSha256(string value)
    {
        if (value == null || value.Length != 64)
        {
            return false;
        }

        try
        {
            return Convert.FromHexString(value).Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void HandleObservedBatch(
        PlayerCommandObservationStage stage,
        long update,
        long windowSequence,
        PlaybackCommandBatch batch,
        ICollection<string> lines,
        string source,
        bool previewMode)
    {
        if (stage == PlayerCommandObservationStage.QueueNoOp
            || IsDispatchCanaryStage(stage)
            || stage == PlayerCommandObservationStage.SceneCommandDispatch)
        {
            PreviewChainResolution? chain = null;
            // FIX E: capability+stage policy, deliberately mode-independent.
            // EditorPreview AND formal playback both apply the resolved
            // chain; playback previously required previewMode here and never
            // executed the inherited-start rebuild it had already resolved.
            bool applyResolvedChain = PreviewChainApplyPolicy.ShouldApplyChain(
                stage == PlayerCommandObservationStage.SceneCommandDispatch
                    ? PreviewChainApplyStage.SceneCommandDispatch
                    : PreviewChainApplyStage.NotSceneCommandDispatch,
                _options?.PreviewChainEnabled == true);
            if (stage == PlayerCommandObservationStage.SceneCommandDispatch
                && (!previewMode || applyResolvedChain))
            {
                chain = TryResolvePreviewChain(
                    batch,
                    lines,
                    deduplicateByEditorGeneration: previewMode,
                    preferLiveEditorGraph: previewMode);
            }

            ManagedDispatchSchedule schedule =
                stage == PlayerCommandObservationStage.SceneCommandDispatch
                    ? ManagedDispatchSchedule.SameFrameLateUpdate(update)
                    : ManagedDispatchSchedule.NextUpdate(update);
            // FIX D: only editor-preview batches belong to a DataList
            // generation; playback batches carry 0 and never touch editor
            // generation state when they apply their chain.
            long chainGenerationId = previewMode
                && EmbeddedEditorPreviewLeaseCache.TryGetCurrentGeneration(
                    out DataListGenerationScope batchScope)
                    ? batchScope.GenerationId
                    : 0;
            DeferredBatches.Enqueue(new DeferredCommandBatch(
                windowSequence,
                schedule,
                batch,
                chain,
                applyResolvedChain,
                chainGenerationId));
            string eventName = stage switch
            {
                PlayerCommandObservationStage.QueueNoOp => "QUEUED_NO_OP",
                PlayerCommandObservationStage.DispatchReplayCanary =>
                    "QUEUED_DISPATCH_REPLAY_CANARY",
                PlayerCommandObservationStage.SceneCommandDispatch =>
                    "QUEUED_SCENE_COMMANDS",
                _ => "QUEUED_DISPATCH_CANARY"
            };
            lines.Add(
                $"{Stamp()} {eventName} sequence={windowSequence}; record={batch.PlaybackRecordIndex}; "
                + $"commands={batch.Commands.Count}; earliestUpdate={schedule.EarliestUpdate}; "
                + $"earliestPhase={schedule.EarliestPhase}; source={source}");
            return;
        }

        foreach (PlaybackCommandInstruction command in batch.Commands)
        {
            lines.Add(
                $"{Stamp()} WOULD_DISPATCH sequence={windowSequence}; "
                + $"record={batch.PlaybackRecordIndex}; commandId={command.CommandId}; "
                + $"order={command.Order}; phase={command.Phase}; type={command.CommandType}; "
                + $"capability={command.RequiredCapability}; directive={Escape(command.CanonicalDirective)}; "
                + $"source={source}");
        }
    }

    private static PreviewChainResolution? TryResolvePreviewChain(
        PlaybackCommandBatch batch,
        ICollection<string> lines,
        bool deduplicateByEditorGeneration = true,
        bool preferLiveEditorGraph = false)
    {
        // FIX D: generation-scoped dedup replaces the permanent
        // per-observation-sequence marker. Sibling/stale windows of ONE
        // DataList generation collapse because the first actual application
        // marks the generation via TryMarkGenerationChainApplied; every NEW
        // generation id reopens resolution. Playback passes
        // deduplicateByEditorGeneration=false and never consults editor
        // generation state (FIX E).
        if (deduplicateByEditorGeneration
            && EmbeddedEditorPreviewLeaseCache.TryGetCurrentGeneration(out DataListGenerationScope scope)
            && scope.ChainApplied)
        {
            lines.Add(
                $"{Stamp()} preview-chain status=SKIP; "
                + "reason=chain-already-applied-for-generation; "
                + $"generation={scope.GenerationId}; "
                + $"collecting={scope.CollectingCandidates}; "
                + $"record={batch.PlaybackRecordIndex}");
            return null;
        }

        PreviewChainSource? source = EffectiveChainSource(preferLiveEditorGraph);
        if (source == null)
        {
            lines.Add(
                $"{Stamp()} preview-chain status=SKIP; reason=chain-source-unavailable; "
                + $"record={batch.PlaybackRecordIndex}");
            return null;
        }

        var parser = new CharacterTransformDirectiveParser();
        var slots = new SortedSet<int>();
        foreach (PlaybackCommandInstruction command in batch.Commands)
        {
            Result<CharacterTransformCommand> parsed = parser.Parse(command.CanonicalDirective);
            if (parsed.Success && parsed.Value != null)
            {
                slots.Add(parsed.Value.PublicSlot);
            }
        }

        // Silent slots occupied in the AAP inherit too: real playback keeps
        // their transforms even when this scene has no directive for them.
        StoryNodeSnapshot? projectNode = source.Project.Nodes.FirstOrDefault(n =>
            string.Equals(n.NodeGuid, batch.Scene.NodeGuid, StringComparison.Ordinal));
        if (projectNode != null
            && batch.Scene.SceneIndex >= 0
            && batch.Scene.SceneIndex < projectNode.Scenes.Count)
        {
            foreach (ProjectCharacterSnapshot character in projectNode.Scenes[batch.Scene.SceneIndex].Characters)
            {
                if (character.PhysicalSlot >= 1 && character.PhysicalSlot <= 5)
                {
                    slots.Add(character.PhysicalSlot);
                }
            }
        }

        if (slots.Count == 0)
        {
            lines.Add(
                $"{Stamp()} preview-chain status=SKIP; reason=no-parsable-slots; "
                + $"record={batch.PlaybackRecordIndex}");
            return null;
        }

        Result<PreviewChainResolution> resolved = source.Resolver.Resolve(
            batch.Scene,
            slots,
            (key, slot) => source.Directives.TryGet(key.NodeGuid, key.SceneIndex, slot, out string directive)
                ? directive
                : null);
        if (!resolved.Success || resolved.Value == null)
        {
            lines.Add(
                $"{Stamp()} preview-chain status=SKIP; reason=resolver-failed; "
                + $"record={batch.PlaybackRecordIndex}; detail={Escape(resolved.Error)}");
            return null;
        }

        foreach (PreviewChainSlotResolution slot in resolved.Value.Slots)
        {
            string diagnostics = slot.Diagnostics.Count == 0
                ? string.Empty
                : "; diag=" + Escape(string.Join("|", slot.Diagnostics));
            lines.Add(
                $"{Stamp()} preview-chain-slot slot={slot.PublicSlot}; "
                + $"folded={slot.State.FoldedCommandCount}; stop={slot.StopReason}; "
                + $"inherited={slot.HasInheritedStart}; occupant={Escape(slot.ExpectedOccupant)}"
                + $"{diagnostics}");
        }

        // FIX D: resolution alone no longer marks anything. The generation is
        // marked applied only at actual application time (DispatchEditorPreview,
        // DispatchSceneCommands, or the chain-only drain), so first caller
        // wins and a new generation id always rebuilds.
        return resolved.Value;
    }

    private static void EnqueuePendingEmbeddedPreview(
        long update,
        long windowSequence,
        long editorObservationSequence,
        CompiledScriptIdentity compiledScript,
        IReadOnlyList<string> canonicalDirectives,
        long putTimestampUtcTicks,
        long chainGenerationId = 0)
    {
        while (PendingEmbeddedPreviews.Count >= MaximumPendingEmbeddedPreviews)
        {
            PendingEmbeddedPreview dropped = PendingEmbeddedPreviews.Dequeue();
            PlayerCommandObservationLog.Append(
                $"{Stamp()} preview-retry status=DROPPED; sequence={dropped.WindowSequence}; "
                + "reason=pending-overflow");
        }

        PendingEmbeddedPreviews.Enqueue(new PendingEmbeddedPreview(
            windowSequence,
            update,
            editorObservationSequence,
            compiledScript,
            canonicalDirectives,
            putTimestampUtcTicks,
            chainGenerationId));
    }

    private static void TryEnqueueChainOnlyPreview(
        PlayerCommandObservationStage stage,
        long update,
        ClosedAdvanceWindow window,
        bool previewMode,
        ICollection<string> lines)
    {
        if (stage != PlayerCommandObservationStage.SceneCommandDispatch
            || !previewMode
            || _options?.PreviewChainEnabled != true
            || _chainSource == null)
        {
            return;
        }

        if (window.CloseReason != AdvanceWindowCloseReason.Completed
            || window.CaptureOverflowed
            || window.EmbeddedCommandConflict)
        {
            return;
        }

        EditorSceneIdentitySnapshot? identity = Plugin.Api.SceneIdentity.Current;
        if (identity == null || identity.ObservationSequence <= 0)
        {
            return;
        }

        // FIX D: chain-only coverage is scoped to ONE DataList generation
        // instead of the editor observation sequence, which repeated green
        // previews without OnChildSelect never advance. This replaces the old
        // ClaimsSelection(identity) gate with scope semantics:
        // CollectingCandidates is informational only — an unconfirmed
        // generation stays Collecting for its whole lifetime, so suppressing
        // on it would break exactly the no-selection previews FIX D targets.
        // Authority is carried by scope.ChainApplied (a lease or earlier
        // application already covered this generation) and by the two file-
        // local reservations. Without any retained generation scope there is
        // no bounded dedup domain, so fail closed.
        if (!EmbeddedEditorPreviewLeaseCache.TryGetCurrentGeneration(out DataListGenerationScope scope))
        {
            return;
        }

        // Stale sibling windows issued before this generation began belong to
        // an older click and never open chain coverage.
        if (window.Sequence <= scope.MinimumWindowSequenceExclusive)
        {
            return;
        }

        if (scope.ChainApplied
            || scope.GenerationId == _chainOnlyEnqueuedGenerationId
            || scope.GenerationId == _leaseReservedChainGenerationId)
        {
            return;
        }

        _chainOnlyEnqueuedGenerationId = scope.GenerationId;
        EnqueuePendingEmbeddedPreview(
            update,
            window.Sequence,
            identity.ObservationSequence,
            new CompiledScriptIdentity(
                identity.CompiledScriptSha256,
                identity.CompiledScriptLength,
                identity.CompiledScriptLineCount),
            Array.Empty<string>(),
            DateTimeOffset.UtcNow.Ticks,
            scope.GenerationId);
        lines.Add(
            $"{Stamp()} preview-chain status=CHAIN_ONLY_QUEUED; observation={window.Sequence}; "
            + $"identityObservation={identity.ObservationSequence}; "
            + $"generation={scope.GenerationId}; collecting={scope.CollectingCandidates}");
    }

    private static void DrainPendingEmbeddedPreviews(
        PlayerCommandObservationStage stage,
        long update)
    {
        if (PendingEmbeddedPreviews.Count == 0)
        {
            return;
        }

        if (stage != PlayerCommandObservationStage.SceneCommandDispatch)
        {
            PendingEmbeddedPreviews.Clear();
            return;
        }

        var lines = new List<string>();
        int count = PendingEmbeddedPreviews.Count;
        while (count-- > 0)
        {
            PendingEmbeddedPreview pending = PendingEmbeddedPreviews.Dequeue();
            EditorSceneIdentitySnapshot? identity = Plugin.Api.SceneIdentity.Current;
            if (identity == null
                || identity.ObservationSequence != pending.EditorObservationSequence)
            {
                lines.Add(
                    $"{Stamp()} preview-retry status=DROPPED; sequence={pending.WindowSequence}; "
                    + "reason=editor-identity-moved-on");
                continue;
            }

            if (pending.CanonicalDirectives.Count == 0)
            {
                // FIX D: generation-scoped authority check replaces
                // ClaimsSelection(identity). A lease queued for THIS pending's
                // generation (reservation) or an application already marked
                // applied for it suppresses the duplicate; a NEW generation
                // id reopens chain-only coverage.
                if (pending.ChainGenerationId != 0
                    && EmbeddedEditorPreviewLeaseCache.TryGetCurrentGeneration(
                        out DataListGenerationScope drainScope)
                    && drainScope.GenerationId == pending.ChainGenerationId
                    && (drainScope.ChainApplied
                        || drainScope.GenerationId == _leaseReservedChainGenerationId))
                {
                    lines.Add(
                        $"{Stamp()} preview-chain status=CHAIN_ONLY_SKIPPED; "
                        + $"sequence={pending.WindowSequence}; "
                        + $"generation={pending.ChainGenerationId}; "
                        + "reason=editor-preview-lease-authoritative");
                    continue;
                }

                DrainChainOnlyPreview(pending, update, lines);
                continue;
            }

            bool budgetExhausted =
                update - pending.CreatedUpdate >= EmbeddedPreviewRetryUpdates;
            ConfiguredSceneResolutionSnapshot? selected = Plugin.Api.SceneResolution.Current;
            var cached = new CachedEmbeddedEditorDirectives(
                pending.CompiledScript,
                pending.CanonicalDirectives,
                false,
                string.Empty,
                pending.PutTimestampUtcTicks);
            if (!TryBuildCachedEmbeddedBatch(
                    cached,
                    selected,
                    out PlaybackCommandBatch? batch,
                    out string buildError))
            {
                if (budgetExhausted)
                {
                    lines.Add(
                        $"{Stamp()} preview-retry status=DROPPED; sequence={pending.WindowSequence}; "
                        + $"reason=retry-budget-exhausted; waitedUpdates={update - pending.CreatedUpdate}; "
                        + $"detail={Escape(buildError)}");
                    continue;
                }

                PendingEmbeddedPreviews.Enqueue(pending);
                continue;
            }

            var selectedBinding = new EmbeddedPreviewSelectionBinding(
                selected!.ObservationSequence,
                selected.Status == ConfiguredSceneResolutionStatus.Mapped,
                selected.SelectedSceneTrusted == true,
                selected.PlaybackRecordIndex,
                selected.NodeGuid,
                selected.SceneIndex,
                selected.SceneFingerprint);
            var currentEditorScript = new CompiledScriptIdentity(
                identity.CompiledScriptSha256,
                identity.CompiledScriptLength,
                identity.CompiledScriptLineCount);
            Result<int> guarded = new EmbeddedPreviewCommandGuard().Validate(
                batch!,
                currentEditorScript,
                identity.ObservationSequence,
                selectedBinding);
            if (!guarded.Success)
            {
                if (budgetExhausted)
                {
                    lines.Add(
                        $"{Stamp()} preview-retry status=DROPPED; sequence={pending.WindowSequence}; "
                        + $"reason=retry-budget-exhausted; waitedUpdates={update - pending.CreatedUpdate}; "
                        + $"detail={Escape(guarded.Error)}");
                    continue;
                }

                PendingEmbeddedPreviews.Enqueue(pending);
                continue;
            }

            lines.Add(
                $"{Stamp()} preview-retry status=RESOLVED; sequence={pending.WindowSequence}; "
                + $"record={batch!.PlaybackRecordIndex}; "
                + $"waitedUpdates={update - pending.CreatedUpdate}");
            HandleObservedBatch(
                stage,
                update,
                pending.WindowSequence,
                batch!,
                lines,
                "EMBEDDED_EDITOR_CACHE_RETRY",
                previewMode: true);
        }

        if (lines.Count != 0)
        {
            PlayerCommandObservationLog.Append(lines);
        }
    }

    private static void DrainChainOnlyPreview(
        PendingEmbeddedPreview pending,
        long update,
        ICollection<string> lines)
    {
        // FIX D: generation-scoped coverage check replaces the permanent
        // per-observation marker. A pending whose generation was already
        // superseded by a newer click is dropped; a pending whose generation
        // already had its chain applied is skipped. A new generation id
        // always reopens application.
        bool hasScope = EmbeddedEditorPreviewLeaseCache.TryGetCurrentGeneration(
            out DataListGenerationScope scope);
        if (hasScope && scope.GenerationId != pending.ChainGenerationId)
        {
            lines.Add(
                $"{Stamp()} preview-chain status=CHAIN_ONLY_DROPPED; sequence={pending.WindowSequence}; "
                + $"reason=editor-generation-superseded; pendingGeneration={pending.ChainGenerationId}; "
                + $"currentGeneration={scope.GenerationId}");
            return;
        }

        if (!hasScope || scope.ChainApplied)
        {
            lines.Add(
                $"{Stamp()} preview-chain status=CHAIN_ONLY_SKIPPED; sequence={pending.WindowSequence}; "
                + "reason=chain-already-applied-for-generation");
            return;
        }

        // Editor-preview only drain: prefer the live editor graph.
        PreviewChainSource? source = EffectiveChainSource(preferLiveEditorGraph: true);
        if (source == null)
        {
            lines.Add(
                $"{Stamp()} preview-chain status=CHAIN_ONLY_SKIPPED; sequence={pending.WindowSequence}; "
                + "reason=chain-source-unavailable");
            TryMarkChainAppliedForPending(pending);
            return;
        }

        bool budgetExhausted =
            update - pending.CreatedUpdate >= EmbeddedPreviewRetryUpdates;
        ConfiguredSceneResolutionSnapshot? selected = Plugin.Api.SceneResolution.Current;
        if (selected == null
            || selected.ObservationSequence != pending.EditorObservationSequence
            || selected.Status != ConfiguredSceneResolutionStatus.Mapped
            || !selected.SelectedSceneTrusted
            || !selected.SceneIndex.HasValue)
        {
            if (budgetExhausted)
            {
                lines.Add(
                    $"{Stamp()} preview-chain status=CHAIN_ONLY_DROPPED; sequence={pending.WindowSequence}; "
                    + $"reason=retry-budget-exhausted; waitedUpdates={update - pending.CreatedUpdate}");
                TryMarkChainAppliedForPending(pending);
                return;
            }

            PendingEmbeddedPreviews.Enqueue(pending);
            return;
        }

        TryMarkChainAppliedForPending(pending);
        StoryNodeSnapshot? node = source.Project.Nodes.FirstOrDefault(n =>
            string.Equals(n.NodeGuid, selected.NodeGuid, StringComparison.Ordinal));
        int sceneIndex = selected.SceneIndex.Value;
        if (node == null || sceneIndex < 0 || sceneIndex >= node.Scenes.Count)
        {
            lines.Add(
                $"{Stamp()} preview-chain status=CHAIN_ONLY_DROPPED; sequence={pending.WindowSequence}; "
                + "reason=scene-missing-from-chain-source");
            return;
        }

        var occupiedSlots = node.Scenes[sceneIndex].Characters
            .Select(character => character.PhysicalSlot)
            .Where(slot => slot >= 1 && slot <= 5)
            .Distinct()
            .OrderBy(slot => slot)
            .ToArray();
        var sceneKey = new SceneKey(selected.NodeGuid, sceneIndex, selected.SceneFingerprint);
        string sceneIdentity =
            $"playback-sidecar:{sceneKey.NodeGuid}:{sceneKey.SceneIndex}:{sceneKey.Fingerprint}";
        Result<PreviewChainResolution> resolved = source.Resolver.Resolve(
            sceneKey,
            occupiedSlots,
            (key, slot) => source.Directives.TryGet(key.NodeGuid, key.SceneIndex, slot, out string directive)
                ? directive
                : null);
        if (!resolved.Success || resolved.Value == null)
        {
            lines.Add(
                $"{Stamp()} preview-chain status=CHAIN_ONLY_FAILED; sequence={pending.WindowSequence}; "
                + $"detail={Escape(resolved.Error)}");
            return;
        }

        ApplyPreviewChainSlots(pending.WindowSequence, resolved.Value, sceneIdentity, lines);
    }

    /// <summary>
    /// FIX D: marks a chain-only pending's DataList generation as
    /// chain-covered via the atomic first-wins cache marker. Unknown or
    /// already-marked generations return false harmlessly.
    /// </summary>
    private static void TryMarkChainAppliedForPending(PendingEmbeddedPreview pending)
    {
        if (pending.ChainGenerationId != 0)
        {
            EmbeddedEditorPreviewLeaseCache.TryMarkGenerationChainApplied(
                pending.ChainGenerationId);
        }
    }

    private static PreviewChainApplyResult ApplyPreviewChainSlots(
        long windowSequence,
        PreviewChainResolution chain,
        string sceneIdentity,
        ICollection<string> lines)
    {
        var appliedSlots = new HashSet<int>();
        var residualMutationSlots = new HashSet<int>();
        foreach (PreviewChainSlotResolution slot in chain.Slots)
        {
            lines.Add(
                $"{Stamp()} preview-chain-slot sequence={windowSequence}; slot={slot.PublicSlot}; "
                + $"folded={slot.State.FoldedCommandCount}; stop={slot.StopReason}; "
                + $"inherited={slot.HasInheritedStart}; occupant={Escape(slot.ExpectedOccupant)}");
            if (!slot.HasInheritedStart || !slot.HasExpectedOccupant)
            {
                continue;
            }

            ApiResult<CharacterTransformInheritedStartSnapshot> applied =
                Plugin.Host.CharacterCommandsInternal.ApplyInheritedStartWithOriginOnMainThread(
                    sceneIdentity,
                    slot.PublicSlot,
                    slot.ExpectedOccupant,
                    slot.State,
                    slot.LineageIdentity);
            if (!applied.Success || applied.Value == null)
            {
                if (MayHaveResidualMutation(applied.Error))
                {
                    residualMutationSlots.Add(slot.PublicSlot);
                }

                // Chain application must never break the verified dispatch path;
                // the scene continues from its official positions instead.
                lines.Add(
                    $"{Stamp()} SCENE_CHAIN_FAILED sequence={windowSequence}; "
                    + $"slot={slot.PublicSlot}; reason={Escape(applied.Error)}; "
                    + "action=continue-without-chain");
                continue;
            }

            CharacterTransformInheritedStartSnapshot start = applied.Value;
            appliedSlots.Add(slot.PublicSlot);
            lines.Add(
                $"{Stamp()} SCENE_CHAIN_APPLIED sequence={windowSequence}; "
                + $"slot={start.PublicSlot}; occupant={Escape(start.OccupantIdentifier)}; "
                + $"beforeX={start.Before.Position.X:R}; startX={start.Applied.Position.X:R}; "
                + $"startY={start.Applied.Position.Y:R}; startRotZ={start.Applied.LocalEulerAngles.Z:R}; "
                + $"beforeRotY={start.Before.LocalEulerAngles.Y:R}; "
                + $"startRotY={start.Applied.LocalEulerAngles.Y:R}; "
                + $"flippedFromOfficial={slot.State.FlippedFromOfficial}; "
                + $"positionWritten={start.PositionWritten}; rotationWritten={start.RotationWritten}; "
                + $"baselineCaptured={start.BaselineCaptured}; "
                + $"lineage={Escape(slot.LineageIdentity)}");
        }

        return new PreviewChainApplyResult(appliedSlots, residualMutationSlots);
    }

    private static bool TryValidateEditorPreviewDirectives(
        IReadOnlyList<string> directives,
        out IReadOnlyList<EditorPreviewCommand> commands,
        out IReadOnlyList<EditorPreviewResource> resources,
        out string error)
    {
        commands = Array.Empty<EditorPreviewCommand>();
        resources = Array.Empty<EditorPreviewResource>();
        error = string.Empty;
        if (directives == null
            || directives.Count > EmbeddedAavtDirectiveExtractor.MaxCommandsPerScene)
        {
            error = "editor preview allows at most "
                + $"{EmbeddedAavtDirectiveExtractor.MaxCommandsPerScene} canonical resources";
            return false;
        }

        var commandList = new List<EditorPreviewCommand>(directives.Count);
        var resourceSet = new HashSet<EditorPreviewResource>();
        var occupiedSlots = new HashSet<int>();
        var characterCompiler = new CharacterTransformCommandFamilyCompiler();
        var pendingCompiler = new SlotPendingCommandFamilyCompiler();
        var cameraCompiler = new SceneCameraCommandFamilyCompiler();
        var spineCompiler = new SpineOverlayCommandFamilyCompiler();
        var presetCompiler = new CharacterPresetCommandFamilyCompiler();
        for (int index = 0; index < directives.Count; index++)
        {
            string directive = directives[index] ?? string.Empty;
            ICommandFamilyCompiler compiler;
            EditorPreviewCommandKind kind;
            if (directive.StartsWith(
                    "#char;",
                    StringComparison.Ordinal))
            {
                compiler = characterCompiler;
                kind = EditorPreviewCommandKind.Character;
            }
            else if (directive.StartsWith(
                         SlotPendingCommandFamilyCompiler.CanonicalRootToken + ";",
                         StringComparison.Ordinal))
            {
                compiler = pendingCompiler;
                kind = EditorPreviewCommandKind.SlotPending;
            }
            else if (directive.StartsWith(
                         SceneCameraCommandFamilyCompiler.CanonicalRootToken + ";",
                         StringComparison.Ordinal))
            {
                compiler = cameraCompiler;
                kind = EditorPreviewCommandKind.Camera;
            }
            else if (directive.StartsWith(
                         SpineOverlayCommandFamilyCompiler.CanonicalRootToken + ";",
                         StringComparison.Ordinal))
            {
                compiler = spineCompiler;
                kind = EditorPreviewCommandKind.SpineOverlay;
            }
            else if (directive.StartsWith(CharacterPresetCommandFamilyCompiler.CanonicalRootToken + ";",
                         StringComparison.Ordinal))
            {
                compiler = presetCompiler;
                kind = EditorPreviewCommandKind.CharacterPreset;
            }
            else
            {
                error = $"editor preview directive {index} has an unsupported command family";
                return false;
            }

            Result<CanonicalTimelineCommand> canonical = compiler.Canonicalize(directive);
            if (!canonical.Success
                || canonical.Value == null
                || !string.Equals(canonical.Value.Directive, directive, StringComparison.Ordinal))
            {
                error = $"editor preview directive {index} is not canonical: {canonical.Error}";
                return false;
            }

            int publicSlot = canonical.Value.PublicSlot;
            int trackIndex = kind == EditorPreviewCommandKind.SpineOverlay
                ? CommandResourceIdentity.SpineTrackOf(directive)
                : 0;
            if (kind == EditorPreviewCommandKind.SpineOverlay && trackIndex < 0)
            {
                error = $"editor preview directive {index} names no readable overlay track";
                return false;
            }

            EditorPreviewResource resource = kind switch
            {
                EditorPreviewCommandKind.Camera => new EditorPreviewResource(
                    RuntimeEditorPreviewResourceKind.Camera,
                    0),
                EditorPreviewCommandKind.SpineOverlay => new EditorPreviewResource(
                    RuntimeEditorPreviewResourceKind.SpineOverlay,
                    publicSlot,
                    trackIndex),
                EditorPreviewCommandKind.CharacterPreset => new EditorPreviewResource(
                    RuntimeEditorPreviewResourceKind.CharacterPreset,
                    publicSlot),
                _ => new EditorPreviewResource(
                    RuntimeEditorPreviewResourceKind.Character,
                    publicSlot)
            };
            if (kind == EditorPreviewCommandKind.Camera)
            {
                if (!resourceSet.Add(resource))
                {
                    error = "editor preview contains more than one scene camera resource";
                    return false;
                }
            }
            else if (kind is EditorPreviewCommandKind.SpineOverlay or EditorPreviewCommandKind.CharacterPreset)
            {
                // One overlay per track: two of them on the same track of one character would
                // fight for the same keys, while different tracks are exactly how several parts
                // are driven at once.
                if (!resourceSet.Add(resource))
                {
                    error = kind == EditorPreviewCommandKind.CharacterPreset
                        ? $"editor preview contains more than one character preset for slot {publicSlot}"
                        : "editor preview contains conflicting overlay resources for track "
                            + $"{trackIndex} of slot {publicSlot}";
                    return false;
                }
            }
            else if (!occupiedSlots.Add(publicSlot))
            {
                error = $"editor preview contains conflicting resources for character slot {publicSlot}";
                return false;
            }

            if (kind != EditorPreviewCommandKind.SlotPending)
            {
                resourceSet.Add(resource);
            }

            commandList.Add(new EditorPreviewCommand(index, kind, publicSlot, directive, trackIndex));
        }

        commands = Array.AsReadOnly(commandList.ToArray());
        resources = Array.AsReadOnly(resourceSet
            .OrderBy(resource => resource.Kind)
            .ThenBy(resource => resource.PublicSlot)
            .ToArray());
        return true;
    }

    private static void DrainDeferredEditorPreviews(
        PlayerCommandObservationStage stage,
        long update,
        ManagedDispatchPhase phase)
    {
        if (DeferredEditorPreviews.Count == 0)
        {
            return;
        }

        var lines = new List<string>();
        while (DeferredEditorPreviews.Count != 0
            && DeferredEditorPreviews.Peek().Schedule.IsDue(update, phase))
        {
            DeferredEditorPreview queued = DeferredEditorPreviews.Dequeue();
            if (stage != PlayerCommandObservationStage.SceneCommandDispatch)
            {
                lines.Add(
                    $"{Stamp()} editor-preview-dispatch status=DROP; sequence={queued.WindowSequence}; "
                    + "reason=scene-command-dispatch-disabled");
                continue;
            }

            ApiResult<PlayerRuntimeContextSnapshot> context =
                Plugin.Api.PlayerContext.ReadOnMainThread();
            ApiResult<EditorSceneSnapshot> currentScene = Plugin.Api.Editor.GetSelectedScene();
            string drift = string.Empty;
            if (!context.Success
                || context.Value == null
                || !EmbeddedEditorPreviewLeaseCache.IsCurrent(
                    queued.Lease,
                    context.Value,
                    Plugin.Api.SceneIdentity.Current,
                    currentScene,
                    out drift))
            {
                lines.Add(
                    $"{Stamp()} editor-preview-dispatch status=DROP; sequence={queued.WindowSequence}; "
                    + $"reason={Escape(context.Success ? drift : context.Error)}; writes=false");
                continue;
            }

            DispatchEditorPreview(queued, lines);
        }

        if (lines.Count != 0)
        {
            PlayerCommandObservationLog.Append(lines);
        }
    }

    private static void DispatchEditorPreview(
        DeferredEditorPreview queued,
        ICollection<string> lines)
    {
        EmbeddedEditorPreviewLease lease = queued.Lease;
        // Authority was revalidated immediately before entering this method.
        // Replaying the same selection starts from the current unmodified pose.
        Plugin.Host.CharacterPresetsInternal.ObserveDialogueBoundary(
            queued.WindowSequence, "authoritative-preview-dialogue");
        Plugin.Host.CharacterPresetsInternal.StopAll("authoritative-preview-replay");
        ActiveEditorPreview? previous = _activeEditorPreview;
        bool sameScene = previous != null
            && string.Equals(
                previous.SceneIdentity,
                lease.StableSceneIdentity,
                StringComparison.Ordinal);
        if (previous != null)
        {
            ActiveEditorPreview cleanupTarget = sameScene
                ? previous
                : CameraOnly(previous);
            bool cleaned = CleanupEditorPreviewResources(
                cleanupTarget,
                lines,
                strict: true);
            if (!sameScene)
            {
                int skippedCharacters = previous.Resources.Count(resource =>
                    resource.Kind == RuntimeEditorPreviewResourceKind.Character);
                ReleaseSpineOverlays(previous.SceneIdentity, lines);
                lines.Add(
                    $"{Stamp()} editor-preview-cleanup scope=scene-change; "
                    + $"skippedCharacterResources={skippedCharacters}; "
                    + "reason=old-scene-character-baseline-must-not-write-current-live-slots");
            }

            if (!cleaned)
            {
                lines.Add(
                    $"{Stamp()} editor-preview-dispatch status=BLOCKED; sequence={queued.WindowSequence}; "
                    + "reason=previous-preview-cleanup-failed; currentWrites=false");
                return;
            }

            _activeEditorPreview = null;
        }

        PreviewChainResolution? chain = TryResolveEditorPreviewChain(lease, queued.Commands, lines);
        var chainApply = PreviewChainApplyResult.Empty;
        if (chain != null)
        {
            chainApply = ApplyPreviewChainSlots(
                queued.WindowSequence,
                chain,
                lease.StableSceneIdentity,
                lines);
            // FIX D: first caller wins per (DataList generation, window). The
            // lease marks its own claimed window only after actually applying
            // the chain, so sibling windows collapse onto their window marker
            // while the green replay button's NEXT window (same generation —
            // replays never raise DataList) rebuilds the chain again.
            EmbeddedEditorPreviewLeaseCache.TryMarkGenerationChainApplied(
                lease.DataListGeneration,
                lease.ClaimedWindowSequence);
        }

        var activeResources = new HashSet<EditorPreviewResource>();
        if (chain != null)
        {
            foreach (PreviewChainSlotResolution slot in chain.Slots)
            {
                if (chainApply.AppliedSlots.Contains(slot.PublicSlot)
                    || chainApply.ResidualMutationSlots.Contains(slot.PublicSlot))
                {
                    activeResources.Add(new EditorPreviewResource(
                        RuntimeEditorPreviewResourceKind.Character,
                        slot.PublicSlot));
                }
            }
        }

        if (chainApply.ResidualMutationSlots.Count != 0)
        {
            FailEditorPreviewDispatch(
                queued,
                activeResources,
                "inherited chain left residual mutation risk in slots "
                    + string.Join(",", chainApply.ResidualMutationSlots.OrderBy(slot => slot)),
                lines);
            return;
        }

        // Camera inheritance (2026-09-18): playback keeps the live camera across
        // scenes, while the editor preview rebuilds the Back/Spine layers on
        // every click and therefore previews from the default composition.
        // Reproduce the playback composition BEFORE this scene's own commands so
        // a relative camera command plans from the same base it would in
        // playback. A scene with no ancestor camera command stays untouched.
        if (TryApplyInheritedCameraStart(lease, lines))
        {
            activeResources.Add(new EditorPreviewResource(
                RuntimeEditorPreviewResourceKind.Camera,
                SceneCameraCommandFamilyCompiler.SingletonResourceSlot));
        }

        for (int index = 0; index < queued.Commands.Count; index++)
        {
            EditorPreviewCommand command = queued.Commands[index];
            if (command.Kind == EditorPreviewCommandKind.SlotPending)
            {
                lines.Add(
                    $"{Stamp()} editor-preview-command status=DEFERRED_ONLY; "
                    + $"sequence={queued.WindowSequence}; order={command.Order}; "
                    + $"slot={command.PublicSlot}; reason=unsaved-slot-pending-does-not-enter-runtime-store");
                continue;
            }

            if (command.Kind == EditorPreviewCommandKind.Camera)
            {
                ApiResult<SceneCameraExecutionSnapshot> camera =
                    Plugin.Api.SceneCamera.ExecuteOnMainThread(
                        lease.StableSceneIdentity,
                        command.CanonicalDirective);
                if (!camera.Success || camera.Value == null)
                {
                    if (MayHaveResidualMutation(camera.Error))
                    {
                        activeResources.Add(new EditorPreviewResource(
                            RuntimeEditorPreviewResourceKind.Camera,
                            command.PublicSlot));
                    }

                    FailEditorPreviewDispatch(
                        queued,
                        activeResources,
                        $"camera command failed: {camera.Error}",
                        lines);
                    return;
                }

                activeResources.Add(new EditorPreviewResource(
                    RuntimeEditorPreviewResourceKind.Camera,
                    command.PublicSlot));

                // Mirrors the playback SCENE_CAMERA_DISPATCHED fields so the
                // preview path can be accepted with the same criteria
                // (NOTES_SceneCamera.md: prior/entry state, replay restore, and
                // the fact that only Back+Spine were written).
                SceneCameraExecutionSnapshot cameraExecution = camera.Value;
                lines.Add(
                    $"{Stamp()} editor-preview-camera status=DISPATCHED; "
                    + $"sequence={queued.WindowSequence}; order={command.Order}; "
                    + $"operation={cameraExecution.Command.Operation}; "
                    + $"durationMs={cameraExecution.Command.DurationMilliseconds}; "
                    + $"easing={cameraExecution.Command.Easing}; "
                    + $"positionChanged={cameraExecution.PositionChanged}; "
                    + $"zoomChanged={cameraExecution.ZoomChanged}; "
                    + $"baselineCaptured={cameraExecution.BaselineCaptured}; "
                    + $"replayRestored={cameraExecution.ReplayRestored}; "
                    + $"isReset={cameraExecution.IsReset}; "
                    + $"beforeX={cameraExecution.Before.X:R}; beforeY={cameraExecution.Before.Y:R}; "
                    + $"beforeZoom={cameraExecution.Before.Zoom:R}; "
                    + $"targetX={cameraExecution.Target.X:R}; targetY={cameraExecution.Target.Y:R}; "
                    + $"targetZoom={cameraExecution.Target.Zoom:R}; "
                    + "layers=Back+Spine; frontUIWritten=false");
                continue;
            }

            if (command.Kind == EditorPreviewCommandKind.CharacterPreset)
            {
                ApiResult<bool> preset = Plugin.Host.CharacterPresetsInternal.Begin(
                    lease.StableSceneIdentity, command.CanonicalDirective, queued.WindowSequence);
                if (preset.Success)
                {
                    activeResources.Add(new EditorPreviewResource(
                        RuntimeEditorPreviewResourceKind.CharacterPreset, command.PublicSlot));
                }
                lines.Add($"{Stamp()} editor-preview-preset status={(preset.Success ? "DISPATCHED" : "FAILED")}; "
                    + $"sequence={queued.WindowSequence}; order={command.Order}; slot={command.PublicSlot}; "
                    + $"reason={Escape(preset.Success ? string.Empty : preset.Error)}; pivot=existing-character-root; "
                    + "footPivotVerified=false; action=continue");
                continue;
            }

            if (command.Kind == EditorPreviewCommandKind.SpineOverlay)
            {
                // Overlays are sticky inside a scene, exactly as in playback, so this path never
                // records an active resource: the transition between two cards of one scene must
                // not release anything (see ReleaseSpineOverlays for the scene-change case). The
                // engine's own scene rebuild is what ends them otherwise.
                Result<SpineOverlayCommand> overlay =
                    new SpineOverlayDirectiveParser().Parse(command.CanonicalDirective);
                if (!overlay.Success || overlay.Value == null)
                {
                    lines.Add(
                        $"{Stamp()} editor-preview-spine status=SKIPPED; "
                        + $"sequence={queued.WindowSequence}; order={command.Order}; "
                        + $"slot={command.PublicSlot}; track={command.TrackIndex}; "
                        + $"reason={Escape(overlay.Error)}");
                    continue;
                }

                ModResult<ISpineOverlayCommandService> provider = GetSpineOverlayProvider();
                if (!provider.Success || provider.Value == null)
                {
                    lines.Add(
                        $"{Stamp()} editor-preview-spine status=UNAVAILABLE; "
                        + $"sequence={queued.WindowSequence}; order={command.Order}; "
                        + $"slot={command.PublicSlot}; track={command.TrackIndex}; "
                        + $"reason={Escape(provider.Error?.Message ?? string.Empty)}");
                    continue;
                }

                SpineOverlayCommand parsed = overlay.Value;
                ModResult<SpineOverlayExecutionSnapshot> overlayApplied = provider.Value.ApplyOverlay(
                    new SpineOverlayRequest(
                        lease.StableSceneIdentity,
                        parsed.PublicSlot,
                        parsed.Operation == SpineOverlayOperation.Clear
                            ? SpineOverlayRequestKind.Clear
                            : SpineOverlayRequestKind.Play,
                        parsed.AnimationName,
                        parsed.TrackIndex,
                        parsed.MixMilliseconds,
                        parsed.FadeMilliseconds,
                        parsed.Loop,
                        parsed.Blend == SpineOverlayBlend.Add,
                        parsed.Hold));
                if (!overlayApplied.Success || overlayApplied.Value == null)
                {
                    lines.Add(
                        $"{Stamp()} editor-preview-spine status=FAILED; "
                        + $"sequence={queued.WindowSequence}; order={command.Order}; "
                        + $"slot={parsed.PublicSlot}; track={parsed.TrackIndex}; "
                        + $"reason={Escape(overlayApplied.Error?.Message ?? string.Empty)}; action=continue");
                    continue;
                }

                SpineOverlayExecutionSnapshot overlayExecution = overlayApplied.Value;
                lines.Add(
                    $"{Stamp()} editor-preview-spine status=DISPATCHED; "
                    + $"sequence={queued.WindowSequence}; order={command.Order}; "
                    + $"slot={overlayExecution.PublicSlot}; operation={overlayExecution.Operation}; "
                    + $"animation={Escape(overlayExecution.AnimationName)}; "
                    + $"track={overlayExecution.TrackIndex}; applied={overlayExecution.Applied}; "
                    + $"detail={Escape(overlayExecution.Detail)}");
                continue;
            }

            var request = new CharacterTransformDispatchRequest(
                lease.StableSceneIdentity,
                command.CanonicalDirective,
                CharacterCommandSource.PlaybackSidecar,
                OriginIdentity: OriginIdentityFor(chain, command.PublicSlot));
            ApiResult<CharacterTransformDispatchSnapshot> dispatched =
                Plugin.Api.CharacterCommands.DispatchOnMainThread(request);
            if (!dispatched.Success || dispatched.Value == null)
            {
                if (MayHaveResidualMutation(dispatched.Error))
                {
                    activeResources.Add(new EditorPreviewResource(
                        RuntimeEditorPreviewResourceKind.Character,
                        command.PublicSlot));
                }

                FailEditorPreviewDispatch(
                    queued,
                    activeResources,
                    $"character slot {command.PublicSlot} failed: {dispatched.Error}",
                    lines);
                return;
            }

            activeResources.Add(new EditorPreviewResource(
                RuntimeEditorPreviewResourceKind.Character,
                command.PublicSlot));

            CharacterTransformExecutionSnapshot execution = dispatched.Value.Execution;
            lines.Add(
                $"{Stamp()} editor-preview-character status=DISPATCHED; "
                + $"sequence={queued.WindowSequence}; order={command.Order}; "
                + $"slot={command.PublicSlot}; operation={execution.Command.Operation}; "
                + $"targetX={execution.Target.Position.X:R}; targetY={execution.Target.Position.Y:R}; "
                + $"targetRotY={execution.Target.LocalEulerAngles.Y:R}; "
                + $"targetScreenRotZ={execution.Target.LocalEulerAngles.Z:R}");
        }

        _activeEditorPreview = new ActiveEditorPreview(
            lease.StableSceneIdentity,
            Array.AsReadOnly(activeResources
                .OrderBy(resource => resource.Kind)
                .ThenBy(resource => resource.PublicSlot)
                .ToArray()));
        lines.Add(
            $"{Stamp()} editor-preview-dispatch status=COMPLETE; "
            + $"sequence={queued.WindowSequence}; commands={queued.Commands.Count}; "
            + $"activeResources={activeResources.Count}; tombstone={lease.IsTombstone}; "
            + "archiveWrites=false; mappingRequired=false");
    }

    private static PreviewChainResolution? TryResolveEditorPreviewChain(
        EmbeddedEditorPreviewLease lease,
        IReadOnlyList<EditorPreviewCommand> commands,
        ICollection<string> lines)
    {
        // Lease application is editor-preview by definition: the live graph
        // is the preferred source so unsaved ancestors and unsaved scenes
        // resolve; disk remains the fallback when the live build fails.
        PreviewChainSource? source = EffectiveChainSource(preferLiveEditorGraph: true);
        if (source == null)
        {
            lines.Add(
                $"{Stamp()} editor-preview-chain status=SKIP; reason=chain-source-unavailable");
            return null;
        }

        StoryNodeSnapshot? node = source.Project.Nodes.FirstOrDefault(candidate =>
            string.Equals(candidate.NodeGuid, lease.Scene.NodeGuid, StringComparison.Ordinal));
        if (node == null
            || lease.Scene.SceneIndex < 0
            || lease.Scene.SceneIndex >= node.Scenes.Count)
        {
            lines.Add(
                $"{Stamp()} editor-preview-chain status=SKIP; reason=live-scene-not-in-loaded-project; "
                + $"node={Escape(lease.Scene.NodeGuid)}; scene={lease.Scene.SceneIndex}");
            return null;
        }

        var slots = new SortedSet<int>(commands
            .Where(command => command.Kind is EditorPreviewCommandKind.Character or EditorPreviewCommandKind.SlotPending)
            .Select(command => command.PublicSlot));
        foreach (ProjectCharacterSnapshot character in node.Scenes[lease.Scene.SceneIndex].Characters)
        {
            if (character.PhysicalSlot is >= 1 and <= 5)
            {
                slots.Add(character.PhysicalSlot);
            }
        }

        // The 24-hex live editor fingerprint belongs to the one-shot lease.
        // Lineage identity must use the canonical project SceneKey so an
        // official-move boundary has the same identity in this scene and its
        // descendants.
        SceneKey sceneKey = node.Scenes[lease.Scene.SceneIndex].Key;
        Result<PreviewChainResolution> resolved = source.Resolver.Resolve(
            sceneKey,
            slots,
            (key, slot) =>
            {
                EmbeddedEditorPreviewLeaseCache.TryGetOverlayDirective(
                    lease.Scene.ProjectKey,
                    key.NodeGuid,
                    key.SceneIndex,
                    slot,
                    out bool authoritative,
                    out string? overlayDirective);
                if (authoritative)
                {
                    return overlayDirective;
                }

                return source.Directives.TryGet(
                    key.NodeGuid,
                    key.SceneIndex,
                    slot,
                    out string directive)
                        ? directive
                        : null;
            });
        if (!resolved.Success || resolved.Value == null)
        {
            lines.Add(
                $"{Stamp()} editor-preview-chain status=SKIP; reason=resolver-failed; "
                + $"detail={Escape(resolved.Error)}");
            return null;
        }

        lines.Add(
            $"{Stamp()} editor-preview-chain status=READY; "
            + $"slots={resolved.Value.Slots.Count}; overlayProject={Escape(lease.Scene.ProjectKey)}");
        return resolved.Value;
    }

    /// <summary>
    /// Binds the open project pair from a PLAYBACK advance window (2026-09-18).
    /// <para>
    /// The pair is normally discovered from the editor selection, so launching
    /// the game and going straight into playback left the index at
    /// <c>status=WAITING; reason=auto-discover-no-active-pair</c> and every
    /// window reported <c>NO_INDEX</c> — nothing dispatched at all. A playing
    /// card carries exactly the same anchor the editor provides (the compiled
    /// script identity, plus the engine row index), so the pair can be bound
    /// from the first observed playback window instead.
    /// </para>
    /// <para>
    /// Cheap by construction: it runs only while no pair is bound and no index
    /// exists, and <see cref="ActiveProjectPairSource.ResolveActive"/> reuses
    /// its stamped scan and snapshot caches. Once a pair is bound the normal
    /// index load takes over on the next update; the current window still stays
    /// a no-op, the following cards dispatch.
    /// </para>
    /// </summary>
    private static void TryDiscoverPairFromPlaybackWindow(
        ApiResult<PlayerRuntimeContextSnapshot>? contextResult,
        ClosedAdvanceWindow window,
        ICollection<string> lines)
    {
        if (_index != null
            || contextResult?.Success != true
            || contextResult.Value == null
            || contextResult.Value.PreviewMode
            || window.PlaybackRowIndex < 0
            || window.ManagedUnityMessages.Count == 0
            || !ActiveProjectPairSource.Enabled
            || ActiveProjectPairSource.HasActivePair)
        {
            return;
        }

        if (Interlocked.Increment(ref _playbackPairDiscoveryAttempts) > MaximumPlaybackPairDiscoveryAttempts)
        {
            LogOnce(
                "index-load-playback-discovery-exhausted",
                $"{Stamp()} playback-pair-discovery status=EXHAUSTED; "
                + $"attempts={MaximumPlaybackPairDiscoveryAttempts}; "
                + "no data-root pair matched the playing script; the index stays unavailable");
            return;
        }

        CompiledScriptIdentity identity =
            CommandIdentity.CompiledScript(window.ManagedUnityMessages[0]);
        Result<ActivePairBinding> binding = ActiveProjectPairSource.ResolveActive(
            new ObservedCompiledSceneIdentity(
                identity.Sha256,
                identity.Utf16Length,
                identity.LineCount)
            {
                PlaybackRowIndex = window.PlaybackRowIndex
            });
        lines.Add(
            $"{Stamp()} playback-pair-discovery sequence={window.Sequence}; "
            + $"row={window.PlaybackRowIndex}; "
            + $"scriptSha16={Short16(identity.Sha256)}; success={binding.Success}; "
            + (binding.Success && binding.Value != null
                ? $"pair={Escape(binding.Value.Pair.Name)}"
                : $"reason={Escape(binding.Error)}"));
        if (!binding.Success)
        {
            return;
        }

        // The pair changed, so drop anything the previous pair left behind and
        // let StartOrConsumeIndexLoad queue the load for the new one.
        RequestIndexReload();
    }

    private static int _playbackPairDiscoveryAttempts;
    private const int MaximumPlaybackPairDiscoveryAttempts = 8;

    /// <summary>
    /// Applies the folded ancestor camera composition as this scene's entry
    /// state before its own commands run, so the preview shows what sequential
    /// playback would show. Editor preview only: formal playback keeps the live
    /// camera by itself and must not be re-written from the graph. Fail-soft: an
    /// unavailable graph, scene or camera service leaves the scene on its
    /// official composition instead of blocking the dispatch.
    /// </summary>
    private static bool TryApplyInheritedCameraStart(
        EmbeddedEditorPreviewLease lease,
        ICollection<string> lines)
    {
        if (_options?.PreviewCameraChainEnabled != true)
        {
            return false;
        }

        PreviewChainSource? source = EffectiveChainSource(preferLiveEditorGraph: true);
        if (source == null)
        {
            lines.Add(
                $"{Stamp()} editor-preview-camera-chain status=SKIP; "
                + "reason=chain-source-unavailable");
            return false;
        }

        StoryNodeSnapshot? node = source.Project.Nodes.FirstOrDefault(candidate =>
            string.Equals(candidate.NodeGuid, lease.Scene.NodeGuid, StringComparison.Ordinal));
        if (node == null
            || lease.Scene.SceneIndex < 0
            || lease.Scene.SceneIndex >= node.Scenes.Count)
        {
            lines.Add(
                $"{Stamp()} editor-preview-camera-chain status=SKIP; "
                + "reason=live-scene-not-in-loaded-project; "
                + $"node={Escape(lease.Scene.NodeGuid)}; scene={lease.Scene.SceneIndex}");
            return false;
        }

        SceneKey sceneKey = node.Scenes[lease.Scene.SceneIndex].Key;
        Result<PreviewCameraChainResolution> resolved = source.CameraChains.Resolve(
            sceneKey,
            key =>
            {
                if (EmbeddedEditorPreviewLeaseCache.TryGetOverlayDirective(
                        lease.Scene.ProjectKey,
                        key.NodeGuid,
                        key.SceneIndex,
                        SceneCameraCommandFamilyCompiler.SingletonResourceSlot,
                        out bool authoritative,
                        out string? overlayDirective))
                {
                    return authoritative ? overlayDirective : null;
                }

                return source.Directives.TryGetCamera(
                    key.NodeGuid,
                    key.SceneIndex,
                    out string indexDirective)
                        ? indexDirective
                        : null;
            });
        if (!resolved.Success || resolved.Value == null)
        {
            lines.Add(
                $"{Stamp()} editor-preview-camera-chain status=SKIP; reason=resolver-failed; "
                + $"detail={Escape(resolved.Error)}");
            return false;
        }

        PreviewCameraChainResolution folded = resolved.Value;
        lines.Add(
            $"{Stamp()} editor-preview-camera-chain status=READY; "
            + $"folded={folded.FoldedCommandCount}; stop={folded.StopReason}; "
            + $"state=({folded.InheritedState.X:R},{folded.InheritedState.Y:R},"
            + $"{folded.InheritedState.Zoom:R})");
        if (!folded.HasInheritedStart)
        {
            return false;
        }

        ApiResult<SceneCameraInheritedStartSnapshot> applied =
            Plugin.Host.SceneCameraCommandsInternal.ApplyInheritedStartOnMainThread(
                lease.StableSceneIdentity,
                folded.InheritedState);
        if (!applied.Success || applied.Value == null)
        {
            lines.Add(
                $"{Stamp()} editor-preview-camera-chain status=FAILED; "
                + $"reason={Escape(applied.Error)}; action=continue-without-inherited-camera");
            return false;
        }

        SceneCameraInheritedStartSnapshot start = applied.Value;
        lines.Add(
            $"{Stamp()} editor-preview-camera-chain status=APPLIED; "
            + $"folded={folded.FoldedCommandCount}; stop={folded.StopReason}; "
            + $"beforeX={start.Before.X:R}; beforeY={start.Before.Y:R}; beforeZoom={start.Before.Zoom:R}; "
            + $"startX={start.Applied.X:R}; startY={start.Applied.Y:R}; startZoom={start.Applied.Zoom:R}; "
            + $"baselineCaptured={start.BaselineCaptured}");
        return true;
    }

    private static bool CleanupEditorPreviewResources(
        ActiveEditorPreview active,
        ICollection<string> lines,
        bool strict)
    {
        bool success = true;
        foreach (EditorPreviewResource resource in active.Resources)
        {
            if (resource.Kind == RuntimeEditorPreviewResourceKind.CharacterPreset)
            {
                Plugin.Host.CharacterPresetsInternal.StopScene(active.SceneIdentity, "preview-resource-cleanup");
                continue;
            }
            if (resource.Kind == RuntimeEditorPreviewResourceKind.Camera)
            {
                ApiResult<SceneCameraExecutionSnapshot> reset =
                    Plugin.Api.SceneCamera.ExecuteOnMainThread(
                        active.SceneIdentity,
                        "#camera;reset;duration=0;easing=linear");
                bool restored = reset.Success && reset.Value != null;
                success &= restored;
                lines.Add(
                    $"{Stamp()} editor-preview-cleanup resource=camera; success={restored}; "
                    + $"reason={Escape(restored ? string.Empty : reset.Error)}");
                continue;
            }

            var request = new CharacterTransformDispatchRequest(
                active.SceneIdentity,
                $"#char;{resource.PublicSlot};reset;duration=0;easing=linear",
                CharacterCommandSource.PlaybackSidecar);
            ApiResult<CharacterTransformDispatchSnapshot> resetCharacter =
                Plugin.Api.CharacterCommands.DispatchOnMainThread(request);
            bool characterRestored = resetCharacter.Success && resetCharacter.Value != null;
            if (strict)
            {
                success &= characterRestored;
            }

            lines.Add(
                $"{Stamp()} editor-preview-cleanup resource=character; "
                + $"slot={resource.PublicSlot}; success={characterRestored}; strict={strict}; "
                + $"reason={Escape(characterRestored ? string.Empty : resetCharacter.Error)}");
        }

        return success;
    }

    private static void FailEditorPreviewDispatch(
        DeferredEditorPreview queued,
        IEnumerable<EditorPreviewResource> resources,
        string reason,
        ICollection<string> lines)
    {
        var failed = new ActiveEditorPreview(
            queued.Lease.StableSceneIdentity,
            Array.AsReadOnly(resources.Distinct().ToArray()));
        bool cleaned = CleanupEditorPreviewResources(failed, lines, strict: true);
        _activeEditorPreview = cleaned ? null : failed;
        lines.Add(
            $"{Stamp()} editor-preview-dispatch status=FAILED; "
            + $"sequence={queued.WindowSequence}; reason={Escape(reason)}; "
            + $"cleanupAttempted=true; cleanupSucceeded={cleaned}; "
            + $"cleanupHandleRetained={!cleaned}; persistedFallback=false");
    }

    private static void MaintainEditorPreviewMode(PlayerCommandObservationStage stage)
    {
        if (stage != PlayerCommandObservationStage.SceneCommandDispatch)
        {
            StopCharacterPresets("scene-dispatch-disabled");
            return;
        }

        ApiResult<PlayerRuntimeContextSnapshot> context =
            Plugin.Api.PlayerContext.ReadOnMainThread();
        if (!context.Success || context.Value == null)
        {
            StopCharacterPresets("player-context-unavailable");
            return;
        }

        PlayerRuntimeMode current = context.Value.Mode;
        if (current != _lastObservedPlayerMode || current == PlayerRuntimeMode.Unavailable)
        {
            StopCharacterPresets("player-mode-changed-or-exited");
        }
        if (current == PlayerRuntimeMode.EditorPreview
            && EmbeddedEditorPreviewLeaseCache.TryGetCurrentGeneration(out DataListGenerationScope presetScope)
            && presetScope.GenerationId != _lastPresetSelectionGeneration)
        {
            // Selection generations are an existing managed signal, including
            // empty ordinary rows that intentionally have no command lease.
            StopCharacterPresets("editor-selection-generation-changed");
            _lastPresetSelectionGeneration = presetScope.GenerationId;
        }
        if (_lastObservedPlayerMode == PlayerRuntimeMode.EditorPreview
            && current == PlayerRuntimeMode.Playback
            && _activeEditorPreview != null)
        {
            var lines = new List<string>();
            ActiveEditorPreview cameraOnly = CameraOnly(_activeEditorPreview);
            bool cleaned = CleanupEditorPreviewResources(
                cameraOnly,
                lines,
                strict: true);
            lines.Add(
                $"{Stamp()} editor-preview-mode-exit cleanup={cleaned}; "
                + $"cameraResources={cameraOnly.Resources.Count}; "
                + $"skippedCharacterResources={_activeEditorPreview.Resources.Count - cameraOnly.Resources.Count}; "
                + "nextMode=Playback; archiveWrites=false");
            PlayerCommandObservationLog.Append(lines);
            if (!cleaned)
            {
                // Keep the camera resource and the previous-mode marker so the
                // next Update retries instead of losing the only safe cleanup handle.
                _activeEditorPreview = cameraOnly;
                return;
            }

            _activeEditorPreview = null;
        }
        else if (current == PlayerRuntimeMode.Unavailable)
        {
            _activeEditorPreview = null;
        }

        _lastObservedPlayerMode = current;
    }

    private static ActiveEditorPreview CameraOnly(ActiveEditorPreview active) => new(
        active.SceneIdentity,
        Array.AsReadOnly(active.Resources
            .Where(resource => resource.Kind == RuntimeEditorPreviewResourceKind.Camera)
            .ToArray()));

    private static bool MayHaveResidualMutation(string error) =>
        error?.Contains(
            RuntimeMutationFailure.ResidualMutationMarker,
            StringComparison.Ordinal) == true;

    private static void DrainDeferredBatches(
        PlayerCommandObservationStage stage,
        long update,
        ManagedDispatchPhase phase)
    {
        if (DeferredBatches.Count == 0)
        {
            return;
        }

        var lines = new List<string>();
        while (DeferredBatches.Count != 0
            && DeferredBatches.Peek().Schedule.IsDue(update, phase))
        {
            DeferredCommandBatch queued = DeferredBatches.Dequeue();
            if (IsDispatchCanaryStage(stage))
            {
                DispatchCanary(queued, lines);
                continue;
            }

            if (stage == PlayerCommandObservationStage.SceneCommandDispatch)
            {
                DispatchSceneCommands(queued, lines);
                continue;
            }

            foreach (PlaybackCommandInstruction command in queued.Batch.Commands)
            {
                lines.Add(
                    $"{Stamp()} NO_OP sequence={queued.WindowSequence}; "
                    + $"record={queued.Batch.PlaybackRecordIndex}; commandId={command.CommandId}; "
                    + $"order={command.Order}; directive={Escape(command.CanonicalDirective)}; "
                    + "dispatcherCalled=false; transformWritten=false");
            }
        }

        if (lines.Count != 0)
        {
            PlayerCommandObservationLog.Append(lines);
        }
    }

    private static void DispatchCanary(
        DeferredCommandBatch queued,
        ICollection<string> lines)
    {
        PlaybackDispatchCanaryGate? gate = _dispatchCanaryGate;
        if (gate == null)
        {
            lines.Add(
                $"{Stamp()} DISPATCH_BLOCKED sequence={queued.WindowSequence}; "
                + "reason=canary-gate-unavailable; dispatcherCalled=false");
            return;
        }

        Result<PlaybackDispatchAuthorization> authorized =
            gate.TryAuthorize(queued.Batch);
        if (!authorized.Success || authorized.Value == null)
        {
            lines.Add(
                $"{Stamp()} DISPATCH_BLOCKED sequence={queued.WindowSequence}; "
                + $"record={queued.Batch.PlaybackRecordIndex}; reason={Escape(authorized.Error)}; "
                + "dispatcherCalled=false");
            return;
        }

        PlaybackDispatchAuthorization authorization = authorized.Value;
        lines.Add(
            $"{Stamp()} DISPATCH_AUTHORIZED sequence={queued.WindowSequence}; "
            + $"ordinal={authorization.ExecutionOrdinal}; record={authorization.PlaybackRecordIndex}; "
            + $"commandId={authorization.CommandId}; dispatcherCallPending=true");

        var request = new CharacterTransformDispatchRequest(
            authorization.SceneIdentity,
            authorization.CanonicalDirective,
            CharacterCommandSource.PlaybackSidecar,
            OriginIdentity: OriginIdentityFor(
                queued.Chain,
                authorization.CanonicalDirective));
        ApiResult<CharacterTransformDispatchSnapshot> dispatched =
            Plugin.Api.CharacterCommands.DispatchOnMainThread(request);
        if (!dispatched.Success || dispatched.Value == null)
        {
            lines.Add(
                $"{Stamp()} DISPATCH_FAILED sequence={queued.WindowSequence}; "
                + $"record={authorization.PlaybackRecordIndex}; commandId={authorization.CommandId}; "
                + $"reason={Escape(dispatched.Error)}; retry=false; canaryBudgetConsumed=true");
            return;
        }

        CharacterTransformDispatchSnapshot dispatch = dispatched.Value;
        CharacterTransformExecutionSnapshot execution = dispatch.Execution;
        lines.Add(
            $"{Stamp()} DISPATCHED sequence={queued.WindowSequence}; "
            + $"record={authorization.PlaybackRecordIndex}; commandId={authorization.CommandId}; "
            + $"dispatchSequence={dispatch.Sequence}; source={dispatch.Source}; "
            + $"slot={execution.Command.PublicSlot}; occupant={Escape(execution.OccupantIdentifier)}; "
            + $"operation={execution.Command.Operation}; durationMs={execution.Command.DurationMilliseconds}; "
            + $"easing={execution.Command.Easing}; positionChanged={execution.PositionChanged}; "
            + $"rotationChanged={execution.RotationChanged}; baselineCaptured={execution.BaselineCaptured}; "
            + $"beforeX={execution.Before.Position.X:R}; targetX={execution.Target.Position.X:R}; "
            + "retry=false; canaryBudgetConsumed=true");
    }

    private static void DispatchSceneCommands(
        DeferredCommandBatch queued,
        ICollection<string> lines)
    {
        PlaybackSceneDispatchGate? gate = _sceneDispatchGate;
        if (gate == null)
        {
            lines.Add(
                $"{Stamp()} SCENE_DISPATCH_BLOCKED sequence={queued.WindowSequence}; "
                + "reason=scene-gate-unavailable; dispatcherCalled=false");
            return;
        }

        Result<PlaybackSceneDispatchAuthorization> authorized =
            gate.TryAuthorize(queued.WindowSequence, queued.Batch);
        if (!authorized.Success || authorized.Value == null)
        {
            lines.Add(
                $"{Stamp()} SCENE_DISPATCH_BLOCKED sequence={queued.WindowSequence}; "
                + $"record={queued.Batch.PlaybackRecordIndex}; reason={Escape(authorized.Error)}; "
                + "dispatcherCalled=false");
            return;
        }

        PlaybackSceneDispatchAuthorization authorization = authorized.Value;
        Plugin.Host.CharacterPresetsInternal.ObserveDialogueBoundary(
            authorization.WindowSequence, "authorized-scene-dialogue");
        lines.Add(
            $"{Stamp()} SCENE_DISPATCH_AUTHORIZED sequence={authorization.WindowSequence}; "
            + $"ordinal={authorization.ExecutionOrdinal}; record={authorization.PlaybackRecordIndex}; "
            + $"commands={authorization.Commands.Count}; consumedBeforeDispatch=true");

        if (queued.ApplyResolvedChain && queued.Chain != null)
        {
            ApplyPreviewChain(authorization, queued.Chain, lines);
            // FIX D/E: only editor-preview batches carry a DataList
            // generation id; formal playback batches carry 0 and apply the
            // same chain machinery without ever consulting or mutating editor
            // generation state. The planner is idempotent, so playback never
            // needs its own suppression marker.
            if (queued.ChainGenerationId != 0)
            {
                EmbeddedEditorPreviewLeaseCache.TryMarkGenerationChainApplied(
                    queued.ChainGenerationId);
            }
        }

        for (int index = 0; index < authorization.Commands.Count; index++)
        {
            PlaybackCommandInstruction command = authorization.Commands[index];
            if (string.Equals(command.CommandType, CharacterPresetCommandFamilyCompiler.CommandTypeId,
                    StringComparison.Ordinal))
            {
                ApiResult<bool> preset = Plugin.Host.CharacterPresetsInternal.Begin(
                    authorization.SceneIdentity, command.CanonicalDirective, authorization.WindowSequence);
                lines.Add($"{Stamp()} CHARACTER_PRESET_{(preset.Success ? "DISPATCHED" : "FAILED")} "
                    + $"sequence={authorization.WindowSequence}; record={authorization.PlaybackRecordIndex}; "
                    + $"commandId={command.CommandId}; order={command.Order}; "
                    + $"reason={Escape(preset.Success ? string.Empty : preset.Error)}; "
                    + "pivot=existing-character-root; footPivotVerified=false; action=continue");
                continue;
            }
            if (string.Equals(
                    command.CommandType,
                    SlotPendingCommandFamilyCompiler.CommandTypeId,
                    StringComparison.Ordinal))
            {
                ApiResult<SlotPendingStoredSnapshot> stored =
                    Plugin.Api.CharacterCommands.StoreSlotPendingOnMainThread(
                        authorization.SceneIdentity,
                        command.CanonicalDirective,
                        authorization.WindowSequence);
                if (!stored.Success || stored.Value == null)
                {
                    lines.Add(
                        $"{Stamp()} SLOT_PENDING_STORE_FAILED sequence={authorization.WindowSequence}; "
                        + $"record={authorization.PlaybackRecordIndex}; commandId={command.CommandId}; "
                        + $"order={command.Order}; "
                        + $"reason={Escape(stored.Error)}; action=continue");
                    continue;
                }

                lines.Add(
                    $"{Stamp()} SLOT_PENDING_STORED sequence={authorization.WindowSequence}; "
                    + $"record={authorization.PlaybackRecordIndex}; commandId={command.CommandId}; "
                    + $"order={command.Order}; slot={stored.Value.PublicSlot}; "
                    + $"directive={Escape(stored.Value.CanonicalDirective)}; "
                    + $"createdWindow={stored.Value.CreatedWindowSequence}; "
                    + "expiryWindows=16");
                continue;
            }

            if (string.Equals(
                    command.CommandType,
                    SceneCameraCommandFamilyCompiler.CommandTypeId,
                    StringComparison.Ordinal))
            {
                ApiResult<SceneCameraExecutionSnapshot> camera =
                    Plugin.Api.SceneCamera.ExecuteOnMainThread(
                        authorization.SceneIdentity,
                        command.CanonicalDirective);
                if (!camera.Success || camera.Value == null)
                {
                    lines.Add(
                        $"{Stamp()} SCENE_CAMERA_FAILED sequence={authorization.WindowSequence}; "
                        + $"record={authorization.PlaybackRecordIndex}; commandId={command.CommandId}; "
                        + $"order={command.Order}; reason={Escape(camera.Error)}; retry=false");
                    for (int skipped = index + 1;
                        skipped < authorization.Commands.Count;
                        skipped++)
                    {
                        PlaybackCommandInstruction remaining =
                            authorization.Commands[skipped];
                        lines.Add(
                            $"{Stamp()} SCENE_COMMAND_SKIPPED sequence={authorization.WindowSequence}; "
                            + $"record={authorization.PlaybackRecordIndex}; commandId={remaining.CommandId}; "
                            + $"order={remaining.Order}; reason=prior-command-failed; retry=false");
                    }

                    return;
                }

                SceneCameraExecutionSnapshot cameraExecution = camera.Value;
                lines.Add(
                    $"{Stamp()} SCENE_CAMERA_DISPATCHED sequence={authorization.WindowSequence}; "
                    + $"record={authorization.PlaybackRecordIndex}; commandId={command.CommandId}; "
                    + $"order={command.Order}; operation={cameraExecution.Command.Operation}; "
                    + $"durationMs={cameraExecution.Command.DurationMilliseconds}; "
                    + $"easing={cameraExecution.Command.Easing}; "
                    + $"positionChanged={cameraExecution.PositionChanged}; zoomChanged={cameraExecution.ZoomChanged}; "
                    + $"baselineCaptured={cameraExecution.BaselineCaptured}; "
                    + $"replayRestored={cameraExecution.ReplayRestored}; isReset={cameraExecution.IsReset}; "
                    + $"beforeX={cameraExecution.Before.X:R}; beforeY={cameraExecution.Before.Y:R}; "
                    + $"beforeZoom={cameraExecution.Before.Zoom:R}; targetX={cameraExecution.Target.X:R}; "
                    + $"targetY={cameraExecution.Target.Y:R}; targetZoom={cameraExecution.Target.Zoom:R}; "
                    + "layers=Back+Spine; frontUIWritten=false; retry=false");
                continue;
            }

            if (string.Equals(
                    command.CommandType,
                    SpineOverlayCommandFamilyCompiler.CommandTypeId,
                    StringComparison.Ordinal))
            {
                // Sticky by contract: the overlay stays on its reserved track until a clear
                // directive or the scene rebuild. Failures here are per-command authoring
                // problems, so they are reported and skipped — unlike a camera failure they must
                // not cost the rest of the card.
                Result<SpineOverlayCommand> overlay =
                    new SpineOverlayDirectiveParser().Parse(command.CanonicalDirective);
                if (!overlay.Success || overlay.Value == null)
                {
                    lines.Add(
                        $"{Stamp()} SPINE_OVERLAY_SKIPPED sequence={authorization.WindowSequence}; "
                        + $"record={authorization.PlaybackRecordIndex}; commandId={command.CommandId}; "
                        + $"order={command.Order}; reason={Escape(overlay.Error)}; action=continue");
                    continue;
                }

                SpineOverlayCommand parsed = overlay.Value;
                ModResult<ISpineOverlayCommandService> provider = GetSpineOverlayProvider();
                if (!provider.Success || provider.Value == null)
                {
                    lines.Add(
                        $"{Stamp()} SPINE_OVERLAY_UNAVAILABLE sequence={authorization.WindowSequence}; "
                        + $"record={authorization.PlaybackRecordIndex}; commandId={command.CommandId}; "
                        + $"order={command.Order}; slot={parsed.PublicSlot}; "
                        + $"reason={Escape(provider.Error?.Message ?? string.Empty)}; action=continue");
                    continue;
                }

                ModResult<SpineOverlayExecutionSnapshot> applied = provider.Value.ApplyOverlay(
                    new SpineOverlayRequest(
                        authorization.SceneIdentity,
                        parsed.PublicSlot,
                        parsed.Operation == SpineOverlayOperation.Clear
                            ? SpineOverlayRequestKind.Clear
                            : SpineOverlayRequestKind.Play,
                        parsed.AnimationName,
                        parsed.TrackIndex,
                        parsed.MixMilliseconds,
                        parsed.FadeMilliseconds,
                        parsed.Loop,
                        parsed.Blend == SpineOverlayBlend.Add,
                        parsed.Hold));
                if (!applied.Success || applied.Value == null)
                {
                    lines.Add(
                        $"{Stamp()} SPINE_OVERLAY_FAILED sequence={authorization.WindowSequence}; "
                        + $"record={authorization.PlaybackRecordIndex}; commandId={command.CommandId}; "
                        + $"order={command.Order}; slot={parsed.PublicSlot}; "
                        + $"reason={Escape(applied.Error?.Message ?? string.Empty)}; action=continue");
                    continue;
                }

                SpineOverlayExecutionSnapshot spineExecution = applied.Value;
                lines.Add(
                    $"{Stamp()} SPINE_OVERLAY_DISPATCHED sequence={authorization.WindowSequence}; "
                    + $"record={authorization.PlaybackRecordIndex}; commandId={command.CommandId}; "
                    + $"order={command.Order}; slot={spineExecution.PublicSlot}; "
                    + $"operation={spineExecution.Operation}; "
                    + $"animation={Escape(spineExecution.AnimationName)}; "
                    + $"track={spineExecution.TrackIndex}; applied={spineExecution.Applied}; "
                    + $"detail={Escape(spineExecution.Detail)}; retry=false");
                continue;
            }

            var request = new CharacterTransformDispatchRequest(
                authorization.SceneIdentity,
                command.CanonicalDirective,
                CharacterCommandSource.PlaybackSidecar,
                OriginIdentity: OriginIdentityFor(
                    queued.Chain,
                    command.CanonicalDirective));
            ApiResult<CharacterTransformDispatchSnapshot> dispatched =
                Plugin.Api.CharacterCommands.DispatchOnMainThread(request);
            if (!dispatched.Success || dispatched.Value == null)
            {
                lines.Add(
                    $"{Stamp()} SCENE_COMMAND_FAILED sequence={authorization.WindowSequence}; "
                    + $"record={authorization.PlaybackRecordIndex}; commandId={command.CommandId}; "
                    + $"order={command.Order}; reason={Escape(dispatched.Error)}; retry=false");
                for (int skipped = index + 1;
                    skipped < authorization.Commands.Count;
                    skipped++)
                {
                    PlaybackCommandInstruction remaining =
                        authorization.Commands[skipped];
                    lines.Add(
                        $"{Stamp()} SCENE_COMMAND_SKIPPED sequence={authorization.WindowSequence}; "
                        + $"record={authorization.PlaybackRecordIndex}; commandId={remaining.CommandId}; "
                        + $"order={remaining.Order}; reason=prior-command-failed; retry=false");
                }

                return;
            }

            CharacterTransformDispatchSnapshot dispatch = dispatched.Value;
            CharacterTransformExecutionSnapshot execution = dispatch.Execution;
            lines.Add(
                $"{Stamp()} SCENE_COMMAND_DISPATCHED sequence={authorization.WindowSequence}; "
                + $"record={authorization.PlaybackRecordIndex}; commandId={command.CommandId}; "
                + $"order={command.Order}; dispatchSequence={dispatch.Sequence}; "
                + $"slot={execution.Command.PublicSlot}; occupant={Escape(execution.OccupantIdentifier)}; "
                + $"operation={execution.Command.Operation}; durationMs={execution.Command.DurationMilliseconds}; "
                + $"easing={execution.Command.Easing}; positionChanged={execution.PositionChanged}; "
                + $"rotationChanged={execution.RotationChanged}; baselineCaptured={execution.BaselineCaptured}; "
                + $"beforeX={execution.Before.Position.X:R}; "
                + $"targetX={execution.Target.Position.X:R}; "
                + $"beforeZ={execution.Before.LocalEulerAngles.Z:R}; "
                + $"targetZ={execution.Target.LocalEulerAngles.Z:R}; retry=false");
        }
    }

    private static void ApplyPreviewChain(
        PlaybackSceneDispatchAuthorization authorization,
        PreviewChainResolution chain,
        ICollection<string> lines)
    {
        ApplyPreviewChainSlots(
            authorization.WindowSequence,
            chain,
            authorization.SceneIdentity,
            lines);
    }

    /// <summary>
    /// Releases the overlays of a scene the editor preview has left. Overlays are sticky within a
    /// scene — exactly like playback — so a transition between two cards of one scene must not
    /// release anything; the engine's scene rebuild is what ends them otherwise, and this call only
    /// keeps the provider's bookkeeping bounded when the identity really changes.
    /// </summary>
    private static void ReleaseSpineOverlays(string sceneIdentity, ICollection<string> lines)
    {
        ModResult<ISpineOverlayCommandService> provider = GetSpineOverlayProvider();
        if (!provider.Success || provider.Value == null) return;
        ModResult<int> released = provider.Value.ReleaseScene(
            sceneIdentity,
            SpineOverlayTracks.DefaultFadeMilliseconds);
        if (!released.Success)
        {
            lines.Add(
                $"{Stamp()} editor-preview-spine status=RELEASE_FAILED; "
                + $"scene={Escape(sceneIdentity)}; "
                + $"reason={Escape(released.Error?.Message ?? string.Empty)}");
            return;
        }

        if (released.Value == 0) return;
        lines.Add(
            $"{Stamp()} editor-preview-spine status=RELEASED; scene={Escape(sceneIdentity)}; "
            + $"tracks={released.Value}; reason=scene-change");
    }

    /// <summary>
    /// The spine overlay provider, if the spine package is loaded. Optional by design: a missing
    /// provider costs one command, not the card, so the caller reports it and carries on.
    /// </summary>
    private static ModResult<ISpineOverlayCommandService> GetSpineOverlayProvider() =>
        ModServices.Current?.GetService<ISpineOverlayCommandService>()
        ?? ModResult<ISpineOverlayCommandService>.Fail(
            ModErrorCode.NotReady,
            "Spine 叠加服务尚未就绪（更多Spine动画支持未加载或未注册）。");

    private static ObservationIndexLoadResult LoadIndex(
        PlayerCommandObservationOptions options)
    {
        PlaybackArchiveSnapshot? playback = null;
        ProjectSnapshot? project = null;
        string ArchiveContext() => DescribeArchiveContext(project, playback);

        Result<string> playbackPath = ResolveArchivePath(
            options.PlaybackPath,
            ".aas",
            "playback");
        if (!playbackPath.Success || playbackPath.Value == null)
        {
            return ObservationIndexLoadResult.Fail(
                "playback-path-invalid",
                playbackPath.Error,
                ArchiveContext());
        }

        Result<PlaybackArchiveSnapshot> playbackRead =
            new AasScenarioReader().Read(playbackPath.Value);
        if (!playbackRead.Success || playbackRead.Value == null)
        {
            return ObservationIndexLoadResult.Fail(
                "aas-read-failed",
                playbackRead.Error,
                ArchiveContext());
        }

        playback = playbackRead.Value;
        if (options.Stage is PlayerCommandObservationStage.IdentityOnly
            or PlayerCommandObservationStage.PlaybackContextProbe)
        {
            Result<IPlaybackCommandIndex> identityIndex =
                new PlaybackCommandBinder().Bind(playback, null);
            return identityIndex.Success && identityIndex.Value != null
                ? ObservationIndexLoadResult.Ready(
                    identityIndex.Value,
                    projectionLoaded: false,
                    workspaceSource: "none",
                    ArchiveContext(),
                    continueIdentities: null)
                : ObservationIndexLoadResult.Fail(
                    "identity-index-bind-failed",
                    identityIndex.Error,
                    ArchiveContext());
        }

        Result<string> projectPath = ResolveArchivePath(
            options.ProjectPath,
            ".aap",
            "project");
        if (!projectPath.Success || projectPath.Value == null)
        {
            return ObservationIndexLoadResult.Fail(
                "project-path-invalid",
                projectPath.Error,
                ArchiveContext());
        }

        Result<ProjectSnapshot> projectRead =
            new AapProjectReader().Read(projectPath.Value);
        if (!projectRead.Success || projectRead.Value == null)
        {
            return ObservationIndexLoadResult.Fail(
                "aap-read-failed",
                projectRead.Error,
                ArchiveContext());
        }

        project = projectRead.Value;

        // Save → build consistency, judged from the pair as it was before the
        // reads above: the official autosave writes only the .aap, so a save
        // that has not been built leaves the .aas one revision behind and the
        // embedded projection cannot bind its commands to playback records.
        // Taking both the write times and the signature here — never after the
        // read — means a build landing mid-load can only make the poll retry,
        // never leave the loader waiting for a change that already happened.
        DateTime aapWritten = FileWriteTimeUtc(projectPath.Value);
        DateTime aasWritten = FileWriteTimeUtc(playbackPath.Value);
        ProjectPublicationState publicationState =
            ProjectPublicationConsistency.Classify(aapWritten, aasWritten);
        string observedSignature =
            DescribeArchiveSignature(projectPath.Value, playbackPath.Value);

        PlaybackCommandProjection? embeddedProjection = null;
        int embeddedCommandCount = 0;
        IReadOnlyList<string>? authoritativeContinueIdentities = null;
        if (options.EmbeddedEditorCommandsEnabled
            && _embeddedEditorCommandsRuntimeReady)
        {
            Result<EmbeddedProjectCommandCompilation> embeddedCompile =
                new EmbeddedProjectCommandCompiler(CaptureOwnedPromptProjection()).Compile(
                    project,
                    playback,
                    Plugin.Version,
                    options.AcceptLegacyCharacterDirectiveAlias);
            if (!embeddedCompile.Success || embeddedCompile.Value == null)
            {
                // A save whose publish has not landed is the one failure this
                // loader is expected to meet and to recover from by itself, so
                // the worker reports the pair's state as a fact and the main
                // thread decides whether it may still wait. The judgement uses
                // write times taken before the pair was read, and the same
                // instant is handed back as the poll baseline, so a build landing
                // mid-load can only cause one redundant attempt. Anything the
                // pair still cannot compile after the wait expires is reported as
                // the failure it is.
                return ObservationIndexLoadResult.Fail(
                    "embedded-commands-invalid",
                    $"{embeddedCompile.Error}; "
                    + $"sinceAapMs={DeltaMilliseconds(aasWritten, aapWritten)}",
                    ArchiveContext(),
                    publicationState,
                    observedSignature);
            }

            embeddedProjection = embeddedCompile.Value.Projection;
            embeddedCommandCount = embeddedCompile.Value.CommandCount;
            authoritativeContinueIdentities = embeddedCompile.Value.ContinueIdentities;
            PlayerCommandObservationLog.Append(
                $"{Stamp()} embedded-compile status=OK; "
                + $"scenes={embeddedCompile.Value.SceneCount}; commands={embeddedCommandCount}; "
                + $"continueIdentities={authoritativeContinueIdentities.Count}; "
                + $"aasRecordsScanned={playback.Records.Count}; "
                + $"aasRevision16={Short16(playback.Source.RevisionSha256)}");
        }

        string workspaceRoot;
        try
        {
            workspaceRoot = Path.GetFullPath(options.WorkspaceRoot);
        }
        catch (Exception ex)
        {
            return ObservationIndexLoadResult.Fail(
                "workspace-root-invalid",
                ex.Message,
                ArchiveContext());
        }

        var source = new ModWorkspaceSourceBinding(
            project.Source.PathKey,
            project.Source.RevisionSha256,
            playback.Source.PathKey,
            playback.Source.RevisionSha256,
            playback.SchemaName);
        var context = new ModWorkspaceRuntimeContext(
            Plugin.Version,
            source,
            new HashSet<string>(
                new[]
                {
                    CharacterTransformCommandFamilyCompiler.CapabilityId,
                    SlotPendingCommandFamilyCompiler.CapabilityId,
                    SceneCameraCommandFamilyCompiler.CapabilityId
                },
                StringComparer.Ordinal));
        var workspaceStore = new JsonModWorkspaceStore(workspaceRoot);
        Result<ModWorkspaceLoadSnapshot> workspaceLoad =
            workspaceStore.TryLoad(project.Source.PathKey, context);
        if (!workspaceLoad.Success || workspaceLoad.Value == null)
        {
            return ObservationIndexLoadResult.Fail(
                "workspace-manifest-unavailable",
                workspaceLoad.Error,
                ArchiveContext());
        }

        ModWorkspaceLoadSnapshot workspaceSnapshot = workspaceLoad.Value;
        ModWorkspaceManifest? workspace = workspaceSnapshot.Manifest;
        PlaybackCommandProjection? sidecarProjection = null;
        string workspaceSource = "none";
        if (workspace != null && !workspaceSnapshot.Compatibility.CanExecute)
        {
            return ObservationIndexLoadResult.Fail(
                "workspace-not-executable",
                $"{workspaceSnapshot.Compatibility.Status}: {workspaceSnapshot.Compatibility.Message}",
                ArchiveContext());
        }

        if (workspace != null)
        {
            var commandStore = new JsonCommandWorkspaceStore(workspaceRoot);
            Result<CommandTimelineLoadSnapshot> timelineLoad =
                commandStore.TryLoadTimeline(
                    project.Source.PathKey,
                    workspace,
                    project,
                    playback);
            if (!timelineLoad.Success || timelineLoad.Value?.Timeline == null
                || timelineLoad.Value.Status != CommandWorkspaceDocumentStatus.Ready)
            {
                return ObservationIndexLoadResult.Fail(
                    "command-timeline-unavailable",
                    $"{timelineLoad.Error}; status={timelineLoad.Value?.Status.ToString() ?? "failed"}",
                    ArchiveContext());
            }

            CommandTimelineDocument timeline = timelineLoad.Value.Timeline;
            Result<PlaybackCommandProjectionLoadSnapshot> projectionLoad =
                commandStore.TryLoadProjection(
                    project.Source.PathKey,
                    timeline,
                    workspace,
                    project,
                    playback);
            if (!projectionLoad.Success || projectionLoad.Value?.Projection == null
                || projectionLoad.Value.Status != CommandWorkspaceDocumentStatus.Ready)
            {
                return ObservationIndexLoadResult.Fail(
                    "command-projection-unavailable",
                    $"{projectionLoad.Error}; status={projectionLoad.Value?.Status.ToString() ?? "failed"}",
                    ArchiveContext());
            }

            sidecarProjection = projectionLoad.Value.Projection;
            workspaceSource = workspaceSnapshot.Source.ToString();
        }

        PlaybackCommandProjection? executableProjection = sidecarProjection;
        if (embeddedProjection != null && embeddedCommandCount != 0)
        {
            Result<PlaybackCommandProjection> composed =
                new PlaybackCommandProjectionComposer().Compose(
                    sidecarProjection,
                    embeddedProjection);
            if (!composed.Success || composed.Value == null)
            {
                return ObservationIndexLoadResult.Fail(
                    "projection-composition-failed",
                    composed.Error,
                    ArchiveContext());
            }

            executableProjection = composed.Value;
            workspaceSource = sidecarProjection == null
                ? $"embedded-environment:{embeddedCommandCount}"
                : $"{workspaceSource}+embedded-environment:{embeddedCommandCount}";
        }

        Result<IPlaybackCommandIndex> bound = new PlaybackCommandBinder().Bind(
            playback,
            executableProjection);
        return bound.Success && bound.Value != null
            ? ObservationIndexLoadResult.Ready(
                bound.Value,
                projectionLoaded: executableProjection?.Batches.Count > 0,
                workspaceSource: workspaceSource,
                ArchiveContext(),
                authoritativeContinueIdentities)
            : ObservationIndexLoadResult.Fail(
                "command-index-bind-failed",
                bound.Error,
                ArchiveContext());
    }

    private static string DescribeArchiveContext(
        ProjectSnapshot? project,
        PlaybackArchiveSnapshot? playback)
    {
        string aap = project == null
            ? "aapPathKey16=none; aapRevision16=none"
            : $"aapPathKey16={Short16(project.Source.PathKey)}; "
                + $"aapRevision16={Short16(project.Source.RevisionSha256)}";
        string aas = playback == null
            ? "aasPathKey16=none; aasRevision16=none"
            : $"aasPathKey16={Short16(playback.Source.PathKey)}; "
                + $"aasRevision16={Short16(playback.Source.RevisionSha256)}";
        return $"{aap}; {aas}";
    }

    private static string Short16(string sha256) =>
        sha256.Length <= 16 ? sha256 : sha256[..16];

    private static PlayerCommandObservationOptions Normalize(
        PlayerCommandObservationOptions options)
    {
        string workspaceRoot = options.WorkspaceRoot;
        if (!Path.IsPathRooted(workspaceRoot))
        {
            workspaceRoot = Path.Combine(
                BepInEx.Paths.GameRootPath,
                workspaceRoot);
        }

        return options with { WorkspaceRoot = Path.GetFullPath(workspaceRoot) };
    }

    private static Result<string> NormalizeFilePath(
        string configured,
        string extension,
        string label)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return Result<string>.Fail(
                $"Configured {label} path is empty.");
        }

        try
        {
            string trimmed = configured.Trim();
            if (!Path.IsPathRooted(trimmed))
            {
                return Result<string>.Fail(
                    $"Configured {label} path must be absolute.");
            }

            string fullPath = Path.GetFullPath(trimmed);
            if (!Path.IsPathRooted(fullPath)
                || !string.Equals(
                    Path.GetExtension(fullPath),
                    extension,
                    StringComparison.OrdinalIgnoreCase)
                || !File.Exists(fullPath))
            {
                return Result<string>.Fail(
                    $"Configured {label} path must be an existing absolute {extension} file.");
            }

            return Result<string>.Ok(fullPath);
        }
        catch (Exception ex)
        {
            return Result<string>.Fail(
                $"Configured {label} path is invalid: {ex.Message}");
        }
    }

    private static Result<string> ResolveArchivePath(
        string configuredPath,
        string extension,
        string label)
    {
        if (!ActiveProjectPairSource.Enabled)
        {
            return NormalizeFilePath(configuredPath, extension, label);
        }

        Core.Projects.DiscoveredProjectPair? pair =
            ActiveProjectPairSource.ActivePairSnapshot();
        if (pair == null)
        {
            return Result<string>.Fail(
                "auto discovery is enabled but no active project pair has been "
                + "arbitrated from an editor selection yet.");
        }

        string autoPath = extension.Equals(".aap", StringComparison.Ordinal)
            ? pair.AapPath
            : pair.AasPath;
        try
        {
            if (!File.Exists(autoPath))
            {
                return Result<string>.Fail(
                    $"The auto-discovered {label} file no longer exists: {autoPath}");
            }

            return Result<string>.Ok(Path.GetFullPath(autoPath));
        }
        catch (Exception ex)
        {
            return Result<string>.Fail(
                $"The auto-discovered {label} path is invalid: {ex.Message}");
        }
    }

    private static void LogOnce(string key, string message)
    {
        bool added;
        lock (_logOnceGate)
        {
            added = _logOnceKeys.Add(key);
        }

        if (added)
        {
            PlayerCommandObservationLog.Append(message);
        }
    }

    private static string StageAction(PlayerCommandObservationStage stage) =>
        stage switch
        {
            PlayerCommandObservationStage.QueueNoOp => "QUEUE_NO_OP",
            PlayerCommandObservationStage.DispatchCanary => "QUEUE_DISPATCH_CANARY",
            PlayerCommandObservationStage.DispatchReplayCanary =>
                "QUEUE_DISPATCH_REPLAY_CANARY",
            PlayerCommandObservationStage.PlaybackContextProbe => "NO_OP_CONTEXT_PROBE",
            PlayerCommandObservationStage.SceneCommandDispatch => "QUEUE_SCENE_COMMANDS",
            _ => "WOULD_DISPATCH"
        };

    private static void AppendPlayerContext(
        long windowSequence,
        ApiResult<PlayerRuntimeContextSnapshot>? result,
        ICollection<string> lines)
    {
        if (result == null)
        {
            lines.Add(
                $"{Stamp()} player-context sequence={windowSequence}; status=NOT_READ; action=NO_OP");
            return;
        }

        if (!result.Success || result.Value == null)
        {
            lines.Add(
                $"{Stamp()} player-context sequence={windowSequence}; status=FAILED; "
                + $"reason={Escape(result.Error)}; action=NO_OP");
            return;
        }

        PlayerRuntimeContextSnapshot context = result.Value;
        lines.Add(
            $"{Stamp()} player-context sequence={windowSequence}; status=READ; "
            + $"playerAvailable={context.PlayerAvailable}; previewMode={context.PreviewMode}; "
            + $"provisionalMode={context.Mode}; "
            + $"manualControlsAllowed={PlayerManualControlPolicy.AllowsManualControls(context)}; "
            + "action=NO_OP; wrapperCached=false");
    }

    private static bool IsDispatchCanaryStage(
        PlayerCommandObservationStage stage) =>
        stage is PlayerCommandObservationStage.DispatchCanary
            or PlayerCommandObservationStage.DispatchReplayCanary;

    private static bool IsRealDispatchStage(
        PlayerCommandObservationStage stage) =>
        IsDispatchCanaryStage(stage)
        || stage == PlayerCommandObservationStage.SceneCommandDispatch;

    private static string Stamp() =>
        DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture);

    private static string Escape(string value) =>
        value.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private enum EditorPreviewCommandKind
    {
        Character = 0,
        Camera = 1,
        SlotPending = 2,
        SpineOverlay = 3,
        CharacterPreset = 4
    }

    private enum RuntimeEditorPreviewResourceKind
    {
        Character = 0,
        Camera = 1,
        SpineOverlay = 2,
        CharacterPreset = 3
    }

    private readonly record struct EditorPreviewResource(
        RuntimeEditorPreviewResourceKind Kind,
        int PublicSlot,
        int TrackIndex = 0);

    private sealed record EditorPreviewCommand(
        int Order,
        EditorPreviewCommandKind Kind,
        int PublicSlot,
        string CanonicalDirective,
        int TrackIndex = 0);

    private sealed record DeferredEditorPreview(
        long WindowSequence,
        ManagedDispatchSchedule Schedule,
        EmbeddedEditorPreviewLease Lease,
        IReadOnlyList<EditorPreviewCommand> Commands,
        IReadOnlyList<EditorPreviewResource> Resources);

    private sealed record ActiveEditorPreview(
        string SceneIdentity,
        IReadOnlyList<EditorPreviewResource> Resources);

    private sealed record PreviewChainApplyResult(
        HashSet<int> AppliedSlots,
        HashSet<int> ResidualMutationSlots)
    {
        public static PreviewChainApplyResult Empty => new(new(), new());
    }

    private sealed record DeferredCommandBatch(
        long WindowSequence,
        ManagedDispatchSchedule Schedule,
        PlaybackCommandBatch Batch,
        PreviewChainResolution? Chain,
        bool ApplyResolvedChain,
        long ChainGenerationId);

    private sealed record PendingEmbeddedPreview(
        long WindowSequence,
        long CreatedUpdate,
        long EditorObservationSequence,
        CompiledScriptIdentity CompiledScript,
        IReadOnlyList<string> CanonicalDirectives,
        long PutTimestampUtcTicks,
        long ChainGenerationId);

    private sealed class PreviewChainSource
    {
        public PreviewChainSource(
            ProjectSnapshot project,
            PreviewChainResolver resolver,
            PreviewChainDirectiveIndex directives,
            PreviewCameraChainResolver cameraChains)
        {
            Project = project;
            Resolver = resolver;
            Directives = directives;
            CameraChains = cameraChains;
        }

        public ProjectSnapshot Project { get; }

        public PreviewChainResolver Resolver { get; }

        public PreviewChainDirectiveIndex Directives { get; }

        public PreviewCameraChainResolver CameraChains { get; }
    }

    private sealed record PreviewChainSourceLoadResult(
        bool Success,
        PreviewChainSource? Source,
        string Reason,
        string Diagnostic)
    {
        public static PreviewChainSourceLoadResult Ready(
            PreviewChainSource source) => new(true, source, string.Empty, string.Empty);

        public static PreviewChainSourceLoadResult Fail(
            string reason,
            string diagnostic) => new(false, null, reason, diagnostic);
    }

    private sealed record ObservationIndexLoadResult(
        bool Success,
        IPlaybackCommandIndex? Index,
        bool ProjectionLoaded,
        string WorkspaceSource,
        IReadOnlyList<string>? ContinueIdentities,
        string Reason,
        string ArchiveContext,
        string Diagnostic,
        ProjectPublicationState PublicationState,
        string ArchiveSignature)
    {
        /// <summary>
        /// <paramref name="archiveSignature"/> is the pair signature observed
        /// BEFORE the worker read the pair, and becomes the archive-change poll's
        /// baseline. It is empty only when the load failed before the pair could
        /// be described at all.
        /// <para>
        /// <paramref name="publicationState"/> is meaningful only for the
        /// embedded-commands-invalid failure, which is the only one the worker
        /// classifies; every other outcome leaves it
        /// <see cref="ProjectPublicationState.Unknown"/> and therefore never
        /// waits for a publish.
        /// </para>
        /// </summary>
        public static ObservationIndexLoadResult Ready(
            IPlaybackCommandIndex index,
            bool projectionLoaded,
            string workspaceSource,
            string archiveContext,
            IReadOnlyList<string>? continueIdentities,
            string archiveSignature = "") => new(
                true,
                index,
                projectionLoaded,
                workspaceSource,
                continueIdentities,
                string.Empty,
                archiveContext,
                string.Empty,
                ProjectPublicationState.Unknown,
                archiveSignature);

        public static ObservationIndexLoadResult Fail(
            string reason,
            string diagnostic,
            string archiveContext,
            ProjectPublicationState publicationState = ProjectPublicationState.Unknown,
            string archiveSignature = "") => new(
                false,
                null,
                false,
                "none",
                null,
                reason,
                archiveContext,
                diagnostic,
                publicationState,
                archiveSignature);
    }
}
