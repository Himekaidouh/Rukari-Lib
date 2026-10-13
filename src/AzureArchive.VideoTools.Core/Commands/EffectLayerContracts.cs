using System.Globalization;
using System.Text;
using AzureArchive.VideoTools.Core.Cameras;
using AzureArchive.VideoTools.Core.Characters;
using AzureArchive.VideoTools.Core.Results;
using Rukari.Lib.Tools;

namespace AzureArchive.VideoTools.Core.Commands;

/// <summary>
/// One effect inside one layer, as the window that owns it lists it (2026-10-13).
///
/// <para>
/// It carries the directive twice on purpose: <paramref name="PublicDirective"/> is what the document holds and what
/// gets written back, while <paramref name="CanonicalDirective"/> is what the planner and the family parser read.
/// Converting between them is the extractor's job, and doing it once here is what keeps the row the author reads and
/// the row the game plays the same row.
/// </para>
/// </summary>
public sealed record EffectLayerSegment(
    string LayerKey,
    string LayerLabel,
    int IndexInLayer,
    string PublicDirective,
    string CanonicalDirective,
    string Label);

/// <summary>The layers a window owns on the selected row, in the order the window shows them.</summary>
public sealed record EffectLayerRead(
    IReadOnlyList<EffectLayerSegment> Segments,
    string RowLabel,
    string RevisionSha256,
    string SelectionToken,
    string Error)
{
    /// <summary>No row could be read. An unreadable row is not an empty row, and the window says which it is.</summary>
    public static EffectLayerRead Failed(string error) =>
        new(Array.Empty<EffectLayerSegment>(), string.Empty, string.Empty, string.Empty, error ?? string.Empty);

    /// <summary>Whether a row was read. An empty layer on a readable row is a different thing entirely.</summary>
    public bool Available => Error.Length == 0;

    /// <summary>
    /// Reads the given layers out of a row's raw additional-prompt text. The extractor accepts several directives for
    /// one resource in authored order — the editor's document model does not — so a layer with two effects in it reads
    /// back here exactly as playback will run it.
    /// </summary>
    public static EffectLayerRead From(
        IReadOnlyList<EffectLayer> layers,
        string? additionalPrompt,
        string revisionSha256,
        string selectionToken,
        string rowLabel)
    {
        ArgumentNullException.ThrowIfNull(layers);
        EmbeddedAavtExtraction extraction =
            new EmbeddedAavtDirectiveExtractor().Extract(additionalPrompt ?? string.Empty);
        if (extraction.Errors.Count != 0)
        {
            return Failed("当前台词里有无法解析的指令："
                + ToolDrawerLayout.FitSingleLine(string.Join(" | ", extraction.Errors), 15, 420f));
        }

        var segments = new List<EffectLayerSegment>();
        foreach (EffectLayer layer in layers)
        {
            int index = 0;
            foreach (EmbeddedAavtCommand command in extraction.Commands)
            {
                if (!string.Equals(
                        CommandResourceIdentity.KeyFor(command),
                        layer.ResourceKey,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                segments.Add(new EffectLayerSegment(
                    layer.ResourceKey,
                    layer.Label,
                    index++,
                    EditorCommandDocumentComposer.ToPublicDirective(command.CanonicalDirective),
                    command.CanonicalDirective,
                    EffectDirectiveLabel.Of(command.CanonicalDirective)));
            }
        }

        return new EffectLayerRead(
            segments,
            rowLabel ?? string.Empty,
            revisionSha256 ?? string.Empty,
            selectionToken ?? string.Empty,
            string.Empty);
    }
}

/// <summary>
/// One effect an editor has composed but not written yet: the draft a layer's 「＋ 加为下一段」 would add (2026-10-13).
///
/// <para>
/// It carries the directive twice for the same reason a row does, and it decides its own layer through the extractor:
/// which scope a camera has, or which slot a preset names, is the extractor's business, and an editor that guessed it
/// from a string would file the effect under the wrong layer the first time a directive form changed. Whether the
/// layer belongs to the window asking is decided by the stack that receives it, so this type stays about the
/// directive alone.
/// </para>
/// </summary>
public readonly record struct EffectDraft(
    string LayerKey,
    string PublicDirective,
    string CanonicalDirective,
    string Label,
    string Error)
{
    /// <summary>No draft is active; the reason is shown instead of an empty button.</summary>
    public static EffectDraft None(string error) => new(string.Empty, string.Empty, string.Empty, string.Empty, error);

    public bool Available => Error.Length == 0 && PublicDirective.Length != 0;

    /// <summary>
    /// Reads a draft out of the directive an editor publishes, and refuses anything this panel cannot write back.
    ///
    /// <para>
    /// Both spellings are accepted, because the callers use different ones: an editor publishes the public form
    /// (<c>#aavt;camera;set;…</c>, the form the document holds) while a plan or a hand-written card may carry the
    /// canonical one (<c>#camera;set;…</c>). The document form is tried first — it is what the extractor reads — and
    /// the canonical one is converted rather than parsed separately, so both end up as the same pair of strings.
    /// </para>
    /// </summary>
    public static EffectDraft FromDirective(string? directive)
    {
        if (string.IsNullOrWhiteSpace(directive))
            return None("还没有可加入的效果草稿：先在编辑器里调好它。");

        string text = directive.Trim();
        EmbeddedAavtExtraction extraction = new EmbeddedAavtDirectiveExtractor().Extract(text);
        if (extraction.Errors.Count != 0 || extraction.Commands.Count != 1)
        {
            try
            {
                text = EditorCommandDocumentComposer.ToPublicDirective(text);
            }
            catch (Exception)
            {
                return None("当前草稿不是一条可写入的效果指令。");
            }

            extraction = new EmbeddedAavtDirectiveExtractor().Extract(text);
            if (extraction.Errors.Count != 0 || extraction.Commands.Count != 1)
                return None("当前草稿不是一条可写入的效果指令。");
        }

        EmbeddedAavtCommand command = extraction.Commands[0];
        string layerKey = CommandResourceIdentity.KeyFor(command);
        if (EffectLayerCatalog.ForResourceKey(layerKey) is not EffectLayer layer)
            return None("当前草稿不属于任何一个可以叠加的效果层。");

        return new EffectDraft(
            layerKey,
            EditorCommandDocumentComposer.ToPublicDirective(command.CanonicalDirective),
            command.CanonicalDirective,
            EffectDirectiveLabel.Of(command.CanonicalDirective),
            string.Empty);
    }
}

/// <summary>One layer as it should be written: every one of its lines, in order. An empty list removes the layer.</summary>
public sealed record EffectLayerEdit(string LayerKey, IReadOnlyList<string> Directives);

/// <summary>
/// What one write did: either the row as it now reads, or why nothing was written.
/// <para>
/// <paramref name="Queue"/> is what the write made of the whole row's queue — the 串成一条 / 各层并行 mode — and is
/// null when the write did not get as far as planning one. A window owns one layer, so the offsets in it are the only
/// part of a row-wide decision that window could have made; reporting the plan is how the author sees the other half.
/// </para>
/// </summary>
public readonly record struct EffectLayerApply(
    bool Success,
    string Error,
    EffectLayerRead Read,
    EffectQueuePlan? Queue = null)
{
    public static EffectLayerApply Ok(EffectLayerRead read, EffectQueuePlan? queue = null) =>
        new(true, string.Empty, read, queue);

    public static EffectLayerApply Failed(string error) => new(false, error ?? string.Empty, EffectLayerRead.Failed(error ?? string.Empty));
}

/// <summary>The camera layers this window owns, in the order the window shows them.</summary>
public static class CameraLayers
{
    public static IReadOnlyList<EffectLayer> All { get; } = new[]
    {
        EffectLayerCatalog.Camera,
        EffectLayerCatalog.BackgroundCamera
    };

    /// <summary>Whether a resource key is one of the camera layers.</summary>
    public static bool Contains(string? layerKey)
    {
        if (string.IsNullOrEmpty(layerKey)) return false;
        foreach (EffectLayer layer in All)
        {
            if (string.Equals(layer.ResourceKey, layerKey, StringComparison.Ordinal)) return true;
        }

        return false;
    }
}

