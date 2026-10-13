using System.Globalization;

namespace AzureArchive.VideoTools.Core.Commands;

/// <summary>
/// The order of a camera window's effects, with no surface attached: what the author has stacked, what is selected,
/// and what a write would send (2026-10-13).
///
/// <para>
/// It is separate from the drawing because the same stack is drawn in two very different places — the standalone
/// window first, and now the picture editor's own camera mode, which has its own widgets and its own idea of a row.
/// The behaviour that matters (what is appended where, what a move does, when the row is pending, what the summary
/// says) is identical in both, so it is stated once here and tested once here; only the pixels differ.
/// </para>
///
/// <para>
/// One instance is one row of one dialogue line: <see cref="Adopt"/> replaces everything it holds with what the game
/// reports, which is what happens on every read and after every successful write.
/// </para>
/// </summary>
public sealed class CameraLayerStack
{
    private readonly List<CameraLayerSegment> _rows = new();

    /// <summary>A row of the two layers, in display order: the layers are interleaved here and filtered when read.</summary>
    public IReadOnlyList<CameraLayerSegment> Rows => _rows;

    /// <summary>How the row names itself, empty when the reader had no name for it.</summary>
    public string RowLabel { get; private set; } = string.Empty;

    /// <summary>The revision the current stack was read from; a write is guarded by it.</summary>
    public string RevisionSha256 { get; private set; } = string.Empty;

    /// <summary>The selection token the current stack was read from; a write is guarded by it.</summary>
    public string SelectionToken { get; private set; } = string.Empty;

    /// <summary>What the last read said, shown whenever there is no message from an action.</summary>
    public string ReadStatus { get; private set; } = "正在读取当前台词…";

    /// <summary>The last thing the author did, cleared when the stack is re-read from a write.</summary>
    public string Message { get; private set; } = string.Empty;

    /// <summary>Whether the stack on screen differs from the row it was read from.</summary>
    public bool HasPendingChanges { get; private set; }

    /// <summary>Whether a write can be attempted at all: a row was read and it is still the one the stack came from.</summary>
    public bool CanWrite => RevisionSha256.Length != 0 && SelectionToken.Length != 0;

    /// <summary>The layer the selection is in.</summary>
    public EffectLayer SelectedLayer { get; private set; } = EffectLayerCatalog.Camera;

    /// <summary>Index of the selected effect inside its own layer, or -1 when nothing is selected.</summary>
    public int SelectedIndex { get; private set; } = -1;

    /// <summary>What the author would act on, or null when the layer it was in no longer has that row.</summary>
    public CameraLayerSegment? Selected => RowAt(SelectedLayer, SelectedIndex);

    /// <summary>What a status line shows: the last action, or the state of the row.</summary>
    public string Status => Message.Length != 0 ? Message : ReadStatus;

    /// <summary>One layer's rows, in play order.</summary>
    public IReadOnlyList<CameraLayerSegment> RowsOf(EffectLayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        var rows = new List<CameraLayerSegment>();
        foreach (CameraLayerSegment row in _rows)
        {
            if (string.Equals(row.LayerKey, layer.ResourceKey, StringComparison.Ordinal)) rows.Add(row);
        }

        return rows;
    }

    /// <summary>How many effects one layer currently holds.</summary>
    public int CountOf(EffectLayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        int count = 0;
        foreach (CameraLayerSegment row in _rows)
        {
            if (string.Equals(row.LayerKey, layer.ResourceKey, StringComparison.Ordinal)) count++;
        }

        return count;
    }

    /// <summary>One layer's writable directives, in play order.</summary>
    public IReadOnlyList<string> DirectivesOf(EffectLayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        var directives = new List<string>();
        foreach (CameraLayerSegment row in _rows)
        {
            if (string.Equals(row.LayerKey, layer.ResourceKey, StringComparison.Ordinal))
                directives.Add(row.PublicDirective);
        }

        return directives;
    }

