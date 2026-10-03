using AzureArchive.VideoTools.Core.Characters;
using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Tests;

internal static class EditorPreviewLeaseTests
{
    private const string NodeGuid = "12345678-1234-1234-1234-1234567890ab";
    private static readonly string SceneFingerprint = new('A', 24);

    public static void AuthorizesExactEditorPreviewOnceAndMarksPendingDeferred()
    {
        EditorPreviewSelectionBinding selection = Selection(
            generation: 7,
            request: 31,
            observation: 92,
            script: "compiled editor scene");
        string[] directives =
        {
            "#char;1;set;x=100;rotation=-12;duration=0;easing=linear",
            "#charp;2;set;y=-500",
            "#camera;move;dx=25;dzoom=0.1;duration=300;easing=easeInOut"
        };
        var candidate = new EditorPreviewLeaseCandidate(
            selection,
            Array.AsReadOnly(directives),
            IsTombstone: false);
        var live = new EditorPreviewLiveObservation(
            PlayerRuntimeContextSnapshot.FromPreviewMode(true),
            selection with { },
            ExactCompiledScriptMessageCount: 1);
        var gate = new EditorPreviewLeaseGate();

        var authorized = gate.TryAuthorize(candidate, live);

        AssertEx.True(authorized.Success, authorized.Error);
        EditorPreviewLeaseAuthorization value = AssertEx.NotNull(authorized.Value);
        AssertEx.Equal(3, value.Commands.Count);
        AssertEx.True(value.Commands[0].DispatchImmediately);
        AssertEx.False(value.Commands[1].DispatchImmediately);
        AssertEx.Equal(
            EditorPreviewDispatchMode.DeferredSlotPending,
            value.Commands[1].DispatchMode);
        AssertEx.True(value.Commands[2].DispatchImmediately);
        AssertEx.Equal(value.StableSceneIdentity, value.Footprint.StableSceneIdentity);
        AssertEx.True(value.StableSceneIdentity.StartsWith("editor-preview:", StringComparison.Ordinal));

        var replay = gate.TryAuthorize(candidate, live);
        AssertEx.False(replay.Success);
        AssertEx.True(
            replay.Error.Contains("already consumed", StringComparison.Ordinal),
            replay.Error);
    }

    public static void ZeroOpaqueRequestAuthorizesWithoutChangingTheSceneOrOtherGuards()
    {
        EditorPreviewSelectionBinding selection = Selection(7, 0, 92, "zero opaque request script");
        EditorPreviewLeaseCandidate candidate = Candidate(
            selection,
            "#char;1;set;x=100;duration=0;easing=linear");
        var live = new EditorPreviewLiveObservation(
            PlayerRuntimeContextSnapshot.FromPreviewMode(true),
            selection with { },
            ExactCompiledScriptMessageCount: 1,
            WindowSequence: 100);

        Result<EditorPreviewLeaseAuthorization> authorized =
            new EditorPreviewLeaseGate().TryAuthorize(candidate, live);
        AssertEx.True(authorized.Success, authorized.Error);
        AssertEx.Equal(0, candidate.Selection.SelectionRequestId);
        AssertEx.Equal(0, live.Selection.SelectionRequestId);
        AssertEx.Equal(5, candidate.Selection.Scene.SceneIndex);
        AssertEx.Equal(1, AssertEx.NotNull(authorized.Value).Commands.Count);

        AssertRejected(candidate, observation => observation with
        {
            RuntimeContext = PlayerRuntimeContextSnapshot.FromPreviewMode(false)
        });
        AssertRejected(candidate, observation => observation with
        {
            Selection = observation.Selection with { SelectionGeneration = 0 }
        });
        AssertRejected(candidate, observation => observation with
        {
            Selection = observation.Selection with { SelectionRequestId = 1 }
        });
        AssertRejected(candidate, observation => observation with
        {
            Selection = observation.Selection with { ObservationSequence = 0 }
        });
        AssertRejected(candidate, observation => observation with
        {
            Selection = observation.Selection with
            {
                CompiledScript = CommandIdentity.CompiledScript("different script")
            }
        });
        AssertRejected(candidate, observation => observation with
        {
            Selection = observation.Selection with
            {
                Scene = observation.Selection.Scene with { SceneIndex = 0 }
            }
        });
        AssertRejected(candidate, observation => observation with
        {
            ExactCompiledScriptMessageCount = 0
        });
        AssertEx.False(new EditorPreviewLeaseGate().TryAuthorize(
            candidate with { MinimumWindowSequenceExclusive = 100 }, live).Success);
    }

