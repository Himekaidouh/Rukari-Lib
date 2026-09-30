using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Projects;

namespace AzureArchive.VideoTools.Tests;

internal static class PlaybackDispatchCanaryGateTests
{
    private const string CommandId = "a98e4f35-2b3c-4ca4-89ad-920b4ab513bc";
    private const string Directive =
        "#char;3;move;dx=500;duration=2000;easing=easeInOut";

    public static void AuthorizesExactBatchOnlyOnce()
    {
        PlaybackDispatchCanaryGate gate = CreateGate();
        PlaybackCommandBatch batch = Batch();

        var first = gate.TryAuthorize(batch);
        AssertEx.True(first.Success, first.Error);
        PlaybackDispatchAuthorization authorization = AssertEx.NotNull(first.Value);
        AssertEx.Equal(1, authorization.ExecutionOrdinal);
        AssertEx.Equal(0, authorization.PlaybackRecordIndex);
        AssertEx.Equal(CommandId, authorization.CommandId);
        AssertEx.Equal(Directive, authorization.CanonicalDirective);
        AssertEx.True(
            authorization.SceneIdentity.StartsWith(
                "playback-sidecar:node-guid:0:",
                StringComparison.Ordinal));

        var second = gate.TryAuthorize(batch);
        AssertEx.False(second.Success);
        AssertEx.True(
            second.Error.Contains("limit", StringComparison.OrdinalIgnoreCase),
            second.Error);
    }

    public static void RejectsDriftWithoutConsumingBudget()
    {
        PlaybackDispatchCanaryGate gate = CreateGate();
        PlaybackCommandBatch exact = Batch();
        PlaybackCommandBatch drifted = exact with
        {
            Commands = Array.AsReadOnly(new[]
            {
                exact.Commands[0] with { CanonicalDirective = "#char;3;move;dx=400;duration=2000;easing=easeInOut" }
            })
        };

        var rejected = gate.TryAuthorize(drifted);
        AssertEx.False(rejected.Success);

        var accepted = gate.TryAuthorize(exact);
        AssertEx.True(accepted.Success, accepted.Error);
        AssertEx.Equal(1, AssertEx.NotNull(accepted.Value).ExecutionOrdinal);
    }

    public static void AuthorizesReplayExactlyTwice()
    {
        var created = PlaybackDispatchCanaryGate.Create(
            Policy() with { MaximumExecutions = 2 });
        AssertEx.True(created.Success, created.Error);
        PlaybackDispatchCanaryGate gate = AssertEx.NotNull(created.Value);
        PlaybackCommandBatch batch = Batch();

        var first = gate.TryAuthorize(batch);
        var second = gate.TryAuthorize(batch);
        var third = gate.TryAuthorize(batch);

        AssertEx.True(first.Success, first.Error);
        AssertEx.True(second.Success, second.Error);
        AssertEx.Equal(1, AssertEx.NotNull(first.Value).ExecutionOrdinal);
        AssertEx.Equal(2, AssertEx.NotNull(second.Value).ExecutionOrdinal);
        AssertEx.False(third.Success);
        AssertEx.True(
            third.Error.Contains("limit", StringComparison.OrdinalIgnoreCase),
            third.Error);
    }

    public static void RejectsUnsafePolicy()
    {
        var disabled = PlaybackDispatchCanaryGate.Create(
            Policy() with { Enabled = false });
        AssertEx.False(disabled.Success);

        var repeated = PlaybackDispatchCanaryGate.Create(
            Policy() with { MaximumExecutions = 3 });
        AssertEx.False(repeated.Success);

        var nonCanonical = PlaybackDispatchCanaryGate.Create(
            Policy() with { CanonicalDirective = "#CHAR;3;MOVE;dx=500" });
        AssertEx.False(nonCanonical.Success);
    }

    private static PlaybackDispatchCanaryGate CreateGate()
    {
        var created = PlaybackDispatchCanaryGate.Create(Policy());
        AssertEx.True(created.Success, created.Error);
        return AssertEx.NotNull(created.Value);
    }

    private static PlaybackDispatchCanaryPolicy Policy() => new(
        Enabled: true,
        PlaybackRecordIndex: 0,
        CommandId,
        Directive,
        MaximumExecutions: 1);

    private static PlaybackCommandBatch Batch() => new(
        new SceneKey("node-guid", 0, new string('A', 64)),
        PlaybackRecordIndex: 0,
        PlaybackRecordFingerprint: new string('B', 64),
        new CompiledScriptIdentity(new string('C', 64), 11, 2),
        Array.AsReadOnly(new[]
        {
            new PlaybackCommandInstruction(
                CommandId,
                Order: 0,
                CommandTimelinePhase.SceneEnter,
                CharacterTransformCommandFamilyCompiler.CommandTypeId,
                CharacterTransformCommandFamilyCompiler.CapabilityId,
                Directive)
        }));
}
