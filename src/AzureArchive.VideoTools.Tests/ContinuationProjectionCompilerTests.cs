using AzureArchive.VideoTools.Core.Compilation;
using AzureArchive.VideoTools.Core.Continuity;
using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Formats.Aap;

namespace AzureArchive.VideoTools.Tests;

internal static class ContinuationProjectionCompilerTests
{
    public static void CompilesShaBoundContiguousAppendChain()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot project = ReadProject(files, "projection.aap", "A", "B", "C", "D");
        SceneSnapshot[] scenes = project.ScriptNodes.Single().Scenes.ToArray();
        PlaybackArchiveSnapshot playback = Playback(
            project,
            "synthetic",
            "A",
            "B",
            "C",
            "D");
        ContinuationDocument metadata = DocumentFor(
            project,
            new ContinuationRule(scenes[1].Key),
            new ContinuationRule(scenes[2].Key));

        var result = new ContinuationProjectionCompiler().Compile(
            project,
            playback,
            metadata);

        AssertEx.True(result.Success, result.Error);
        ContinuationPlaybackProjection projection = AssertEx.NotNull(result.Value);
        AssertEx.Equal(ContinuationPlaybackProjection.CurrentSchemaVersion, projection.SchemaVersion);
        AssertEx.Equal(metadata.ProjectId, projection.ProjectId);
        AssertEx.Equal(project.Source.PathKey, projection.ProjectPathKey);
        AssertEx.Equal(project.Source.RevisionSha256, projection.ProjectRevisionSha256);
        AssertEx.Equal(playback.Source.PathKey, projection.PlaybackPathKey);
        AssertEx.Equal(playback.Source.RevisionSha256, projection.PlaybackRevisionSha256);
        AssertEx.Equal(playback.SchemaName, projection.PlaybackSchemaName);
        AssertEx.Equal(2, projection.Instructions.Count);

        ContinuationPlaybackInstruction second = projection.Instructions[0];
        AssertEx.Equal(scenes[1].Key, second.Scene);
        AssertEx.Equal(scenes[0].Key, second.PreviousScene);
        AssertEx.Equal(scenes[0].Key, second.ChainStartScene);
        AssertEx.Equal(2, second.PlaybackRecordIndex);
        AssertEx.Equal(1, second.PreviousPlaybackRecordIndex);
        AssertEx.Equal(1, second.ChainStartPlaybackRecordIndex);
        AssertEx.Equal(Fingerprint(2), second.PlaybackRecordFingerprint);
        AssertEx.Equal("A", second.ExpectedLockedPrefix);
        AssertEx.Equal("B", second.Suffix);
        AssertEx.Equal("AB", second.ResultText);
        AssertEx.Equal(1, second.TypewriterStartCharacterIndex);

