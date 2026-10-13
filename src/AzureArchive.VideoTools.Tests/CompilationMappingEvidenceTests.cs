using System.Buffers.Binary;
using System.Security.Cryptography;
using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Mapping;
using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Formats.Aap;
using AzureArchive.VideoTools.Formats.Aas;
using AzureArchive.VideoTools.Formats.Sidecar;

namespace AzureArchive.VideoTools.Tests;

internal static class CompilationMappingEvidenceTests
{
    public static void AllEmptyNodesWithoutCaptureProofRemainUnmapped()
    {
        using var files = new TemporaryAapDirectory();
        Fixture pair = EmptyPair(files, "unproven");
        ProjectPlaybackMappingReport mapped = Map(pair.Project, pair.Playback);
        AssertEx.Equal(0, mapped.UniqueMappingCount);
        AssertEx.True(mapped.Scenes.All(scene => scene.PlaybackRecordIndex == null));
        var compiled = new EmbeddedProjectCommandCompiler().Compile(pair.Project, pair.Playback, "1.5.2");
        AssertEx.False(compiled.Success);
        AssertEx.True(compiled.Error.Contains("unique playback mapping", StringComparison.Ordinal), compiled.Error);
    }

    public static void CapturedMappingAssociatesTrueEmptyAndNewlineRecordsAndCompiles()
    {
        using var files = new TemporaryAapDirectory();
        Fixture pair = EmptyPair(files, "captured");
        AssertEx.True(CompilationMappingEvidenceValidator.Validate(pair.Evidence, pair.Project, pair.Playback).Success);
        ProjectSnapshot captured = pair.Project with { CompilationMapping = pair.Evidence };
        ProjectPlaybackMappingReport mapped = Map(captured, pair.Playback);
        AssertEx.Equal(2, mapped.UniqueMappingCount);
        AssertEx.True(mapped.Scenes.All(scene => scene.Status == ScenePlaybackMappingStatus.ObservedCompilerRecordMatch));
        // Export proof deliberately associates sources with records in reverse order.
        AssertEx.Equal(1, mapped.Scenes[0].PlaybackRecordIndex);
        AssertEx.Equal(0, mapped.Scenes[1].PlaybackRecordIndex);
        EmbeddedProjectCommandCompilation compiled = Compile(captured, pair.Playback);
        AssertEx.Equal(2, compiled.CommandCount);
        PlaybackCommandBatch empty = compiled.Projection.Batches.Single(batch => batch.PlaybackRecordIndex == 1);
        PlaybackCommandBatch newline = compiled.Projection.Batches.Single(batch => batch.PlaybackRecordIndex == 0);
        AssertEx.Equal(0, empty.CompiledScript.Utf16Length);
        AssertEx.Equal(1, newline.CompiledScript.Utf16Length);
        AssertEx.True(empty.Commands.Single().CanonicalDirective.Contains("x=111", StringComparison.Ordinal));
        AssertEx.True(newline.Commands.Single().CanonicalDirective.Contains("x=222", StringComparison.Ordinal));
        var bound = new PlaybackCommandBinder().Bind(pair.Playback, compiled.Projection);
        AssertEx.True(bound.Success, bound.Error);
        IPlaybackCommandIndex index = AssertEx.NotNull(bound.Value);
        AssertEx.False(PlaybackAdvanceDispatchPolicy.ObserveCompleted(index, new[] { "" }, -1).Success);
        AssertEx.False(PlaybackAdvanceDispatchPolicy.ObserveCompleted(index, new[] { "" }, 0).Success);
        var exact = PlaybackAdvanceDispatchPolicy.ObserveCompleted(index, new[] { "" }, 1);
        AssertEx.True(exact.Success, exact.Error);
        AssertEx.Equal(1, AssertEx.NotNull(exact.Value).PlaybackRecordIndex);
        var absent = PlaybackAdvanceDispatchPolicy.ObserveExactRow(index, Array.Empty<string>(), 1);
        AssertEx.True(absent.Success, absent.Error);
        AssertEx.Equal(PlaybackCommandWindowObservationStatus.NoIdentity, AssertEx.NotNull(absent.Value).Status);
        AssertEx.False(PlaybackAdvanceDispatchPolicy.ObserveExactRow(index, new string[] { null! }, 1).Success);
    }

