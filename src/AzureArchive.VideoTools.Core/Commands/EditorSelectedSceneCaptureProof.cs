using AzureArchive.VideoTools.Core.Results;
using Rukari.Lib.Editor;

namespace AzureArchive.VideoTools.Core.Commands;

/// <summary>
/// Managed values from one existing selected-row read. The adapter must already
/// have verified the shared document against that actual row's context and texts.
/// </summary>
public sealed record EditorSelectedSceneCaptureFrame(
    EditorPreviewSceneAddress? Scene,
    string? DialogueText,
    EditorDocumentSnapshot? Document);

/// <summary>
/// Retains complete input evidence only after two fresh selected-row reads agree.
/// This does not select a row, create a compiled identity or authorize execution.
/// The separate unique-input fallback still rejects every selected row.
/// </summary>
public static class EditorSelectedSceneCapturePlanner
{
    public static Result<EditorInputSceneCaptureProof> Capture(
        EditorSelectedSceneCaptureFrame? before,
        EditorSelectedSceneCaptureFrame? after)
    {
        string? invalid = Validate(before) ?? Validate(after);
        if (invalid != null) return Result<EditorInputSceneCaptureProof>.Fail(invalid);
        EditorSelectedSceneCaptureFrame first = before!;
        EditorSelectedSceneCaptureFrame second = after!;
        EditorDocumentSnapshot prior = first.Document!;
        EditorDocumentSnapshot actual = second.Document!;
        if (first.Scene != second.Scene
            || !string.Equals(prior.ContextId, actual.ContextId, StringComparison.Ordinal)
            || !string.Equals(prior.DialogueText, actual.DialogueText, StringComparison.Ordinal)
            || !string.Equals(prior.AdditionalPrompt, actual.AdditionalPrompt, StringComparison.Ordinal))
            return Result<EditorInputSceneCaptureProof>.Fail("selected-scene-capture-drift");

        EditorPreviewSceneAddress scene = first.Scene!;
        return Result<EditorInputSceneCaptureProof>.Ok(new EditorInputSceneCaptureProof(
            scene.ProjectKey, scene.NodeGuid, scene.SceneIndex, prior.ContextId,
            prior.DialogueText, prior.AdditionalPrompt));
    }

    private static string? Validate(EditorSelectedSceneCaptureFrame? frame)
    {
        EditorPreviewSceneAddress? scene = frame?.Scene;
        if (scene == null || !IsFingerprint(scene.ProjectKey)
            || !Guid.TryParseExact(scene.NodeGuid, "D", out _)
            || scene.SceneIndex < 0 || !IsFingerprint(scene.Fingerprint))
            return "selected-scene-capture-address-incomplete-or-invalid";
        EditorDocumentSnapshot? document = frame!.Document;
        if (document == null || string.IsNullOrWhiteSpace(document.ContextId)
            || document.DialogueText == null || document.AdditionalPrompt == null
            || frame.DialogueText == null)
            return "selected-scene-capture-document-incomplete";
        if (!string.Equals(frame.DialogueText, document.DialogueText, StringComparison.Ordinal))
            return "selected-scene-capture-dialogue-mismatch";
        return null;
    }

    private static bool IsFingerprint(string? value) =>
        value != null && value.Length == 24 && value.All(Uri.IsHexDigit);
}
