using System.Security.Cryptography;
using Rukari.Lib.Tools;

namespace Rukari.Lib.Tests;

internal static class BundledUiAssetsTests
{
    internal static void SkinFollowsTheRuntimeAndKeepsOneVerifiedSnapshot()
    {
        using var fixture = new SkinFixture();
        string relocated = Path.Combine(fixture.Root, "另一处安装", "mods", "RukariLib", "0.2.4");
        Directory.CreateDirectory(Path.GetDirectoryName(relocated)!);
        Directory.Move(fixture.Module, relocated);
        var skin = BundledUiAssets.Load(Path.Combine(relocated, "Rukari.Lib.Runtime.dll"), out string? error);
        Check.True(skin is not null, "A relocated runtime must find its own skin: " + error);
        Check.True(skin!.Catalog.ContainsKey("Fixture"), "The matching metadata must load.");
        byte[] original = skin.Atlas("Common")!.ToArray();
        File.WriteAllText(Path.Combine(relocated, "ui", "atlases", "Common.png"), "changed after load");
        Check.True(original.SequenceEqual(skin.Atlas("Common")!), "A running skin must use its verified snapshot.");
        Check.True(skin.Decoration("Popup_Img_Deco_1")!.SequenceEqual(skin.Decoration("Popup_Img_Deco_1.png")!),
            "Decoration names may omit the extension.");
        Check.True(skin.Atlas("Studio") is null && skin.Decoration("../../elsewhere") is null,
            "Unbundled names must not fall through to another directory.");
    }

    internal static void MissingOrMismatchedSkinFailsAsAWhole()
    {
        using var fixture = new SkinFixture();
        string assembly = Path.Combine(fixture.Module, "Rukari.Lib.Runtime.dll");
        string metadata = Path.Combine(fixture.Module, "ui", "metadata", "Common.json");
        File.AppendAllText(metadata, " ");
        Check.True(BundledUiAssets.Load(assembly, out string? mismatch) is null && mismatch!.Contains("hash mismatch"),
            "Even a parseable metadata change must reject the entire set.");
        File.Delete(metadata);
        Check.True(BundledUiAssets.Load(assembly, out _) is null, "A missing metadata file must not use hard-coded coordinates.");
        Check.True(BundledUiAssets.Load(Path.Combine(fixture.Root, "no-skin", "runtime.dll"), out _) is null,
            "A missing installation must report fallback rather than throw.");
    }

    internal static void SkinManifestRejectsDuplicatesAndForeignPaths()
    {
        using var fixture = new SkinFixture();
        string assembly = Path.Combine(fixture.Module, "Rukari.Lib.Runtime.dll");
        string manifest = Path.Combine(fixture.Module, "ui", BundledUiAssets.ManifestName);
        string[] lines = File.ReadAllLines(manifest);
        string[] duplicate = lines.ToArray();
        duplicate[1] = duplicate[0];
        File.WriteAllLines(manifest, duplicate);
        Check.True(BundledUiAssets.Load(assembly, out _) is null, "Duplicate entries cannot stand in for a required file.");
        lines[0] = lines[0][..65] + "../outside.png";
        File.WriteAllLines(manifest, lines);
        Check.True(BundledUiAssets.Load(assembly, out _) is null, "Only the known skin file set can be read.");
    }

    internal static void ShippedSkinContainsTheChromeAndValidSpriteBounds()
    {
        string? assembly = Environment.GetEnvironmentVariable("RUKARI_TEST_UI_ASSEMBLY");
        if (string.IsNullOrEmpty(assembly))
        {
            // Source-only SDK consumers do not receive artwork. Product builds must check the actual payload.
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "build.ps1"))) directory = directory.Parent;
            if (directory is null) return;
            string configuration = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.Name;
            assembly = Path.Combine(directory.FullName, "Rukari.Lib.Runtime", "bin", configuration, "Rukari.Lib.Runtime.dll");
        }
        var skin = BundledUiAssets.Load(assembly, out string? error);
        Check.True(skin is not null, "The distributed skin must load: " + error);
        byte[] png = skin!.Atlas("Common")!;
        Check.True(png.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "The shared atlas is a PNG.");
        int width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
        int height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
        Check.Equal(2048, width); Check.Equal(2048, height);
        foreach (var region in skin.Catalog.Values)
            Check.True(region.X >= 0 && region.Y >= 0 && region.X + region.Width <= width && region.Y + region.Height <= height,
                "Sprite lies outside the matching PNG: " + region.Name);
        foreach (string name in new[] { "Common_Popup_Bg", "Common_Square_Bg", "Common_Icon_Close", "Common_Icon_Gacha",
            "Common_Bg_Raius10px", "Common_Bg_Raius5px_Shadow", "Common_Btn_BG", "Common_Btn_Normal_B_C_Pt", "Common_Btn_Normal_Y_S_Pt" })
            Check.True(skin.Catalog.ContainsKey(name), "Missing shared UI sprite: " + name);
        Check.Equal(145, skin.Catalog["Common_Popup_Bg"].BorderTop, "The title band depends on the original slice boundary.");
    }

    private sealed class SkinFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "RukariSkinTests-" + Guid.NewGuid().ToString("N"));
        internal string Module => Path.Combine(Root, "module");
        internal SkinFixture()
        {
            string ui = Path.Combine(Module, "ui");
            var lines = new List<string>();
            foreach (string name in BundledUiAssets.RequiredFiles)
            {
                string path = Path.Combine(ui, name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, name.EndsWith(".json", StringComparison.Ordinal)
                    ? "{\"sprites\":[{\"name\":\"Fixture\",\"x\":0,\"y\":0,\"width\":1,\"height\":1}]}" : name);
                lines.Add(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) + " " + name);
            }
            File.WriteAllLines(Path.Combine(ui, BundledUiAssets.ManifestName), lines);
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
