using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Characters;
using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Runtime;

namespace AzureArchive.VideoTools.Tests;

internal static class PlaybackAdvanceDispatchPolicyTests
{
    public static void CompletedKeepsUniqueIdentityFallbackButEmptyRequiresExactRow()
    {
        IPlaybackCommandIndex index = Index(new[] { "ordinary script", "", "\n" }, 0, 1, 2);
        var unknownRow = PlaybackAdvanceDispatchPolicy.ObserveCompleted(index, new[] { "ordinary script" }, -1);
        AssertEx.True(unknownRow.Success, unknownRow.Error);
        AssertEx.Equal(0, AssertEx.NotNull(AssertEx.NotNull(unknownRow.Value).Batch).PlaybackRecordIndex);
        // The existing completed-window unique-identity behavior also remains
        // independent of a stale row hint. Overlaps still require exact rows.
        AssertEx.True(PlaybackAdvanceDispatchPolicy.ObserveCompleted(index, new[] { "ordinary script" }, 2).Success);
        AssertEx.False(PlaybackAdvanceDispatchPolicy.ObserveExactRow(index, new[] { "ordinary script" }, -1).Success);
        AssertEx.False(PlaybackAdvanceDispatchPolicy.ObserveCompleted(index, new[] { "" }, -1).Success);
        AssertEx.False(PlaybackAdvanceDispatchPolicy.ObserveCompleted(index, new[] { "" }, 0).Success);
        AssertEx.True(PlaybackAdvanceDispatchPolicy.ObserveCompleted(index, new[] { "" }, 1).Success);
        AssertEx.True(PlaybackAdvanceDispatchPolicy.ObserveCompleted(index, new[] { "\n" }, -1).Success);
        IPlaybackCommandIndex ordinary = Index(new[] { "no command" });
        var ordinaryUnknown = PlaybackAdvanceDispatchPolicy.ObserveCompleted(ordinary, new[] { "no command" }, -1);
        AssertEx.Equal(PlaybackCommandWindowObservationStatus.OrdinaryRecord,
            AssertEx.NotNull(ordinaryUnknown.Value).Status);
        AssertEx.Equal(CommandIdentity.CompiledScript("no command"), ordinaryUnknown.Value!.CompiledScript);
        AssertEx.Equal(PlaybackCommandWindowObservationStatus.NoIdentity,
            AssertEx.NotNull(PlaybackAdvanceDispatchPolicy.ObserveCompleted(index, Array.Empty<string>(), -1).Value).Status);
    }

    public static void CompletedRetainsOneCommandAmongOrdinaryIdentitySemantics()
    {
        IPlaybackCommandIndex index = Index(new[] { "command", "ordinary" }, 0);
        var completed = PlaybackAdvanceDispatchPolicy.ObserveCompleted(index, new[] { "command", "ordinary" }, -1);
        AssertEx.True(completed.Success, completed.Error);
        AssertEx.Equal(0, AssertEx.NotNull(AssertEx.NotNull(completed.Value).Batch).PlaybackRecordIndex);
        AssertEx.False(PlaybackAdvanceDispatchPolicy.ObserveExactRow(index, new[] { "command", "ordinary" }, 0).Success);
        IPlaybackCommandIndex both = Index(new[] { "first command", "second command" }, 0, 1);
        AssertEx.Equal(PlaybackCommandWindowObservationStatus.MultipleRecords,
            AssertEx.NotNull(PlaybackAdvanceDispatchPolicy.ObserveCompleted(both,
                new[] { "first command", "second command" }, -1).Value).Status);
    }

    public static void DuplicateSiblingCannotInvalidateQueuedPresetBoundary()
    {
        IPlaybackCommandIndex index = Index(new[] { "\n", "next ordinary row" }, 0);
        var claims = new PlaybackAdvanceDispatchGate();
        var presets = new CharacterPresetWindowGate();
        var first = claims.TryClaim(10, 7, index, Observe(index, 0, "\n"));
        AssertEx.True(first.Success && first.Value);
        if (PlaybackAdvanceDispatchPolicy.ShouldObserveBoundary(completedWindow: false, first.Value))
            presets.ObservePlaybackBoundary(100, -1, 0, 1);
        AssertEx.True(presets.MayStart(100));

        var completed = PlaybackAdvanceDispatchPolicy.ObserveCompleted(index, new[] { "\n" }, 0);
        var duplicate = claims.TryClaim(10, 7, index, AssertEx.NotNull(completed.Value));
        AssertEx.True(duplicate.Success && !duplicate.Value);
        if (PlaybackAdvanceDispatchPolicy.ShouldObserveBoundary(completedWindow: true, duplicate.Value))
            presets.ObservePlaybackBoundary(101, 0, 0, 1);
        AssertEx.True(presets.MayStart(100), "A completed sibling must not block the overlap's queued preset.");

        var next = claims.TryClaim(11, 7, index, Observe(index, 1, "next ordinary row"));
        AssertEx.True(next.Success && next.Value);
        if (PlaybackAdvanceDispatchPolicy.ShouldObserveBoundary(completedWindow: true, next.Value))
            presets.ObservePlaybackBoundary(102, 0, 1, 1);
        AssertEx.False(presets.MayStart(100));
        AssertEx.True(presets.MayStart(102));
    }

