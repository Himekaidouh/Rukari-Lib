namespace Rukari.Lib.Commands;

/// <summary>Capability published when the runtime can separate registered directives from official text.</summary>
public static class EmbeddedDirectiveCapabilities
{
    /// <summary>Shared compile-boundary extraction and dispatch, without replacing the official parser.</summary>
    public const string Compilation = "rukari.commands.compilation";
}

/// <summary>The adapter boundary that produced a script string. Nested boundaries may describe the same compilation.</summary>
public enum DirectiveCompilationBoundary
{
    /// <summary>An editor script-text wrapper.</summary>
    ScriptText,
    /// <summary>A standalone script compilation.</summary>
    Standalone,
    /// <summary>A continuous script compilation.</summary>
    Continuous,
}

/// <summary>A whole registered directive line, without its line terminator. Line numbers are one-based.</summary>
/// <param name="Text">The exact original line, including leading and trailing whitespace.</param>
/// <param name="LineNumber">One-based line number in the source string.</param>
/// <param name="OwnerId">The registration that owns the most specific matching route.</param>
/// <param name="Route">The canonical, lowercase semicolon-token route that matched.</param>
public sealed record RemovedDirective(string Text, int LineNumber, string OwnerId, string Route);

/// <summary>
/// An immutable compilation snapshot. All registered lines are removed before any callback runs.
/// A callback also receives empty OwnedDirectives, so it can observe deletion of its last instruction.
/// </summary>
public sealed class DirectiveCompilation
{
    internal DirectiveCompilation(string sourceText, string officialText,
        IReadOnlyList<RemovedDirective> removedDirectives, IReadOnlyList<RemovedDirective> ownedDirectives,
        DirectiveCompilationBoundary boundary, long compilationId, bool isAuthoritative)
        => (SourceText, OfficialText, RemovedDirectives, OwnedDirectives, Boundary, CompilationId, IsAuthoritative)
            = (sourceText, officialText, removedDirectives, ownedDirectives, boundary, compilationId, isAuthoritative);

    /// <summary>The unmodified input at this boundary, not necessarily the original editor document.</summary>
    public string SourceText { get; }
    /// <summary>The final text for the official parser, shared by every callback in this dispatch.</summary>
    public string OfficialText { get; }
    /// <summary>All owned lines removed from this input, in source order.</summary>
    public IReadOnlyList<RemovedDirective> RemovedDirectives { get; }
    /// <summary>Only the lines assigned to this callback's registration, in source order.</summary>
    public IReadOnlyList<RemovedDirective> OwnedDirectives { get; }
    /// <summary>The runtime boundary that produced this input.</summary>
    public DirectiveCompilationBoundary Boundary { get; }
    /// <summary>One correlation ID for an outer compilation and its nested boundaries; not a saved scene ID.</summary>
    public long CompilationId { get; }
    /// <summary>Whether this is the outer authoritative result for its boundary family.</summary>
    public bool IsAuthoritative { get; }
}

/// <summary>
/// A managed, immutable route snapshot. It contains no plugin callbacks and is safe for worker-thread projection.
/// Disposing a registration later does not change this snapshot; obtain a new snapshot for subsequent work.
/// </summary>
public interface IEmbeddedDirectiveSanitizer
{
    /// <summary>The registry revision captured with these routes. Include it in projected-text cache keys.</summary>
    long Revision { get; }
    /// <summary>Removes only registered whole lines; preserves every other character and line terminator.</summary>
    string Sanitize(string text);

    /// <summary>
    /// Removes lines owned by other registered mods while retaining the supplied owners and unregistered text.
    /// Ownership is resolved using ALL routes before filtering: retaining a parent never captures another
    /// owner's child route. Owner IDs are ordinal, case-sensitive registration IDs. No callbacks run.
    /// </summary>
    string SanitizeExceptOwners(string text, IReadOnlyList<string> ownerIds);
}

/// <summary>
/// Shared routing for custom whole-line directives. This service does not execute instructions, save projects,
/// rewrite arbitrary text, or suppress errors in unregistered namespaces.
/// </summary>
public interface IEmbeddedDirectiveService
{
    /// <summary>The current route-registry revision; successful registration, removal and shutdown change it.</summary>
    long Revision { get; }

    /// <summary>
    /// Atomically claims semicolon-token routes on the main thread. The most specific route owns a line;
    /// an identical route conflicts, while parent and child routes may coexist. Use a namespace owned by your mod.
    /// Callbacks run synchronously on the compiling thread, may receive zero owned lines, and must avoid native
    /// calls off the main thread and recursive compile cycles. Retain and dispose the lease to unregister.
    /// </summary>
    ModResult<IDisposable> Register(string ownerId, IReadOnlyList<string> routes, Action<DirectiveCompilation> callback);

    /// <summary>Sanitizes with one fresh snapshot. Safe on worker threads; never invokes callbacks.</summary>
    string Sanitize(string text);

    /// <summary>Captures routes and their revision together for a consistent multi-item worker operation.</summary>
    IEmbeddedDirectiveSanitizer CaptureSanitizer();
}
