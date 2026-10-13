using System.Security.Cryptography;
using System.Text;
using AzureArchive.VideoTools.Core.Results;
using AzureArchive.VideoTools.Core.VisualEditor;
using AzureArchive.VideoTools.Core.Voices;

namespace AzureArchive.VideoTools.Core.Commands;

public sealed record EditorCommandDocument(
    string Text,
    string RevisionSha256,
    int OfficialLineCount,
    int AavtLineCount,
    bool HasContinueDirective,
    bool HasClearScreenTextDirective,
    IReadOnlyList<EmbeddedAavtCommand> Commands,
    IReadOnlyList<ScreenTextLine> ScreenTextLines)
{
    /// <summary>
    /// How this row asked to be laid out: the <c>#aavt;queue</c> marker, or independent tracks when there is none.
    /// </summary>
    public EffectQueueMode QueueMode { get; init; } = EffectQueueMode.IndependentTracks;
}

public sealed record EditorCommandEditPreview(
    string ExpectedRevisionSha256,
    string ResultRevisionSha256,
    string UpdatedText,
    string CanonicalPublicDirective,
    int PublicSlot,
    bool ReplacedExisting);

public sealed record EditorCommandContinueEdit(
    string ExpectedRevisionSha256,
    string ResultRevisionSha256,
    string UpdatedText,
    bool ContinueEnabled,
    bool AlreadyPresent);

/// <summary>The row's queue marker after one toggle: what it now says, and whether it already said it.</summary>
public sealed record EditorCommandQueueEdit(
    string ExpectedRevisionSha256,
    string ResultRevisionSha256,
    string UpdatedText,
    EffectQueueMode QueueMode,
    bool AlreadyPresent);

/// <summary>The standalone marker line this build writes for <see cref="EffectQueueMode.OneQueue"/>.</summary>
public static class EffectQueueMarker
{
    public const string Directive = "#aavt;" + EmbeddedAavtDirectiveExtractor.QueueMarkerNamespace;

    /// <summary>The mode a document is in, read back from the line itself.</summary>
    public static EffectQueueMode Read(string? additionalPrompt) =>
        new EmbeddedAavtDirectiveExtractor().Extract(additionalPrompt ?? string.Empty).QueueMode;
}

public sealed record EditorCommandScreenTextEdit(
    string ExpectedRevisionSha256,
    string ResultRevisionSha256,
    string UpdatedText,
    ScreenTextDirective? Directive,
    bool ReplacedExisting,
    bool AlreadyPresent);

public sealed record EditorCommandClearScreenTextEdit(
    string ExpectedRevisionSha256,
    string ResultRevisionSha256,
    string UpdatedText,
    bool AlreadyPresent);

public sealed record EditorCommandVoiceEdit(
    string ExpectedRevisionSha256,
    string ResultRevisionSha256,
    string UpdatedText,
    string? ResourceKey,
    bool ReplacedExisting,
    bool AlreadyPresent);

/// <summary>
/// The result of replacing one whole resource layer: the layer's lines are now the given ones, in the given order,
/// and every other line of the document is untouched (2026-10-13).
///
/// <para>
/// This is a record of its own rather than an <see cref="EditorCommandEditPreview"/> because that one carries a
/// single directive and a single slot. A layer may hold several effects, and the window that wrote them has to be
/// able to see exactly what it now holds.
/// </para>
/// </summary>
public sealed record EditorCommandLayerEdit(
    string ExpectedRevisionSha256,
    string ResultRevisionSha256,
    string UpdatedText,
    string ResourceKey,
    IReadOnlyList<string> CanonicalPublicDirectives,
    bool ReplacedExisting);

public sealed class EditorCommandDocumentComposer
{
    private readonly EmbeddedAavtDirectiveExtractor _extractor = new();

    /// <summary>
    /// The document as a one-command-per-resource editor sees it: a text that declares two directives for one
    /// resource is refused. Every writer that replaces or removes a single resource line goes through this rule.
    /// </summary>
    public Result<EditorCommandDocument> Read(
        string text,
        bool acceptLegacyCharAlias = true) =>
        ReadDocument(text, acceptLegacyCharAlias, allowMultipleResourcesPerResource: false);

    /// <summary>
    /// The same document read for display, and for edits that do not touch resource directives (2026-10-13).
    ///
    /// <para>
    /// A resource with several directives is an ordinary row now — the overlay model's whole point is that a window
    /// stacks effects inside its own layer — so a reader that refuses such a row cannot be the one a page uses to
    /// show the author what is on the line. The strict <see cref="Read"/> keeps its guarantee for the writers that
    /// still edit one command per resource; a writer that would have to choose between two lines of the resource it
    /// is writing refuses them explicitly instead of picking one.
    /// </para>
    /// </summary>
    public Result<EditorCommandDocument> ReadAllowingMultipleResources(
        string text,
        bool acceptLegacyCharAlias = true) =>
        ReadDocument(text, acceptLegacyCharAlias, allowMultipleResourcesPerResource: true);

