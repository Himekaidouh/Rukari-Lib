using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Playback;

namespace AzureArchive.VideoTools.Tests;

internal static class EditorPreviewWindowLeaseTests
{
    public static void ConfirmedGenerationReplaysOncePerActualWindow()
    {
        var gate = new EditorPreviewLeaseGate();
        EditorPreviewLeaseCandidate candidate = Candidate();
        EditorPreviewLiveObservation first = Live(candidate, 100);

        var authorized = gate.TryAuthorizeWindow(candidate, first);
        AssertEx.True(authorized.Success, authorized.Error);
        AssertEx.False(gate.TryAuthorizeWindow(candidate, first).Success);

        // Green replay rebuilds the window without issuing another DataList
        // or publishing a new selection observation.
        EditorPreviewLiveObservation replay = first with { WindowSequence = 101 };
        var replayed = gate.TryAuthorizeWindow(candidate, replay);
        AssertEx.True(replayed.Success, replayed.Error);
        AssertEx.Equal(AssertEx.NotNull(authorized.Value).StableSceneIdentity,
            AssertEx.NotNull(replayed.Value).StableSceneIdentity);
        AssertEx.Equal(77L, candidate.Selection.ObservationSequence);
        AssertEx.False(gate.TryAuthorizeWindow(candidate, replay).Success);
        AssertEx.True(gate.TryAuthorizeWindow(candidate, replay with { WindowSequence = 105 }).Success);
    }

    public static void ReplayRequiresActualWindowAndConfirmedObservation()
    {
        EditorPreviewLeaseCandidate candidate = Candidate();
        foreach (long sequence in new[] { long.MinValue, -1L, 0L, long.MaxValue })
        {
            var gate = new EditorPreviewLeaseGate();
            AssertEx.False(gate.TryAuthorizeWindow(candidate, Live(candidate, sequence)).Success);
            AssertEx.True(gate.TryAuthorizeWindow(candidate, Live(candidate, 100)).Success);
        }

        var defaultGate = new EditorPreviewLeaseGate();
        var legacyDefault = new EditorPreviewLiveObservation(
            PlayerRuntimeContextSnapshot.FromPreviewMode(true), candidate.Selection, 1);
        AssertEx.False(defaultGate.TryAuthorizeWindow(candidate, legacyDefault).Success);
        AssertEx.True(defaultGate.TryAuthorizeWindow(candidate, Live(candidate, 100)).Success);

        foreach (long observation in new[] { -1L, 0L })
        {
            var gate = new EditorPreviewLeaseGate();
            EditorPreviewLeaseCandidate unconfirmed = candidate with
            {
                Selection = candidate.Selection with { ObservationSequence = observation }
            };
            AssertEx.False(gate.TryAuthorizeWindow(unconfirmed, Live(candidate, 100)).Success);
            AssertEx.False(gate.TryAuthorizeWindow(candidate, Live(candidate, 100)).Success,
                "failed confirmation must consume its real window");
            AssertEx.True(gate.TryAuthorizeWindow(candidate, Live(candidate, 101)).Success);
        }
    }

    public static void ReplayRejectsOldGenerationsAndRegressedWindows()
    {
        var gate = new EditorPreviewLeaseGate();
        EditorPreviewLeaseCandidate first = Candidate();
        AssertEx.True(gate.TryAuthorizeWindow(first, Live(first, 100)).Success);
        AssertEx.False(gate.TryAuthorizeWindow(first, Live(first, 99)).Success);

        // Reused request, script and scene cannot make the old generation current.
        EditorPreviewLeaseCandidate newer = first with
        {
            Selection = first.Selection with { SelectionGeneration = 21, ObservationSequence = 78 },
            MinimumWindowSequenceExclusive = 100
        };
        AssertEx.False(gate.TryAuthorizeWindow(newer, Live(newer, 100)).Success);
        AssertEx.True(gate.TryAuthorizeWindow(newer, Live(newer, 101)).Success);
        AssertEx.False(gate.TryAuthorizeWindow(first, Live(first, 102)).Success);
        AssertEx.True(gate.TryAuthorizeWindow(newer, Live(newer, 102)).Success,
            "rejecting an old generation must not consume the newer generation's window");

        EditorPreviewLeaseCandidate latest = newer with
        {
            Selection = newer.Selection with { SelectionGeneration = 22, ObservationSequence = 79 }
        };
        AssertEx.False(gate.TryAuthorizeWindow(latest,
            Live(latest, 103) with { ExactCompiledScriptMessageCount = 0 }).Success);
        AssertEx.False(gate.TryAuthorizeWindow(newer, Live(newer, 104)).Success,
            "a failed actual window in a newer generation must still supersede the old generation");
        AssertEx.True(gate.TryAuthorizeWindow(latest, Live(latest, 104)).Success);
    }

