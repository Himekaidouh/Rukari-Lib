using System.Text.Json;
using System.Text.Json.Nodes;
using Rukari.CharacterVoice.Core;

/// <summary>
/// The cleanup the editor leaves behind when an imported lobby names audio that was never brought along: every
/// entry whose file is gone has to be removable, and nothing else may change.
/// </summary>
internal static class VoiceOverrideCleanupTests
{
    internal static void RunAll()
    {
        SeparatesPresentFromMissing();
        RemovesOnlyMissingEntriesAndKeepsTheRestOfTheManifest();
        IsIdempotentAndNeverTouchesANonVoicePath();
        DropsTheIndexLinesOfTheRemovedFiles();
        RefusesToRemoveAFileThatCameBack();
    }

    private static void SeparatesPresentFromMissing()
    {
        using var fixture = new Fixture();
        fixture.Voice("keep-1.ogg");
        fixture.Voice("keep-2.wav");
        fixture.Manifest("voices/keep-1.ogg", "voices/gone-1.ogg", "voices/keep-2.wav", "voices/gone-2.mp3");

        VoiceOverrideCleanupPlan plan = VoiceOverrideCleanupPolicy.Inspect(fixture.Project);
        Equal(4, plan.Total);
        Equal(2, plan.Present.Count);
        Equal(2, plan.Missing.Count);
        True(plan.Missing.Any(entry => entry.RelativePath == "voices/gone-1.ogg"));
        True(plan.Missing.Any(entry => entry.RelativePath == "voices/gone-2.mp3"));
    }

    private static void RemovesOnlyMissingEntriesAndKeepsTheRestOfTheManifest()
    {
        using var fixture = new Fixture();
        fixture.Voice("keep-1.ogg");
        fixture.ManifestWithExtras("voices/keep-1.ogg", "voices/gone.ogg");
        string originalManifest = File.ReadAllText(fixture.ManifestPath);

        VoiceOverrideCleanupPlan plan = VoiceOverrideCleanupPolicy.Inspect(fixture.Project);
        VoiceOverrideCleanupResult result = VoiceOverrideCleanupPolicy.Apply(plan, fixture.BackupRoot);

        Equal(1, result.RemovedCount);
        Equal(1, result.RemainingCount);
        True(Directory.Exists(result.BackupDirectory));
        True(File.Exists(Path.Combine(result.BackupDirectory, "manifest.json")));
        Equal(originalManifest, File.ReadAllText(Path.Combine(result.BackupDirectory, "manifest.json")));

        JsonNode manifest = JsonNode.Parse(File.ReadAllText(fixture.ManifestPath))!;
        JsonArray overrides = manifest["VoiceOverrides"]!.AsArray();
        Equal(1, overrides.Count);
        Equal("voices/keep-1.ogg", overrides[0]!.GetValue<string>());
        // Everything that is not a dead voice entry survives untouched.
        Equal("unchanged", manifest["Keep"]!.GetValue<string>());
        Equal(1, manifest["CharacterOverrides"]!.AsArray().Count);
        Equal("legacy", manifest["CharacterOverrides"]![0]!["Name"]!.GetValue<string>());
    }

