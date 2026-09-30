using System.Text;
using AzureArchive.VideoTools.Core.Voices;

namespace AzureArchive.VideoTools.Core.Commands;

public sealed record EmbeddedAavtCommand(
    int LineNumber,
    string SourceDirective,
    string CanonicalDirective,
    int PublicSlot);

public sealed record EmbeddedAavtExtraction(
    string SanitizedText,
    IReadOnlyList<EmbeddedAavtCommand> Commands,
    IReadOnlyList<string> Errors,
    int RemovedLineCount)
{
    public bool HasEmbeddedDirectives => RemovedLineCount != 0;

    /// <summary>
    /// True when a bare <c>#aavt;continue</c> marker line was removed. The
    /// marker carries no slot command; it marks the scene as continuing from
    /// the previous visible dialogue line.
    /// </summary>
    public bool HasContinueDirective { get; init; }

    /// <summary>
    /// True when this text declared AAVT directive lines while every declared
    /// line turned out to be non-visual — a bare <c>#aavt;continue</c> marker, a
    /// mirrored voice line, or both — and nothing was rejected. Only those two
    /// forms are removed without yielding a canonical command; every other
    /// directive either produces a command or an error.
    /// <para>
    /// Such a compile is a deliberate empty effect set, not a vacuous one: the
    /// scene still owns cleanup and inherited-chain semantics even though it
    /// carries no canonical command, so an editor-preview capture may classify
    /// it as an explicit tombstone. A text that removed no directive line stays
    /// <c>false</c> so an unexplained empty capture keeps failing closed.
    /// </para>
    /// </summary>
    public bool HasOnlyNonVisualDirectives =>
        Commands.Count == 0 && RemovedLineCount != 0 && Errors.Count == 0;
}

public sealed class EmbeddedAavtDirectiveExtractor
{
    /// <summary>
    /// Five character slots, five transient preset resources, one camera resource, and up to ten spine overlays — one per reserved
    /// track, so a card can drive several parts of one character at once.
    /// <para>
    /// A spine overlay is a resource of its own, keyed by the character slot <em>and</em> the track
    /// it claims: a card may carry <c>#aavt;char;3;…</c> next to <c>#aavt;spine;3;…</c>, and it may
    /// carry eyes on track 20 and arms on track 21, but not two directives on the same track. Ten is
    /// the width of the reserved range rather than a Spine limit; the real bound is per key.
    /// </para>
    /// </summary>
    public const int MaxCommandsPerScene = 21;

    private readonly CharacterTransformCommandFamilyCompiler _characterCompiler = new();
    private readonly SlotPendingCommandFamilyCompiler _slotPendingCompiler = new();
    private readonly SceneCameraCommandFamilyCompiler _cameraCompiler = new();
    private readonly SpineOverlayCommandFamilyCompiler _spineCompiler = new();
    private readonly CharacterPresetCommandFamilyCompiler _presetCompiler = new();

    public EmbeddedAavtExtraction Extract(
        string text,
        bool acceptLegacyCharAlias = true)
    {
        ArgumentNullException.ThrowIfNull(text);

        var sanitized = new StringBuilder(text.Length);
        var commands = new List<EmbeddedAavtCommand>();
        var errors = new List<string>();
        var occupiedSlots = new HashSet<string>(StringComparer.Ordinal);
        bool hasContinueDirective = false;
        int removedLines = 0;
        int offset = 0;
        int lineNumber = 1;

        while (offset < text.Length)
        {
            int contentEnd = offset;
            while (contentEnd < text.Length
                && text[contentEnd] != '\r'
                && text[contentEnd] != '\n')
            {
                contentEnd++;
            }

            int segmentEnd = contentEnd;
            if (segmentEnd < text.Length && text[segmentEnd] == '\r')
            {
                segmentEnd++;
            }

            if (segmentEnd < text.Length && text[segmentEnd] == '\n')
            {
                segmentEnd++;
            }

            string line = text[offset..contentEnd];
            string trimmed = line.Trim();
            if (VoiceDirectivePolicy.IsVoiceDirectiveLine(trimmed))
            {
                // Voice is mirrored into the native voice field before parsing/export.
                // It is not a character/camera command and must never reach the native text parser.
                removedLines++;
            }
            else if (trimmed.Equals("#aavt;continue", StringComparison.OrdinalIgnoreCase))
            {
                removedLines++;
                hasContinueDirective = true;
            }
            else if (IsAavtDirective(trimmed)
                || (acceptLegacyCharAlias && IsLegacyCharacterDirective(trimmed)))
            {
                removedLines++;
                ParseDirective(trimmed, lineNumber, commands, errors, occupiedSlots);
            }
            else
            {
                sanitized.Append(text, offset, segmentEnd - offset);
            }

            offset = segmentEnd;
            lineNumber++;
        }

        VoiceDirectiveParseResult voice = VoiceDirectivePolicy.Parse(text);
        errors.AddRange(voice.Errors);
        if (!CommandResourceIdentity.IsWithinSceneBudget(commands.Select(
                command => EmbeddedProjectCommandCompiler.FamilyTypeIdFor(command.CanonicalDirective))))
        {
            errors.Add(
                "A scene may contain at most five AAVT character directives, five transient presets, ten spine overlay "
                + "directives and one camera directive.");
        }

        return new EmbeddedAavtExtraction(
            sanitized.ToString(),
            Array.AsReadOnly(commands.ToArray()),
            Array.AsReadOnly(errors.ToArray()),
            removedLines)
        {
            HasContinueDirective = hasContinueDirective
        };
    }

