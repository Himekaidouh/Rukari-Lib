namespace Rukari.Lib.Tools;

/// <summary>
/// Standalone tool windows: the same shared frame, theme and input ownership as the drawer's hosted pages, drawn
/// in a surface of the mod's own that the user can move, resize and keep beside the workspace.
///
/// <para>
/// This is a SIBLING of <see cref="IToolboxService"/> rather than a member of it. The drawer's contract is frozen:
/// adding a method to it would break every provider compiled against the old interface, and a page that failed to
/// implement one would die with a <c>TypeLoadException</c> at load. A mod asks for this interface and falls back to
/// a drawer page when the installed library does not offer it.
/// </para>
///
/// <para>
/// The library still owns everything native: the canvas, the frame, the title band, the close button, the resize
/// grip, the theme and every input region. A window page fills its content rectangle through
/// <see cref="IToolPanelSurface"/> exactly as a drawer page does, and must never create its own canvas or game
/// objects. All members require the game main thread.
/// </para>
/// </summary>
public interface IToolWindowService
{
    /// <summary>
    /// Registers one window-backed page. It appears in its module's entries column beside the drawer pages — the
    /// rail is the way in, exactly as it is for a drawer page — but choosing it opens a window instead of the
    /// modal drawer sheet, so it can stay on screen while the drawer shows something else. Disposing the lease
    /// closes the window if it is open and removes the entry.
    ///
    /// <para>
    /// <paramref name="windowId"/> namespaces the window's remembered position, and
    /// <paramref name="iconId"/> names a built-in glyph ("wave", "camera", "card", "magnify", "gear"), both like
    /// their <see cref="IToolboxService.RegisterHostedPage"/> counterparts. There is no optional parameter: an
    /// optional parameter would replace this method's signature and break already-compiled providers.
    /// </para>
    /// </summary>
    ModResult<IDisposable> RegisterWindow(string ownerId, string windowId, string title,
        IToolPanelContent content, string? iconId);

    /// <summary>Opens a registered window, or does nothing when it is already open.</summary>
    ModResult<bool> Show(string windowId);

    /// <summary>Closes a registered window. Safe to repeat, and never touches the provider's own draft.</summary>
    void Hide(string windowId);

    /// <summary>
    /// Whether that window is currently drawn. Named rather than overloaded as <c>IsOpen</c>: a type cannot carry a
    /// property and a method of the same name, and the toolbox's own <see cref="IToolboxService.IsOpen"/> is frozen.
    /// </summary>
    bool IsWindowOpen(string windowId);

    /// <summary>
    /// Opens the window when it is closed and closes it when it is open, which is what a rail entry does.
    /// </summary>
    ModResult<bool> Toggle(string windowId);

    /// <summary>
    /// Forgets where the user put this window, so it opens docked at its default place again. Returns whether
    /// there was a position to forget. The window's own title band also resets on a double click.
    /// </summary>
    bool ResetPlacement(string windowId);
}
