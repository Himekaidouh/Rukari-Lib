using System.Globalization;
using System.Text;
using AzureArchive.VideoTools.Core.Cameras;
using AzureArchive.VideoTools.Core.Characters;
using AzureArchive.VideoTools.Core.Results;
using Rukari.Lib.Tools;

namespace AzureArchive.VideoTools.Core.Commands;

/// <summary>
/// One effect inside one camera layer, as the window lists it (2026-10-13).
///
/// <para>
/// It carries the directive twice on purpose: <paramref name="PublicDirective"/> is what the document holds and what
/// gets written back, while <paramref name="CanonicalDirective"/> is what the planner and the camera parser read.
/// Converting between them is the extractor's job, and doing it once here is what keeps the row the author reads and
/// the row the game plays the same row.
/// </para>
/// </summary>
public sealed record CameraLayerSegment(
    string LayerKey,
    string LayerLabel,
    int IndexInLayer,
    string PublicDirective,
    string CanonicalDirective,
    string Label);

/// <summary>The camera layers of the selected row, in the order the window shows them.</summary>
public sealed record CameraLayerRead(
    IReadOnlyList<CameraLayerSegment> Segments,
    string RowLabel,
    string RevisionSha256,
    string SelectionToken,
    string Error)
{
    /// <summary>No row could be read. An unreadable row is not an empty row, and the window says which it is.</summary>
    public static CameraLayerRead Failed(string error) =>
        new(Array.Empty<CameraLayerSegment>(), string.Empty, string.Empty, string.Empty, error ?? string.Empty);

    /// <summary>Whether a row was read. An empty camera layer on a readable row is a different thing entirely.</summary>
    public bool Available => Error.Length == 0;

    /// <summary>
    /// Reads the two camera layers out of a row's raw additional-prompt text. The extractor accepts several
    /// directives for one resource in authored order — the editor's document model does not — so a layer with two
    /// effects in it reads back here exactly as playback will run it.
    /// </summary>
    public static CameraLayerRead From(
        string? additionalPrompt,
        string revisionSha256,
        string selectionToken,
        string rowLabel)
    {
        EmbeddedAavtExtraction extraction =
            new EmbeddedAavtDirectiveExtractor().Extract(additionalPrompt ?? string.Empty);
        if (extraction.Errors.Count != 0)
        {
            return Failed("当前台词里有无法解析的指令："
                + ToolDrawerLayout.FitSingleLine(string.Join(" | ", extraction.Errors), 15, 420f));
        }

        var segments = new List<CameraLayerSegment>();
        foreach (EffectLayer layer in CameraLayers.All)
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

                segments.Add(new CameraLayerSegment(
                    layer.ResourceKey,
                    layer.Label,
                    index++,
                    EditorCommandDocumentComposer.ToPublicDirective(command.CanonicalDirective),
                    command.CanonicalDirective,
                    CameraLayerLabel.Of(command.CanonicalDirective)));
            }
        }

        return new CameraLayerRead(
            segments,
            rowLabel ?? string.Empty,
            revisionSha256 ?? string.Empty,
            selectionToken ?? string.Empty,
            string.Empty);
    }
}

/// <summary>The camera the picture editor is publishing right now: what a new segment would be built from.</summary>
public readonly record struct CameraDraft(
    string LayerKey,
    string PublicDirective,
    string CanonicalDirective,
    string Label,
    string Error)
{
    /// <summary>No draft is active; the reason is shown instead of an empty button.</summary>
    public static CameraDraft None(string error) => new(string.Empty, string.Empty, string.Empty, string.Empty, error);

    public bool Available => Error.Length == 0 && PublicDirective.Length != 0;

    /// <summary>
    /// Reads a draft out of the camera directive the picture editor publishes, and refuses anything that is not a
    /// camera effect or that this editor cannot write back (2026-10-13).
    ///
    /// <para>
    /// Both spellings are accepted, because the two callers use different ones: the editor publishes the public form
    /// (<c>#aavt;camera;set;…</c>, the form the document holds) while a plan or a hand-written card may carry the
    /// canonical one (<c>#camera;set;…</c>). The document form is tried first — it is what the extractor reads — and
    /// the canonical one is converted rather than parsed separately, so both end up as the same pair of strings.
    /// </para>
    ///
    /// <para>
    /// The layer comes from the extractor rather than from the text: "scope=background" is the extractor's business,
    /// and an editor that guessed it from a string would put a background camera into the whole-picture layer the
    /// first time the directive form changed.
    /// </para>
    /// </summary>
    public static CameraDraft FromDirective(string? directive)
    {
        if (string.IsNullOrWhiteSpace(directive))
            return None("还没有镜头草稿：先在画面编辑器里调好镜头。");

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
                return None("当前镜头草稿不是一条可写入的镜头指令。");
            }

            extraction = new EmbeddedAavtDirectiveExtractor().Extract(text);
            if (extraction.Errors.Count != 0 || extraction.Commands.Count != 1)
                return None("当前镜头草稿不是一条可写入的镜头指令。");
        }

        EmbeddedAavtCommand command = extraction.Commands[0];
        string layerKey = CommandResourceIdentity.KeyFor(command);
        if (!CameraLayers.Contains(layerKey))
            return None("当前草稿不是镜头效果，无法加入镜头层。");

        return new CameraDraft(
            layerKey,
            EditorCommandDocumentComposer.ToPublicDirective(command.CanonicalDirective),
            command.CanonicalDirective,
            CameraLayerLabel.Of(command.CanonicalDirective),
            string.Empty);
    }
}

