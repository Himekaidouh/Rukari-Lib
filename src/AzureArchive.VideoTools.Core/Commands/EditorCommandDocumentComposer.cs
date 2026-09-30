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
    IReadOnlyList<ScreenTextLine> ScreenTextLines);

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

public sealed class EditorCommandDocumentComposer
{
    private readonly EmbeddedAavtDirectiveExtractor _extractor = new();

    public Result<EditorCommandDocument> Read(
        string text,
        bool acceptLegacyCharAlias = true)
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
            screenText.Value));
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

        Result<EditorCommandDocument> current = Read(
            sourceText,
            acceptLegacyCharAlias);
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
        string canonicalPublicDirective = ToPublicDirective(command.CanonicalDirective);
        string updated = ReplaceOrAppend(
            sourceText,
            current.Value.Commands,
            CommandResourceIdentity.KeyFor(command),
            canonicalPublicDirective,
            out bool replacedExisting);
        Result<EditorCommandDocument> verified = Read(updated, acceptLegacyCharAlias);
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
        Result<EditorCommandDocument> verified = Read(updated, acceptLegacyCharAlias);
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

        Result<EditorCommandDocument> current = Read(sourceText, acceptLegacyCharAlias);
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
        Result<EditorCommandDocument> verified = Read(updated, acceptLegacyCharAlias);
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
        Result<EditorCommandDocument> current = Read(
            sourceText,
            acceptLegacyCharAlias);
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
        Result<EditorCommandDocument> verified = Read(updated, acceptLegacyCharAlias);
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
        Result<EditorCommandDocument> current = Read(
            sourceText,
            acceptLegacyCharAlias);
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
        Result<EditorCommandDocument> verified = Read(updated, acceptLegacyCharAlias);
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
        Result<EditorCommandDocument> current = Read(
            sourceText,
            acceptLegacyCharAlias);
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
        Result<EditorCommandDocument> verified = Read(updated, acceptLegacyCharAlias);
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
            bool isContinueLine = string.Equals(
                trimmed,
                "#aavt;continue",
                StringComparison.OrdinalIgnoreCase);
            if (!isContinueLine)
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

    private static string ToPublicDirective(string canonicalDirective)
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
