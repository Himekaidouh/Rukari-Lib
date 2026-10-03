namespace Rukari.Lib.Tools;

/// <summary>A managed action from a tool button or resource row. Values are opaque to the toolbox.</summary>
public sealed record ToolAction(string Id, string? Value = null);

/// <summary>A page action. The provider owns validation and the operation behind this button.</summary>
public sealed record ToolButton(string Id, string Label, bool Enabled = true, bool Highlighted = false, string? Value = null)
{
    /// <summary>
    /// Named face to draw this button with, or null for the toolbox's shared plate. Deliberately an init-only
    /// property rather than a sixth constructor parameter: a provider compiled against the five-argument
    /// constructor keeps working, which is the same reason RegisterPage grew an overload instead of an optional
    /// parameter. See <see cref="ToolButtonSkins"/>.
    /// </summary>
    public string? Skin { get; init; }
}

/// <summary>Named button faces a page may ask for. A name the drawer does not know falls back to the shared plate.</summary>
public static class ToolButtonSkins
{
    /// <summary>
    /// The game's own button: the pale diagonal-cut plate its everyday controls wear, with dark navy text. Mesh is
    /// deliberately not part of it — the official sheet keeps the facet band for large controls and its own panel
    /// edges. Requires the decoded atlas export; on a machine without it the button silently keeps the shared plate,
    /// so asking for this face is never a hard dependency.
    /// </summary>
    public const string Official = "official.button";

    /// <summary>
    /// The yellow line the official sheet draws under a panel's title. It is a plain atlas sprite tinted by the
    /// drawer, so a panel that cannot reach the export simply draws no line.
    /// </summary>
    public const string OfficialTitleUnderline = "Common:Common_Line_Select_Y";
}

/// <summary>A selectable managed row. IDs must be unique within the page and are never treated as paths.</summary>
public sealed record ToolListItem(string Id, string Label, bool Selected = false, bool Enabled = true);

/// <summary>How a page's search field navigates its resource list.</summary>
public enum ToolListSearchMode
{
    /// <summary>Show only matching rows.</summary>
    Filter,
    /// <summary>Keep every row and jump to the first matching row's page. This never selects or applies a row.</summary>
    Locate
}

/// <summary>
/// Opts into session-local list navigation memory. ContextId identifies the project and list source,
/// never a selected dialogue or native object. Changing it isolates the saved page and search text.
/// </summary>
public sealed record ToolListNavigationOptions(string ContextId, ToolListSearchMode SearchMode);

/// <summary>
/// Immutable page state. The host paginates and optionally filters Items; selecting a row sends
/// ItemActionId with its ID as Value. Providers must return managed data only, and must not modify lists after return.
/// </summary>
public sealed record ToolPageSnapshot(
    string Summary,
    IReadOnlyList<ToolButton> Buttons,
    IReadOnlyList<ToolListItem>? Items = null,
    string Status = "",
    string ItemActionId = "select",
    bool AllowSearch = true)
{
    /// <summary>
    /// Optional navigation memory and search policy. An init-only property preserves the existing
    /// six-argument constructor for already compiled providers. Null keeps the original filtering behavior.
    /// </summary>
    public ToolListNavigationOptions? ListNavigation { get; init; }
}

/// <summary>A shared toolbox. All operations and provider callbacks run on the game main thread.</summary>
public interface IToolboxService
{
    /// <summary>
    /// Whether the shared drawer is open. Changes immediately with Open/Close and is false when no live page
    /// or renderer is available. Other tool windows can use this managed state to yield input ownership.
    /// </summary>
    bool IsOpen { get; }

    /// <summary>
    /// Registers one namespaced page without replacing an existing page. Dispose the lease to remove exactly
    /// this page. Providers own their state, edits, undo, and callback lifetime; no native objects cross this contract.
    /// Pages sharing one ownerId share one permanent rail button, and the first page supplies that button's title
    /// and icon.
    /// </summary>
    ModResult<IDisposable> RegisterPage(string ownerId, string pageId, string title,
        Func<ToolPageSnapshot> snapshot, Action<ToolAction> action);

    /// <summary>
    /// Same as the five-argument overload, with an explicit rail icon. <paramref name="iconId"/> names a built-in
    /// glyph ("wave", "camera", "card", "magnify", "gear"); unknown or empty values get a stable default emblem,
    /// so no artwork has to be shipped. This is a separate overload rather than an optional parameter: an optional
    /// parameter would replace the five-argument method signature and break every provider compiled against it.
    /// </summary>
    ModResult<IDisposable> RegisterPage(string ownerId, string pageId, string title,
        Func<ToolPageSnapshot> snapshot, Action<ToolAction> action, string? iconId);

    /// <summary>
    /// Registers a page whose content the mod draws through the shared panel, for interfaces that a snapshot of
    /// buttons and rows cannot express. The library still owns the canvas, the frame, the title, the close
    /// button, the theme and all input ownership; the mod only asks for controls inside the content rectangle,
    /// and must not create its own canvas or game objects.
    /// </summary>
    ModResult<IDisposable> RegisterHostedPage(string ownerId, string pageId, string title,
        IToolPanelContent content, string? iconId);

    /// <summary>Opens the toolbox, optionally selecting a registered page. A missing page returns NotFound.</summary>
    ModResult<bool> Open(string? pageId = null);

    /// <summary>Closes the shared drawer without changing any provider's draft.</summary>
    void Close();

    /// <summary>Requests a fresh snapshot, optionally for one page. No business operation is performed.</summary>
    void Refresh(string? pageId = null);
}

/// <summary>A rectangle in physical screen pixels, with its origin at the bottom left.</summary>
public readonly record struct ToolInputRect(float X, float Y, float Width, float Height)
{
    /// <summary>Whether the rectangle is finite and has positive dimensions.</summary>
    public bool IsValid => float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Width)
        && float.IsFinite(Height) && Width > 0 && Height > 0;

    /// <summary>Tests a physical screen point; invalid rectangles never capture input.</summary>
    public bool Contains(float x, float y) => IsValid && float.IsFinite(x) && float.IsFinite(y)
        && x >= X && x <= X + Width && y >= Y && y <= Y + Height;
}

/// <summary>A leased input region. Dispose it on unload; publishing an empty list releases its rectangles.</summary>
public interface IToolInputRegion : IDisposable
{
    /// <summary>
    /// Replaces the region snapshot. captureAllPointer is for an active drag and must be reset when it ends.
    /// Calls require the main thread; the service copies the rectangles and retains no game objects.
    /// </summary>
    void Update(IReadOnlyList<ToolInputRect> rectangles, bool captureAllPointer = false);
}

/// <summary>Coordinates the one native mouse guard used by all shared tools.</summary>
public interface IToolInputService
{
    /// <summary>Registers an owner/region pair. A duplicate returns Conflict; dispose the lease to release it.</summary>
    ModResult<IToolInputRegion> RegisterRegion(string ownerId, string regionId);

    /// <summary>Whether any active leased region owns a physical screen point. Main-thread only.</summary>
    bool CapturesPointer(float x, float y);
}

/// <summary>Names of capabilities published by the shared toolbox runtime.</summary>
public static class ToolCapabilities
{
    /// <summary>Managed page registration with the bundled shared drawer renderer.</summary>
    public const string Toolbox = "rukari.tools.toolbox";
    /// <summary>Shared ownership of native mouse input regions.</summary>
    public const string Input = "rukari.tools.input";
}
