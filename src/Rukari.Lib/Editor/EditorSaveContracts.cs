namespace Rukari.Lib.Editor;

/// <summary>Identifiers for the on-demand save contract (2026-09-21).</summary>
public static class EditorSaveCapabilities
{
    /// <summary>Capability ID for <see cref="IEditorSaveService"/> version 0.1.</summary>
    public const string OnDemand = "aavt.editor.save";
}

/// <summary>What one save attempt actually did, step by step.</summary>
/// <param name="ProjectName">The project the editor was holding, for the status line.</param>
/// <param name="Saved">Whether the session wrote the project file (<c>.aap</c>).</param>
/// <param name="Compiled">Whether the editor compiled the scripts (<c>.builds</c>).</param>
/// <param name="Published">
/// Whether the playable archive was written (<c>data\saves\&lt;name&gt;.aas</c>). Only this step makes
/// the change visible to formal playback, which is why the three are reported separately.
/// </param>
/// <param name="ElapsedMilliseconds">How long the whole chain took.</param>
/// <param name="Detail">Diagnostic text: the first step that failed, or what was skipped.</param>
public sealed record EditorSaveReceipt(
    string ProjectName,
    bool Saved,
    bool Compiled,
    bool Published,
    int ElapsedMilliseconds,
    string Detail)
{
    /// <summary>True only when every step succeeded; a partial save is never reported as success.</summary>
    public bool Complete => Saved && Compiled && Published;

    /// <summary>True when there was nothing to write, which is a normal answer, not a failure.</summary>
    public bool NothingToDo => !Saved && !Compiled && !Published;
}

/// <summary>
/// Saving on demand (2026-09-21), the sibling of <see cref="IEditorDocumentService"/>.
/// <para>
/// The official interface only ever compiles; the playable <c>.aas</c> is written by whoever calls
/// the compile overload that takes a file name. This contract exists so the author can ask for the
/// whole chain — save, compile, publish — instead of waiting for the automatic path and restarting
/// the game when it does not come.
/// </para>
/// <para>
/// Implemented by the package that owns the editor integration, not by Rukari lib itself: the lib
/// declares what a save means, the implementation is the one that can reach the editor's session.
/// Main thread only; a clean document answers <see cref="EditorSaveReceipt.NothingToDo"/> instead of
/// writing the same bytes again.
/// </para>
/// </summary>
public interface IEditorSaveService
{
    /// <summary>Saves the open project, compiles it, and publishes the playable archive.</summary>
    /// <returns>What each step did; check <see cref="EditorSaveReceipt.Complete"/> before claiming success.</returns>
    ModResult<EditorSaveReceipt> SaveAndPublish();
}
