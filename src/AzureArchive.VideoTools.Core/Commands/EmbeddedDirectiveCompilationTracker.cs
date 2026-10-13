using System.Text;
using AzureArchive.VideoTools.Core.Voices;
using Rukari.Lib.Commands;

namespace AzureArchive.VideoTools.Core.Commands;

/// <summary>
/// Tracks AAVT captures within one shared compiler call. Foreign directive removal must not
/// count as an AAVT capture: a foreign-only result can still delete an old AAVT effect.
/// Use one instance per compiler thread; the shared host supplies the outer-call identity.
/// </summary>
public sealed class AavtCompilationCaptureTracker
{
    private readonly EmbeddedAavtDirectiveExtractor _extractor = new();
    private readonly HashSet<string> _aavtIdentities = new(StringComparer.Ordinal);
    private string _lastAavtIdentitySha256 = string.Empty;
    private long _compilationId = long.MinValue;

    /// <summary>
    /// Consumes only this registration's lines. A more specific foreign child route must not
    /// also execute through AAVT's parent route. The known voice module is the sole exception:
    /// its mirrored voice line declares a non-visual effect tombstone, never a visual command.
    /// </summary>
    public EmbeddedDirectiveCompilationCapture Capture(
        DirectiveCompilation compilation,
        bool acceptLegacyAlias)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        var ownLines = compilation.OwnedDirectives.Select(line => line.LineNumber).ToHashSet();
        var excludedLines = compilation.RemovedDirectives
            .Where(line => !ownLines.Contains(line.LineNumber)
                && !(string.Equals(line.OwnerId, "rukari.charactervoice", StringComparison.Ordinal)
                    && string.Equals(line.Route, "#aavt;voice", StringComparison.Ordinal)
                    && VoiceDirectivePolicy.IsVoiceDirectiveLine(line.Text)))
            .Select(line => line.LineNumber).ToHashSet();
        string source = compilation.SourceText;
        if (excludedLines.Count != 0)
        {
            var retained = new StringBuilder(source.Length);
            int offset = 0;
            int lineNumber = 1;
            while (offset < source.Length)
            {
                int contentEnd = offset;
                while (contentEnd < source.Length && source[contentEnd] is not ('\r' or '\n')) contentEnd++;
                int lineEnd = contentEnd;
                if (lineEnd < source.Length && source[lineEnd] == '\r') lineEnd++;
                if (lineEnd < source.Length && source[lineEnd] == '\n') lineEnd++;
                // Retain terminators even for excluded lines so own error locations stay useful.
                int retainedStart = excludedLines.Contains(lineNumber) ? contentEnd : offset;
                retained.Append(source, retainedStart, lineEnd - retainedStart);
                offset = lineEnd;
                lineNumber++;
            }
            source = retained.ToString();
        }
        return Capture(compilation.CompilationId, source, compilation.OfficialText, acceptLegacyAlias);
    }

    public EmbeddedDirectiveCompilationCapture Capture(
        long compilationId,
        string sourceText,
        string officialText,
        bool acceptLegacyAlias)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        ArgumentNullException.ThrowIfNull(officialText);
        if (_compilationId != compilationId)
        {
            _compilationId = compilationId;
            _aavtIdentities.Clear();
            _lastAavtIdentitySha256 = string.Empty;
        }

        // Nested boundaries of one compile share a CompilationId, and the outer
        // wrapper reports its own final official text. Keeping the earlier AAVT
        // identity lets that wrapper text be tied back to the capture it belongs
        // to, which is the identity the engine and the editor selection report.
        string previousAavtIdentity = _lastAavtIdentitySha256;
        CompiledScriptIdentity identity = CommandIdentity.CompiledScript(officialText);
        bool seenEarlier = _aavtIdentities.Contains(identity.Sha256);
        // Parse our original lines (including mirrored voice-only tombstones), but use the
        // host's final text after ALL registered namespaces for every compiled identity.
        EmbeddedAavtExtraction extraction = _extractor.Extract(sourceText, acceptLegacyAlias)
            with { SanitizedText = officialText };
        if (extraction.HasEmbeddedDirectives)
        {
            _aavtIdentities.Add(identity.Sha256);
            _lastAavtIdentitySha256 = identity.Sha256;
        }

        return new(identity, extraction, seenEarlier)
        {
            PreviousAavtIdentitySha256 = previousAavtIdentity
        };
    }
}

public sealed record EmbeddedDirectiveCompilationCapture(
    CompiledScriptIdentity Identity,
    EmbeddedAavtExtraction Extraction,
    bool AavtSeenEarlierInCompilation)
{
    /// <summary>
    /// Identity of the most recent capture that carried AAVT directives within the
    /// same compilation, or empty when this compile has none. The outer wrapper
    /// boundary reports its own final official text, so this is the only link back
    /// to the capture the engine-visible identity belongs to.
    /// </summary>
    public string PreviousAavtIdentitySha256 { get; init; } = string.Empty;
}