    private static void IsIdempotentAndNeverTouchesANonVoicePath()
    {
        using var fixture = new Fixture();
        fixture.Voice("keep.ogg");
        fixture.Manifest("voices/keep.ogg", "voices/gone.ogg", "other/not-a-voice.ogg", "../escape.ogg",
            "voices/no-extension");

        VoiceOverrideCleanupPlan first = VoiceOverrideCleanupPolicy.Inspect(fixture.Project);
        Equal(1, first.Missing.Count);
        Equal("voices/gone.ogg", first.Missing[0].RelativePath);
        VoiceOverrideCleanupResult applied = VoiceOverrideCleanupPolicy.Apply(first, fixture.BackupRoot);
        Equal(1, applied.RemovedCount);

        // The non-voice strings stay exactly where they were: this module only manages voices/.
        JsonArray remaining = JsonNode.Parse(File.ReadAllText(fixture.ManifestPath))!["VoiceOverrides"]!.AsArray();
        Equal(4, remaining.Count);
        True(remaining.Any(item => item!.GetValue<string>() == "other/not-a-voice.ogg"));
        True(remaining.Any(item => item!.GetValue<string>() == "../escape.ogg"));

        VoiceOverrideCleanupPlan second = VoiceOverrideCleanupPolicy.Inspect(fixture.Project);
        Equal(0, second.Missing.Count);
        Equal(0, VoiceOverrideCleanupPolicy.Apply(second, fixture.BackupRoot).RemovedCount);
    }

    private static void DropsTheIndexLinesOfTheRemovedFiles()
    {
        using var fixture = new Fixture();
        fixture.Voice("keep.ogg");
        fixture.Manifest("voices/keep.ogg", "voices/gone.ogg");
        File.WriteAllText(fixture.IndexPath,
            "keep => [521521515] 1\ngone => [521521515] 2\nunrelated => []\n");

        VoiceOverrideCleanupPolicy.Apply(VoiceOverrideCleanupPolicy.Inspect(fixture.Project), fixture.BackupRoot);

        string[] lines = File.ReadAllLines(fixture.IndexPath);
        Equal(2, lines.Length);
        True(lines[0].StartsWith("keep ", StringComparison.Ordinal));
        True(lines[1].StartsWith("unrelated ", StringComparison.Ordinal));
    }

    private static void RefusesToRemoveAFileThatCameBack()
    {
        using var fixture = new Fixture();
        fixture.Manifest("voices/late.ogg");
        VoiceOverrideCleanupPlan plan = VoiceOverrideCleanupPolicy.Inspect(fixture.Project);
        Equal(1, plan.Missing.Count);

        fixture.Voice("late.ogg");
        VoiceOverrideCleanupResult result = VoiceOverrideCleanupPolicy.Apply(plan, fixture.BackupRoot);
        Equal(0, result.RemovedCount);
        Equal(1, JsonNode.Parse(File.ReadAllText(fixture.ManifestPath))!["VoiceOverrides"]!.AsArray().Count);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "rukari-voice-cleanup-tests-" + Guid.NewGuid().ToString("N"));
        internal string Project => Path.Combine(Root, "project");
        internal string BackupRoot => Path.Combine(Root, "backups");
        internal string ManifestPath => Path.Combine(Project, "manifest.json");
        internal string IndexPath => Path.Combine(Project, "voices", "voices.txt");

        internal Fixture()
        {
            Directory.CreateDirectory(Path.Combine(Project, "voices"));
            Directory.CreateDirectory(BackupRoot);
        }

        internal void Voice(string relative)
        {
            // Manifest entries are "voices/<name>", so the fixture writes there too.
            string path = Path.Combine(Project, "voices", relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
        }

        internal void Manifest(params string[] entries) => File.WriteAllText(ManifestPath,
            JsonSerializer.Serialize(new { VoiceOverrides = entries }));

        internal void ManifestWithExtras(params string[] entries) => File.WriteAllText(ManifestPath,
            JsonSerializer.Serialize(new
            {
                Keep = "unchanged",
                CharacterOverrides = new[] { new { Identifier = "521521515", Name = "legacy" } },
                VoiceOverrides = entries,
                PopupOverrides = Array.Empty<string>(),
            }));

        public void Dispose()
        {
            string full = Path.GetFullPath(Root);
            string temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(full).StartsWith("rukari-voice-cleanup-tests-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Unexpected fixture cleanup target.");
            }

            Directory.Delete(full, recursive: true);
        }
    }

    private static void True(bool value) { if (!value) throw new InvalidOperationException("Cleanup assertion failed."); }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}; got {actual}.");
    }
}
