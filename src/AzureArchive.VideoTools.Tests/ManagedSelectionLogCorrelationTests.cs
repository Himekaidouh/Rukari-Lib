using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Playback;

namespace AzureArchive.VideoTools.Tests;

internal static class ManagedSelectionLogCorrelationTests
{
    private static readonly EditorPreviewSceneAddress Scene = new("project", "node", 9, "fingerprint");
    private static readonly CompiledScriptIdentity Script = CommandIdentity.CompiledScript("selected scene\n#5;h");
    private static readonly EditorPreviewSceneAddress FullScene = new(
        new string('A', 24), "11111111-2222-3333-4444-555555555555", 9, new string('B', 24));

    private static ManagedSelectionLogCorrelation Open(int requestId = 401,
        EditorPreviewSceneAddress? scene = null)
    {
        var correlation = new ManagedSelectionLogCorrelation();
        correlation.Begin(requestId, 20, 11, 5, 4, scene ?? Scene);
        return correlation;
    }

    private static ManagedSelectionLogCandidate Capture(ManagedSelectionLogCorrelation correlation)
    {
        AssertEx.True(correlation.TryObserveFirst(21, Script, 11, 5, 4, out _));
        AssertEx.True(correlation.Pending != null);
        return correlation.Pending!;
    }

    public static void ZeroRequestIsOpaqueAndPreservesTheObservedScript()
    {
        ManagedSelectionLogCorrelation correlation = Open(0);
        ManagedSelectionLogCandidate candidate = Capture(correlation);
        AssertEx.Equal(0, candidate.Marker.RequestId);
        AssertEx.Equal(9, candidate.Marker.CapturedScene!.SceneIndex);
        AssertEx.Equal(Script, candidate.Script);
        AssertEx.True(correlation.TryComplete(candidate, 11, 5, Scene, out _));
    }

    public static void NoMarkerAndLaterMessagesNeverSupplyAnIdentity()
    {
        var noMarker = new ManagedSelectionLogCorrelation();
        AssertEx.False(noMarker.TryObserveFirst(21, Script, 11, 5, 4, out _));
        ManagedSelectionLogCorrelation correlation = Open();
        ManagedSelectionLogCandidate candidate = Capture(correlation);
        AssertEx.False(correlation.TryObserveFirst(22, CommandIdentity.CompiledScript("later"),
            11, 5, 4, out _));
        AssertEx.Equal(candidate, correlation.Pending!);
    }

    public static void ReusedRequestCannotReviveASupersededMarker()
    {
        ManagedSelectionLogCorrelation correlation = Open();
        ManagedSelectionLogCandidate old = Capture(correlation);
        correlation.Begin(401, 21, 11, 6, 5, Scene);
        AssertEx.True(correlation.TryObserveFirst(22, Script, 11, 6, 5, out _));
        ManagedSelectionLogCandidate current = correlation.Pending!;
        AssertEx.False(correlation.TryComplete(old, 11, 6, Scene, out _));
        AssertEx.Equal(current, correlation.Pending!);
        AssertEx.True(correlation.TryComplete(current, 11, 6, Scene, out _));
    }

    public static void BackgroundFirstMessageCannotBeReplacedByAMainThreadMessage()
    {
        ManagedSelectionLogCorrelation correlation = Open();
        AssertEx.False(correlation.TryObserveFirst(21, Script, 22, 5, 4, out _));
        AssertEx.False(correlation.TryObserveFirst(22, Script, 11, 5, 4, out _));
        AssertEx.True(correlation.Pending == null);
    }

    public static void MissingCaptureOrMainThreadProofCannotPublish()
    {
        var noScene = new ManagedSelectionLogCorrelation();
        noScene.Begin(0, 20, 11, 5, 4, null);
        AssertEx.False(noScene.TryObserveFirst(21, Script, 11, 5, 4, out _));
        AssertEx.True(noScene.Pending == null);
        var noThread = new ManagedSelectionLogCorrelation();
        noThread.Begin(401, 20, 0, 5, 4, Scene);
        AssertEx.False(noThread.TryObserveFirst(21, Script, 11, 5, 4, out _));
    }

