using AzureArchive.VideoTools.Core.Commands;
using Rukari.Lib.Editor;

namespace AzureArchive.VideoTools.Tests;

internal static class EditorSelectedSceneCaptureProofTests
{
    private static readonly CompiledScriptIdentity Script = CommandIdentity.CompiledScript("real first compiled script");

    public static void TwoFreshSelectedReadsRetainTheCompleteInputProof()
    {
        EditorSelectedSceneCaptureFrame first = Frame();
        EditorSelectedSceneCaptureFrame second = first with
        {
            Scene = first.Scene! with { },
            Document = first.Document! with { }
        };
        var captured = EditorSelectedSceneCapturePlanner.Capture(first, second);
        AssertEx.True(captured.Success, captured.Error);
        EditorInputSceneCaptureProof proof = AssertEx.NotNull(captured.Value);
        EditorPreviewSceneAddress scene = first.Scene!;
        EditorDocumentSnapshot document = first.Document!;
        AssertEx.Equal(scene.ProjectKey, proof.ProjectKey);
        AssertEx.Equal(scene.NodeGuid, proof.NodeGuid);
        AssertEx.Equal(scene.SceneIndex, proof.SceneIndex);
        AssertEx.Equal(document.ContextId, proof.ContextId);
        AssertEx.Equal(document.DialogueText, proof.DialogueText);
        AssertEx.Equal(document.AdditionalPrompt, proof.AdditionalPrompt);
        AssertEx.True(EditorInputSceneCapturePlanner.Confirm(proof, second.Document).Success);
    }

    public static void TokenRotationAndEmptyInputsDoNotEraseActualContextEvidence()
    {
        EditorSelectedSceneCaptureFrame first = Frame() with { DialogueText = string.Empty };
        first = first with
        {
            Document = first.Document! with { DialogueText = string.Empty, AdditionalPrompt = string.Empty }
        };
        EditorSelectedSceneCaptureFrame second = first with
        {
            Document = first.Document! with
            {
                SelectionToken = "fresh-token-after-data-list",
                Revision = "fresh-document-revision",
                CanUndo = true
            }
        };
        var captured = EditorSelectedSceneCapturePlanner.Capture(first, second);
        AssertEx.True(captured.Success, captured.Error);
        EditorInputSceneCaptureProof proof = AssertEx.NotNull(captured.Value);
        AssertEx.Equal("11:22:203:3", proof.ContextId);
        AssertEx.Equal(string.Empty, proof.DialogueText);
        AssertEx.Equal(string.Empty, proof.AdditionalPrompt);
        AssertEx.True(EditorDataListCascadePlanner.CanReuse(
            Cascade(first, proof), Cascade(second, proof with { }), out string error), error);
    }

