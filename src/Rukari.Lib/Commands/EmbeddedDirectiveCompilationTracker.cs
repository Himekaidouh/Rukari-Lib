namespace Rukari.Lib.Commands;

// One tracker per compiler thread. Separate wrapper/continuous nesting preserves legacy authority semantics.
internal sealed class DirectiveCompilationTracker
{
    private static long _nextId;
    private readonly Stack<DirectiveCompilationBoundary> _boundaries = new();
    private int _wrapperDepth;
    private int _continuousDepth;

    public long CompilationId { get; private set; }

    public long Begin(DirectiveCompilationBoundary boundary)
    {
        RequireBoundary(boundary);
        if (_boundaries.Count == 0) CompilationId = Interlocked.Increment(ref _nextId);
        _boundaries.Push(boundary);
        if (boundary == DirectiveCompilationBoundary.Continuous) _continuousDepth++;
        else _wrapperDepth++;
        return CompilationId;
    }

    public bool IsAuthoritative(DirectiveCompilationBoundary boundary)
    {
        RequireBoundary(boundary);
        return boundary == DirectiveCompilationBoundary.Continuous ? _continuousDepth == 1 : _wrapperDepth == 1;
    }

    public void End(DirectiveCompilationBoundary boundary)
    {
        RequireBoundary(boundary);
        if (_boundaries.Count == 0 || _boundaries.Peek() != boundary)
            throw new InvalidOperationException("Compilation boundaries must end in reverse begin order.");
        _boundaries.Pop();
        if (boundary == DirectiveCompilationBoundary.Continuous) _continuousDepth--;
        else _wrapperDepth--;
        if (_boundaries.Count == 0) CompilationId = 0;
    }

    private static void RequireBoundary(DirectiveCompilationBoundary boundary)
    {
        if (!Enum.IsDefined(typeof(DirectiveCompilationBoundary), boundary)) throw new ArgumentOutOfRangeException(nameof(boundary));
    }
}
