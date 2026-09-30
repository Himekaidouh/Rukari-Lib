namespace Rukari.Lib.Tools;

/// <summary>
/// The open/closed state of the tool rail as a tree, owned by the shared library so every mod gets the same
/// behaviour instead of drawing its own panel.
///
/// The rail opens one column at a time, and <b>opening a column never selects anything inside it</b>:
///
/// <list type="number">
/// <item>the rail itself, which is permanent and holds one icon per registered module;</item>
/// <item>the module column, which pops out of the big icon with nothing open — the user's own press on a module
/// is the only thing that can open one;</item>
/// <item>a multi-entry module's entries column, which exists only for a module that publishes several pages;
/// a single-page module has no such level, so its button opens its panel directly;</item>
/// <item>the open entry's panel, which is the leaf the user works in.</item>
/// </list>
///
/// <see cref="Depth"/> reports how many of those are drawn: 0 = the rail, 1 = the module column (with a module
/// open or not), 2 = a panel. Every operation is written so that <b>one press is one visible change</b>: a press
/// that would only move an invisible selection, or close a panel the user cannot see, does not exist. This type
/// is pure managed — no game type, no rendering — so all of that is unit-testable.
/// </summary>
public sealed class ToolTreeState
{
    /// <summary>True while the module column is out of the rail. The big icon is what opens and closes it.</summary>
    public bool ColumnOpen { get; private set; }

    /// <summary>Module whose entries or panel are showing, or null while the column shows nothing open.</summary>
    public string? OpenModuleId { get; private set; }

    /// <summary>Entry whose panel is showing, or null when no panel is showing.</summary>
    public string? OpenEntryId { get; private set; }

    /// <summary>0 = rail only, 1 = the module column is out, 2 = a panel is showing.</summary>
    public int Depth => !ColumnOpen ? 0 : OpenEntryId == null ? 1 : 2;

    /// <summary>True while any column is out of the rail, whether or not something inside it is open.</summary>
    public bool IsExpanded => ColumnOpen;

    /// <summary>
    /// The big icon's press: pops the module column out with <b>nothing</b> open, or closes every level when
    /// something is already out. The icon must never select a module for the user: a column that came up with one
    /// already open both highlights a button the user did not press and turns the next press on that button into a
    /// close instead of an open, which reads as "the button does nothing".
    /// </summary>
    /// <returns>True when anything changed.</returns>
    public bool PressRailIcon()
    {
        if (Depth == 0) return OpenColumn();
        CollapseAll();
        return true;
    }

    /// <summary>Pops the module column out and opens nothing. Returns true when anything changed.</summary>
    public bool OpenColumn()
    {
        if (ColumnOpen) return false;
        ColumnOpen = true;
        OpenModuleId = null;
        OpenEntryId = null;
        return true;
    }

    /// <summary>
    /// Opens one module's level and closes whichever module was open, because two modules can never share the
    /// space beside the rail. Never invents an open entry: that is the caller's press to make.
    /// </summary>
    public bool OpenModule(string moduleId)
    {
        Require(moduleId, nameof(moduleId));
        bool changed = !ColumnOpen || !string.Equals(OpenModuleId, moduleId, StringComparison.Ordinal);
        ColumnOpen = true;
        OpenModuleId = moduleId;
        OpenEntryId = null;
        return changed;
    }

    /// <summary>
    /// Closes the open module and keeps the column out, which is what puts a highlighted button back to rest
    /// without throwing the user out of the tree.
    /// </summary>
    public bool CloseModule()
    {
        if (OpenModuleId == null && OpenEntryId == null) return false;
        OpenModuleId = null;
        OpenEntryId = null;
        return true;
    }

    /// <summary>
    /// Opens one entry of a module, filling in the module level when the caller opened the entry directly.
    /// </summary>
    public bool OpenEntry(string moduleId, string entryId)
    {
        Require(moduleId, nameof(moduleId));
        Require(entryId, nameof(entryId));
        ColumnOpen = true;
        OpenModuleId = moduleId;
        OpenEntryId = entryId;
        return true;
    }

    /// <summary>
    /// Walks back exactly one level, assuming the open module draws an entries column of its own. Returns the depth
    /// that remains, so a caller can tell whether another press is still meaningful.
    /// </summary>
    public int CollapseOneLevel() => CollapseOneLevel(moduleHasEntries: true);

    /// <summary>
    /// The same walk, told whether the open module draws an entries column at all.
    ///
    /// <paramref name="moduleHasEntries"/> false means the module publishes a single entry, so its button opens its
    /// panel directly and there is no entries level between the two: the panel and the module then close together.
    /// Leaving the module open would highlight a button while showing nothing, and the next press — the one that
    /// closes it — would look like the press that did nothing.
    /// </summary>
    /// <returns>The depth that remains.</returns>
    public int CollapseOneLevel(bool moduleHasEntries)
    {
        if (OpenEntryId != null)
        {
            OpenEntryId = null;
            if (!moduleHasEntries) OpenModuleId = null;
            return Depth;
        }

        if (OpenModuleId != null)
        {
            OpenModuleId = null;
            return Depth;
        }

        ColumnOpen = false;
        return 0;
    }

    /// <summary>Closes every level. Used when the editor closes or the drawer is hidden.</summary>
    public void CollapseAll()
    {
        ColumnOpen = false;
        OpenModuleId = null;
        OpenEntryId = null;
    }

    /// <summary>
    /// Drops state that points at something no longer registered, which happens whenever a mod is disabled or
    /// unloaded while its panel is open. Returns true when anything changed, so the caller can redraw.
    ///
    /// This overload does not know which entries the open module still publishes, so it can only reconcile the
    /// module level. Pass the open module's entries to
    /// <see cref="Synchronize(IReadOnlyList{ToolModule}, IReadOnlyList{ToolEntry}?)"/> to also drop a leaf whose
    /// provider has unloaded.
    /// </summary>
    public bool Synchronize(IReadOnlyList<ToolModule> modules) => Synchronize(modules, null);

    /// <summary>
    /// Reconciles both levels. <paramref name="entries"/> must be the entries the open module publishes right
    /// now, or null when the caller cannot read them; an open leaf whose entry is gone falls back one level
    /// rather than leaving a panel drawn for an unloaded provider.
    /// </summary>
    public bool Synchronize(IReadOnlyList<ToolModule> modules, IReadOnlyList<ToolEntry>? entries)
    {
        ArgumentNullException.ThrowIfNull(modules);
        if (OpenModuleId == null)
        {
            return false;
        }

        ToolModule? module = null;
        foreach (ToolModule candidate in modules)
        {
            if (string.Equals(candidate.Id, OpenModuleId, StringComparison.Ordinal))
            {
                module = candidate;
                break;
            }
        }

        if (module == null)
        {
            CollapseAll();
            return true;
        }

        // An entry id is only meaningful within the module that published it, so a module switch cannot leave
        // a stale entry behind. The same applies when the module survives but the entry's provider unloaded.
        if (OpenEntryId == null || entries == null)
        {
            return false;
        }

        foreach (ToolEntry entry in entries)
        {
            if (string.Equals(entry.Id, OpenEntryId, StringComparison.Ordinal))
            {
                return false;
            }
        }

        OpenEntryId = null;
        return true;
    }

    private static void Require(string value, string name)
    {
        // net6.0: ArgumentException.ThrowIfNullOrWhiteSpace only exists from .NET 7.
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A tool id cannot be empty.", name);
        }
    }
}
