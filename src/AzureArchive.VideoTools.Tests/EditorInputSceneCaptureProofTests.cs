using AzureArchive.VideoTools.Core.Commands;
using Rukari.Lib.Editor;

namespace AzureArchive.VideoTools.Tests;

internal static class EditorInputSceneCaptureProofTests
{
    private static EditorInputSceneFrameContext Context() => new(
        0x11, 0x22, 0x33, 0x44, 0x55, 0x66, "actual-project", "actual-node", 8, false);

    private static EditorInputSceneRowProof Row(int index = 3) => new(
        0x100 + index, 0x11, 0x22, index, 0x200 + index,
        true, false, "actual dialogue", "#aavt;char;position;5;1;2;3");

    private static EditorInputSceneFrame Frame(params EditorInputSceneRowProof[] rows) => new(
        Context(), "actual dialogue", "#aavt;char;position;5;1;2;3",
        rows.Length == 0 ? new[] { Row(), Row(4) with { DialogueText = "other dialogue" } } : rows);

    private static EditorInputSceneCaptureProof Capture(EditorInputSceneFrame? frame = null)
    {
        EditorInputSceneFrame actual = frame ?? Frame();
        return AssertEx.NotNull(EditorInputSceneCapturePlanner.Capture(actual, actual).Value);
    }

    public static void UniqueSynchronizedInputsSelectTheActualRowContext()
    {
        EditorInputSceneFrame before = Frame();
        EditorInputSceneFrame after = before with { Rows = before.Rows.ToArray() };
        var result = EditorInputSceneCapturePlanner.Capture(before, after);
        AssertEx.True(result.Success, result.Error);
        EditorInputSceneCaptureProof proof = AssertEx.NotNull(result.Value);
        AssertEx.Equal("actual-project", proof.ProjectKey);
        AssertEx.Equal("actual-node", proof.NodeGuid);
        AssertEx.Equal(3, proof.SceneIndex);
        AssertEx.Equal("11:22:203:3", proof.ContextId);
        AssertEx.Equal(before.DialogueInput, proof.DialogueText);
        AssertEx.Equal(before.AdditionalPromptInput, proof.AdditionalPrompt);
    }

    public static void AnEstablishedOrAmbiguousSelectionCannotUseTheFallback()
    {
        EditorInputSceneFrame one = Frame(Row() with { Selected = true });
        AssertEx.False(EditorInputSceneCapturePlanner.Capture(one, one).Success);
        EditorInputSceneFrame two = Frame(Row() with { Selected = true }, Row(4) with { Selected = true });
        AssertEx.False(EditorInputSceneCapturePlanner.Capture(two, two).Success);
    }

    public static void EveryActiveRowNeedsValidOwnershipAndScriptMembership()
    {
        EditorInputSceneRowProof[] invalid =
        {
            Row(4) with { InspectorPointer = 0x77 },
            Row(4) with { NodePointer = 0x77 },
            Row(4) with { SceneIndex = -1 },
            Row(4) with { SceneIndex = 8 },
            Row(4) with { RowPointer = 0 },
            Row(4) with { ScriptPointer = 0 },
            Row(4) with { DialogueText = null },
            Row(4) with { AdditionalPrompt = null }
        };
        foreach (EditorInputSceneRowProof row in invalid)
        {
            // Even a malformed nonmatching row cannot be skipped to manufacture uniqueness.
            EditorInputSceneFrame frame = Frame(Row(), row with { DialogueText = row.DialogueText == null ? null : "other" });
            AssertEx.False(EditorInputSceneCapturePlanner.Capture(frame, frame).Success);
        }
    }

    public static void DuplicateActualIndicesAreRejectedEvenWithOneMatchingInput()
    {
        EditorInputSceneFrame frame = Frame(Row(), Row() with { RowPointer = 0x999, DialogueText = "other" });
        AssertEx.False(EditorInputSceneCapturePlanner.Capture(frame, frame).Success);
        frame = Frame(Row(), Row(4) with { RowPointer = Row().RowPointer, DialogueText = "other" });
        AssertEx.False(EditorInputSceneCapturePlanner.Capture(frame, frame).Success);
    }

    public static void DialogueAndPromptMatchingAreExactAndOrdinal()
    {
        EditorInputSceneFrame original = Frame();
        EditorInputSceneFrame[] unmatched =
        {
            original with { DialogueInput = "Actual dialogue" },
            original with { DialogueInput = "actual dialogue " },
            original with { AdditionalPromptInput = original.AdditionalPromptInput + "\n" },
            original with { AdditionalPromptInput = "#aavt;char;position;5;1;2;4" }
        };
        foreach (EditorInputSceneFrame frame in unmatched)
            AssertEx.False(EditorInputSceneCapturePlanner.Capture(frame, frame).Success);
    }