    public static void NegativeOpaqueRequestsFailForCapturedAndLiveSelections()
    {
        EditorPreviewSelectionBinding zero = Selection(7, 0, 92, "negative opaque request script");
        foreach (int invalidRequest in new[] { -1, int.MinValue })
        {
            EditorPreviewSelectionBinding invalid = zero with { SelectionRequestId = invalidRequest };
            AssertEx.False(Authorize(invalid,
                new[] { "#char;1;set;x=100;duration=0;easing=linear" }, tombstone: false).Success);
            AssertRejected(Candidate(zero, "#char;1;set;x=100;duration=0;easing=linear"),
                observation => observation with { Selection = invalid });
        }
    }

    public static void RejectsEveryLiveIdentityDriftAndNonEditorWindows()
    {
        EditorPreviewSelectionBinding selection = Selection(4, 9, 18, "exact script");
        var candidate = Candidate(
            selection,
            "#char;3;move;dx=20;duration=0;easing=linear");

        AssertRejected(candidate, observation => observation with
        {
            RuntimeContext = PlayerRuntimeContextSnapshot.FromPreviewMode(false)
        });
        AssertRejected(candidate, observation => observation with
        {
            Selection = observation.Selection with { SelectionGeneration = 5 }
        });
        AssertRejected(candidate, observation => observation with
        {
            Selection = observation.Selection with { SelectionRequestId = 10 }
        });
        AssertRejected(candidate, observation => observation with
        {
            Selection = observation.Selection with { ObservationSequence = 19 }
        });
        AssertRejected(candidate, observation => observation with
        {
            Selection = observation.Selection with
            {
                CompiledScript = CommandIdentity.CompiledScript("different script")
            }
        });
        AssertRejected(candidate, observation => observation with
        {
            Selection = observation.Selection with
            {
                Scene = observation.Selection.Scene with { ProjectKey = new string('E', 24) }
            }
        });
        AssertRejected(candidate, observation => observation with
        {
            Selection = observation.Selection with
            {
                Scene = observation.Selection.Scene with { ProjectKey = "_unknown_project" }
            }
        });
        AssertRejected(candidate, observation => observation with
        {
            Selection = observation.Selection with
            {
                Scene = observation.Selection.Scene with
                {
                    Fingerprint = new string('B', 24)
                }
            }
        });
        AssertRejected(candidate, observation => observation with
        {
            Selection = observation.Selection with
            {
                Scene = observation.Selection.Scene with
                {
                    Fingerprint = new string('C', 64)
                }
            }
        });
        AssertRejected(candidate, observation => observation with
        {
            ExactCompiledScriptMessageCount = 0
        });
        AssertRejected(candidate, observation => observation with
        {
            ExactCompiledScriptMessageCount = 2
        });

        EditorPreviewLeaseCandidate staleWindowCandidate = candidate with
        {
            MinimumWindowSequenceExclusive = 25
        };
        var staleWindow = new EditorPreviewLeaseGate().TryAuthorize(
            staleWindowCandidate,
            new EditorPreviewLiveObservation(
                PlayerRuntimeContextSnapshot.FromPreviewMode(true),
                selection with { },
                ExactCompiledScriptMessageCount: 1,
                WindowSequence: 25));
        AssertEx.False(staleWindow.Success);
        AssertEx.True(
            staleWindow.Error.Contains("predates", StringComparison.Ordinal),
            staleWindow.Error);
    }

