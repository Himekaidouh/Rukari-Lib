using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Commands;

public sealed record EmbeddedPreviewSelectionBinding(
    long ObservationSequence,
    bool IsMapped,
    bool IsTrusted,
    int? PlaybackRecordIndex,
    string NodeGuid,
    int? SceneIndex,
    string SceneFingerprint);

public sealed class EmbeddedPreviewCommandGuard
{
    public Result<int> Validate(
        PlaybackCommandBatch provisionalBatch,
        CompiledScriptIdentity editorScript,
        long editorObservationSequence,
        EmbeddedPreviewSelectionBinding selectedScene)
    {
        ArgumentNullException.ThrowIfNull(provisionalBatch);
        ArgumentNullException.ThrowIfNull(editorScript);
        ArgumentNullException.ThrowIfNull(selectedScene);

        if (editorScript != provisionalBatch.CompiledScript)
        {
            return Result<int>.Fail(
                "The current editor script identity does not match the sanitized embedded command script.");
        }

        if (editorObservationSequence <= 0
            || selectedScene.ObservationSequence != editorObservationSequence)
        {
            return Result<int>.Fail(
                "The editor identity and configured scene resolution were not produced by the same observation.");
        }

        if (!selectedScene.IsMapped
            || !selectedScene.IsTrusted
            || selectedScene.PlaybackRecordIndex != provisionalBatch.PlaybackRecordIndex
            || !string.Equals(
                selectedScene.NodeGuid,
                provisionalBatch.Scene.NodeGuid,
                StringComparison.Ordinal)
            || selectedScene.SceneIndex != provisionalBatch.Scene.SceneIndex
            || !string.Equals(
                selectedScene.SceneFingerprint,
                provisionalBatch.Scene.Fingerprint,
                StringComparison.Ordinal))
        {
            return Result<int>.Fail(
                "The current trusted editor selection does not match the provisional embedded command scene.");
        }

        return Result<int>.Ok(provisionalBatch.PlaybackRecordIndex);
    }
}
