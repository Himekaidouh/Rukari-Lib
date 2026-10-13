using System.Diagnostics;
using AzureArchive.VideoTools.Core.Commands;
using Rukari.Lib;
using Rukari.Lib.Editor;

namespace AzureArchive.VideoTools.Runtime;

/// <summary>
/// Reads one effect layer of the selected dialogue row, and states it once per change.
///
/// <para>
/// It reads the SHARED editor transaction's raw text and parses it with the extractor, not the editor's command
/// document model. That model refuses a row which declares two directives for one resource, and a layer holding two
/// effects is exactly the row this model is meant to produce, so a reader built on it could not report the layer it
/// exists to report.
/// </para>
///
/// <para>
/// The read is cached for a quarter second and re-run when the layer itself changes, because a page draws every
/// frame and a shared-transaction read is not free. Every distinct line is logged once at info level: a hosted
/// window's numbers are otherwise only checkable by whoever is looking at the screen, which is what used to make a
/// wrong count invisible.
/// </para>
/// </summary>
internal sealed class EffectLayerReader
{
    private readonly Action<string> _log;
    private string _layerKey = string.Empty;
    private string _logged = string.Empty;
    private long _lastRead;
    private EffectLayerSummary? _summary;

    internal EffectLayerReader(Action<string> log) =>
        _log = log ?? throw new ArgumentNullException(nameof(log));

    /// <summary>
    /// The layer's current state. <paramref name="force"/> skips the cache, which is what a page does when it is
    /// shown again: the row may have changed while the window was hidden.
    /// </summary>
    internal EffectLayerSummary Read(EffectLayer layer, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(layer);
        long now = Stopwatch.GetTimestamp();
        bool sameLayer = string.Equals(_layerKey, layer.ResourceKey, StringComparison.Ordinal);
        if (!force && sameLayer && _summary is not null && now - _lastRead < Stopwatch.Frequency / 4)
            return _summary;

        _layerKey = layer.ResourceKey;
        _lastRead = now;
        EffectLayerSummary summary = ReadNow(layer);
        _summary = summary;
        string line = summary.LogLine;
        if (string.Equals(line, _logged, StringComparison.Ordinal)) return summary;
        _logged = line;
        _log(line);
        return summary;
    }

    private static EffectLayerSummary ReadNow(EffectLayer layer)
    {
        var documents = ModServices.Current?.GetService<IEditorDocumentService>();
        if (documents?.Success != true || documents.Value is null)
            return EffectLayerSummary.Unavailable(layer, "共享编辑事务不可用。");
        ModResult<EditorDocumentSnapshot> read = documents.Value.ReadSelection();
        if (!read.Success || read.Value is null)
            return EffectLayerSummary.Unavailable(layer, read.Error?.Message ?? "当前没有选中的台词。");
        return EffectLayerSummary.Read(layer, read.Value.AdditionalPrompt ?? string.Empty);
    }
}
