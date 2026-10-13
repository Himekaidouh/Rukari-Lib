namespace Rukari.Lib.Tools;

/// <summary>
/// Which standalone window is on top, and which one owns a press.
///
/// <para>
/// The shared input guard is a UNION of rectangles with no notion of stacking order: with two windows overlapping,
/// a point inside both would be delivered to both, and one press would act twice — a drag in each window, or a
/// button in each. The hosts, not the guard, have to agree on who is on top, and this is that agreement. It is pure
/// arithmetic so the rule can be tested without a game, exactly like the window geometry beside it.
/// </para>
///
/// <para>
/// The order is bottom to top, matching what a renderer needs: the topmost window draws last and answers first.
/// A window that is shown arrives on top — the user just asked for it — and a press on any part of a window raises
/// it, which is the gesture every windowing system has.
/// </para>
/// </summary>
public sealed class ToolWindowStack
{
    private readonly List<string> _order = new();
    private readonly Dictionary<string, ToolWindowPlacement> _placements = new(StringComparer.Ordinal);

    /// <summary>How many windows are on screen right now.</summary>
    public int Count => _order.Count;

    /// <summary>Bottom-to-top window ids, which is also the order a renderer must draw them in.</summary>
    public IReadOnlyList<string> Order => _order;

    /// <summary>
    /// Registers a window that has just appeared, at the top of the stack. Shown again, it only moves its
    /// rectangle: a window that was already on screen must not jump over the others for a frame merely because
    /// its renderer ran.
    /// </summary>
    public void Show(string windowId, ToolWindowPlacement placement)
    {
        if (string.IsNullOrWhiteSpace(windowId)) return;
        _placements[windowId] = placement;
        if (!_order.Contains(windowId)) _order.Add(windowId);
    }

    /// <summary>Moves or resizes a window that is already on screen, keeping its place in the stack.</summary>
    public void Update(string windowId, ToolWindowPlacement placement)
    {
        if (string.IsNullOrWhiteSpace(windowId) || !_placements.ContainsKey(windowId)) return;
        _placements[windowId] = placement;
    }

    /// <summary>Removes a hidden window. A later Show puts it back on top, as a fresh window would be.</summary>
    public void Hide(string windowId)
    {
        if (string.IsNullOrWhiteSpace(windowId)) return;
        _placements.Remove(windowId);
        _order.Remove(windowId);
    }

    /// <summary>Brings a window to the front. A press anywhere on it asks for this; unknown ids do nothing.</summary>
    public void Raise(string windowId)
    {
        if (string.IsNullOrWhiteSpace(windowId)) return;
        if (!_placements.ContainsKey(windowId)) return;
        _order.Remove(windowId);
        _order.Add(windowId);
    }

    /// <summary>
    /// The window a press at this point belongs to: the topmost one whose rectangle contains it, or null when the
    /// point is over none of them. A host that is not the answer must ignore the press entirely, because the guard
    /// cannot tell it that something else is in front of it.
    /// </summary>
    public string? OwnerAt(float x, float y)
    {
        for (int i = _order.Count - 1; i >= 0; i--)
        {
            if (_placements.TryGetValue(_order[i], out ToolWindowPlacement placement)
                && ToolWindowLayout.Contains(placement, x, y)) return _order[i];
        }
        return null;
    }

    /// <summary>
    /// Where a window sits in the stack: 0 is the bottom, and -1 means it is not on screen. A renderer turns this
    /// into its draw order, so the topmost window is drawn above the others rather than beside them.
    /// </summary>
    public int Depth(string windowId) =>
        string.IsNullOrWhiteSpace(windowId) ? -1 : _order.IndexOf(windowId);

    /// <summary>Whether a window is on screen and therefore participating in the order.</summary>
    public bool IsShown(string windowId) => Depth(windowId) >= 0;

    /// <summary>Forgets every window; used when the library is torn down.</summary>
    public void Clear()
    {
        _order.Clear();
        _placements.Clear();
    }
}
