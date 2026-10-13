using System.Security.Cryptography;
using System.Text.Json;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Projects;

/// <summary>The original AAP2 and its officially written, same-name legacy projection.</summary>
public sealed record OfficialAapCompatibilityBinding(
    string ProjectPath,
    string NativeProjectPath,
    string NativeRevisionSha256,
    string ProjectionRevisionSha256);

/// <summary>
/// Reads AA's compatibility projection instead of interpreting AAP2 as legacy JSON.
/// Hash/identity checks authorize a source format only; scene and compiler mapping proofs
/// remain the responsibility of their existing readers. Never writes either game file.
/// </summary>
public static class OfficialAapCompatibilityProjection
{
    private const long DefaultMaximumBytes = 64L * 1024 * 1024;
    private const int MaximumResolvedEntries = 16;
    private static readonly object ResolutionGate = new();
    private static readonly Dictionary<string, ResolutionEntry> Resolutions = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsNativePath(string path) =>
        Path.GetExtension(path).Equals(".aap2", StringComparison.OrdinalIgnoreCase);

    /// <summary>Legacy paths stay unchanged. AAP2 paths require the verified official AAP.</summary>
    public static Result<OfficialAapCompatibilityBinding> Resolve(
        string path, long maximumBytes = DefaultMaximumBytes, int maximumJsonDepth = 128)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            if (!IsNativePath(fullPath))
                return Result<OfficialAapCompatibilityBinding>.Ok(new(fullPath, string.Empty, string.Empty, string.Empty));
            string projectionPath = Path.ChangeExtension(fullPath, ".aap");
            Result<PairRevision> before = ReadPairRevision(fullPath, projectionPath, maximumBytes);
            if (!before.Success || before.Value == null) return Fail(before.Error);
            lock (ResolutionGate)
            {
                if (Resolutions.TryGetValue(fullPath, out ResolutionEntry? cached)
                    && cached.Revision == before.Value && cached.MaximumBytes == maximumBytes
                    && cached.MaximumJsonDepth == maximumJsonDepth)
                    return Result<OfficialAapCompatibilityBinding>.Ok(cached.Binding);
            }
            Result<byte[]> projection = ReadStable(projectionPath, maximumBytes, "projection");
            if (!projection.Success || projection.Value == null) return Fail(projection.Error);
            using JsonDocument document = JsonDocument.Parse(projection.Value,
                new JsonDocumentOptions { MaxDepth = maximumJsonDepth });
            var validated = ValidateRead(projectionPath, document.RootElement, projection.Value,
                requireNative: true, maximumBytes, maximumJsonDepth);
            if (!validated.Success || validated.Value == null) return validated;
            Result<PairRevision> after = ReadPairRevision(fullPath, projectionPath, maximumBytes);
            if (!after.Success || after.Value == null) return Fail(after.Error);
            if (before.Value != after.Value) return Fail("aap2-projection-changed-during-read");
            lock (ResolutionGate)
            {
                if (Resolutions.Count >= MaximumResolvedEntries && !Resolutions.ContainsKey(fullPath)) Resolutions.Clear();
                Resolutions[fullPath] = new(after.Value, maximumBytes, maximumJsonDepth, validated.Value);
            }
            return validated;
        }
        catch (JsonException) { return Fail("aap2-projection-json-invalid"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException)
        { return Fail("aap2-projection-read-failed:" + ex.GetType().Name); }
    }

    /// <summary>
    /// Also validates AAPs reached by discovery, not just explicitly selected AAP2 paths.
    /// A compatibility marker with a missing source must never become a legacy fallback.
    /// </summary>
    public static Result<OfficialAapCompatibilityBinding> ValidateRead(
        string projectionPath, JsonElement projection, ReadOnlySpan<byte> projectionBytes,
        bool requireNative = false, long maximumBytes = DefaultMaximumBytes, int maximumJsonDepth = 128)
    {
        try
        {
            string fullPath = Path.GetFullPath(projectionPath);
            string nativePath = Path.ChangeExtension(fullPath, ".aap2");
            bool hasMarker = projection.ValueKind == JsonValueKind.Object
                && projection.TryGetProperty("Aap2Compatibility", out _);
            if (!requireNative && !hasMarker && !File.Exists(nativePath))
                return Result<OfficialAapCompatibilityBinding>.Ok(new(fullPath, string.Empty, string.Empty, string.Empty));
            if (!Path.GetExtension(fullPath).Equals(".aap", StringComparison.OrdinalIgnoreCase))
                return Fail("aap2-projection-extension-invalid");
            if (HasReparsePoint(fullPath)) return Fail("aap2-projection-reparse-rejected");
            if (!hasMarker || !projection.TryGetProperty("Aap2Compatibility", out JsonElement marker)
                || marker.ValueKind != JsonValueKind.Object
                || !Integer(marker, "Version", 1))
                return Fail("aap2-projection-compatibility-metadata-invalid");

            Result<byte[]> nativeRead = ReadStable(nativePath, maximumBytes, "source");
            if (!nativeRead.Success || nativeRead.Value == null) return Fail(nativeRead.Error);
            byte[] bytes = nativeRead.Value;
            if (bytes.Length <= 5 || bytes[0] != 'A' || bytes[1] != 'A' || bytes[2] != 'P'
                || bytes[3] != '2' || bytes[4] != '\n') return Fail("aap2-source-header-invalid");
            using JsonDocument nativeDocument = JsonDocument.Parse(bytes.AsMemory(5),
                new JsonDocumentOptions { MaxDepth = maximumJsonDepth });
            JsonElement native = nativeDocument.RootElement;
            if (native.ValueKind != JsonValueKind.Object || !Integer(native, "FormatVersion", 2))
                return Fail("aap2-source-format-invalid");
            string name = Path.GetFileNameWithoutExtension(nativePath);
            if (!Text(native, "ProjectName").Equals(name, StringComparison.Ordinal)
                || !Text(projection, "ProjectName").Equals(name, StringComparison.Ordinal))
                return Fail("aap2-projection-project-name-mismatch");
            if (!Guid.TryParse(Text(native, "ProjectId"), out Guid nativeId) || nativeId == Guid.Empty
                || !Guid.TryParse(Text(marker, "ProjectId"), out Guid projectionId) || projectionId != nativeId)
                return Fail("aap2-projection-project-id-mismatch");
            string hash = Convert.ToHexString(SHA256.HashData(bytes));
            if (!Text(marker, "SourceFileVersion").Equals("sha256:" + hash, StringComparison.OrdinalIgnoreCase))
                return Fail("aap2-projection-source-hash-mismatch");
            return Result<OfficialAapCompatibilityBinding>.Ok(new(fullPath, nativePath, hash,
                Convert.ToHexString(SHA256.HashData(projectionBytes))));
        }
        catch (JsonException) { return Fail("aap2-source-json-invalid"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException)
        { return Fail("aap2-projection-validation-failed:" + ex.GetType().Name); }
    }

    /// <summary>Rejects a native save arriving while its projection is being parsed.</summary>
    public static Result VerifyStillCurrent(
        OfficialAapCompatibilityBinding binding, long maximumBytes = DefaultMaximumBytes)
    {
        if (binding.NativeProjectPath.Length == 0) return Result.Ok();
        try
        {
            string path = binding.NativeProjectPath;
            if (HasReparsePoint(path)) return Result.Fail("reason=aap2-source-reparse-rejected");
            if (!File.Exists(path)) return Result.Fail("reason=aap2-source-unavailable");
            var before = new FileInfo(path);
            before.Refresh();
            if (before.Length <= 0 || before.Length > maximumBytes)
                return Result.Fail("reason=aap2-source-size-invalid");
            string hash;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var digest = SHA256.Create()) hash = Convert.ToHexString(digest.ComputeHash(stream));
            var after = new FileInfo(path);
            after.Refresh();
            return after.Exists && !HasReparsePoint(path) && before.Length == after.Length
                && before.LastWriteTimeUtc == after.LastWriteTimeUtc
                && hash.Equals(binding.NativeRevisionSha256, StringComparison.OrdinalIgnoreCase)
                    ? Result.Ok() : Result.Fail("reason=aap2-source-changed-during-read");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException)
        { return Result.Fail("reason=aap2-source-revalidation-failed:" + ex.GetType().Name); }
    }

    private static Result<byte[]> ReadStable(string path, long maximumBytes, string role)
    {
        if (HasReparsePoint(path)) return Result<byte[]>.Fail("aap2-" + role + "-reparse-rejected");
        if (!File.Exists(path)) return Result<byte[]>.Fail("aap2-" + role + "-unavailable");
        var before = new FileInfo(path);
        before.Refresh();
        if (maximumBytes <= 0 || before.Length <= 0 || before.Length > maximumBytes || before.Length > int.MaxValue)
            return Result<byte[]>.Fail("aap2-" + role + "-size-invalid");
        byte[] bytes;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (stream.Length != before.Length) return Result<byte[]>.Fail("aap2-" + role + "-changed-during-read");
            bytes = new byte[(int)stream.Length];
            int total = 0;
            while (total < bytes.Length)
            {
                int read = stream.Read(bytes, total, bytes.Length - total);
                if (read == 0) return Result<byte[]>.Fail("aap2-" + role + "-changed-during-read");
                total += read;
            }
        }
        var after = new FileInfo(path);
        after.Refresh();
        return after.Exists && !HasReparsePoint(path) && before.Length == after.Length
            && before.LastWriteTimeUtc == after.LastWriteTimeUtc
            ? Result<byte[]>.Ok(bytes) : Result<byte[]>.Fail("aap2-" + role + "-changed-during-read");
    }

    private static bool HasReparsePoint(string path)
    {
        FileSystemInfo current = new FileInfo(path);
        while (true)
        {
            current.Refresh();
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) return true;
            string? parent = Path.GetDirectoryName(current.FullName);
            if (string.IsNullOrEmpty(parent) || parent.Equals(current.FullName, StringComparison.OrdinalIgnoreCase)) return false;
            current = new DirectoryInfo(parent);
        }
    }

    // Resolve is also used for live path normalization before snapshot-cache hits.
    // A bounded receipt cache avoids hashing both documents every frame. Actual
    // reader cache misses still call ValidateRead and VerifyStillCurrent in full.
    private sealed record FileRevision(long Length, DateTime LastWriteUtc, DateTime CreationUtc);
    private sealed record PairRevision(FileRevision Native, FileRevision Projection);
    private sealed record ResolutionEntry(PairRevision Revision, long MaximumBytes, int MaximumJsonDepth,
        OfficialAapCompatibilityBinding Binding);

    private static Result<PairRevision> ReadPairRevision(string nativePath, string projectionPath, long maximumBytes)
    {
        var native = ReadRevision(nativePath, maximumBytes, "source");
        if (!native.Success || native.Value == null) return Result<PairRevision>.Fail(native.Error);
        var projection = ReadRevision(projectionPath, maximumBytes, "projection");
        return projection.Success && projection.Value != null
            ? Result<PairRevision>.Ok(new(native.Value, projection.Value)) : Result<PairRevision>.Fail(projection.Error);
    }

    private static Result<FileRevision> ReadRevision(string path, long maximumBytes, string role)
    {
        if (HasReparsePoint(path)) return Result<FileRevision>.Fail("aap2-" + role + "-reparse-rejected");
        if (!File.Exists(path)) return Result<FileRevision>.Fail("aap2-" + role + "-unavailable");
        var file = new FileInfo(path);
        file.Refresh();
        return maximumBytes > 0 && file.Exists && file.Length > 0 && file.Length <= maximumBytes
            ? Result<FileRevision>.Ok(new(file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc))
            : Result<FileRevision>.Fail("aap2-" + role + "-size-invalid");
    }

    private static bool Integer(JsonElement owner, string name, int expected) =>
        owner.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out int number) && number == expected;
    private static string Text(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty : string.Empty;
    private static Result<OfficialAapCompatibilityBinding> Fail(string reason) =>
        Result<OfficialAapCompatibilityBinding>.Fail(reason.StartsWith("reason=", StringComparison.Ordinal) ? reason : "reason=" + reason);
}
