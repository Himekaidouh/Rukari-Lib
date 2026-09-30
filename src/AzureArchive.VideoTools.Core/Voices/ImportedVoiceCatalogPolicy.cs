namespace AzureArchive.VideoTools.Core.Voices;

public sealed record ImportedSoundOverrideSnapshot(string OverridePath, string FullPath);

public sealed record ImportedVoiceCatalogCandidate(string ResourceKey, string DisplayName, string FullPath);

/// <summary>Builds unambiguous voice keys from a bounded snapshot of the native sound manifests.</summary>
public static class ImportedVoiceCatalogPolicy
{
    public const int MaximumOverridePaths = 2048;

    /// <summary>Resolves an existing short native key for selection highlighting without guessing between files.</summary>
    public static string? ResolveBindingKey(string? bindingKey, IReadOnlyList<string> catalogKeys)
    {
        ArgumentNullException.ThrowIfNull(catalogKeys);
        if (string.IsNullOrWhiteSpace(bindingKey) || catalogKeys.Count > MaximumOverridePaths) return null;
        string normalized = bindingKey.Replace('\\', '/');
        if (!VoiceDirectivePolicy.TryDecodeNativeIdentifier(VoiceDirectivePolicy.NativeIdentifierPrefix + normalized, out _))
            return null;

        string? candidate = null;
        bool ambiguous = false;
        foreach (string key in catalogKeys)
        {
            if (string.Equals(key, normalized, StringComparison.Ordinal)) return key;
            if (string.IsNullOrEmpty(key) || !key.EndsWith('/' + normalized, StringComparison.Ordinal)) continue;
            if (candidate is null) candidate = key;
            else if (!string.Equals(candidate, key, StringComparison.Ordinal)) ambiguous = true;
        }
        return ambiguous ? null : candidate;
    }

    public static IReadOnlyList<ImportedVoiceCatalogCandidate> CreateCandidates(
        IReadOnlyList<ImportedSoundOverrideSnapshot> overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        if (overrides.Count > MaximumOverridePaths)
            throw new ArgumentException($"音效资源超过 {MaximumOverridePaths} 项，无法在安全读取上限内列举。", nameof(overrides));

        // Keep unsupported formats in the collision set: the native resolver does not filter formats.
        var rows = overrides.Where(item => item is not null && !string.IsNullOrEmpty(item.OverridePath))
            .Select(item => new Row(item, NativeStem(item.OverridePath), Extension(item.OverridePath)))
            .ToArray();
        var result = new Dictionary<string, ImportedVoiceCatalogCandidate>(StringComparer.Ordinal);
        foreach (Row row in rows)
        {
            if (row.Extension is not (".wav" or ".ogg" or ".mp3")
                || string.IsNullOrWhiteSpace(row.Source.FullPath)
                || row.Source.OverridePath.Any(char.IsControl)) continue;

            string key = row.Stem;
            // Absolute manifest entries are supported by the native resolver. Keep their full directory
            // suffix, excluding only the volume/root syntax that is not a valid relative resource key.
            if (key.Length >= 3 && char.IsLetter(key[0]) && key[1] == ':' && key[2] == '/') key = key[3..];
            else if (key.StartsWith('/')) key = key.TrimStart('/');
            if (!VoiceDirectivePolicy.TryDecodeNativeIdentifier(VoiceDirectivePolicy.NativeIdentifierPrefix + key, out string canonical))
                continue;

            string[] paths = rows.Where(other => Matches(other.Stem, canonical))
                .Select(other => other.Source.FullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(2).ToArray();
            if (paths.Length != 1 || !string.Equals(paths[0], row.Source.FullPath, StringComparison.OrdinalIgnoreCase))
                continue;

            string normalizedPath = row.Source.OverridePath.Replace('\\', '/');
            string name = normalizedPath[(normalizedPath.LastIndexOf('/') + 1)..];
            result.TryAdd(canonical, new(canonical, name, row.Source.FullPath));
        }

        var repeatedNames = result.Values.GroupBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return result.Values.Select(item => repeatedNames.Contains(item.DisplayName)
                ? item with { DisplayName = $"{item.DisplayName}  ·  {item.ResourceKey}" } : item)
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ResourceKey, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool Matches(string stem, string key) =>
        string.Equals(stem, key, StringComparison.Ordinal)
        || stem.EndsWith('/' + key, StringComparison.Ordinal);

    private static string Extension(string path)
    {
        string normalized = path.Replace('\\', '/');
        int dot = normalized.LastIndexOf('.');
        return dot > normalized.LastIndexOf('/') ? normalized[dot..] : string.Empty;
    }

    // Native Utils.Util.MatchPathEndIgnoreExtension removes one extension and then trailing dots.
    private static string NativeStem(string path)
    {
        string normalized = path.Replace('\\', '/');
        string extension = Extension(normalized);
        return (extension.Length == 0 ? normalized : normalized[..^extension.Length]).TrimEnd('.');
    }

    private sealed record Row(ImportedSoundOverrideSnapshot Source, string Stem, string Extension);
}
