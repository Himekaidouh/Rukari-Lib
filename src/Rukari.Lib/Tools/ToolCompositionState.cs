namespace Rukari.Lib.Tools;

/// <summary>
/// Who wants the input method's composition window, and where the caret is.
///
/// <para>
/// Composition is one global switch in the engine: whoever writes it last wins. Two surfaces that each wrote it for
/// themselves therefore fought — the drawer turning it off because its own search box lost focus could switch it off
/// under a text field the user had just clicked in a tool window, and the loser depended on which surface happened
/// to run later in the frame. This tracks the wishes instead, so the answer is "does ANY owner want it", which does
/// not depend on the order at all.
/// </para>
///
/// <para>
/// It is pure on purpose: the arbitration rule is the part that was wrong, and a rule about ordering is exactly
/// what a test can check without a game.
/// </para>
/// </summary>
public sealed class ToolCompositionState
{
    private readonly HashSet<string> _owners = new(StringComparer.Ordinal);
    private ToolInputRect _cursor;

    /// <summary>Whether any owner wants composition right now.</summary>
    public bool Wanted => _owners.Count != 0;

    /// <summary>
    /// Where the engine should put its candidate window: the caret the last owner to ask for composition reported.
    /// Invalid until an owner reports one, and invalid rectangles are ignored rather than used.
    /// </summary>
    public ToolInputRect Cursor => _cursor;

    /// <summary>Owners that want composition; a diagnostic aid and a bound on the set.</summary>
    public int OwnerCount => _owners.Count;

    /// <summary>
    /// Records one owner's wish and its caret, and reports whether the answer changed. An owner that wants
    /// composition keeps wanting it until it says otherwise, so a surface that stops drawing (or is hidden) cannot
    /// leave the engine composing on its behalf: it must release, which is what its own lifecycle already does.
    /// </summary>
    public bool Set(string ownerId, bool on, ToolInputRect cursor = default)
    {
        if (string.IsNullOrWhiteSpace(ownerId)) return false;
        bool before = Wanted;
        if (on)
        {
            if (_owners.Count >= 32 && !_owners.Contains(ownerId)) return false;
            _owners.Add(ownerId);
            if (cursor.IsValid) _cursor = cursor;
        }
        else
        {
            _owners.Remove(ownerId);
            if (_owners.Count == 0) _cursor = default;
        }
        return before != Wanted;
    }

    /// <summary>Drops every owner, for input being torn down. The caret goes with them.</summary>
    public void Clear()
    {
        _owners.Clear();
        _cursor = default;
    }
}