    public static void LateClosedOrDifferentWindowMessagesAreRejected()
    {
        AssertEx.False(Open().TryObserveFirst(21, Script, 11, 5, 5, out _));
        AssertEx.False(Open().TryObserveFirst(21, Script, 11, 6, 4, out _));
        AssertEx.False(Open().TryObserveFirst(21, Script, 11, 5, 3, out _));
        var noOpenWindow = new ManagedSelectionLogCorrelation();
        noOpenWindow.Begin(401, 20, 11, 4, 4, Scene);
        AssertEx.False(noOpenWindow.TryObserveFirst(21, Script, 11, 5, 4, out _));
    }

    public static void InvalidOrMissingFirstScriptDoesNotFallBackToALaterOne()
    {
        CompiledScriptIdentity?[] invalid =
        {
            null,
            new("partial", 10, 1),
            new(new string('A', 64), -1, 1),
            new(new string('A', 64), ManagedSelectionLogCorrelation.MaximumScriptCharacters + 1, 1),
            new(new string('A', 64), 2, 4),
            new(new string('A', 64), 10, 0)
        };
        foreach (CompiledScriptIdentity? script in invalid)
        {
            ManagedSelectionLogCorrelation correlation = Open();
            AssertEx.False(correlation.TryObserveFirst(21, script, 11, 5, 4, out _));
            AssertEx.False(correlation.TryObserveFirst(22, Script, 11, 5, 4, out _));
            AssertEx.True(correlation.Pending == null);
        }
        AssertEx.False(Open().TryObserveFirst(22, Script, 11, 5, 4, out _));
    }

    public static void FreshSceneDriftOrUnavailableSceneConsumesOnlyThatCandidate()
    {
        EditorPreviewSceneAddress?[] drifts =
        {
            null,
            Scene with { ProjectKey = "other-project" },
            Scene with { NodeGuid = "other-node" },
            Scene with { SceneIndex = 0 },
            Scene with { Fingerprint = "changed-content" }
        };
        foreach (EditorPreviewSceneAddress? live in drifts)
        {
            ManagedSelectionLogCorrelation correlation = Open();
            ManagedSelectionLogCandidate candidate = Capture(correlation);
            AssertEx.False(correlation.TryComplete(candidate, 11, 5, live, out _));
            AssertEx.True(correlation.Pending == null);
            AssertEx.False(correlation.TryComplete(candidate, 11, 5, Scene, out _));
        }
    }

    public static void ConfirmationRejectsWrongThreadAndWindowRegression()
    {
        ManagedSelectionLogCorrelation wrongThread = Open();
        AssertEx.False(wrongThread.TryComplete(Capture(wrongThread), 22, 5, Scene, out _));
        ManagedSelectionLogCorrelation oldWindow = Open();
        AssertEx.False(oldWindow.TryComplete(Capture(oldWindow), 11, 4, Scene, out _));
    }

    public static void SameSelectionCascadeMayFinishBeforeThePumpButPublishesOnce()
    {
        ManagedSelectionLogCorrelation correlation = Open();
        ManagedSelectionLogCandidate candidate = Capture(correlation);
        // Windows 6..8 are observed siblings after the exact first-message
        // capture in 5. They do not themselves create or authorize an identity.
        AssertEx.True(correlation.TryComplete(candidate, 11, 8, Scene, out _));
        AssertEx.False(correlation.TryComplete(candidate, 11, 8, Scene, out _));
        AssertEx.True(correlation.Pending == null);
    }

    private static ManagedSelectionLogCorrelation Deferred(
        int requestId = 0,
        long issued = 4,
        long closed = 4,
        long selectionGeneration = 77)
    {
        var correlation = new ManagedSelectionLogCorrelation();
        correlation.Begin(requestId, 20, 11, issued, closed, FullScene, selectionGeneration);
        return correlation;
    }

    private static ManagedSelectionLogCandidate CaptureDeferred(
        ManagedSelectionLogCorrelation correlation,
        long issued = 5,
        long closed = 4)
    {
        AssertEx.False(correlation.TryObserveFirst(21, Script, 11, issued, closed, out string error));
        AssertEx.Equal("log-outside-data-list-open-window", error);
        AssertEx.True(correlation.Pending == null);
        AssertEx.True(correlation.DeferredPending != null);
        ManagedSelectionLogCandidate candidate = correlation.DeferredPending!;
        AssertEx.True(correlation.IsDeferredPending(candidate));
        AssertEx.False(correlation.IsPending(candidate));
        return candidate;
    }