    private Result<EditorCommandDocument> ReadDocument(
        string text,
        bool acceptLegacyCharAlias,
        bool allowMultipleResourcesPerResource)
    {
        ArgumentNullException.ThrowIfNull(text);

        EmbeddedAavtExtraction extracted = _extractor.Extract(
            text,
            acceptLegacyCharAlias);
        if (extracted.Errors.Count != 0)
        {
            return Result<EditorCommandDocument>.Fail(
                "Existing additional-prompt document contains invalid AAVT directives: "
                + string.Join(" | ", extracted.Errors));
        }

        // The extractor accepts several directives for one slot, because playback queues them in authored order and
        // the layer editors write them on purpose. This document model stays one-command-per-resource for the writers
        // that address a resource by key: two commands for one key would make "replace" and "remove" ambiguous, and
        // they say so rather than silently changing only the first line.
        if (!allowMultipleResourcesPerResource)
        {
            var documentKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (EmbeddedAavtCommand command in extracted.Commands)
            {
                if (!documentKeys.Add(CommandResourceIdentity.KeyFor(command)))
                {
                    return Result<EditorCommandDocument>.Fail(
                        $"Existing additional-prompt document declares more than one directive for "
                        + $"public slot {command.PublicSlot}; the visual editor edits one command per resource.");
                }
            }
        }

        Result<IReadOnlyList<ScreenTextLine>> screenText =
            ScreenTextDirectiveCodec.ReadDocument(text);
        if (!screenText.Success || screenText.Value == null)
        {
            return Result<EditorCommandDocument>.Fail(screenText.Error);
        }

        int lineCount = CountLines(text);
        return Result<EditorCommandDocument>.Ok(new EditorCommandDocument(
            text,
            Revision(text),
            Math.Max(0, lineCount - extracted.RemovedLineCount),
            extracted.RemovedLineCount,
            extracted.HasContinueDirective,
            ContainsStandaloneLine(text, "#clearST"),
            extracted.Commands,
            screenText.Value)
        {
            QueueMode = extracted.QueueMode
        });
    }

    public Result<EditorCommandEditPreview> PreviewUpsert(
        string sourceText,
        string expectedRevisionSha256,
        string directive,
        bool acceptLegacyCharAlias = true)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        ArgumentNullException.ThrowIfNull(expectedRevisionSha256);
        ArgumentNullException.ThrowIfNull(directive);

        Result<EditorCommandDocument> current = ReadDocument(
            sourceText,
            acceptLegacyCharAlias,
            allowMultipleResourcesPerResource: true);
        if (!current.Success || current.Value == null)
        {
            return Result<EditorCommandEditPreview>.Fail(current.Error);
        }

        if (!string.Equals(
                current.Value.RevisionSha256,
                expectedRevisionSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return Result<EditorCommandEditPreview>.Fail(
                "Additional-prompt revision changed after the draft was created.");
        }

        string trimmedDirective = directive.Trim();
        if (trimmedDirective.Length == 0
            || trimmedDirective.Contains('\r')
            || trimmedDirective.Contains('\n'))
        {
            return Result<EditorCommandEditPreview>.Fail(
                "A visual-editor draft must contain exactly one non-empty directive line.");
        }

        EmbeddedAavtExtraction candidate = _extractor.Extract(
            trimmedDirective,
            acceptLegacyCharAlias);
        if (candidate.Errors.Count != 0
            || candidate.RemovedLineCount != 1
            || candidate.Commands.Count != 1
            || candidate.HasContinueDirective)
        {
            string detail = candidate.Errors.Count == 0
                ? "The draft is not exactly one supported AAVT resource directive."
                : string.Join(" | ", candidate.Errors);
            return Result<EditorCommandEditPreview>.Fail(detail);
        }

        EmbeddedAavtCommand command = candidate.Commands[0];
        string resourceKey = CommandResourceIdentity.KeyFor(command);
        // The one rule this writer keeps: it replaces the one line of the resource it names. A layer that already
        // holds several effects is written by its layer editor, and saying so is the only safe answer — replacing
        // "the first one" would silently delete the rest of the author's stack (2026-10-13).
        int existingLines = CountOfResource(current.Value.Commands, resourceKey);
        if (existingLines > 1)
        {
            return Result<EditorCommandEditPreview>.Fail(
                $"资源 {resourceKey} 在这一行里已经有 {existingLines} 段；"
                + "一次一条指令的编辑器无法决定改哪一段，请在负责这一层的窗口里修改。");
        }

        string canonicalPublicDirective = ToPublicDirective(command.CanonicalDirective);
        string updated = ReplaceOrAppend(
            sourceText,
            current.Value.Commands,
            resourceKey,
            canonicalPublicDirective,
            out bool replacedExisting);
        Result<EditorCommandDocument> verified = ReadDocument(
            updated,
            acceptLegacyCharAlias,
            allowMultipleResourcesPerResource: true);
        if (!verified.Success || verified.Value == null)
        {
            return Result<EditorCommandEditPreview>.Fail(
                "Merged additional-prompt document did not validate: " + verified.Error);
        }

        return Result<EditorCommandEditPreview>.Ok(new EditorCommandEditPreview(
            current.Value.RevisionSha256,
            verified.Value.RevisionSha256,
            updated,
            canonicalPublicDirective,
            command.PublicSlot,
            replacedExisting));
    }

