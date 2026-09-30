using Rukari.Lib.Tools;

namespace Rukari.Lib.Tests;

/// <summary>
/// The atlas export is generated outside the game and read at runtime, so its two failure modes matter more than
/// its happy path: a document that parses into wrong rectangles (a re-export must never leave a stale coordinate
/// behind) and a document that throws instead of reporting itself unusable (which would take the rail down with it).
/// </summary>
internal static class AtlasSpriteCatalogTests
{
    private const string SampleJson = @"{
  ""exporter"": ""UnityPy 1.25.2"",
  ""atlases"": [ { ""name"": ""Common"", ""sprite_count"": 380 } ],
  ""total_sprites"": 3,
  ""sprites"": [
    { ""name"": ""Common_Btn_Normal_Y_S_Pt"", ""x"": 937, ""y"": 1697, ""width"": 251, ""height"": 140,
      ""borderLeft"": 0, ""borderRight"": 0, ""borderTop"": 0, ""borderBottom"": 0, ""paddingLeft"": 16 },
    { ""name"": ""Campaign_List_Normal"", ""x"": 1096, ""y"": 1908, ""width"": 52, ""height"": 60,
      ""borderLeft"": 25, ""borderTop"": 24, ""borderRight"": 24, ""borderBottom"": 30 },
    { ""name"": ""Common_Bg_Raius20px"", ""x"": 1438, ""y"": 461, ""width"": 54, ""height"": 54,
      ""borderLeft"": 22, ""borderTop"": 22, ""borderRight"": 22, ""borderBottom"": 22 }
  ]
}";

    internal static void AtlasMetadataGivesEveryRecordedSpriteItsRectangleAndBorder()
    {
        Check.True(AtlasSpriteCatalog.TryParse(SampleJson, out var regions, out string? error), $"Metadata must parse: {error}");
        Check.Equal(3, regions.Count, "Every sprite record must be kept.");

        Check.True(regions.TryGetValue("Common_Btn_Normal_Y_S_Pt", out AtlasSpriteRegion plate), "The official button plate must be found.");
        Check.Equal(937, plate.X, "Plate X.");
        Check.Equal(1697, plate.Y, "Plate Y.");
        Check.Equal(251, plate.Width, "Plate width.");
        Check.Equal(140, plate.Height, "Plate height.");
        Check.True(!plate.HasBorder, "The official button plate carries no nine-slice border; its diagonal edge is baked into the pixels.");

        Check.True(regions.TryGetValue("Campaign_List_Normal", out AtlasSpriteRegion row), "The list row must be found.");
        Check.Equal(25, row.BorderLeft, "Row left border.");
        Check.Equal(24, row.BorderTop, "Row top border.");
        Check.Equal(24, row.BorderRight, "Row right border.");
        Check.Equal(30, row.BorderBottom, "Row bottom border.");
        Check.True(row.HasBorder, "A bordered row must report itself as nine-sliceable.");

        Check.True(regions.TryGetValue("Common_Bg_Raius20px", out AtlasSpriteRegion panel), "The rounded panel must be found.");
        Check.Equal(22, panel.BorderLeft, "Panel left border.");
        Check.Equal(22, panel.BorderBottom, "Panel bottom border.");
    }

    internal static void AnUnusableAtlasMetadataDocumentIsRejectedInsteadOfThrowing()
    {
        string?[] broken =
        {
            null,
            "",
            "   ",
            "{",
            "{}",
            @"{ ""total_sprites"": 0 }",
            @"{ ""sprites"": [] }",
            @"{ ""sprites"": [ { ""name"": """", ""x"": 0, ""y"": 0, ""width"": 0, ""height"": 0 } ] }",
            @"{ ""sprites"": [ { ""name"": ""NoRectangle"" } ] }"
        };

        foreach (string? document in broken)
        {
            bool parsed = AtlasSpriteCatalog.TryParse(document, out var regions, out string? error);
            Check.True(!parsed, $"A document without usable sprites must not parse: {document}");
            Check.True(error is not null, "A rejected document must say why.");
            Check.Equal(0, regions.Count, "A rejected document must yield no regions.");
        }
    }

    internal static void ExportPathsPairAnAtlasImageWithItsMetadata()
    {
        const string root = @"E:\asseet\AA1.0_UI_20260913_180259";
        Check.Equal(Path.Combine(root, "metadata", "Common.json"),
            AtlasExportPaths.MetadataFileFor(Path.Combine(root, "atlases", "Common.png")),
            "The metadata file must be derived from the atlas image beside it, so a relocated export keeps working.");
        Check.Equal(Path.Combine(root, "metadata", "Studio.json"),
            AtlasExportPaths.MetadataFileFor(Path.Combine(root, "atlases", "Studio.png")),
            "Every atlas, not just Common, must find its own metadata.");
        Check.True(AtlasExportPaths.MetadataFileFor(null) is null, "A missing path cannot produce a metadata file.");
        Check.True(AtlasExportPaths.MetadataFileFor("Common.png") is null, "A bare file name has no pair.");
        Check.True(AtlasExportPaths.AtlasCandidates("").Count == 0, "An unnamed atlas has no candidates.");

        if (Environment.GetEnvironmentVariable("RUKARI_ATLAS_EXPORT") is null)
        {
            Check.True(AtlasExportPaths.Roots.Contains(root), "The export root this machine used must stay a candidate.");
            Check.True(AtlasExportPaths.AtlasCandidates("Studio").Contains(Path.Combine(root, "atlases", "Studio.png")),
                "A second atlas must be reachable, not just Common.");
            Check.True(AtlasExportPaths.DecorationCandidates("Image_AngleBtn_Deco").Contains(Path.Combine(root, "decorations", "Image_AngleBtn_Deco.png")),
                "Decoration textures must resolve, with the extension optional.");
            Check.True(AtlasExportPaths.DecorationCandidates("Image_AngleBtn_Deco.png").Contains(Path.Combine(root, "decorations", "Image_AngleBtn_Deco.png")),
                "Naming the extension must not double it.");
        }
    }

    /// <summary>
    /// The parser is only worth trusting against the document it will actually meet. The export is this machine's
    /// own decoded copy, so the check is skipped where it is absent rather than failing a clean checkout.
    /// </summary>
    internal static void TheDecodedExportOnDiskAgreesWithTheParser()
    {
        const string root = @"E:\asseet\AA1.0_UI_20260913_180259";
        string common = Path.Combine(root, "metadata", "Common.json");
        string studio = Path.Combine(root, "metadata", "Studio.json");
        if (!File.Exists(common) || !File.Exists(studio)) return;

        Check.True(AtlasSpriteCatalog.TryParse(File.ReadAllText(common), out var regions, out string? error),
            $"The real Common metadata must parse: {error}");
        Check.True(regions.Count > 300, $"The real Common metadata carries hundreds of sprites, saw {regions.Count}.");

        Check.True(regions.TryGetValue("Common_Btn_Normal_Y_S_Pt", out AtlasSpriteRegion plate), "The yellow official button plate must be recorded.");
        Check.Equal(937, plate.X, "Recorded plate X must match the verified export.");
        Check.Equal(1697, plate.Y, "Recorded plate Y must match the verified export.");
        Check.Equal(251, plate.Width, "Recorded plate width must match the verified export.");
        Check.Equal(140, plate.Height, "Recorded plate height must match the verified export.");
        Check.True(!plate.HasBorder, "The yellow official button plate must stay unscaled by slicing.");

        Check.True(regions.TryGetValue("Common_Bg_Raius10px", out AtlasSpriteRegion row), "The row plate must be recorded.");
        Check.Equal(10, row.BorderLeft, "The row plate's border is what makes it stretchable.");

        Check.True(AtlasSpriteCatalog.TryParse(File.ReadAllText(studio), out var studioRegions, out string? studioError),
            $"The real Studio metadata must parse: {studioError}");
        Check.True(studioRegions.TryGetValue("save", out AtlasSpriteRegion save), "The editor's save glyph must be recorded in Studio.");
        Check.Equal(0, save.X, "The save glyph X must match the verified export.");
        Check.Equal(146, save.Y, "The save glyph Y must match the verified export.");
        Check.Equal(128, save.Width, "The save glyph is square.");
        Check.Equal(128, save.Height, "The save glyph is square.");
    }
}
