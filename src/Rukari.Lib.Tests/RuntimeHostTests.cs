namespace Rukari.Lib.Tests;

internal static class RuntimeHostTests
{
    private const string Owner = "tests.provider";
    private const string CapabilityId = "tests.counter";

    internal static void ReadinessPreventsPrematureAndPostStopWrites()
    {
        using var host = new ModRuntimeHost();
        var writes = 0;
        Check.Failure(ModErrorCode.NotReady, Check.Completed(host.InvokeAsync(() => ModResult<int>.Ok(++writes))));
        Check.Failure(ModErrorCode.NotReady, host.GetService<ICounter>());
        host.Start();
        host.Start();
        Check.Equal(1, Check.Success(Check.Completed(host.InvokeAsync(() => ModResult<int>.Ok(++writes)))), "Ready host should execute writes.");
        host.Dispose();
        Check.Failure(ModErrorCode.NotReady, Check.Completed(host.InvokeAsync(() => ModResult<int>.Ok(++writes))));
        Check.Equal(1, writes, "Rejected writes must never execute.");
        Check.Throws<ObjectDisposedException>(host.Start);
    }

    internal static void StartAndPumpRequireCreatingThread()
    {
        using var host = new ModRuntimeHost();
        Check.OnWorker(() =>
        {
            Check.True(!host.IsMainThread, "Worker must not be mistaken for the creating thread.");
            Check.Throws<InvalidOperationException>(host.Start);
            return true;
        });
        host.Start();
        Check.OnWorker(() =>
        {
            Check.Throws<InvalidOperationException>(() => host.Pump());
            return true;
        });
        Check.True(host.IsMainThread, "Owner must retain main thread identity.");
    }

    internal static void MainThreadCallsCompleteImmediately()
    {
        using var host = Ready();
        var ownerThread = Environment.CurrentManagedThreadId;
        var operation = host.InvokeAsync(() => ModResult<int>.Ok(Environment.CurrentManagedThreadId));
        Check.True(operation.IsCompleted, "Main thread must not await its own pump.");
        Check.Equal(ownerThread, Check.Success(Check.Completed(operation)), "Callback ran on the wrong thread.");
    }

    internal static void WorkersQueueInOrderUntilMainThreadPumps()
    {
        using var host = Ready();
        var executionOrder = new List<int>();
        var executionThreads = new List<int>();
        var pending = Enumerable.Range(1, 3).Select(index => Check.OnWorker(() => host.InvokeAsync(() =>
        {
            executionOrder.Add(index);
            executionThreads.Add(Environment.CurrentManagedThreadId);
            return ModResult<int>.Ok(index);
        }))).ToArray();

        Check.Equal(0, executionOrder.Count, "A worker must never execute the native-facing callback.");
        Check.True(pending.All(task => !task.IsCompleted), "Queued calls must wait for the owner thread.");
        host.Pump(2);
        Check.True(executionOrder.SequenceEqual(new[] { 1, 2 }), "Pump must preserve enqueue order and budget.");
        Check.True(!pending[2].IsCompleted, "An operation past the pump budget must remain pending.");
        host.Pump(1);
        Check.True(executionOrder.SequenceEqual(new[] { 1, 2, 3 }), "Remaining operation must execute next.");
        Check.True(executionThreads.All(id => id == Environment.CurrentManagedThreadId), "Every callback must run on the host thread.");
        for (var index = 0; index < pending.Length; index++)
            Check.Equal(index + 1, Check.Success(Check.Completed(pending[index])), "Result does not match its queued operation.");
    }

    internal static void FullQueueRejectsOnlyTheNewOperation()
    {
        using var host = Ready(1);
        var writes = 0;
        var first = Check.OnWorker(() => host.InvokeAsync(() => ModResult<int>.Ok(++writes)));
        var rejected = Check.OnWorker(() => host.InvokeAsync(() => ModResult<int>.Ok(++writes)));
        Check.Failure(ModErrorCode.Busy, Check.Completed(rejected));
        Check.True(!first.IsCompleted, "Queue overflow must not evict an accepted write.");
        host.Pump();
        Check.Equal(1, Check.Success(Check.Completed(first)), "Accepted write must remain intact.");
        Check.Equal(1, writes, "Rejected operation must never execute.");
    }