    public static void UniqueAnchorStillAllowsTrueEmptyCommandWithoutCaptureProof()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot project = ReadProject(files.Write("anchored.aap", SyntheticAap.Project(new[]
        {
            SyntheticAap.Scene("unique anchor"),
            SyntheticAap.Scene("", additionalPrompt: "#aavt;char;3;set;x=111;duration=0;easing=linear")
        })));
        PlaybackArchiveSnapshot playback = ReadPlayback(files.WriteBytes("anchored.aas", Archive(
            new SyntheticAasRecord(TextJp: "unique anchor", CompiledScript: "compiled unique anchor"),
            new SyntheticAasRecord(TextJp: "", CompiledScript: ""))));
        ProjectPlaybackMappingReport mapped = Map(project, playback);
        AssertEx.Equal(ScenePlaybackMappingStatus.AnchoredNodeSequenceMatch, mapped.Scenes[1].Status);
        AssertEx.True(project.CompilationMapping == null);
        EmbeddedProjectCommandCompilation compiled = Compile(project, playback);
        PlaybackCommandBatch batch = compiled.Projection.Batches.Single();
        AssertEx.Equal(1, batch.PlaybackRecordIndex);
        AssertEx.Equal(0, batch.CompiledScript.Utf16Length);
        var bound = new PlaybackCommandBinder().Bind(playback, compiled.Projection);
        AssertEx.True(bound.Success, bound.Error);
        IPlaybackCommandIndex index = AssertEx.NotNull(bound.Value);
        AssertEx.False(PlaybackAdvanceDispatchPolicy.ObserveCompleted(index, new[] { "" }, -1).Success);
        AssertEx.False(PlaybackAdvanceDispatchPolicy.ObserveExactRow(index, new[] { "" }, 0).Success);
        AssertEx.True(PlaybackAdvanceDispatchPolicy.ObserveExactRow(index, new[] { "" }, 1).Success);
    }

    public static void CapturedEvidenceRejectsArchiveRevisionsLengthsAndSchemaDrift()
    {
        using var files = new TemporaryAapDirectory();
        Fixture pair = EmptyPair(files, "revisions");
        CompilationMappingEvidence[] invalid =
        {
            pair.Evidence with { ProjectRevisionSha256 = new string('F', 64) },
            pair.Evidence with { PlaybackRevisionSha256 = new string('E', 64) },
            pair.Evidence with { ProjectLength = pair.Evidence.ProjectLength + 1 },
            pair.Evidence with { PlaybackLength = pair.Evidence.PlaybackLength + 1 },
            pair.Evidence with { ProjectLength = 0 },
            pair.Evidence with { PlaybackLength = 0 },
            pair.Evidence with { SchemaVersion = CompilationMappingEvidence.CurrentSchemaVersion + 1 },
            pair.Evidence with { ProjectRevisionSha256 = "invalid" }
        };
        foreach (CompilationMappingEvidence evidence in invalid) RejectEvidence(pair, evidence);
    }

    public static void CapturedEvidenceRejectsDuplicateSourcesAndRecords()
    {
        using var files = new TemporaryAapDirectory();
        Fixture pair = EmptyPair(files, "duplicates");
        CompilationMappingEntry first = pair.Evidence.Entries[0];
        CompilationMappingEntry second = pair.Evidence.Entries[1];
        RejectEvidence(pair, pair.Evidence with { Entries = new[] { first, first } });
        RejectEvidence(pair, pair.Evidence with { Entries = new[] { first, first with { Scene = second.Scene } } });
        RejectEvidence(pair, pair.Evidence with { Entries = new[] { first with { PlaybackRecordIndex = -1 }, second } });
        RejectEvidence(pair, pair.Evidence with { Entries = new[] { first with { PlaybackRecordIndex = 2 }, second } });
        RejectEvidence(pair, pair.Evidence with { Entries = Array.Empty<CompilationMappingEntry>() });
        RejectEvidence(pair, pair.Evidence with { Entries = null! });
        RejectEvidence(pair, pair.Evidence with { Entries = new CompilationMappingEntry[] { null! } });
    }

    public static void CapturedEvidenceRejectsSceneRecordAndIdentityContentDrift()
    {
        using var files = new TemporaryAapDirectory();
        Fixture pair = EmptyPair(files, "content");
        CompilationMappingEntry first = pair.Evidence.Entries[0];
        CompilationMappingEntry second = pair.Evidence.Entries[1];
        CompilationMappingEntry[] invalid =
        {
            first with { Scene = first.Scene with { Fingerprint = new string('F', 64) } },
            first with { Scene = first.Scene with { NodeGuid = Guid.NewGuid().ToString("D") } },
            first with { Scene = first.Scene with { SceneIndex = 1 } },
            first with { PlaybackRecordFingerprint = new string('F', 64) },
            first with { CompiledScript = first.CompiledScript with { Sha256 = CommandIdentity.CompiledScript("\n").Sha256 } },
            first with { CompiledScript = first.CompiledScript with { Utf16Length = 1 } },
            first with { CompiledScript = first.CompiledScript with { LineCount = 2 } },
            first with { CompiledScript = null! },
            first with { Scene = null! }
        };
        foreach (CompilationMappingEntry entry in invalid)
            RejectEvidence(pair, pair.Evidence with { Entries = new[] { entry, second } });

        // Dialogue agreement is checked in addition to the copied hash fields.
        ProjectSnapshot differentText = pair.Project with
        {
            Nodes = pair.Project.Nodes.Select(node => node.NodeGuid == first.Scene.NodeGuid
                ? node with { Scenes = node.Scenes.Select(scene => scene with { DialogueText = "different" }).ToArray() }
                : node).ToArray()
        };
        AssertEx.False(CompilationMappingEvidenceValidator.Validate(pair.Evidence, differentText, pair.Playback).Success);
        PlaybackArchiveSnapshot differentRecord = pair.Playback with
        {
            Records = pair.Playback.Records.Select(record => record.RecordIndex == first.PlaybackRecordIndex
                ? record with { TextJp = "different" } : record).ToArray()
        };
        AssertEx.False(CompilationMappingEvidenceValidator.Validate(pair.Evidence, pair.Project, differentRecord).Success);
    }

    public static void MappingSidecarRoundTripsWithoutChangingEitherArchive()
    {
        using var files = new TemporaryAapDirectory();
        Fixture pair = EmptyPair(files, "roundtrip");
        byte[] projectBytes = File.ReadAllBytes(pair.Project.Source.FullPath);
        byte[] playbackBytes = File.ReadAllBytes(pair.Playback.Source.FullPath);
        var saved = new JsonCompilationMappingEvidenceStore().Save(pair.Evidence, pair.Project, pair.Playback);
        AssertEx.True(saved.Success, saved.Error);
        AssertEx.Equal(JsonCompilationMappingEvidenceStore.GetPath(pair.Project.Source.FullPath), saved.Value);
        AssertEx.True(projectBytes.SequenceEqual(File.ReadAllBytes(pair.Project.Source.FullPath)));
        AssertEx.True(playbackBytes.SequenceEqual(File.ReadAllBytes(pair.Playback.Source.FullPath)));
        var loaded = new JsonCompilationMappingEvidenceStore().Read(pair.Project.Source.FullPath);
        AssertEx.True(loaded.Success, loaded.Error);
        AssertEx.Equal(pair.Evidence.Entries[0], AssertEx.NotNull(loaded.Value).Entries[0]);
        ProjectSnapshot reread = ReadProject(pair.Project.Source.FullPath);
        AssertEx.NotNull(reread.CompilationMapping);
        AssertEx.Equal(2, Map(reread, pair.Playback).UniqueMappingCount);
        AssertEx.Equal(2, Compile(reread, pair.Playback).CommandCount);
        AssertEx.Equal(pair.Project.Source.RevisionSha256, Hash(pair.Project.Source.FullPath));
        AssertEx.Equal(pair.Playback.Source.RevisionSha256, Hash(pair.Playback.Source.FullPath));
    }

    public static void MappingSidecarRebindsCopiedContentsAtDifferentPaths()
    {
        using var files = new TemporaryAapDirectory();
        Fixture pair = EmptyPair(files, "original");
        var store = new JsonCompilationMappingEvidenceStore();
        var saved = store.Save(pair.Evidence, pair.Project, pair.Playback);
        AssertEx.True(saved.Success, saved.Error);
        string newProjectPath = files.WriteBytes("relocated.aap", File.ReadAllBytes(pair.Project.Source.FullPath));
        string newPlaybackPath = files.WriteBytes("relocated.aas", File.ReadAllBytes(pair.Playback.Source.FullPath));
        File.Copy(AssertEx.NotNull(saved.Value), JsonCompilationMappingEvidenceStore.GetPath(newProjectPath));
        ProjectSnapshot relocatedProject = ReadProject(newProjectPath);
        PlaybackArchiveSnapshot relocatedPlayback = ReadPlayback(newPlaybackPath);
        AssertEx.False(relocatedProject.Source.PathKey == pair.Project.Source.PathKey);
        AssertEx.False(relocatedPlayback.Source.PathKey == pair.Playback.Source.PathKey);
        AssertEx.True(CompilationMappingEvidenceValidator.Validate(
            AssertEx.NotNull(relocatedProject.CompilationMapping), relocatedProject, relocatedPlayback).Success);
        AssertEx.Equal(2, Map(relocatedProject, relocatedPlayback).UniqueMappingCount);
        EmbeddedProjectCommandCompilation compiled = Compile(relocatedProject, relocatedPlayback);
        AssertEx.Equal(relocatedPlayback.Source.PathKey, compiled.Projection.PlaybackPathKey);
        AssertEx.True(store.Save(AssertEx.NotNull(relocatedProject.CompilationMapping), relocatedProject, relocatedPlayback).Success);
    }

    public static void MappingSidecarRejectsModifiedProjectOrPlaybackContents()
    {
        using var files = new TemporaryAapDirectory();
        Fixture pair = EmptyPair(files, "modified");
        var store = new JsonCompilationMappingEvidenceStore();
        AssertEx.True(store.Save(pair.Evidence, pair.Project, pair.Playback).Success);
        byte[] originalProject = File.ReadAllBytes(pair.Project.Source.FullPath);
        // Valid whitespace leaves scene fingerprints intact while invalidating archive revision evidence.
        File.AppendAllText(pair.Project.Source.FullPath, "\n");
        ProjectSnapshot changedProject = ReadProject(pair.Project.Source.FullPath);
        RejectEvidence(pair with { Project = changedProject }, AssertEx.NotNull(changedProject.CompilationMapping));
        var staleProjectSave = store.Save(pair.Evidence, pair.Project, pair.Playback);
        AssertEx.False(staleProjectSave.Success);
        AssertEx.True(staleProjectSave.Error.Contains("changed-before-save", StringComparison.Ordinal), staleProjectSave.Error);
        File.WriteAllBytes(pair.Project.Source.FullPath, originalProject);
        File.WriteAllBytes(pair.Playback.Source.FullPath, Archive(
            new SyntheticAasRecord(TextJp: "", CompiledScript: "\n\n"),
            new SyntheticAasRecord(TextJp: "", CompiledScript: "")));
        PlaybackArchiveSnapshot changedPlayback = ReadPlayback(pair.Playback.Source.FullPath);
        ProjectSnapshot unchangedProject = ReadProject(pair.Project.Source.FullPath);
        RejectEvidence(pair with { Project = unchangedProject, Playback = changedPlayback },
            AssertEx.NotNull(unchangedProject.CompilationMapping));
        AssertEx.False(store.Save(pair.Evidence, pair.Project, pair.Playback).Success);
    }

    public static void CapturedDuplicateEmptyScriptsResolveOnlyWithAnActualMatchingRow()
    {
        using var files = new TemporaryAapDirectory();
        Fixture pair = EmptyPair(files, "duplicates-empty", duplicateScripts: true);
        ProjectSnapshot captured = pair.Project with { CompilationMapping = pair.Evidence };
        CompiledScriptIdentity script = CommandIdentity.CompiledScript("");
        ObservedCompiledSceneIdentity identity = new(script.Sha256, script.Utf16Length, script.LineCount);
        var resolvers = new[]
        {
            new ObservedSceneIdentityResolver(),
            new ObservedSceneIdentityResolver(null, null, () => "identity/v1")
        };
        foreach (ObservedSceneIdentityResolver resolver in resolvers)
        {
            foreach (int unknownOrWrongRow in new[] { -1, 2 })
            {
                var unresolved = resolver.Resolve(identity with { PlaybackRowIndex = unknownOrWrongRow }, captured, pair.Playback);
                AssertEx.True(unresolved.Success, unresolved.Error);
                ObservedSceneIdentityResolution ambiguous = AssertEx.NotNull(unresolved.Value);
                AssertEx.Equal(ObservedSceneIdentityStatus.AmbiguousPlaybackScript, ambiguous.Status);
                AssertEx.False(ambiguous.SelectedSceneTrusted);
                AssertEx.True(ambiguous.Scene == null && ambiguous.PlaybackRecordIndex == null);
            }
            foreach (int row in new[] { 0, 1 })
            {
                var resolved = resolver.Resolve(identity with { PlaybackRowIndex = row }, captured, pair.Playback);
                AssertEx.True(resolved.Success, resolved.Error);
                ObservedSceneIdentityResolution match = AssertEx.NotNull(resolved.Value);
                AssertEx.Equal(ObservedSceneIdentityStatus.Mapped, match.Status);
                AssertEx.Equal(row, match.PlaybackRecordIndex);
                AssertEx.Equal(pair.Evidence.Entries.Single(entry => entry.PlaybackRecordIndex == row).Scene, match.Scene);
                AssertEx.True(match.SelectedSceneTrusted);
            }
        }
        var compiled = Compile(captured, pair.Playback);
        var bound = new PlaybackCommandBinder().Bind(pair.Playback, compiled.Projection);
        AssertEx.True(bound.Success, bound.Error);
        IPlaybackCommandIndex index = AssertEx.NotNull(bound.Value);
        foreach (int row in new[] { -1, 2 })
        {
            var ambiguous = PlaybackAdvanceDispatchPolicy.ObserveCompleted(index, new[] { "" }, row);
            AssertEx.True(ambiguous.Success, ambiguous.Error);
            AssertEx.Equal(PlaybackCommandWindowObservationStatus.AmbiguousIdentity, AssertEx.NotNull(ambiguous.Value).Status);
            AssertEx.True(ambiguous.Value!.Batch == null);
            AssertEx.False(PlaybackAdvanceDispatchPolicy.ObserveExactRow(index, new[] { "" }, row).Success);
        }
        AssertEx.True(PlaybackAdvanceDispatchPolicy.ObserveCompleted(index, new[] { "" }, 0).Success);
        AssertEx.True(PlaybackAdvanceDispatchPolicy.ObserveCompleted(index, new[] { "" }, 1).Success);
    }

    public static void SharedEmptyRowAcrossCapturedProjectsStillFailsCrossProjectArbitration()
    {
        using var files = new TemporaryAapDirectory();
        Fixture alpha = EmptyPair(files, "alpha", duplicateScripts: true);
        Fixture beta = EmptyPair(files, "beta", duplicateScripts: true);
        var store = new JsonCompilationMappingEvidenceStore();
        AssertEx.True(store.Save(alpha.Evidence, alpha.Project, alpha.Playback).Success);
        AssertEx.True(store.Save(beta.Evidence, beta.Project, beta.Playback).Success);
        var pairs = new[]
        {
            new DiscoveredProjectPair("alpha", alpha.Project.Source.FullPath, alpha.Playback.Source.FullPath, true),
            new DiscoveredProjectPair("beta", beta.Project.Source.FullPath, beta.Playback.Source.FullPath, true)
        };
        CompiledScriptIdentity script = CommandIdentity.CompiledScript("");
        var identity = new ObservedCompiledSceneIdentity(script.Sha256, script.Utf16Length, script.LineCount)
        {
            PlaybackRowIndex = 0
        };
        var arbiter = new ActiveProjectPairArbiter(new AapProjectReader().Read, new AasScenarioReader().Read);
        ActivePairArbitration result = arbiter.Arbitrate(identity, pairs);
        AssertEx.Equal(ActivePairArbitrationStatus.AmbiguousCrossProject, result.Status);
        AssertEx.False(result.Success);
        AssertEx.True(result.Pair == null && result.Resolution == null);
        AssertEx.True(result.Diagnostics.Any(line => line.StartsWith("also-matched=", StringComparison.Ordinal)));
        // A known active pair can still resolve its own row; the cross-project sweep cannot choose one for us.
        var sticky = arbiter.TryResolvePair(pairs[0], identity);
        AssertEx.True(sticky.Success, sticky.Error);
        AssertEx.Equal(ObservedSceneIdentityStatus.Mapped, AssertEx.NotNull(sticky.Value).Status);
    }

    public static void MappingSidecarMalformedJsonAndNullEntriesRemainFailClosed()
    {
        using var files = new TemporaryAapDirectory();
        Fixture pair = EmptyPair(files, "malformed");
        string sidecar = JsonCompilationMappingEvidenceStore.GetPath(pair.Project.Source.FullPath);
        var store = new JsonCompilationMappingEvidenceStore();
        foreach (string malformed in new[] { "{invalid", "{}", "{\"entries\":[]}" })
        {
            File.WriteAllText(sidecar, malformed);
            AssertEx.False(store.Read(pair.Project.Source.FullPath).Success);
            ProjectSnapshot read = ReadProject(pair.Project.Source.FullPath);
            AssertEx.True(read.CompilationMapping == null);
            AssertEx.Contains(read.Diagnostics, diagnostic => diagnostic.Code == "CompilerMappingEvidenceUnreadable",
                "An unreadable sidecar must leave the official project readable and commands unauthorized.");
            AssertEx.Equal(0, Map(read, pair.Playback).UniqueMappingCount);
        }
        CompilationMappingEvidence invalid = pair.Evidence with { Entries = new CompilationMappingEntry[] { null! } };
        File.WriteAllText(sidecar, System.Text.Json.JsonSerializer.Serialize(invalid,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
        ProjectSnapshot malformedEntries = ReadProject(pair.Project.Source.FullPath);
        RejectEvidence(pair with { Project = malformedEntries }, AssertEx.NotNull(malformedEntries.CompilationMapping));
    }

    private static Fixture EmptyPair(TemporaryAapDirectory files, string name, bool duplicateScripts = false)
    {
        ProjectSnapshot project = ReadProject(files.Write(name + ".aap", SyntheticAap.ProjectWithTwoScriptNodes(
            new[] { SyntheticAap.Scene("", additionalPrompt: "#aavt;char;3;set;x=111;duration=0;easing=linear") },
            new[] { SyntheticAap.Scene("", additionalPrompt: "#aavt;char;3;set;x=222;duration=0;easing=linear") })));
        PlaybackArchiveSnapshot playback = ReadPlayback(files.WriteBytes(name + ".aas", Archive(
            new SyntheticAasRecord(TextJp: "", CompiledScript: duplicateScripts ? "" : "\n"),
            new SyntheticAasRecord(TextJp: "", CompiledScript: ""))));
        SceneSnapshot[] scenes = project.ScriptNodes.SelectMany(node => node.Scenes).ToArray();
        CompilationMappingEntry[] entries = scenes.Select((scene, index) =>
        {
            PlaybackRecordSnapshot record = playback.Records[1 - index];
            return new CompilationMappingEntry(scene.Key, record.RecordIndex, record.Fingerprint,
                CommandIdentity.CompiledScript(record.CompiledScript));
        }).ToArray();
        var evidence = new CompilationMappingEvidence(CompilationMappingEvidence.CurrentSchemaVersion,
            project.Source.RevisionSha256, project.Source.Length,
            playback.Source.RevisionSha256, playback.Source.Length, entries);
        return new Fixture(project, playback, evidence);
    }

    private static void RejectEvidence(Fixture pair, CompilationMappingEvidence evidence)
    {
        var validated = CompilationMappingEvidenceValidator.Validate(evidence, pair.Project, pair.Playback);
        AssertEx.False(validated.Success);
        ProjectSnapshot rejected = pair.Project with { CompilationMapping = evidence };
        ProjectPlaybackMappingReport mapped = Map(rejected, pair.Playback);
        AssertEx.Equal(0, mapped.UniqueMappingCount);
        AssertEx.Contains(mapped.Issues, issue => issue.Code == "CompilerMappingEvidenceRejected",
            "Invalid capture evidence must be reported and must not authorize the all-empty nodes.");
        AssertEx.False(new EmbeddedProjectCommandCompiler().Compile(rejected, pair.Playback, "1.5.2").Success);
    }

    private static ProjectSnapshot ReadProject(string path)
    {
        var result = new AapProjectReader().Read(path);
        AssertEx.True(result.Success, result.Error);
        return AssertEx.NotNull(result.Value);
    }

    private static PlaybackArchiveSnapshot ReadPlayback(string path)
    {
        var result = new AasScenarioReader().Read(path);
        AssertEx.True(result.Success, result.Error);
        return AssertEx.NotNull(result.Value);
    }

    private static ProjectPlaybackMappingReport Map(ProjectSnapshot project, PlaybackArchiveSnapshot playback)
    {
        var result = new ConservativeProjectPlaybackMapper().Map(project, playback);
        AssertEx.True(result.Success, result.Error);
        return AssertEx.NotNull(result.Value);
    }

    private static EmbeddedProjectCommandCompilation Compile(ProjectSnapshot project, PlaybackArchiveSnapshot playback)
    {
        var result = new EmbeddedProjectCommandCompiler().Compile(project, playback, "1.5.2");
        AssertEx.True(result.Success, result.Error);
        return AssertEx.NotNull(result.Value);
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    // Reuse the known one-record fixture's record payloads. Relative FlatBuffer
    // offsets stay unchanged; only the root table/vector slots are rebuilt.
    private static byte[] Archive(params SyntheticAasRecord[] records)
    {
        byte[][] payloads = records.Select(record => SyntheticAas.Archive(record)[28..]).ToArray();
        byte[] bytes = new byte[24 + records.Length * 4 + payloads.Sum(payload => payload.Length + 8)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), 12);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4, 2), 6);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6, 2), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8, 2), 4);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12, 4), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20, 4), (uint)records.Length);
        int cursor = 24 + records.Length * 4;
        for (int index = 0; index < records.Length; index++)
        {
            int payloadStart = ((cursor + 36 + 7) & ~7) - 36;
            int vectorSlot = 24 + index * 4;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(vectorSlot, 4), (uint)(payloadStart + 36 - vectorSlot));
            payloads[index].CopyTo(bytes, payloadStart);
            cursor = payloadStart + payloads[index].Length;
        }
        return bytes[..cursor];
    }

    private sealed record Fixture(ProjectSnapshot Project, PlaybackArchiveSnapshot Playback,
        CompilationMappingEvidence Evidence);
}
