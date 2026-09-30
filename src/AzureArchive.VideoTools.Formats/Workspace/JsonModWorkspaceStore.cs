using System.Text.Json;
using AzureArchive.VideoTools.Core.Results;
using AzureArchive.VideoTools.Core.Workspaces;

namespace AzureArchive.VideoTools.Formats.Workspace;

public sealed class JsonModWorkspaceStore : IModWorkspaceStore
{
    public const string ManifestFileName = "workspace.aavt.json";
    public const string BackupSuffix = ".bak";

    private static readonly HashSet<string> RootProperties = new(StringComparer.Ordinal)
    {
        "schemaVersion",
        "workspaceId",
        "createdWithPluginVersion",
        "minimumPluginVersion",
        "source",
        "requiredCapabilities"
    };

    private static readonly HashSet<string> SourceProperties = new(StringComparer.Ordinal)
    {
        "projectPathKey",
        "projectRevisionSha256",
        "playbackPathKey",
        "playbackRevisionSha256",
        "playbackSchemaName"
    };

    private readonly string _rootDirectory;
    private readonly string _rootPrefix;
    private readonly StringComparison _pathComparison;
    private readonly JsonModWorkspaceStoreOptions _options;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly IModWorkspaceCompatibilityGate _compatibilityGate;
    private readonly object _writeGate = new();

