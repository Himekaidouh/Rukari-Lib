using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Mapping;

/// <summary>
/// Lookup from a projected compiled-script SHA-256 to the playback record
/// indices that carry it. Building one costs exactly a single pass over a
/// playback snapshot's records, so caching it replaces the per-selection sweep
/// over every record of every project with dictionary lookups.
///
/// Freshness is structural rather than time-based: an index is only ever bound
/// to one immutable <see cref="PlaybackArchiveSnapshot"/> instance, and an
/// edited or re-exported AAS always yields a new instance because the snapshot
/// cache revalidates by write time and length before handing one out. A changed
/// archive therefore can never be served by a stale index, which is exactly the
/// guarantee the earlier linear scan was providing by brute force.
/// </summary>
public sealed class PlaybackScriptIndex
{
    private readonly Dictionary<string, List<IndexedScript>> _byHash;

    private PlaybackScriptIndex(
        Dictionary<string, List<IndexedScript>> byHash,
        int recordCount)
    {
        _byHash = byHash;
        RecordCount = recordCount;
    }

    /// <summary>Number of playback records this index covers.</summary>
    public int RecordCount { get; }

    /// <summary>
    /// Collects matching record indices in ascending order, which is the order
    /// the linear scan produced. The length and line-count filters are retained
    /// so the candidate set stays identical to that scan even in principle.
    /// </summary>
    public void CollectCandidates(
        string normalizedHash,
        int length,
        int lineCount,
        List<int> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        if (!_byHash.TryGetValue(normalizedHash, out List<IndexedScript>? entries))
        {
            return;
        }

        foreach (IndexedScript entry in entries)
        {
            if (entry.Length == length && entry.LineCount == lineCount)
            {
                into.Add(entry.RecordIndex);
            }
        }
    }

    /// <summary>
    /// Projects and hashes every record once. Failures carry the same messages
    /// the record-by-record scan produced, and they are reported for the first
    /// offending record in index order so diagnostics stay byte-identical.
    /// </summary>
    public static Result<PlaybackScriptIndex> Build(
        PlaybackArchiveSnapshot playback,
        Func<string, string> projection)
    {
        ArgumentNullException.ThrowIfNull(playback);
        ArgumentNullException.ThrowIfNull(projection);

        if (playback.Records == null)
        {
            return Result<PlaybackScriptIndex>.Fail(
                "Playback snapshot has no record collection.");
        }

        var byHash = new Dictionary<string, List<IndexedScript>>(StringComparer.Ordinal);
        for (int index = 0; index < playback.Records.Count; index++)
        {
            PlaybackRecordSnapshot? record = playback.Records[index];
            if (record == null
                || record.RecordIndex != index
                || record.CompiledScript == null)
            {
                return Result<PlaybackScriptIndex>.Fail(
                    $"Playback record {index} is null or violates the positional compiled-script contract.");
            }

            string projectedScript;
            try
            {
                projectedScript = projection(record.CompiledScript)
                    ?? throw new InvalidOperationException(
                        "Compiled-script identity projection returned null.");
            }
            catch (Exception ex)
            {
                return Result<PlaybackScriptIndex>.Fail(
                    $"Playback record {index} identity projection failed: {ex.Message}");
            }

            string hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(projectedScript)));
            if (!byHash.TryGetValue(hash, out List<IndexedScript>? entries))
            {
                entries = new List<IndexedScript>(1);
                byHash.Add(hash, entries);
            }

            entries.Add(new IndexedScript(
                index,
                projectedScript.Length,
                CountLines(projectedScript)));
        }

        return Result<PlaybackScriptIndex>.Ok(
            new PlaybackScriptIndex(byHash, playback.Records.Count));
    }

    private static int CountLines(string text)
    {
        int count = 1;
        foreach (char character in text)
        {
            if (character == '\n')
            {
                count++;
            }
        }

        return count;
    }

    private readonly record struct IndexedScript(
        int RecordIndex,
        int Length,
        int LineCount);
}

/// <summary>
/// Binds built indexes to the exact snapshot instance they were built from, so
/// they cannot outlive or misdescribe it. The table holds snapshots weakly and
/// the index holds no reference back to its snapshot, so a replaced snapshot is
/// collected along with its index.
///
/// The projection key is supplied live by the caller: a resolver whose
/// projection depends on mutable configuration must return a key that changes
/// with it, otherwise a cached index could describe a different projection.
/// </summary>
internal static class PlaybackScriptIndexCache
{
    private static readonly ConditionalWeakTable<PlaybackArchiveSnapshot, Buckets> Table =
        new();

    public static Result<PlaybackScriptIndex> GetOrBuild(
        PlaybackArchiveSnapshot playback,
        string projectionKey,
        Func<string, string> projection)
    {
        Buckets buckets = Table.GetValue(playback, _ => new Buckets());
        return buckets.GetOrBuild(projectionKey, playback, projection);
    }

    private sealed class Buckets
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, Result<PlaybackScriptIndex>> _entries =
            new(StringComparer.Ordinal);

        public Result<PlaybackScriptIndex> GetOrBuild(
            string projectionKey,
            PlaybackArchiveSnapshot playback,
            Func<string, string> projection)
        {
            lock (_gate)
            {
                if (_entries.TryGetValue(projectionKey, out Result<PlaybackScriptIndex>? cached))
                {
                    return cached;
                }

                Result<PlaybackScriptIndex> built =
                    PlaybackScriptIndex.Build(playback, projection);

                // Failures are never cached: a corrupt snapshot must keep
                // reporting its own reason instead of freezing one verdict.
                if (built.Success)
                {
                    _entries[projectionKey] = built;
                }

                return built;
            }
        }
    }
}
