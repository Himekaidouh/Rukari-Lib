namespace Rukari.Lib.Tools;

/// <summary>
/// One change of the page that owns the visible panel: the page that stopped showing, if any, and the page that
/// started, if any. A transition always names at least one of the two, and never the same page twice.
/// </summary>
public readonly record struct ToolPanelTransition(IToolPanelContent? Hidden, IToolPanelContent? Shown);

/// <summary>
/// Tracks which hosted page owns the visible panel, so a page's show and hide callbacks fire exactly once per
/// transition instead of once per frame — or once per code path that happens to hide something, of which there are
/// several: collapsing a level, switching to another page, leaving the node editor, and throwing the toolbox away.
///
/// <para>
/// The renderer asks once per frame with the page that should be showing now — null while the panel is closed —
/// and performs whatever the answer names. Keeping the decision here rather than inside the renderer makes
/// "paired, never overlapping" a rule a unit test can check without a game, and makes a path that forgets to hide,
/// or hides twice, impossible to write.
/// </para>
/// </summary>
public sealed class ToolPanelSession
{
    /// <summary>The page whose panel is showing, or null while no hosted page is on screen.</summary>
    public IToolPanelContent? Current { get; private set; }

    /// <summary>
    /// Advances to the page that should be showing now. Returns the transition when the page changed — including
    /// the transitions into and out of "nothing is showing" — and null when nothing changed, so a caller only ever
    /// runs the callbacks on the frame they are due.
    /// </summary>
    public ToolPanelTransition? Advance(IToolPanelContent? next)
    {
        if (ReferenceEquals(Current, next)) return null;
        var transition = new ToolPanelTransition(Current, next);
        Current = next;
        return transition;
    }

    /// <summary>Stops showing the current page, naming it once, for a renderer that is being torn down.</summary>
    public ToolPanelTransition? Reset() => Advance(null);
}