    internal static void PreCancelledOperationNeverWrites()
    {
        using var host = Ready();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var writes = 0;
        Check.Failure(ModErrorCode.Cancelled, Check.Completed(host.InvokeAsync(() => ModResult<int>.Ok(++writes), cancellation.Token)));
        var workerCall = Check.OnWorker(() => host.InvokeAsync(() => ModResult<int>.Ok(++writes), cancellation.Token));
        Check.Failure(ModErrorCode.Cancelled, Check.Completed(workerCall));
        host.Pump();
        Check.Equal(0, writes, "Pre-cancelled calls must not execute on either calling thread.");
    }

    internal static void QueuedCancellationCompletesWithoutPumpAndReleasesCapacity()
    {
        using var host = Ready(1);
        using var cancellation = new CancellationTokenSource();
        var cancelledWrites = 0;
        var pending = Check.OnWorker(() => host.InvokeAsync(() => ModResult<int>.Ok(++cancelledWrites), cancellation.Token));
        cancellation.Cancel();
        Check.Failure(ModErrorCode.Cancelled, Check.Completed(pending));
        var replacement = Check.OnWorker(() => host.InvokeAsync(() => ModResult<int>.Ok(42)));
        Check.True(!replacement.IsCompleted, "Cancelled operation must immediately release queue capacity.");
        host.Pump();
        Check.Equal(42, Check.Success(Check.Completed(replacement)), "Replacement operation should be accepted.");
        Check.Equal(0, cancelledWrites, "Cancelled pending callback must never write later.");
    }

    internal static void CancellationDuringWritePreservesActualResult()
    {
        using var host = Ready();
        using var cancellation = new CancellationTokenSource();
        var writes = 0;
        var queued = Check.OnWorker(() => host.InvokeAsync(() =>
        {
            writes++;
            Check.OnWorker(() => { cancellation.Cancel(); return true; });
            return ModResult<int>.Ok(writes);
        }, cancellation.Token));
        host.Pump();
        Check.Equal(1, Check.Success(Check.Completed(queued)), "Cancellation after a write starts must not falsely report an unapplied write.");
        Check.Equal(1, writes, "Write should run exactly once.");
    }

    internal static void StopCancelsPendingWritesFromAnyThread()
    {
        using var host = Ready();
        var writes = 0;
        var pending = Check.OnWorker(() => host.InvokeAsync(() => ModResult<int>.Ok(++writes)));
        Check.OnWorker(() => { host.Dispose(); return true; });
        Check.Failure(ModErrorCode.Cancelled, Check.Completed(pending));
        Check.Equal(0, writes, "Shutdown must never flush queued writes into a disposed scene.");
        Check.True(!Check.Completed(host.InvokeAsync(() => ModResult<int>.Ok(++writes))).Success, "Stopped dispatcher must reject new writes.");
        Check.Equal(0, writes, "Post-stop write must not execute.");
    }

    internal static void StopDuringWritePreservesActualResult()
    {
        using var host = Ready();
        var writes = 0;
        var pending = Check.OnWorker(() => host.InvokeAsync(() =>
        {
            writes++;
            Check.OnWorker(() => { host.Dispose(); return true; });
            return ModResult<int>.Ok(writes);
        }));
        var neverStarted = Check.OnWorker(() => host.InvokeAsync(() => ModResult<int>.Ok(++writes)));
        host.Pump();
        Check.Equal(1, Check.Success(Check.Completed(pending)), "A completed write must not be mislabeled as cancelled by concurrent shutdown.");
        Check.Failure(ModErrorCode.Cancelled, Check.Completed(neverStarted));
        Check.Equal(1, writes, "Shutdown must cancel only work that has not started.");
    }

    internal static void FailedOperationDoesNotPoisonFollowingWrites()
    {
        using var host = Ready();
        var first = Check.OnWorker(() => host.InvokeAsync<int>(() => throw new InvalidOperationException("provider failed")));
        var second = Check.OnWorker(() => host.InvokeAsync(() => ModResult<int>.Ok(17)));
        host.Pump();
        var failure = Check.Completed(first);
        Check.Failure(ModErrorCode.ProviderFailed, failure);
        Check.Equal(17, Check.Success(Check.Completed(second)), "One provider exception must not strand following writes.");
        var immediate = host.InvokeAsync<int>(() => throw new InvalidOperationException("immediate failure"));
        Check.Failure(ModErrorCode.ProviderFailed, Check.Completed(immediate));
    }

