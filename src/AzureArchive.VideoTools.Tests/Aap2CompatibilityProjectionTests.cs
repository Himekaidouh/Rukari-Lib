using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Mapping;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Formats.Aap;
using AzureArchive.VideoTools.Formats.Aas;
using AzureArchive.VideoTools.Formats.Sidecar;

namespace AzureArchive.VideoTools.Tests;

internal static class Aap2CompatibilityProjectionTests
{
    public static void VerifiedNativePathUsesOfficialLegacyProjectionWithoutChangingGameFiles()
    {
        using var files = new TemporaryAapDirectory();
        Fixture fixture = WriteFixture(files.Root);
        string nativeHash = Hash(fixture.NativePath), projectionHash = Hash(fixture.ProjectionPath);
        var resolved = OfficialAapCompatibilityProjection.Resolve(fixture.NativePath);
        AssertEx.True(resolved.Success, resolved.Error);
        AssertEx.Equal(fixture.ProjectionPath, AssertEx.NotNull(resolved.Value).ProjectPath);
        AssertEx.Equal(nativeHash, resolved.Value!.NativeRevisionSha256);
        ProjectSnapshot native = Read(fixture.NativePath), projection = Read(fixture.ProjectionPath);
        AssertEx.Equal(fixture.ProjectionPath, native.Source.FullPath);
        AssertEx.Equal(projectionHash, native.Source.RevisionSha256);
        AssertEx.Equal(projection.ScriptNodes.Single().Scenes.Single().Key,
            native.ScriptNodes.Single().Scenes.Single().Key);
        AssertEx.Equal("#aavt;camera;set;zoom=1;duration=0", native.ScriptNodes.Single().Scenes.Single().AdditionalPrompt);
        AssertEx.Equal(nativeHash, Hash(fixture.NativePath));
        AssertEx.Equal(projectionHash, Hash(fixture.ProjectionPath));
    }

    public static void NativeStorageAndPreviewToPlaybackRetainTheSameVerifiedLegacyPair()
    {
        using var files = new TemporaryAapDirectory();
        string root = Path.Combine(files.Root, "data");
        Fixture fixture = WriteFixture(Path.Combine(root, "projects"));
        string resource = Path.Combine(root, "projects", "work");
        string playbackResource = Path.Combine(root, "saves", "work");
        Directory.CreateDirectory(resource);
        Directory.CreateDirectory(playbackResource);
        string playbackPath = Path.Combine(root, "saves", "work.aas");
        File.WriteAllBytes(playbackPath, SyntheticAas.Archive(new SyntheticAasRecord(TextJp: "scene", CompiledScript: "compiled")));
        var tracker = new OfficialProjectPathTracker();
        tracker.Observe(fixture.NativePath, resource, false);
        long revision = tracker.Current.Revision;
        var resolved = OfficialProjectStoragePaths.Resolve(tracker.Current, string.Empty, files.Root);
        AssertEx.True(resolved.Success, resolved.Error);
        AssertEx.Equal(fixture.ProjectionPath, AssertEx.NotNull(resolved.Value).ProjectFilePath);
        AssertEx.Equal(playbackPath, resolved.Value!.PlaybackFilePath);
        AssertEx.False(tracker.Observe(fixture.ProjectionPath, resource, false));
        AssertEx.True(tracker.IsCurrent(revision));
        AssertEx.False(tracker.Observe(string.Empty, playbackResource, false));
        AssertEx.Equal(fixture.ProjectionPath,
            AssertEx.NotNull(OfficialProjectStoragePaths.Resolve(tracker.Current, string.Empty, files.Root).Value).ProjectFilePath);
    }

    public static void StaleProjectionHashAndChangedSourceCannotAuthorizeAap2Reads()
    {
        using var files = new TemporaryAapDirectory();
        Fixture fixture = WriteFixture(files.Root);
        var binding = AssertEx.NotNull(OfficialAapCompatibilityProjection.Resolve(fixture.NativePath).Value);
        File.AppendAllText(fixture.NativePath, "\n");
        RejectBoth(fixture, "source-hash-mismatch");
        var freshness = OfficialAapCompatibilityProjection.VerifyStillCurrent(binding);
        AssertEx.False(freshness.Success);
        AssertEx.True(freshness.Error.Contains("source-changed-during-read", StringComparison.Ordinal), freshness.Error);
    }

