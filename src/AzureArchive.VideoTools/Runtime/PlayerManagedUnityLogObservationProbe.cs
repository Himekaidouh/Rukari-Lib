using System;
using BepInEx.Logging;

namespace AzureArchive.VideoTools.Runtime;

internal static class PlayerManagedUnityLogObservationProbe
{
    private static ManagedUnityLogListener? _listener;

    public static void Install()
    {
        if (_listener != null)
        {
            return;
        }

        _listener = new ManagedUnityLogListener();
        BepInEx.Logging.Logger.Listeners.Add(_listener);
        Plugin.Logger.LogInfo(
            "Player command observation listener installed; source=Unity, data=managed-string-only.");
    }

    private sealed class ManagedUnityLogListener : ILogListener
    {
        public LogLevel LogLevelFilter => LogLevel.Message;

        public void LogEvent(object sender, LogEventArgs eventArgs)
        {
            if (string.Equals(
                    eventArgs.Source.SourceName,
                    "Unity",
                    StringComparison.Ordinal)
                && eventArgs.Data is string text)
            {
                PlayerAdvanceObservationWindow.Capture(text);
            }
        }

        public void Dispose()
        {
        }
    }
}
