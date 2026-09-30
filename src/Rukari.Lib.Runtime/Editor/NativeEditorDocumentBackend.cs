using System.Collections.ObjectModel;
using System.Reflection;
using Rukari.Lib.Editor;
using Studio.Scripts;
using Studio.Scripts.Nodes;

namespace Rukari.Lib.Runtime.Editor;

// Native wrappers never escape one synchronous call. This follows the previously verified
// UIInput.Set -> SetAdditionalPrompt path without parsing any feature's directives.
internal sealed class NativeEditorDocumentBackend : IEditorDocumentBackend
{
    private sealed record Live(ScriptNodeInspector Inspector, Script Current, UIInput Input, EditorBackendDocument Document);

    public ModResult<EditorBackendDocument> Read()
    {
        try { return ModResult<EditorBackendDocument>.Ok(Resolve().Document); }
        catch (Exception e) { return ModResult<EditorBackendDocument>.Fail(ModErrorCode.NotReady, Unwrap(e)); }
    }

    public ModResult<EditorBackendDocument> Write(EditorBackendDocument expected, string text)
    {
        bool attempted = false;
        try
        {
            Live live = Resolve();
            if (!Matches(live.Document, expected))
                return ModResult<EditorBackendDocument>.Fail(ModErrorCode.Conflict, "写入前台词已变化。");
            attempted = true;
            live.Input.Set(text, false);
            live.Inspector.SetAdditionalPrompt();
            Live verified = Resolve();
            if (verified.Document.ContextId != expected.ContextId || verified.Document.DialogueText != expected.DialogueText
                || verified.Document.AdditionalPrompt != text)
                throw new InvalidOperationException("官方输入写回结果不一致。");
            return ModResult<EditorBackendDocument>.Ok(verified.Document);
        }
        catch (Exception e)
        {
            string rollback = string.Empty;
            if (attempted)
            {
                try
                {
                    // Resolve anew; never write through a stale inspector after a project/line change.
                    Live active = Resolve(requireSynced: false);
                    if (active.Document.ContextId != expected.ContextId || active.Document.DialogueText != expected.DialogueText)
                        rollback = " 台词已切换，未向新台词回滚。";
                    else if (active.Document.AdditionalPrompt != text && active.Document.AdditionalPrompt != expected.AdditionalPrompt)
                        rollback = " 内容另有变化，未覆盖现有修改。";
                    else if (Get<string>(active.Input, "value") is string pending
                        && pending != text && pending != expected.AdditionalPrompt)
                        rollback = " 输入框另有未提交修改，未覆盖现有输入。";
                    else
                    {
                        active.Input.Set(expected.AdditionalPrompt, false);
                        active.Inspector.SetAdditionalPrompt();
                        if (!Matches(Resolve().Document, expected)) throw new InvalidOperationException("回滚核对失败");
                        rollback = " 原始内容已恢复并核对。";
                    }
                }
                catch (Exception r) { rollback = " 回滚失败：" + Unwrap(r); }
            }
            return ModResult<EditorBackendDocument>.Fail(ModErrorCode.ProviderFailed, Unwrap(e) + rollback);
        }
    }

    private static bool Matches(EditorBackendDocument a, EditorBackendDocument b) => a.ContextId == b.ContextId
        && a.DialogueText == b.DialogueText && a.AdditionalPrompt == b.AdditionalPrompt;

    private static Live Resolve(bool requireSynced = true)
    {
        if (!EditorWorkspaceContext.IsNodeEditorVisible)
            throw new InvalidOperationException("请先进入项目并打开一个 Script 节点。");
        var inspector = GetStatic<ScriptNodeInspector>(typeof(ScriptNodeInspector), "instance");
        RequireAlive(inspector, "请先打开剧情编辑器。");
        if (inspector!.rearrangeScheduled)
            throw new InvalidOperationException("工作台正在整理台词，请稍后重试。");
        var node = inspector.scriptNode;
        RequireAlive(node, "当前台词节点已失效。");
        // The old selected-item accessor is a quarantined native boundary. Read the existing
        // visible rows instead; no selection mutation, callback argument or MCP session is needed.
        var rows = inspector.scriptNodeListItemsCache;
        RequireAlive(rows, "台词列表尚未就绪。");
        if (rows.Count > 10000) throw new InvalidOperationException("当前节点的台词列表过大。");
        ScriptListItem? item = null;
        for (int rowIndex = 0, count = rows.Count; rowIndex < count; rowIndex++)
        {
            var row = rows[rowIndex];
            if (row == null || row.WasCollected || row.Pointer == IntPtr.Zero
                || row.gameObject == null || !row.gameObject.activeInHierarchy || !row.selected) continue;
            if (item != null) throw new InvalidOperationException("请只选择一句台词。");
            item = row;
        }
        RequireAlive(item, "请先在工作台选择一句台词。");
        var itemNode = item!.scriptNode;
        var itemInspector = item.inspector;
        RequireAlive(itemNode, "选中行已失效。");
        RequireAlive(itemInspector, "选中行的工作台已失效。");
        if (itemNode.Pointer != node!.Pointer || itemInspector.Pointer != inspector.Pointer)
            throw new InvalidOperationException("选中行与当前节点不一致。");
        int index = item.index;
        var scripts = Get<Il2CppSystem.Collections.Generic.List<Script>>(node!, "scripts");
        if (scripts == null || index < 0 || index >= scripts.Count) throw new InvalidOperationException("台词索引已变化。");
        Script current = scripts[index];
        RequireAlive(current, "当前台词已失效。");
        var input = Get<UIInput>(inspector!, "additionalPromptInput");
        RequireAlive(input, "额外指令输入暂不可用。");
        string prompt = Get<string>(current, "additionalPrompt") ?? string.Empty;
        if (requireSynced && (Get<string>(input!, "value") ?? string.Empty) != prompt)
            throw new InvalidOperationException("请先完成额外指令输入，等待编辑器同步。");
        string dialogue = Get<string>(current, "text") ?? string.Empty;
        var contentInput = inspector.contentInput;
        RequireAlive(contentInput, "台词输入暂不可用。");
        if (requireSynced && contentInput.value != dialogue)
            throw new InvalidOperationException("请先完成台词输入，等待编辑器同步。");
        // Existing public interop identity properties only; these numbers are never dereferenced.
        string context = $"{inspector!.Pointer.ToInt64():X}:{node!.Pointer.ToInt64():X}:{current.Pointer.ToInt64():X}:{index}";
        var fields = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal)
            { ["voice"] = Get<string>(current, "voice") ?? string.Empty });
        return new(inspector, current, input!, new(context, dialogue, prompt, fields));
    }

    private static T? Get<T>(object o, string name) => (T?)o.GetType().GetProperty(name,
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(o);
    private static T? GetStatic<T>(Type type, string name) => (T?)type.GetProperty(name,
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
    private static void RequireAlive(object? value, string message)
    {
        if (value == null || Get<bool>(value, "WasCollected") || Get<IntPtr>(value, "Pointer") == IntPtr.Zero)
            throw new InvalidOperationException(message);
    }
    private static string Unwrap(Exception e) => (e is TargetInvocationException && e.InnerException != null ? e.InnerException : e).Message;
}
