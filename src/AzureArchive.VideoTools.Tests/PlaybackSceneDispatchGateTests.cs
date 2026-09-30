using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Projects;

namespace AzureArchive.VideoTools.Tests;

internal static class PlaybackSceneDispatchGateTests
{
    public static void AuthorizesEachNewSceneWindowOnceIncludingReplay()
    {
        var gate = new PlaybackSceneDispatchGate();
        PlaybackCommandBatch batch = Batch();

        var first = gate.TryAuthorize(10, batch);
        var duplicate = gate.TryAuthorize(10, batch);
        var replay = gate.TryAuthorize(25, batch);

        AssertEx.True(first.Success, first.Error);
        AssertEx.Equal(1L, AssertEx.NotNull(first.Value).ExecutionOrdinal);
        AssertEx.False(duplicate.Success);
        AssertEx.True(replay.Success, replay.Error);
        PlaybackSceneDispatchAuthorization replayAuthorization =
            AssertEx.NotNull(replay.Value);
        AssertEx.Equal(2L, replayAuthorization.ExecutionOrdinal);
        AssertEx.Equal(25L, replayAuthorization.WindowSequence);
    }

    public static void RejectsNonCanonicalOrOutOfOrderBatches()
    {
        var gate = new PlaybackSceneDispatchGate();
        PlaybackCommandBatch batch = Batch();

        var malformed = batch with
        {
            Commands = Array.AsReadOnly(new[]
            {
                batch.Commands[0] with { Order = 1 }
            })
        };
        AssertEx.False(gate.TryAuthorize(1, malformed).Success);
        AssertEx.True(gate.TryAuthorize(2, batch).Success);
        AssertEx.False(gate.TryAuthorize(1, batch).Success);
    }

    public static void AuthorizesSlotPendingInstructionsWithFamilyAwareValidation()
    {
        var gate = new PlaybackSceneDispatchGate();
        var scene = new SceneKey(
            "6fcb9e8d-a1e2-4bb4-a610-b59795bd1036",
            1,
            new string('A', 64));
        var pending = new PlaybackCommandInstruction(
            "b98e4f35-2b3c-4ca4-89ad-920b4ab513bc",
            0,
            CommandTimelinePhase.SceneEnter,
            SlotPendingCommandFamilyCompiler.CommandTypeId,
            SlotPendingCommandFamilyCompiler.CapabilityId,
            "#charp;3;set;x=500;flipX=true");
        var batch = new PlaybackCommandBatch(
            scene,
            11,
            new string('B', 64),
            new CompiledScriptIdentity(new string('C', 64), 10, 1),
            Array.AsReadOnly(new[] { pending }));

        var authorized = gate.TryAuthorize(30, batch);
        AssertEx.True(authorized.Success, authorized.Error);

        var wrongCapability = batch with
        {
            Commands = Array.AsReadOnly(new[]
            {
                pending with { RequiredCapability = CharacterTransformCommandFamilyCompiler.CapabilityId }
            })
        };
        AssertEx.False(gate.TryAuthorize(31, wrongCapability).Success);

        var nonCanonicalDirective = batch with
        {
            Commands = Array.AsReadOnly(new[]
            {
                pending with { CanonicalDirective = "#charp;3;pivot;x=1" }
            })
        };
        AssertEx.False(gate.TryAuthorize(32, nonCanonicalDirective).Success);
    }

    private static PlaybackCommandBatch Batch()
    {
        var scene = new SceneKey(
            "6fcb9e8d-a1e2-4bb4-a610-b59795bd1036",
            0,
            new string('A', 64));
        var command = new PlaybackCommandInstruction(
            "a98e4f35-2b3c-4ca4-89ad-920b4ab513bc",
            0,
            CommandTimelinePhase.SceneEnter,
            CharacterTransformCommandFamilyCompiler.CommandTypeId,
            CharacterTransformCommandFamilyCompiler.CapabilityId,
            "#char;3;move;dx=500;duration=2000;easing=easeInOut");
        return new PlaybackCommandBatch(
            scene,
            0,
            new string('B', 64),
            new CompiledScriptIdentity(new string('C', 64), 10, 1),
            Array.AsReadOnly(new[] { command }));
    }
}
