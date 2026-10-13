using System.Diagnostics;

namespace AzureArchive.VideoTools.Core.Commands;

/// <summary>
/// One surface's layer editing: which layers it owns, when it re-reads them, what a write sends, and what its rows and
/// buttons say (2026-10-13).
///
/// <para>
/// It exists because the same editing was written three times — the camera mode, the character mode and the preset
/// page — and the third copy is where a policy quietly diverges. Everything that decides <em>when</em> to re-read and
/// <em>what</em> a press does lives here; the game-side calls are injected, because Core cannot reach the editor
/// services and a policy that must be trusted should be testable without a game.
/// </para>
///
/// <para>
/// The stack itself stays <see cref="EffectLayerStack"/>: this type adds the read gate, the write round trip and the
/// words, not another model of the same data.
/// </para>
/// </summary>
public sealed class EffectLayerEditor
{
    /// <summary>How often a surface re-reads the row while it has nothing pending.</summary>
    public static readonly long ReadInterval = Stopwatch.Frequency / 4;

    private readonly Func<IReadOnlyList<EffectLayer>, EffectLayerRead> _read;
    private readonly Func<string, string, IReadOnlyList<EffectLayerEdit>, EffectLayerApply> _write;
    private readonly Func<string?> _draft;
    private readonly Func<long> _clock;
    private readonly string _noun;
    private long _lastRead;
    private bool _neverRead = true;

    /// <summary>
    /// Creates an editor over the layers one surface owns.
    /// <paramref name="read"/> reads those layers out of the selected row, <paramref name="write"/> commits one edit
    /// per layer in a single transaction, <paramref name="draft"/> is the effect the surface has composed but not
    /// written, and <paramref name="noun"/> names one effect in the surface's own words ("镜头段", "角色段").
    /// </summary>
    public EffectLayerEditor(
        IReadOnlyList<EffectLayer> layers,
        Func<IReadOnlyList<EffectLayer>, EffectLayerRead> read,
        Func<string, string, IReadOnlyList<EffectLayerEdit>, EffectLayerApply> write,
        Func<string?> draft,
        string noun,
        Func<long>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(layers);
        Stack = new EffectLayerStack(layers);
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _write = write ?? throw new ArgumentNullException(nameof(write));
        _draft = draft ?? throw new ArgumentNullException(nameof(draft));
        _noun = string.IsNullOrWhiteSpace(noun) ? "段" : noun;
        _clock = clock ?? Stopwatch.GetTimestamp;
    }

    /// <summary>The order of the effects, and what is selected.</summary>
    public EffectLayerStack Stack { get; }

    /// <summary>The layers this surface owns, in the order it shows them.</summary>
    public IReadOnlyList<EffectLayer> Layers => Stack.Layers;

    /// <summary>The layer a read failure is reported on, and the one a surface shows by default.</summary>
    public EffectLayer PrimaryLayer => Layers[0];

    /// <summary>Whether the stack on screen differs from the row it was read from.</summary>
    public bool HasPendingChanges => Stack.HasPendingChanges;

    /// <summary>Whether the layer holds anything at all.</summary>
    public bool HasSegments => Stack.Rows.Count != 0;

    /// <summary>What the surface says about itself: the last action, or the state of the row.</summary>
    public string Status => Stack.Status;

    /// <summary>What a segment button reads: which effect the buttons act on, out of how many in its layer.</summary>
    public string SegmentLabel => $"{_noun} {Math.Max(0, Stack.SelectedIndex + 1)}/{SelectedLayerCount}";

    /// <summary>What a delete button reads, naming the effect it would remove.</summary>
    public string DeleteLabel => Stack.SelectedIndex >= 0 ? $"删除第 {Stack.SelectedIndex + 1} 段" : "删除这段";

    private int SelectedLayerCount => Stack.CountOf(Stack.SelectedLayer);

    /// <summary>The one line a surface shows about one of its layers, draft included.</summary>
    public string LineOf(EffectLayer layer) => Stack.LineOf(layer);

    /// <summary>How many effects one layer holds right now.</summary>
    public int CountOf(EffectLayer layer) => Stack.CountOf(layer);