    /// <summary>One layer's own count and length, for its header.</summary>
    public EffectLayerSummary SummaryOf(EffectLayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        var directives = new List<EffectLayerDirective>();
        foreach (CameraLayerSegment row in _rows)
        {
            if (string.Equals(row.LayerKey, layer.ResourceKey, StringComparison.Ordinal))
                directives.Add(new EffectLayerDirective(row.LayerKey, row.CanonicalDirective));
        }

        return EffectLayerSummary.FromDirectives(layer, directives);
    }

    /// <summary>
    /// Both layers as they should be written. Both are always included, so clearing a layer is the same operation as
    /// changing one and a window can never leave a stale effect behind in the layer it did not touch.
    /// </summary>
    public IReadOnlyList<CameraLayerEdit> BuildEdits()
    {
        var edits = new List<CameraLayerEdit>(CameraLayers.All.Count);
        foreach (EffectLayer layer in CameraLayers.All)
            edits.Add(new CameraLayerEdit(layer.ResourceKey, new List<string>(DirectivesOf(layer))));
        return edits;
    }

    /// <summary>
    /// Takes a read as the truth. A failed read clears the stack rather than leaving the previous row on screen, and
    /// the reason is shown in its place.
    /// </summary>
    public void Adopt(CameraLayerRead read, string? message = null)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (!read.Available)
        {
            _rows.Clear();
            RowLabel = string.Empty;
            RevisionSha256 = string.Empty;
            SelectionToken = string.Empty;
            HasPendingChanges = false;
            Message = string.Empty;
            SelectedIndex = -1;
            ReadStatus = string.IsNullOrWhiteSpace(read.Error) ? "当前台词不可用，请重新选中台词。" : read.Error;
            return;
        }