    private static ManagedSelectionPreviewWindowProof PreviewProof(
        ManagedSelectionLogCandidate candidate,
        long windowSequence = 5,
        long issued = 8,
        long closed = 8) => new(
            candidate.Marker.SelectionGeneration,
            candidate.Marker.RequestId,
            candidate.Marker.ClosedWindowWatermark,
            windowSequence,
            Script,
            1,
            PlayerRuntimeContextSnapshot.FromPreviewMode(true),
            FullScene)
        {
            IssuedWindowSequence = issued,
            ClosedWindowWatermark = closed
        };

    public static void DeferredFirstLogRequiresExplicitEligiblePreviewProofAndPublishesOnce()
    {
        ManagedSelectionLogCorrelation correlation = Deferred();
        ManagedSelectionLogCandidate candidate = CaptureDeferred(correlation);
        AssertEx.Equal(77L, candidate.Marker.SelectionGeneration);
        AssertEx.Equal(0, candidate.Marker.RequestId);
        AssertEx.Equal(21L, candidate.LogSequence);
        AssertEx.Equal(Script, candidate.Script);
        AssertEx.Equal(5L, candidate.ObservedWindowSequence);
        AssertEx.Equal(4L, candidate.ObservedClosedWindowWatermark);
        AssertEx.Equal("no-open-window-at-data-list", candidate.TimingMismatchReason);
        AssertEx.Equal(candidate.TimingMismatchReason, correlation.FirstLogWindowMismatchReason);
        AssertEx.False(correlation.TryComplete(candidate, 11, 8, FullScene, out _));
        AssertEx.True(correlation.IsDeferredPending(candidate));
        AssertEx.False(correlation.TryObserveFirst(22, CommandIdentity.CompiledScript("later"),
            11, 5, 4, out _));
        AssertEx.Equal(candidate, correlation.DeferredPending!);
        ManagedSelectionPreviewWindowProof proof = PreviewProof(candidate);
        AssertEx.True(correlation.TryCompleteDeferred(candidate, 11, proof, out _));
        AssertEx.True(correlation.DeferredPending == null);
        AssertEx.False(correlation.TryCompleteDeferred(candidate, 11, proof, out _));
    }

    public static void OriginalBeginRemainsStrictOnlyAndStrictCaptureNeverDefers()
    {
        var oldContract = new ManagedSelectionLogCorrelation();
        ManagedSelectionLogMarker marker = oldContract.Begin(0, 20, 11, 4, 4, FullScene);
        AssertEx.Equal(0L, marker.SelectionGeneration);
        AssertEx.False(oldContract.TryObserveFirst(21, Script, 11, 5, 4, out string error));
        AssertEx.Equal("log-outside-data-list-open-window", error);
        AssertEx.Equal("no-open-window-at-data-list", oldContract.FirstLogWindowMismatchReason);
        AssertEx.True(oldContract.Pending == null && oldContract.DeferredPending == null);

        ManagedSelectionLogCorrelation strict = Deferred(0, 5, 4);
        ManagedSelectionLogCandidate candidate = Capture(strict);
        AssertEx.True(strict.DeferredPending == null);
        AssertEx.False(strict.TryCompleteDeferred(candidate, 11, PreviewProof(candidate), out _));
        AssertEx.True(strict.IsPending(candidate));
        AssertEx.True(strict.TryComplete(candidate, 11, 8, FullScene, out _));
    }

    public static void OverlappingOpenWindowTimingRetainsOnlyTheRealFirstLog()
    {
        (long Issued, long Closed, string Reason, long ProofWindow)[] cases =
        {
            (6, 4, "different-window-issued-after-data-list", 6),
            (6, 5, "different-window-issued-after-data-list", 6)
        };
        foreach (var timing in cases)
        {
            ManagedSelectionLogCorrelation correlation = Deferred(401, 5, 4);
            ManagedSelectionLogCandidate candidate = CaptureDeferred(correlation,
                timing.Issued, timing.Closed);
            AssertEx.Equal(timing.Reason, candidate.TimingMismatchReason);
            AssertEx.False(correlation.TryObserveFirst(22, Script, 11, 7, 6, out _));
            AssertEx.Equal(candidate, correlation.DeferredPending!);
            AssertEx.True(correlation.TryCompleteDeferred(candidate, 11,
                PreviewProof(candidate, timing.ProofWindow), out _));
        }
    }