    /// <summary>One layer's writable directives, in the order they play.</summary>
    public IReadOnlyList<string> DirectivesOf(EffectLayer layer) => Stack.DirectivesOf(layer);

    /// <summary>The layer the buttons act on; a surface with several layers sets this from its own selector.</summary>
    public EffectLayer SelectedLayer => Stack.SelectedLayer;

    /// <summary>
    /// Points the buttons at one of this surface's layers. A surface that shows a scope selector calls this when it
    /// changes, so a row index can never point into the layer the author is not looking at.
    /// </summary>
    public void SelectLayer(EffectLayer layer) => Stack.Select(layer, Stack.SelectedIndex);

    /// <summary>
    /// Re-reads the row at a walk, and never on top of work the author has not applied: a re-read would silently
    /// replace the stack being built. <paramref name="force"/> is what a page uses when it is shown again.
    /// </summary>
    public bool Refresh(bool force = false)
    {
        long now = _clock();
        if (!force && !_neverRead)
        {
            if (Stack.HasPendingChanges) return false;
            if (now - _lastRead < ReadInterval) return false;
        }

        _lastRead = now;
        _neverRead = false;
        Stack.Adopt(_read(Layers));
        return true;
    }

    /// <summary>Appends the composed draft to its own layer, or says why there is nothing to append.</summary>
    public bool Add()
    {
        string? directive = _draft();
        return Stack.Add(EffectDraft.FromDirective(directive));
    }

    /// <summary>Moves the selection to the next effect of the layer the buttons act on.</summary>
    public void Cycle()
    {
        int total = SelectedLayerCount;
        if (total == 0)
        {
            Stack.Note($"{Stack.SelectedLayer.Label}还没有效果。");
            return;
        }

        int next = Stack.SelectedIndex < 0 ? 0 : (Stack.SelectedIndex + 1) % total;
        Stack.Select(Stack.SelectedLayer, next);
        if (Stack.HasPendingChanges) Stack.Note($"第 {next + 1} 段：{Stack.Selected?.Label}");
    }

    /// <summary>Whether the selected effect can move one place within the layer the buttons act on.</summary>
    public bool CanMove(int delta)
    {
        int index = Stack.SelectedIndex;
        return index >= 0 && index + delta >= 0 && index + delta < SelectedLayerCount;
    }

    /// <summary>Moves the selected effect one place within its layer, or says why it cannot.</summary>
    public bool Move(int delta) => Stack.MoveSelected(delta);

    /// <summary>Removes the selected effect, as a pending change.</summary>
    public bool Delete() => Stack.DeleteSelected();

    /// <summary>Empties one layer, as a pending change: 应用 is still what writes it.</summary>
    public bool Clear(EffectLayer layer) => Stack.Clear(layer);

    /// <summary>
    /// Writes every layer this surface owns in one transaction, guarded by the token and the revision the stack was
    /// read from, and takes the write's own readback as the new truth.
    /// <para>
    /// It returns null when there is nothing to write — including when the row could not be read, which is a state the
    /// caller must be able to tell apart from a failed write.
    /// </para>
    /// </summary>
    public EffectLayerApply? Apply()
    {
        if (!Stack.HasPendingChanges)
        {
            Stack.Note("没有需要写入的改动。");
            return null;
        }

        if (!Stack.CanWrite)
        {
            Stack.Note("这一句还没有读取成功，请重新选中台词后再试。");
            return null;
        }

        EffectLayerApply applied = _write(Stack.RevisionSha256, Stack.SelectionToken, Stack.BuildEdits());
        if (!applied.Success)
        {
            Stack.Note("写入失败：" + applied.Error);
            return applied;
        }

        // The queue line is part of what the write did, not decoration: in 串成一条 mode it is the only place an author
        // sees when the effects of the layers they are not looking at will start.
        string message = "已写入当前句；" + Stack.Consequence();
        if (applied.Queue is { Success: true } queue) message += "　" + queue.Line;
        Stack.Adopt(applied.Read, message);
        return applied;
    }

    /// <summary>One line per distinct state, so the numbers a surface shows can be checked from a log.</summary>
    public string Describe() => Stack.Describe();
}