    public static void EnforcesCanonicalSixResourceLimitAndExplicitTombstones()
    {
        EditorPreviewSelectionBinding selection = Selection(1, 2, 3, "six resources");
        string[] six =
        {
            "#char;1;set;x=1;duration=0;easing=linear",
            "#charp;2;set;y=2",
            "#char;3;move;dx=3;duration=0;easing=linear",
            "#charp;4;move;dy=4",
            "#char;5;reset;duration=0;easing=linear",
            "#camera;set;zoom=1.2;duration=0;easing=linear"
        };

        var valid = Authorize(selection, six, tombstone: false);
        AssertEx.True(valid.Success, valid.Error);
        AssertEx.Equal(6, AssertEx.NotNull(valid.Value).Commands.Count);

        var duplicateSlot = Authorize(
            selection,
            new[]
            {
                "#char;2;set;x=1;duration=0;easing=linear",
                "#charp;2;set;y=2"
            },
            tombstone: false);
        AssertEx.False(duplicateSlot.Success);

        var nonCanonical = Authorize(
            selection,
            new[] { "#char;1;set;x=1;duration=0;easing=LINEAR" },
            tombstone: false);
        AssertEx.False(nonCanonical.Success);

        var seven = Authorize(
            selection,
            six.Concat(new[] { "#char;1;move;dx=1;duration=0;easing=linear" }).ToArray(),
            tombstone: false);
        AssertEx.False(seven.Success);

        var tombstone = Authorize(selection, Array.Empty<string>(), tombstone: true);
        AssertEx.True(tombstone.Success, tombstone.Error);
        AssertEx.True(AssertEx.NotNull(tombstone.Value).IsTombstone);
        AssertEx.Equal(0, tombstone.Value!.Footprint.Entries.Count);

        AssertEx.False(Authorize(
            selection,
            Array.Empty<string>(),
            tombstone: false).Success);
        AssertEx.False(Authorize(
            selection,
            new[] { "#char;1;set;x=1;duration=0;easing=linear" },
            tombstone: true).Success);
    }

    public static void PlansFieldAndWholeCommandCleanupFromResourceUnion()
    {
        EditorPreviewSceneAddress scene = Scene();
        EditorPreviewLeaseAuthorization previous = AssertEx.NotNull(Authorize(
            Selection(1, 1, 1, "previous", scene),
            new[]
            {
                "#char;1;set;x=10;y=20;rotation=30;flipX=true;duration=0;easing=linear",
                "#charp;2;set;x=40",
                "#camera;move;dx=5;dzoom=0.2;duration=0;easing=linear"
            },
            tombstone: false).Value);
        EditorPreviewLeaseAuthorization current = AssertEx.NotNull(Authorize(
            Selection(2, 2, 2, "current", scene),
            new[]
            {
                "#char;1;set;x=15;duration=0;easing=linear",
                "#charp;2;set;y=50"
            },
            tombstone: false).Value);
        var planner = new EditorPreviewResourceCleanupPlanner();

        var result = planner.Plan(previous.Footprint, current.Footprint);

        AssertEx.True(result.Success, result.Error);
        EditorPreviewResourceCleanupPlan plan = AssertEx.NotNull(result.Value);
        AssertEx.Equal(3, plan.Resources.Count);

        EditorPreviewResourceTransition slot1 = plan.Resources.Single(item =>
            item.Resource == new EditorPreviewResourceKey(
                EditorPreviewResourceKind.CharacterSlot,
                1));
        AssertEx.Equal(
            EditorPreviewResourceFields.CharacterPositionX
            | EditorPreviewResourceFields.CharacterPositionY
            | EditorPreviewResourceFields.CharacterScreenRotation
            | EditorPreviewResourceFields.CharacterHorizontalFlip,
            slot1.UnionFields);
        AssertEx.Equal(
            EditorPreviewResourceFields.CharacterPositionY
            | EditorPreviewResourceFields.CharacterScreenRotation
            | EditorPreviewResourceFields.CharacterHorizontalFlip,
            slot1.RemovedFields);
        AssertEx.True(slot1.RestorePreviousImmediate);
        AssertEx.True(slot1.ApplyCurrentImmediately);
        AssertEx.False(slot1.CommandRemoved);

        EditorPreviewResourceTransition slot2 = plan.Resources.Single(item =>
            item.Resource == new EditorPreviewResourceKey(
                EditorPreviewResourceKind.CharacterSlot,
                2));
        AssertEx.True(slot2.ClearPreviousSlotPending);
        AssertEx.True(slot2.StoreCurrentSlotPending);
        AssertEx.False(slot2.RestorePreviousImmediate);

        EditorPreviewResourceTransition camera = plan.Resources.Single(item =>
            item.Resource.Kind == EditorPreviewResourceKind.SceneCamera);
        AssertEx.True(camera.RestorePreviousImmediate);
        AssertEx.True(camera.CommandRemoved);
        AssertEx.Equal(EditorPreviewResourceFields.None, camera.CurrentFields);

        EditorPreviewLeaseAuthorization tombstone = AssertEx.NotNull(Authorize(
            Selection(3, 3, 3, "tombstone", scene),
            Array.Empty<string>(),
            tombstone: true).Value);
        var deleted = planner.Plan(current.Footprint, tombstone.Footprint);
        AssertEx.True(deleted.Success, deleted.Error);
        AssertEx.True(AssertEx.NotNull(deleted.Value).Resources.All(item => item.CommandRemoved));

        EditorPreviewLeaseAuthorization otherScene = AssertEx.NotNull(Authorize(
            Selection(4, 4, 4, "other", scene with { SceneIndex = 6 }),
            Array.Empty<string>(),
            tombstone: true).Value);
        AssertEx.False(planner.Plan(current.Footprint, otherScene.Footprint).Success);

        EditorPreviewLeaseAuthorization sameSceneDifferentLease = AssertEx.NotNull(Authorize(
            Selection(5, 5, 5, "new identity", scene),
            new[] { "#char;5;set;x=1;duration=0;easing=linear" },
            tombstone: false).Value);
        AssertEx.Equal(previous.StableSceneIdentity, sameSceneDifferentLease.StableSceneIdentity);
    }

