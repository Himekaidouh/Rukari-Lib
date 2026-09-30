namespace Rukari.Lib;

/// <summary>The lifetime of a runtime instance. A stopped instance cannot be restarted.</summary>
public enum RuntimeState
{
    /// <summary>The runtime has not yet started accepting service operations.</summary>
    Starting,
    /// <summary>The runtime accepts registrations and dispatched operations.</summary>
    Ready,
    /// <summary>The runtime has stopped and released its registrations.</summary>
    Stopped
}

/// <summary>A provider's declared maturity level; no value is proof of native interface safety.</summary>
public enum CapabilityLevel
{
    /// <summary>The provider declares the capability available.</summary>
    Available,
    /// <summary>The capability is experimental and may have limited validation.</summary>
    Experimental,
    /// <summary>The provider declares documented validation for a specified environment.</summary>
    Verified,
    /// <summary>The capability is currently unavailable.</summary>
    Unavailable
}

/// <summary>Managed metadata describing one provided capability.</summary>
/// <param name="Id">A namespaced capability identifier.</param>
/// <param name="ProviderId">The registering mod's identifier.</param>
/// <param name="Version">The capability contract version, distinct from the game version.</param>
/// <param name="Level">The provider's maturity declaration.</param>
/// <param name="Detail">The scope and known limits of the declaration.</param>
public sealed record CapabilityInfo(string Id, string ProviderId, string Version, CapabilityLevel Level, string Detail);

/// <summary>
/// Shared managed service discovery and main-thread dispatch. Keep native game objects out of
/// public service contracts and queued captures; obtain fresh native state inside a provider operation.
/// </summary>
public interface IModRuntime
{
    /// <summary>The runtime lifecycle state.</summary>
    RuntimeState State { get; }

    /// <summary>Whether the caller is currently on the runtime's captured game main thread.</summary>
    bool IsMainThread { get; }

    /// <summary>Reads capability metadata. Managed discovery may be called from any thread.</summary>
    ModResult<CapabilityInfo> GetCapability(string id);

    /// <summary>
    /// Looks up a managed service contract. Discovery may be called from any thread;
    /// invoke main-thread-only service methods through <see cref="InvokeAsync{T}"/>.
    /// A retained service reference does not extend the provider's lifetime.
    /// </summary>
    ModResult<T> GetService<T>() where T : class;

    /// <summary>
    /// Registers one service on the main thread, without replacing any existing type or capability ID.
    /// The owner must equal the capability provider. Dispose the returned lease to remove only this registration.
    /// </summary>
    ModResult<IDisposable> RegisterService<T>(string ownerId, T service, CapabilityInfo capability) where T : class;

    /// <summary>
    /// Runs a synchronous operation on the main thread, immediately when already there or through a bounded
    /// queue otherwise. Cancellation prevents pending work from starting; it does not undo a transaction that
    /// has already started. Unexpected operation exceptions become <see cref="ModErrorCode.ProviderFailed"/>.
    /// Await the returned task; never synchronously block the main thread waiting for queued work.
    /// </summary>
    Task<ModResult<T>> InvokeAsync<T>(Func<ModResult<T>> operation, CancellationToken cancellationToken = default);
}
