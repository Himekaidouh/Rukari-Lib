namespace Rukari.Lib;

/// <summary>A stable category for an operation failure. Messages are diagnostic text, not identifiers.</summary>
public enum ModErrorCode
{
    /// <summary>No error. This value cannot describe a failed result.</summary>
    None,
    /// <summary>The runtime or provider is not ready, or has stopped.</summary>
    NotReady,
    /// <summary>The requested capability is not supported.</summary>
    Unsupported,
    /// <summary>The operation requires the game main thread.</summary>
    WrongThread,
    /// <summary>An argument is missing or invalid.</summary>
    InvalidArgument,
    /// <summary>The registration or expected edit revision conflicts with current state.</summary>
    Conflict,
    /// <summary>The requested resource, service, or selection was not found.</summary>
    NotFound,
    /// <summary>The provider or bounded dispatch queue cannot accept more work.</summary>
    Busy,
    /// <summary>The operation was cancelled before it started.</summary>
    Cancelled,
    /// <summary>A provider operation failed unexpectedly.</summary>
    ProviderFailed
}

/// <summary>A managed failure description, safe to retain outside the game main thread.</summary>
/// <param name="Code">A machine-readable failure category.</param>
/// <param name="Message">A human-readable explanation; callers must not parse it as a contract.</param>
public sealed record ModError(ModErrorCode Code, string Message);

/// <summary>An explicit operation outcome. Check <see cref="Success"/> before reading <see cref="Value"/>.</summary>
/// <typeparam name="T">The managed value returned on success.</typeparam>
public sealed class ModResult<T>
{
    private ModResult(bool success, T value, ModError? error)
    {
        Success = success;
        Value = value;
        Error = error;
    }

    /// <summary>Whether the operation completed successfully.</summary>
    public bool Success { get; }

    /// <summary>The success value. This is <c>default(T)</c> on failure and must not then be used.</summary>
    public T Value { get; }

    /// <summary>The failure description; non-null on failure and null on success.</summary>
    public ModError? Error { get; }

    /// <summary>Creates a successful result.</summary>
    public static ModResult<T> Ok(T value) => new(true, value, null);

    /// <summary>Creates a failed result with a non-empty error category.</summary>
    public static ModResult<T> Fail(ModErrorCode code, string message) => Fail(new ModError(code, message));

    /// <summary>Creates a failed result. <see cref="ModErrorCode.None"/> is not a failure category.</summary>
    public static ModResult<T> Fail(ModError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (error.Code == ModErrorCode.None || !Enum.IsDefined(typeof(ModErrorCode), error.Code))
            throw new ArgumentOutOfRangeException(nameof(error), "A failed result requires a defined non-None error code.");
        ArgumentNullException.ThrowIfNull(error.Message);
        return new(false, default!, error);
    }
}