    public JsonModWorkspaceStore(
        string rootDirectory,
        JsonModWorkspaceStoreOptions? options = null,
        IModWorkspaceCompatibilityGate? compatibilityGate = null)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException(
                "Mod workspace root directory is empty.",
                nameof(rootDirectory));
        }

        _options = options ?? new JsonModWorkspaceStoreOptions();
        ValidateOptions(_options);
        _compatibilityGate = compatibilityGate
            ?? new ModWorkspaceCompatibilityGate();

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
            throw new ArgumentException(
                "Mod workspace root directory is invalid.",
                nameof(rootDirectory));
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

    public Result<ModWorkspaceLoadSnapshot> TryLoad(
        string projectPathKey,
        ModWorkspaceRuntimeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        Result contextValidation = ModWorkspaceCompatibilityGate.ValidateContext(context);
        if (!contextValidation.Success)
        {
            return Result<ModWorkspaceLoadSnapshot>.Fail(contextValidation.Error);
        }

        Result<string> pathResult = ResolveManifestPath(projectPathKey);
        if (!pathResult.Success || pathResult.Value == null)
        {
            return Result<ModWorkspaceLoadSnapshot>.Fail(pathResult.Error);
        }

        string primaryPath = pathResult.Value;
        string backupPath = GetBackupPath(primaryPath);

        try
        {
            bool primaryExists = File.Exists(primaryPath);
            bool backupExists = File.Exists(backupPath);
            if (!primaryExists && !backupExists)
            {
                return Result<ModWorkspaceLoadSnapshot>.Ok(new(
                    null,
                    DetectedSchemaVersion: 0,
                    ModWorkspaceLoadSource.None,
                    ModWorkspaceCompatibilitySnapshot.NotFound(),
                    Array.Empty<ModWorkspaceStoreNotice>()));
            }

            Result<InspectedManifest>? primary = null;
            if (primaryExists)
            {
                primary = InspectManifestFile(primaryPath, context);
                if (primary.Success && primary.Value != null)
                {
                    return Result<ModWorkspaceLoadSnapshot>.Ok(ToLoadSnapshot(
                        primary.Value,
                        ModWorkspaceLoadSource.Primary,
                        Array.Empty<ModWorkspaceStoreNotice>()));
                }
            }

            if (backupExists)
            {
                Result<InspectedManifest> backup = InspectManifestFile(
                    backupPath,
                    context);
                if (backup.Success && backup.Value != null)
                {
                    string code = primaryExists
                        ? "PrimaryUnreadable"
                        : "PrimaryMissing";
                    string message = primaryExists
                        ? $"The primary workspace manifest is unreadable; the previous atomic backup was loaded. Primary error: {primary?.Error}"
                        : "The primary workspace manifest is missing; the previous atomic backup was loaded.";
                    return Result<ModWorkspaceLoadSnapshot>.Ok(ToLoadSnapshot(
                        backup.Value,
                        ModWorkspaceLoadSource.Backup,
                        new[] { new ModWorkspaceStoreNotice(code, message) }));
                }

                string primaryError = primary?.Error
                    ?? "Primary workspace manifest is missing.";
                return Result<ModWorkspaceLoadSnapshot>.Fail(
                    $"Neither primary nor backup workspace manifest is readable. Primary: {primaryError} Backup: {backup.Error}");
            }

            return Result<ModWorkspaceLoadSnapshot>.Fail(
                $"Primary workspace manifest is unreadable and no backup exists: {primary?.Error}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Result<ModWorkspaceLoadSnapshot>.Fail(
                $"Workspace manifest cannot be accessed: {ex.Message}");
        }
        catch (IOException ex)
        {
            return Result<ModWorkspaceLoadSnapshot>.Fail(
                $"Workspace manifest could not be read: {ex.Message}");
        }
        catch (Exception ex)
        {
            return Result<ModWorkspaceLoadSnapshot>.Fail(
                $"Unexpected workspace read failure ({ex.GetType().Name}): {ex.Message}");
        }
    }

    public Result<ModWorkspaceSaveSnapshot> SaveAtomic(
        string projectPathKey,
        ModWorkspaceManifest manifest,
        ModWorkspaceRuntimeContext context)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(context);

        Result<string> pathResult = ResolveManifestPath(projectPathKey);
        if (!pathResult.Success || pathResult.Value == null)
        {
            return Result<ModWorkspaceSaveSnapshot>.Fail(pathResult.Error);
        }

        if (!string.Equals(
                projectPathKey,
                manifest.Source?.ProjectPathKey,
                StringComparison.OrdinalIgnoreCase))
        {
            return Result<ModWorkspaceSaveSnapshot>.Fail(
                "Workspace folder key must match the manifest project path key.");
        }

        Result<ModWorkspaceCompatibilitySnapshot> compatibility =
            _compatibilityGate.Evaluate(manifest, context);
        if (!compatibility.Success || compatibility.Value == null)
        {
            return Result<ModWorkspaceSaveSnapshot>.Fail(compatibility.Error);
        }

        if (!compatibility.Value.CanWrite)
        {
            return Result<ModWorkspaceSaveSnapshot>.Fail(
                $"Workspace is not writable: {compatibility.Value.Status}: {compatibility.Value.Message}");
        }

        if (manifest.RequiredCapabilities.Count > _options.MaxCapabilities)
        {
            return Result<ModWorkspaceSaveSnapshot>.Fail(
                $"Workspace has {manifest.RequiredCapabilities.Count} capabilities, exceeding the {_options.MaxCapabilities}-capability limit.");
        }

        byte[] bytes;
        try
        {
            bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, _serializerOptions);
        }
        catch (Exception ex)
        {
            return Result<ModWorkspaceSaveSnapshot>.Fail(
                $"Workspace manifest could not be serialized ({ex.GetType().Name}): {ex.Message}");
        }

        if (bytes.LongLength > _options.MaxFileSizeBytes)
        {
            return Result<ModWorkspaceSaveSnapshot>.Fail(
                $"Serialized workspace manifest is {bytes.LongLength} bytes, exceeding the {_options.MaxFileSizeBytes}-byte limit.");
        }

        lock (_writeGate)
        {
            return SaveAtomicCore(pathResult.Value, manifest, context, bytes);
        }
    }

    private Result<ModWorkspaceSaveSnapshot> SaveAtomicCore(
        string primaryPath,
        ModWorkspaceManifest manifest,
        ModWorkspaceRuntimeContext context,
        byte[] bytes)
    {
        string? directory = Path.GetDirectoryName(primaryPath);
        if (string.IsNullOrEmpty(directory))
        {
            return Result<ModWorkspaceSaveSnapshot>.Fail(
                "Workspace manifest path has no parent directory.");
        }

        string backupPath = GetBackupPath(primaryPath);
        string lockPath = primaryPath + ".lock";
        string temporaryPath = primaryPath + ".tmp." + Guid.NewGuid().ToString("N");

        try
        {
            Result existingPathSafety = EnsureNoReparsePoints(primaryPath);
            if (!existingPathSafety.Success)
            {
                return Result<ModWorkspaceSaveSnapshot>.Fail(existingPathSafety.Error);
            }

            Directory.CreateDirectory(directory);

            foreach (string path in new[] { primaryPath, backupPath, lockPath })
            {
                Result pathSafety = EnsureNoReparsePoints(path);
                if (!pathSafety.Success)
                {
                    return Result<ModWorkspaceSaveSnapshot>.Fail(pathSafety.Error);
                }
            }

            Result<FileStream> lockResult = AcquireWriteLock(lockPath);
            if (!lockResult.Success || lockResult.Value == null)
            {
                return Result<ModWorkspaceSaveSnapshot>.Fail(lockResult.Error);
            }

            using (lockResult.Value)
            {
                bool replacedExisting = File.Exists(primaryPath);
                if (replacedExisting)
                {
                    Result<InspectedManifest> existing = InspectManifestFile(
                        primaryPath,
                        context);
                    if (!existing.Success || existing.Value?.Manifest == null)
                    {
                        return Result<ModWorkspaceSaveSnapshot>.Fail(
                            "Existing workspace manifest is unreadable or uses an unsupported schema; refusing to overwrite it.");
                    }

                    if (existing.Value.Compatibility.Status
                            != ModWorkspaceCompatibilityStatus.Ready
                        || !string.Equals(
                            existing.Value.Manifest.WorkspaceId,
                            manifest.WorkspaceId,
                            StringComparison.Ordinal))
                    {
                        return Result<ModWorkspaceSaveSnapshot>.Fail(
                            "Existing workspace identity or compatibility state does not permit replacement.");
                    }
                }

                WriteTemporaryFile(temporaryPath, bytes);

                Result<InspectedManifest> temporary = InspectManifestFile(
                    temporaryPath,
                    context);
                if (!temporary.Success
                    || temporary.Value?.Manifest == null
                    || !ManifestsEqual(manifest, temporary.Value.Manifest)
                    || temporary.Value.Compatibility.Status
                        != ModWorkspaceCompatibilityStatus.Ready)
                {
                    string error = temporary.Success
                        ? "Temporary workspace content changed during verification."
                        : temporary.Error;
                    return Result<ModWorkspaceSaveSnapshot>.Fail(
                        $"Temporary workspace validation failed: {error}");
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

                Result<InspectedManifest> committed = InspectManifestFile(
                    primaryPath,
                    context);
                if (!committed.Success
                    || committed.Value?.Manifest == null
                    || !ManifestsEqual(manifest, committed.Value.Manifest)
                    || committed.Value.Compatibility.Status
                        != ModWorkspaceCompatibilityStatus.Ready)
                {
                    string error = committed.Success
                        ? "Committed workspace content does not match the requested manifest."
                        : committed.Error;
                    return Result<ModWorkspaceSaveSnapshot>.Fail(
                        $"Committed workspace validation failed: {error}");
                }

                return Result<ModWorkspaceSaveSnapshot>.Ok(new(
                    directory,
                    primaryPath,
                    replacedExisting,
                    replacedExisting && File.Exists(backupPath)));
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            return Result<ModWorkspaceSaveSnapshot>.Fail(
                $"Workspace manifest cannot be written: {ex.Message}");
        }
        catch (IOException ex)
        {
            return Result<ModWorkspaceSaveSnapshot>.Fail(
                $"Workspace atomic save failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            return Result<ModWorkspaceSaveSnapshot>.Fail(
                $"Unexpected workspace save failure ({ex.GetType().Name}): {ex.Message}");
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private Result<InspectedManifest> InspectManifestFile(
        string path,
        ModWorkspaceRuntimeContext context)
    {
        Result pathSafety = EnsureNoReparsePoints(path);
        if (!pathSafety.Success)
        {
            return Result<InspectedManifest>.Fail(pathSafety.Error);
        }

        Result<byte[]> bytesResult = ReadFile(path);
        if (!bytesResult.Success || bytesResult.Value == null)
        {
            return Result<InspectedManifest>.Fail(bytesResult.Error);
        }

        try
        {
            using JsonDocument json = JsonDocument.Parse(
                bytesResult.Value,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = _options.MaxJsonDepth
                });

            Result<int> schemaResult = ReadSchemaVersion(json.RootElement);
            if (!schemaResult.Success)
            {
                return Result<InspectedManifest>.Fail(schemaResult.Error);
            }

            int schemaVersion = schemaResult.Value;
            if (schemaVersion > ModWorkspaceManifest.CurrentSchemaVersion)
            {
                return Result<InspectedManifest>.Ok(new(
                    null,
                    schemaVersion,
                    Blocked(
                        ModWorkspaceCompatibilityStatus.FutureSchema,
                        $"Workspace schema {schemaVersion} is newer than supported schema {ModWorkspaceManifest.CurrentSchemaVersion}.")));
            }

            if (schemaVersion < ModWorkspaceManifest.OldestSupportedSchemaVersion)
            {
                return Result<InspectedManifest>.Ok(new(
                    null,
                    schemaVersion,
                    Blocked(
                        ModWorkspaceCompatibilityStatus.UnsupportedLegacySchema,
                        $"Workspace schema {schemaVersion} is older than the oldest supported schema {ModWorkspaceManifest.OldestSupportedSchemaVersion}.")));
            }

            Result shapeValidation = ValidateJsonShape(json.RootElement);
            if (!shapeValidation.Success)
            {
                return Result<InspectedManifest>.Fail(shapeValidation.Error);
            }

            ModWorkspaceManifest? manifest =
                json.RootElement.Deserialize<ModWorkspaceManifest>(
                    _serializerOptions);
            if (manifest == null)
            {
                return Result<InspectedManifest>.Fail(
                    "Workspace JSON did not produce a manifest.");
            }

            if (manifest.RequiredCapabilities == null)
            {
                return Result<InspectedManifest>.Fail(
                    "Workspace required capabilities collection is missing.");
            }

            if (manifest.RequiredCapabilities.Count > _options.MaxCapabilities)
            {
                return Result<InspectedManifest>.Fail(
                    $"Workspace has {manifest.RequiredCapabilities.Count} capabilities, exceeding the {_options.MaxCapabilities}-capability limit.");
            }

            Result<ModWorkspaceCompatibilitySnapshot> compatibility =
                _compatibilityGate.Evaluate(manifest, context);
            return compatibility.Success && compatibility.Value != null
                ? Result<InspectedManifest>.Ok(new(
                    manifest,
                    schemaVersion,
                    compatibility.Value))
                : Result<InspectedManifest>.Fail(compatibility.Error);
        }
        catch (JsonException ex)
        {
            return Result<InspectedManifest>.Fail(
                $"Workspace JSON is invalid: {ex.Message}");
        }
        catch (Exception ex)
        {
            return Result<InspectedManifest>.Fail(
                $"Unexpected workspace parse failure ({ex.GetType().Name}): {ex.Message}");
        }
    }

    private Result<byte[]> ReadFile(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            if (stream.Length <= 0)
            {
                return Result<byte[]>.Fail("Workspace manifest is empty.");
            }

            if (stream.Length > _options.MaxFileSizeBytes)
            {
                return Result<byte[]>.Fail(
                    $"Workspace manifest is {stream.Length} bytes, exceeding the {_options.MaxFileSizeBytes}-byte limit.");
            }

            byte[] bytes = new byte[(int)stream.Length];
            int offset = 0;
            while (offset < bytes.Length)
            {
                int read = stream.Read(bytes, offset, bytes.Length - offset);
                if (read == 0)
                {
                    return Result<byte[]>.Fail(
                        "Workspace manifest ended before its declared length.");
                }

                offset += read;
            }

            return Result<byte[]>.Ok(bytes);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Result<byte[]>.Fail(
                $"Workspace manifest cannot be accessed: {ex.Message}");
        }
        catch (IOException ex)
        {
            return Result<byte[]>.Fail(
                $"Workspace manifest could not be read: {ex.Message}");
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
            $"Workspace write lock could not be acquired after {_options.LockRetryAttempts} attempts: {lastError?.Message}");
    }

    private static void WriteTemporaryFile(string path, byte[] bytes)
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

    private Result<string> ResolveManifestPath(string projectPathKey)
    {
        if (!IsSha256(projectPathKey))
        {
            return Result<string>.Fail(
                "Workspace project key must be a 64-character hexadecimal SHA-256 value.");
        }

        try
        {
            string workspaceDirectory = Path.GetFullPath(Path.Combine(
                _rootDirectory,
                projectPathKey.ToUpperInvariant()));
            if (!workspaceDirectory.StartsWith(_rootPrefix, _pathComparison))
            {
                return Result<string>.Fail(
                    "Workspace path resolves outside the configured root.");
            }

            return Result<string>.Ok(Path.Combine(
                workspaceDirectory,
                ManifestFileName));
        }
        catch (Exception ex) when (
            ex is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return Result<string>.Fail($"Workspace path is invalid: {ex.Message}");
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
                        $"Workspace path contains a reparse-point directory: {current}");
                }
            }
        }

        if ((File.Exists(targetPath) || Directory.Exists(targetPath))
            && IsReparsePoint(targetPath))
        {
            return Result.Fail(
                $"Workspace target is a reparse point: {targetPath}");
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
                     new[]
                     {
                         Path.DirectorySeparatorChar,
                         Path.AltDirectorySeparatorChar
                     },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            yield return current;
        }
    }

    private static Result<int> ReadSchemaVersion(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Result<int>.Fail(
                "Workspace manifest root must be a JSON object.");
        }

        int count = 0;
        int schemaVersion = 0;
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!string.Equals(
                    property.Name,
                    "schemaVersion",
                    StringComparison.Ordinal))
            {
                continue;
            }

            count++;
            if (property.Value.ValueKind != JsonValueKind.Number
                || !property.Value.TryGetInt32(out schemaVersion)
                || schemaVersion <= 0)
            {
                return Result<int>.Fail(
                    "Workspace schemaVersion must be a positive Int32 value.");
            }
        }

        return count switch
        {
            1 => Result<int>.Ok(schemaVersion),
            0 => Result<int>.Fail("Workspace schemaVersion is missing."),
            _ => Result<int>.Fail("Workspace schemaVersion is duplicated.")
        };
    }

    private static Result ValidateJsonShape(JsonElement root)
    {
        Result rootResult = ValidateObjectProperties(
            root,
            RootProperties,
            "workspace root");
        if (!rootResult.Success)
        {
            return rootResult;
        }

        if (!root.TryGetProperty("source", out JsonElement source)
            || source.ValueKind != JsonValueKind.Object)
        {
            return Result.Fail("Workspace source must be a JSON object.");
        }

        Result sourceResult = ValidateObjectProperties(
            source,
            SourceProperties,
            "workspace source");
        if (!sourceResult.Success)
        {
            return sourceResult;
        }

        if (!root.TryGetProperty(
                "requiredCapabilities",
                out JsonElement capabilities)
            || capabilities.ValueKind != JsonValueKind.Array)
        {
            return Result.Fail(
                "Workspace requiredCapabilities must be a JSON array.");
        }

        int index = 0;
        foreach (JsonElement capability in capabilities.EnumerateArray())
        {
            if (capability.ValueKind != JsonValueKind.String)
            {
                return Result.Fail(
                    $"Workspace capability {index} must be a JSON string.");
            }

            index++;
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

    private static ModWorkspaceLoadSnapshot ToLoadSnapshot(
        InspectedManifest inspected,
        ModWorkspaceLoadSource source,
        IReadOnlyList<ModWorkspaceStoreNotice> notices) => new(
        inspected.Manifest,
        inspected.SchemaVersion,
        source,
        inspected.Compatibility,
        notices);

    private static ModWorkspaceCompatibilitySnapshot Blocked(
        ModWorkspaceCompatibilityStatus status,
        string message) => new(
        status,
        CanReadMetadata: true,
        CanWrite: false,
        CanExecute: false,
        message,
        Array.Empty<string>());

    private static bool ManifestsEqual(
        ModWorkspaceManifest expected,
        ModWorkspaceManifest actual)
    {
        if (expected.SchemaVersion != actual.SchemaVersion
            || !string.Equals(
                expected.WorkspaceId,
                actual.WorkspaceId,
                StringComparison.Ordinal)
            || !string.Equals(
                expected.CreatedWithPluginVersion,
                actual.CreatedWithPluginVersion,
                StringComparison.Ordinal)
            || !string.Equals(
                expected.MinimumPluginVersion,
                actual.MinimumPluginVersion,
                StringComparison.Ordinal)
            || expected.Source != actual.Source
            || expected.RequiredCapabilities.Count
                != actual.RequiredCapabilities.Count)
        {
            return false;
        }

        for (int index = 0; index < expected.RequiredCapabilities.Count; index++)
        {
            if (!string.Equals(
                    expected.RequiredCapabilities[index],
                    actual.RequiredCapabilities[index],
                    StringComparison.Ordinal))
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
            // Temporary files are never considered by load or future saves.
        }
    }

    private static void ValidateOptions(JsonModWorkspaceStoreOptions options)
    {
        if (options.MaxFileSizeBytes <= 0
            || options.MaxFileSizeBytes > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Maximum workspace file size must be between 1 and Int32.MaxValue bytes.");
        }

        if (options.MaxJsonDepth <= 0
            || options.MaxCapabilities <= 0
            || options.LockRetryAttempts <= 0
            || options.LockRetryDelayMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Workspace store limits and retry settings are invalid.");
        }
    }

    private sealed record InspectedManifest(
        ModWorkspaceManifest? Manifest,
        int SchemaVersion,
        ModWorkspaceCompatibilitySnapshot Compatibility);
}
