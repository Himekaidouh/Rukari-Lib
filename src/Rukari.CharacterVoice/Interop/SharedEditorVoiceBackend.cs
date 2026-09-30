using Rukari.CharacterVoice.Core;
using Rukari.Lib;
using Rukari.Lib.Editor;
using Rukari.Lib.Voices;

namespace Rukari.CharacterVoice.Interop;

/// <summary>The voice provider uses the single shared editor transaction owner.</summary>
internal sealed class SharedEditorVoiceBackend : IVoiceAuthoringBackend
{
    private readonly IEditorDocumentService _editor;

    internal SharedEditorVoiceBackend(IEditorDocumentService editor) => _editor = editor;

    public ModResult<IReadOnlyList<VoiceResource>> ReadCatalog()
    {
        var result = ImportedVoiceCatalogService.ReadOnMainThread();
        return result.Success
            ? ModResult<IReadOnlyList<VoiceResource>>.Ok(Array.AsReadOnly(result.Value
                .Select(entry => new VoiceResource(entry.ResourceKey, entry.DisplayName)).ToArray()))
            : ModResult<IReadOnlyList<VoiceResource>>.Fail(result.Error!);
    }

    public ModResult<VoiceAuthoringDocument> ReadDocument()
    {
        var result = _editor.ReadSelection();
        return result.Success ? Snapshot(result.Value) : ModResult<VoiceAuthoringDocument>.Fail(result.Error!);
    }

    public ModResult<VoiceAuthoringDocument> Apply(VoiceAuthoringDocument expected, string? resourceId)
    {
        var current = _editor.ReadSelection();
        if (!current.Success) return ModResult<VoiceAuthoringDocument>.Fail(current.Error!);
        var snapshot = Snapshot(current.Value);
        if (!snapshot.Success) return snapshot;
        if (snapshot.Value != expected)
            return ModResult<VoiceAuthoringDocument>.Fail(ModErrorCode.Conflict, "写入前台词或修订已变化，请重新选择。");

        if (resourceId is not null)
        {
            // The editor exposes only a managed field snapshot; resource interpretation and
            // the policy protecting native voice bindings belong to this feature module.
            if (current.Value.NativeFields is null || !current.Value.NativeFields.TryGetValue("voice", out string? nativeVoice))
                return ModResult<VoiceAuthoringDocument>.Fail(ModErrorCode.Unsupported, "编辑器未提供原配音字段，暂不能安全添加语音绑定。");
            if (!string.IsNullOrEmpty(nativeVoice) && !nativeVoice.StartsWith(VoiceDirectivePolicy.NativeIdentifierPrefix, StringComparison.Ordinal))
            {
                ScenarioResourceManager resources = ScenarioResourceManager.Instance;
                if (ReferenceEquals(resources, null))
                    return ModResult<VoiceAuthoringDocument>.Fail(ModErrorCode.NotReady, "语音资源库尚未就绪。");
                if (resources.VoiceExists(nativeVoice))
                    return ModResult<VoiceAuthoringDocument>.Fail(ModErrorCode.Conflict, "本句已有原版语音资源，当前语音指令不会覆盖它。");
            }
        }

        var edited = VoicePromptEditor.SetBinding(current.Value.AdditionalPrompt, resourceId);
        if (!edited.Success) return ModResult<VoiceAuthoringDocument>.Fail(edited.Error!);
        var applied = _editor.Replace(new(current.Value.SelectionToken, current.Value.Revision, edited.Value));
        return applied.Success ? Snapshot(applied.Value.Selection) : ModResult<VoiceAuthoringDocument>.Fail(applied.Error!);
    }

    private static ModResult<VoiceAuthoringDocument> Snapshot(EditorDocumentSnapshot document)
    {
        VoiceDirectiveParseResult parsed = VoiceDirectivePolicy.Parse(document.AdditionalPrompt);
        if (!parsed.Success)
            return ModResult<VoiceAuthoringDocument>.Fail(ModErrorCode.InvalidArgument, string.Join(" | ", parsed.Errors));
        return ModResult<VoiceAuthoringDocument>.Ok(new(document.ContextId, document.Revision, document.DialogueText,
            parsed.ResourceKey, document.SelectionToken));
    }
}
