using System.Text.Json;
using AzureArchive.VideoTools.Core.Continuity;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Formats.Sidecar;

public sealed class JsonContinuationStore : IContinuationStore
{
    public const string SidecarExtension = ".aavt.json";
    public const string BackupSuffix = ".bak";

    private static readonly HashSet<string> RootProperties = new(StringComparer.Ordinal)
    {
        "schemaVersion",
        "projectId",
        "projectPathKey",
        "projectRevisionSha256",
        "rules"
    };

    private static readonly HashSet<string> RuleProperties = new(StringComparer.Ordinal)
    {
        "scene",
        "enabled"
    };

    private static readonly HashSet<string> SceneProperties = new(StringComparer.Ordinal)
    {
        "nodeGuid",
        "sceneIndex",
        "fingerprint"
    };

    private readonly string _rootDirectory;
    private readonly string _rootPrefix;
    private readonly StringComparison _pathComparison;
    private readonly JsonContinuationStoreOptions _options;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly object _writeGate = new();

    public JsonContinuationStore(
        string rootDirectory,
        JsonContinuationStoreOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("Sidecar root directory is empty.", nameof(rootDirectory));
        }

        _options = options ?? new JsonContinuationStoreOptions();
        ValidateOptions(_options);

        string fullRoot = Path.GetFullPath(rootDirectory);
        string? volumeRoot = Path.GetPathRoot(fullRoot);
        _rootDirectory = string.Equals(
                fullRoot,
                volumeRoot,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal)
            ? fullRoot
            : fullRoot.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        if (string.IsNullOrEmpty(_rootDirectory))
        {
            throw new ArgumentException("Sidecar root directory is invalid.", nameof(rootDirectory));
        }

