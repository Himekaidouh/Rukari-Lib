namespace AzureArchive.VideoTools.Core.Results;

public readonly record struct Result(bool Success, string Error)
{
    public static Result Ok() => new(true, string.Empty);

    public static Result Fail(string error) => new(false, NormalizeError(error));

    private static string NormalizeError(string error) =>
        string.IsNullOrWhiteSpace(error) ? "Operation failed." : error.Trim();
}

public sealed class Result<T>
{
    private Result(bool success, T? value, string error)
    {
        Success = success;
        Value = value;
        Error = error;
    }

    public bool Success { get; }

    public T? Value { get; }

    public string Error { get; }

    public static Result<T> Ok(T value) => new(true, value, string.Empty);

    public static Result<T> Fail(string error) => new(
        false,
        default,
        string.IsNullOrWhiteSpace(error) ? "Operation failed." : error.Trim());
}