    public static void MultipleCompleteInputMatchesRemainAmbiguousIncludingEmptyRows()
    {
        EditorInputSceneFrame frame = Frame(Row(), Row(4));
        AssertEx.False(EditorInputSceneCapturePlanner.Capture(frame, frame).Success);
        frame = frame with
        {
            DialogueInput = string.Empty,
            AdditionalPromptInput = string.Empty,
            Rows = new[]
            {
                Row() with { DialogueText = string.Empty, AdditionalPrompt = string.Empty },
                Row(4) with { DialogueText = string.Empty, AdditionalPrompt = string.Empty }
            }
        };
        AssertEx.False(EditorInputSceneCapturePlanner.Capture(frame, frame).Success);
        frame = frame with { Rows = new[] { frame.Rows[0] } };
        AssertEx.True(EditorInputSceneCapturePlanner.Capture(frame, frame).Success);
    }

    public static void HiddenRowsCannotSupplyOrAmbiguateTheVisibleMatch()
    {
        EditorInputSceneFrame frame = Frame(Row(), Row(4) with { Active = false, Selected = true });
        AssertEx.True(EditorInputSceneCapturePlanner.Capture(frame, frame).Success);
        frame = Frame(Row() with { Active = false });
        AssertEx.False(EditorInputSceneCapturePlanner.Capture(frame, frame).Success);
    }

    public static void AnyFramePointerOrProjectChangeRejectsTheCapture()
    {
        EditorInputSceneFrame before = Frame();
        EditorInputSceneFrameContext context = before.Context;
        EditorInputSceneFrameContext[] drifts =
        {
            context with { InspectorPointer = 0x77 }, context with { NodePointer = 0x77 },
            context with { CachePointer = 0x77 }, context with { ScriptsPointer = 0x77 },
            context with { ContentInputPointer = 0x77 }, context with { AdditionalPromptInputPointer = 0x77 },
            context with { ProjectKey = "other-project" }, context with { NodeGuid = "other-node" },
            context with { ScriptCount = 9 }
        };
        foreach (EditorInputSceneFrameContext drift in drifts)
            AssertEx.False(EditorInputSceneCapturePlanner.Capture(before, before with { Context = drift }).Success);
    }

    public static void InputsOrRowsChangingDuringTheReadCannotBecomeASnapshot()
    {
        EditorInputSceneFrame before = Frame();
        EditorInputSceneFrame[] drifts =
        {
            before with { DialogueInput = "other" },
            before with { AdditionalPromptInput = "other" },
            before with { Rows = before.Rows.Reverse().ToArray() },
            before with { Rows = new[] { before.Rows[0] } },
            before with { Rows = new[] { Row() with { ScriptPointer = 0x999 }, before.Rows[1] } },
            before with { Rows = new[] { Row() with { AdditionalPrompt = "changed" }, before.Rows[1] } }
        };
        foreach (EditorInputSceneFrame after in drifts)
            AssertEx.False(EditorInputSceneCapturePlanner.Capture(before, after).Success);
    }

    public static void MissingInputsContextOrRearrangingCacheCannotCapture()
    {
        EditorInputSceneFrame frame = Frame();
        AssertEx.False(EditorInputSceneCapturePlanner.Capture(null, frame).Success);
        AssertEx.False(EditorInputSceneCapturePlanner.Capture(frame, null).Success);
        EditorInputSceneFrame[] invalid =
        {
            frame with { DialogueInput = null }, frame with { AdditionalPromptInput = null },
            frame with { Rows = null! }, frame with { Context = null! },
            frame with { Context = frame.Context with { RearrangeScheduled = true } },
            frame with { Context = frame.Context with { ContentInputPointer = 0 } },
            frame with { Context = frame.Context with { ScriptCount = 0 } }
        };
        foreach (EditorInputSceneFrame item in invalid)
            AssertEx.False(EditorInputSceneCapturePlanner.Capture(item, item).Success);
    }

    public static void SharedConfirmationRequiresTheCapturedContextAndBothCompleteTexts()
    {
        EditorInputSceneCaptureProof proof = Capture();
        var actual = new EditorDocumentSnapshot("actual-service-token", proof.ContextId, "actual-revision",
            proof.DialogueText, proof.AdditionalPrompt, false);
        AssertEx.True(EditorInputSceneCapturePlanner.Confirm(proof, actual).Success);
        // In particular, prompt drift must fail even when the scene address/dialogue did not change.
        EditorDocumentSnapshot[] changed =
        {
            actual with { ContextId = "11:22:204:4" },
            actual with { DialogueText = proof.DialogueText + " " },
            actual with { AdditionalPrompt = proof.AdditionalPrompt + "\n" }
        };
        foreach (EditorDocumentSnapshot snapshot in changed)
            AssertEx.False(EditorInputSceneCapturePlanner.Confirm(proof, snapshot).Success);
    }

    public static void MissingSharedSnapshotNeverConfirmsAProvisionalRow()
    {
        EditorInputSceneCaptureProof proof = Capture();
        AssertEx.False(EditorInputSceneCapturePlanner.Confirm(proof, null).Success);
        var actual = new EditorDocumentSnapshot("actual-token", proof.ContextId, "actual-revision",
            proof.DialogueText, proof.AdditionalPrompt, false);
        AssertEx.False(EditorInputSceneCapturePlanner.Confirm(null, actual).Success);
    }
}
