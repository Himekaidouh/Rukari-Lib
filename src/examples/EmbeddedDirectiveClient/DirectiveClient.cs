using Rukari.Lib;
using Rukari.Lib.Commands;

namespace Example.Rukari.EmbeddedDirectives;

/// <summary>
/// Collects compile notifications only. It does not implement a playback command, write a project,
/// or parse a saved archive. The callback performs managed work and is safe on a compiler worker.
/// </summary>
public sealed class DirectiveClient : IDisposable
{
    public const string OwnerId = "example.rukari.embedded-directives";
    public const string Route = "#example.echo";

    private readonly IModRuntime _runtime;
    private readonly object _gate = new();
    private IDisposable? _registration;
    private bool _disposed;
    private long _notifications;
    private long _authoritativeNotifications;
    private long _ownedLinesObserved;
    private long _lastCompilationId;
    private int _lastOwnedLineCount;
    private DirectiveCompilationBoundary _lastBoundary;

    private DirectiveClient(IModRuntime runtime) => _runtime = runtime;

    public static Task<ModResult<DirectiveClient>> RegisterAsync(CancellationToken cancellation = default)
    {
        IModRuntime? runtime = ModServices.Current;
        if (runtime is null || runtime.State != RuntimeState.Ready)
            return Task.FromResult(ModResult<DirectiveClient>.Fail(ModErrorCode.NotReady, "Rukari lib runtime is not ready."));

        ModResult<CapabilityInfo> capability = runtime.GetCapability(EmbeddedDirectiveCapabilities.Compilation);
        if (!capability.Success)
            return Task.FromResult(ModResult<DirectiveClient>.Fail(capability.Error!));
        if (capability.Value.Level == CapabilityLevel.Unavailable)
            return Task.FromResult(ModResult<DirectiveClient>.Fail(ModErrorCode.Unsupported, capability.Value.Detail));

        // Registration and lease disposal belong on the main thread. Never synchronously wait
        // for InvokeAsync on that thread; the caller awaits or polls the returned task instead.
        return runtime.InvokeAsync(() =>
        {
            ModResult<IEmbeddedDirectiveService> service = runtime.GetService<IEmbeddedDirectiveService>();
            if (!service.Success) return ModResult<DirectiveClient>.Fail(service.Error!);

            var client = new DirectiveClient(runtime);
            ModResult<IDisposable> registration = service.Value.Register(OwnerId, new[] { Route }, client.OnCompilation);
            if (!registration.Success) return ModResult<DirectiveClient>.Fail(registration.Error!);
            client._registration = registration.Value;
            return ModResult<DirectiveClient>.Ok(client);
        }, cancellation);
    }

    private void OnCompilation(DirectiveCompilation compilation)
    {
        // This is NOT a playback callback. It can run on a background thread, repeat for the
        // same document, and report zero owned lines. Never touch Unity, play audio, compile
        // recursively, or assume that CompilationId identifies a persisted scene here.
        lock (_gate)
        {
            if (_disposed) return;
            _notifications++;
            if (!compilation.IsAuthoritative) return;
            _authoritativeNotifications++;
            _ownedLinesObserved += compilation.OwnedDirectives.Count;
            _lastCompilationId = compilation.CompilationId;
            _lastOwnedLineCount = compilation.OwnedDirectives.Count;
            _lastBoundary = compilation.Boundary;
        }
    }

    /// <summary>Returns managed statistics; counters describe notifications, never game effects.</summary>
    public CompilationStatistics Snapshot()
    {
        lock (_gate)
            return new CompilationStatistics(_notifications, _authoritativeNotifications, _ownedLinesObserved,
                _lastCompilationId, _lastOwnedLineCount, _lastBoundary);
    }

    /// <summary>Call on the game main thread when the owning plugin stops.</summary>
    public void Dispose()
    {
        if (!_runtime.IsMainThread)
            throw new InvalidOperationException("Dispose the directive registration on the game main thread.");
        lock (_gate) _disposed = true;
        Interlocked.Exchange(ref _registration, null)?.Dispose();
    }
}

public sealed record CompilationStatistics(long Notifications, long AuthoritativeNotifications,
    long OwnedLinesObserved, long LastCompilationId, int LastOwnedLineCount, DirectiveCompilationBoundary LastBoundary);
