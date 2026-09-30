using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Commands;

public sealed record PlaybackDispatchCanaryPolicy(
    bool Enabled,
    int PlaybackRecordIndex,
    string CommandId,
    string CanonicalDirective,
    int MaximumExecutions);

public sealed record PlaybackDispatchAuthorization(
    int ExecutionOrdinal,
    int PlaybackRecordIndex,
    string CommandId,
    string SceneIdentity,
    string CanonicalDirective);

public sealed class PlaybackDispatchCanaryGate
{
    private readonly object _gate = new();
    private readonly PlaybackDispatchCanaryPolicy _policy;
    private int _authorizedExecutions;

    private PlaybackDispatchCanaryGate(PlaybackDispatchCanaryPolicy policy)
    {
        _policy = policy;
    }

    public static Result<PlaybackDispatchCanaryGate> Create(
        PlaybackDispatchCanaryPolicy policy)
    {
        if (policy == null)
        {
            return Result<PlaybackDispatchCanaryGate>.Fail(
                "Dispatch canary policy is required.");
        }

        if (!policy.Enabled)
        {
            return Result<PlaybackDispatchCanaryGate>.Fail(
                "Dispatch canary must be explicitly enabled.");
        }

        if (policy.PlaybackRecordIndex < 0)
        {
            return Result<PlaybackDispatchCanaryGate>.Fail(
                "Dispatch canary playback record index must be non-negative.");
        }

        if (!Guid.TryParseExact(policy.CommandId, "D", out _))
        {
            return Result<PlaybackDispatchCanaryGate>.Fail(
                "Dispatch canary command ID must be a canonical GUID.");
        }

        var canonical = new CharacterTransformCommandFamilyCompiler()
            .Canonicalize(policy.CanonicalDirective);
        if (!canonical.Success || canonical.Value == null
            || !string.Equals(
                canonical.Value.Directive,
                policy.CanonicalDirective,
                StringComparison.Ordinal))
        {
            return Result<PlaybackDispatchCanaryGate>.Fail(
                "Dispatch canary directive must be a canonical character transform directive.");
        }

        if (policy.MaximumExecutions is < 1 or > 2)
        {
            return Result<PlaybackDispatchCanaryGate>.Fail(
                "Dispatch canary maximum executions must be one or two.");
        }

        return Result<PlaybackDispatchCanaryGate>.Ok(
            new PlaybackDispatchCanaryGate(policy));
    }

    public Result<PlaybackDispatchAuthorization> TryAuthorize(
        PlaybackCommandBatch batch)
    {
        if (batch == null)
        {
            return Result<PlaybackDispatchAuthorization>.Fail(
                "Dispatch batch is required.");
        }

        if (batch.PlaybackRecordIndex != _policy.PlaybackRecordIndex)
        {
            return Result<PlaybackDispatchAuthorization>.Fail(
                "Dispatch batch record does not match the canary record.");
        }

        if (batch.Commands == null || batch.Commands.Count != 1)
        {
            return Result<PlaybackDispatchAuthorization>.Fail(
                "Dispatch canary requires exactly one command in the batch.");
        }

        PlaybackCommandInstruction command = batch.Commands[0];
        if (!string.Equals(command.CommandId, _policy.CommandId, StringComparison.Ordinal)
            || command.Order != 0
            || command.Phase != CommandTimelinePhase.SceneEnter
            || !string.Equals(
                command.CommandType,
                CharacterTransformCommandFamilyCompiler.CommandTypeId,
                StringComparison.Ordinal)
            || !string.Equals(
                command.RequiredCapability,
                CharacterTransformCommandFamilyCompiler.CapabilityId,
                StringComparison.Ordinal)
            || !string.Equals(
                command.CanonicalDirective,
                _policy.CanonicalDirective,
                StringComparison.Ordinal))
        {
            return Result<PlaybackDispatchAuthorization>.Fail(
                "Dispatch command does not exactly match the canary policy.");
        }

        string sceneIdentity = BuildSceneIdentity(batch);
        if (string.IsNullOrWhiteSpace(sceneIdentity))
        {
            return Result<PlaybackDispatchAuthorization>.Fail(
                "Dispatch batch scene identity is invalid.");
        }

        lock (_gate)
        {
            if (_authorizedExecutions >= _policy.MaximumExecutions)
            {
                return Result<PlaybackDispatchAuthorization>.Fail(
                    "Dispatch canary process execution limit has been reached.");
            }

            int ordinal = ++_authorizedExecutions;
            return Result<PlaybackDispatchAuthorization>.Ok(
                new PlaybackDispatchAuthorization(
                    ordinal,
                    batch.PlaybackRecordIndex,
                    command.CommandId,
                    sceneIdentity,
                    command.CanonicalDirective));
        }
    }

    private static string BuildSceneIdentity(PlaybackCommandBatch batch)
    {
        if (batch.Scene == null
            || string.IsNullOrWhiteSpace(batch.Scene.NodeGuid)
            || batch.Scene.SceneIndex < 0
            || string.IsNullOrWhiteSpace(batch.Scene.Fingerprint))
        {
            return string.Empty;
        }

        return $"playback-sidecar:{batch.Scene.NodeGuid}:{batch.Scene.SceneIndex}:{batch.Scene.Fingerprint}";
    }
}
