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
    /// <summary>Whether the Script node workspace is still visible underneath any official window.</summary>
    public bool IsNodeEditorVisible => RuntimeReady && InspectorActive && ContainerActive
        && IsCurrentInspector && HasScriptNode && PanelsVisible && !IsTransitioning;
}

/// <summary>
/// Tool visibility is separate from workspace lifetime: opening an official selector must hide tools
/// without making document providers treat their live Script node as closed.
/// </summary>
public readonly record struct EditorToolVisibilityState(
    bool NodeEditorVisible,
    bool WindowStateAvailable,
    bool BlockingActive,
    bool BlockingPanelsVisible)
{
    /// <summary>Whether tools can draw and expand in the unobstructed Script workspace.</summary>
    public bool IsToolWorkspaceVisible => NodeEditorVisible && WindowStateAvailable
        && !(BlockingActive && BlockingPanelsVisible);
}
