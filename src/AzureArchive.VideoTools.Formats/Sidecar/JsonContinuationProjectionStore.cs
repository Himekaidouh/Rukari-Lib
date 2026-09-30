using System.Text;
using System.Text.Json;
using AzureArchive.VideoTools.Core.Compilation;
using AzureArchive.VideoTools.Core.Continuity;
using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Formats.Sidecar;

public sealed class JsonContinuationProjectionStore : IContinuationProjectionStore
{
    public const string SidecarExtension = ".aavt.playback.json";
    public const string BackupSuffix = ".bak";

    private static readonly HashSet<string> RootProperties = new(StringComparer.Ordinal)
    {
        "schemaVersion",
        "projectId",
        "projectPathKey",
        "projectRevisionSha256",
        "playbackPathKey",
        "playbackRevisionSha256",
        "playbackSchemaName",
        "instructions"
    };

    private static readonly HashSet<string> InstructionProperties = new(StringComparer.Ordinal)
    {
        "scene",
        "previousScene",
        "chainStartScene",
        "playbackRecordIndex",
        "playbackRecordFingerprint",
        "previousPlaybackRecordIndex",
        "chainStartPlaybackRecordIndex",
        "expectedLockedPrefix",
        "suffix",
        "resultText",
        "typewriterStartCharacterIndex"
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
    private readonly JsonContinuationProjectionStoreOptions _options;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly IContinuationProjectionCompiler _projectionCompiler;
    private readonly object _writeGate = new();

    public JsonContinuationProjectionStore(
        string rootDirectory,
        JsonContinuationProjectionStoreOptions? options = null,
        IContinuationProjectionCompiler? projectionCompiler = null)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException(
                "Projection sidecar root directory is empty.",
                nameof(rootDirectory));
        }

        _options = options ?? new JsonContinuationProjectionStoreOptions();
        ValidateOptions(_options);
        _projectionCompiler = projectionCompiler
            ?? new ContinuationProjectionCompiler();

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
                "Projection sidecar root directory is invalid.",
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

    public Result<ContinuationProjectionLoadSnapshot> TryLoad(
        string sidecarPath,
        ProjectSnapshot currentProject,
        PlaybackArchiveSnapshot currentPlayback)
    {
        ArgumentNullException.ThrowIfNull(currentProject);
        ArgumentNullException.ThrowIfNull(currentPlayback);

        Result currentValidation = ValidateCurrentSnapshots(
            currentProject,
            currentPlayback);
        if (!currentValidation.Success)
        {
            return Result<ContinuationProjectionLoadSnapshot>.Fail(
                currentValidation.Error);
        }

        Result<string> pathResult = ResolveSidecarPath(sidecarPath);
        if (!pathResult.Success || pathResult.Value == null)
        {
            return Result<ContinuationProjectionLoadSnapshot>.Fail(pathResult.Error);
        }

        string primaryPath = pathResult.Value;
        string backupPath = GetBackupPath(primaryPath);

        try
        {
            bool primaryExists = File.Exists(primaryPath);
            bool backupExists = File.Exists(backupPath);
            if (!primaryExists && !backupExists)
            {
                return Result<ContinuationProjectionLoadSnapshot>.Ok(
                    new ContinuationProjectionLoadSnapshot(
                        null,
                        ContinuationProjectionLoadSource.None,
                        Array.Empty<ContinuationProjectionStoreNotice>()));
            }

            Result<ContinuationPlaybackProjection>? primaryResult = null;
            if (primaryExists)
            {
                primaryResult = ReadSafeCurrentProjection(
                    primaryPath,
                    currentProject,
                    currentPlayback);
                if (primaryResult.Success && primaryResult.Value != null)
                {
                    return Result<ContinuationProjectionLoadSnapshot>.Ok(
                        new ContinuationProjectionLoadSnapshot(
                            primaryResult.Value,
                            ContinuationProjectionLoadSource.Primary,
                            Array.Empty<ContinuationProjectionStoreNotice>()));
                }
            }

            if (backupExists)
            {
                Result<ContinuationPlaybackProjection> backupResult =
                    ReadSafeCurrentProjection(
                        backupPath,
                        currentProject,
                        currentPlayback);
                if (backupResult.Success && backupResult.Value != null)
                {
                    string code = primaryExists
                        ? "PrimaryRejected"
                        : "PrimaryMissing";
                    string message = primaryExists
                        ? "The primary projection was unreadable, malformed, or stale; the previous validated backup was loaded."
                        : "The primary projection is missing; the previous validated backup was loaded.";
                    return Result<ContinuationProjectionLoadSnapshot>.Ok(
                        new ContinuationProjectionLoadSnapshot(
                            backupResult.Value,
                            ContinuationProjectionLoadSource.Backup,
                            new[]
                            {
                                new ContinuationProjectionStoreNotice(code, message)
                            }));
                }

                string primaryError = primaryResult?.Error
                    ?? "Primary projection is missing.";
                return Result<ContinuationProjectionLoadSnapshot>.Fail(
                    "Neither primary nor backup projection is valid for the current AAP/AAS snapshots. "
                    + $"Primary: {primaryError} Backup: {backupResult.Error}");
            }

            return Result<ContinuationProjectionLoadSnapshot>.Fail(
                "Primary projection is invalid for the current AAP/AAS snapshots and no backup exists: "
                + primaryResult?.Error);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Result<ContinuationProjectionLoadSnapshot>.Fail(
                $"Projection sidecar cannot be accessed: {ex.Message}");
        }
        catch (IOException ex)
        {
            return Result<ContinuationProjectionLoadSnapshot>.Fail(
                $"Projection sidecar could not be read: {ex.Message}");
        }
        catch (Exception ex)
        {
            return Result<ContinuationProjectionLoadSnapshot>.Fail(
                $"Unexpected projection sidecar read failure ({ex.GetType().Name}): {ex.Message}");
        }
    }

