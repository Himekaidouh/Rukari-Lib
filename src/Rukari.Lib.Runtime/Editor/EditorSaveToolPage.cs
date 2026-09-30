using Rukari.Lib.Editor;
using Rukari.Lib.Tools;

namespace Rukari.Lib.Runtime.Editor;

/// <summary>
/// The shared save page (2026-09-21): one button that runs the editor's whole save chain, on the
/// rail so it is there whatever feature packages are enabled.
/// <para>
/// Rukari lib declares the contract but cannot reach the editor itself, so the page asks the runtime
/// for the implementation when the button is pressed. A package that is not loaded, or a service
/// that is not ready, is reported as such — a save button that silently does nothing is worse than
/// no button at all.
/// </para>
/// </summary>
internal sealed class EditorSaveToolPage
{
    internal const string PageId = "rukari.lib.editor.save";
    private const string OwnerId = "rukari.lib.runtime";

    private readonly IModRuntime _runtime;
    private readonly Action<string> _log;
    private string _status = string.Empty;

    private EditorSaveToolPage(IModRuntime runtime, Action<string> log)
    {
        _runtime = runtime;
        _log = log;
    }

    /// <summary>Registers the page on the rail; a failure is logged and changes nothing else.</summary>
    internal static IDisposable? Install(IToolboxService toolbox, IModRuntime runtime, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(toolbox);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(log);
        var page = new EditorSaveToolPage(runtime, log);
        ModResult<IDisposable> registered = toolbox.RegisterPage(
            OwnerId,
            PageId,
            "工程保存",
            page.Snapshot,
            page.Handle,
            "master");
        if (!registered.Success)
        {
            log("Save page was not registered: " + registered.Error?.Message);
            return null;
        }

        log("Save page registered: 保存并发布 (save -> compile -> publish the playable file).");
        return registered.Value;
    }

    private ToolPageSnapshot Snapshot()
    {
        ModResult<IEditorSaveService> service = _runtime.GetService<IEditorSaveService>();
        bool available = service.Success && service.Value != null;
        return new ToolPageSnapshot(
            "手动保存工程，用于预览效果。",
            new[]
            {
                new ToolButton("save", "保存并发布", Enabled: available) { Skin = ToolButtonSkins.Official }
            },
            Items: null,
            Status: _status.Length != 0
                ? _status
                : available
                    ? string.Empty
                    : "保存服务不可用：" + (service.Error?.Message ?? "未注册"),
            AllowSearch: false);
    }

    private void Handle(ToolAction action)
    {
        if (!string.Equals(action.Id, "save", StringComparison.Ordinal)) return;
        ModResult<IEditorSaveService> service = _runtime.GetService<IEditorSaveService>();
        if (!service.Success || service.Value == null)
        {
            _status = "保存失败：" + (service.Error?.Message ?? "保存服务未注册（需要「更多的画面效果」）。");
            return;
        }

        ModResult<EditorSaveReceipt> result = service.Value.SaveAndPublish();
        if (!result.Success || result.Value == null)
        {
            _status = "保存失败：" + (result.Error?.Message ?? "未知原因");
            _log("Save request failed: " + (result.Error?.Message ?? "unknown"));
            return;
        }

        EditorSaveReceipt receipt = result.Value;
        _status = (receipt.Complete
                ? $"已保存/编译/发布 {DateTimeOffset.Now:HH:mm:ss}"
                : $"未完成 保存={receipt.Saved} 编译={receipt.Compiled} 发布={receipt.Published}")
            + $"｜{receipt.ElapsedMilliseconds} ms｜{receipt.ProjectName}"
            + (receipt.Detail.Length != 0 ? $"｜{receipt.Detail}" : string.Empty);
        _log(
            $"Save: project='{receipt.ProjectName}'; saved={receipt.Saved}; compiled={receipt.Compiled}; "
            + $"published={receipt.Published}; elapsedMs={receipt.ElapsedMilliseconds}; detail={receipt.Detail}");
    }
}
