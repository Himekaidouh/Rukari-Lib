using System.Security.Cryptography;
using System.Text;
using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Mapping;
using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Formats.Aap;

namespace AzureArchive.VideoTools.Tests;

internal static class ObservedSceneIdentityResolverTests
{
    public static void MapsExactCompiledScriptIdentityToProjectScene()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot project = ReadProject(files, "mapped.aap", "A");
        const string compiledScript = "3;actor;01;A\n#wait;500\n";
        PlaybackArchiveSnapshot playback = Playback(
            project,
            files,
            "mapped.aas",
            Record(0, "A", compiledScript));

        var result = new ObservedSceneIdentityResolver().Resolve(
            Identity(compiledScript),
            project,
            playback);

        AssertEx.True(result.Success, result.Error);
        ObservedSceneIdentityResolution resolution = AssertEx.NotNull(result.Value);
        AssertEx.Equal(ObservedSceneIdentityStatus.Mapped, resolution.Status);
        AssertEx.Equal(0, resolution.PlaybackRecordIndex);
        AssertEx.Equal(project.ScriptNodes.Single().Scenes[0].Key, resolution.Scene);
        AssertEx.True(resolution.SelectedSceneTrusted);
        AssertEx.True(resolution.ArchivePairFresh);
        AssertEx.True(resolution.PairingTrusted);
    }

    public static void DuplicateCompiledScriptsRemainAmbiguous()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot project = ReadProject(files, "duplicate.aap", "A", "B");
        const string compiledScript = "same-compiled-script";
        PlaybackArchiveSnapshot playback = Playback(
            project,
            files,
            "duplicate.aas",
            Record(0, "A", compiledScript),
            Record(1, "B", compiledScript));

        ObservedSceneIdentityResolution resolution = AssertEx.NotNull(
            new ObservedSceneIdentityResolver().Resolve(
                Identity(compiledScript),
                project,
                playback).Value);

        AssertEx.Equal(
            ObservedSceneIdentityStatus.AmbiguousPlaybackScript,
            resolution.Status);
        AssertEx.Equal(2, resolution.CandidatePlaybackRecordIndices.Count);
        AssertEx.False(resolution.SelectedSceneTrusted);
        AssertEx.False(resolution.ArchivePairFresh);
        AssertEx.False(resolution.PairingTrusted);
    }

    public static void EditorProjectionMapsSanitizedContaminatedPlaybackWithoutMutation()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot project = ReadProject(files, "projected.aap", "A");
        const string sanitizedScript = "3;actor;01;A\n#wait;500\n";
        const string contaminatedScript = sanitizedScript
            + "#aavt;char;3;move;dx=500;duration=800;easing=easeInOut\n";
        PlaybackArchiveSnapshot playback = Playback(
            project,
            files,
            "projected.aas",
            Record(0, "A", contaminatedScript));
        var extractor = new EmbeddedAavtDirectiveExtractor();

        var result = new ObservedSceneIdentityResolver(
            compiledScriptProjection: script => extractor.Extract(script).SanitizedText)
            .Resolve(Identity(sanitizedScript), project, playback);

        AssertEx.True(result.Success, result.Error);
        ObservedSceneIdentityResolution resolution = AssertEx.NotNull(result.Value);
        AssertEx.Equal(ObservedSceneIdentityStatus.Mapped, resolution.Status);
        AssertEx.Equal(0, resolution.PlaybackRecordIndex);
        AssertEx.Equal(contaminatedScript, playback.Records[0].CompiledScript);
    }

    public static void ExactPlaybackRecordWithoutProjectMappingIsReported()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot project = ReadProject(files, "unmapped.aap", "A");
        const string compiledScript = "compiled-B";
        PlaybackArchiveSnapshot playback = Playback(
            project,
            files,
            "unmapped.aas",
            Record(0, "B", compiledScript));

        ObservedSceneIdentityResolution resolution = AssertEx.NotNull(
            new ObservedSceneIdentityResolver().Resolve(
                Identity(compiledScript),
                project,
                playback).Value);

        AssertEx.Equal(ObservedSceneIdentityStatus.PlaybackRecordOnly, resolution.Status);
        AssertEx.Equal(0, resolution.PlaybackRecordIndex);
        AssertEx.True(resolution.Scene == null);
        AssertEx.False(resolution.SelectedSceneTrusted);
        AssertEx.True(resolution.ArchivePairFresh);
        AssertEx.False(resolution.PairingTrusted);
    }

    public static void OlderPlaybackCanBeObservedButIsNotTrusted()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot project = ReadProject(files, "stale.aap", "A");
        const string compiledScript = "compiled-A";
        PlaybackArchiveSnapshot playback = Playback(
            project,
            files,
            "stale.aas",
            Record(0, "A", compiledScript));
        playback = playback with
        {
            Source = playback.Source with
            {
                LastWriteTimeUtc = project.Source.LastWriteTimeUtc - TimeSpan.FromMinutes(1)
            }
        };

        ObservedSceneIdentityResolution resolution = AssertEx.NotNull(
            new ObservedSceneIdentityResolver().Resolve(
                Identity(compiledScript),
                project,
                playback).Value);

        AssertEx.Equal(ObservedSceneIdentityStatus.Mapped, resolution.Status);
        AssertEx.True(resolution.SelectedSceneTrusted);
        AssertEx.False(resolution.ArchivePairFresh);
        AssertEx.False(resolution.PairingTrusted);
        AssertEx.Contains(
            resolution.MappingIssues,
            issue => issue.Code == "PlaybackOlderThanProject",
            "Expected stale playback to remain visible but untrusted.");
    }

    public static void UnrelatedAmbiguityDoesNotInvalidateSelectedSceneIdentity()
    {
        using var files = new TemporaryAapDirectory();
        string projectPath = files.Write(
            "partial.aap",
            SyntheticAap.ProjectWithTwoScriptNodes(
                new[] { SyntheticAap.Scene("A") },
                new[] { SyntheticAap.Scene("Same"), SyntheticAap.Scene("Same") }));
        var projectResult = new AapProjectReader().Read(projectPath);
        AssertEx.True(projectResult.Success, projectResult.Error);
        ProjectSnapshot project = AssertEx.NotNull(projectResult.Value);
        const string selectedScript = "compiled-A";
        PlaybackArchiveSnapshot playback = Playback(
            project,
            files,
            "partial.aas",
            Record(0, "A", selectedScript),
            Record(1, "Same", "compiled-same-1"),
            Record(2, "Same", "compiled-same-2"));

        ObservedSceneIdentityResolution resolution = AssertEx.NotNull(
            new ObservedSceneIdentityResolver().Resolve(
                Identity(selectedScript),
                project,
                playback).Value);

        AssertEx.Equal(ObservedSceneIdentityStatus.Mapped, resolution.Status);
        AssertEx.True(resolution.SelectedSceneTrusted);
        AssertEx.True(resolution.ArchivePairFresh);
        AssertEx.False(resolution.PairingTrusted);
        AssertEx.Contains(
            resolution.MappingIssues,
            issue => issue.Code == "AmbiguousScenes",
            "Expected unrelated ambiguous scenes to retain strict pairing diagnostics.");
    }

    public static void FileNameMismatchInvalidatesSelectedSceneTrust()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot project = ReadProject(files, "source.aap", "A");
        const string compiledScript = "compiled-A";
        PlaybackArchiveSnapshot playback = Playback(
            project,
            files,
            "other.aas",
            Record(0, "A", compiledScript));

        ObservedSceneIdentityResolution resolution = AssertEx.NotNull(
            new ObservedSceneIdentityResolver().Resolve(
                Identity(compiledScript),
                project,
                playback).Value);

        AssertEx.Equal(ObservedSceneIdentityStatus.Mapped, resolution.Status);
        AssertEx.False(resolution.SelectedSceneTrusted);
        AssertEx.False(resolution.ArchivePairFresh);
        AssertEx.False(resolution.PairingTrusted);
        AssertEx.Contains(
            resolution.MappingIssues,
            issue => issue.Code == "FileNameMismatch",
            "Expected a mismatched configured pair to remain untrusted.");
    }

    public static void MalformedIdentityFailsClosed()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot project = ReadProject(files, "invalid.aap", "A");
        PlaybackArchiveSnapshot playback = Playback(
            project,
            files,
            "invalid.aas",
            Record(0, "A", "compiled-A"));

        var result = new ObservedSceneIdentityResolver().Resolve(
            new ObservedCompiledSceneIdentity("not-a-sha256", 10, 1),
            project,
            playback);

        AssertEx.False(result.Success);
        AssertEx.True(
            result.Error.Contains("SHA-256", StringComparison.Ordinal),
            $"Unexpected identity validation error: {result.Error}");
    }

    private static ObservedCompiledSceneIdentity Identity(string compiledScript)
    {
        string sha256 = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(compiledScript)));
        return new ObservedCompiledSceneIdentity(
            sha256,
            compiledScript.Length,
            compiledScript.Count(character => character == '\n') + 1);
    }

    private static ProjectSnapshot ReadProject(
        TemporaryAapDirectory files,
        string fileName,
        params string[] dialogue)
    {
        string path = files.Write(
            fileName,
            SyntheticAap.Project(
                dialogue.Select(text => SyntheticAap.Scene(text)).ToArray()));
        var result = new AapProjectReader().Read(path);
        AssertEx.True(result.Success, result.Error);
        return AssertEx.NotNull(result.Value);
    }

    private static PlaybackArchiveSnapshot Playback(
        ProjectSnapshot project,
        TemporaryAapDirectory files,
        string fileName,
        params PlaybackRecordSnapshot[] records)
    {
        string fullPath = Path.Combine(files.Root, fileName);
        var source = new PlaybackArchiveSourceSnapshot(
            fullPath,
            new string('1', 64),
            new string('2', 64),
            1,
            project.Source.LastWriteTimeUtc);
        return new PlaybackArchiveSnapshot(
            source,
            "synthetic",
            Array.AsReadOnly(records));
    }

    private static PlaybackRecordSnapshot Record(
        int index,
        string text,
        string compiledScript) => new(
        index,
        123,
        0,
        0,
        string.Empty,
        0,
        0,
        0,
        string.Empty,
        compiledScript,
        text,
        text,
        text,
        string.Empty,
        text,
        string.Empty,
        true,
        new string('A', 64));
}