        ContinuationPlaybackInstruction third = projection.Instructions[1];
        AssertEx.Equal(scenes[2].Key, third.Scene);
        AssertEx.Equal(scenes[1].Key, third.PreviousScene);
        AssertEx.Equal(scenes[0].Key, third.ChainStartScene);
        AssertEx.Equal(3, third.PlaybackRecordIndex);
        AssertEx.Equal(2, third.PreviousPlaybackRecordIndex);
        AssertEx.Equal(1, third.ChainStartPlaybackRecordIndex);
        AssertEx.Equal("AB", third.ExpectedLockedPrefix);
        AssertEx.Equal("C", third.Suffix);
        AssertEx.Equal("ABC", third.ResultText);
        AssertEx.Equal(2, third.TypewriterStartCharacterIndex);
    }

    public static void RejectsUnmappedMemberOfContinuationChain()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot project = ReadProject(files, "ambiguous-chain.aap", "A", "B");
        SceneSnapshot continued = project.ScriptNodes.Single().Scenes[1];
        PlaybackArchiveSnapshot playback = Playback(
            project,
            "A",
            "A",
            "synthetic",
            "B");

        var result = new ContinuationProjectionCompiler().Compile(
            project,
            playback,
            DocumentFor(project, new ContinuationRule(continued.Key)));

        AssertEx.False(result.Success);
        AssertEx.True(
            result.Error.Contains("requires a unique playback mapping", StringComparison.Ordinal),
            result.Error);
    }

    public static void RejectsNonContiguousPlaybackChain()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot project = ReadProject(files, "record-gap.aap", "A", "B");
        SceneSnapshot continued = project.ScriptNodes.Single().Scenes[1];
        PlaybackArchiveSnapshot playback = Playback(project, "A", "synthetic", "B");

        var result = new ContinuationProjectionCompiler().Compile(
            project,
            playback,
            DocumentFor(project, new ContinuationRule(continued.Key)));

        AssertEx.False(result.Success);
        AssertEx.True(
            result.Error.Contains("non-contiguous playback record", StringComparison.Ordinal),
            result.Error);
    }

    public static void RejectsStaleProjectRevision()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot project = ReadProject(files, "stale-projection.aap", "A", "B");
        SceneSnapshot continued = project.ScriptNodes.Single().Scenes[1];
        ContinuationDocument stale = DocumentFor(
            project,
            new ContinuationRule(continued.Key)) with
        {
            ProjectRevisionSha256 = new string('0', 64)
        };

        var result = new ContinuationProjectionCompiler().Compile(
            project,
            Playback(project, "A", "B"),
            stale);

        AssertEx.False(result.Success);
        AssertEx.True(
            result.Error.Contains("exact current AAP revision", StringComparison.Ordinal),
            result.Error);
    }

    public static void RejectsOlderPlaybackArchive()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot project = ReadProject(files, "older-playback.aap", "A", "B");
        SceneSnapshot continued = project.ScriptNodes.Single().Scenes[1];
        PlaybackArchiveSnapshot current = Playback(project, "A", "B");
        PlaybackArchiveSnapshot playback = current with
        {
            Source = current.Source with
            {
                LastWriteTimeUtc = project.Source.LastWriteTimeUtc - TimeSpan.FromMinutes(1)
            }
        };

        var result = new ContinuationProjectionCompiler().Compile(
            project,
            playback,
            DocumentFor(project, new ContinuationRule(continued.Key)));

        AssertEx.False(result.Success);
        AssertEx.True(
            result.Error.Contains("PlaybackOlderThanProject", StringComparison.Ordinal),
            result.Error);
    }

    public static void RejectsInvalidPlaybackFingerprint()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot project = ReadProject(files, "bad-record.aap", "A", "B");
        SceneSnapshot continued = project.ScriptNodes.Single().Scenes[1];
        PlaybackArchiveSnapshot valid = Playback(project, "A", "B");
        PlaybackRecordSnapshot[] records = valid.Records.ToArray();
        records[1] = records[1] with { Fingerprint = "not-a-sha" };
        PlaybackArchiveSnapshot malformed = valid with
        {
            Records = Array.AsReadOnly(records)
        };

        var result = new ContinuationProjectionCompiler().Compile(
            project,
            malformed,
            DocumentFor(project, new ContinuationRule(continued.Key)));

        AssertEx.False(result.Success);
        AssertEx.True(
            result.Error.Contains("fingerprint", StringComparison.OrdinalIgnoreCase),
            result.Error);
    }

    public static void DisabledRuleProducesNoRuntimeInstruction()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot project = ReadProject(files, "disabled-rule.aap", "A", "B");
        SceneSnapshot continued = project.ScriptNodes.Single().Scenes[1];

        var result = new ContinuationProjectionCompiler().Compile(
            project,
            Playback(project, "A", "B"),
            DocumentFor(project, new ContinuationRule(continued.Key, Enabled: false)));

        AssertEx.True(result.Success, result.Error);
        AssertEx.Equal(0, AssertEx.NotNull(result.Value).Instructions.Count);
    }

    public static void MalformedProjectSnapshotFailsWithoutThrowing()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot project = ReadProject(files, "malformed-project.aap", "A", "B");
        SceneSnapshot continued = project.ScriptNodes.Single().Scenes[1];
        ProjectSnapshot malformed = project with { Nodes = null! };

        var result = new ContinuationProjectionCompiler().Compile(
            malformed,
            Playback(project, "A", "B"),
            DocumentFor(project, new ContinuationRule(continued.Key)));

        AssertEx.False(result.Success);
        AssertEx.True(
            result.Error.Contains("node collection", StringComparison.OrdinalIgnoreCase),
            result.Error);
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
        params string[] dialogue)
    {
        string fullPath = Path.ChangeExtension(project.Source.FullPath, ".aas");
        var source = new PlaybackArchiveSourceSnapshot(
            fullPath,
            new string('B', 64),
            new string('C', 64),
            dialogue.Length,
            project.Source.LastWriteTimeUtc + TimeSpan.FromSeconds(1));
        PlaybackRecordSnapshot[] records = dialogue
            .Select((text, index) => Record(index, text))
            .ToArray();
        return new PlaybackArchiveSnapshot(
            source,
            "synthetic/v1",
            Array.AsReadOnly(records));
    }

    private static PlaybackRecordSnapshot Record(int index, string text) => new(
        index,
        0,
        0,
        0,
        string.Empty,
        0,
        0,
        0,
        string.Empty,
        string.Empty,
        text,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        false,
        Fingerprint(index));

    private static string Fingerprint(int index) => index.ToString("X64");

    private static ContinuationDocument DocumentFor(
        ProjectSnapshot project,
        params ContinuationRule[] rules) => new(
        ContinuationDocument.CurrentSchemaVersion,
        Guid.NewGuid().ToString("D"),
        project.Source.PathKey,
        project.Source.RevisionSha256,
        Array.AsReadOnly(rules));
}
