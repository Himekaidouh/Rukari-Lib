using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Mapping;

/// <summary>Copied values from a successful official export, bound to both archive contents.</summary>
public sealed record CompilationMappingEntry(
    SceneKey Scene,
    int PlaybackRecordIndex,
    string PlaybackRecordFingerprint,
    CompiledScriptIdentity CompiledScript);

public sealed record CompilationMappingEvidence(
    int SchemaVersion,
    string ProjectRevisionSha256,
    long ProjectLength,
    string PlaybackRevisionSha256,
    long PlaybackLength,
    IReadOnlyList<CompilationMappingEntry> Entries)
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumEntries = 100000;
}

public static class CompilationMappingEvidenceValidator
{
    public static Result Validate(
        CompilationMappingEvidence evidence,
        ProjectSnapshot project,
        PlaybackArchiveSnapshot playback)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(playback);
        if (evidence.SchemaVersion != CompilationMappingEvidence.CurrentSchemaVersion
            || !SameSha(evidence.ProjectRevisionSha256, project.Source.RevisionSha256)
            || evidence.ProjectLength != project.Source.Length || evidence.ProjectLength <= 0
            || !SameSha(evidence.PlaybackRevisionSha256, playback.Source.RevisionSha256)
            || evidence.PlaybackLength != playback.Source.Length || evidence.PlaybackLength <= 0)
            return Result.Fail("compiler-mapping-evidence-archive-revision-mismatch");

        if (evidence.Entries == null || evidence.Entries.Count == 0
            || evidence.Entries.Count > CompilationMappingEvidence.MaximumEntries
            || project.Nodes == null || playback.Records == null)
            return Result.Fail("compiler-mapping-evidence-collection-invalid");

        var scenes = new Dictionary<string, SceneSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (StoryNodeSnapshot node in project.ScriptNodes)
        {
            if (!node.HasPersistentGuid || !Guid.TryParseExact(node.NodeGuid, "D", out _))
                continue;
            foreach (SceneSnapshot scene in node.Scenes)
                if (!scenes.TryAdd(Location(scene.Key), scene))
                    return Result.Fail("compiler-mapping-evidence-project-scene-duplicated");
        }

        var sourceClaims = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var recordClaims = new HashSet<int>();
        foreach (CompilationMappingEntry? entry in evidence.Entries)
        {
            if (entry?.Scene == null || entry.CompiledScript == null
                || !Guid.TryParseExact(entry.Scene.NodeGuid, "D", out _)
                || entry.Scene.SceneIndex < 0
                || !scenes.TryGetValue(Location(entry.Scene), out SceneSnapshot? currentScene)
                || !SameSha(entry.Scene.Fingerprint, currentScene.Key.Fingerprint)
                || !sourceClaims.Add(Location(entry.Scene))
                || entry.PlaybackRecordIndex < 0
                || entry.PlaybackRecordIndex >= playback.Records.Count
                || !recordClaims.Add(entry.PlaybackRecordIndex))
                return Result.Fail("compiler-mapping-evidence-scene-or-record-invalid");

            PlaybackRecordSnapshot record = playback.Records[entry.PlaybackRecordIndex];
            CompiledScriptIdentity identity = CommandIdentity.CompiledScript(record.CompiledScript);
            if (record.RecordIndex != entry.PlaybackRecordIndex
                || !string.Equals(currentScene.DialogueText, record.TextJp, StringComparison.Ordinal)
                || !SameSha(entry.PlaybackRecordFingerprint, record.Fingerprint)
                || !SameSha(entry.CompiledScript.Sha256, identity.Sha256)
                || entry.CompiledScript.Utf16Length != identity.Utf16Length
                || entry.CompiledScript.LineCount != identity.LineCount)
                return Result.Fail("compiler-mapping-evidence-record-content-mismatch");
        }
        return Result.Ok();
    }

    private static string Location(SceneKey scene) => $"{scene.NodeGuid}:{scene.SceneIndex}";
    private static bool SameSha(string? left, string? right) =>
        CommandIdentity.TryNormalizeSha256(left ?? string.Empty, out string first)
        && CommandIdentity.TryNormalizeSha256(right ?? string.Empty, out string second)
        && first == second;
}