    public static void UnidentifiedCompletedCleanupKeepsHoldsAndEndsRealAdvances()
    {
        var presets = new CharacterPresetWindowGate();
        AssertEx.True(presets.ObserveBoundary(10));
        AssertEx.True(PlaybackAdvanceDispatchPolicy.ShouldObserveBoundary(completedWindow: true, callChainClaim: null));
        AssertEx.False(presets.ObservePlaybackBoundary(11, 3, 3, 0));
        AssertEx.True(presets.MayStart(10));
        AssertEx.True(presets.ObservePlaybackBoundary(12, 3, 4, 0));
        AssertEx.False(presets.MayStart(10));
        AssertEx.False(PlaybackAdvanceDispatchPolicy.ShouldObserveBoundary(completedWindow: false, callChainClaim: null));
        AssertEx.False(PlaybackAdvanceDispatchPolicy.ShouldObserveBoundary(completedWindow: true, callChainClaim: false));
        AssertEx.True(presets.MayStart(12));
    }

    public static void ExactRowsSeparateEmptyNewlineAndAbsentMessages()
    {
        IPlaybackCommandIndex index = Index(new[] { "", "\n", "\n" }, 0, 1);
        PlaybackAdvanceObservation empty = Observe(index, 0, "");
        AssertEx.Equal(PlaybackCommandWindowObservationStatus.CommandBatch, empty.Status);
        AssertEx.Equal(0, AssertEx.NotNull(empty.CompiledScript).Utf16Length);
        AssertEx.Equal(0, AssertEx.NotNull(empty.Batch).PlaybackRecordIndex);

        PlaybackAdvanceObservation newline = Observe(index, 1, "\n");
        AssertEx.Equal(1, AssertEx.NotNull(newline.CompiledScript).Utf16Length);
        AssertEx.Equal(1, AssertEx.NotNull(newline.Batch).PlaybackRecordIndex);
        PlaybackAdvanceObservation ordinary = Observe(index, 2, "\n");
        AssertEx.Equal(PlaybackCommandWindowObservationStatus.OrdinaryRecord, ordinary.Status);
        AssertEx.True(ordinary.Batch == null);

        PlaybackAdvanceObservation absent = Observe(index, 0);
        AssertEx.Equal(PlaybackCommandWindowObservationStatus.NoIdentity, absent.Status);
        AssertEx.True(absent.Batch == null && absent.CompiledScript == null);
        AssertEx.False(PlaybackAdvanceDispatchPolicy.ObserveExactRow(index, new[] { "" }, 1).Success);
        AssertEx.False(PlaybackAdvanceDispatchPolicy.ObserveExactRow(index, new[] { "\n" }, 0).Success);
    }

    public static void UniqueIdentityCannotOverrideWrongOrUnknownRow()
    {
        IPlaybackCommandIndex index = Index(new[] { "first", "second" }, 0);
        // The ordinary index accepts a unique identity despite its wrong row;
        // the formal admission must independently reject that disagreement.
        AssertEx.False(PlaybackAdvanceDispatchPolicy.ObserveExactRow(index, new[] { "first" }, 1).Success);
        AssertEx.False(PlaybackAdvanceDispatchPolicy.ObserveExactRow(index, new[] { "first" }, -1).Success);
        AssertEx.False(PlaybackAdvanceDispatchPolicy.ObserveExactRow(index, new[] { "first" }, 2).Success);
        AssertEx.False(PlaybackAdvanceDispatchPolicy.ObserveExactRow(index, new[] { "first", "second" }, 0).Success);
        AssertEx.Equal(0, AssertEx.NotNull(Observe(index, 0, "unrelated Unity diagnostic", "first").Batch).PlaybackRecordIndex);
    }

    public static void ClaimsSurviveSeparateDrainsAndNewCallChainReplays()
    {
        IPlaybackCommandIndex index = Index(new[] { "\n" }, 0);
        PlaybackAdvanceObservation match = Observe(index, 0, "\n");
        var gate = new PlaybackAdvanceDispatchGate();
        AssertEx.True(gate.TryClaim(10, 7, index, match).Value);
        // No managed-update key participates: draining the completed sibling
        // on the next update still cannot apply the same relative move twice.
        var duplicate = gate.TryClaim(10, 7, index, match);
        AssertEx.True(duplicate.Success, duplicate.Error);
        AssertEx.False(duplicate.Value);
        AssertEx.True(gate.TryClaim(11, 7, index, match).Value);
        AssertEx.False(gate.TryClaim(10, 7, index, match).Success);
    }

