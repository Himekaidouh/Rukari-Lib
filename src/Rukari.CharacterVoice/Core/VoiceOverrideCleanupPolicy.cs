using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rukari.CharacterVoice.Core;

/// <summary>One entry of the project's <c>VoiceOverrides</c>, as the editor wrote it.</summary>
internal sealed record VoiceOverrideEntry(string RelativePath, string FileName);

/// <summary>What a cleanup would do: the entries whose audio is on disk, and the ones whose audio is gone.</summary>
internal sealed record VoiceOverrideCleanupPlan(
    string ProjectRoot,
    string ManifestPath,
    string IndexPath,
    IReadOnlyList<VoiceOverrideEntry> Present,
    IReadOnlyList<VoiceOverrideEntry> Missing)
{
    internal int Total => Present.Count + Missing.Count;
}

internal sealed record VoiceOverrideCleanupResult(int RemovedCount, int RemainingCount, string BackupDirectory);

/// <summary>
/// Removes the project voice entries whose audio file no longer exists — the state an imported lobby leaves behind
/// when its bundled voice was never brought along, and the reason the editor refuses to compile: its save path
/// packages every <c>VoiceOverrides</c> entry and throws on a path that is not there.
///
/// <para>
/// Deliberately narrow: only entries that are valid project voice paths <em>and</em> provably absent are touched,
/// every other property of the manifest is preserved verbatim, the originals are copied out of the project first,
/// the replacement is written through a temporary file and read back, and a second run removes nothing. The
/// matching lines of the editor's own <c>voices.txt</c> index are dropped with them.
/// </para>
/// </summary>
internal static class VoiceOverrideCleanupPolicy
{
    private const string ManifestName = "manifest.json";
    private const string VoicesDirectory = "voices";
    private const string IndexFileName = "voices.txt";
    private const int MaximumEntries = 200_000;

    /// <summary>Reads the manifest and reports which voice entries still have their audio.</summary>
    internal static VoiceOverrideCleanupPlan Inspect(string projectRoot)
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

        return new VoiceOverrideCleanupPlan(root, manifestPath, Path.Combine(root, VoicesDirectory, IndexFileName),
            present, missing);
    }

    /// <summary>
    /// Drops exactly the entries that are still missing at this moment. Copies the manifest and the voice index to
    /// <paramref name="backupRoot"/> before anything is written, and verifies the result by reading it back.
    /// </summary>
    internal static VoiceOverrideCleanupResult Apply(VoiceOverrideCleanupPlan plan, string backupRoot)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Missing.Count == 0) return new VoiceOverrideCleanupResult(0, plan.Present.Count, string.Empty);

        // Re-check on the way in: a file that appeared since Inspect must not be dropped from the manifest.
        var stillMissing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (VoiceOverrideEntry entry in plan.Missing)
        {
            string target = Path.Combine(plan.ProjectRoot, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(target)) stillMissing.Add(entry.RelativePath);
        }

        if (stillMissing.Count == 0) return new VoiceOverrideCleanupResult(0, plan.Total, string.Empty);

        string backupDirectory = CreateBackupDirectory(plan.ProjectRoot, backupRoot);
        CopyIfExists(plan.ManifestPath, Path.Combine(backupDirectory, ManifestName));
        CopyIfExists(plan.IndexPath, Path.Combine(backupDirectory, IndexFileName));

        string original = File.ReadAllText(plan.ManifestPath);
        JsonNode? node = JsonNode.Parse(original) ?? throw new IOException("项目 manifest 不是有效 JSON。");
        if (node is not JsonObject manifest || manifest["VoiceOverrides"] is not JsonArray overrides)
        {
            throw new IOException("项目 manifest 没有 VoiceOverrides 数组。");
        }

        var kept = new JsonArray();
        int removed = 0;
        int remaining = 0;
        foreach (JsonNode? item in overrides.ToArray())
        {
            string? value = item?.GetValue<string>();
            string normalized = (value ?? string.Empty).Replace('\\', '/');
            if (stillMissing.Contains(normalized))
            {
                removed++;
                continue;
            }

            kept.Add(value is null ? null : JsonValue.Create(value));
            remaining++;
        }

        if (removed == 0) return new VoiceOverrideCleanupResult(0, remaining, backupDirectory);

        manifest["VoiceOverrides"] = kept;
        string updated = manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        WriteAtomic(plan.ManifestPath, updated);
        PruneIndex(plan.IndexPath, stillMissing);

        // Read back: the entry count must match, and none of the removed paths may still be listed.
        string readBack = File.ReadAllText(plan.ManifestPath);
        using JsonDocument document = JsonDocument.Parse(readBack);
        if (!document.RootElement.TryGetProperty("VoiceOverrides", out JsonElement check)
            || check.ValueKind != JsonValueKind.Array
            || check.GetArrayLength() != remaining)
        {
            throw new IOException("清理后的回读校验失败；备份仍在 " + backupDirectory);
        }

        foreach (JsonElement value in check.EnumerateArray())
        {
            if (stillMissing.Contains((value.GetString() ?? string.Empty).Replace('\\', '/')))
            {
                throw new IOException("回读发现被删除的语音条目仍然存在；备份仍在 " + backupDirectory);
            }
        }

        return new VoiceOverrideCleanupResult(removed, remaining, backupDirectory);
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

    /// <summary>
    /// Drops the index lines of the removed files. The editor's index maps a voice file to the character and slot it
    /// belongs to; a line for a file that is no longer referenced only misleads.
    /// </summary>
    private static void PruneIndex(string indexPath, HashSet<string> removed)
    {
        if (!File.Exists(indexPath)) return;
        var stems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // The editor's index uses the file name without its extension ("052b870b-…" for "voices/052b870b-….ogg").
        foreach (string relative in removed) stems.Add(Path.GetFileNameWithoutExtension(relative));

        string[] lines = File.ReadAllLines(indexPath);
        var kept = new List<string>(lines.Length);
        int dropped = 0;
        foreach (string line in lines)
        {
            int separator = line.IndexOf("=>", StringComparison.Ordinal);
            string stem = (separator < 0 ? line : line[..separator]).Trim();
            if (stem.Length != 0 && stems.Contains(stem))
            {
                dropped++;
                continue;
            }

            kept.Add(line);
        }

        if (dropped == 0) return;
        var text = new StringBuilder();
        foreach (string line in kept) text.Append(line).Append('\n');
        WriteAtomic(indexPath, text.ToString());
    }

    private static string CreateBackupDirectory(string projectRoot, string backupRoot)
    {
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(projectRoot));
        if (string.IsNullOrWhiteSpace(name)) name = "project";
        string directory = Path.Combine(backupRoot, name + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void CopyIfExists(string source, string destination)
    {
        if (File.Exists(source)) File.Copy(source, destination, overwrite: false);
    }

    /// <summary>Writes through a temporary file in the same directory, so a failure cannot truncate the original.</summary>
    private static void WriteAtomic(string path, string content)
    {
        string temporary = path + ".rukari-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
