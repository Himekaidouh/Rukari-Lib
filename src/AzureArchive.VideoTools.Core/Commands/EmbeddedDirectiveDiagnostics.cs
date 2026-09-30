namespace AzureArchive.VideoTools.Core.Commands;

public static class EmbeddedDirectiveDiagnostics
{
    public static string BoundaryCaptureLine(
        string source,
        string originalText,
        EmbeddedAavtExtraction extraction,
        CompiledScriptIdentity sanitizedIdentity)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException("Boundary source is required.", nameof(source));
        }

        ArgumentNullException.ThrowIfNull(originalText);
        ArgumentNullException.ThrowIfNull(extraction);
        ArgumentNullException.ThrowIfNull(sanitizedIdentity);

        return "embedded-directive-capture"
            + $" boundary={source}"
            + $"; originalChars={originalText.Length}"
            + $"; originalLines={CommandIdentity.CountLines(originalText)}"
            + $"; removedLines={extraction.RemovedLineCount}"
            + $"; commands={extraction.Commands.Count}"
            + $"; errors={extraction.Errors.Count}"
            + $"; continue={extraction.HasContinueDirective}"
            + $"; nonVisualOnly={extraction.HasOnlyNonVisualDirectives}"
            + $"; sanitizedSha256={sanitizedIdentity.Sha256}"
            + $"; sanitizedChars={sanitizedIdentity.Utf16Length}"
            + $"; sanitizedLines={sanitizedIdentity.LineCount}";
    }
}