    public static void SameCallChainVisitsDifferentIdenticalAndCommandlessRows()
    {
        IPlaybackCommandIndex index = Index(new[] { "\n", "\n", "\n" }, 0, 1);
        var gate = new PlaybackAdvanceDispatchGate();
        AssertEx.True(gate.TryClaim(10, 7, index, Observe(index, 0, "\n")).Value);
        AssertEx.True(gate.TryClaim(10, 7, index, Observe(index, 1, "\n")).Value);
        AssertEx.True(gate.TryClaim(10, 7, index, Observe(index, 2, "\n")).Value);
        AssertEx.False(gate.TryClaim(10, 7, index, Observe(index, 0, "\n")).Value);
        AssertEx.False(gate.TryClaim(10, 7, index, Observe(index, 2, "\n")).Value);
    }

    public static void CallChainRejectsStorageIndexDriftAndMissingIdentity()
    {
        IPlaybackCommandIndex index = Index(new[] { "\n" }, 0);
        PlaybackAdvanceObservation match = Observe(index, 0, "\n");
        var gate = new PlaybackAdvanceDispatchGate();
        AssertEx.True(gate.TryClaim(10, 7, index, match).Value);
        AssertEx.False(gate.TryClaim(10, 8, index, match).Success);
        IPlaybackCommandIndex changed = IndexWithRevision(new[] { "\n" }, new string('D', 64), 0);
        AssertEx.False(gate.TryClaim(10, 7, changed, Observe(changed, 0, "\n")).Success);
        AssertEx.True(gate.TryClaim(11, 8, changed, Observe(changed, 0, "\n")).Value);
        AssertEx.False(gate.TryClaim(12, 8, changed, Observe(changed, 0)).Success);
    }

    public static void OverlapAndCompletedSiblingClaimSavedEmptyRowOnce()
    {
        DrainWindows();
        IPlaybackCommandIndex index = Index(new[] { "ordinary", "\n" }, 1);
        var gate = new PlaybackAdvanceDispatchGate();
        long outer = PlayerAdvanceObservationWindow.BeginAdvance(0, 7);
        try
        {
            PlayerAdvanceObservationWindow.Capture("\n");
            long inner = PlayerAdvanceObservationWindow.BeginAdvance(1, 7);
            PlayerAdvanceObservationWindow.Capture("\n");
            PlayerAdvanceObservationWindow.EndAdvance(inner, 1);
            PlayerAdvanceObservationWindow.EndAdvance(outer, 1);

            AssertEx.True(PlayerAdvanceObservationWindow.TryDequeue(out ClosedAdvanceWindow? first));
            ClosedAdvanceWindow overlap = AssertEx.NotNull(first);
            AssertEx.Equal(AdvanceWindowCloseReason.ReplacedByOverlappingPrefix, overlap.CloseReason);
            AssertEx.Equal(1, overlap.PlaybackRowIndex);
            AssertEx.True(gate.TryClaim(overlap.CallChainId, overlap.StorageRevision, index,
                Observe(index, overlap.PlaybackRowIndex, overlap.ManagedUnityMessages.ToArray())).Value);

            // Deliberately dequeue the sibling separately, as in a later drain.
            AssertEx.True(PlayerAdvanceObservationWindow.TryDequeue(out ClosedAdvanceWindow? second));
            ClosedAdvanceWindow completed = AssertEx.NotNull(second);
            AssertEx.Equal(AdvanceWindowCloseReason.Completed, completed.CloseReason);
            AssertEx.Equal(overlap.CallChainId, completed.CallChainId);
            AssertEx.False(gate.TryClaim(completed.CallChainId, completed.StorageRevision, index,
                Observe(index, completed.PlaybackRowIndex, completed.ManagedUnityMessages.ToArray())).Value);
            AssertEx.False(PlayerAdvanceObservationWindow.TryDequeue(out _));

            long replay = PlayerAdvanceObservationWindow.BeginAdvance(1, 7);
            PlayerAdvanceObservationWindow.Capture("\n");
            PlayerAdvanceObservationWindow.EndAdvance(replay, 1);
            AssertEx.True(PlayerAdvanceObservationWindow.TryDequeue(out ClosedAdvanceWindow? replayed));
            ClosedAdvanceWindow newCall = AssertEx.NotNull(replayed);
            AssertEx.True(newCall.CallChainId > overlap.CallChainId);
            AssertEx.True(gate.TryClaim(newCall.CallChainId, newCall.StorageRevision, index,
                Observe(index, newCall.PlaybackRowIndex, newCall.ManagedUnityMessages.ToArray())).Value);
        }
        finally
        {
            PlayerAdvanceObservationWindow.AbortAdvance(outer);
            DrainWindows();
        }
    }

