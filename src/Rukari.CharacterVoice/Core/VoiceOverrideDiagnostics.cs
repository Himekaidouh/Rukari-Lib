using System.Text.Json;

namespace Rukari.CharacterVoice.Core;

internal sealed record VoiceOverrideEntry(string RelativePath, string FileName);

internal sealed record VoiceOverrideInventory(IReadOnlyList<VoiceOverrideEntry> Present, IReadOnlyList<VoiceOverrideEntry> Missing)
{
    internal int Total => Present.Count + Missing.Count;
}

/// <summary>Read-only diagnostics for native voice references. Never edits the manifest or voice index.</summary>
internal static class VoiceOverrideDiagnostics
{
    private const string ManifestName = "manifest.json";
    private const int MaximumEntries = 200_000;
    internal static VoiceOverrideInventory Inspect(string projectRoot)
    {
        string root = ProjectVoiceImportPolicy.NormalizeProjectRoot(projectRoot);
        string manifestPath = Path.Combine(root, ManifestName);
        if (!File.Exists(manifestPath)) throw new IOException("项目里没有 manifest.json。");

        var present = new List<VoiceOverrideEntry>();
        var missing = new List<VoiceOverrideEntry>();
        foreach (string relative in ReadVoiceEntries(manifestPath))
        {
            string target = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            (File.Exists(target) ? present : missing).Add(new VoiceOverrideEntry(relative, Path.GetFileName(relative)));
        }

        return new VoiceOverrideInventory(present, missing);
    }

    private static List<string> ReadVoiceEntries(string manifestPath)
    {
        using FileStream stream = new(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using JsonDocument document = JsonDocument.Parse(stream);
        var entries = new List<string>();
        if (!document.RootElement.TryGetProperty("VoiceOverrides", out JsonElement voices)) return entries;
        if (voices.ValueKind != JsonValueKind.Array || voices.GetArrayLength() > MaximumEntries)
        {
            throw new IOException("项目 VoiceOverrides 格式或条目数无效。");
        }

        foreach (JsonElement value in voices.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String) continue;
            string relative = (value.GetString() ?? string.Empty).Replace('\\', '/');
            if (ProjectVoiceImportPolicy.IsProjectVoicePath(relative)) entries.Add(relative);
        }

        return entries;
    }

}
