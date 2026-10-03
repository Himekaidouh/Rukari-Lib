namespace Rukari.Lib.Editor;

/// <summary>Capability identifiers for managed manual-publication scope providers.</summary>
public static class EditorPublicationScopeCapabilities
{
    /// <summary>Capability ID for <see cref="IEditorPublicationScopeService"/> version 0.1.</summary>
    public const string Manual = "rukari.editor.publication-scope";
}

/// <summary>
/// Identifies one explicit save, compile, and publication chain to an optional resource companion provider.
/// This sibling contract does not change <see cref="IEditorSaveService"/> or authorize any extra native call.
/// </summary>
public interface IEditorPublicationScopeService
{
    /// <summary>
    /// Validates the current editor project against <paramref name="expectedProjectName"/> and opens a scope.
    /// The caller must keep the returned ticket alive for its whole synchronous manual publication chain,
    /// disposing it on the same main thread, in reverse order, including when saving or compiling fails.
    /// </summary>
    /// <param name="expectedProjectName">The exact project name observed by the manual save provider.</param>
    /// <returns>
    /// A managed lifetime ticket on success, or an explicit failure when the runtime, main thread, session,
    /// or current resource root cannot be verified. Failure must not be treated as an empty successful scope.
    /// Beginning a scope does not save, compile, publish, copy, or play any resource.
    /// </returns>
    ModResult<IDisposable> Begin(string expectedProjectName);
}
