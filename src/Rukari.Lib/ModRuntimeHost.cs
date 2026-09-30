namespace Rukari.Lib;

// The plugin owns Unity lifecycle integration. This implementation remains pure managed code so
// dispatch, cancellation and registration rules can be validated without loading a game assembly.
internal sealed class ModRuntimeHost : IModRuntime, IDisposable
{
    private readonly object _gate = new();
    private readonly int _mainThreadId = Environment.CurrentManagedThreadId;
    private readonly int _queueCapacity;
    private readonly LinkedList<QueuedOperation> _queue = new();
    private readonly Dictionary<Type, ServiceRegistration> _services = new();
    private readonly Dictionary<string, ServiceRegistration> _capabilities = new(StringComparer.Ordinal);
    private RuntimeState _state = RuntimeState.Starting;

    public ModRuntimeHost(int queueCapacity = 128)
    {
        if (queueCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(queueCapacity));
        _queueCapacity = queueCapacity;
    }

    public RuntimeState State
    {
        get { lock (_gate) return _state; }
    }

    public bool IsMainThread => Environment.CurrentManagedThreadId == _mainThreadId;

    public void Start()
    {
        RequireMainThread();
        lock (_gate)
        {
            if (_state == RuntimeState.Stopped)
                throw new ObjectDisposedException(nameof(ModRuntimeHost));
            _state = RuntimeState.Ready;
        }
    }

    public ModResult<CapabilityInfo> GetCapability(string id)
    {
        lock (_gate)
        {
            if (_state != RuntimeState.Ready)
                return NotReady<CapabilityInfo>();
            if (string.IsNullOrWhiteSpace(id))
                return ModResult<CapabilityInfo>.Fail(ModErrorCode.InvalidArgument, "A capability ID is required.");
            return _capabilities.TryGetValue(id, out ServiceRegistration? registration)
                ? ModResult<CapabilityInfo>.Ok(registration.Capability)
                : ModResult<CapabilityInfo>.Fail(ModErrorCode.NotFound, "The requested capability is not registered.");
        }
    }

    public ModResult<T> GetService<T>() where T : class
    {
        lock (_gate)
        {
            if (_state != RuntimeState.Ready)
                return NotReady<T>();
            return _services.TryGetValue(typeof(T), out ServiceRegistration? registration)
                ? ModResult<T>.Ok((T)registration.Service!)
                : ModResult<T>.Fail(ModErrorCode.NotFound, "The requested service contract is not registered.");
        }
    }

    public ModResult<IDisposable> RegisterService<T>(string ownerId, T service, CapabilityInfo capability) where T : class
    {
        if (!IsMainThread)
            return ModResult<IDisposable>.Fail(ModErrorCode.WrongThread, "Service registration requires the main thread.");

        lock (_gate)
        {
            if (_state != RuntimeState.Ready)
                return NotReady<IDisposable>();
            if (string.IsNullOrWhiteSpace(ownerId) || service is null || capability is null ||
                string.IsNullOrWhiteSpace(capability.Id) || string.IsNullOrWhiteSpace(capability.ProviderId) ||
                string.IsNullOrWhiteSpace(capability.Version) || capability.Detail is null ||
                !Enum.IsDefined(typeof(CapabilityLevel), capability.Level) ||
                !StringComparer.Ordinal.Equals(ownerId, capability.ProviderId))
            {
                return ModResult<IDisposable>.Fail(ModErrorCode.InvalidArgument,
                    "A service, valid capability metadata, and matching owner/provider IDs are required.");
            }
            if (_services.ContainsKey(typeof(T)) || _capabilities.ContainsKey(capability.Id))
            {
                return ModResult<IDisposable>.Fail(ModErrorCode.Conflict,
                    "This service contract or capability ID is already registered.");
            }

            var registration = new ServiceRegistration(this, typeof(T), service, capability);
            _services.Add(typeof(T), registration);
            _capabilities.Add(capability.Id, registration);
            return ModResult<IDisposable>.Ok(registration);
        }
    }

    public Task<ModResult<T>> InvokeAsync<T>(Func<ModResult<T>> operation, CancellationToken cancellationToken = default)
    {
        if (operation is null)
            return Task.FromResult(ModResult<T>.Fail(ModErrorCode.InvalidArgument, "An operation is required."));

        lock (_gate)
        {
            if (_state != RuntimeState.Ready)
                return Task.FromResult(NotReady<T>());
            if (cancellationToken.IsCancellationRequested)
                return Task.FromResult(Cancelled<T>());

            if (!IsMainThread)
            {
                if (_queue.Count >= _queueCapacity)
                    return Task.FromResult(ModResult<T>.Fail(ModErrorCode.Busy, "The main-thread operation queue is full."));

                var pending = new QueuedOperation<T>(this, operation);
                pending.Node = _queue.AddLast(pending);
                // Registration occurs while holding the queue lock. A synchronous cancellation
                // callback can re-enter this lock; concurrent callbacks wait until initialization ends.
                pending.RegisterCancellationUnderLock(cancellationToken);
                return pending.Task;
            }
            // Passing this point marks immediate work as started. Cancellation or shutdown after
            // this decision cannot claim to have undone the synchronous transaction below.
        }

        return Task.FromResult(SafeInvoke(operation));
    }

