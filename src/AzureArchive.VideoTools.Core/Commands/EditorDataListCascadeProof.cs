namespace AzureArchive.VideoTools.Core.Commands;

/// <summary>
/// Fresh managed evidence captured at one DataList boundary. RequestId remains
/// opaque; ActiveWindowSequence comes from the actual open window snapshot.
/// </summary>
public sealed record EditorDataListCascadeProof(
    int RequestId,
    int MainThreadId,
    long ActiveWindowSequence,
    long IssuedWindowSequence,
    long ClosedWindowWatermark,
    EditorPreviewSceneAddress? Scene,
    EditorInputSceneCaptureProof? InputProof);

/// <summary>
/// Allows only an exact duplicate within the same still-open preview window.
/// A reuse decision preserves the original generation and first-log state;
/// it never transfers an identity or creates one from the captured scene.
/// </summary>
public static class EditorDataListCascadePlanner
{
    public static bool CanReuse(
        EditorDataListCascadeProof? previous,
        EditorDataListCascadeProof? current,
        out string error)
    {
        error = Validate(previous) ?? Validate(current) ?? string.Empty;
        if (error.Length != 0) return false;

        EditorDataListCascadeProof prior = previous!;
        EditorDataListCascadeProof actual = current!;
        if (prior.RequestId != actual.RequestId)
            error = "data-list-cascade-request-drift";
        else if (prior.MainThreadId != actual.MainThreadId)
            error = "data-list-cascade-main-thread-drift";
        else if (prior.ActiveWindowSequence != actual.ActiveWindowSequence
            || prior.IssuedWindowSequence != actual.IssuedWindowSequence
            || prior.ClosedWindowWatermark != actual.ClosedWindowWatermark)
            error = "data-list-cascade-window-drift";
        else if (prior.Scene != actual.Scene)
            error = "data-list-cascade-scene-drift";
        else if (prior.InputProof != actual.InputProof)
            error = "data-list-cascade-input-proof-drift";

        return error.Length == 0;
    }

    private static string? Validate(EditorDataListCascadeProof? proof)
    {
        if (proof == null)
            return "data-list-cascade-proof-unavailable";
        if (proof.RequestId < 0)
            return "data-list-cascade-request-invalid";
        if (proof.MainThreadId <= 0)
            return "data-list-cascade-main-thread-unavailable";
        if (proof.ActiveWindowSequence <= 0
            || proof.ActiveWindowSequence != proof.IssuedWindowSequence
            || proof.ClosedWindowWatermark < 0
            || proof.ActiveWindowSequence <= proof.ClosedWindowWatermark)
            return "data-list-cascade-window-not-open";

        EditorPreviewSceneAddress? scene = proof.Scene;
        if (scene == null || !IsFingerprint(scene.ProjectKey)
            || !Guid.TryParseExact(scene.NodeGuid, "D", out _)
            || scene.SceneIndex < 0 || !IsFingerprint(scene.Fingerprint))
            return "data-list-cascade-scene-incomplete-or-invalid";

        EditorInputSceneCaptureProof? input = proof.InputProof;
        if (input == null || string.IsNullOrWhiteSpace(input.ContextId)
            || input.DialogueText == null || input.AdditionalPrompt == null)
            return "data-list-cascade-input-proof-incomplete";
        if (!string.Equals(input.ProjectKey, scene.ProjectKey, StringComparison.Ordinal)
            || !string.Equals(input.NodeGuid, scene.NodeGuid, StringComparison.Ordinal)
            || input.SceneIndex != scene.SceneIndex)
            return "data-list-cascade-input-proof-does-not-match-scene";

        return null;
    }

    private static bool IsFingerprint(string? value) =>
        value != null && value.Length == 24 && value.All(Uri.IsHexDigit);
}