    /// <summary>
    /// Removes only the resource identified by one valid public directive. A
    /// character transform and a transient preset on the same slot remain
    /// separate resources; the directive's parameters do not identify the row.
    /// </summary>
    public Result<EditorCommandEditPreview> PreviewRemove(
        string sourceText,
        string expectedRevisionSha256,
        string directive,
        bool acceptLegacyCharAlias = true)
    {
        Result<EditorCommandEditPreview> checkedDraft = PreviewUpsert(
            sourceText, expectedRevisionSha256, directive, acceptLegacyCharAlias);
        if (!checkedDraft.Success || checkedDraft.Value == null)
        {
            return Result<EditorCommandEditPreview>.Fail(checkedDraft.Error);
        }

        EditorCommandEditPreview draft = checkedDraft.Value;
        EmbeddedAavtCommand target = _extractor.Extract(
            draft.CanonicalPublicDirective, acceptLegacyCharAlias).Commands.Single();
        string resourceKey = CommandResourceIdentity.KeyFor(target);
        EmbeddedAavtCommand? existing = _extractor.Extract(sourceText, acceptLegacyCharAlias)
            .Commands.FirstOrDefault(command => CommandResourceIdentity.KeyFor(command) == resourceKey);
        string updated = existing == null ? sourceText : RemoveLine(sourceText, existing.LineNumber);
        Result<EditorCommandDocument> verified = ReadDocument(
            updated,
            acceptLegacyCharAlias,
            allowMultipleResourcesPerResource: true);
        if (!verified.Success || verified.Value == null)
        {
            return Result<EditorCommandEditPreview>.Fail(
                "Additional-prompt document did not validate after resource removal: " + verified.Error);
        }

        return Result<EditorCommandEditPreview>.Ok(new EditorCommandEditPreview(
            draft.ExpectedRevisionSha256,
            verified.Value.RevisionSha256,
            updated,
            draft.CanonicalPublicDirective,
            target.PublicSlot,
            existing != null));
    }

    /// <summary>
    /// Replaces every line of one resource layer with the given directives, in the given order; an empty list removes
    /// the layer. Every other line of the document is left exactly as it was (2026-10-13).
    ///
    /// <para>
    /// The overlay model needs this and nothing more. A window owns a layer and stacks effects inside it, so the
    /// window writes its own resource and cannot reach another window's; the directives are therefore refused unless
    /// they all belong to <paramref name="resourceKey"/>.
    /// </para>
    ///
    /// <para>
    /// It reads and verifies with the extractor rather than with <see cref="Read"/>: that document model refuses a
    /// text which declares two directives for one resource, and a layer holding two effects is exactly the text this
    /// method exists to produce. What <see cref="Read"/> gives elsewhere is enforced here by hand — invalid
    /// documents are refused, the revision must match, every directive must be one supported resource line, and the
    /// result is re-extracted and compared, in order, against both the layer that was asked for and the lines of
    /// every other resource, so a merge that dropped or reordered somebody else's directive cannot be returned as a
    /// success.
    /// </para>
    /// </summary>
    public Result<EditorCommandLayerEdit> PreviewReplaceResource(
        string sourceText,
        string expectedRevisionSha256,
        string resourceKey,
        IReadOnlyList<string> directives,
        bool acceptLegacyCharAlias = true)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        ArgumentNullException.ThrowIfNull(expectedRevisionSha256);
        ArgumentNullException.ThrowIfNull(resourceKey);
        ArgumentNullException.ThrowIfNull(directives);

        EmbeddedAavtExtraction current = _extractor.Extract(sourceText, acceptLegacyCharAlias);
        if (current.Errors.Count != 0)
        {
            return Result<EditorCommandLayerEdit>.Fail(
                "Existing additional-prompt document contains invalid AAVT directives: "
                + string.Join(" | ", current.Errors));
        }

        string revision = Revision(sourceText);
        if (!string.Equals(revision, expectedRevisionSha256, StringComparison.OrdinalIgnoreCase))
        {
            return Result<EditorCommandLayerEdit>.Fail(
                "Additional-prompt revision changed after the draft was created.");
        }

        var publicDirectives = new List<string>(directives.Count);
        foreach (string directive in directives)
        {
            if (directive is null)
                return Result<EditorCommandLayerEdit>.Fail("A layer directive cannot be null.");

            string trimmed = directive.Trim();
            if (trimmed.Length == 0 || trimmed.Contains('\r') || trimmed.Contains('\n'))
            {
                return Result<EditorCommandLayerEdit>.Fail(
                    "A layer directive must be exactly one non-empty line.");
            }

            EmbeddedAavtExtraction candidate = _extractor.Extract(trimmed, acceptLegacyCharAlias);
            if (candidate.Errors.Count != 0
                || candidate.RemovedLineCount != 1
                || candidate.Commands.Count != 1
                || candidate.HasContinueDirective)
            {
                string detail = candidate.Errors.Count == 0
                    ? "A layer directive must be exactly one supported AAVT resource directive."
                    : string.Join(" | ", candidate.Errors);
                return Result<EditorCommandLayerEdit>.Fail(detail);
            }

            EmbeddedAavtCommand command = candidate.Commands[0];
            string key = CommandResourceIdentity.KeyFor(command);
            if (!string.Equals(key, resourceKey, StringComparison.Ordinal))
            {
                // One layer, one writer: a window may not write an effect into somebody else's layer by accident.
                return Result<EditorCommandLayerEdit>.Fail(
                    $"Directive '{trimmed}' belongs to layer '{key}', not to '{resourceKey}'.");
            }

            publicDirectives.Add(ToPublicDirective(command.CanonicalDirective));
        }

