using System.Runtime.ExceptionServices;

namespace Rukari.Lib.Tests;

internal static class Check
{
    internal static void True(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    internal static void Equal<T>(T expected, T actual) => Equal(expected, actual, "Values must match.");

    /// <summary>
    /// Compares two numbers a test may only expect to a small tolerance, such as a time parsed out of a file. An
    /// exact comparison there fails on the last bit of a double rather than on the thing being tested.
    /// </summary>
    internal static void Near(double expected, double actual, string message, double tolerance = .0005)
    {
        if (!double.IsFinite(actual) || Math.Abs(expected - actual) > tolerance)
            throw new InvalidOperationException($"{message} Expected: {expected}; actual: {actual}.");
    }

    internal static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message} Expected: {expected}; actual: {actual}.");
    }

    internal static void Same(object expected, object? actual, string message) =>
        True(ReferenceEquals(expected, actual), message);

    internal static void Failure<T>(ModErrorCode expected, ModResult<T> actual)
    {
        True(!actual.Success, $"Expected failure {expected}.");
        True(actual.Error is not null, "Failure must describe its error.");
        Equal(expected, actual.Error!.Code, "Unexpected failure code.");
    }

    internal static T Success<T>(ModResult<T> result)
    {
        True(result.Success, $"Expected success, got {result.Error}.");
        return result.Value;
    }

    internal static void Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    // Return the Task itself: awaiting on this worker would deadlock the host's pump.
    internal static T OnWorker<T>(Func<T> action)
    {
        T result = default!;
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try { result = action(); }
            catch (Exception exception) { failure = exception; }
        }) { IsBackground = true };
        worker.Start();
        True(worker.Join(TimeSpan.FromSeconds(5)), "Worker did not return within five seconds.");
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }

    internal static T Completed<T>(Task<T> task)
    {
        True(task.Wait(TimeSpan.FromSeconds(5)), "Operation did not complete within five seconds.");
        return task.GetAwaiter().GetResult();
    }
}
