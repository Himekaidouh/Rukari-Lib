using System;
using System.Collections.Generic;
using System.Linq;
using AzureArchive.VideoTools.Core.Commands;

namespace AzureArchive.VideoTools.Runtime;

internal sealed record CachedEmbeddedEditorDirectives(
    CompiledScriptIdentity CompiledScript,
    IReadOnlyList<string> CanonicalDirectives,
    bool HasContinueDirective,
    string ValidationError,
    long PutTimestampUtcTicks)
{
    public bool IsValid => ValidationError.Length == 0;
}

internal static class EmbeddedEditorDirectiveCache
{
    private const int MaximumEntries = 256;
    private static readonly object Gate = new();
    private static readonly Dictionary<CompiledScriptIdentity, CachedEmbeddedEditorDirectives>
        Entries = new();
    private static readonly Queue<CompiledScriptIdentity> InsertionOrder = new();

    public static void Replace(
        CompiledScriptIdentity identity,
        EmbeddedAavtExtraction extraction,
        string source)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(extraction);
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException("Cache source is required.", nameof(source));
        }

        int entryCount;
        lock (Gate)
        {
            var cached = new CachedEmbeddedEditorDirectives(
                identity,
                Array.AsReadOnly(extraction.Commands
                    .Select(command => command.CanonicalDirective)
                    .ToArray()),
                extraction.HasContinueDirective,
                extraction.Errors.Count == 0
                    ? string.Empty
                    : string.Join(" | ", extraction.Errors),
                DateTimeOffset.UtcNow.Ticks);
            Entries[identity] = cached;
            InsertionOrder.Enqueue(identity);
            while (Entries.Count > MaximumEntries && InsertionOrder.Count != 0)
            {
                CompiledScriptIdentity oldest = InsertionOrder.Dequeue();
                Entries.Remove(oldest);
            }

            entryCount = Entries.Count;
        }

        PlayerCommandObservationLog.Append(
            $"{DateTimeOffset.Now:O} embedded-preview-cache event=CACHE_PUT; "
            + $"source={source}; scriptSha16={ShortSha(identity.Sha256)}; "
            + $"scriptLength={identity.Utf16Length}; scriptLines={identity.LineCount}; "
            + $"commands={extraction.Commands.Count}; valid={extraction.Errors.Count == 0}; "
            + $"entries={entryCount}");
    }

    public static void Remove(CompiledScriptIdentity identity, string reason)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Cache removal reason is required.", nameof(reason));
        }
        bool removed;
        int entryCount;
        lock (Gate)
        {
            removed = Entries.Remove(identity);
            entryCount = Entries.Count;
        }

        PlayerCommandObservationLog.Append(
            $"{DateTimeOffset.Now:O} embedded-preview-cache event=CACHE_REMOVE; "
            + $"scriptSha16={ShortSha(identity.Sha256)}; removed={removed}; "
            + $"reason={reason}; entries={entryCount}");
    }

    /// <summary>
    /// Wipes the whole text-identity LRU. Used by the live project-key
    /// rotation fan-out: after the key changes every cached script identity
    /// belongs to a scene address that no longer resolves the same way.
    /// </summary>
    public static void Clear(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Cache clear reason is required.", nameof(reason));
        }

        int removedCount;
        lock (Gate)
        {
            removedCount = Entries.Count;
            Entries.Clear();
            InsertionOrder.Clear();
        }

        PlayerCommandObservationLog.Append(
            $"{DateTimeOffset.Now:O} embedded-preview-cache event=CACHE_CLEAR; "
            + $"removed={removedCount}; reason={reason}; entries=0");
    }

    public static bool TryRemoveStale(
        CompiledScriptIdentity identity,
        TimeSpan minimumAge,
        string reason,
        out bool hadContinueDirective)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (minimumAge < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumAge));
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Cache removal reason is required.", nameof(reason));
        }

        bool removed = false;
        int entryCount;
        lock (Gate)
        {
            hadContinueDirective = false;
            if (Entries.TryGetValue(identity, out CachedEmbeddedEditorDirectives? cached)
                && (cached.CanonicalDirectives.Count != 0 || cached.HasContinueDirective)
                && DateTimeOffset.UtcNow.Ticks - cached.PutTimestampUtcTicks >= minimumAge.Ticks)
            {
                hadContinueDirective = cached.HasContinueDirective;
                removed = Entries.Remove(identity);
            }

            entryCount = Entries.Count;
        }

        PlayerCommandObservationLog.Append(
            $"{DateTimeOffset.Now:O} embedded-preview-cache event=CACHE_REMOVE_STALE; "
            + $"scriptSha16={ShortSha(identity.Sha256)}; removed={removed}; "
            + $"hadContinue={hadContinueDirective}; reason={reason}; entries={entryCount}");
        return removed;
    }

    public static bool TryMatch(
        IReadOnlyList<string> managedUnityMessages,
        out CachedEmbeddedEditorDirectives? cached,
        out int exactMessageCount,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(managedUnityMessages);
        cached = null;
        exactMessageCount = 0;
        error = string.Empty;

        lock (Gate)
        {
            CachedEmbeddedEditorDirectives[] matches = managedUnityMessages
                .Select(CommandIdentity.CompiledScript)
                .Distinct()
                .Where(identity => Entries.ContainsKey(identity))
                .Select(identity => Entries[identity])
                .ToArray();
            if (matches.Length == 0)
            {
                return false;
            }

            if (matches.Length != 1)
            {
                error = "The player window contains more than one cached embedded editor script identity.";
                return false;
            }

            CachedEmbeddedEditorDirectives match = matches[0];
            cached = match;
            exactMessageCount = managedUnityMessages.Count(message =>
                CommandIdentity.CompiledScript(message) == match.CompiledScript);
            return exactMessageCount > 0;
        }
    }

    private static string ShortSha(string sha256) =>
        sha256.Length <= 16 ? sha256 : sha256[..16];
}
