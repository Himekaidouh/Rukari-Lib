using HarmonyLib;
using Rukari.Lib.Editor;
using Studio.Scripts;

namespace Rukari.Lib.Runtime.Editor;

internal static class EditorHost
{
    private static EditorDocumentSession? _session;
    private static IDisposable? _lease;
    private static Harmony? _patches;

    internal static void Initialize(IModRuntime runtime)
    {
        var session = new EditorDocumentSession(new NativeEditorDocumentBackend(),
            () => runtime.State == RuntimeState.Ready, () => runtime.IsMainThread);
        var patches = new Harmony(Plugin.Guid + ".editor-selection");
        try
        {
            var invalidation = new HarmonyMethod(typeof(EditorHost), nameof(Invalidate));
            patches.Patch(AccessTools.Method(typeof(ScriptNodeInspector), nameof(ScriptNodeInspector.DataList), new[] { typeof(int) })
                ?? throw new MissingMethodException("ScriptNodeInspector.DataList(int)"), prefix: invalidation);
            patches.Patch(AccessTools.Method(typeof(ScriptNodeInspector), nameof(ScriptNodeInspector.OnChildSelect), new[] { typeof(Selectable) })
                ?? throw new MissingMethodException("ScriptNodeInspector.OnChildSelect(Selectable)"), prefix: invalidation);
            var registered = runtime.RegisterService<IEditorDocumentService>(Plugin.Guid, session,
                new("rukari.editor.documents", Plugin.Guid, "0.2.0", CapabilityLevel.Experimental,
                    "Exact selection/revision, official input transaction, verified readback and guarded rollback; native lifetime remains version dependent."));
            if (!registered.Success) throw new InvalidOperationException(registered.Error?.Message);
            _session = session;
            _lease = registered.Value;
            _patches = patches;
        }
        catch { patches.UnpatchSelf(); throw; }
    }

    private static void Invalidate() => _session?.InvalidateSelection();
    internal static void Shutdown()
    {
        _lease?.Dispose(); _lease = null;
        _patches?.UnpatchSelf(); _patches = null;
        _session = null;
    }
}
