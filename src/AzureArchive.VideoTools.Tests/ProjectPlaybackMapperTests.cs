using AzureArchive.VideoTools.Core.Mapping;
using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Formats.Aap;

namespace AzureArchive.VideoTools.Tests;

internal static class ProjectPlaybackMapperTests
{
    public static void MapsOnlyExclusiveDialogueMatches()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot project = ReadProject(files, "mapping.aap", "A", "B");
        PlaybackArchiveSnapshot playback = Playback(
            files,
            "mapping.aas",
            Record(0, "synthetic"),
            Record(1, "A"),
            Record(2, "B"));

        var result = new ConservativeProjectPlaybackMapper().Map(project, playback);
        AssertEx.True(result.Success, result.Error);
        ProjectPlaybackMappingReport report = AssertEx.NotNull(result.Value);
        AssertEx.Equal(2, report.UniqueMappingCount);
        AssertEx.Equal(1, report.Scenes[0].PlaybackRecordIndex);
        AssertEx.Equal(2, report.Scenes[1].PlaybackRecordIndex);
        AssertEx.Equal(1, report.UnclaimedPlaybackRecordIndices.Count);
        AssertEx.Equal(0, report.UnclaimedPlaybackRecordIndices[0]);
    }

    public static void UniqueAnchorMapsAnEntireContiguousNode()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot project = ReadProject(
            files,
            "anchored.aap",
            "Anchor",
            "Repeat",
            "Repeat",
            string.Empty);
        PlaybackArchiveSnapshot playback = Playback(
            files,
            "anchored.aas",
            Record(0, "synthetic"),
            Record(1, "Anchor"),
            Record(2, "Repeat"),
            Record(3, "Repeat"),
            Record(4, string.Empty));

        ProjectPlaybackMappingReport report = AssertEx.NotNull(
            new ConservativeProjectPlaybackMapper().Map(project, playback).Value);
        AssertEx.Equal(4, report.UniqueMappingCount);
        AssertEx.Equal(
            ScenePlaybackMappingStatus.UniqueDialogueTextMatch,
            report.Scenes[0].Status);
        AssertEx.True(report.Scenes.Skip(1).All(
            scene => scene.Status == ScenePlaybackMappingStatus.AnchoredNodeSequenceMatch));
        AssertEx.Equal(4, report.Scenes[3].PlaybackRecordIndex);
    }

    public static void ContinuousCompilerStateDeltasDoNotBreakTextIdentity()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot project = ReadProject(files, "delta.aap", "A");
        PlaybackRecordSnapshot deltaRecord = Record(0, "A") with
        {
            BgmId = 999,
            BackgroundName = 123,
            BackgroundEffect = 456,
            VoiceJp = "compiled-voice"
        };

        ProjectPlaybackMappingReport report = AssertEx.NotNull(
            new ConservativeProjectPlaybackMapper().Map(
                project,
                Playback(files, "delta.aas", deltaRecord)).Value);
        AssertEx.Equal(1, report.UniqueMappingCount);
    }

    public static void DuplicateSemanticRecordsRemainAmbiguous()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot project = ReadProject(files, "duplicates.aap", "Same", "Same");
        PlaybackArchiveSnapshot playback = Playback(
            files,
            "duplicates.aas",
            Record(0, "Same"),
            Record(1, "Same"));

        ProjectPlaybackMappingReport report = AssertEx.NotNull(
            new ConservativeProjectPlaybackMapper().Map(project, playback).Value);
        AssertEx.Equal(0, report.UniqueMappingCount);
        AssertEx.True(report.Scenes.All(
            scene => scene.Status == ScenePlaybackMappingStatus.Ambiguous));
        AssertEx.True(report.Scenes.All(scene => scene.CandidateRecordIndices.Count == 2));
    }

    public static void OlderOrDifferentlyNamedPlaybackIsReported()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot project = ReadProject(files, "source.aap", "A");
        PlaybackArchiveSnapshot playback = Playback(files, "other.aas", Record(0, "A"));
        playback = playback with
        {
            Source = playback.Source with
            {
                LastWriteTimeUtc = project.Source.LastWriteTimeUtc - TimeSpan.FromMinutes(1)
            }
        };

        ProjectPlaybackMappingReport report = AssertEx.NotNull(
            new ConservativeProjectPlaybackMapper().Map(project, playback).Value);
        AssertEx.Contains(
            report.Issues,
            issue => issue.Code == "FileNameMismatch",
            "Expected a file-name mismatch issue.");
        AssertEx.Contains(
            report.Issues,
            issue => issue.Code == "PlaybackOlderThanProject",
            "Expected an older-playback issue.");
    }

    public static void NonPositionalPlaybackRecordIsRejected()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot project = ReadProject(files, "invalid-index.aap", "A");
        PlaybackArchiveSnapshot playback = Playback(
            files,
            "invalid-index.aas",
            Record(7, "A"));

        var result = new ConservativeProjectPlaybackMapper().Map(project, playback);
        AssertEx.False(result.Success);
        AssertEx.True(
            result.Error.Contains("positional index", StringComparison.OrdinalIgnoreCase),
            $"Unexpected mapping error: {result.Error}");
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
            DateTimeOffset.UtcNow);
        return new PlaybackArchiveSnapshot(
            source,
            "synthetic",
            Array.AsReadOnly(records));
    }

    private static PlaybackRecordSnapshot Record(int index, string text) => new(
        index,
        123,
        0,
        0,
        string.Empty,
        0,
        0,
        0,
        string.Empty,
        string.Empty,
        text,
        text,
        text,
        string.Empty,
        text,
        string.Empty,
        true,
        new string('A', 64));
}