    public static void ConsecutiveDataListGenerationsEachAuthorizeExactlyOnce()
    {
        EditorPreviewSceneAddress scene = Scene();
        var gate = new EditorPreviewLeaseGate();
        var firstBinding = new EditorPreviewSelectionBinding(
            10,
            31,
            92,
            CommandIdentity.CompiledScript("generation one script"),
            scene);
        var firstCandidate = new EditorPreviewLeaseCandidate(
            firstBinding,
            Array.AsReadOnly(new[] { "#char;1;set;x=100;duration=0;easing=linear" }),
            IsTombstone: false);
        var firstLive = new EditorPreviewLiveObservation(
            PlayerRuntimeContextSnapshot.FromPreviewMode(true),
            firstBinding with { },
            ExactCompiledScriptMessageCount: 1,
            WindowSequence: 100);

        AssertEx.True(gate.TryAuthorize(firstCandidate, firstLive).Success, "generation one must authorize once");
        AssertEx.False(
            gate.TryAuthorize(firstCandidate, firstLive).Success,
            "the same generation must never authorize twice");

        // Second green preview of the same scene opens a NEW generation with
        // its own identity and authorizes exactly once as well.
        var secondBinding = new EditorPreviewSelectionBinding(
            11,
            32,
            93,
            CommandIdentity.CompiledScript("generation two script"),
            scene);
        var secondCandidate = new EditorPreviewLeaseCandidate(
            secondBinding,
            Array.AsReadOnly(new[] { "#char;1;set;x=140;duration=0;easing=linear" }),
            IsTombstone: false);
        var secondLive = new EditorPreviewLiveObservation(
            PlayerRuntimeContextSnapshot.FromPreviewMode(true),
            secondBinding with { },
            ExactCompiledScriptMessageCount: 1,
            WindowSequence: 120);

        Result<EditorPreviewLeaseAuthorization> second = gate.TryAuthorize(secondCandidate, secondLive);
        AssertEx.True(second.Success, second.Error);
        AssertEx.False(gate.TryAuthorize(secondCandidate, secondLive).Success);
    }

    public static void SecondGreenPreviewWithoutSelectionEventStillAuthorizes()
    {
        // No OnChildSelect fired for this preview: the captured side carries
        // ObservationSequence=0 ("selection event not required") while the
        // live side still shows the stale identity of an earlier selection.
        var expected = new EditorPreviewSelectionBinding(
            12,
            33,
            0,
            CommandIdentity.CompiledScript("unconfirmed preview script"),
            Scene());
        var candidate = new EditorPreviewLeaseCandidate(
            expected,
            Array.AsReadOnly(new[] { "#charp;2;set;y=-500" }),
            IsTombstone: false);
        var staleIdentityLive = expected with { ObservationSequence = 55 };
        var gate = new EditorPreviewLeaseGate();

        Result<EditorPreviewLeaseAuthorization> authorized = gate.TryAuthorize(
            candidate,
            new EditorPreviewLiveObservation(
                PlayerRuntimeContextSnapshot.FromPreviewMode(true),
                staleIdentityLive,
                ExactCompiledScriptMessageCount: 1,
                WindowSequence: 200));
        AssertEx.True(authorized.Success, authorized.Error);

        // The relaxation skips ONLY the observation field; every other
        // mandatory field still rejects when the live side drifts.
        AssertRejected(candidate, observation => observation with
        {
            Selection = observation.Selection with { SelectionRequestId = 99 }
        });
        AssertRejected(candidate, observation => observation with
        {
            Selection = observation.Selection with
            {
                CompiledScript = CommandIdentity.CompiledScript("different script")
            }
        });
        AssertRejected(candidate, observation => observation with
        {
            Selection = observation.Selection with
            {
                Scene = observation.Selection.Scene with { SceneIndex = 6 }
            }
        });

        var zeroObservationGate = new EditorPreviewLeaseGate();
        var invalidLive = staleIdentityLive with { ObservationSequence = 0 };
        AssertEx.False(zeroObservationGate.TryAuthorize(
            candidate,
            new EditorPreviewLiveObservation(
                PlayerRuntimeContextSnapshot.FromPreviewMode(true),
                invalidLive,
                1)).Success,
            "the LIVE side must always carry a positive observation sequence");
    }

