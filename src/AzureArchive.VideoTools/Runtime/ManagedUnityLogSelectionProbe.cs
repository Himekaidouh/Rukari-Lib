using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using BepInEx.Logging;

namespace AzureArchive.VideoTools.Runtime;

internal static class ManagedUnityLogSelectionProbe
{
    private const int Capacity = 16;
    private static readonly object Gate = new();
    private static readonly Queue<ManagedLogEntry> Entries = new(Capacity);
    private static ManagedUnityLogListener? _listener;
    private static long _sequence;
    private static DataListMarker _marker;

    public static void Install()
    {
        if (_listener != null)
        {
            return;
        }

        _listener = new ManagedUnityLogListener();
        BepInEx.Logging.Logger.Listeners.Add(_listener);
        Plugin.Logger.LogInfo(
            "Managed Unity-log selection probe installed; it accepts only BepInEx-managed string data.");
    }

    public static void MarkDataList(int requestId)
    {
        lock (Gate)
        {
            _marker = new DataListMarker(requestId, _sequence);
        }
    }

    public static SelectionCorrelation CompleteSelection()
    {
        lock (Gate)
        {
            if (_marker.RequestId == 0)
            {
                return SelectionCorrelation.NoMarker;
            }

            List<ManagedLogEntry> afterMarker = new();
            foreach (ManagedLogEntry entry in Entries)
            {
                if (entry.Sequence > _marker.UnityLogSequence)
                {
                    afterMarker.Add(entry);
                }
            }

            ManagedLogEntry first = afterMarker.Count > 0
                ? afterMarker[0]
                : default;
            ManagedLogEntry latest = afterMarker.Count > 0
                ? afterMarker[afterMarker.Count - 1]
                : default;
            return new SelectionCorrelation(
                true,
                _marker.RequestId,
                _marker.UnityLogSequence,
                _sequence,
                afterMarker.Count,
                first,
                latest);
        }
    }

    private static void Capture(LogEventArgs eventArgs)
    {
        if (!string.Equals(eventArgs.Source.SourceName, "Unity", StringComparison.Ordinal)
            || eventArgs.Data is not string text)
        {
            return;
        }

        ManagedLogEntry entry = CreateEntry(text);
        lock (Gate)
        {
            entry = entry with { Sequence = ++_sequence };
            Entries.Enqueue(entry);
            while (Entries.Count > Capacity)
            {
                Entries.Dequeue();
            }
        }
    }

    private static ManagedLogEntry CreateEntry(string text)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        string hash = Convert.ToHexString(digest);
        int lineCount = 1;
        foreach (char character in text)
        {
            if (character == '\n')
            {
                lineCount++;
            }
        }

        string preview = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (preview.Length > 96)
        {
            preview = preview.Substring(0, 96);
        }

        return new ManagedLogEntry(0, text.Length, lineCount, hash, preview);
    }

    private sealed class ManagedUnityLogListener : ILogListener
    {
        public LogLevel LogLevelFilter => LogLevel.Message;

        public void LogEvent(object sender, LogEventArgs eventArgs)
        {
            Capture(eventArgs);
        }

        public void Dispose()
        {
        }
    }

    private readonly record struct DataListMarker(int RequestId, long UnityLogSequence);

    internal readonly record struct ManagedLogEntry(
        long Sequence,
        int Length,
        int LineCount,
        string Hash,
        string Preview);

    internal readonly record struct SelectionCorrelation(
        bool HasMarker,
        int RequestId,
        long StartSequence,
        long EndSequence,
        int MessagesAfterDataList,
        ManagedLogEntry First,
        ManagedLogEntry Latest)
    {
        public static SelectionCorrelation NoMarker => new(
            false,
            0,
            0,
            0,
            0,
            default,
            default);
    }
}
