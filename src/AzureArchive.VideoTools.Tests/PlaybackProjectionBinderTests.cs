using AzureArchive.VideoTools.Core.Compilation;
using AzureArchive.VideoTools.Core.Continuity;
using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Formats.Aap;

namespace AzureArchive.VideoTools.Tests;

internal static class PlaybackProjectionBinderTests
{
    public static void FindsInstructionByExactPlaybackRecord()
    {
        (PlaybackArchiveSnapshot playback, ContinuationPlaybackProjection projection) =
            BuildProjection();

        var bound = new PlaybackProjectionBinder().Bind(playback, projection);

        AssertEx.True(bound.Success, bound.Error);
        IPlaybackProjectionIndex index = AssertEx.NotNull(bound.Value);
        AssertEx.Equal(playback.Records.Count, index.RecordCount);
        AssertEx.Equal(1, index.InstructionCount);

        var ordinary = index.Observe(0);
        AssertEx.True(ordinary.Success, ordinary.Error);
        AssertEx.False(AssertEx.NotNull(ordinary.Value).HasInstruction);

        var continued = index.Observe(1);
        AssertEx.True(continued.Success, continued.Error);
        PlaybackProjectionObservation observation = AssertEx.NotNull(continued.Value);
        AssertEx.True(observation.HasInstruction);
        AssertEx.Equal("A", observation.Instruction!.ExpectedLockedPrefix);
        AssertEx.Equal("B", observation.Instruction.Suffix);
        AssertEx.Equal("AB", observation.Instruction.ResultText);
    }

    public static void BindsPlaybackWithoutProjectionForObservationOnly()
    {
        (PlaybackArchiveSnapshot playback, _) = BuildProjection();

        var bound = new PlaybackProjectionBinder().Bind(playback, null);

        AssertEx.True(bound.Success, bound.Error);
        IPlaybackProjectionIndex index = AssertEx.NotNull(bound.Value);
        AssertEx.Equal(0, index.InstructionCount);
        var observed = index.Observe(1);
        AssertEx.True(observed.Success, observed.Error);
        AssertEx.False(AssertEx.NotNull(observed.Value).HasInstruction);
    }

    public static void RejectsStaleProjectionPlaybackIdentity()
    {
        (PlaybackArchiveSnapshot playback, ContinuationPlaybackProjection projection) =
            BuildProjection();
        ContinuationPlaybackProjection stale = projection with
        {
            PlaybackRevisionSha256 = new string('F', 64)
        };

        var bound = new PlaybackProjectionBinder().Bind(playback, stale);

        AssertEx.False(bound.Success);
        AssertEx.True(
            bound.Error.Contains("not bound", StringComparison.OrdinalIgnoreCase),
            bound.Error);
    }

    public static void RejectsStaleInstructionFingerprint()
    {
        (PlaybackArchiveSnapshot playback, ContinuationPlaybackProjection projection) =
            BuildProjection();
        ContinuationPlaybackInstruction changed = projection.Instructions[0] with
        {
            PlaybackRecordFingerprint = new string('E', 64)
        };
        ContinuationPlaybackProjection stale = projection with
        {
            Instructions = Array.AsReadOnly(new[] { changed })
        };

        var bound = new PlaybackProjectionBinder().Bind(playback, stale);

        AssertEx.False(bound.Success);
        AssertEx.True(
            bound.Error.Contains("stale fingerprint", StringComparison.OrdinalIgnoreCase),
            bound.Error);
    }

    public static void RejectsOutOfRangeObservedRecord()
    {
        (PlaybackArchiveSnapshot playback, ContinuationPlaybackProjection projection) =
            BuildProjection();
        IPlaybackProjectionIndex index = AssertEx.NotNull(
            new PlaybackProjectionBinder().Bind(playback, projection).Value);

        var observed = index.Observe(playback.Records.Count);

        AssertEx.False(observed.Success);
        AssertEx.True(
            observed.Error.Contains("outside", StringComparison.OrdinalIgnoreCase),
            observed.Error);
    }

    private static (
        PlaybackArchiveSnapshot Playback,
        ContinuationPlaybackProjection Projection) BuildProjection()
    {
        using var files = new TemporaryAapDirectory();
        string projectPath = files.Write(
            "runtime-binding.aap",
            SyntheticAap.Project(new[]
            {
                SyntheticAap.Scene("A"),
                SyntheticAap.Scene("B")
            }));
        var projectResult = new AapProjectReader().Read(projectPath);
        AssertEx.True(projectResult.Success, projectResult.Error);
        ProjectSnapshot project = AssertEx.NotNull(projectResult.Value);
        SceneSnapshot continued = project.ScriptNodes.Single().Scenes[1];

        PlaybackRecordSnapshot[] records =
        {
            Record(0, "A"),
            Record(1, "B")
        };
        var playback = new PlaybackArchiveSnapshot(
            new PlaybackArchiveSourceSnapshot(
                Path.ChangeExtension(projectPath, ".aas"),
                new string('A', 64),
                new string('B', 64),
                records.Length,
                project.Source.LastWriteTimeUtc + TimeSpan.FromSeconds(1)),
            "synthetic/v1",
            Array.AsReadOnly(records));
        var metadata = new ContinuationDocument(
            ContinuationDocument.CurrentSchemaVersion,
            Guid.NewGuid().ToString("D"),
            project.Source.PathKey,
            project.Source.RevisionSha256,
            new[] { new ContinuationRule(continued.Key) });
        var compiled = new ContinuationProjectionCompiler().Compile(
            project,
            playback,
            metadata);
        AssertEx.True(compiled.Success, compiled.Error);
        return (playback, AssertEx.NotNull(compiled.Value));
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
        index.ToString("X64"));
}
