using AzureArchive.VideoTools.Core.Results;
using Rukari.Lib.Editor;

namespace AzureArchive.VideoTools.Core.Commands;

/// <summary>Primitive values freshly read by a ready main-thread runtime adapter.</summary>
public sealed record EditorInputSceneFrameContext(
    long InspectorPointer,
    long NodePointer,
    long CachePointer,
    long ScriptsPointer,
    long ContentInputPointer,
    long AdditionalPromptInputPointer,
    string ProjectKey,
    string NodeGuid,
    int ScriptCount,
    bool RearrangeScheduled);

/// <summary>One cache entry; Active means its live GameObject is visible in the hierarchy.</summary>
public sealed record EditorInputSceneRowProof(
    long RowPointer,
    long InspectorPointer,
    long NodePointer,
    int SceneIndex,
    long ScriptPointer,
    bool Active,
    bool Selected,
    string? DialogueText,
    string? AdditionalPrompt);

/// <summary>Copy every cache entry in order; retain no native wrapper or mutable native list.</summary>
public sealed record EditorInputSceneFrame(
    EditorInputSceneFrameContext Context,
    string? DialogueInput,
    string? AdditionalPromptInput,
    IReadOnlyList<EditorInputSceneRowProof> Rows);

/// <summary>Actual input-matched row evidence, never a compiled identity or selection token.</summary>
public sealed record EditorInputSceneCaptureProof(
    string ProjectKey,
    string NodeGuid,
    int SceneIndex,
    string ContextId,
    string DialogueText,
    string AdditionalPrompt);

/// <summary>
/// Narrow DataList capture fallback. The adapter must first enforce runtime/main-thread/workspace
/// guards and restrict this path to an absent selected row. A result is provisional until the
/// ordinary selected-row read and shared document confirmation both succeed.
/// </summary>
public static class EditorInputSceneCapturePlanner
{
    public const int MaximumCacheRows = 10000;

    public static Result<EditorInputSceneCaptureProof> Capture(
        EditorInputSceneFrame? before,
        EditorInputSceneFrame? after)
    {
        string? invalid = ValidateFrame(before) ?? ValidateFrame(after);
        if (invalid != null) return Result<EditorInputSceneCaptureProof>.Fail(invalid);
        EditorInputSceneFrame first = before!;
        EditorInputSceneFrame second = after!;
        if (first.Context != second.Context
            || !string.Equals(first.DialogueInput, second.DialogueInput, StringComparison.Ordinal)
            || !string.Equals(first.AdditionalPromptInput, second.AdditionalPromptInput, StringComparison.Ordinal)
            || !first.Rows.SequenceEqual(second.Rows))
            return Result<EditorInputSceneCaptureProof>.Fail("input-scene-frame-drift");

        EditorInputSceneRowProof? match = null;
        foreach (EditorInputSceneRowProof row in first.Rows)
        {
            if (!row.Active
                || !string.Equals(row.DialogueText, first.DialogueInput, StringComparison.Ordinal)
                || !string.Equals(row.AdditionalPrompt, first.AdditionalPromptInput, StringComparison.Ordinal))
                continue;
            if (match != null)
                return Result<EditorInputSceneCaptureProof>.Fail("input-scene-match-ambiguous");
            match = row;
        }

        if (match == null)
            return Result<EditorInputSceneCaptureProof>.Fail("input-scene-match-unavailable");

        // The same actual pointer/index format used by the shared document backend. These
        // primitive numbers are compared only; they are never dereferenced or used as requests.
        string contextId = FormattableString.Invariant(
            $"{first.Context.InspectorPointer:X}:{first.Context.NodePointer:X}:{match.ScriptPointer:X}:{match.SceneIndex}");
        return Result<EditorInputSceneCaptureProof>.Ok(new EditorInputSceneCaptureProof(
            first.Context.ProjectKey, first.Context.NodeGuid, match.SceneIndex, contextId,
            match.DialogueText!, match.AdditionalPrompt!));
    }

    public static Result Confirm(
        EditorInputSceneCaptureProof? capture,
        EditorDocumentSnapshot? actual)
    {
        if (capture == null || actual == null)
            return Result.Fail("input-scene-shared-confirmation-unavailable");
        if (string.IsNullOrWhiteSpace(capture.ContextId)
            || capture.DialogueText == null || capture.AdditionalPrompt == null
            || actual.DialogueText == null || actual.AdditionalPrompt == null
            || !string.Equals(capture.ContextId, actual.ContextId, StringComparison.Ordinal)
            || !string.Equals(capture.DialogueText, actual.DialogueText, StringComparison.Ordinal)
            || !string.Equals(capture.AdditionalPrompt, actual.AdditionalPrompt, StringComparison.Ordinal))
            return Result.Fail("input-scene-shared-confirmation-drift");
        return Result.Ok();
    }

    private static string? ValidateFrame(EditorInputSceneFrame? frame)
    {
        if (frame == null || frame.Context == null || frame.Rows == null
            || frame.Rows.Count > MaximumCacheRows
            || frame.DialogueInput == null || frame.AdditionalPromptInput == null)
            return "input-scene-frame-unavailable";
        EditorInputSceneFrameContext context = frame.Context;
        if (context.InspectorPointer == 0 || context.NodePointer == 0
            || context.CachePointer == 0 || context.ScriptsPointer == 0
            || context.ContentInputPointer == 0 || context.AdditionalPromptInputPointer == 0
            || string.IsNullOrWhiteSpace(context.ProjectKey) || string.IsNullOrWhiteSpace(context.NodeGuid)
            || context.ScriptCount <= 0 || context.RearrangeScheduled)
            return "input-scene-context-unavailable";

        var indices = new HashSet<int>();
        var rows = new HashSet<long>();
        foreach (EditorInputSceneRowProof row in frame.Rows)
        {
            if (row == null) return "input-scene-row-unavailable";
            if (!row.Active) continue;
            if (row.RowPointer == 0 || row.ScriptPointer == 0
                || row.InspectorPointer != context.InspectorPointer || row.NodePointer != context.NodePointer
                || row.SceneIndex < 0 || row.SceneIndex >= context.ScriptCount
                || row.DialogueText == null || row.AdditionalPrompt == null)
                return "input-scene-row-structure-invalid";
            if (!indices.Add(row.SceneIndex) || !rows.Add(row.RowPointer))
                return "input-scene-duplicate-visible-row";
            if (row.Selected) return "input-scene-selected-row-present";
        }
        return null;
    }
}
