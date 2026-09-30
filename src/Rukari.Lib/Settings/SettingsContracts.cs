using Rukari.Lib.Tools;

namespace Rukari.Lib.Settings;

/// <summary>Capability identifiers for the global mod settings window.</summary>
public static class SettingsCapabilities
{
    /// <summary>The global settings registration and window service.</summary>
    public const string Settings = "rukari.settings";
}

/// <summary>
/// Registers global settings independently of the current project or editor selection. All operations, including
/// disposal of registration leases, require the game main thread. Content uses the shared drawing contract but is
/// hosted by the settings window; unlike an editor tool page it must work without an open project. Registration
/// does not activate a playback theme, persist values, or transfer ownership of the content object to the library.
/// </summary>
public interface IModSettingsService
{
    /// <summary>Whether the settings host accepted opening and has not subsequently been closed.</summary>
    bool IsOpen { get; }

    /// <summary>
    /// Registers one independent column per owner. Duplicate owners are rejected. Disposing the returned lease
    /// removes exactly this registration and hides it if visible; the content object itself is not disposed.
    /// </summary>
    ModResult<IDisposable> RegisterPage(string ownerId, string title, IToolPanelContent content);

    /// <summary>
    /// Opens the global window, optionally selecting an owner. Null preserves the current selection or selects the
    /// first registered page. Success requires the native host to confirm that it displayed the window.
    /// </summary>
    ModResult<bool> Open(string? ownerId);

    /// <summary>Closes the window and pairs the visible page's lifecycle callback. Safe to repeat.</summary>
    void Close();
}