    internal static void ExplicitProviderFailureIsPreserved()
    {
        using var host = Ready();
        var provided = ModResult<int>.Fail(ModErrorCode.Conflict, "The selected line changed.");
        var operation = Check.OnWorker(() => host.InvokeAsync(() => provided));
        host.Pump();
        var result = Check.Completed(operation);
        Check.Failure(ModErrorCode.Conflict, result);
        Check.Equal("The selected line changed.", result.Error!.Message, "Host must preserve provider conflict details for callers.");
    }

    internal static void NullProviderResultDoesNotStrandFollowingWrites()
    {
        using var host = Ready();
        var invalid = Check.OnWorker(() => host.InvokeAsync<int>(() => null!));
        var following = Check.OnWorker(() => host.InvokeAsync(() => ModResult<int>.Ok(23)));
        host.Pump();
        Check.Failure(ModErrorCode.ProviderFailed, Check.Completed(invalid));
        Check.Equal(23, Check.Success(Check.Completed(following)), "Invalid provider response must not strand later work.");
    }

    internal static void ProviderCancellationExceptionDoesNotClaimRollback()
    {
        using var host = Ready();
        var writes = 0;
        var operation = Check.OnWorker(() => host.InvokeAsync<int>(() =>
        {
            writes++;
            throw new OperationCanceledException("A downstream operation was cancelled after writing.");
        }));
        host.Pump();
        Check.Failure(ModErrorCode.ProviderFailed, Check.Completed(operation));
        Check.Equal(1, writes, "Provider exception cannot prove that an already-started operation made no changes.");
    }

    internal static void ProviderCanDispatchNestedMainThreadWork()
    {
        using var host = Ready();
        using var lease = Check.Success(host.RegisterService<ICounter>(Owner, new Counter(12), Capability()));
        var operation = Check.OnWorker(() => host.InvokeAsync(() =>
        {
            var nested = host.InvokeAsync(() => host.GetService<ICounter>());
            Check.True(nested.IsCompleted, "Nested main-thread operation must not deadlock waiting for its own queue.");
            return ModResult<int>.Ok(Check.Success(Check.Completed(nested)).Value);
        }));
        host.Pump();
        Check.Equal(12, Check.Success(Check.Completed(operation)), "Nested calls must preserve their results.");
    }

    internal static void DuplicateServiceAndCapabilityNeverReplaceExistingOwner()
    {
        using var host = Ready();
        var first = new Counter(1);
        using var registration = Check.Success(host.RegisterService<ICounter>(Owner, first, Capability()));
        Check.Failure(ModErrorCode.Conflict, host.RegisterService<ICounter>(Owner, new Counter(2), Capability("tests.other")));
        Check.Failure(ModErrorCode.Conflict, host.RegisterService<ILabel>(Owner, new Label(), Capability()));
        Check.Same(first, Check.Success(host.GetService<ICounter>()), "Conflicting registration must not replace the existing service.");
        Check.True(!host.GetService<ILabel>().Success, "Capability conflict must not partially register a second service.");
        Check.True(!host.GetCapability("tests.other").Success, "Service type conflict must not partially publish a capability.");
    }

    internal static void OldRegistrationLeaseCannotRemoveReplacement()
    {
        using var host = Ready();
        var oldLease = Check.Success(host.RegisterService<ICounter>(Owner, new Counter(1), Capability()));
        oldLease.Dispose();
        var replacement = new Counter(2);
        using var newLease = Check.Success(host.RegisterService<ICounter>(Owner, replacement, Capability()));
        oldLease.Dispose();
        Check.Same(replacement, Check.Success(host.GetService<ICounter>()), "Repeated old cleanup must not remove another registration.");
        Check.True(host.GetCapability(CapabilityId).Success, "Repeated old cleanup must preserve the new capability.");
        Check.OnWorker(() => { newLease.Dispose(); return true; });
        Check.True(!host.GetService<ICounter>().Success, "Background cleanup must remove its exact registration.");
        Check.True(!host.GetCapability(CapabilityId).Success, "Service and capability must be removed together.");
    }

    internal static void ForeignOwnerCannotPublishCapability()
    {
        using var host = Ready();
        Check.Failure(ModErrorCode.InvalidArgument, host.RegisterService<ICounter>("different.owner", new Counter(1), Capability()));
        Check.True(!host.GetService<ICounter>().Success, "Rejected ownership must not register a service.");
        Check.True(!host.GetCapability(CapabilityId).Success, "Rejected ownership must not publish a capability.");
        using var lease = Check.Success(host.RegisterService<ICounter>(Owner, new Counter(2), Capability()));
    }

