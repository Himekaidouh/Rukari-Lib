using System.Text;

namespace AzureArchive.VideoTools.Tests;

internal static class AssertEx
{
    public static void True(bool condition, string? message = null)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message ?? "Expected condition to be true.");
        }
    }

    public static void False(bool condition, string? message = null) =>
        True(!condition, message ?? "Expected condition to be false.");

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                message ?? $"Expected <{expected}> but found <{actual}>.");
        }
    }

    public static T NotNull<T>(T? value, string? message = null)
        where T : class
    {
        if (value == null)
        {
            throw new InvalidOperationException(message ?? "Expected a non-null value.");
        }

        return value;
    }

    public static void Contains<T>(IEnumerable<T> values, Func<T, bool> predicate, string message)
    {
        if (!values.Any(predicate))
        {
            throw new InvalidOperationException(message);
        }
    }
}

internal sealed class TemporaryAapDirectory : IDisposable
{
    public TemporaryAapDirectory()
    {
        Root = Path.Combine(
            Path.GetTempPath(),
            "AzureArchive.VideoTools.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Write(string fileName, string json)
    {
        string path = Path.Combine(Root, fileName);
        File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    public string WriteBytes(string fileName, byte[] bytes)
    {
        string path = Path.Combine(Root, fileName);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public void Dispose()
    {
        string fullRoot = Path.GetFullPath(Root);
        string fullTemp = Path.GetFullPath(Path.GetTempPath());
        if (!fullRoot.StartsWith(fullTemp, StringComparison.OrdinalIgnoreCase)
            || !fullRoot.Contains("AzureArchive.VideoTools.Tests", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing to remove unexpected test path: {fullRoot}");
        }

        if (Directory.Exists(fullRoot))
        {
            Directory.Delete(fullRoot, recursive: true);
        }
    }
}