        List<string> othersBefore = OtherLayersOf(current.Commands, resourceKey);
        string updated = ReplaceLayerLines(
            sourceText, current.Commands, resourceKey, publicDirectives, out bool replacedExisting);

        EmbeddedAavtExtraction verified = _extractor.Extract(updated, acceptLegacyCharAlias);
        if (verified.Errors.Count != 0)
        {
            return Result<EditorCommandLayerEdit>.Fail(
                "Merged additional-prompt document did not validate: " + string.Join(" | ", verified.Errors));
        }

        List<string> written = LayerOf(verified.Commands, resourceKey);
        if (!written.SequenceEqual(publicDirectives, StringComparer.Ordinal))
        {
            return Result<EditorCommandLayerEdit>.Fail(
                "Merged additional-prompt document did not keep this layer's lines in order.");
        }

        if (!OtherLayersOf(verified.Commands, resourceKey).SequenceEqual(othersBefore, StringComparer.Ordinal))
        {
            return Result<EditorCommandLayerEdit>.Fail(
                "Merged additional-prompt document changed another layer's lines.");
        }

        return Result<EditorCommandLayerEdit>.Ok(new EditorCommandLayerEdit(
            revision,
            Revision(updated),
            updated,
            resourceKey,
            publicDirectives,
            replacedExisting));
    }

    /// <summary>How many lines one resource already holds, for the refusal above and for a caller's own message.</summary>
    public static int CountOfResource(IReadOnlyList<EmbeddedAavtCommand> commands, string resourceKey)
    {
        ArgumentNullException.ThrowIfNull(commands);
        int count = 0;
        foreach (EmbeddedAavtCommand command in commands)
        {
            if (string.Equals(CommandResourceIdentity.KeyFor(command), resourceKey, StringComparison.Ordinal))
                count++;
        }

        return count;
    }

    /// <summary>The public form of one layer's directives, in authored order.</summary>
    private static List<string> LayerOf(IReadOnlyList<EmbeddedAavtCommand> commands, string resourceKey)
    {
        var lines = new List<string>();
        foreach (EmbeddedAavtCommand command in commands)
        {
            if (string.Equals(CommandResourceIdentity.KeyFor(command), resourceKey, StringComparison.Ordinal))
                lines.Add(ToPublicDirective(command.CanonicalDirective));
        }

        return lines;
    }

    /// <summary>
    /// Every other layer's directives, keyed by their resource, in authored order. Line numbers are deliberately not
    /// part of it: writing a layer moves the lines below it, and a check that failed on that would fail every time.
    /// </summary>
    private static List<string> OtherLayersOf(IReadOnlyList<EmbeddedAavtCommand> commands, string resourceKey)
    {
        var lines = new List<string>();
        foreach (EmbeddedAavtCommand command in commands)
        {
            string key = CommandResourceIdentity.KeyFor(command);
            if (string.Equals(key, resourceKey, StringComparison.Ordinal)) continue;
            lines.Add(key + " " + ToPublicDirective(command.CanonicalDirective));
        }

        return lines;
    }

    /// <summary>
    /// Swaps the layer's lines for <paramref name="replacements"/> where the first of them stood, so the layer stays
    /// together in the text and a reader sees the effects in the order they will play. Lines of other resources keep
    /// their own bytes and their own line terminators.
    /// </summary>
    private static string ReplaceLayerLines(
        string source,
        IReadOnlyList<EmbeddedAavtCommand> existingCommands,
        string resourceKey,
        IReadOnlyList<string> replacements,
        out bool replacedExisting)
    {
        var layerLines = new HashSet<int>();
        foreach (EmbeddedAavtCommand command in existingCommands)
        {
            if (string.Equals(CommandResourceIdentity.KeyFor(command), resourceKey, StringComparison.Ordinal))
                layerLines.Add(command.LineNumber);
        }

        replacedExisting = layerLines.Count != 0;
        var output = new StringBuilder(source.Length + 64);
        bool emitted = false;
        int offset = 0;
        int lineNumber = 1;
        while (offset < source.Length)
        {
            int contentEnd = offset;
            while (contentEnd < source.Length && source[contentEnd] != '\r' && source[contentEnd] != '\n')
                contentEnd++;

            int segmentEnd = contentEnd;
            if (segmentEnd < source.Length && source[segmentEnd] == '\r') segmentEnd++;
            if (segmentEnd < source.Length && source[segmentEnd] == '\n') segmentEnd++;

            if (layerLines.Contains(lineNumber))
            {
                if (!emitted)
                {
                    emitted = true;
                    for (int index = 0; index < replacements.Count; index++)
                    {
                        output.Append(replacements[index]);
                        // The last replacement inherits the replaced line's own terminator; the ones before it get the
                        // document's preferred newline, so a file that ended without one still ends without one.
                        if (index == replacements.Count - 1)
                            output.Append(source, contentEnd, segmentEnd - contentEnd);
                        else
                            output.Append(PreferredNewLine(source));
                    }
                }
            }
            else
            {
                output.Append(source, offset, segmentEnd - offset);
            }

            offset = segmentEnd;
            lineNumber++;
        }

        if (replacedExisting) return output.ToString();
        for (int index = 0; index < replacements.Count; index++)
        {
            if (output.Length != 0 && output[^1] != '\r' && output[^1] != '\n')
                output.Append(PreferredNewLine(source));
            output.Append(replacements[index]);
        }

        return output.ToString();
    }

    private static string RemoveLine(string source, int lineNumber)
    {
        int offset = 0;
        for (int currentLine = 1; offset < source.Length; currentLine++)
        {
            int end = offset;
            while (end < source.Length && source[end] != '\r' && source[end] != '\n') end++;
            if (end < source.Length && source[end] == '\r') end++;
            if (end < source.Length && source[end] == '\n') end++;
            if (currentLine == lineNumber) return source.Remove(offset, end - offset);
            offset = end;
        }

        return source;
    }

    public static string Revision(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>
    /// Upserts or removes the one voice directive, preserving all unrelated text
    /// and line endings. The resource key comes from the resource catalog: its
    /// actual file extension has already been removed. Invalid source documents,
    /// including malformed or duplicate voice lines, must be corrected first.
    /// </summary>
    public Result<EditorCommandVoiceEdit> SetVoice(
        string sourceText,
        string expectedRevisionSha256,
        string? resourceKey,
        bool acceptLegacyCharAlias = true)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        ArgumentNullException.ThrowIfNull(expectedRevisionSha256);

        Result<EditorCommandDocument> current = ReadDocument(
            sourceText,
            acceptLegacyCharAlias,
            allowMultipleResourcesPerResource: true);
        if (!current.Success || current.Value == null)
        {
            return Result<EditorCommandVoiceEdit>.Fail(current.Error);
        }

        if (!string.Equals(current.Value.RevisionSha256, expectedRevisionSha256, StringComparison.OrdinalIgnoreCase))
        {
            return Result<EditorCommandVoiceEdit>.Fail(
                "Additional-prompt revision changed after the voice draft was created.");
        }

        string? normalizedKey = null;
        string? replacement = null;
        if (resourceKey != null)
        {
            if (!VoiceDirectivePolicy.TryDecodeNativeIdentifier(
                    VoiceDirectivePolicy.NativeIdentifierPrefix + resourceKey.Replace('\\', '/'),
                    out string catalogKey))
            {
                return Result<EditorCommandVoiceEdit>.Fail(
                    "Voice resource key must be a normalized relative catalog key without control characters or additional directives.");
            }

            normalizedKey = catalogKey;
            replacement = "#aavt;voice;" + catalogKey;
            // Catalog stems can legitimately end in .wav/.ogg/.mp3. The text
            // parser removes one audio extension, so retain a disposable suffix
            // in that case to round-trip the catalog key without stripping twice.
            if (!string.Equals(VoiceDirectivePolicy.Parse(replacement).ResourceKey, catalogKey, StringComparison.Ordinal))
            {
                replacement += ".ogg";
            }
        }

        string updated = ReplaceOrRemoveVoice(sourceText, replacement, out bool replacedExisting);
        Result<EditorCommandDocument> verified = ReadDocument(
            updated,
            acceptLegacyCharAlias,
            allowMultipleResourcesPerResource: true);
        if (!verified.Success || verified.Value == null)
        {
            return Result<EditorCommandVoiceEdit>.Fail(
                "Merged voice document did not validate: " + verified.Error);
        }

        VoiceDirectiveParseResult voice = VoiceDirectivePolicy.Parse(updated);
        if (!voice.Success
            || voice.HasDirective != (normalizedKey != null)
            || !string.Equals(voice.ResourceKey, normalizedKey, StringComparison.Ordinal))
        {
            return Result<EditorCommandVoiceEdit>.Fail(
                "Merged document did not keep the requested voice resource key.");
        }

        return Result<EditorCommandVoiceEdit>.Ok(new(
            current.Value.RevisionSha256,
            verified.Value.RevisionSha256,
            updated,
            normalizedKey,
            replacedExisting,
            string.Equals(updated, sourceText, StringComparison.Ordinal)));
    }

    /// <summary>
    /// Upserts or removes the one official <c>#st</c>/<c>#stm</c> line owned
    /// by the visual editor. Unrelated official and AAVT lines are preserved
    /// byte-for-byte. Multiple existing screen-text lines are treated as a
    /// hand-authored document and are not collapsed implicitly.
    /// </summary>
    public Result<EditorCommandScreenTextEdit> SetScreenText(
        string sourceText,
        string expectedRevisionSha256,
        ScreenTextDirective? directive,
        bool acceptLegacyCharAlias = true)
    {
        Result<EditorCommandDocument> current = ReadDocument(
            sourceText,
            acceptLegacyCharAlias,
            allowMultipleResourcesPerResource: true);
        if (!current.Success || current.Value == null)
        {
            return Result<EditorCommandScreenTextEdit>.Fail(current.Error);
        }

        if (!string.Equals(
                current.Value.RevisionSha256,
                expectedRevisionSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return Result<EditorCommandScreenTextEdit>.Fail(
                "Additional-prompt revision changed after the screen-text draft was created.");
        }

        if (current.Value.ScreenTextLines.Count > 1)
        {
            return Result<EditorCommandScreenTextEdit>.Fail(
                "The selected scene has multiple #st/#stm lines; edit them manually before using the visual screen-text editor.");
        }

        string? replacement = directive?.ToOfficialDirective();
        if (replacement != null)
        {
            Result<ScreenTextDirective> validation =
                ScreenTextDirectiveCodec.ParseLine(replacement);
            if (!validation.Success)
            {
                return Result<EditorCommandScreenTextEdit>.Fail(validation.Error);
            }
        }

        string updated = ReplaceOrRemoveScreenText(
            sourceText,
            current.Value.ScreenTextLines,
            replacement,
            out bool replacedExisting);
        bool alreadyPresent = string.Equals(updated, sourceText, StringComparison.Ordinal);
        Result<EditorCommandDocument> verified = ReadDocument(
            updated,
            acceptLegacyCharAlias,
            allowMultipleResourcesPerResource: true);
        if (!verified.Success || verified.Value == null)
        {
            return Result<EditorCommandScreenTextEdit>.Fail(
                "Merged screen-text document did not validate: " + verified.Error);
        }

        int expectedCount = directive == null ? 0 : 1;
        if (verified.Value.ScreenTextLines.Count != expectedCount)
        {
            return Result<EditorCommandScreenTextEdit>.Fail(
                "Merged document did not keep the requested screen-text state.");
        }

        return Result<EditorCommandScreenTextEdit>.Ok(new(
            current.Value.RevisionSha256,
            verified.Value.RevisionSha256,
            updated,
            directive,
            replacedExisting,
            alreadyPresent));
    }

    /// <summary>
    /// Appends the official persistent-screen-text clear command. Existing
    /// official and AAVT lines remain byte-for-byte unchanged. The operation
    /// is revision locked and idempotent.
    /// </summary>
    public Result<EditorCommandClearScreenTextEdit> AddClearScreenText(
        string sourceText,
        string expectedRevisionSha256,
        bool acceptLegacyCharAlias = true)
    {
        Result<EditorCommandDocument> current = ReadDocument(
            sourceText,
            acceptLegacyCharAlias,
            allowMultipleResourcesPerResource: true);
        if (!current.Success || current.Value == null)
        {
            return Result<EditorCommandClearScreenTextEdit>.Fail(current.Error);
        }

        if (!string.Equals(
                current.Value.RevisionSha256,
                expectedRevisionSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return Result<EditorCommandClearScreenTextEdit>.Fail(
                "Additional-prompt revision changed after the clear-screen-text draft was created.");
        }

        if (current.Value.HasClearScreenTextDirective)
        {
            return Result<EditorCommandClearScreenTextEdit>.Ok(new(
                current.Value.RevisionSha256,
                current.Value.RevisionSha256,
                sourceText,
                AlreadyPresent: true));
        }

        string updated = AppendStandaloneLine(sourceText, "#clearST");
        Result<EditorCommandDocument> verified = ReadDocument(
            updated,
            acceptLegacyCharAlias,
            allowMultipleResourcesPerResource: true);
        if (!verified.Success
            || verified.Value == null
            || !verified.Value.HasClearScreenTextDirective)
        {
            return Result<EditorCommandClearScreenTextEdit>.Fail(
                "Merged document did not keep the official #clearST directive: "
                + verified.Error);
        }

        return Result<EditorCommandClearScreenTextEdit>.Ok(new(
            current.Value.RevisionSha256,
            verified.Value.RevisionSha256,
            updated,
            AlreadyPresent: false));
    }

    /// <summary>
    /// Adds or removes the standalone <c>#aavt;continue</c> line. The rest of
    /// the document (official text and character directives) is preserved
    /// byte-for-byte, and the result is re-validated before returning.
    /// </summary>
    public EditorCommandContinueEdit SetContinue(
        string sourceText,
        string expectedRevisionSha256,
        bool enabled,
        bool acceptLegacyCharAlias = true)
    {
        Result<EditorCommandDocument> current = ReadDocument(
            sourceText,
            acceptLegacyCharAlias,
            allowMultipleResourcesPerResource: true);
        if (current.Value == null)
        {
            throw new InvalidOperationException(current.Error);
        }

        if (!string.Equals(
                current.Value.RevisionSha256,
                expectedRevisionSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Additional-prompt revision changed after the draft was created.");
        }

        if (current.Value.HasContinueDirective == enabled)
        {
            return new EditorCommandContinueEdit(
                expectedRevisionSha256,
                expectedRevisionSha256,
                sourceText,
                enabled,
                AlreadyPresent: true);
        }

        string updated = enabled
            ? AppendContinueLine(sourceText)
            : RemoveContinueLines(sourceText);
        Result<EditorCommandDocument> verified = ReadDocument(
            updated,
            acceptLegacyCharAlias,
            allowMultipleResourcesPerResource: true);
        if (verified.Value == null
            || verified.Value.HasContinueDirective != enabled)
        {
            throw new InvalidOperationException(
                "Merged additional-prompt document did not keep the continue directive state: "
                + (verified.Error ?? "unknown error"));
        }

        return new EditorCommandContinueEdit(
            expectedRevisionSha256,
            verified.Value.RevisionSha256,
            updated,
            enabled,
            AlreadyPresent: false);
    }

    private static string AppendContinueLine(string source)
        => AppendStandaloneLine(source, "#aavt;continue");

    /// <summary>
    /// Adds or removes the row's <c>#aavt;queue</c> marker. Same shape as the continue toggle: the rest of the
    /// document keeps its own bytes, and the result is re-read before it is returned (2026-10-13).
    /// </summary>
    public EditorCommandQueueEdit SetQueueMode(
        string sourceText,
        string expectedRevisionSha256,
        EffectQueueMode mode,
        bool acceptLegacyCharAlias = true)
    {
        Result<EditorCommandDocument> current = ReadDocument(
            sourceText,
            acceptLegacyCharAlias,
            allowMultipleResourcesPerResource: true);
        if (current.Value == null)
        {
            throw new InvalidOperationException(current.Error);
        }

        if (!string.Equals(
                current.Value.RevisionSha256,
                expectedRevisionSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Additional-prompt revision changed after the draft was created.");
        }

        if (current.Value.QueueMode == mode)
        {
            return new EditorCommandQueueEdit(
                expectedRevisionSha256,
                expectedRevisionSha256,
                sourceText,
                mode,
                AlreadyPresent: true);
        }

        string updated = mode == EffectQueueMode.OneQueue
            ? AppendStandaloneLine(sourceText, EffectQueueMarker.Directive)
            : RemoveStandaloneLines(sourceText, EffectQueueMarker.Directive);
        Result<EditorCommandDocument> verified = ReadDocument(
            updated,
            acceptLegacyCharAlias,
            allowMultipleResourcesPerResource: true);
        if (verified.Value == null || verified.Value.QueueMode != mode)
        {
            throw new InvalidOperationException(
                "Merged additional-prompt document did not keep the queue marker state: "
                + (verified.Error ?? "unknown error"));
        }

        return new EditorCommandQueueEdit(
            expectedRevisionSha256,
            verified.Value.RevisionSha256,
            updated,
            mode,
            AlreadyPresent: false);
    }

    private static string AppendStandaloneLine(string source, string directive)
    {
        var builder = new StringBuilder(source.Length + directive.Length + 2);
        builder.Append(source);
        if (source.Length != 0 && source[^1] != '\r' && source[^1] != '\n')
        {
            builder.Append(PreferredNewLine(source));
        }

        builder.Append(directive);
        return builder.ToString();
    }

    private static bool ContainsStandaloneLine(string source, string directive)
    {
        using var reader = new StringReader(source);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.Equals(
                    line.Trim(),
                    directive,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string RemoveContinueLines(string source)
        => RemoveStandaloneLines(source, "#aavt;continue");

    /// <summary>Removes every line that is exactly this standalone marker, keeping the rest byte-for-byte.</summary>
    private static string RemoveStandaloneLines(string source, string directive)
    {
        var output = new StringBuilder(source.Length);
        int offset = 0;
        while (offset < source.Length)
        {
            int contentEnd = offset;
            while (contentEnd < source.Length
                && source[contentEnd] != '\r'
                && source[contentEnd] != '\n')
            {
                contentEnd++;
            }

            int segmentEnd = contentEnd;
            if (segmentEnd < source.Length && source[segmentEnd] == '\r')
            {
                segmentEnd++;
            }

            if (segmentEnd < source.Length && source[segmentEnd] == '\n')
            {
                segmentEnd++;
            }

            string trimmed = source[offset..contentEnd].Trim();
            bool isMarkerLine = string.Equals(
                trimmed,
                directive,
                StringComparison.OrdinalIgnoreCase);
            if (!isMarkerLine)
            {
                output.Append(source, offset, segmentEnd - offset);
            }

            offset = segmentEnd;
        }

        return output.ToString();
    }

    private static string ReplaceOrRemoveScreenText(
        string source,
        IReadOnlyList<ScreenTextLine> existing,
        string? replacement,
        out bool replacedExisting)
    {
        var lineNumbers = existing
            .Select(item => item.LineNumber)
            .ToHashSet();
        var output = new StringBuilder(source.Length + (replacement?.Length ?? 0) + 2);
        replacedExisting = false;
        int offset = 0;
        int lineNumber = 1;
        while (offset < source.Length)
        {
            int contentEnd = offset;
            while (contentEnd < source.Length
                && source[contentEnd] != '\r'
                && source[contentEnd] != '\n')
            {
                contentEnd++;
            }

            int segmentEnd = contentEnd;
            if (segmentEnd < source.Length && source[segmentEnd] == '\r')
            {
                segmentEnd++;
            }

            if (segmentEnd < source.Length && source[segmentEnd] == '\n')
            {
                segmentEnd++;
            }

            if (lineNumbers.Contains(lineNumber))
            {
                if (replacement != null && !replacedExisting)
                {
                    output.Append(replacement);
                    output.Append(source, contentEnd, segmentEnd - contentEnd);
                }

                replacedExisting = true;
            }
            else
            {
                output.Append(source, offset, segmentEnd - offset);
            }

            offset = segmentEnd;
            lineNumber++;
        }

        if (replacement != null && !replacedExisting)
        {
            if (source.Length != 0
                && source[^1] != '\r'
                && source[^1] != '\n')
            {
                output.Append(PreferredNewLine(source));
            }

            output.Append(replacement);
        }

        return output.ToString();
    }

    private static string ReplaceOrRemoveVoice(
        string source,
        string? replacement,
        out bool replacedExisting)
    {
        var output = new StringBuilder(source.Length + (replacement?.Length ?? 0) + 2);
        replacedExisting = false;
        int offset = 0;
        while (offset < source.Length)
        {
            int contentEnd = offset;
            while (contentEnd < source.Length && source[contentEnd] != '\r' && source[contentEnd] != '\n')
            {
                contentEnd++;
            }

            int segmentEnd = contentEnd;
            if (segmentEnd < source.Length && source[segmentEnd] == '\r') segmentEnd++;
            if (segmentEnd < source.Length && source[segmentEnd] == '\n') segmentEnd++;

            if (VoiceDirectivePolicy.IsVoiceDirectiveLine(source[offset..contentEnd]))
            {
                if (replacement != null)
                {
                    output.Append(replacement);
                    output.Append(source, contentEnd, segmentEnd - contentEnd);
                }

                replacedExisting = true;
            }
            else
            {
                output.Append(source, offset, segmentEnd - offset);
            }

            offset = segmentEnd;
        }

        return replacement != null && !replacedExisting
            ? AppendStandaloneLine(source, replacement)
            : output.ToString();
    }

    private static string ReplaceOrAppend(
        string source,
        IReadOnlyList<EmbeddedAavtCommand> existingCommands,
        string resourceKey,
        string replacement,
        out bool replacedExisting)
    {
        var commandByLine = existingCommands.ToDictionary(
            command => command.LineNumber);
        var output = new StringBuilder(source.Length + replacement.Length + 2);
        replacedExisting = false;
        int offset = 0;
        int lineNumber = 1;
        while (offset < source.Length)
        {
            int contentEnd = offset;
            while (contentEnd < source.Length
                && source[contentEnd] != '\r'
                && source[contentEnd] != '\n')
            {
                contentEnd++;
            }

            int segmentEnd = contentEnd;
            if (segmentEnd < source.Length && source[segmentEnd] == '\r')
            {
                segmentEnd++;
            }

            if (segmentEnd < source.Length && source[segmentEnd] == '\n')
            {
                segmentEnd++;
            }

            if (commandByLine.TryGetValue(lineNumber, out EmbeddedAavtCommand? command)
                && CommandResourceIdentity.KeyFor(command) == resourceKey)
            {
                output.Append(replacement);
                output.Append(source, contentEnd, segmentEnd - contentEnd);
                replacedExisting = true;
            }
            else
            {
                output.Append(source, offset, segmentEnd - offset);
            }

            offset = segmentEnd;
            lineNumber++;
        }

        if (!replacedExisting)
        {
            if (source.Length != 0
                && source[^1] != '\r'
                && source[^1] != '\n')
            {
                output.Append(PreferredNewLine(source));
            }

            output.Append(replacement);
        }

        return output.ToString();
    }

    /// <summary>
    /// The public form of one canonical resource directive — the form an additional-prompt document holds. Any window
    /// that reads canonical text (the camera the picture editor publishes, a plan's own segments) and writes it back
    /// needs this, and it has to be the same conversion the merge uses or the two would disagree about one line.
    /// </summary>
    public static string ToPublicDirective(string canonicalDirective)
    {
        const string characterPrefix = "#char;";
        string pendingPrefix = SlotPendingCommandFamilyCompiler.CanonicalRootToken + ";";
        if (canonicalDirective.StartsWith(characterPrefix, StringComparison.Ordinal))
        {
            return "#aavt;char;" + canonicalDirective[characterPrefix.Length..];
        }

        if (canonicalDirective.StartsWith(pendingPrefix, StringComparison.Ordinal))
        {
            return "#aavt;"
                + SlotPendingCommandFamilyCompiler.PublicNamespaceToken
                + ";"
                + canonicalDirective[pendingPrefix.Length..];
        }

        string cameraPrefix = SceneCameraCommandFamilyCompiler.CanonicalRootToken + ";";
        if (canonicalDirective.StartsWith(cameraPrefix, StringComparison.Ordinal))
        {
            return "#aavt;"
                + SceneCameraCommandFamilyCompiler.PublicNamespaceToken
                + ";"
                + canonicalDirective[cameraPrefix.Length..];
        }

        foreach (string root in new[] { "#fx;", "#spine;" })
        {
            if (canonicalDirective.StartsWith(root, StringComparison.Ordinal))
                return "#aavt;" + canonicalDirective[1..];
        }

        throw new InvalidOperationException("Unsupported canonical AAVT resource family.");
    }

    private static int CountLines(string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        int count = 0;
        int offset = 0;
        while (offset < text.Length)
        {
            while (offset < text.Length && text[offset] != '\r' && text[offset] != '\n')
            {
                offset++;
            }

            if (offset < text.Length && text[offset] == '\r')
            {
                offset++;
            }

            if (offset < text.Length && text[offset] == '\n')
            {
                offset++;
            }

            count++;
        }

        return count;
    }

    private static string PreferredNewLine(string text)
    {
        int carriageReturn = text.IndexOf('\r');
        int lineFeed = text.IndexOf('\n');
        if (carriageReturn >= 0
            && carriageReturn + 1 < text.Length
            && text[carriageReturn + 1] == '\n')
        {
            return "\r\n";
        }

        if (lineFeed >= 0)
        {
            return "\n";
        }

        return carriageReturn >= 0 ? "\r" : "\n";
    }
}