    public static void FirstAfterCloseCannotBindEarlierWindowMessageOrFallBack()
    {
        (long MarkerIssued, long MarkerClosed, long ObservedIssued, long ObservedClosed)[] closed =
        {
            (5, 4, 5, 5),
            (5, 4, 6, 6),
            (4, 4, 4, 4)
        };
        foreach (var timing in closed)
        {
            var correlation = new ManagedSelectionLogCorrelation();
            ManagedSelectionLogMarker marker = correlation.Begin(401, 20, 11,
                timing.MarkerIssued, timing.MarkerClosed, FullScene, 77);
            AssertEx.False(correlation.TryObserveFirst(21, Script, 11,
                timing.ObservedIssued, timing.ObservedClosed, out string error));
            AssertEx.Equal("log-outside-data-list-open-window", error);
            AssertEx.True(correlation.Pending == null && correlation.DeferredPending == null);
            AssertEx.False(correlation.TryObserveFirst(22, Script, 11, 7, 6, out _));
            AssertEx.True(correlation.DeferredPending == null);

            // A matching string in an earlier closed container cannot prove
            // that this real first event happened inside that container.
            var fabricated = new ManagedSelectionLogCandidate(marker, 21, Script)
            {
                ObservedWindowSequence = timing.ObservedIssued,
                ObservedClosedWindowWatermark = timing.ObservedClosed
            };
            AssertEx.False(correlation.TryCompleteDeferred(fabricated, 11,
                PreviewProof(fabricated, timing.ObservedIssued), out _));
        }
    }

    public static void DeferredProofRequiresExactLeaseGenerationRequestAndOriginalCutoff()
    {
        Func<ManagedSelectionPreviewWindowProof, ManagedSelectionPreviewWindowProof>[] invalid =
        {
            proof => proof with { SelectionGeneration = 0 },
            proof => proof with { SelectionGeneration = proof.SelectionGeneration + 1 },
            proof => proof with { RequestId = -1 },
            proof => proof with { RequestId = 401 },
            proof => proof with { MinimumWindowSequenceExclusive = -1 },
            proof => proof with { MinimumWindowSequenceExclusive = 3 },
            proof => proof with { MinimumWindowSequenceExclusive = 5 }
        };
        foreach (var change in invalid)
        {
            ManagedSelectionLogCorrelation correlation = Deferred();
            ManagedSelectionLogCandidate candidate = CaptureDeferred(correlation);
            AssertEx.False(correlation.TryCompleteDeferred(candidate, 11,
                change(PreviewProof(candidate)), out string error));
            AssertEx.Equal("preview-proof-does-not-match-data-list-lease", error);
            AssertEx.True(correlation.DeferredPending == null);
            AssertEx.False(correlation.TryCompleteDeferred(candidate, 11, PreviewProof(candidate), out _));
        }
    }

    public static void DeferredProofMustMatchTheFirstScriptExactlyOnce()
    {
        Func<ManagedSelectionPreviewWindowProof, ManagedSelectionPreviewWindowProof>[] invalid =
        {
            proof => proof with { CompiledScript = CommandIdentity.CompiledScript("later compile") },
            proof => proof with { CompiledScript = Script with { Utf16Length = Script.Utf16Length + 1 } },
            proof => proof with { CompiledScript = Script with { LineCount = Script.LineCount + 1 } },
            proof => proof with { CompiledScript = Script with { Sha256 = "partial" } },
            proof => proof with { CompiledScript = null! },
            proof => proof with { ExactCompiledScriptMessageCount = 0 },
            proof => proof with { ExactCompiledScriptMessageCount = 2 },
            proof => proof with { ExactCompiledScriptMessageCount = -1 }
        };
        foreach (var change in invalid)
        {
            ManagedSelectionLogCorrelation correlation = Deferred();
            ManagedSelectionLogCandidate candidate = CaptureDeferred(correlation);
            AssertEx.False(correlation.TryCompleteDeferred(candidate, 11,
                change(PreviewProof(candidate)), out _));
            AssertEx.True(correlation.DeferredPending == null);
        }
    }

