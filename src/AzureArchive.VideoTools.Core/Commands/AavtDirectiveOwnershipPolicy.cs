using Rukari.Lib.Commands;

namespace AzureArchive.VideoTools.Core.Commands;

/// <summary>Prevents this editor from claiming lines owned by a more specific foreign registration.</summary>
public static class AavtDirectiveOwnershipPolicy
{
    private static readonly string[] Owners = { "rukari.moreeffects", "rukari.charactervoice" };

    public static string? FindEditorConflict(string text, IEmbeddedDirectiveSanitizer snapshot)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(snapshot);
        using var reader = new StringReader(text);
        string? line;
        int number = 0;
        while ((line = reader.ReadLine()) != null)
        {
            number++;
            string trimmed = line.Trim();
            int separator = trimmed.IndexOf(';');
            string root = (separator < 0 ? trimmed : trimmed[..separator]).Trim();
            if (!root.Equals("#aavt", StringComparison.OrdinalIgnoreCase)
                && !root.Equals("#char", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(line, snapshot.SanitizeExceptOwners(line, Owners), StringComparison.Ordinal))
                return $"Conflict：第 {number} 行的指令已由其它 Mod 接管，请先解决命名空间冲突；本次未修改台词。";
        }
        return null;
    }
}
