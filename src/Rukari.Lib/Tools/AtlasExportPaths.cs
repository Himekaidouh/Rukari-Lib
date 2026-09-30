namespace Rukari.Lib.Tools;

/// <summary>
/// Where a decoded atlas export may live, and how its three parts are laid out inside it: <c>atlases\*.png</c>,
/// <c>metadata\*.json</c> and <c>decorations\*.png</c>. Pure managed and free of Unity types, so the layout can be
/// tested off the game thread.
/// Legacy export helpers retained for compatibility; the runtime's shared skin uses its own DLL-relative ui directory.
/// </summary>
public static class AtlasExportPaths
{
    private const string ExportFolder = "atlas-export";
    private const string AtlasFolder = "atlases";
    private const string MetadataFolder = "metadata";
    private const string DecorationFolder = "decorations";

    /// <summary>Explicitly configured and application-relative legacy export roots, in priority order. The first one that has files wins.</summary>
    public static IReadOnlyList<string> Roots { get; } = BuildRoots();

    /// <summary>Candidate files for the Common atlas, kept so callers of the original single-atlas API still work.</summary>
    public static IReadOnlyList<string> Candidates => AtlasCandidates("Common");

    /// <summary>
    /// Candidate files for one atlas image. <c>RUKARI_ATLAS_EXPORT</c> may name either an export folder or, as a
    /// legacy form, the Common atlas PNG itself.
    /// </summary>
    /// <param name="atlasName">Atlas name without extension, for example <c>Common</c> or <c>Studio</c>.</param>
    public static IReadOnlyList<string> AtlasCandidates(string atlasName)
    {
        var list = new List<string>();
        string name = (atlasName ?? string.Empty).Trim();
        if (name.Length == 0) return list;
        string? configured = Environment.GetEnvironmentVariable("RUKARI_ATLAS_EXPORT");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            string value = configured!.Trim();
            if (value.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(name, "Common", StringComparison.OrdinalIgnoreCase)) list.Add(value);
            }
            else
            {
                list.Add(Path.Combine(value, AtlasFolder, name + ".png"));
            }
        }

        foreach (string root in Roots)
        {
            list.Add(Path.Combine(root, AtlasFolder, name + ".png"));
        }

        return list;
    }

    /// <summary>Candidate files for one standalone decoration texture.</summary>
    /// <param name="fileName">File name with or without its <c>.png</c> extension.</param>
    public static IReadOnlyList<string> DecorationCandidates(string fileName)
    {
        var list = new List<string>();
        string name = (fileName ?? string.Empty).Trim();
        if (name.Length == 0) return list;
        if (!name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) name += ".png";
        string? configured = Environment.GetEnvironmentVariable("RUKARI_ATLAS_EXPORT");
        if (!string.IsNullOrWhiteSpace(configured) && !configured!.Trim().EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            list.Add(Path.Combine(configured.Trim(), DecorationFolder, name));
        }

        foreach (string root in Roots)
        {
            list.Add(Path.Combine(root, DecorationFolder, name));
        }

        return list;
    }

    /// <summary>
    /// The metadata file that belongs to an atlas image, derived from the image path so a relocated export keeps
    /// working: <c>&lt;root&gt;\atlases\Common.png</c> pairs with <c>&lt;root&gt;\metadata\Common.json</c>.
    /// </summary>
    /// <param name="atlasPath">Full path of an atlas PNG.</param>
    public static string? MetadataFileFor(string? atlasPath)
    {
        if (string.IsNullOrWhiteSpace(atlasPath)) return null;
        string? atlasFolder = Path.GetDirectoryName(atlasPath);
        if (atlasFolder is null) return null;
        string? root = Path.GetDirectoryName(atlasFolder);
        if (root is null) return null;
        return Path.Combine(root, MetadataFolder, Path.GetFileNameWithoutExtension(atlasPath) + ".json");
    }

    private static IReadOnlyList<string> BuildRoots()
    {
        var list = new List<string>();
        string? configured = Environment.GetEnvironmentVariable("RUKARI_ATLAS_EXPORT");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            string value = configured!.Trim();
            if (value.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                string? folder = Path.GetDirectoryName(value);
                string? root = folder is null ? null : Path.GetDirectoryName(folder);
                if (root is not null) list.Add(root);
            }
            else
            {
                list.Add(value);
            }
        }

        // Legacy consumers may supply an explicit export or an application-relative fixture.
        list.Add(Path.Combine(AppContext.BaseDirectory, ExportFolder));
        return list;
    }
}