/// <summary>One layer as it should be written: every one of its lines, in order. An empty list removes the layer.</summary>
public sealed record CameraLayerEdit(string LayerKey, IReadOnlyList<string> Directives);

/// <summary>What one write did: either the row as it now reads, or why nothing was written.</summary>
public readonly record struct CameraLayerApply(bool Success, string Error, CameraLayerRead Read)
{
    public static CameraLayerApply Ok(CameraLayerRead read) => new(true, string.Empty, read);

    public static CameraLayerApply Failed(string error) => new(false, error ?? string.Empty, CameraLayerRead.Failed(error ?? string.Empty));
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

/// <summary>
/// A short, human label for one camera directive: what it does, how long it takes, and any authored placement.
///
/// <para>
/// The window exists so an author can stack effects in order, so a row has to say what the effect <em>is</em> without
/// making the author decode a directive. It is a label, not a second source of truth: everything it prints is read
/// back out of the directive itself, and a directive that cannot be parsed shows as itself rather than as a guess.
/// </para>
/// </summary>
public static class CameraLayerLabel
{
    /// <summary>Widest label a row gets before it is cut, in panel units.</summary>
    private const float RowLabelWidth = 330f;

    /// <summary>Font size the panel draws a row label at; used to measure the cut.</summary>
    public const int RowLabelSize = 15;

    public static string Of(string? canonicalDirective)
    {
        if (string.IsNullOrWhiteSpace(canonicalDirective)) return string.Empty;
        Result<SceneCameraCommand> parsed = new SceneCameraDirectiveParser().Parse(canonicalDirective);
        if (!parsed.Success || parsed.Value is null)
            return ToolDrawerLayout.FitSingleLine(canonicalDirective, RowLabelSize, RowLabelWidth);

        SceneCameraCommand command = parsed.Value;
        var text = new StringBuilder(Operation(command.Operation));
        AppendFields(text, command);
        if (command.DurationMilliseconds > 0) text.Append(" · ").Append(command.DurationMilliseconds).Append("ms");
        text.Append(" · ").Append(Easing(command.Easing));
        // An authored placement is shown rather than hidden: a window that reorders effects must not imply that an
        // effect it did not write has no timing of its own.
        if (command.Placement.HasOffset)
            text.Append(" · at=").Append(command.Placement.AtMilliseconds).Append("ms");
        if (command.Placement.Interrupt) text.Append(" · 接管");
        return ToolDrawerLayout.FitSingleLine(text.ToString(), RowLabelSize, RowLabelWidth);
    }

    private static void AppendFields(StringBuilder text, SceneCameraCommand command)
    {
        var fields = new List<string>(4);
        if (command.Operation == SceneCameraOperation.Move)
        {
            Add(fields, "Δx", command.DeltaX);
            Add(fields, "Δy", command.DeltaY);
            Add(fields, "Δ缩放", command.DeltaZoom);
        }
        else
        {
            Add(fields, "x", command.X);
            Add(fields, "y", command.Y);
            Add(fields, "缩放", command.Zoom);
        }

        foreach (string field in fields) text.Append(' ').Append(field);
    }

    private static void Add(List<string> fields, string name, float? value)
    {
        if (!value.HasValue || !float.IsFinite(value.Value)) return;
        fields.Add(name + "=" + value.Value.ToString("0.###", CultureInfo.InvariantCulture));
    }

    private static string Operation(SceneCameraOperation operation) => operation switch
    {
        SceneCameraOperation.Set => "设置",
        SceneCameraOperation.Move => "移动",
        SceneCameraOperation.Reset => "复位",
        _ => "镜头"
    };

    private static string Easing(CharacterTransformEasing easing) => easing switch
    {
        CharacterTransformEasing.Linear => "匀速",
        CharacterTransformEasing.EaseIn => "渐快",
        CharacterTransformEasing.EaseOut => "渐慢",
        CharacterTransformEasing.EaseInOut => "缓入缓出",
        _ => easing.ToString()
    };
}
