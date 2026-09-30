using System.Diagnostics.CodeAnalysis;

namespace Rukari.Lib;

/// <summary>Finds the process-wide runtime supplied by the optional Rukari.Lib.Runtime plugin.</summary>
public static class ModServices
{
    private static IModRuntime? _current;

    /// <summary>The attached runtime, or null before attachment or after detachment. Check its state before use.</summary>
    public static IModRuntime? Current => Volatile.Read(ref _current);

    /// <summary>Attempts to obtain the attached runtime; this does not imply it has reached <see cref="RuntimeState.Ready"/>.</summary>
    public static bool TryGetRuntime([NotNullWhen(true)] out IModRuntime? runtime)
    {
        runtime = Current;
        return runtime is not null;
    }

    internal static void Attach(IModRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        IModRuntime? existing = Interlocked.CompareExchange(ref _current, runtime, null);
        if (existing is not null && !ReferenceEquals(existing, runtime))
            throw new InvalidOperationException("A different mod runtime is already attached.");
    }

    internal static void Detach(IModRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        Interlocked.CompareExchange(ref _current, null, runtime);
    }
}