        _rootPrefix = _rootDirectory.EndsWith(Path.DirectorySeparatorChar)
            || _rootDirectory.EndsWith(Path.AltDirectorySeparatorChar)
            ? _rootDirectory
            : _rootDirectory + Path.DirectorySeparatorChar;
        _pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        _serializerOptions = new JsonSerializerOptions
        {
            AllowTrailingCommas = false,
            MaxDepth = _options.MaxJsonDepth,
            PropertyNameCaseInsensitive = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            WriteIndented = true
        };
    }

    public string RootDirectory => _rootDirectory;

    public Result<ContinuationLoadSnapshot> TryLoad(string sidecarPath)
    {
        Result<string> pathResult = ResolveSidecarPath(sidecarPath);
        if (!pathResult.Success || pathResult.Value == null)
        {
            return Result<ContinuationLoadSnapshot>.Fail(pathResult.Error);
        }

        string primaryPath = pathResult.Value;
        string backupPath = GetBackupPath(primaryPath);

        try
        {
            bool primaryExists = File.Exists(primaryPath);
            bool backupExists = File.Exists(backupPath);
            if (!primaryExists && !backupExists)
            {
                return Result<ContinuationLoadSnapshot>.Ok(new ContinuationLoadSnapshot(
                    null,
                    ContinuationLoadSource.None,
                    Array.Empty<ContinuationStoreNotice>()));
            }

            Result<ContinuationDocument>? primaryResult = null;
            if (primaryExists)
            {
                Result safePrimary = EnsureNoReparsePoints(primaryPath);
                primaryResult = safePrimary.Success
                    ? ReadDocumentFile(primaryPath)
                    : Result<ContinuationDocument>.Fail(safePrimary.Error);

                if (primaryResult.Success && primaryResult.Value != null)
                {
                    return Result<ContinuationLoadSnapshot>.Ok(new ContinuationLoadSnapshot(
                        primaryResult.Value,
                        ContinuationLoadSource.Primary,
                        Array.Empty<ContinuationStoreNotice>()));
                }
            }

            if (backupExists)
            {
                Result safeBackup = EnsureNoReparsePoints(backupPath);
                Result<ContinuationDocument> backupResult = safeBackup.Success
                    ? ReadDocumentFile(backupPath)
                    : Result<ContinuationDocument>.Fail(safeBackup.Error);
                if (backupResult.Success && backupResult.Value != null)
                {
                    string code = primaryExists ? "PrimaryUnreadable" : "PrimaryMissing";
                    string message = primaryExists
                        ? "The primary sidecar is unreadable; the previous atomic backup was loaded."
                        : "The primary sidecar is missing; the previous atomic backup was loaded.";
                    return Result<ContinuationLoadSnapshot>.Ok(new ContinuationLoadSnapshot(
                        backupResult.Value,
                        ContinuationLoadSource.Backup,
                        new[] { new ContinuationStoreNotice(code, message) }));
                }

                string primaryError = primaryResult?.Error ?? "Primary sidecar is missing.";
                return Result<ContinuationLoadSnapshot>.Fail(
                    $"Neither primary nor backup sidecar is readable. Primary: {primaryError} Backup: {backupResult.Error}");
            }

            return Result<ContinuationLoadSnapshot>.Fail(
                $"Primary sidecar is unreadable and no backup exists: {primaryResult?.Error}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Result<ContinuationLoadSnapshot>.Fail(
                $"Sidecar cannot be accessed: {ex.Message}");
        }
        catch (IOException ex)
        {
            return Result<ContinuationLoadSnapshot>.Fail(
                $"Sidecar could not be read: {ex.Message}");
        }
        catch (Exception ex)
        {
            return Result<ContinuationLoadSnapshot>.Fail(
                $"Unexpected sidecar read failure ({ex.GetType().Name}): {ex.Message}");
        }
    }

    public Result<ContinuationSaveSnapshot> SaveAtomic(
        string sidecarPath,
        ContinuationDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        Result<string> pathResult = ResolveSidecarPath(sidecarPath);
        if (!pathResult.Success || pathResult.Value == null)
        {
            return Result<ContinuationSaveSnapshot>.Fail(pathResult.Error);
        }

        Result documentValidation = ValidateDocument(document);
        if (!documentValidation.Success)
        {
            return Result<ContinuationSaveSnapshot>.Fail(documentValidation.Error);
        }

        byte[] bytes;
        try
        {
            bytes = JsonSerializer.SerializeToUtf8Bytes(document, _serializerOptions);
        }
        catch (Exception ex)
        {
            return Result<ContinuationSaveSnapshot>.Fail(
                $"Continuation metadata could not be serialized ({ex.GetType().Name}): {ex.Message}");
        }

        if (bytes.LongLength > _options.MaxFileSizeBytes)
        {
            return Result<ContinuationSaveSnapshot>.Fail(
                $"Serialized sidecar is {bytes.LongLength} bytes, exceeding the {_options.MaxFileSizeBytes}-byte limit.");
        }

        lock (_writeGate)
        {
            return SaveAtomicCore(pathResult.Value, document, bytes);
        }
    }

    private Result<ContinuationSaveSnapshot> SaveAtomicCore(
        string primaryPath,
        ContinuationDocument document,
        byte[] bytes)
    {
        string? directory = Path.GetDirectoryName(primaryPath);
        if (string.IsNullOrEmpty(directory))
        {
            return Result<ContinuationSaveSnapshot>.Fail(
                "Sidecar path has no parent directory.");
        }

        string backupPath = GetBackupPath(primaryPath);
        string lockPath = primaryPath + ".lock";
        string temporaryPath = primaryPath + ".tmp." + Guid.NewGuid().ToString("N");

        try
        {
            Result existingPathSafety = EnsureNoReparsePoints(primaryPath);
            if (!existingPathSafety.Success)
            {
                return Result<ContinuationSaveSnapshot>.Fail(existingPathSafety.Error);
            }

            Directory.CreateDirectory(directory);

            Result createdPathSafety = EnsureNoReparsePoints(primaryPath);
            if (!createdPathSafety.Success)
            {
                return Result<ContinuationSaveSnapshot>.Fail(createdPathSafety.Error);
            }

            Result backupSafety = EnsureNoReparsePoints(backupPath);
            if (!backupSafety.Success)
            {
                return Result<ContinuationSaveSnapshot>.Fail(backupSafety.Error);
            }

            Result lockSafety = EnsureNoReparsePoints(lockPath);
            if (!lockSafety.Success)
            {
                return Result<ContinuationSaveSnapshot>.Fail(lockSafety.Error);
            }

            Result<FileStream> lockResult = AcquireWriteLock(lockPath);
            if (!lockResult.Success || lockResult.Value == null)
            {
                return Result<ContinuationSaveSnapshot>.Fail(lockResult.Error);
            }

            using (lockResult.Value)
            {
                bool replacedExisting = File.Exists(primaryPath);
                WriteTemporaryFile(temporaryPath, bytes);

                Result<ContinuationDocument> temporaryResult = ReadDocumentFile(temporaryPath);
                if (!temporaryResult.Success
                    || temporaryResult.Value == null
                    || !DocumentsEqual(document, temporaryResult.Value))
                {
                    string error = temporaryResult.Success
                        ? "Temporary sidecar content changed during verification."
                        : temporaryResult.Error;
                    return Result<ContinuationSaveSnapshot>.Fail(
                        $"Temporary sidecar validation failed: {error}");
                }

                if (replacedExisting)
                {
                    File.Replace(
                        temporaryPath,
                        primaryPath,
                        backupPath,
                        ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temporaryPath, primaryPath);
                }

                Result<ContinuationDocument> committedResult = ReadDocumentFile(primaryPath);
                if (!committedResult.Success
                    || committedResult.Value == null
                    || !DocumentsEqual(document, committedResult.Value))
                {
                    string error = committedResult.Success
                        ? "Committed sidecar content does not match the requested document."
                        : committedResult.Error;
                    return Result<ContinuationSaveSnapshot>.Fail(
                        $"Committed sidecar validation failed: {error}");
                }

                return Result<ContinuationSaveSnapshot>.Ok(new ContinuationSaveSnapshot(
                    primaryPath,
                    replacedExisting,
                    replacedExisting && File.Exists(backupPath)));
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            return Result<ContinuationSaveSnapshot>.Fail(
                $"Sidecar cannot be written: {ex.Message}");
        }
        catch (IOException ex)
        {
            return Result<ContinuationSaveSnapshot>.Fail(
                $"Sidecar atomic save failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            return Result<ContinuationSaveSnapshot>.Fail(
                $"Unexpected sidecar save failure ({ex.GetType().Name}): {ex.Message}");
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private Result<FileStream> AcquireWriteLock(string lockPath)
    {
        IOException? lastError = null;
        for (int attempt = 1; attempt <= _options.LockRetryAttempts; attempt++)
        {
            try
            {
                return Result<FileStream>.Ok(new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None));
            }
            catch (IOException ex)
            {
                lastError = ex;
                if (attempt < _options.LockRetryAttempts)
                {
                    Thread.Sleep(_options.LockRetryDelayMilliseconds);
                }
            }
        }

        return Result<FileStream>.Fail(
            $"Sidecar write lock could not be acquired after {_options.LockRetryAttempts} attempts: {lastError?.Message}");
    }

    private void WriteTemporaryFile(string path, byte[] bytes)
    {
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.SequentialScan | FileOptions.WriteThrough);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(flushToDisk: true);
    }

    private Result<ContinuationDocument> ReadDocumentFile(string path)
    {
        try
        {
            byte[] bytes;
            using (var stream = new FileStream(
                       path,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read | FileShare.Delete,
                       bufferSize: 4096,
                       FileOptions.SequentialScan))
            {
                if (stream.Length <= 0)
                {
                    return Result<ContinuationDocument>.Fail("Sidecar file is empty.");
                }

                if (stream.Length > _options.MaxFileSizeBytes)
                {
                    return Result<ContinuationDocument>.Fail(
                        $"Sidecar file is {stream.Length} bytes, exceeding the {_options.MaxFileSizeBytes}-byte limit.");
                }

                bytes = new byte[(int)stream.Length];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = stream.Read(bytes, offset, bytes.Length - offset);
                    if (read == 0)
                    {
                        return Result<ContinuationDocument>.Fail(
                            "Sidecar file ended before its declared length.");
                    }

                    offset += read;
                }
            }

            using JsonDocument json = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = _options.MaxJsonDepth
                });

            Result shapeValidation = ValidateJsonShape(json.RootElement);
            if (!shapeValidation.Success)
            {
                return Result<ContinuationDocument>.Fail(shapeValidation.Error);
            }

            ContinuationDocument? document = json.RootElement.Deserialize<ContinuationDocument>(
                _serializerOptions);
            if (document == null)
            {
                return Result<ContinuationDocument>.Fail(
                    "Sidecar JSON did not produce a continuation document.");
            }

            Result validation = ValidateDocument(document);
            return validation.Success
                ? Result<ContinuationDocument>.Ok(document)
                : Result<ContinuationDocument>.Fail(validation.Error);
        }
        catch (JsonException ex)
        {
            return Result<ContinuationDocument>.Fail(
                $"Sidecar JSON is invalid: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Result<ContinuationDocument>.Fail(
                $"Sidecar file cannot be accessed: {ex.Message}");
        }
        catch (IOException ex)
        {
            return Result<ContinuationDocument>.Fail(
                $"Sidecar file could not be read: {ex.Message}");
        }
        catch (Exception ex)
        {
            return Result<ContinuationDocument>.Fail(
                $"Unexpected sidecar parse failure ({ex.GetType().Name}): {ex.Message}");
        }
    }

    private Result<string> ResolveSidecarPath(string sidecarPath)
    {
        if (string.IsNullOrWhiteSpace(sidecarPath))
        {
            return Result<string>.Fail("Sidecar path is empty.");
        }

        if (Path.IsPathRooted(sidecarPath))
        {
            return Result<string>.Fail(
                "Sidecar path must be relative to the configured sidecar root.");
        }

        string normalized = sidecarPath
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        string[] segments = normalized.Split(Path.DirectorySeparatorChar);
        if (segments.Any(segment =>
                string.IsNullOrWhiteSpace(segment)
                || segment == "."
                || segment == ".."
                || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || segment.Contains(Path.VolumeSeparatorChar)))
        {
            return Result<string>.Fail(
                "Sidecar path contains an empty, traversal, or invalid segment.");
        }

        if (!normalized.EndsWith(SidecarExtension, StringComparison.OrdinalIgnoreCase))
        {
            return Result<string>.Fail(
                $"Sidecar path must end with {SidecarExtension}; official project and save extensions are not accepted.");
        }

        try
        {
            string fullPath = Path.GetFullPath(Path.Combine(_rootDirectory, normalized));
            if (!fullPath.StartsWith(_rootPrefix, _pathComparison))
            {
                return Result<string>.Fail(
                    "Sidecar path resolves outside the configured sidecar root.");
            }

            return Result<string>.Ok(fullPath);
        }
        catch (Exception ex) when (
            ex is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return Result<string>.Fail($"Sidecar path is invalid: {ex.Message}");
        }
    }

    private Result EnsureNoReparsePoints(string targetPath)
    {
        string? directory = Path.GetDirectoryName(targetPath);
        if (directory != null)
        {
            foreach (string current in EnumeratePathChain(directory))
            {
                if (Directory.Exists(current) && IsReparsePoint(current))
                {
                    return Result.Fail(
                        $"Sidecar path contains a reparse-point directory: {current}");
                }
            }
        }

        if ((File.Exists(targetPath) || Directory.Exists(targetPath))
            && IsReparsePoint(targetPath))
        {
            return Result.Fail($"Sidecar target is a reparse point: {targetPath}");
        }

        return Result.Ok();
    }

    private static IEnumerable<string> EnumeratePathChain(string fullPath)
    {
        string normalized = Path.GetFullPath(fullPath);
        string? root = Path.GetPathRoot(normalized);
        if (string.IsNullOrEmpty(root))
        {
            yield break;
        }

        string current = root;
        yield return current;

        string remainder = normalized[root.Length..];
        foreach (string segment in remainder.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            yield return current;
        }
    }

    private Result ValidateDocument(ContinuationDocument document)
    {
        if (document.SchemaVersion != ContinuationDocument.CurrentSchemaVersion)
        {
            return Result.Fail(
                $"Unsupported continuation schema version {document.SchemaVersion}; expected {ContinuationDocument.CurrentSchemaVersion}.");
        }

        if (!Guid.TryParseExact(document.ProjectId, "D", out _))
        {
            return Result.Fail("Continuation project ID must be a canonical UUID.");
        }

        if (!IsSha256(document.ProjectPathKey))
        {
            return Result.Fail("Continuation project path key must be a 64-character hexadecimal SHA-256 value.");
        }

        if (!IsSha256(document.ProjectRevisionSha256))
        {
            return Result.Fail("Continuation project revision must be a 64-character hexadecimal SHA-256 value.");
        }

        if (document.Rules == null)
        {
            return Result.Fail("Continuation rules collection is missing.");
        }

        if (document.Rules.Count > _options.MaxRules)
        {
            return Result.Fail(
                $"Continuation document has {document.Rules.Count} rules, exceeding the {_options.MaxRules}-rule limit.");
        }

        var locations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < document.Rules.Count; index++)
        {
            ContinuationRule? rule = document.Rules[index];
            if (rule == null)
            {
                return Result.Fail($"Continuation rule {index} is null.");
            }

            if (rule.Scene == null)
            {
                return Result.Fail($"Continuation rule {index} has no scene key.");
            }

            if (!Guid.TryParseExact(rule.Scene.NodeGuid, "D", out _))
            {
                return Result.Fail(
                    $"Continuation rule {index} node GUID is not a canonical UUID.");
            }

            if (rule.Scene.SceneIndex <= 0)
            {
                return Result.Fail(
                    $"Continuation rule {index} must target a scene after index 0.");
            }

            if (!IsSha256(rule.Scene.Fingerprint))
            {
                return Result.Fail(
                    $"Continuation rule {index} fingerprint must be a 64-character hexadecimal SHA-256 value.");
            }

            string location = $"{rule.Scene.NodeGuid}:{rule.Scene.SceneIndex}";
            if (!locations.Add(location))
            {
                return Result.Fail(
                    $"Continuation document contains duplicate rules for {location}.");
            }
        }

        return Result.Ok();
    }

    private static Result ValidateJsonShape(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Result.Fail("Sidecar root must be a JSON object.");
        }

        Result rootResult = ValidateObjectProperties(root, RootProperties, "sidecar root");
        if (!rootResult.Success)
        {
            return rootResult;
        }

        if (!root.TryGetProperty("rules", out JsonElement rules)
            || rules.ValueKind != JsonValueKind.Array)
        {
            return Result.Fail("Sidecar rules must be a JSON array.");
        }

        int ruleIndex = 0;
        foreach (JsonElement rule in rules.EnumerateArray())
        {
            if (rule.ValueKind != JsonValueKind.Object)
            {
                return Result.Fail($"Sidecar rule {ruleIndex} must be a JSON object.");
            }

            Result ruleResult = ValidateObjectProperties(
                rule,
                RuleProperties,
                $"sidecar rule {ruleIndex}");
            if (!ruleResult.Success)
            {
                return ruleResult;
            }

            if (!rule.TryGetProperty("scene", out JsonElement scene)
                || scene.ValueKind != JsonValueKind.Object)
            {
                return Result.Fail(
                    $"Sidecar rule {ruleIndex} scene must be a JSON object.");
            }

            Result sceneResult = ValidateObjectProperties(
                scene,
                SceneProperties,
                $"sidecar rule {ruleIndex} scene");
            if (!sceneResult.Success)
            {
                return sceneResult;
            }

            ruleIndex++;
        }

        return Result.Ok();
    }

    private static Result ValidateObjectProperties(
        JsonElement element,
        IReadOnlySet<string> allowed,
        string context)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                return Result.Fail(
                    $"Duplicate JSON property '{property.Name}' in {context}.");
            }

            if (!allowed.Contains(property.Name))
            {
                return Result.Fail(
                    $"Unknown JSON property '{property.Name}' in {context}.");
            }
        }

        return Result.Ok();
    }

    private static bool DocumentsEqual(
        ContinuationDocument expected,
        ContinuationDocument actual)
    {
        if (expected.SchemaVersion != actual.SchemaVersion
            || !string.Equals(expected.ProjectId, actual.ProjectId, StringComparison.Ordinal)
            || !string.Equals(expected.ProjectPathKey, actual.ProjectPathKey, StringComparison.Ordinal)
            || !string.Equals(expected.ProjectRevisionSha256, actual.ProjectRevisionSha256, StringComparison.Ordinal)
            || expected.Rules.Count != actual.Rules.Count)
        {
            return false;
        }

        for (int index = 0; index < expected.Rules.Count; index++)
        {
            if (expected.Rules[index] != actual.Rules[index])
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static string GetBackupPath(string primaryPath) =>
        primaryPath + BackupSuffix;

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // An orphaned temporary file is ignored by both load and save.
        }
    }

    private static void ValidateOptions(JsonContinuationStoreOptions options)
    {
        if (options.MaxFileSizeBytes <= 0 || options.MaxFileSizeBytes > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Maximum sidecar file size must be between 1 and Int32.MaxValue bytes.");
        }

        if (options.MaxJsonDepth <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Maximum JSON depth must be positive.");
        }

        if (options.MaxRules <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Maximum rule count must be positive.");
        }

        if (options.LockRetryAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Lock retry attempts must be positive.");
        }

        if (options.LockRetryDelayMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Lock retry delay cannot be negative.");
        }
    }
}
