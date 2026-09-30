namespace Rukari.Lib.Commands;

/// <summary>Capability id published with the shared command-help registry.</summary>
public static class CommandHelpCapabilities
{
    /// <summary>Publishes a mod's own directives into the editor's 「指令格式」 help panel.</summary>
    public const string CommandHelp = "rukari.commandhelp";
}

/// <summary>
/// One row of the editor's 「指令格式」 help: the section it is grouped under, the syntax a user can insert into
/// the additional-prompt field, and one explanation. The shape mirrors the editor's own row so a published row
/// renders and behaves exactly like an official one.
/// </summary>
public sealed record CommandHelpEntry(string Section, string Syntax, string Description);

/// <summary>
/// The shared registry behind the editor's 「指令格式」 panel. Publishing is additive: official rows are never
/// edited, and the returned lease removes exactly the rows the registering mod added.
/// </summary>
public interface ICommandHelpService
{
    /// <summary>Publishes one row. Returns a lease that removes it again.</summary>
    ModResult<IDisposable> Register(string section, string syntax, string description);
}

/// <summary>
/// Bounded, game-free rules for what may be published. Kept apart from the panel so the rules stay testable
/// without a running game.
/// </summary>
public static class CommandHelpPolicy
{
    /// <summary>Longest accepted section name.</summary>
    public const int MaximumSectionLength = 16;
    /// <summary>Longest accepted syntax sample.</summary>
    public const int MaximumSyntaxLength = 160;
    /// <summary>Longest accepted explanation.</summary>
    public const int MaximumDescriptionLength = 320;
    /// <summary>Most rows every mod together may publish.</summary>
    public const int MaximumPublishedRows = 64;

    /// <summary>Normalizes one row, or explains why it cannot be published.</summary>
    public static bool TryNormalize(string? section, string? syntax, string? description,
        out CommandHelpEntry entry, out string error)
    {
        entry = new CommandHelpEntry(string.Empty, string.Empty, string.Empty);
        string cleanSection = Single(section);
        string cleanSyntax = Single(syntax);
        string cleanDescription = Single(description);
        if (cleanSection.Length == 0) { error = "指令帮助需要分组名。"; return false; }
        if (cleanSyntax.Length == 0) { error = "指令帮助需要语法示例。"; return false; }
        if (cleanDescription.Length == 0) { error = "指令帮助需要一句说明。"; return false; }
        if (cleanSection.Length > MaximumSectionLength) { error = "指令帮助的分组名过长。"; return false; }
        if (cleanSyntax.Length > MaximumSyntaxLength) { error = "指令帮助的语法示例过长。"; return false; }
        if (cleanDescription.Length > MaximumDescriptionLength) { error = "指令帮助的说明过长。"; return false; }
        error = string.Empty;
        entry = new CommandHelpEntry(cleanSection, cleanSyntax, cleanDescription);
        return true;
    }

    /// <summary>
    /// Rows to publish, in registration order, skipping any syntax the panel already shows (case-insensitive).
    /// This is what makes publishing idempotent: a cached reference array is never given a second copy.
    /// </summary>
    public static IReadOnlyList<CommandHelpEntry> SelectMissing(IEnumerable<CommandHelpEntry> published,
        IEnumerable<string> panelSyntaxes)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string syntax in panelSyntaxes) if (!string.IsNullOrWhiteSpace(syntax)) seen.Add(syntax.Trim());
        var rows = new List<CommandHelpEntry>();
        foreach (CommandHelpEntry entry in published)
        {
            if (entry is null || !seen.Add(entry.Syntax)) continue;
            rows.Add(entry);
        }
        return rows;
    }

    /// <summary>Folds every whitespace run into one space and drops control characters.</summary>
    private static string Single(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var builder = new System.Text.StringBuilder(value.Length);
        bool pendingSpace = false;
        foreach (char character in value)
        {
            if (char.IsControl(character) || char.IsWhiteSpace(character)) { pendingSpace = builder.Length > 0; continue; }
            if (pendingSpace) { builder.Append(' '); pendingSpace = false; }
            builder.Append(character);
        }
        return builder.ToString();
    }
}
