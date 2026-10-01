extern alias unitycore;

using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Rukari.Lib.Tools;
using Studio.Scripts;
using Studio.Scripts.Nodes;
using Studio.Scripts.Window;
using Behaviour = unitycore::UnityEngine.Behaviour;
using Transform = unitycore::UnityEngine.Transform;

namespace Rukari.Lib.Runtime.Editor;

/// <summary>
/// The shared, main-thread visibility boundary for tools that belong inside a Script node editor.
/// Every call resolves fresh native state; no inspector, panel, node, or selection wrapper is retained.
/// </summary>
public static class EditorWorkspaceContext
{
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly PropertyInfo? InspectorInstance = typeof(ScriptNodeInspector).GetProperty("instance",
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
    private static readonly PropertyInfo? InspectorContainerProperty = typeof(ScriptNodeInspector).GetProperty("container", InstanceFlags);
    private static readonly PropertyInfo? InspectorNode = typeof(ScriptNodeInspector).GetProperty("scriptNode", InstanceFlags);
    private static readonly PropertyInfo? InspectorLoading = typeof(ScriptNodeInspector).GetProperty("loading", InstanceFlags);
    private static readonly PropertyInfo? InspectorUnloading = typeof(ScriptNodeInspector).GetProperty("unloading", InstanceFlags);
    private static readonly PropertyInfo? ActiveInspector = typeof(InspectorContainer).GetProperty("activeInspector", InstanceFlags);
    private static Action<string>? _log;
    private static bool _failureLogged;

    internal static void Initialize(Action<string> log) { _log = log; _failureLogged = false; }
    internal static void Shutdown() { _log = null; _failureLogged = false; }

    /// <summary>
    /// True only for the visible, loaded Script inspector currently owned by its container. The project
    /// graph, hidden panels, transitions, unavailable state and off-thread calls return false. An official
    /// selector can cover a live workspace; use IsToolWorkspaceVisible when deciding to draw tools.
    /// This does not require a selected dialogue row and never changes the game's current selection.
    /// </summary>
    public static bool IsNodeEditorVisible
    {
        get
        {
            IModRuntime? runtime = ModServices.Current;
            if (runtime is null || runtime.State != RuntimeState.Ready || !runtime.IsMainThread) return false;
            try
            {
                var inspector = Read<ScriptNodeInspector>(InspectorInstance, null);
                if (!IsActive(inspector)) return false;
                var container = Read<InspectorContainer>(InspectorContainerProperty, inspector);
                if (!IsActive(container)) return false;
                var active = Read<INodeInspector>(ActiveInspector, container);
                bool isCurrent = IsAlive(active) && active!.Pointer == inspector!.Pointer;
                if (!isCurrent) return false;
                var node = Read<ScriptNode>(InspectorNode, inspector);
                // ScriptNode is a scene component. Merely retaining its wrapper does not prove its
                // native GameObject survived leaving a project; resolve that object on this call too.
                bool hasNode = IsAlive(node) && node!.gameObject != null;
                bool transitioning = Read<bool>(InspectorLoading, inspector) || Read<bool>(InspectorUnloading, inspector);
                if (!hasNode || transitioning) return false;

                // Verified AA 1.0 level2 serialization: ScriptNodeInspector GO349 has UIPanel 2208;
                // InspectorPanel GO92 has UIPanel 2189; its UI Root ancestor GO62 has UIPanel 2188.
                // All three GameObjects start active. Local child alpha alone misses a transparent
                // parent, so inspect every ancestor's current enabled/alpha instead of cached finalAlpha.
                UIPanel inspectorPanel = inspector!.GetComponent<UIPanel>();
                UIPanel containerPanel = container!.GetComponent<UIPanel>();
                bool panelsVisible = IsVisiblePanel(inspectorPanel) && IsVisiblePanel(containerPanel)
                    && AreAncestorPanelsVisible(inspector.transform.parent);
                return new EditorWorkspaceVisibilityState(true, true, true, isCurrent, hasNode,
                    panelsVisible, transitioning).IsNodeEditorVisible;
            }
            catch (Exception ex)
            {
                LogVisibilityFailure(ex);
                return false;
            }
        }
    }

    /// <summary>
    /// Whether node tools may draw and accept input now. The official window manager covers character,
    /// emotion, music, sound, background and popup-image selectors, including their blocking transitions.
    /// </summary>
    public static bool IsToolWorkspaceVisible
    {
        get
        {
            if (!IsNodeEditorVisible) return false;
            try
            {
                var inspector = Read<ScriptNodeInspector>(InspectorInstance, null);
                if (!IsActive(inspector)) return false;
                WindowManager? manager = FindWindowManager(inspector!.transform);
                if (!IsAlive(manager) || manager!.gameObject == null)
                    throw new MissingMemberException("The official window manager is unavailable.");
                var blocking = manager!.blocking;
                var panel = manager.panel;
                if (!IsAlive(blocking) || !IsAlive(panel))
                    throw new MissingMemberException("The official window visibility references are unavailable.");
                bool blockingVisible = IsVisiblePanel(panel) && AreAncestorPanelsVisible(blocking!.transform);
                // activeWindow may retain a previously used window wrapper. The actual blocking layer
                // is the visibility boundary; an old reference alone is not evidence of an open window.
                return new EditorToolVisibilityState(true, true,
                    blocking!.activeInHierarchy, blockingVisible).IsToolWorkspaceVisible;
            }
            catch (Exception ex)
            {
                LogVisibilityFailure(ex);
                return false;
            }
        }
    }

    private static WindowManager? FindWindowManager(Transform? ancestor)
    {
        // AAfix4 level2: UI Root/WindowPanel GO1 has WindowManager 1366. Its references point
        // to Blocking GO382 and UIPanel 2266. Every official resource explorer uses this manager.
        // Resolve it from this inspector's own ancestry; never retain a scene wrapper or invoke a
        // Singleton getter that could search for/create a different scene's manager.
        for (int depth = 0; depth < 64 && IsAlive(ancestor); depth++)
        {
            Transform? windowPanel = ancestor!.Find("WindowPanel");
            if (IsAlive(windowPanel))
                return windowPanel!.gameObject.GetComponent(Il2CppType.Of<WindowManager>())?.TryCast<WindowManager>();
            ancestor = ancestor.parent;
        }
        return null;
    }

    private static void LogVisibilityFailure(Exception ex)
    {
        if (_failureLogged) return;
        _failureLogged = true;
        Exception cause = (ex as TargetInvocationException)?.InnerException ?? ex;
        _log?.Invoke($"Node workspace visibility unavailable; tools remain hidden: {cause.GetType().Name}: {cause.Message}");
    }

    private static bool IsVisiblePanel(UIPanel? panel)
    {
        if (!IsActive(panel)) return false;
        float alpha = panel!.alpha;
        return float.IsFinite(alpha) && alpha > .001f;
    }

    private static bool AreAncestorPanelsVisible(Transform? ancestor)
    {
        for (int depth = 0; depth < 64; depth++)
        {
            if (ancestor is null) return true;
            if (!IsAlive(ancestor) || !ancestor.gameObject.activeInHierarchy) return false;
            UIPanel? panel = ancestor.GetComponent<UIPanel>();
            if (panel is not null && !IsVisiblePanel(panel)) return false;
            ancestor = ancestor.parent;
        }
        // A malformed, cyclic or unexpectedly deep hierarchy is not proof that the workspace is visible.
        return ancestor is null;
    }

    private static bool IsActive(Behaviour? component) => IsAlive(component)
        && component!.isActiveAndEnabled && component.gameObject.activeInHierarchy;

    private static bool IsAlive(Il2CppObjectBase? value) => value is not null && !value.WasCollected && value.Pointer != IntPtr.Zero;

    private static T? Read<T>(PropertyInfo? property, object? target)
    {
        if (property is null) throw new MissingMemberException("A required Script node workspace property is unavailable.");
        return (T?)property.GetValue(target);
    }
}