    /// <summary>
    /// 2026-10-12 field log: a card with an empty dialogue advances twice inside
    /// one click, so the window that completes carries the identities of the
    /// rows the cascade passed over while the row that actually started playing
    /// never appears among them. The index then answers MultipleRecords and the
    /// commands bound to that row are never dispatched, even though the window
    /// did carry an exact row. The row hint must be allowed to resolve that,
    /// and only that: an unknown row or a row without a batch stays fail-closed.
    /// </summary>
    public static void MultipleRecordsFallsBackToTheExactRowTheWindowCarried()
    {
        // Rows 0..5 only: the shared fixture derives each record fingerprint from
        // 'A' + row, so a sixth or later row stops being valid hexadecimal.
        IPlaybackCommandIndex index = Index(
            new[] { "r0", "r1", "r2", "r3", "r4", "r5" }, 5);

        // Neither captured identity belongs to the row that started playing.
        var completed = PlaybackAdvanceDispatchPolicy.ObserveCompleted(index, new[] { "r3", "r4" }, 5);
        AssertEx.True(completed.Success, completed.Error);
        AssertEx.Equal(PlaybackCommandWindowObservationStatus.CommandBatch,
            AssertEx.NotNull(completed.Value).Status);
        AssertEx.Equal(5, AssertEx.NotNull(AssertEx.NotNull(completed.Value).Batch).PlaybackRecordIndex);

        // Fail-closed is unchanged where the named row carries no batch, and
        // where the window could not name a row at all. The original verdict is
        // deliberately preserved rather than rewritten: the row may only upgrade
        // the answer to a batch that truly sits on it, so a commandless row cannot
        // silently change into a different status. Both are NO_OP in the runtime.
        AssertEx.Equal(PlaybackCommandWindowObservationStatus.MultipleRecords,
            AssertEx.NotNull(PlaybackAdvanceDispatchPolicy.ObserveCompleted(index, new[] { "r3", "r4" }, 4).Value).Status);
        AssertEx.Equal(PlaybackCommandWindowObservationStatus.MultipleRecords,
            AssertEx.NotNull(PlaybackAdvanceDispatchPolicy.ObserveCompleted(index, new[] { "r3", "r4" }, -1).Value).Status);
    }

    private static PlaybackAdvanceObservation Observe(IPlaybackCommandIndex index, int row, params string[] messages)
    {
        var result = PlaybackAdvanceDispatchPolicy.ObserveExactRow(index, messages, row);
        AssertEx.True(result.Success, result.Error);
        return AssertEx.NotNull(result.Value);
    }

    private static IPlaybackCommandIndex Index(string[] scripts, params int[] commandRows) =>
        IndexWithRevision(scripts, new string('C', 64), commandRows);

    private static IPlaybackCommandIndex IndexWithRevision(string[] scripts, string revision, params int[] commandRows)
    {
        var source = new PlaybackArchiveSourceSnapshot("E:/test.aas", new string('B', 64), revision,
            scripts.Length, DateTimeOffset.UnixEpoch);
        PlaybackRecordSnapshot[] records = scripts.Select((script, row) => new PlaybackRecordSnapshot(
            row, 0, 0, 0, "", 0, 0, 0, "", script, "", "", "", "", "", "", false,
            new string((char)('A' + row), 64))).ToArray();
        var playback = new PlaybackArchiveSnapshot(source, "synthetic/v1", records);
        PlaybackCommandBatch[] batches = commandRows.Select(row => new PlaybackCommandBatch(
            new SceneKey("00112233-4455-6677-8899-aabbccddeeff", row, new string('A', 64)),
            row, records[row].Fingerprint, CommandIdentity.CompiledScript(scripts[row]),
            new[] { new PlaybackCommandInstruction(Guid.NewGuid().ToString("D"), 0,
                CommandTimelinePhase.SceneEnter, CharacterTransformCommandFamilyCompiler.CommandTypeId,
                CharacterTransformCommandFamilyCompiler.CapabilityId,
                "#char;3;move;dx=100;duration=0;easing=linear") })).ToArray();
        var projection = new PlaybackCommandProjection(PlaybackCommandProjection.CurrentSchemaVersion,
            "10213243-5465-7687-98a9-bacbdcedfe0f", new string('A', 64), new string('D', 64),
            new string('E', 64), source.PathKey, source.RevisionSha256, playback.SchemaName, batches);
        var bound = new PlaybackCommandBinder().Bind(playback, projection);
        AssertEx.True(bound.Success, bound.Error);
        return AssertEx.NotNull(bound.Value);
    }

    private static void DrainWindows()
    {
        while (PlayerAdvanceObservationWindow.TryDequeue(out _)) { }
        PlayerAdvanceObservationWindow.TakeDroppedWindowCount();
    }
}