    public static void ProjectionIdentityNameAndSchemaMustMatchItsNativeSource()
    {
        using var files = new TemporaryAapDirectory();
        var changes = new (string Reason, Action<JsonObject> Change)[]
        {
            ("project-id-mismatch", root => root["Aap2Compatibility"]!["ProjectId"] = Guid.NewGuid().ToString("D")),
            ("project-name-mismatch", root => root["ProjectName"] = "different"),
            ("compatibility-metadata-invalid", root => root["Aap2Compatibility"]!["Version"] = 2),
            ("source-hash-mismatch", root => root["Aap2Compatibility"]!["SourceFileVersion"] = "sha256:" + new string('F', 64)),
            ("compatibility-metadata-invalid", root => root.Remove("Aap2Compatibility"))
        };
        foreach (var change in changes)
        {
            Fixture fixture = WriteFixture(files.Root);
            var json = (JsonObject)JsonNode.Parse(File.ReadAllText(fixture.ProjectionPath))!;
            change.Change(json);
            File.WriteAllText(fixture.ProjectionPath, json.ToJsonString());
            RejectBoth(fixture, change.Reason);
        }
        // Matching marker hashes do not make a different native name/version/header valid.
        foreach (var mutation in new (string Reason, Action<JsonObject> Change)[]
        {
            ("project-name-mismatch", root => root["ProjectName"] = "other"),
            ("source-format-invalid", root => root["FormatVersion"] = 3),
            ("project-id-mismatch", root => root["ProjectId"] = Guid.Empty.ToString("D"))
        })
        {
            Fixture fixture = WriteFixture(files.Root);
            var native = (JsonObject)JsonNode.Parse(File.ReadAllText(fixture.NativePath)[5..])!;
            mutation.Change(native);
            File.WriteAllText(fixture.NativePath, "AAP2\n" + native.ToJsonString());
            RefreshProjectionHash(fixture);
            RejectBoth(fixture, mutation.Reason);
        }
        Fixture header = WriteFixture(files.Root);
        File.WriteAllText(header.NativePath, "AAP3\n" + File.ReadAllText(header.NativePath)[5..]);
        RefreshProjectionHash(header);
        RejectBoth(header, "source-header-invalid");
    }

    public static void MissingProjectionOrNativeSourceFailsWithoutAnUnrelatedFallback()
    {
        using var files = new TemporaryAapDirectory();
        Fixture missingProjection = WriteFixture(files.Root);
        File.Delete(missingProjection.ProjectionPath);
        var projection = new AapProjectReader().Read(missingProjection.NativePath);
        AssertEx.False(projection.Success);
        AssertEx.True(projection.Error.Contains("projection-unavailable", StringComparison.Ordinal), projection.Error);
        Fixture missingNative = WriteFixture(files.Root);
        File.Delete(missingNative.NativePath);
        var native = new AapProjectReader().Read(missingNative.ProjectionPath);
        AssertEx.False(native.Success);
        AssertEx.True(native.Error.Contains("source-unavailable", StringComparison.Ordinal), native.Error);
    }

    public static void ProjectionUnderAReparseDirectoryIsRejected()
    {
        // The supported Windows platform supplies this compatibility junction.
        // Read its attributes only; never create links or touch its target files.
        if (!OperatingSystem.IsWindows()) return;
        string link = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Application Data");
        AssertEx.True((new DirectoryInfo(link).Attributes & FileAttributes.ReparsePoint) != 0);
        string path = Path.Combine(link, "rukari-absent-projection-" + Guid.NewGuid().ToString("N") + ".aap2");
        var result = new AapProjectReader().Read(path);
        AssertEx.False(result.Success);
        AssertEx.True(result.Error.Contains("reparse-rejected", StringComparison.Ordinal), result.Error);
        AssertEx.False(result.Error.Contains(link, StringComparison.Ordinal), result.Error);
    }