    public static void StaleSiblingWindowsWithinOneGenerationDoNotDoubleExecute()
    {
        var binding = new EditorPreviewSelectionBinding(
            20,
            40,
            77,
            CommandIdentity.CompiledScript("sibling window script"),
            Scene());
        EditorPreviewLeaseCandidate cutoffCandidate = Candidate(
            binding,
            "#char;3;move;dx=20;duration=0;easing=linear") with
        {
            MinimumWindowSequenceExclusive = 25
        };

        // Stale sibling at the exact cutoff fails closed. (At the runtime
        // cache layer this shape is suppressed WITHOUT consuming the
        // generation; here we assert the core gate still refuses it.)
        var staleGate = new EditorPreviewLeaseGate();
        Result<EditorPreviewLeaseAuthorization> stale = staleGate.TryAuthorize(
            cutoffCandidate,
            new EditorPreviewLiveObservation(
                PlayerRuntimeContextSnapshot.FromPreviewMode(true),
                binding with { },
                1,
                WindowSequence: 25));
        AssertEx.False(stale.Success);
        AssertEx.True(stale.Error.Contains("predates", StringComparison.Ordinal), stale.Error);

        // A fresh sibling window of the same generation authorizes exactly
        // once and every later replay of that click fails as consumed.
        var gate = new EditorPreviewLeaseGate();
        Result<EditorPreviewLeaseAuthorization> fresh = gate.TryAuthorize(
            cutoffCandidate,
            new EditorPreviewLiveObservation(
                PlayerRuntimeContextSnapshot.FromPreviewMode(true),
                binding with { },
                1,
                WindowSequence: 30));
        AssertEx.True(fresh.Success, fresh.Error);

        Result<EditorPreviewLeaseAuthorization> replay = gate.TryAuthorize(
            cutoffCandidate,
            new EditorPreviewLiveObservation(
                PlayerRuntimeContextSnapshot.FromPreviewMode(true),
                binding with { },
                1,
                WindowSequence: 31));
        AssertEx.False(replay.Success);
        AssertEx.True(replay.Error.Contains("consumed", StringComparison.Ordinal), replay.Error);
    }

    public static void TombstoneAuthorizationOverridesOlderOverlayFootprint()
    {
        EditorPreviewSceneAddress scene = Scene();
        var previousBinding = new EditorPreviewSelectionBinding(
            30,
            50,
            80,
            CommandIdentity.CompiledScript("previous overlay script"),
            scene);
        EditorPreviewLeaseAuthorization previous = AssertEx.NotNull(Authorize(
            previousBinding,
            new[]
            {
                "#char;2;set;x=10;y=-500;rotation=-10;duration=0;easing=linear",
                "#camera;set;zoom=1.1;duration=0;easing=linear"
            },
            tombstone: false).Value);

        // Deleting the whole directive compiles a tombstone that authorizes
        // immediately with zero commands and an empty footprint.
        EditorPreviewLeaseAuthorization tombstone = AssertEx.NotNull(Authorize(
            previousBinding with { SelectionGeneration = 31, ObservationSequence = 81 },
            Array.Empty<string>(),
            tombstone: true).Value);
        AssertEx.Equal(0, tombstone.Commands.Count);
        AssertEx.True(tombstone.IsTombstone);

        var planner = new EditorPreviewResourceCleanupPlanner();
        Result<EditorPreviewResourceCleanupPlan> cleanup =
            planner.Plan(previous.Footprint, tombstone.Footprint);
        AssertEx.True(cleanup.Success, cleanup.Error);
        AssertEx.True(AssertEx.NotNull(cleanup.Value).Resources.All(item => item.CommandRemoved),
            "the tombstone must override every older overlay resource");

        // An empty NON-tombstone candidate stays unauthorized (no authority).
        AssertEx.False(Authorize(
            previousBinding with { SelectionGeneration = 32, ObservationSequence = 82 },
            Array.Empty<string>(),
            tombstone: false).Success);
    }