    private void ParseDirective(
        string directive,
        int lineNumber,
        ICollection<EmbeddedAavtCommand> commands,
        ICollection<string> errors,
        ISet<string> occupiedResources)
    {
        string internalDirective;
        if (IsLegacyCharacterDirective(directive))
        {
            internalDirective = directive;
        }
        else
        {
            string[] parts = directive.Split(';');
            bool isCharacter = parts.Length >= 4
                && string.Equals(parts[0].Trim(), "#aavt", StringComparison.OrdinalIgnoreCase)
                && string.Equals(parts[1].Trim(), "char", StringComparison.OrdinalIgnoreCase);
            bool isSlotPending = parts.Length >= 4
                && string.Equals(parts[0].Trim(), "#aavt", StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    parts[1].Trim(),
                    SlotPendingCommandFamilyCompiler.PublicNamespaceToken,
                    StringComparison.OrdinalIgnoreCase);
            bool isCamera = parts.Length >= 3
                && string.Equals(parts[0].Trim(), "#aavt", StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    parts[1].Trim(),
                    SceneCameraCommandFamilyCompiler.PublicNamespaceToken,
                    StringComparison.OrdinalIgnoreCase);
            bool isSpine = parts.Length >= 4
                && string.Equals(parts[0].Trim(), "#aavt", StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    parts[1].Trim(),
                    SpineOverlayCommandFamilyCompiler.PublicNamespaceToken,
                    StringComparison.OrdinalIgnoreCase);
            bool isPreset = parts.Length >= 4
                && string.Equals(parts[0].Trim(), "#aavt", StringComparison.OrdinalIgnoreCase)
                && string.Equals(parts[1].Trim(), CharacterPresetCommandFamilyCompiler.PublicNamespaceToken, StringComparison.OrdinalIgnoreCase);
            if (!isCharacter && !isSlotPending && !isCamera && !isSpine && !isPreset)
            {
                errors.Add(
                    $"Line {lineNumber}: AAVT directive must start with "
                    + $"#aavt;char;<slot>;<operation> or #aavt;fx;<slot>;<preset> or "
                    + $"#aavt;{SlotPendingCommandFamilyCompiler.PublicNamespaceToken};<slot>;<operation> or "
                    + $"#aavt;{SpineOverlayCommandFamilyCompiler.PublicNamespaceToken};<slot>;<animation|clear> or "
                    + $"#aavt;{SceneCameraCommandFamilyCompiler.PublicNamespaceToken};<operation>.");
                return;
            }

            internalDirective = isPreset
                ? CharacterPresetCommandFamilyCompiler.CanonicalRootToken + ";" + string.Join(';', parts.Skip(2))
                : isCamera
                ? SceneCameraCommandFamilyCompiler.CanonicalRootToken + ";"
                    + string.Join(';', parts.Skip(2))
                : isSpine
                    ? SpineOverlayCommandFamilyCompiler.CanonicalRootToken + ";"
                        + string.Join(';', parts.Skip(2))
                    : isSlotPending
                        ? SlotPendingCommandFamilyCompiler.CanonicalRootToken + ";"
                            + string.Join(';', parts.Skip(2))
                        : "#char;" + string.Join(';', parts.Skip(2));
        }

        ICommandFamilyCompiler family = internalDirective.StartsWith(CharacterPresetCommandFamilyCompiler.CanonicalRootToken + ";", StringComparison.OrdinalIgnoreCase)
            ? _presetCompiler
            : internalDirective.StartsWith(
                SceneCameraCommandFamilyCompiler.CanonicalRootToken + ";",
                StringComparison.OrdinalIgnoreCase)
            ? _cameraCompiler
            : internalDirective.StartsWith(
                SpineOverlayCommandFamilyCompiler.CanonicalRootToken + ";",
                StringComparison.OrdinalIgnoreCase)
                ? _spineCompiler
                : internalDirective.StartsWith(
                    SlotPendingCommandFamilyCompiler.CanonicalRootToken + ";",
                    StringComparison.OrdinalIgnoreCase)
                    ? _slotPendingCompiler
                    : _characterCompiler;
        var canonical = family.Canonicalize(internalDirective);
        if (!canonical.Success || canonical.Value == null)
        {
            errors.Add($"Line {lineNumber}: {canonical.Error}");
            return;
        }

        if (!occupiedResources.Add(CommandResourceIdentity.KeyFor(canonical.Value)))
        {
            errors.Add(
                string.Equals(
                    canonical.Value.CommandType,
                    SpineOverlayCommandFamilyCompiler.CommandTypeId,
                    StringComparison.Ordinal)
                    ? $"Line {lineNumber}: slot {canonical.Value.PublicSlot} already has an AAVT "
                        + "spine overlay directive in this scene."
                    : canonical.Value.CommandType == CharacterPresetCommandFamilyCompiler.CommandTypeId
                        ? $"Line {lineNumber}: slot {canonical.Value.PublicSlot} already has an AAVT preset directive in this scene."
                    : canonical.Value.PublicSlot == SceneCameraCommandFamilyCompiler.SingletonResourceSlot
                        ? $"Line {lineNumber}: this scene already has an AAVT camera directive."
                        : $"Line {lineNumber}: slot {canonical.Value.PublicSlot} already has an AAVT "
                            + "character directive in this scene.");
            return;
        }

        commands.Add(new EmbeddedAavtCommand(
            lineNumber,
            directive,
            canonical.Value.Directive,
            canonical.Value.PublicSlot));
    }

    private static bool IsAavtDirective(string line) =>
        HasRootToken(line, "#aavt");

    private static bool IsLegacyCharacterDirective(string line) =>
        HasRootToken(line, "#char");

    private static bool HasRootToken(string line, string root)
    {
        int separator = line.IndexOf(';');
        return string.Equals((separator < 0 ? line : line[..separator]).Trim(),
            root, StringComparison.OrdinalIgnoreCase);
    }
}