    public static void ReplayRequiresUnchangedGenerationRequestObservationHashAndScene()
    {
        EditorPreviewLeaseCandidate candidate = Candidate();
        Func<EditorPreviewLiveObservation, EditorPreviewLiveObservation>[] invalid =
        {
            live => live with { Selection = null! },
            live => live with { Selection = live.Selection with { SelectionGeneration = 21 } },
            live => live with { Selection = live.Selection with { SelectionRequestId = 1 } },
            live => live with { Selection = live.Selection with { ObservationSequence = 0 } },
            live => live with { Selection = live.Selection with { ObservationSequence = 78 } },
            live => live with { Selection = live.Selection with
                { CompiledScript = CommandIdentity.CompiledScript("another compiled script") } },
            live => live with { Selection = live.Selection with
                { CompiledScript = live.Selection.CompiledScript with
                    { Utf16Length = live.Selection.CompiledScript.Utf16Length + 1 } } },
            live => live with { Selection = live.Selection with
                { CompiledScript = live.Selection.CompiledScript with
                    { LineCount = live.Selection.CompiledScript.LineCount + 1 } } },
            live => live with { Selection = live.Selection with
                { Scene = live.Selection.Scene with { ProjectKey = new string('C', 24) } } },
            live => live with { Selection = live.Selection with
                { Scene = live.Selection.Scene with { NodeGuid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee" } } },
            live => live with { Selection = live.Selection with
                { Scene = live.Selection.Scene with { SceneIndex = 6 } } },
            live => live with { Selection = live.Selection with
                { Scene = live.Selection.Scene with { Fingerprint = new string('D', 24) } } }
        };
        foreach (var mutation in invalid)
            AssertFailedWindowAllowsLaterReplay(candidate, mutation);
    }

    public static void ReplayStillRequiresPreviewRuntimeExactCountTimingAndCanonicalCommands()
    {
        EditorPreviewLeaseCandidate candidate = Candidate();
        PlayerRuntimeContextSnapshot?[] invalidContexts =
        {
            null,
            PlayerRuntimeContextSnapshot.Unavailable,
            PlayerRuntimeContextSnapshot.FromPreviewMode(false),
            new(false, true, PlayerRuntimeMode.EditorPreview),
            new(true, false, PlayerRuntimeMode.EditorPreview),
            new(true, true, PlayerRuntimeMode.Playback),
            new(true, true, (PlayerRuntimeMode)99)
        };
        foreach (PlayerRuntimeContextSnapshot? context in invalidContexts)
            AssertFailedWindowAllowsLaterReplay(candidate, live => live with { RuntimeContext = context! });
        foreach (int count in new[] { -1, 0, 2 })
            AssertFailedWindowAllowsLaterReplay(candidate, live => live with { ExactCompiledScriptMessageCount = count });

        var timingGate = new EditorPreviewLeaseGate();
        EditorPreviewLeaseCandidate afterCutoff = candidate with { MinimumWindowSequenceExclusive = 100 };
        AssertEx.False(timingGate.TryAuthorizeWindow(afterCutoff, Live(afterCutoff, 100)).Success);
        AssertEx.False(timingGate.TryAuthorizeWindow(candidate, Live(candidate, 100)).Success);
        AssertEx.True(timingGate.TryAuthorizeWindow(afterCutoff, Live(afterCutoff, 101)).Success);

        var commandGate = new EditorPreviewLeaseGate();
        EditorPreviewLeaseCandidate invalidCommand = candidate with
        {
            CanonicalDirectives = Array.AsReadOnly(new[] { "#char;1;invented-operation" })
        };
        AssertEx.False(commandGate.TryAuthorizeWindow(invalidCommand, Live(candidate, 100)).Success);
        AssertEx.False(commandGate.TryAuthorizeWindow(candidate, Live(candidate, 100)).Success);
        AssertEx.True(commandGate.TryAuthorizeWindow(candidate, Live(candidate, 101)).Success);

        var emptyGate = new EditorPreviewLeaseGate();
        EditorPreviewLeaseCandidate empty = candidate with { CanonicalDirectives = Array.Empty<string>() };
        AssertEx.False(emptyGate.TryAuthorizeWindow(empty, Live(empty, 100)).Success);
        EditorPreviewLeaseCandidate tombstone = empty with { IsTombstone = true };
        var authorizedTombstone = emptyGate.TryAuthorizeWindow(tombstone, Live(tombstone, 101));
        AssertEx.True(authorizedTombstone.Success, authorizedTombstone.Error);
        AssertEx.Equal(0, AssertEx.NotNull(authorizedTombstone.Value).Commands.Count);
    }

    public static void LegacyEntryRemainsOneShotAndBothEntriesRejectSupersededGenerations()
    {
        EditorPreviewLeaseCandidate candidate = Candidate();
        var legacy = new EditorPreviewLeaseGate();
        AssertEx.True(legacy.TryAuthorize(candidate, Live(candidate, 100)).Success);
        AssertEx.False(legacy.TryAuthorize(candidate, Live(candidate, 101)).Success);

        EditorPreviewLeaseCandidate newer = candidate with
        {
            Selection = candidate.Selection with { SelectionGeneration = 21, ObservationSequence = 78 }
        };
        var windowThenLegacy = new EditorPreviewLeaseGate();
        AssertEx.True(windowThenLegacy.TryAuthorizeWindow(newer, Live(newer, 100)).Success);
        AssertEx.False(windowThenLegacy.TryAuthorize(candidate, Live(candidate, 101)).Success);
        AssertEx.False(windowThenLegacy.TryAuthorize(newer, Live(newer, 101)).Success);
        AssertEx.True(windowThenLegacy.TryAuthorizeWindow(newer, Live(newer, 101)).Success);

        var legacyThenWindow = new EditorPreviewLeaseGate();
        AssertEx.True(legacyThenWindow.TryAuthorize(newer, Live(newer, 100)).Success);
        AssertEx.False(legacyThenWindow.TryAuthorizeWindow(candidate, Live(candidate, 101)).Success);
        AssertEx.False(legacyThenWindow.TryAuthorizeWindow(newer, Live(newer, 100)).Success,
            "switching APIs must not authorize the same actual window twice");
        AssertEx.True(legacyThenWindow.TryAuthorizeWindow(newer, Live(newer, 101)).Success);
    }

    private static void AssertFailedWindowAllowsLaterReplay(
        EditorPreviewLeaseCandidate candidate,
        Func<EditorPreviewLiveObservation, EditorPreviewLiveObservation> mutation)
    {
        var gate = new EditorPreviewLeaseGate();
        EditorPreviewLiveObservation live = Live(candidate, 100);
        AssertEx.False(gate.TryAuthorizeWindow(candidate, mutation(live)).Success);
        AssertEx.False(gate.TryAuthorizeWindow(candidate, live).Success,
            "correcting a failed proof cannot reuse the consumed actual window");
        AssertEx.True(gate.TryAuthorizeWindow(candidate, live with { WindowSequence = 101 }).Success,
            "a failed window must not consume the entire confirmed generation");
    }

    private static EditorPreviewLeaseCandidate Candidate() => new(
        new EditorPreviewSelectionBinding(
            20, 0, 77, CommandIdentity.CompiledScript("confirmed green preview script"),
            new EditorPreviewSceneAddress(new string('A', 24),
                "11111111-2222-3333-4444-555555555555", 5, new string('B', 24))),
        Array.AsReadOnly(new[] { "#char;1;set;x=100;duration=0;easing=linear" }),
        IsTombstone: false,
        MinimumWindowSequenceExclusive: 99);

    private static EditorPreviewLiveObservation Live(EditorPreviewLeaseCandidate candidate, long sequence) => new(
        PlayerRuntimeContextSnapshot.FromPreviewMode(true), candidate.Selection with { }, 1, sequence);
}
