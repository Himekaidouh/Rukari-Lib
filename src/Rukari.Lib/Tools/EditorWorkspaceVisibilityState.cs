namespace Rukari.Lib.Tools;

/// <summary>
/// Managed visibility facts for the Script node workspace. A selected dialogue is deliberately not a
/// condition: an open node with no selected row still exposes tools, whose editing actions can be disabled.
/// </summary>
public readonly record struct EditorWorkspaceVisibilityState(
    bool RuntimeReady,
    bool InspectorActive,
    bool ContainerActive,
    bool IsCurrentInspector,
    bool HasScriptNode,
    bool PanelsVisible,
    bool IsTransitioning)
{
    /// <summary>Whether the Script node workspace can expose its tools.</summary>
    public bool IsNodeEditorVisible => RuntimeReady && InspectorActive && ContainerActive
        && IsCurrentInspector && HasScriptNode && PanelsVisible && !IsTransitioning;
}