    public Result<ContinuationProjectionSaveSnapshot> SaveAtomic(
        string sidecarPath,
        ContinuationPlaybackProjection projection,
        ProjectSnapshot currentProject,
        PlaybackArchiveSnapshot currentPlayback)
    {
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(currentProject);
        ArgumentNullException.ThrowIfNull(currentPlayback);

        Result<string> pathResult = ResolveSidecarPath(sidecarPath);
        if (!pathResult.Success || pathResult.Value == null)
        {
            return Result<ContinuationProjectionSaveSnapshot>.Fail(pathResult.Error);
        }

        Result currentValidation = ValidateCurrentSnapshots(
            currentProject,
            currentPlayback);
        if (!currentValidation.Success)
        {
            return Result<ContinuationProjectionSaveSnapshot>.Fail(
                currentValidation.Error);
        }

        Result projectionValidation = ValidateProjection(projection);
        if (!projectionValidation.Success)
        {
            return Result<ContinuationProjectionSaveSnapshot>.Fail(
                projectionValidation.Error);
        }

        Result bindingValidation = ValidateAgainstCurrent(
            projection,
            currentProject,
            currentPlayback);
        if (!bindingValidation.Success)
        {
            return Result<ContinuationProjectionSaveSnapshot>.Fail(
                bindingValidation.Error);
        }

        byte[] bytes;
        try
        {
            bytes = JsonSerializer.SerializeToUtf8Bytes(
                projection,
                _serializerOptions);
        }
        catch (Exception ex)
        {
            return Result<ContinuationProjectionSaveSnapshot>.Fail(
                $"Projection could not be serialized ({ex.GetType().Name}): {ex.Message}");
        }

        if (bytes.LongLength > _options.MaxFileSizeBytes)
        {
            return Result<ContinuationProjectionSaveSnapshot>.Fail(
                $"Serialized projection is {bytes.LongLength} bytes, exceeding the {_options.MaxFileSizeBytes}-byte limit.");
        }

        lock (_writeGate)
        {
            return SaveAtomicCore(
                pathResult.Value,
                projection,
                currentProject,
                currentPlayback,
                bytes);
        }
    }