    public static void EveryFullSceneDimensionMustSurviveTheSecondSelectedRead()
    {
        EditorSelectedSceneCaptureFrame first = Frame();
        EditorPreviewSceneAddress scene = first.Scene!;
        EditorPreviewSceneAddress[] drifted =
        {
            scene with { ProjectKey = new string('C', 24) },
            scene with { NodeGuid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee" },
            scene with { SceneIndex = 4 },
            scene with { Fingerprint = new string('D', 24) }
        };
        foreach (EditorPreviewSceneAddress drift in drifted)
        {
            var result = EditorSelectedSceneCapturePlanner.Capture(first, first with { Scene = drift });
            AssertEx.False(result.Success);
            AssertEx.Equal("selected-scene-capture-drift", result.Error);
            AssertEx.True(result.Value == null);
        }
    }

    public static void ContextDialogueAndPromptDriftCannotProduceASelectedProof()
    {
        EditorSelectedSceneCaptureFrame first = Frame();
        EditorDocumentSnapshot document = first.Document!;
        EditorDocumentSnapshot[] drifted =
        {
            document with { ContextId = "11:22:204:4" },
            document with { DialogueText = document.DialogueText + " " },
            document with { AdditionalPrompt = document.AdditionalPrompt + "\n" }
        };
        foreach (EditorDocumentSnapshot drift in drifted)
        {
            // Each read is internally consistent, but the two actual reads disagree.
            var result = EditorSelectedSceneCapturePlanner.Capture(first,
                first with { DialogueText = drift.DialogueText, Document = drift });
            AssertEx.False(result.Success);
            AssertEx.Equal("selected-scene-capture-drift", result.Error);
            AssertEx.True(result.Value == null);
        }
    }

    public static void MissingOrMalformedSelectedEvidenceFailsEvenWhenBothReadsMatch()
    {
        EditorSelectedSceneCaptureFrame valid = Frame();
        EditorPreviewSceneAddress scene = valid.Scene!;
        EditorDocumentSnapshot document = valid.Document!;
        EditorSelectedSceneCaptureFrame?[] invalid =
        {
            null,
            valid with { Scene = null },
            valid with { Scene = scene with { ProjectKey = null! } },
            valid with { Scene = scene with { ProjectKey = new string('A', 23) } },
            valid with { Scene = scene with { ProjectKey = new string('G', 24) } },
            valid with { Scene = scene with { NodeGuid = null! } },
            valid with { Scene = scene with { NodeGuid = "11111111222233334444555555555555" } },
            valid with { Scene = scene with { SceneIndex = -1 } },
            valid with { Scene = scene with { Fingerprint = null! } },
            valid with { Scene = scene with { Fingerprint = new string('B', 64) } },
            valid with { Scene = scene with { Fingerprint = new string('Z', 24) } },
            valid with { DialogueText = null },
            valid with { Document = null },
            valid with { Document = document with { ContextId = null! } },
            valid with { Document = document with { ContextId = string.Empty } },
            valid with { Document = document with { ContextId = " \t" } },
            valid with { Document = document with { DialogueText = null! } },
            valid with { Document = document with { AdditionalPrompt = null! } }
        };
        foreach (EditorSelectedSceneCaptureFrame? incomplete in invalid)
        {
            AssertEx.False(EditorSelectedSceneCapturePlanner.Capture(incomplete, valid).Success);
            AssertEx.False(EditorSelectedSceneCapturePlanner.Capture(valid, incomplete).Success);
            AssertEx.False(EditorSelectedSceneCapturePlanner.Capture(incomplete, incomplete).Success);
        }
    }

    public static void SharedDialogueMustMatchTheActualSelectedSceneSnapshot()
    {
        EditorSelectedSceneCaptureFrame valid = Frame();
        EditorSelectedSceneCaptureFrame[] inconsistent =
        {
            valid with { DialogueText = valid.DialogueText + " " },
            valid with { Document = valid.Document! with { DialogueText = "other official dialogue" } }
        };
        foreach (EditorSelectedSceneCaptureFrame frame in inconsistent)
        {
            var result = EditorSelectedSceneCapturePlanner.Capture(frame, frame);
            AssertEx.False(result.Success);
            AssertEx.Equal("selected-scene-capture-dialogue-mismatch", result.Error);
        }
    }

    public static void SelectedProofMatchesTheExistingFallbackFormatWithoutRelaxingItsGuard()
    {
        EditorSelectedSceneCaptureFrame selected = Frame();
        EditorInputSceneCaptureProof proof = Capture(selected);
        var context = new EditorInputSceneFrameContext(0x11, 0x22, 0x33, 0x44, 0x55, 0x66,
            proof.ProjectKey, proof.NodeGuid, 8, false);
        var row = new EditorInputSceneRowProof(0x103, 0x11, 0x22, 3, 0x203,
            true, false, proof.DialogueText, proof.AdditionalPrompt);
        var frame = new EditorInputSceneFrame(context, proof.DialogueText, proof.AdditionalPrompt, new[] { row });
        var fallback = EditorInputSceneCapturePlanner.Capture(frame, frame);
        AssertEx.True(fallback.Success, fallback.Error);
        AssertEx.Equal(proof, AssertEx.NotNull(fallback.Value));
        AssertEx.True(EditorDataListCascadePlanner.CanReuse(
            Cascade(selected, fallback.Value), Cascade(selected, proof), out string error), error);

        EditorInputSceneFrame stillSelected = frame with { Rows = new[] { row with { Selected = true } } };
        var forbidden = EditorInputSceneCapturePlanner.Capture(stillSelected, stillSelected);
        AssertEx.False(forbidden.Success);
        AssertEx.Equal("input-scene-selected-row-present", forbidden.Error);
    }

    public static void RepeatedSelectedDataListPreservesTheRealFirstLogAndCompletesOnce()
    {
        EditorSelectedSceneCaptureFrame selected = Frame();
        EditorDataListCascadeProof previous = Cascade(selected, Capture(selected));
        var correlation = new ManagedSelectionLogCorrelation();
        ManagedSelectionLogMarker original = Begin(correlation, previous, 20, 8);
        AssertEx.True(correlation.TryObserveFirst(21, Script, previous.MainThreadId,
            previous.IssuedWindowSequence, previous.ClosedWindowWatermark, out _));
        ManagedSelectionLogCandidate first = AssertEx.NotNull(correlation.Pending);

        EditorSelectedSceneCaptureFrame fresh = selected with
        {
            Document = selected.Document! with { SelectionToken = "fresh-selected-row-token" }
        };
        EditorDataListCascadeProof duplicate = Cascade(fresh, Capture(fresh));
        AssertEx.True(EditorDataListCascadePlanner.CanReuse(previous, duplicate, out string error), error);
        if (!EditorDataListCascadePlanner.CanReuse(previous, duplicate, out _))
            Begin(correlation, duplicate, 21, 9);

        AssertEx.Equal(first, AssertEx.NotNull(correlation.Pending));
        AssertEx.Equal(original, first.Marker);
        AssertEx.Equal(8L, first.Marker.SelectionGeneration);
        AssertEx.False(correlation.TryObserveFirst(22, Script, duplicate.MainThreadId,
            duplicate.IssuedWindowSequence, duplicate.ClosedWindowWatermark, out _));
        AssertEx.True(correlation.TryComplete(first, duplicate.MainThreadId,
            duplicate.IssuedWindowSequence, duplicate.Scene, out _));
        AssertEx.False(correlation.TryComplete(first, duplicate.MainThreadId,
            duplicate.IssuedWindowSequence, duplicate.Scene, out _));
    }

    public static void ChangedSelectedEvidenceStartsFreshWithoutBorrowingTheOldFirstLog()
    {
        EditorSelectedSceneCaptureFrame selected = Frame();
        EditorDataListCascadeProof previous = Cascade(selected, Capture(selected));
        EditorSelectedSceneCaptureFrame[] changed =
        {
            selected with { Document = selected.Document! with { AdditionalPrompt = "changed authored directives" } },
            selected with { Document = selected.Document! with { ContextId = "11:22:303:3" } },
            selected with { Scene = selected.Scene! with { Fingerprint = new string('D', 24) } },
            selected with { Scene = selected.Scene! with { SceneIndex = 4 },
                Document = selected.Document! with { ContextId = "11:22:204:4" } }
        };
        foreach (EditorSelectedSceneCaptureFrame fresh in changed)
        {
            var correlation = new ManagedSelectionLogCorrelation();
            Begin(correlation, previous, 20, 8);
            AssertEx.True(correlation.TryObserveFirst(21, Script, previous.MainThreadId,
                previous.IssuedWindowSequence, previous.ClosedWindowWatermark, out _));
            ManagedSelectionLogCandidate old = AssertEx.NotNull(correlation.Pending);

            EditorDataListCascadeProof current = Cascade(fresh, Capture(fresh));
            AssertEx.False(EditorDataListCascadePlanner.CanReuse(previous, current, out _));
            Begin(correlation, current, 21, 9);
            AssertEx.True(correlation.Pending == null && correlation.DeferredPending == null);
            AssertEx.False(correlation.TryComplete(old, current.MainThreadId,
                current.IssuedWindowSequence, current.Scene, out _));
            // An unchanged opaque request and compiled hash cannot supply the new generation's FIRST.
            AssertEx.True(correlation.TryObserveFirst(22, Script, current.MainThreadId,
                current.IssuedWindowSequence, current.ClosedWindowWatermark, out _));
            ManagedSelectionLogCandidate actual = AssertEx.NotNull(correlation.Pending);
            AssertEx.Equal(9L, actual.Marker.SelectionGeneration);
            AssertEx.True(correlation.TryComplete(actual, current.MainThreadId,
                current.IssuedWindowSequence, current.Scene, out _));
        }
    }

    public static void DuplicateSelectedEvidenceCannotRearmAnInvalidFirstLog()
    {
        EditorSelectedSceneCaptureFrame selected = Frame();
        EditorDataListCascadeProof proof = Cascade(selected, Capture(selected));
        var correlation = new ManagedSelectionLogCorrelation();
        ManagedSelectionLogMarker marker = Begin(correlation, proof, 20, 8);
        AssertEx.False(correlation.TryObserveFirst(21, null, proof.MainThreadId,
            proof.IssuedWindowSequence, proof.ClosedWindowWatermark, out _));

        EditorDataListCascadeProof duplicate = Cascade(selected, Capture(selected));
        AssertEx.True(EditorDataListCascadePlanner.CanReuse(proof, duplicate, out string error), error);
        if (!EditorDataListCascadePlanner.CanReuse(proof, duplicate, out _))
            Begin(correlation, duplicate, 21, 9);
        AssertEx.False(correlation.TryObserveFirst(22, Script, proof.MainThreadId,
            proof.IssuedWindowSequence, proof.ClosedWindowWatermark, out error));
        AssertEx.Equal("first-log-already-observed", error);
        AssertEx.True(correlation.Pending == null && correlation.DeferredPending == null);
        AssertEx.False(correlation.TryComplete(new ManagedSelectionLogCandidate(marker, 21, Script),
            proof.MainThreadId, proof.IssuedWindowSequence, proof.Scene, out _));
    }

    private static EditorSelectedSceneCaptureFrame Frame()
    {
        var scene = new EditorPreviewSceneAddress(new string('A', 24),
            "11111111-2222-3333-4444-555555555555", 3, new string('B', 24));
        return new EditorSelectedSceneCaptureFrame(scene, "actual selected dialogue",
            new EditorDocumentSnapshot("actual-selection-token", "11:22:203:3", "actual-revision",
                "actual selected dialogue", "#aavt;char;position;5;1;2;3", false));
    }

    private static EditorInputSceneCaptureProof Capture(EditorSelectedSceneCaptureFrame frame)
    {
        var captured = EditorSelectedSceneCapturePlanner.Capture(frame, frame with { });
        AssertEx.True(captured.Success, captured.Error);
        return AssertEx.NotNull(captured.Value);
    }

    private static EditorDataListCascadeProof Cascade(
        EditorSelectedSceneCaptureFrame frame, EditorInputSceneCaptureProof? proof) =>
        new(0, 11, 35, 35, 34, frame.Scene, proof);

    private static ManagedSelectionLogMarker Begin(ManagedSelectionLogCorrelation correlation,
        EditorDataListCascadeProof proof, long startLogSequence, long generation) => correlation.Begin(
            proof.RequestId, startLogSequence, proof.MainThreadId, proof.IssuedWindowSequence,
            proof.ClosedWindowWatermark, proof.Scene, generation);
}