    public static void SilentSlotInheritanceValuesDoNotAccumulateAcrossRepeats()
    {
        // Silent slot 2 inherits y=-500/rotationZ=-10 from the folded chain;
        // X and yaw are never mentioned by any command in the chain.
        var inheritedStart = new PreviewChainSlotState(
            2,
            PreviewChainAxisState.Clear(),
            PreviewChainAxisState.Relative(-500f),
            PreviewChainAxisState.Absolute(-10f),
            FlippedFromOfficial: false,
            FoldedCommandCount: 1)
        {
            FlipControlled = false
        };
        var baseline = new CharacterTransformBaseline(
            "editor-preview:scene",
            2,
            "hoshino",
            new CharacterTransformState(
                new CharacterVector3(100f, 0f, 0f),
                new CharacterVector3(0f, 90f, 0f)));
        var currentOfficial = new CharacterTransformState(
            new CharacterVector3(100f, 20f, 0f),
            new CharacterVector3(0f, 90f, 5f));
        var planner = new CharacterInheritedStartPlanner();

        CharacterInheritedStartTarget first = AssertEx.NotNull(
            planner.Plan(inheritedStart, currentOfficial, baseline).Value);
        // Repeat application feeds the previously applied state back in as the
        // current state — exactly what a repeated green preview produces.
        CharacterInheritedStartTarget second = AssertEx.NotNull(
            planner.Plan(inheritedStart, first.State, baseline).Value);
        CharacterInheritedStartTarget third = AssertEx.NotNull(
            planner.Plan(inheritedStart, second.State, baseline).Value);

        AssertEx.Equal(first.State, second.State);
        AssertEx.Equal(second.State, third.State);
        // Relative inheritance always resolves against the OFFICIAL baseline
        // (y=0), never against the drifted live y=20, so repeats cannot stack.
        AssertEx.Equal(-500f, first.State.Position.Y, "y resolves against the official baseline once");
        AssertEx.Equal(100f, first.State.Position.X, "silent axes keep the live value");
        AssertEx.Equal(90f, first.State.LocalEulerAngles.Y, "an unmentioned yaw is never inherited");
    }

    private static void AssertRejected(
        EditorPreviewLeaseCandidate candidate,
        Func<EditorPreviewLiveObservation, EditorPreviewLiveObservation> mutate)
    {
        var exact = new EditorPreviewLiveObservation(
            PlayerRuntimeContextSnapshot.FromPreviewMode(true),
            candidate.Selection with { },
            1);
        var result = new EditorPreviewLeaseGate().TryAuthorize(candidate, mutate(exact));
        AssertEx.False(result.Success);
    }

    private static AzureArchive.VideoTools.Core.Results.Result<EditorPreviewLeaseAuthorization>
        Authorize(
            EditorPreviewSelectionBinding selection,
            IReadOnlyList<string> directives,
            bool tombstone)
    {
        var candidate = new EditorPreviewLeaseCandidate(
            selection,
            directives,
            tombstone);
        var live = new EditorPreviewLiveObservation(
            PlayerRuntimeContextSnapshot.FromPreviewMode(true),
            selection with { },
            1);
        return new EditorPreviewLeaseGate().TryAuthorize(candidate, live);
    }

    private static EditorPreviewLeaseCandidate Candidate(
        EditorPreviewSelectionBinding selection,
        params string[] directives) => new(
        selection,
        Array.AsReadOnly(directives),
        IsTombstone: false);

    private static EditorPreviewSelectionBinding Selection(
        long generation,
        int request,
        long observation,
        string script,
        EditorPreviewSceneAddress? scene = null) => new(
        generation,
        request,
        observation,
        CommandIdentity.CompiledScript(script),
        scene ?? Scene());

    private static EditorPreviewSceneAddress Scene() => new(
        new string('D', 24),
        NodeGuid,
        5,
        SceneFingerprint);
}
