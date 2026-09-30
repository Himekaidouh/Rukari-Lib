using System.Security.Cryptography;
using System.Text;

namespace Rukari.Lib.Tools;

/// <summary>A single, verified skin snapshot shared by the runtime; no Unity or machine-specific paths.</summary>
internal sealed class BundledUiAssets
{
    internal const string ManifestName = "ui-assets.sha256";
    internal static readonly string[] RequiredFiles =
    {
        "atlases/Common.png", "metadata/Common.json",
        "decorations/Popup_Img_Deco_1.png", "decorations/Popup_Img_Deco_2.png"
    };

    private readonly Dictionary<string, byte[]> _files;
    internal IReadOnlyDictionary<string, AtlasSpriteRegion> Catalog { get; }

    private BundledUiAssets(Dictionary<string, byte[]> files, IReadOnlyDictionary<string, AtlasSpriteRegion> catalog)
    {
        _files = files;
        Catalog = catalog;
    }

    internal byte[]? Atlas(string name) => name == "Common" ? _files["atlases/Common.png"] : null;

    internal byte[]? Decoration(string name)
    {
        string relative = "decorations/" + (name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? name : name + ".png");
        return _files.TryGetValue(relative, out byte[]? bytes) ? bytes : null;
    }

    // The runtime passes its own Assembly.Location, not the process directory or a feature mod's directory.
    // Bytes are kept after verification so later file changes cannot mix the metadata and texture versions.
    internal static BundledUiAssets? Load(string runtimeAssemblyPath, out string? error)
    {
        try
        {
            string? directory = Path.GetDirectoryName(runtimeAssemblyPath);
            if (string.IsNullOrEmpty(directory)) throw new IOException("Runtime assembly has no directory.");
            string root = Path.Combine(directory, "ui");
            string[] lines = File.ReadAllLines(Path.Combine(root, ManifestName));
            if (lines.Length != RequiredFiles.Length) throw new IOException("UI manifest has an unexpected file set.");
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in lines)
            {
                string[] parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2 || parts[0].Length != 64 || !parts[0].All(Uri.IsHexDigit)
                    || !RequiredFiles.Contains(parts[1], StringComparer.Ordinal) || !hashes.TryAdd(parts[1], parts[0]))
                    throw new IOException("UI manifest contains an invalid or duplicate entry.");
            }

            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (string name in RequiredFiles)
            {
                byte[] bytes = File.ReadAllBytes(Path.Combine(root, name));
                string actual = Convert.ToHexString(SHA256.HashData(bytes));
                if (!string.Equals(actual, hashes[name], StringComparison.OrdinalIgnoreCase))
                    throw new IOException("UI asset hash mismatch: " + name);
                files.Add(name, bytes);
            }

            if (!AtlasSpriteCatalog.TryParse(Encoding.UTF8.GetString(files["metadata/Common.json"]),
                    out var catalog, out string? parseError))
                throw new IOException("UI metadata is invalid: " + parseError);
            error = null;
            return new BundledUiAssets(files, catalog);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                   or System.Security.SecurityException or NotSupportedException)
        {
            error = ex.Message;
            return null;
        }
    }
}
