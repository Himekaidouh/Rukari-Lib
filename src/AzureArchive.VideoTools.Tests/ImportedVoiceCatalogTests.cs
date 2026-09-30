using AzureArchive.VideoTools.Core.Voices;

namespace AzureArchive.VideoTools.Tests;

internal static class ImportedVoiceCatalogTests
{
    public static void ShortBindingKeysResolveOnlyUniqueSegmentSuffixes()
    {
        AssertEx.Equal("sounds/凯伊/line_001", ImportedVoiceCatalogPolicy.ResolveBindingKey(
            "line_001", new[] { "sounds/凯伊/line_001", "sounds/line_002" }));
        AssertEx.Equal("sounds/凯伊/line_001", ImportedVoiceCatalogPolicy.ResolveBindingKey(
            "凯伊\\line_001", new[] { "sounds/凯伊/line_001", "sounds/line_002" }));
        AssertEx.True(ImportedVoiceCatalogPolicy.ResolveBindingKey(
            "line_001", new[] { "sounds/凯伊/line_001", "sounds/爱丽丝/line_001" }) is null);
        AssertEx.True(ImportedVoiceCatalogPolicy.ResolveBindingKey(
            "line_001", new[] { "sounds/otherline_001", "sounds/line_001_extra" }) is null);
        AssertEx.True(ImportedVoiceCatalogPolicy.ResolveBindingKey(
            "LINE_001", new[] { "sounds/line_001" }) is null);
    }

    public static void ExactBindingKeyTakesPriorityAndRepeatedCatalogKeysAreNotAmbiguous()
    {
        AssertEx.Equal("sounds/line", ImportedVoiceCatalogPolicy.ResolveBindingKey(
            "sounds/line", new[] { "other/sounds/line", "sounds/line" }));
        AssertEx.Equal("sounds/line", ImportedVoiceCatalogPolicy.ResolveBindingKey(
            "line", new[] { "sounds/line", "sounds/line" }));
        AssertEx.True(ImportedVoiceCatalogPolicy.ResolveBindingKey(null, new[] { "sounds/line" }) is null);
        AssertEx.True(ImportedVoiceCatalogPolicy.ResolveBindingKey("../line", new[] { "sounds/line" }) is null);
    }

    public static void SameFilenameInDifferentDirectoriesGetsDistinctKeysAndLabels()
    {
        var entries = ImportedVoiceCatalogPolicy.CreateCandidates(new[]
        {
            Item("sounds/凯伊/hello.wav", "C:/project/sounds/凯伊/hello.wav"),
            Item("sounds/爱丽丝/hello.wav", "C:/project/sounds/爱丽丝/hello.wav")
        });
        AssertEx.Equal(2, entries.Count);
        AssertEx.Equal(2, entries.Select(entry => entry.ResourceKey).Distinct().Count());
        AssertEx.Equal(2, entries.Select(entry => entry.DisplayName).Distinct().Count());
    }

    public static void AmbiguousNativeSuffixesAndFormatsAreExcluded()
    {
        var entries = ImportedVoiceCatalogPolicy.CreateCandidates(new[]
        {
            Item("sounds/line.wav", "C:/global/sounds/line.wav"),
            Item("nested/sounds/line.ogg", "C:/project/nested/sounds/line.ogg"),
            Item("sounds/duplicate.wav", "C:/project/sounds/duplicate.wav"),
            Item("sounds/duplicate.MP3", "C:/project/sounds/duplicate.MP3")
        });
        AssertEx.Equal(1, entries.Count);
        AssertEx.Equal("nested/sounds/line", entries[0].ResourceKey);
    }

    public static void CanonicalKeysKeepExtensionLikeStemsAndSkipInvalidPaths()
    {
        var entries = ImportedVoiceCatalogPolicy.CreateCandidates(new[]
        {
            Item("sounds/foo.wav.ogg", "C:/project/sounds/foo.wav.ogg"),
            Item("sounds/../bad.wav", "C:/project/bad.wav"),
            Item("sounds/upper.WAV", "C:/project/sounds/upper.WAV"),
            Item("sounds/bad;name.wav", "C:/project/sounds/bad;name.wav")
        });
        AssertEx.Equal(1, entries.Count);
        AssertEx.Equal("sounds/foo.wav", entries[0].ResourceKey);
        AssertEx.True(VoiceDirectivePolicy.TryDecodeNativeIdentifier(
            VoiceDirectivePolicy.NativeIdentifierPrefix + entries[0].ResourceKey, out string key));
        AssertEx.Equal("sounds/foo.wav", key);
    }

    public static void DuplicatePhysicalPathsAreDeduplicatedAndEntriesAreSorted()
    {
        var entries = ImportedVoiceCatalogPolicy.CreateCandidates(new[]
        {
            Item("sounds/Zed.mp3", "C:/project/sounds/Zed.mp3"),
            Item("sounds/alpha.wav", "C:/project/sounds/alpha.wav"),
            Item("sounds/alpha.wav", "c:/PROJECT/sounds/alpha.wav")
        });
        AssertEx.Equal(2, entries.Count);
        AssertEx.Equal("alpha.wav", entries[0].DisplayName);
        AssertEx.Equal("Zed.mp3", entries[1].DisplayName);
    }

    private static ImportedSoundOverrideSnapshot Item(string path, string fullPath) => new(path, fullPath);
}