    internal static void BackgroundRegistrationCannotModifyRegistry()
    {
        using var host = Ready();
        var registration = Check.OnWorker(() => host.RegisterService<ICounter>(Owner, new Counter(1), Capability()));
        Check.Failure(ModErrorCode.WrongThread, registration);
        Check.True(!host.GetService<ICounter>().Success, "Wrong-thread registration must leave registry unchanged.");
    }

    internal static void WorkersCanReadManagedServiceSnapshots()
    {
        using var host = Ready();
        var provider = new Counter(3);
        using var lease = Check.Success(host.RegisterService<ICounter>(Owner, provider, Capability()));
        Check.OnWorker(() =>
        {
            Check.Same(provider, Check.Success(host.GetService<ICounter>()), "Worker must read the same managed provider.");
            Check.Equal(Owner, Check.Success(host.GetCapability(CapabilityId)).ProviderId, "Worker must receive capability metadata.");
            return true;
        });
    }

    internal static void StopRemovesServicesAndCapabilities()
    {
        using var host = Ready();
        using var lease = Check.Success(host.RegisterService<ICounter>(Owner, new Counter(1), Capability()));
        host.Dispose();
        Check.True(!host.GetService<ICounter>().Success, "Stopped host must not expose stale services.");
        Check.True(!host.GetCapability(CapabilityId).Success, "Stopped host must not expose stale capabilities.");
        Check.True(!host.RegisterService<ICounter>(Owner, new Counter(2), Capability()).Success, "Stopped host must not accept providers.");
    }

    internal static void RegistryRemovalDoesNotDisposeProviderOnWrongThread()
    {
        using var host = Ready();
        var provider = new DisposableCounter();
        var lease = Check.Success(host.RegisterService<ICounter>(Owner, provider, Capability()));
        Check.OnWorker(() => { lease.Dispose(); return true; });
        Check.Equal(0, provider.DisposeCalls, "Unregistering must not transfer provider lifetime to an arbitrary cleanup thread.");
        using var replacementLease = Check.Success(host.RegisterService<ICounter>(Owner, provider, Capability()));
        Check.OnWorker(() => { host.Dispose(); return true; });
        Check.Equal(0, provider.DisposeCalls, "Host shutdown must leave native resource destruction to the provider's lifecycle.");
    }

    internal static void GlobalRuntimeAttachmentCannotBeOverwrittenOrDetachedByAnotherHost()
    {
        using var first = Ready();
        using var second = Ready();
        Check.True(!ModServices.TryGetRuntime(out _), "Test process must begin without an attached runtime.");
        try
        {
            ModServices.Attach(first);
            ModServices.Attach(first);
            Check.True(ModServices.TryGetRuntime(out var resolved), "Attached runtime must be discoverable.");
            Check.Same(first, resolved, "Repeated attachment must retain the same host.");
            Check.Throws<InvalidOperationException>(() => ModServices.Attach(second));
            ModServices.Detach(second);
            Check.Same(first, ModServices.Current, "A foreign host must not detach the current runtime.");
            ModServices.Detach(first);
            ModServices.Attach(second);
            ModServices.Detach(first);
            Check.Same(second, ModServices.Current, "Delayed cleanup from an old host must preserve its replacement.");
        }
        finally
        {
            ModServices.Detach(first);
            ModServices.Detach(second);
        }

        Check.True(!ModServices.TryGetRuntime(out _), "Exact detachment must remove the runtime.");
    }

    private static ModRuntimeHost Ready(int capacity = 128)
    {
        var host = new ModRuntimeHost(capacity);
        host.Start();
        return host;
    }

    private static CapabilityInfo Capability(string id = CapabilityId) => new(id, Owner, "0.1.0", default, "Test service.");

    private interface ICounter { int Value { get; } }
    private sealed record Counter(int Value) : ICounter;
    private sealed class DisposableCounter : ICounter, IDisposable
    {
        public int Value => 1;
        internal int DisposeCalls { get; private set; }
        public void Dispose() => DisposeCalls++;
    }
    private interface ILabel { }
    private sealed class Label : ILabel { }
}
