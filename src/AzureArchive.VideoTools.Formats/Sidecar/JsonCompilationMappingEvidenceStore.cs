using System.Security.Cryptography;
using System.Text.Json;
using AzureArchive.VideoTools.Core.Mapping;
using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Formats.Sidecar;

/// <summary>Only writes our own mapping sidecar. Never modifies AAP or AAS bytes.</summary>
public sealed class JsonCompilationMappingEvidenceStore
{
    public const string Extension = ".rukari-map.json";
    private const long MaximumBytes = 64 * 1024 * 1024;
    private static readonly object WriteGate = new();
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 16,
        WriteIndented = true
    };

    public static string GetPath(string projectPath)
    {
        string fullPath = Path.GetFullPath(projectPath);
        if (!Path.GetExtension(fullPath).Equals(".aap", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Compiler mapping sidecar must belong to an AAP file.", nameof(projectPath));
        return fullPath + Extension;
    }

    public Result<CompilationMappingEvidence> Read(string projectPath)
    {
        try
        {
            string path = GetPath(projectPath);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length <= 0 || stream.Length > MaximumBytes)
                return Result<CompilationMappingEvidence>.Fail("compiler-mapping-sidecar-size-invalid");
            CompilationMappingEvidence? evidence = JsonSerializer.Deserialize<CompilationMappingEvidence>(stream, Options);
            if (evidence?.Entries == null || evidence.Entries.Count == 0
                || evidence.Entries.Count > CompilationMappingEvidence.MaximumEntries)
                return Result<CompilationMappingEvidence>.Fail("compiler-mapping-sidecar-collection-invalid");
            return Result<CompilationMappingEvidence>.Ok(evidence);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return Result<CompilationMappingEvidence>.Fail($"compiler-mapping-sidecar-read-failed: {ex.Message}");
        }
    }

    public Result<string> Save(
        CompilationMappingEvidence evidence, ProjectSnapshot project, PlaybackArchiveSnapshot playback)
    {
        Result validated = CompilationMappingEvidenceValidator.Validate(evidence, project, playback);
        if (!validated.Success) return Result<string>.Fail(validated.Error);
        string? temporary = null;
        lock (WriteGate)
        {
            try
            {
                string path = GetPath(project.Source.FullPath);
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(evidence, Options);
                if (bytes.LongLength > MaximumBytes) return Result<string>.Fail("compiler-mapping-sidecar-size-invalid");
                if (!StillCurrent(project.Source.FullPath, evidence.ProjectLength, evidence.ProjectRevisionSha256)
                    || !StillCurrent(playback.Source.FullPath, evidence.PlaybackLength, evidence.PlaybackRevisionSha256))
                    return Result<string>.Fail("compiler-mapping-publication-changed-before-save");
                temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                if (!StillCurrent(project.Source.FullPath, evidence.ProjectLength, evidence.ProjectRevisionSha256)
                    || !StillCurrent(playback.Source.FullPath, evidence.PlaybackLength, evidence.PlaybackRevisionSha256))
                    return Result<string>.Fail("compiler-mapping-publication-changed-during-save");
                File.Move(temporary, path, overwrite: true);
                temporary = null;
                return Result<string>.Ok(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                return Result<string>.Fail($"compiler-mapping-sidecar-save-failed: {ex.Message}");
            }
            finally
            {
                if (temporary != null)
                    try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static bool StillCurrent(string path, long length, string hash)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != length) return false;
        using var digest = SHA256.Create();
        return Convert.ToHexString(digest.ComputeHash(stream)).Equals(hash, StringComparison.OrdinalIgnoreCase);
    }
}
