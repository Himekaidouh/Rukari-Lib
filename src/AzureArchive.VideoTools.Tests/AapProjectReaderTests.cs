using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Formats.Aap;

namespace AzureArchive.VideoTools.Tests;

internal static class AapProjectReaderTests
{
    public static void ParseKnownProjectStructure()
    {
        using var files = new TemporaryAapDirectory();
        string path = files.Write(
            "known.aap",
            SyntheticAap.Project(new[]
            {
                SyntheticAap.Scene("A"),
                SyntheticAap.Scene("B"),
                SyntheticAap.Scene("C")
            }));

        var reader = new AapProjectReader();
        var result = reader.Read(path);

        AssertEx.True(result.Success, result.Error);
        ProjectSnapshot project = AssertEx.NotNull(result.Value);
        AssertEx.Equal("Synthetic Project", project.ProjectName);
        AssertEx.Equal("ProjectData, Assembly-CSharp", project.SourceType);
        AssertEx.Equal(3, project.Nodes.Count);
        AssertEx.Equal(64, project.Source.PathKey.Length);
        AssertEx.Equal(64, project.Source.RevisionSha256.Length);
        AssertEx.True(project.Source.Length > 0);

        StoryNodeSnapshot scriptNode = project.ScriptNodes.Single();
        AssertEx.Equal(SyntheticAap.ScriptNodeGuid, scriptNode.NodeGuid);
        AssertEx.True(scriptNode.HasPersistentGuid);
        AssertEx.Equal(3, scriptNode.Scenes.Count);
        AssertEx.Equal("B", scriptNode.Scenes[1].DialogueText);
        AssertEx.Equal("BG_Test", scriptNode.Scenes[1].BackgroundFriendlyName);
        AssertEx.Equal(0U, scriptNode.Scenes[1].BackgroundName);
        AssertEx.Equal(0U, scriptNode.Scenes[1].BackgroundEffect);
        AssertEx.Equal(0U, scriptNode.Scenes[1].Transition);
        AssertEx.Equal(0L, scriptNode.Scenes[1].BgmId);
        AssertEx.Equal(64, scriptNode.Scenes[1].Key.Fingerprint.Length);
        AssertEx.Equal(0, project.Diagnostics.Count);
    }

    public static void CanonicalFingerprintIgnoresFormattingAndPropertyOrder()
    {
        using var files = new TemporaryAapDirectory();
        string firstPath = files.Write(
            "first.aap",
            SyntheticAap.Project(new[] { SyntheticAap.Scene("Same") }));
        string secondPath = files.Write(
            "second.aap",
            SyntheticAap.Project(new[]
            {
                SyntheticAap.Scene("Same", reversedPropertyOrder: true)
            }));

        var reader = new AapProjectReader();
        ProjectSnapshot first = AssertEx.NotNull(reader.Read(firstPath).Value);
        ProjectSnapshot second = AssertEx.NotNull(reader.Read(secondPath).Value);
        string firstFingerprint = first.ScriptNodes.Single().Scenes.Single().Key.Fingerprint;
        string secondFingerprint = second.ScriptNodes.Single().Scenes.Single().Key.Fingerprint;

        AssertEx.Equal(firstFingerprint, secondFingerprint);
        AssertEx.False(
            string.Equals(
                first.Source.RevisionSha256,
                second.Source.RevisionSha256,
                StringComparison.Ordinal),
            "Differently formatted project files should have different revision hashes.");
    }

    public static void ParsesOfficialCharacterPositionTransition()
    {
        using var files = new TemporaryAapDirectory();
        string path = files.Write(
            "character-position.aap",
            SyntheticAap.Project(new[]
            {
                SyntheticAap.SceneWithCharacterTransition("Move", 3, 3, 4)
            }));

        var result = new AapProjectReader().Read(path);
        AssertEx.True(result.Success, result.Error);
        SceneSnapshot scene = AssertEx.NotNull(result.Value)
            .ScriptNodes.Single().Scenes.Single();
        AssertEx.Equal(3, scene.SpeakerSlot);
        AssertEx.Equal(2, scene.HighlightedSlots.Count);
        AssertEx.Equal(5, scene.Characters.Count);

        ProjectCharacterSnapshot character = scene.Characters.Single(
            value => value.PhysicalSlot == 3);
        AssertEx.Equal("test-character", character.Identifier);
        AssertEx.Equal("03", character.FaceId);
        AssertEx.Equal(3, character.StartingPosition);
        AssertEx.Equal(4, character.EndingPosition);
        AssertEx.True(character.HasOfficialPositionTransition);
        AssertEx.Equal(4, character.Action);
        AssertEx.Equal(1, character.Effect);
    }

    public static void TypeMetadataRemainsPlainData()
    {
        using var files = new TemporaryAapDirectory();
        const string untrustedType = "Untrusted.Type, MissingAssembly";
        string path = files.Write(
            "metadata.aap",
            SyntheticAap.Project(
                new[] { SyntheticAap.Scene("A", untrustedType) },
                rootType: untrustedType));

        var result = new AapProjectReader().Read(path);
        AssertEx.True(result.Success, result.Error);
        ProjectSnapshot project = AssertEx.NotNull(result.Value);
        AssertEx.Equal(untrustedType, project.SourceType);
        AssertEx.Equal(
            untrustedType,
            project.ScriptNodes.Single().Scenes.Single().SourceType);
    }

    public static void InvalidJsonFailsWithoutThrowing()
    {
        using var files = new TemporaryAapDirectory();
        string path = files.Write("broken.aap", "{ not-json }");

        var result = new AapProjectReader().Read(path);
        AssertEx.False(result.Success);
        AssertEx.True(result.Error.StartsWith("AAP JSON is invalid:", StringComparison.Ordinal));
    }

    public static void MissingNodeCollectionFailsClearly()
    {
        using var files = new TemporaryAapDirectory();
        string path = files.Write(
            "missing-nodes.aap",
            "{\"$type\":\"ProjectData, Assembly-CSharp\",\"ProjectName\":\"No Nodes\"}");

        var result = new AapProjectReader().Read(path);
        AssertEx.False(result.Success);
        AssertEx.True(result.Error.Contains("nodes", StringComparison.OrdinalIgnoreCase));
    }

    public static void FoundationComposesReaderAndPureServices()
    {
        using var files = new TemporaryAapDirectory();
        string path = files.Write(
            "foundation.aap",
            SyntheticAap.Project(new[]
            {
                SyntheticAap.Scene("A"),
                SyntheticAap.Scene("B")
            }));

        var foundation = AapVideoToolsFoundation.Create(
            Path.Combine(files.Root, "sidecars"));
        var result = foundation.Projects.Read(path);

        AssertEx.True(result.Success, result.Error);
        AssertEx.NotNull(result.Value);
        AssertEx.NotNull(foundation.Continuity);
        AssertEx.NotNull(foundation.Reconciler);
        AssertEx.NotNull(foundation.ContinuationStore);
        AssertEx.NotNull(foundation.PlaybackArchives);
        AssertEx.NotNull(foundation.PlaybackMapper);
        AssertEx.NotNull(foundation.ProjectionCompiler);
        AssertEx.NotNull(foundation.PlaybackProjectionBinder);
        AssertEx.NotNull(foundation.ProjectionStore);
        AssertEx.NotNull(foundation.Workspaces);
        AssertEx.NotNull(foundation.CommandTimelineCompiler);
        AssertEx.NotNull(foundation.PlaybackCommandBinder);
        AssertEx.NotNull(foundation.CommandWorkspace);
    }
}