    private Result<ContinuationProjectionSaveSnapshot> SaveAtomicCore(
        string primaryPath,
        ContinuationPlaybackProjection projection,
        ProjectSnapshot currentProject,
        PlaybackArchiveSnapshot currentPlayback,
        byte[] bytes)
    {
        string? directory = Path.GetDirectoryName(primaryPath);
        if (string.IsNullOrEmpty(directory))
        {
            return Result<ContinuationProjectionSaveSnapshot>.Fail(
                "Projection sidecar path has no parent directory.");
        }

        string backupPath = GetBackupPath(primaryPath);
        string lockPath = primaryPath + ".lock";
        string temporaryPath = primaryPath + ".tmp." + Guid.NewGuid().ToString("N");

        try
        {
            Result existingPathSafety = EnsureNoReparsePoints(primaryPath);
            if (!existingPathSafety.Success)
            {
                return Result<ContinuationProjectionSaveSnapshot>.Fail(
                    existingPathSafety.Error);
            }

            Directory.CreateDirectory(directory);

            Result createdPathSafety = EnsureNoReparsePoints(primaryPath);
            if (!createdPathSafety.Success)
            {
                return Result<ContinuationProjectionSaveSnapshot>.Fail(
                    createdPathSafety.Error);
            }

            Result backupSafety = EnsureNoReparsePoints(backupPath);
            if (!backupSafety.Success)
            {
                return Result<ContinuationProjectionSaveSnapshot>.Fail(
                    backupSafety.Error);
            }

            Result lockSafety = EnsureNoReparsePoints(lockPath);
            if (!lockSafety.Success)
            {
                return Result<ContinuationProjectionSaveSnapshot>.Fail(
                    lockSafety.Error);
            }

            Result<FileStream> lockResult = AcquireWriteLock(lockPath);
            if (!lockResult.Success || lockResult.Value == null)
            {
                return Result<ContinuationProjectionSaveSnapshot>.Fail(
                    lockResult.Error);
            }

            using (lockResult.Value)
            {
                bool replacedExisting = File.Exists(primaryPath);
                WriteTemporaryFile(temporaryPath, bytes);

                Result<ContinuationPlaybackProjection> temporaryResult =
                    ReadProjectionFile(temporaryPath);
                if (!temporaryResult.Success
                    || temporaryResult.Value == null
                    || !ProjectionsEqual(projection, temporaryResult.Value))
                {
                    string error = temporaryResult.Success
                        ? "Temporary projection content changed during verification."
                        : temporaryResult.Error;
                    return Result<ContinuationProjectionSaveSnapshot>.Fail(
                        $"Temporary projection validation failed: {error}");
                }

                Result temporaryBinding = ValidateAgainstCurrent(
                    temporaryResult.Value,
                    currentProject,
                    currentPlayback);
                if (!temporaryBinding.Success)
                {
                    return Result<ContinuationProjectionSaveSnapshot>.Fail(
                        $"Temporary projection binding validation failed: {temporaryBinding.Error}");
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

                Result<ContinuationPlaybackProjection> committedResult =
                    ReadProjectionFile(primaryPath);
                if (!committedResult.Success
                    || committedResult.Value == null
                    || !ProjectionsEqual(projection, committedResult.Value))
                {
                    string error = committedResult.Success
                        ? "Committed projection does not match the requested document."
                        : committedResult.Error;
                    return Result<ContinuationProjectionSaveSnapshot>.Fail(
                        $"Committed projection validation failed: {error}");
                }

                Result committedBinding = ValidateAgainstCurrent(
                    committedResult.Value,
                    currentProject,
                    currentPlayback);
                if (!committedBinding.Success)
                {
                    return Result<ContinuationProjectionSaveSnapshot>.Fail(
                        $"Committed projection binding validation failed: {committedBinding.Error}");
                }

                return Result<ContinuationProjectionSaveSnapshot>.Ok(
                    new ContinuationProjectionSaveSnapshot(
                        primaryPath,
                        replacedExisting,
                        replacedExisting && File.Exists(backupPath)));
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            return Result<ContinuationProjectionSaveSnapshot>.Fail(
                $"Projection sidecar cannot be written: {ex.Message}");
        }
        catch (IOException ex)
        {
            return Result<ContinuationProjectionSaveSnapshot>.Fail(
                $"Projection atomic save failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            return Result<ContinuationProjectionSaveSnapshot>.Fail(
                $"Unexpected projection save failure ({ex.GetType().Name}): {ex.Message}");
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private Result<ContinuationPlaybackProjection> ReadSafeCurrentProjection(
        string path,
        ProjectSnapshot currentProject,
        PlaybackArchiveSnapshot currentPlayback)
    {
        Result pathSafety = EnsureNoReparsePoints(path);
        if (!pathSafety.Success)
        {
            return Result<ContinuationPlaybackProjection>.Fail(pathSafety.Error);
        }

        Result<ContinuationPlaybackProjection> readResult = ReadProjectionFile(path);
        if (!readResult.Success || readResult.Value == null)
        {
            return Result<ContinuationPlaybackProjection>.Fail(readResult.Error);
        }

        Result bindingValidation = ValidateAgainstCurrent(
            readResult.Value,
            currentProject,
            currentPlayback);
        return bindingValidation.Success
            ? readResult
            : Result<ContinuationPlaybackProjection>.Fail(bindingValidation.Error);
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
            $"Projection write lock could not be acquired after {_options.LockRetryAttempts} attempts: {lastError?.Message}");
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

    private Result<ContinuationPlaybackProjection> ReadProjectionFile(string path)
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
                    return Result<ContinuationPlaybackProjection>.Fail(
                        "Projection sidecar is empty.");
                }

                if (stream.Length > _options.MaxFileSizeBytes)
                {
                    return Result<ContinuationPlaybackProjection>.Fail(
                        $"Projection sidecar is {stream.Length} bytes, exceeding the {_options.MaxFileSizeBytes}-byte limit.");
                }

                bytes = new byte[(int)stream.Length];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = stream.Read(bytes, offset, bytes.Length - offset);
                    if (read == 0)
                    {
                        return Result<ContinuationPlaybackProjection>.Fail(
                            "Projection sidecar ended before its declared length.");
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
                return Result<ContinuationPlaybackProjection>.Fail(
                    shapeValidation.Error);
            }

            ContinuationPlaybackProjection? projection =
                json.RootElement.Deserialize<ContinuationPlaybackProjection>(
                    _serializerOptions);
            if (projection == null)
            {
                return Result<ContinuationPlaybackProjection>.Fail(
                    "Projection JSON did not produce a projection document.");
            }

            Result validation = ValidateProjection(projection);
            return validation.Success
                ? Result<ContinuationPlaybackProjection>.Ok(projection)
                : Result<ContinuationPlaybackProjection>.Fail(validation.Error);
        }
        catch (JsonException ex)
        {
            return Result<ContinuationPlaybackProjection>.Fail(
                $"Projection JSON is invalid: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Result<ContinuationPlaybackProjection>.Fail(
                $"Projection file cannot be accessed: {ex.Message}");
        }
        catch (IOException ex)
        {
            return Result<ContinuationPlaybackProjection>.Fail(
                $"Projection file could not be read: {ex.Message}");
        }
        catch (Exception ex)
        {
            return Result<ContinuationPlaybackProjection>.Fail(
                $"Unexpected projection parse failure ({ex.GetType().Name}): {ex.Message}");
        }
    }

    private Result ValidateProjection(ContinuationPlaybackProjection projection)
    {
        if (projection.SchemaVersion
            != ContinuationPlaybackProjection.CurrentSchemaVersion)
        {
            return Result.Fail(
                $"Unsupported projection schema version {projection.SchemaVersion}; expected {ContinuationPlaybackProjection.CurrentSchemaVersion}.");
        }

        if (!Guid.TryParseExact(projection.ProjectId, "D", out _))
        {
            return Result.Fail("Projection project ID must be a canonical UUID.");
        }

        if (!IsSha256(projection.ProjectPathKey)
            || !IsSha256(projection.ProjectRevisionSha256)
            || !IsSha256(projection.PlaybackPathKey)
            || !IsSha256(projection.PlaybackRevisionSha256))
        {
            return Result.Fail(
                "Projection path keys and revisions must be 64-character hexadecimal SHA-256 values.");
        }

        if (string.IsNullOrWhiteSpace(projection.PlaybackSchemaName)
            || projection.PlaybackSchemaName.Length
                > _options.MaxSchemaNameCharacters)
        {
            return Result.Fail("Projection playback schema name is invalid or too long.");
        }

        if (projection.Instructions == null)
        {
            return Result.Fail("Projection instruction collection is missing.");
        }

        if (projection.Instructions.Count > _options.MaxInstructions)
        {
            return Result.Fail(
                $"Projection has {projection.Instructions.Count} instructions, exceeding the {_options.MaxInstructions}-instruction limit.");
        }

        var sceneLocations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var playbackRecords = new HashSet<int>();
        int previousOrderedRecord = -1;
        for (int index = 0; index < projection.Instructions.Count; index++)
        {
            ContinuationPlaybackInstruction? instruction =
                projection.Instructions[index];
            Result instructionValidation = ValidateInstruction(instruction, index);
            if (!instructionValidation.Success)
            {
                return instructionValidation;
            }

            string sceneLocation = Location(instruction.Scene);
            if (!sceneLocations.Add(sceneLocation))
            {
                return Result.Fail(
                    $"Projection contains duplicate instructions for {sceneLocation}.");
            }

            if (!playbackRecords.Add(instruction.PlaybackRecordIndex))
            {
                return Result.Fail(
                    $"Projection contains duplicate playback record {instruction.PlaybackRecordIndex}.");
            }

            if (instruction.PlaybackRecordIndex <= previousOrderedRecord)
            {
                return Result.Fail(
                    "Projection instructions must be strictly ordered by playback record index.");
            }

            previousOrderedRecord = instruction.PlaybackRecordIndex;
        }

        return Result.Ok();
    }

    private Result ValidateInstruction(
        ContinuationPlaybackInstruction? instruction,
        int index)
    {
        if (instruction?.Scene == null
            || instruction.PreviousScene == null
            || instruction.ChainStartScene == null)
        {
            return Result.Fail($"Projection instruction {index} has missing scene keys.");
        }

        if (!IsValidSceneKey(instruction.Scene, requireAfterFirst: true)
            || !IsValidSceneKey(instruction.PreviousScene, requireAfterFirst: false)
            || !IsValidSceneKey(instruction.ChainStartScene, requireAfterFirst: false))
        {
            return Result.Fail(
                $"Projection instruction {index} has an invalid scene identity.");
        }

        if (!string.Equals(
                instruction.Scene.NodeGuid,
                instruction.PreviousScene.NodeGuid,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                instruction.Scene.NodeGuid,
                instruction.ChainStartScene.NodeGuid,
                StringComparison.OrdinalIgnoreCase)
            || instruction.PreviousScene.SceneIndex
                != instruction.Scene.SceneIndex - 1
            || instruction.ChainStartScene.SceneIndex
                > instruction.PreviousScene.SceneIndex)
        {
            return Result.Fail(
                $"Projection instruction {index} has inconsistent source chain locations.");
        }

        if (instruction.PlaybackRecordIndex <= 0
            || instruction.PreviousPlaybackRecordIndex
                != instruction.PlaybackRecordIndex - 1
            || instruction.ChainStartPlaybackRecordIndex < 0
            || instruction.ChainStartPlaybackRecordIndex
                > instruction.PreviousPlaybackRecordIndex
            || !IsSha256(instruction.PlaybackRecordFingerprint))
        {
            return Result.Fail(
                $"Projection instruction {index} has inconsistent playback record identities.");
        }

        if (!IsBoundedText(instruction.ExpectedLockedPrefix)
            || !IsBoundedText(instruction.Suffix)
            || !IsBoundedText(instruction.ResultText))
        {
            return Result.Fail(
                $"Projection instruction {index} has missing or oversized dialogue text.");
        }

        if (instruction.TypewriterStartCharacterIndex
                != instruction.ExpectedLockedPrefix.Length
            || instruction.ResultText.Length
                != instruction.ExpectedLockedPrefix.Length + instruction.Suffix.Length
            || !instruction.ResultText.AsSpan(
                    0,
                    instruction.ExpectedLockedPrefix.Length)
                .SequenceEqual(instruction.ExpectedLockedPrefix.AsSpan())
            || !instruction.ResultText.AsSpan(
                    instruction.ExpectedLockedPrefix.Length)
                .SequenceEqual(instruction.Suffix.AsSpan()))
        {
            return Result.Fail(
                $"Projection instruction {index} has inconsistent dialogue append semantics.");
        }

        return Result.Ok();
    }

    private Result ValidateAgainstCurrent(
        ContinuationPlaybackProjection projection,
        ProjectSnapshot currentProject,
        PlaybackArchiveSnapshot currentPlayback)
    {
        if (!string.Equals(
                projection.ProjectPathKey,
                currentProject.Source.PathKey,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                projection.ProjectRevisionSha256,
                currentProject.Source.RevisionSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return Result.Fail(
                "Projection does not match the exact current AAP path and revision.");
        }

        if (!string.Equals(
                projection.PlaybackPathKey,
                currentPlayback.Source.PathKey,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                projection.PlaybackRevisionSha256,
                currentPlayback.Source.RevisionSha256,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                projection.PlaybackSchemaName,
                currentPlayback.SchemaName,
                StringComparison.Ordinal))
        {
            return Result.Fail(
                "Projection does not match the exact current AAS path, revision, and schema.");
        }

        var scenesByLocation = new Dictionary<string, SceneSnapshot>(
            StringComparer.OrdinalIgnoreCase);
        foreach (StoryNodeSnapshot node in currentProject.ScriptNodes)
        {
            foreach (SceneSnapshot scene in node.Scenes)
            {
                if (!scenesByLocation.TryAdd(Location(scene.Key), scene))
                {
                    return Result.Fail(
                        $"Current project contains duplicate scene location {Location(scene.Key)}.");
                }
            }
        }

        Dictionary<string, ContinuationPlaybackInstruction> instructionsByScene =
            projection.Instructions.ToDictionary(
                instruction => Location(instruction.Scene),
                StringComparer.OrdinalIgnoreCase);
        foreach (ContinuationPlaybackInstruction instruction in projection.Instructions)
        {
            Result chainValidation = ValidateInstructionAgainstCurrent(
                instruction,
                scenesByLocation,
                instructionsByScene,
                currentPlayback);
            if (!chainValidation.Success)
            {
                return chainValidation;
            }
        }

        ContinuationRule[] reconstructedRules = projection.Instructions
            .Select(instruction => new ContinuationRule(instruction.Scene))
            .ToArray();
        var reconstructedMetadata = new ContinuationDocument(
            ContinuationDocument.CurrentSchemaVersion,
            projection.ProjectId,
            projection.ProjectPathKey,
            projection.ProjectRevisionSha256,
            Array.AsReadOnly(reconstructedRules));
        Result<ContinuationPlaybackProjection> rebuiltResult =
            _projectionCompiler.Compile(
                currentProject,
                currentPlayback,
                reconstructedMetadata);
        if (!rebuiltResult.Success
            || rebuiltResult.Value == null
            || !ProjectionsEqual(projection, rebuiltResult.Value))
        {
            string reason = rebuiltResult.Success
                ? "the rebuilt instructions differ"
                : rebuiltResult.Error;
            return Result.Fail(
                "Projection cannot be reproduced by the trusted compiler: "
                + reason);
        }

        return Result.Ok();
    }

    private Result ValidateInstructionAgainstCurrent(
        ContinuationPlaybackInstruction instruction,
        IReadOnlyDictionary<string, SceneSnapshot> scenesByLocation,
        IReadOnlyDictionary<string, ContinuationPlaybackInstruction> instructionsByScene,
        PlaybackArchiveSnapshot currentPlayback)
    {
        if (!TryGetExactScene(
                scenesByLocation,
                instruction.Scene,
                out SceneSnapshot? currentScene)
            || !TryGetExactScene(
                scenesByLocation,
                instruction.PreviousScene,
                out _)
            || !TryGetExactScene(
                scenesByLocation,
                instruction.ChainStartScene,
                out _))
        {
            return Result.Fail(
                $"Projection instruction {Location(instruction.Scene)} contains a stale source scene key.");
        }

        var visibleText = new StringBuilder();
        string nodeGuid = instruction.Scene.NodeGuid;
        for (int sceneIndex = instruction.ChainStartScene.SceneIndex;
             sceneIndex <= instruction.Scene.SceneIndex;
             sceneIndex++)
        {
            string sceneLocation = Location(nodeGuid, sceneIndex);
            if (!scenesByLocation.TryGetValue(
                    sceneLocation,
                    out SceneSnapshot? sourceScene))
            {
                return Result.Fail(
                    $"Projection instruction {Location(instruction.Scene)} is missing current source chain scene {sceneIndex}.");
            }

            int recordIndex = instruction.ChainStartPlaybackRecordIndex
                + sceneIndex
                - instruction.ChainStartScene.SceneIndex;
            if (recordIndex < 0 || recordIndex >= currentPlayback.Records.Count)
            {
                return Result.Fail(
                    $"Projection instruction {Location(instruction.Scene)} points outside the current AAS chain.");
            }

            PlaybackRecordSnapshot record = currentPlayback.Records[recordIndex];
            if (record.RecordIndex != recordIndex
                || !string.Equals(
                    record.TextJp,
                    sourceScene.DialogueText,
                    StringComparison.Ordinal))
            {
                return Result.Fail(
                    $"Projection instruction {Location(instruction.Scene)} no longer matches AAP/AAS text at source scene {sceneIndex}.");
            }

            if (sceneIndex < instruction.Scene.SceneIndex)
            {
                visibleText.Append(sourceScene.DialogueText);
            }

            if (sceneIndex > instruction.ChainStartScene.SceneIndex)
            {
                if (!instructionsByScene.TryGetValue(
                        sceneLocation,
                        out ContinuationPlaybackInstruction? chainInstruction)
                    || chainInstruction.PlaybackRecordIndex != recordIndex
                    || !SceneKeysEqual(
                        chainInstruction.ChainStartScene,
                        instruction.ChainStartScene))
                {
                    return Result.Fail(
                        $"Projection instruction {Location(instruction.Scene)} has an incomplete intermediate append chain at source scene {sceneIndex}.");
                }
            }
        }

        if (instruction.PlaybackRecordIndex
                != instruction.ChainStartPlaybackRecordIndex
                    + instruction.Scene.SceneIndex
                    - instruction.ChainStartScene.SceneIndex
            || instruction.PreviousPlaybackRecordIndex
                != instruction.PlaybackRecordIndex - 1)
        {
            return Result.Fail(
                $"Projection instruction {Location(instruction.Scene)} has inconsistent current chain offsets.");
        }

        PlaybackRecordSnapshot currentRecord =
            currentPlayback.Records[instruction.PlaybackRecordIndex];
        if (!string.Equals(
                currentRecord.Fingerprint,
                instruction.PlaybackRecordFingerprint,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                currentScene!.DialogueText,
                instruction.Suffix,
                StringComparison.Ordinal)
            || !string.Equals(
                visibleText.ToString(),
                instruction.ExpectedLockedPrefix,
                StringComparison.Ordinal))
        {
            return Result.Fail(
                $"Projection instruction {Location(instruction.Scene)} no longer matches its current record fingerprint or dialogue text.");
        }

        return Result.Ok();
    }

    private static bool TryGetExactScene(
        IReadOnlyDictionary<string, SceneSnapshot> scenesByLocation,
        SceneKey expected,
        out SceneSnapshot? scene)
    {
        if (scenesByLocation.TryGetValue(Location(expected), out scene)
            && SceneKeysEqual(scene.Key, expected))
        {
            return true;
        }

        scene = null;
        return false;
    }

    private static Result ValidateCurrentSnapshots(
        ProjectSnapshot project,
        PlaybackArchiveSnapshot playback)
    {
        if (project.Source == null
            || !IsSha256(project.Source.PathKey)
            || !IsSha256(project.Source.RevisionSha256)
            || project.Nodes == null)
        {
            return Result.Fail("Current AAP snapshot has an invalid identity or node collection.");
        }

        for (int nodeIndex = 0; nodeIndex < project.Nodes.Count; nodeIndex++)
        {
            StoryNodeSnapshot? node = project.Nodes[nodeIndex];
            if (node == null)
            {
                return Result.Fail($"Current AAP node {nodeIndex} is null.");
            }

            if (node.Kind != StoryNodeKind.Script)
            {
                continue;
            }

            if (node.Scenes == null)
            {
                return Result.Fail(
                    $"Current AAP script node {nodeIndex} has no scenes.");
            }

            for (int sceneIndex = 0; sceneIndex < node.Scenes.Count; sceneIndex++)
            {
                SceneSnapshot? scene = node.Scenes[sceneIndex];
                if (scene?.Key == null
                    || scene.DialogueText == null
                    || string.IsNullOrWhiteSpace(scene.Key.NodeGuid)
                    || scene.Key.SceneIndex < 0
                    || !IsSha256(scene.Key.Fingerprint))
                {
                    return Result.Fail(
                        $"Current AAP script node {nodeIndex} scene {sceneIndex} is malformed.");
                }
            }
        }

        if (playback.Source == null
            || !IsSha256(playback.Source.PathKey)
            || !IsSha256(playback.Source.RevisionSha256)
            || string.IsNullOrWhiteSpace(playback.SchemaName)
            || playback.Records == null)
        {
            return Result.Fail(
                "Current AAS snapshot has an invalid identity, schema, or record collection.");
        }

        for (int index = 0; index < playback.Records.Count; index++)
        {
            PlaybackRecordSnapshot? record = playback.Records[index];
            if (record == null
                || record.RecordIndex != index
                || record.TextJp == null
                || !IsSha256(record.Fingerprint))
            {
                return Result.Fail($"Current AAS record {index} is malformed.");
            }
        }

        return Result.Ok();
    }

    private static Result ValidateJsonShape(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Result.Fail("Projection sidecar root must be a JSON object.");
        }

        Result rootResult = ValidateExactProperties(
            root,
            RootProperties,
            "projection sidecar root");
        if (!rootResult.Success)
        {
            return rootResult;
        }

        if (!root.TryGetProperty("instructions", out JsonElement instructions)
            || instructions.ValueKind != JsonValueKind.Array)
        {
            return Result.Fail(
                "Projection sidecar instructions must be a JSON array.");
        }

        int instructionIndex = 0;
        foreach (JsonElement instruction in instructions.EnumerateArray())
        {
            if (instruction.ValueKind != JsonValueKind.Object)
            {
                return Result.Fail(
                    $"Projection instruction {instructionIndex} must be a JSON object.");
            }

            Result instructionResult = ValidateExactProperties(
                instruction,
                InstructionProperties,
                $"projection instruction {instructionIndex}");
            if (!instructionResult.Success)
            {
                return instructionResult;
            }

            foreach (string sceneProperty in new[]
                     {
                         "scene",
                         "previousScene",
                         "chainStartScene"
                     })
            {
                if (!instruction.TryGetProperty(
                        sceneProperty,
                        out JsonElement scene)
                    || scene.ValueKind != JsonValueKind.Object)
                {
                    return Result.Fail(
                        $"Projection instruction {instructionIndex} {sceneProperty} must be a JSON object.");
                }

                Result sceneResult = ValidateExactProperties(
                    scene,
                    SceneProperties,
                    $"projection instruction {instructionIndex} {sceneProperty}");
                if (!sceneResult.Success)
                {
                    return sceneResult;
                }
            }

            instructionIndex++;
        }

        return Result.Ok();
    }

    private static Result ValidateExactProperties(
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

        string? missing = allowed.FirstOrDefault(property => !seen.Contains(property));
        return missing == null
            ? Result.Ok()
            : Result.Fail($"Missing JSON property '{missing}' in {context}.");
    }

    private Result<string> ResolveSidecarPath(string sidecarPath)
    {
        if (string.IsNullOrWhiteSpace(sidecarPath))
        {
            return Result<string>.Fail("Projection sidecar path is empty.");
        }

        if (Path.IsPathRooted(sidecarPath))
        {
            return Result<string>.Fail(
                "Projection sidecar path must be relative to the configured root.");
        }

        string normalized = sidecarPath.Replace(
            Path.AltDirectorySeparatorChar,
            Path.DirectorySeparatorChar);
        string[] segments = normalized.Split(Path.DirectorySeparatorChar);
        if (segments.Any(segment =>
                string.IsNullOrWhiteSpace(segment)
                || segment == "."
                || segment == ".."
                || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || segment.Contains(Path.VolumeSeparatorChar)))
        {
            return Result<string>.Fail(
                "Projection sidecar path contains an empty, traversal, or invalid segment.");
        }

        if (!normalized.EndsWith(SidecarExtension, StringComparison.OrdinalIgnoreCase))
        {
            return Result<string>.Fail(
                $"Projection sidecar path must end with {SidecarExtension}; official and source-sidecar extensions are not accepted.");
        }

        try
        {
            string fullPath = Path.GetFullPath(Path.Combine(_rootDirectory, normalized));
            if (!fullPath.StartsWith(_rootPrefix, _pathComparison))
            {
                return Result<string>.Fail(
                    "Projection sidecar path resolves outside the configured root.");
            }

            return Result<string>.Ok(fullPath);
        }
        catch (Exception ex) when (
            ex is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return Result<string>.Fail(
                $"Projection sidecar path is invalid: {ex.Message}");
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
                        $"Projection path contains a reparse-point directory: {current}");
                }
            }
        }

        if ((File.Exists(targetPath) || Directory.Exists(targetPath))
            && IsReparsePoint(targetPath))
        {
            return Result.Fail(
                $"Projection sidecar target is a reparse point: {targetPath}");
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

    private static bool ProjectionsEqual(
        ContinuationPlaybackProjection expected,
        ContinuationPlaybackProjection actual)
    {
        if (expected.SchemaVersion != actual.SchemaVersion
            || !string.Equals(expected.ProjectId, actual.ProjectId, StringComparison.Ordinal)
            || !string.Equals(expected.ProjectPathKey, actual.ProjectPathKey, StringComparison.Ordinal)
            || !string.Equals(expected.ProjectRevisionSha256, actual.ProjectRevisionSha256, StringComparison.Ordinal)
            || !string.Equals(expected.PlaybackPathKey, actual.PlaybackPathKey, StringComparison.Ordinal)
            || !string.Equals(expected.PlaybackRevisionSha256, actual.PlaybackRevisionSha256, StringComparison.Ordinal)
            || !string.Equals(expected.PlaybackSchemaName, actual.PlaybackSchemaName, StringComparison.Ordinal)
            || expected.Instructions.Count != actual.Instructions.Count)
        {
            return false;
        }

        for (int index = 0; index < expected.Instructions.Count; index++)
        {
            if (expected.Instructions[index] != actual.Instructions[index])
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidSceneKey(SceneKey key, bool requireAfterFirst) =>
        Guid.TryParseExact(key.NodeGuid, "D", out _)
        && key.SceneIndex >= (requireAfterFirst ? 1 : 0)
        && IsSha256(key.Fingerprint);

    private bool IsBoundedText(string? value) =>
        value != null && value.Length <= _options.MaxTextCharacters;

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool SceneKeysEqual(SceneKey left, SceneKey right) =>
        left.SceneIndex == right.SceneIndex
        && string.Equals(left.NodeGuid, right.NodeGuid, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Fingerprint, right.Fingerprint, StringComparison.OrdinalIgnoreCase);

    private static string Location(SceneKey scene) =>
        Location(scene.NodeGuid, scene.SceneIndex);

    private static string Location(string nodeGuid, int sceneIndex) =>
        $"{nodeGuid.Trim().ToLowerInvariant()}:{sceneIndex}";

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
            // Orphaned temporary files are ignored by both load and save.
        }
    }

    private static void ValidateOptions(
        JsonContinuationProjectionStoreOptions options)
    {
        if (options.MaxFileSizeBytes <= 0
            || options.MaxFileSizeBytes > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Maximum projection file size must be between 1 and Int32.MaxValue bytes.");
        }

        if (options.MaxJsonDepth <= 0
            || options.MaxInstructions <= 0
            || options.MaxTextCharacters <= 0
            || options.MaxSchemaNameCharacters <= 0
            || options.LockRetryAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Projection limits and lock retry attempts must be positive.");
        }

        if (options.LockRetryDelayMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Projection lock retry delay cannot be negative.");
        }
    }
}