    public static void NativePathCacheDoesNotReopenStableFilesAndRejectsEitherChangedDocument()
    {
        using var files = new TemporaryAapDirectory();
        Fixture fixture = WriteFixture(files.Root);
        var first = OfficialAapCompatibilityProjection.Resolve(fixture.NativePath);
        AssertEx.True(first.Success, first.Error);
        using (var locked = new FileStream(fixture.NativePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // Native normalization precedes per-frame snapshot-cache hits. It must
            // use stat receipts here, while a real new project read must not do so.
            var cached = OfficialAapCompatibilityProjection.Resolve(fixture.NativePath);
            AssertEx.True(cached.Success, cached.Error);
            AssertEx.Equal(first.Value, cached.Value);
            AssertEx.False(new AapProjectReader().Read(fixture.NativePath).Success);
        }
        var smallerLimit = OfficialAapCompatibilityProjection.Resolve(fixture.NativePath, maximumBytes: 8);
        AssertEx.False(smallerLimit.Success);
        AssertEx.True(smallerLimit.Error.Contains("size-invalid", StringComparison.Ordinal));
        File.AppendAllText(fixture.ProjectionPath, "\n");
        var changedProjection = OfficialAapCompatibilityProjection.Resolve(fixture.NativePath);
        AssertEx.True(changedProjection.Success, changedProjection.Error);
        AssertEx.False(first.Value!.ProjectionRevisionSha256.Equals(changedProjection.Value!.ProjectionRevisionSha256, StringComparison.Ordinal));
        File.AppendAllText(fixture.NativePath, "\n");
        var changedSource = OfficialAapCompatibilityProjection.Resolve(fixture.NativePath);
        AssertEx.False(changedSource.Success);
        AssertEx.True(changedSource.Error.Contains("source-hash-mismatch", StringComparison.Ordinal), changedSource.Error);
    }

    public static void NativeChangesInvalidateAProjectCacheEvenWhenLegacyBytesStayUnchanged()
    {
        using var files = new TemporaryAapDirectory();
        Fixture fixture = WriteFixture(files.Root);
        var cache = new ArchiveSnapshotCache();
        int reads = 0;
        AzureArchive.VideoTools.Core.Results.Result<ProjectSnapshot> Reader(string path)
        { reads++; return new AapProjectReader().Read(path); }
        AssertEx.True(cache.GetOrReadProject(fixture.ProjectionPath, Reader).Success);
        AssertEx.True(cache.GetOrReadProject(fixture.ProjectionPath, Reader).Success);
        AssertEx.Equal(1, reads);
        File.AppendAllText(fixture.NativePath, "\n");
        var changed = cache.GetOrReadProject(fixture.ProjectionPath, Reader);
        AssertEx.False(changed.Success);
        AssertEx.Equal(2, reads);
        File.Delete(fixture.NativePath);
        AssertEx.False(cache.GetOrReadProject(fixture.ProjectionPath, Reader).Success);
        AssertEx.Equal(3, reads);
    }

    public static void NativeFormatAdaptationDoesNotInventEmptySceneCompilerProof()
    {
        using var files = new TemporaryAapDirectory();
        Fixture fixture = WriteFixture(files.Root, empty: true);
        ProjectSnapshot project = Read(fixture.NativePath);
        string playbackPath = files.WriteBytes("work.aas", SyntheticAas.Archive(new SyntheticAasRecord(TextJp: "", CompiledScript: "")));
        var playbackRead = new AasScenarioReader().Read(playbackPath);
        AssertEx.True(playbackRead.Success, playbackRead.Error);
        var playback = AssertEx.NotNull(playbackRead.Value);
        AssertEx.True(project.CompilationMapping == null);
        AssertEx.False(new EmbeddedProjectCommandCompiler().Compile(project, playback, "1.6.2").Success);
        var scene = project.ScriptNodes.Single().Scenes.Single();
        var record = playback.Records.Single();
        var evidence = new CompilationMappingEvidence(CompilationMappingEvidence.CurrentSchemaVersion,
            project.Source.RevisionSha256, project.Source.Length, playback.Source.RevisionSha256, playback.Source.Length,
            new[] { new CompilationMappingEntry(scene.Key, record.RecordIndex, record.Fingerprint,
                CommandIdentity.CompiledScript(record.CompiledScript)) });
        var saved = new JsonCompilationMappingEvidenceStore().Save(evidence, project, playback);
        AssertEx.True(saved.Success, saved.Error);
        AssertEx.Equal(fixture.ProjectionPath + JsonCompilationMappingEvidenceStore.Extension, saved.Value);
        ProjectSnapshot captured = Read(fixture.NativePath);
        AssertEx.NotNull(captured.CompilationMapping);
        var compiled = new EmbeddedProjectCommandCompiler().Compile(captured, playback, "1.6.2");
        AssertEx.True(compiled.Success, compiled.Error);
        AssertEx.Equal(1, AssertEx.NotNull(compiled.Value).CommandCount);
    }

    public static void LegacyAapKeepsItsOriginalIdentityAndNamingRules()
    {
        using var files = new TemporaryAapDirectory();
        string path = files.Write("legacy.aap", SyntheticAap.Project(new[] { SyntheticAap.Scene("old") }));
        var resolved = OfficialAapCompatibilityProjection.Resolve(path);
        AssertEx.True(resolved.Success, resolved.Error);
        AssertEx.Equal(path, AssertEx.NotNull(resolved.Value).ProjectPath);
        AssertEx.Equal(string.Empty, resolved.Value!.NativeProjectPath);
        ProjectSnapshot legacy = Read(path);
        AssertEx.Equal("Synthetic Project", legacy.ProjectName);
        AssertEx.Equal(path, legacy.Source.FullPath);
        AssertEx.Equal(Hash(path), legacy.Source.RevisionSha256);
    }

    private static Fixture WriteFixture(string directory, bool empty = false)
    {
        Directory.CreateDirectory(directory);
        string nativePath = Path.Combine(directory, "work.aap2"), projectionPath = Path.Combine(directory, "work.aap");
        string id = "9627ff59-2405-4074-a4e8-e1868beb33a2";
        var projection = (JsonObject)JsonNode.Parse(SyntheticAap.Project(new[] { SyntheticAap.Scene(empty ? "" : "scene",
            additionalPrompt: "#aavt;camera;set;zoom=1;duration=0") }))!;
        projection["ProjectName"] = "work";
        var native = (JsonObject)NativeShape(projection)!;
        native["LegacySourceVersion"] = "absent"; native["FormatVersion"] = 2; native["ProjectId"] = id;
        File.WriteAllText(nativePath, "AAP2\n" + native.ToJsonString(), new UTF8Encoding(false));
        projection["Aap2Compatibility"] = new JsonObject { ["Version"] = 1, ["ProjectId"] = id,
            ["SourceFileVersion"] = "sha256:" + Hash(nativePath).ToLowerInvariant() };
        File.WriteAllText(projectionPath, projection.ToJsonString(), new UTF8Encoding(false));
        return new(nativePath, projectionPath);
    }

    private static JsonNode? NativeShape(JsonNode? node)
    {
        if (node is JsonArray array) return new JsonArray(array.Select(NativeShape).ToArray());
        if (node is not JsonObject obj) return node == null ? null : JsonNode.Parse(node.ToJsonString());
        if (obj.TryGetPropertyValue("$values", out JsonNode? values)) return NativeShape(values);
        var result = new JsonObject();
        foreach (var property in obj)
            if (property.Key != "$type") result[property.Key] = NativeShape(property.Value);
        string sourceType = obj["$type"]?.GetValue<string>() ?? string.Empty;
        if (sourceType.StartsWith("EntryNodeData", StringComparison.Ordinal)) result["Kind"] = "entry";
        if (sourceType.StartsWith("ScriptNodeData", StringComparison.Ordinal)) result["Kind"] = "dialogue";
        if (sourceType.StartsWith("ExitNodeData", StringComparison.Ordinal)) result["Kind"] = "exit";
        if (sourceType.StartsWith("ScriptData", StringComparison.Ordinal)) result["LineId"] = Guid.NewGuid().ToString("D");
        return result;
    }

    private static void RefreshProjectionHash(Fixture fixture)
    {
        var projection = (JsonObject)JsonNode.Parse(File.ReadAllText(fixture.ProjectionPath))!;
        projection["Aap2Compatibility"]!["SourceFileVersion"] = "sha256:" + Hash(fixture.NativePath);
        File.WriteAllText(fixture.ProjectionPath, projection.ToJsonString());
    }
    private static void RejectBoth(Fixture fixture, string reason)
    {
        foreach (string path in new[] { fixture.NativePath, fixture.ProjectionPath })
        {
            var result = new AapProjectReader().Read(path);
            AssertEx.False(result.Success);
            AssertEx.True(result.Error.Contains(reason, StringComparison.Ordinal), result.Error);
            AssertEx.False(result.Error.Contains(fixture.NativePath, StringComparison.Ordinal), result.Error);
        }
    }
    private static ProjectSnapshot Read(string path)
    {
        var read = new AapProjectReader().Read(path);
        AssertEx.True(read.Success, read.Error);
        return AssertEx.NotNull(read.Value);
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private sealed record Fixture(string NativePath, string ProjectionPath);
}