        _rows.Clear();
        _rows.AddRange(read.Segments);
        RowLabel = read.RowLabel;
        RevisionSha256 = read.RevisionSha256;
        SelectionToken = read.SelectionToken;
        HasPendingChanges = false;
        if (message is not null) Message = message;
        KeepSelectionValid();
        ReadStatus = _rows.Count == 0
            ? "这一行还没有镜头效果：调好镜头后按「＋ 加为下一段」。"
            : $"已读取{(string.IsNullOrEmpty(RowLabel) ? "当前台词" : RowLabel)}："
              + $"{CountOf(EffectLayerCatalog.Camera)} 段镜头、"
              + $"{CountOf(EffectLayerCatalog.BackgroundCamera)} 段背景镜头。";
    }

    /// <summary>
    /// Appends a draft to the end of its own layer: the stack is the order, so a new effect goes after what is there.
    /// The draft's own layer decides where it lands; nothing about the text is guessed.
    /// </summary>
    public bool Add(CameraDraft draft)
    {
        if (!draft.Available)
        {
            Message = draft.Error.Length == 0 ? "当前没有可用的镜头草稿。" : draft.Error;
            return false;
        }

        EffectLayer layer = LayerOf(draft.LayerKey);
        _rows.Add(new CameraLayerSegment(
            layer.ResourceKey,
            layer.Label,
            0,
            draft.PublicDirective,
            draft.CanonicalDirective,
            draft.Label));
        HasPendingChanges = true;
        Select(layer, CountOf(layer) - 1);
        Message = $"已把当前镜头加为{layer.Label}的第 {CountOf(layer)} 段，按「应用」写入当前句。";
        return true;
    }

    /// <summary>Removes one effect of one layer. Only that layer's own numbering is affected.</summary>
    public bool Delete(EffectLayer layer, int index)
    {
        ArgumentNullException.ThrowIfNull(layer);
        int position = PositionOf(layer, index);
        if (position < 0) return false;
        _rows.RemoveAt(position);
        HasPendingChanges = true;
        Message = $"已删除{layer.Label}的第 {index + 1} 段，按「应用」写入当前句。";
        KeepSelectionValid();
        return true;
    }

    /// <summary>Moves one effect one place within its own layer, or says why it cannot.</summary>
    public bool Move(EffectLayer layer, int index, int delta)
    {
        ArgumentNullException.ThrowIfNull(layer);
        int total = CountOf(layer);
        int target = index + delta;
        if (target < 0 || target >= total)
        {
            Message = delta < 0
                ? $"{layer.Label}的第 1 段已经在最前面。"
                : $"{layer.Label}的最后一段已经在最后面。";
            return false;
        }

        int from = PositionOf(layer, index);
        int to = PositionOf(layer, target);
        if (from < 0 || to < 0) return false;
        (_rows[from], _rows[to]) = (_rows[to], _rows[from]);
        HasPendingChanges = true;
        Select(layer, target);
        Message = $"已把{layer.Label}的第 {index + 1} 段移到第 {target + 1} 段，按「应用」写入当前句。";
        return true;
    }

    /// <summary>Removes the selected effect, if there is one.</summary>
    public bool DeleteSelected() => SelectedIndex >= 0 && Delete(SelectedLayer, SelectedIndex);

    /// <summary>Moves the selected effect within its own layer.</summary>
    public bool MoveSelected(int delta) => SelectedIndex >= 0 && Move(SelectedLayer, SelectedIndex, delta);

    /// <summary>Selects one effect of one layer. It clamps rather than throwing, because the caller is a click.</summary>
    public void Select(EffectLayer layer, int index)
    {
        ArgumentNullException.ThrowIfNull(layer);
        int total = CountOf(layer);
        SelectedLayer = layer;
        SelectedIndex = total == 0 ? -1 : Math.Clamp(index, 0, total - 1);
    }

    /// <summary>Sets the message shown until the next action, without touching the stack.</summary>
    public void Note(string message) => Message = message ?? string.Empty;

    /// <summary>Whether the stack has something to write: a pending change on a row that was read.</summary>
    public bool HasWorkToWrite => HasPendingChanges && CanWrite;

    /// <summary>
    /// What the write means for the rest of the editor. A layer with two effects is a row the one-command-per-resource
    /// editors refuse, so the author is told which surface still owns that layer rather than meeting it as a read
    /// error on another page.
    /// </summary>
    public string Consequence()
    {
        foreach (EffectLayer layer in CameraLayers.All)
        {
            int count = CountOf(layer);
            if (count > 1)
                return $"本层现有 {count} 段{layer.Label}，一次一条指令的编辑器不再适合改这一层。";
        }

        return "播放这一句就能看到叠加结果。";
    }

    /// <summary>Re-selects something sensible after a removal, so an action never acts on a vanished row.</summary>
    private void KeepSelectionValid()
    {
        int total = CountOf(SelectedLayer);
        SelectedIndex = total == 0 ? -1 : Math.Clamp(SelectedIndex < 0 ? 0 : SelectedIndex, 0, total - 1);
    }

    private CameraLayerSegment? RowAt(EffectLayer layer, int index) =>
        index < 0 ? null : RowAt(PositionOf(layer, index));

    private CameraLayerSegment? RowAt(int position) =>
        position >= 0 && position < _rows.Count ? _rows[position] : null;

    /// <summary>Where a row of one layer sits in the interleaved list, or -1 when the layer has no such row.</summary>
    private int PositionOf(EffectLayer layer, int index)
    {
        int seen = 0;
        for (int position = 0; position < _rows.Count; position++)
        {
            if (!string.Equals(_rows[position].LayerKey, layer.ResourceKey, StringComparison.Ordinal)) continue;
            if (seen == index) return position;
            seen++;
        }

        return -1;
    }

    private static EffectLayer LayerOf(string layerKey)
    {
        foreach (EffectLayer layer in CameraLayers.All)
        {
            if (string.Equals(layer.ResourceKey, layerKey, StringComparison.Ordinal)) return layer;
        }

        return EffectLayerCatalog.Camera;
    }

    /// <summary>A row label with its place in the layer, for a log line or a test.</summary>
    public string Describe()
    {
        var text = new System.Text.StringBuilder();
        text.Append(Status).Append(" · 待应用=").Append(HasPendingChanges ? "是" : "否");
        foreach (EffectLayer layer in CameraLayers.All)
        {
            text.Append(" · ").Append(layer.ResourceKey).Append('=').Append(CountOf(layer).ToString(CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }
}