    public void Pump(int maxOperations = 32)
    {
        RequireMainThread();
        if (maxOperations < 1)
            throw new ArgumentOutOfRangeException(nameof(maxOperations));

        for (int executed = 0; executed < maxOperations; executed++)
        {
            QueuedOperation next;
            lock (_gate)
            {
                if (_state != RuntimeState.Ready || _queue.First is null)
                    return;
                next = _queue.First.Value;
                _queue.RemoveFirst();
                next.Node = null;
                next.StartUnderLock();
            }
            // Never run provider code while holding the registry/queue lock. A provider can query
            // services, register a service, dispatch nested work, or stop the host without deadlocking.
            next.Execute();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_state == RuntimeState.Stopped)
                return;
            _state = RuntimeState.Stopped;
            while (_queue.First is not null)
            {
                QueuedOperation pending = _queue.First.Value;
                _queue.RemoveFirst();
                pending.Node = null;
                pending.CancelUnderLock();
            }
            // Service lifetime belongs to the provider. Removing a registration must not run any
            // provider Dispose implementation (and potentially Unity code) on the shutdown caller.
            foreach (ServiceRegistration registration in _services.Values)
                registration.InvalidateUnderLock();
            _services.Clear();
            _capabilities.Clear();
        }
    }

    private void RequireMainThread()
    {
        if (!IsMainThread)
            throw new InvalidOperationException("The runtime lifecycle must be pumped from its captured main thread.");
    }

    private void Unregister(ServiceRegistration registration)
    {
        lock (_gate)
        {
            if (_services.TryGetValue(registration.Contract, out ServiceRegistration? current) &&
                ReferenceEquals(current, registration))
            {
                _services.Remove(registration.Contract);
                if (_capabilities.TryGetValue(registration.Capability.Id, out ServiceRegistration? capability) &&
                    ReferenceEquals(capability, registration))
                    _capabilities.Remove(registration.Capability.Id);
            }
            registration.InvalidateUnderLock();
        }
    }

    private void CancelPending(QueuedOperation operation)
    {
        lock (_gate)
        {
            if (operation.Node is null)
                return; // Work has already started or another cancellation/shutdown completed it.
            _queue.Remove(operation.Node);
            operation.Node = null;
            operation.CancelUnderLock();
        }
    }

    private static ModResult<T> SafeInvoke<T>(Func<ModResult<T>> operation)
    {
        try
        {
            return operation() ?? ModResult<T>.Fail(ModErrorCode.ProviderFailed, "The provider returned no operation result.");
        }
        catch (Exception exception)
        {
            // An OperationCanceledException thrown by already-started provider code is also a
            // provider failure; the dispatcher cannot attest that the transaction was rolled back.
            return ModResult<T>.Fail(ModErrorCode.ProviderFailed, $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private static ModResult<T> NotReady<T>() =>
        ModResult<T>.Fail(ModErrorCode.NotReady, "The mod runtime is not ready or has stopped.");

    private static ModResult<T> Cancelled<T>() =>
        ModResult<T>.Fail(ModErrorCode.Cancelled, "The operation was cancelled before it started.");

    private sealed class ServiceRegistration : IDisposable
    {
        private ModRuntimeHost? _owner;

        internal ServiceRegistration(ModRuntimeHost owner, Type contract, object service, CapabilityInfo capability)
        {
            _owner = owner;
            Contract = contract;
            Service = service;
            Capability = capability;
        }

        internal Type Contract { get; }
        internal object? Service { get; private set; }
        internal CapabilityInfo Capability { get; }

        public void Dispose() => Volatile.Read(ref _owner)?.Unregister(this);

        internal void InvalidateUnderLock()
        {
            Volatile.Write(ref _owner, null);
            Service = null;
        }
    }

    private abstract class QueuedOperation
    {
        private CancellationTokenRegistration _cancellation;

        protected QueuedOperation(ModRuntimeHost owner) => Owner = owner;

        protected ModRuntimeHost Owner { get; }
        internal LinkedListNode<QueuedOperation>? Node { get; set; }

        internal void RegisterCancellationUnderLock(CancellationToken token)
        {
            if (!token.CanBeCanceled)
                return;
            _cancellation = token.Register(static state =>
            {
                var pending = (QueuedOperation)state!;
                pending.Owner.CancelPending(pending);
            }, this);
            // Handles cancellation synchronously invoked by Register before the field assignment.
            if (Node is null)
                _cancellation.Unregister();
        }

        internal void StartUnderLock() => _cancellation.Unregister();

        internal void CancelUnderLock()
        {
            // Unregister is deliberately non-blocking: a cancellation callback might be waiting
            // for this lock. Dispose would wait for it and could deadlock the main thread.
            _cancellation.Unregister();
            CompleteCancelled();
        }

        protected abstract void CompleteCancelled();
        internal abstract void Execute();
    }

    private sealed class QueuedOperation<T> : QueuedOperation
    {
        private readonly TaskCompletionSource<ModResult<T>> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Func<ModResult<T>>? _operation;

        internal QueuedOperation(ModRuntimeHost owner, Func<ModResult<T>> operation) : base(owner) => _operation = operation;

        internal Task<ModResult<T>> Task => _completion.Task;

        protected override void CompleteCancelled()
        {
            _operation = null;
            _completion.TrySetResult(Cancelled<T>());
        }

        internal override void Execute()
        {
            Func<ModResult<T>> operation = _operation!;
            _operation = null;
            _completion.TrySetResult(SafeInvoke(operation));
        }
    }
}