    public static void DeferredProofRequiresFreshUnchangedFullSceneIncludingProject()
    {
        EditorPreviewSceneAddress?[] invalid =
        {
            null,
            FullScene with { ProjectKey = new string('C', 24) },
            FullScene with { NodeGuid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee" },
            FullScene with { SceneIndex = 0 },
            FullScene with { Fingerprint = new string('C', 24) },
            FullScene with { ProjectKey = "" },
            FullScene with { NodeGuid = "node" },
            FullScene with { SceneIndex = -1 },
            FullScene with { Fingerprint = "" }
        };
        foreach (EditorPreviewSceneAddress? scene in invalid)
        {
            ManagedSelectionLogCorrelation correlation = Deferred();
            ManagedSelectionLogCandidate candidate = CaptureDeferred(correlation);
            AssertEx.False(correlation.TryCompleteDeferred(candidate, 11,
                PreviewProof(candidate) with { LiveScene = scene! }, out string error));
            AssertEx.Equal("live-scene-drift-from-data-list-capture", error);
            AssertEx.True(correlation.DeferredPending == null);
        }
    }

    public static void DeferredConfirmationRequiresMainThreadAndAllPreviewContextFields()
    {
        ManagedSelectionLogCorrelation wrongThread = Deferred();
        ManagedSelectionLogCandidate wrongThreadCandidate = CaptureDeferred(wrongThread);
        AssertEx.False(wrongThread.TryCompleteDeferred(wrongThreadCandidate, 22,
            PreviewProof(wrongThreadCandidate), out string error));
        AssertEx.Equal("confirmation-not-on-data-list-main-thread", error);
        AssertEx.True(wrongThread.DeferredPending == null);

        PlayerRuntimeContextSnapshot?[] invalid =
        {
            null,
            PlayerRuntimeContextSnapshot.Unavailable,
            PlayerRuntimeContextSnapshot.FromPreviewMode(false),
            new(false, true, PlayerRuntimeMode.EditorPreview),
            new(true, false, PlayerRuntimeMode.EditorPreview),
            new(true, true, PlayerRuntimeMode.Playback),
            new(true, true, (PlayerRuntimeMode)99)
        };
        foreach (PlayerRuntimeContextSnapshot? context in invalid)
        {
            ManagedSelectionLogCorrelation correlation = Deferred();
            ManagedSelectionLogCandidate candidate = CaptureDeferred(correlation);
            AssertEx.False(correlation.TryCompleteDeferred(candidate, 11,
                PreviewProof(candidate) with { RuntimeContext = context! }, out error));
            AssertEx.Equal("preview-proof-not-in-editor-preview-runtime", error);
            AssertEx.True(correlation.DeferredPending == null);
        }
    }

    public static void DeferredWindowsRequireEligibilityAndMonotonicIssuedAndClosedWatermarks()
    {
        Func<ManagedSelectionPreviewWindowProof, ManagedSelectionPreviewWindowProof>[] invalid =
        {
            proof => proof with { WindowSequence = -1 },
            proof => proof with { WindowSequence = 4 },
            proof => proof with { WindowSequence = 7 },
            proof => proof with { WindowSequence = 9 },
            proof => proof with { WindowSequence = 9, IssuedWindowSequence = 9 },
            proof => proof with { IssuedWindowSequence = 5, ClosedWindowWatermark = 5 },
            proof => proof with { ClosedWindowWatermark = 4 },
            proof => proof with { IssuedWindowSequence = -1, ClosedWindowWatermark = -1 },
            proof => proof with { ClosedWindowWatermark = -1 },
            proof => proof with { ClosedWindowWatermark = 9 }
        };
        foreach (var change in invalid)
        {
            ManagedSelectionLogCorrelation correlation = Deferred(401, 5, 4);
            ManagedSelectionLogCandidate candidate = CaptureDeferred(correlation, 6, 5);
            AssertEx.False(correlation.TryCompleteDeferred(candidate, 11,
                change(PreviewProof(candidate, 6)), out _));
            AssertEx.True(correlation.DeferredPending == null);
        }

        // Even an otherwise eligible window with the same script cannot
        // substitute for the exact window observed at the real first log.
        ManagedSelectionLogCorrelation sibling = Deferred(401, 5, 4);
        ManagedSelectionLogCandidate siblingCandidate = CaptureDeferred(sibling, 6, 5);
        AssertEx.False(sibling.TryCompleteDeferred(siblingCandidate, 11,
            PreviewProof(siblingCandidate, 5), out string siblingError));
        AssertEx.Equal("preview-proof-window-not-first-log-window", siblingError);

        ManagedSelectionLogCorrelation beforeWindow = Deferred();
        AssertEx.False(beforeWindow.TryObserveFirst(21, Script, 11, 4, 4, out _));
        AssertEx.True(beforeWindow.DeferredPending == null);
        AssertEx.False(beforeWindow.TryObserveFirst(22, Script, 11, 5, 4, out _));
    }

    public static void SupersededDeferredRequestCannotConsumeTheNewerReusedOpaqueRequest()
    {
        ManagedSelectionLogCorrelation correlation = Deferred(401);
        ManagedSelectionLogCandidate old = CaptureDeferred(correlation);
        ManagedSelectionPreviewWindowProof oldProof = PreviewProof(old);
        correlation.Begin(401, 21, 11, 6, 6, FullScene, 78);
        AssertEx.Equal(string.Empty, correlation.FirstLogWindowMismatchReason);
        AssertEx.False(correlation.TryObserveFirst(22, Script, 11, 7, 6, out _));
        ManagedSelectionLogCandidate current = correlation.DeferredPending!;
        AssertEx.False(correlation.TryCompleteDeferred(old, 11, oldProof, out _));
        AssertEx.Equal(current, correlation.DeferredPending!);
        AssertEx.True(correlation.TryCompleteDeferred(current, 11, PreviewProof(current, 7), out _));

        ManagedSelectionLogCandidate newerOld = current;
        correlation.Begin(401, 22, 11, 8, 7, FullScene, 79);
        AssertEx.True(correlation.DeferredPending == null);
        AssertEx.True(correlation.TryObserveFirst(23, Script, 11, 8, 7, out _));
        ManagedSelectionLogCandidate strict = correlation.Pending!;
        AssertEx.False(correlation.TryCompleteDeferred(newerOld, 11, PreviewProof(newerOld), out _));
        AssertEx.True(correlation.IsPending(strict));
        AssertEx.True(correlation.TryComplete(strict, 11, 8, FullScene, out _));
    }

    public static void PriorStrictConfirmationCannotSupplyTheNewReusedRequestGeneration()
    {
        static (ManagedSelectionLogCorrelation Correlation,
            ManagedSelectionLogCandidate Confirmed,
            ManagedSelectionLogCandidate Current) PriorStrictThenDeferred()
        {
            var correlation = new ManagedSelectionLogCorrelation();
            correlation.Begin(401, 20, 11, 5, 4, FullScene, 77);
            ManagedSelectionLogCandidate confirmed = Capture(correlation);
            AssertEx.True(correlation.TryComplete(confirmed, 11, 5, FullScene, out _));
            correlation.Begin(401, 21, 11, 5, 5, FullScene, 78);
            AssertEx.False(correlation.TryObserveFirst(22, Script, 11, 6, 5, out _));
            AssertEx.True(correlation.DeferredPending != null);
            return (correlation, confirmed, correlation.DeferredPending!);
        }

        foreach (bool usePreviousGeneration in new[] { false, true })
        {
            var (correlation, confirmed, current) = PriorStrictThenDeferred();
            AssertEx.Equal(confirmed.Marker.RequestId, current.Marker.RequestId);
            AssertEx.Equal(confirmed.Script, current.Script);
            AssertEx.Equal(confirmed.Marker.CapturedScene!, current.Marker.CapturedScene!);
            AssertEx.True(current.Marker.Generation > confirmed.Marker.Generation);
            AssertEx.True(current.Marker.SelectionGeneration > confirmed.Marker.SelectionGeneration);
            AssertEx.False(correlation.TryComplete(confirmed, 11, 8, FullScene, out _));
            AssertEx.False(correlation.TryCompleteDeferred(confirmed, 11,
                PreviewProof(confirmed), out _));
            AssertEx.True(correlation.IsDeferredPending(current));
            AssertEx.False(correlation.TryComplete(current, 11, 8, FullScene, out _));
            AssertEx.True(correlation.IsDeferredPending(current));

            ManagedSelectionPreviewWindowProof proof = PreviewProof(current, 6);
            if (usePreviousGeneration)
            {
                AssertEx.False(correlation.TryCompleteDeferred(current, 11,
                    proof with { SelectionGeneration = confirmed.Marker.SelectionGeneration }, out string error));
                AssertEx.Equal("preview-proof-does-not-match-data-list-lease", error);
                AssertEx.True(correlation.DeferredPending == null);
                AssertEx.False(correlation.TryCompleteDeferred(current, 11, proof, out _));
            }
            else
            {
                AssertEx.True(correlation.TryCompleteDeferred(current, 11, proof, out _));
                AssertEx.True(correlation.DeferredPending == null);
            }
        }
    }

    public static void InvalidBackgroundMissingOrSkippedFirstLogCannotCreateDeferredIdentity()
    {
        CompiledScriptIdentity?[] invalid =
        {
            null,
            new("partial", 10, 1),
            new(new string('A', 64), -1, 1),
            new(new string('A', 64), ManagedSelectionLogCorrelation.MaximumScriptCharacters + 1, 1),
            new(new string('A', 64), 2, 4),
            new(new string('A', 64), 10, 0)
        };
        foreach (CompiledScriptIdentity? script in invalid)
        {
            ManagedSelectionLogCorrelation correlation = Deferred();
            AssertEx.False(correlation.TryObserveFirst(21, script, 11, 5, 4, out _));
            AssertEx.False(correlation.TryObserveFirst(22, Script, 11, 5, 4, out _));
            AssertEx.True(correlation.Pending == null && correlation.DeferredPending == null);
        }

        foreach ((long sequence, int thread) in new[] { (21L, 22), (22L, 11) })
        {
            ManagedSelectionLogCorrelation correlation = Deferred();
            AssertEx.False(correlation.TryObserveFirst(sequence, Script, thread, 5, 4, out _));
            AssertEx.False(correlation.TryObserveFirst(23, Script, 11, 5, 4, out _));
            AssertEx.True(correlation.Pending == null && correlation.DeferredPending == null);
        }

        foreach (EditorPreviewSceneAddress? scene in new EditorPreviewSceneAddress?[] { null, Scene })
        {
            var correlation = new ManagedSelectionLogCorrelation();
            correlation.Begin(0, 20, 11, 4, 4, scene, 77);
            AssertEx.False(correlation.TryObserveFirst(21, Script, 11, 5, 4, out _));
            AssertEx.True(correlation.DeferredPending == null);
        }

        var noMainThread = new ManagedSelectionLogCorrelation();
        noMainThread.Begin(0, 20, 0, 4, 4, FullScene, 77);
        AssertEx.False(noMainThread.TryObserveFirst(21, Script, 11, 5, 4, out _));
        AssertEx.False(noMainThread.TryObserveFirst(22, Script, 11, 5, 4, out _));
        AssertEx.True(noMainThread.DeferredPending == null);

        foreach (long generation in new[] { 0L, -1L })
        {
            ManagedSelectionLogCorrelation noGeneration = Deferred(selectionGeneration: generation);
            AssertEx.False(noGeneration.TryObserveFirst(21, Script, 11, 5, 4, out _));
            AssertEx.True(noGeneration.DeferredPending == null);
        }

        ManagedSelectionLogCorrelation noLog = Deferred();
        // A lease-derived hash and even a structurally complete preview proof
        // cannot manufacture a real first-log candidate.
        ManagedSelectionLogMarker marker = noLog.Begin(0, 20, 11, 4, 4, FullScene, 77);
        var fabricated = new ManagedSelectionLogCandidate(marker, 21, Script)
        {
            ObservedWindowSequence = 4,
            ObservedClosedWindowWatermark = 4
        };
        AssertEx.False(noLog.TryCompleteDeferred(fabricated, 11, PreviewProof(fabricated), out _));
        AssertEx.True(noLog.DeferredPending == null && noLog.Pending == null);
        ManagedSelectionLogCandidate real = CaptureDeferred(noLog);
        AssertEx.True(noLog.TryCompleteDeferred(real, 11, PreviewProof(real), out _));
    }

    public static void RegressedOrInvalidTimingCannotBeHeldForDeferredProof()
    {
        (long MarkerIssued, long MarkerClosed, long ObservedIssued, long ObservedClosed)[] invalid =
        {
            (5, 4, 4, 4),
            (5, 4, 5, 3),
            (5, 4, 5, 6),
            (4, 5, 6, 5),
            (5, -1, 5, 0),
            (-1, -1, 0, 0)
        };
        foreach (var timing in invalid)
        {
            ManagedSelectionLogCorrelation correlation = Deferred(401,
                timing.MarkerIssued, timing.MarkerClosed);
            AssertEx.False(correlation.TryObserveFirst(21, Script, 11,
                timing.ObservedIssued, timing.ObservedClosed, out string error));
            AssertEx.Equal("log-outside-data-list-open-window", error);
            AssertEx.True(correlation.FirstLogWindowMismatchReason.Length != 0);
            AssertEx.True(correlation.Pending == null && correlation.DeferredPending == null);
        }
    }
}
