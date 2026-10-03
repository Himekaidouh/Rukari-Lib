using AzureArchive.VideoTools.Core.Commands;

namespace AzureArchive.VideoTools.Tests;

internal static class EditorDataListCascadeProofTests
{
    private static readonly CompiledScriptIdentity Script = CommandIdentity.CompiledScript("real first compiled script");

    public static void CompleteEqualProofReusesZeroOpaqueRequestAndEmptyInputs()
    {
        EditorDataListCascadeProof previous = Proof();
        EditorDataListCascadeProof current = previous with
        {
            Scene = previous.Scene! with { },
            InputProof = previous.InputProof! with { }
        };
        AssertEx.True(EditorDataListCascadePlanner.CanReuse(previous, current, out string error), error);
        AssertEx.Equal(string.Empty, error);
        AssertEx.Equal(0, current.RequestId);
        AssertEx.Equal(9, current.Scene!.SceneIndex);

        EditorDataListCascadeProof empty = previous with
        {
            InputProof = previous.InputProof! with { DialogueText = string.Empty, AdditionalPrompt = string.Empty }
        };
        AssertEx.True(EditorDataListCascadePlanner.CanReuse(empty, empty with { }, out error), error);
    }

    public static void EveryCapturedDimensionMustRemainIdentical()
    {
        EditorDataListCascadeProof previous = Proof();
        EditorPreviewSceneAddress scene = previous.Scene!;
        EditorInputSceneCaptureProof input = previous.InputProof!;
        EditorDataListCascadeProof[] drifted =
        {
            previous with { RequestId = 403 },
            previous with { MainThreadId = 12 },
            previous with { ActiveWindowSequence = 36, IssuedWindowSequence = 36 },
            previous with { IssuedWindowSequence = 36 },
            previous with { ClosedWindowWatermark = 33 },
            WithScene(previous, scene with { ProjectKey = new string('C', 24) }),
            WithScene(previous, scene with { NodeGuid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee" }),
            WithScene(previous, scene with { SceneIndex = 10 }),
            previous with { Scene = scene with { Fingerprint = new string('D', 24) } },
            previous with { InputProof = input with { ContextId = "A:B:C:9" } },
            previous with { InputProof = input with { DialogueText = "changed dialogue" } },
            previous with { InputProof = input with { AdditionalPrompt = "changed prompt" } }
        };
        foreach (EditorDataListCascadeProof current in drifted)
        {
            AssertEx.False(EditorDataListCascadePlanner.CanReuse(previous, current, out string error));
            AssertEx.True(error.Length != 0);
            AssertEx.False(EditorDataListCascadePlanner.CanReuse(current, previous, out _));
        }
    }

    public static void ClosedNewAndRegressedWindowsNeverReuseTheOriginalEpisode()
    {
        EditorDataListCascadeProof previous = Proof();
        EditorDataListCascadeProof[] differentWindows =
        {
            previous with { ActiveWindowSequence = 0, IssuedWindowSequence = 35, ClosedWindowWatermark = 35 },
            previous with { ActiveWindowSequence = 35, ClosedWindowWatermark = 35 },
            previous with { ActiveWindowSequence = 36, IssuedWindowSequence = 36, ClosedWindowWatermark = 35 },
            previous with { ActiveWindowSequence = 34, IssuedWindowSequence = 34, ClosedWindowWatermark = 33 },
            previous with { ActiveWindowSequence = 35, IssuedWindowSequence = 34 },
            previous with { ActiveWindowSequence = 35, IssuedWindowSequence = 36 }
        };
        foreach (EditorDataListCascadeProof current in differentWindows)
            AssertEx.False(EditorDataListCascadePlanner.CanReuse(previous, current, out _));
    }

    public static void MissingInvalidOrInternallyMismatchedProofsNeverReuse()
    {
        EditorDataListCascadeProof valid = Proof();
        EditorPreviewSceneAddress scene = valid.Scene!;
        EditorInputSceneCaptureProof input = valid.InputProof!;
        EditorDataListCascadeProof?[] invalid =
        {
            null,
            valid with { RequestId = -1 },
            valid with { MainThreadId = 0 },
            valid with { MainThreadId = -1 },
            valid with { ActiveWindowSequence = 0, IssuedWindowSequence = 0, ClosedWindowWatermark = 0 },
            valid with { ClosedWindowWatermark = -1 },
            valid with { Scene = null },
            valid with { Scene = scene with { ProjectKey = null! } },
            valid with { Scene = scene with { ProjectKey = "" } },
            valid with { Scene = scene with { ProjectKey = new string('A', 23) } },
            valid with { Scene = scene with { ProjectKey = new string('G', 24) } },
            valid with { Scene = scene with { NodeGuid = null! } },
            valid with { Scene = scene with { NodeGuid = "node" } },
            valid with { Scene = scene with { NodeGuid = "11111111222233334444555555555555" } },
            valid with { Scene = scene with { SceneIndex = -1 } },
            valid with { Scene = scene with { Fingerprint = null! } },
            valid with { Scene = scene with { Fingerprint = new string('B', 64) } },
            valid with { Scene = scene with { Fingerprint = new string('Z', 24) } },
            valid with { InputProof = null },
            valid with { InputProof = input with { ProjectKey = null! } },
            valid with { InputProof = input with { ProjectKey = new string('C', 24) } },
            valid with { InputProof = input with { NodeGuid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee" } },
            valid with { InputProof = input with { SceneIndex = 0 } },
            valid with { InputProof = input with { ContextId = null! } },
            valid with { InputProof = input with { ContextId = "" } },
            valid with { InputProof = input with { ContextId = " \t" } },
            valid with { InputProof = input with { DialogueText = null! } },
            valid with { InputProof = input with { AdditionalPrompt = null! } }
        };
        foreach (EditorDataListCascadeProof? incomplete in invalid)
        {
            AssertEx.False(EditorDataListCascadePlanner.CanReuse(valid, incomplete, out string error));
            AssertEx.True(error.Length != 0);
            AssertEx.False(EditorDataListCascadePlanner.CanReuse(incomplete, valid, out _));
            AssertEx.False(EditorDataListCascadePlanner.CanReuse(incomplete, incomplete, out _),
                "two equal incomplete proofs cannot establish authority");
        }
    }

    public static void DuplicateDataListPreservesTheRealFirstLogAndCompletesOnce()
    {
        EditorDataListCascadeProof proof = Proof();
        var correlation = new ManagedSelectionLogCorrelation();
        ManagedSelectionLogMarker original = Begin(correlation, proof, 20, 8);
        AssertEx.True(correlation.TryObserveFirst(21, Script, proof.MainThreadId,
            proof.IssuedWindowSequence, proof.ClosedWindowWatermark, out _));
        ManagedSelectionLogCandidate first = correlation.Pending!;

        EditorDataListCascadeProof duplicate = proof with { };
        if (!EditorDataListCascadePlanner.CanReuse(proof, duplicate, out _))
            Begin(correlation, duplicate, 21, 9);

        ManagedSelectionLogCandidate preserved = AssertEx.NotNull(correlation.Pending);
        AssertEx.Equal(first, preserved);
        AssertEx.Equal(original, preserved.Marker);
        AssertEx.Equal(8L, preserved.Marker.SelectionGeneration);
        AssertEx.Equal(21L, preserved.LogSequence);
        AssertEx.False(correlation.TryObserveFirst(22, Script, proof.MainThreadId,
            proof.IssuedWindowSequence, proof.ClosedWindowWatermark, out _));
        AssertEx.True(correlation.TryComplete(first, proof.MainThreadId,
            proof.IssuedWindowSequence, proof.Scene, out _));
        AssertEx.False(correlation.TryComplete(first, proof.MainThreadId,
            proof.IssuedWindowSequence, proof.Scene, out _));
    }

    public static void DuplicateDataListCannotResetARejectedFirstLog()
    {
        EditorDataListCascadeProof proof = Proof();
        foreach ((CompiledScriptIdentity? script, int thread, long sequence) in new[]
        {
            ((CompiledScriptIdentity?)null, proof.MainThreadId, 21L),
            ((CompiledScriptIdentity?)Script, proof.MainThreadId + 1, 21L),
            ((CompiledScriptIdentity?)Script, proof.MainThreadId, 22L)
        })
        {
            var correlation = new ManagedSelectionLogCorrelation();
            ManagedSelectionLogMarker marker = Begin(correlation, proof, 20, 8);
            AssertEx.False(correlation.TryObserveFirst(sequence, script, thread,
                proof.IssuedWindowSequence, proof.ClosedWindowWatermark, out _));
            if (!EditorDataListCascadePlanner.CanReuse(proof, proof with { }, out _))
                Begin(correlation, proof, sequence, 9);

            AssertEx.False(correlation.TryObserveFirst(sequence + 1, Script, proof.MainThreadId,
                proof.IssuedWindowSequence, proof.ClosedWindowWatermark, out string error));
            AssertEx.Equal("first-log-already-observed", error);
            AssertEx.True(correlation.Pending == null && correlation.DeferredPending == null);
            var fabricated = new ManagedSelectionLogCandidate(marker, 21, Script);
            AssertEx.False(correlation.TryComplete(fabricated, proof.MainThreadId,
                proof.IssuedWindowSequence, proof.Scene, out _));
        }
    }

    public static void PromptDriftBeginsANewGenerationWithoutBorrowingTheOldFirstLog()
    {
        EditorDataListCascadeProof previous = Proof();
        EditorDataListCascadeProof current = previous with
        {
            InputProof = previous.InputProof! with { AdditionalPrompt = "different authored directives" }
        };
        var correlation = new ManagedSelectionLogCorrelation();
        Begin(correlation, previous, 20, 8);
        AssertEx.True(correlation.TryObserveFirst(21, Script, previous.MainThreadId,
            previous.IssuedWindowSequence, previous.ClosedWindowWatermark, out _));
        ManagedSelectionLogCandidate old = correlation.Pending!;

        AssertEx.False(EditorDataListCascadePlanner.CanReuse(previous, current, out _));
        ManagedSelectionLogMarker next = Begin(correlation, current, 21, 9);
        AssertEx.True(correlation.Pending == null && correlation.DeferredPending == null);
        AssertEx.True(next.Generation > old.Marker.Generation);
        AssertEx.False(correlation.TryComplete(old, current.MainThreadId,
            current.IssuedWindowSequence, current.Scene, out _));
        // Even though sanitized text/hash and opaque request are unchanged,
        // this generation needs its own real first event.
        AssertEx.True(correlation.TryObserveFirst(22, Script, current.MainThreadId,
            current.IssuedWindowSequence, current.ClosedWindowWatermark, out _));
        ManagedSelectionLogCandidate actual = correlation.Pending!;
        AssertEx.Equal(9L, actual.Marker.SelectionGeneration);
        AssertEx.True(correlation.TryComplete(actual, current.MainThreadId,
            current.IssuedWindowSequence, current.Scene, out _));
    }

    public static void ReturningToAnEarlierSceneCannotReviveItsHistoricalEpisode()
    {
        EditorDataListCascadeProof firstScene = Proof();
        EditorDataListCascadeProof otherScene = WithScene(firstScene, firstScene.Scene! with { SceneIndex = 10 });
        var correlation = new ManagedSelectionLogCorrelation();
        Begin(correlation, firstScene, 20, 8);
        AssertEx.True(correlation.TryObserveFirst(21, Script, firstScene.MainThreadId,
            firstScene.IssuedWindowSequence, firstScene.ClosedWindowWatermark, out _));
        ManagedSelectionLogCandidate historical = correlation.Pending!;

        AssertEx.False(EditorDataListCascadePlanner.CanReuse(firstScene, otherScene, out _));
        Begin(correlation, otherScene, 21, 9);
        AssertEx.False(EditorDataListCascadePlanner.CanReuse(otherScene, firstScene, out _));
        Begin(correlation, firstScene, 21, 10);
        AssertEx.True(correlation.Pending == null && correlation.DeferredPending == null);
        AssertEx.False(correlation.TryComplete(historical, firstScene.MainThreadId,
            firstScene.IssuedWindowSequence, firstScene.Scene, out _));
    }

    private static EditorDataListCascadeProof Proof()
    {
        var scene = new EditorPreviewSceneAddress(new string('A', 24),
            "11111111-2222-3333-4444-555555555555", 9, new string('B', 24));
        return new EditorDataListCascadeProof(0, 11, 35, 35, 34, scene,
            new EditorInputSceneCaptureProof(scene.ProjectKey, scene.NodeGuid, scene.SceneIndex,
                "10:20:30:9", "selected dialogue", "authored prompt"));
    }

    private static EditorDataListCascadeProof WithScene(
        EditorDataListCascadeProof proof, EditorPreviewSceneAddress scene) => proof with
    {
        Scene = scene,
        InputProof = proof.InputProof! with
        {
            ProjectKey = scene.ProjectKey,
            NodeGuid = scene.NodeGuid,
            SceneIndex = scene.SceneIndex
        }
    };

    private static ManagedSelectionLogMarker Begin(ManagedSelectionLogCorrelation correlation,
        EditorDataListCascadeProof proof, long startLogSequence, long generation) => correlation.Begin(
            proof.RequestId, startLogSequence, proof.MainThreadId, proof.IssuedWindowSequence,
            proof.ClosedWindowWatermark, proof.Scene, generation);
}
