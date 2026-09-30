using System.Text.Json;
using System.Text.Json.Serialization;
using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Core.Results;
using AzureArchive.VideoTools.Core.Workspaces;

namespace AzureArchive.VideoTools.Formats.Workspace;

public sealed class JsonCommandWorkspaceStore : ICommandWorkspaceStore
{
    public const string TimelineFileName = "commands.aavt.json";
    public const string ProjectionFileName = "commands.aavt.playback.json";
    public const string BackupSuffix = ".bak";

    private static readonly HashSet<string> TimelineRootProperties = new(
        new[]
        {
            "schemaVersion",
            "workspaceId",
            "projectPathKey",
            "projectRevisionSha256",
            "entries"
        },
        StringComparer.Ordinal);

    private static readonly HashSet<string> TimelineEntryProperties = new(
        new[] { "scene", "commands" },
        StringComparer.Ordinal);

    private static readonly HashSet<string> SceneProperties = new(
        new[] { "nodeGuid", "sceneIndex", "fingerprint" },
        StringComparer.Ordinal);

    private static readonly HashSet<string> AuthoringCommandProperties = new(
        new[]
        {
            "commandId",
            "order",
            "phase",
            "commandType",
            "directive",
            "enabled"
        },
        StringComparer.Ordinal);

    private static readonly HashSet<string> ProjectionRootProperties = new(
        new[]
        {
            "schemaVersion",
            "workspaceId",
            "sourceTimelineSha256",
            "projectPathKey",
            "projectRevisionSha256",
            "playbackPathKey",
            "playbackRevisionSha256",
            "playbackSchemaName",
            "batches"
        },
        StringComparer.Ordinal);

    private static readonly HashSet<string> BatchProperties = new(
        new[]
        {
            "scene",
            "playbackRecordIndex",
            "playbackRecordFingerprint",
            "compiledScript",
            "commands"
        },
        StringComparer.Ordinal);

    private static readonly HashSet<string> CompiledScriptProperties = new(
        new[] { "sha256", "utf16Length", "lineCount" },
        StringComparer.Ordinal);

    private static readonly HashSet<string> CompiledCommandProperties = new(
        new[]
        {
            "commandId",
            "order",
            "phase",
            "commandType",
            "requiredCapability",
            "canonicalDirective"
        },
        StringComparer.Ordinal);

    private readonly string _rootDirectory;
    private readonly string _rootPrefix;
    private readonly StringComparison _pathComparison;
    private readonly JsonCommandWorkspaceStoreOptions _options;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly ICommandTimelineCompiler _compiler;
    private readonly IPlaybackCommandBinder _binder;
    private readonly object _writeGate = new();

    public JsonCommandWorkspaceStore(
        string rootDirectory,
        JsonCommandWorkspaceStoreOptions? options = null,
        ICommandTimelineCompiler? compiler = null,
        IPlaybackCommandBinder? binder = null)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException(
                "Command workspace root directory is empty.",
                nameof(rootDirectory));
        }

        _options = options ?? new JsonCommandWorkspaceStoreOptions();
        ValidateOptions(_options);
        _compiler = compiler ?? new CommandTimelineCompiler();
        _binder = binder ?? new PlaybackCommandBinder();

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
                "Command workspace root directory is invalid.",
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
        _serializerOptions.Converters.Add(new JsonStringEnumConverter(
            JsonNamingPolicy.CamelCase,
            allowIntegerValues: false));
    }

    public string RootDirectory => _rootDirectory;

    public Result<CommandTimelineLoadSnapshot> TryLoadTimeline(
        string projectPathKey,
        ModWorkspaceManifest workspace,
        ProjectSnapshot currentProject,
        PlaybackArchiveSnapshot currentPlayback)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(currentProject);
        ArgumentNullException.ThrowIfNull(currentPlayback);

        Result context = ValidateContext(
            projectPathKey,
            workspace,
            currentProject,
            currentPlayback);
        if (!context.Success)
        {
            return Result<CommandTimelineLoadSnapshot>.Fail(context.Error);
        }

        Result<string> pathResult = ResolveDocumentPath(
            projectPathKey,
            TimelineFileName);
        if (!pathResult.Success || pathResult.Value == null)
        {
            return Result<CommandTimelineLoadSnapshot>.Fail(pathResult.Error);
        }

        string primaryPath = pathResult.Value;
        string backupPath = GetBackupPath(primaryPath);
        try
        {
            bool primaryExists = File.Exists(primaryPath);
            bool backupExists = File.Exists(backupPath);
            if (!primaryExists && !backupExists)
            {
                return Result<CommandTimelineLoadSnapshot>.Ok(new(
                    null,
                    0,
                    CommandWorkspaceDocumentStatus.NotFound,
                    CommandWorkspaceLoadSource.None,
                    Array.Empty<CommandWorkspaceStoreNotice>()));
            }

            Result<InspectedDocument<CommandTimelineDocument>>? primary = null;
            if (primaryExists)
            {
                primary = InspectTimeline(
                    primaryPath,
                    validateCurrent: true,
                    workspace,
                    currentProject,
                    currentPlayback);
                if (primary.Success && primary.Value != null)
                {
                    return Result<CommandTimelineLoadSnapshot>.Ok(
                        TimelineSnapshot(
                            primary.Value,
                            CommandWorkspaceLoadSource.Primary,
                            Array.Empty<CommandWorkspaceStoreNotice>()));
                }
            }

            if (backupExists)
            {
                Result<InspectedDocument<CommandTimelineDocument>> backup =
                    InspectTimeline(
                        backupPath,
                        validateCurrent: true,
                        workspace,
                        currentProject,
                        currentPlayback);
                if (backup.Success && backup.Value != null)
                {
                    return Result<CommandTimelineLoadSnapshot>.Ok(
                        TimelineSnapshot(
                            backup.Value,
                            CommandWorkspaceLoadSource.Backup,
                            new[]
                            {
                                RecoveryNotice(primaryExists, "timeline")
                            }));
                }

                return Result<CommandTimelineLoadSnapshot>.Fail(
                    "Neither primary nor backup command timeline is valid. "
                    + $"Primary: {primary?.Error ?? "missing"} Backup: {backup.Error}");
            }

            return Result<CommandTimelineLoadSnapshot>.Fail(
                "Primary command timeline is invalid and no backup exists: "
                + primary?.Error);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return Result<CommandTimelineLoadSnapshot>.Fail(
                $"Command timeline could not be inspected: {ex.Message}");
        }
    }

    public Result<CommandWorkspaceSaveSnapshot> SaveTimelineAtomic(
        string projectPathKey,
        CommandTimelineDocument timeline,
        ModWorkspaceManifest workspace,
        ProjectSnapshot currentProject,
        PlaybackArchiveSnapshot currentPlayback)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(currentProject);
        ArgumentNullException.ThrowIfNull(currentPlayback);

        Result context = ValidateContext(
            projectPathKey,
            workspace,
            currentProject,
            currentPlayback);
        if (!context.Success)
        {
            return Result<CommandWorkspaceSaveSnapshot>.Fail(context.Error);
        }

        Result<PlaybackCommandProjection> compiled = _compiler.Compile(
            currentProject,
            currentPlayback,
            workspace,
            timeline);
        if (!compiled.Success)
        {
            return Result<CommandWorkspaceSaveSnapshot>.Fail(
                $"Command timeline is not compilable: {compiled.Error}");
        }

        Result<string> pathResult = ResolveDocumentPath(
            projectPathKey,
            TimelineFileName);
        if (!pathResult.Success || pathResult.Value == null)
        {
            return Result<CommandWorkspaceSaveSnapshot>.Fail(pathResult.Error);
        }

        Result<byte[]> serialized = Serialize(timeline, "command timeline");
        if (!serialized.Success || serialized.Value == null)
        {
            return Result<CommandWorkspaceSaveSnapshot>.Fail(serialized.Error);
        }

        lock (_writeGate)
        {
            return SaveAtomicCore(
                pathResult.Value,
                timeline,
                serialized.Value,
                (path, validateCurrent) => InspectTimeline(
                    path,
                    validateCurrent,
                    workspace,
                    currentProject,
                    currentPlayback),
                TimelinesEqual,
                document => document.WorkspaceId,
                workspace.WorkspaceId,
                "command timeline");
        }
    }

    public Result<PlaybackCommandProjectionLoadSnapshot> TryLoadProjection(
        string projectPathKey,
        CommandTimelineDocument timeline,
        ModWorkspaceManifest workspace,
        ProjectSnapshot currentProject,
        PlaybackArchiveSnapshot currentPlayback)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(currentProject);
        ArgumentNullException.ThrowIfNull(currentPlayback);

        Result context = ValidateContext(
            projectPathKey,
            workspace,
            currentProject,
            currentPlayback);
        if (!context.Success)
        {
            return Result<PlaybackCommandProjectionLoadSnapshot>.Fail(context.Error);
        }

        Result<PlaybackCommandProjection> rebuilt = _compiler.Compile(
            currentProject,
            currentPlayback,
            workspace,
            timeline);
        if (!rebuilt.Success)
        {
            return Result<PlaybackCommandProjectionLoadSnapshot>.Fail(
                $"Current command timeline is not compilable: {rebuilt.Error}");
        }

        Result<string> pathResult = ResolveDocumentPath(
            projectPathKey,
            ProjectionFileName);
        if (!pathResult.Success || pathResult.Value == null)
        {
            return Result<PlaybackCommandProjectionLoadSnapshot>.Fail(
                pathResult.Error);
        }

        string primaryPath = pathResult.Value;
        string backupPath = GetBackupPath(primaryPath);
        try
        {
            bool primaryExists = File.Exists(primaryPath);
            bool backupExists = File.Exists(backupPath);
            if (!primaryExists && !backupExists)
            {
                return Result<PlaybackCommandProjectionLoadSnapshot>.Ok(new(
                    null,
                    0,
                    CommandWorkspaceDocumentStatus.NotFound,
                    CommandWorkspaceLoadSource.None,
                    Array.Empty<CommandWorkspaceStoreNotice>()));
            }

            Result<InspectedDocument<PlaybackCommandProjection>>? primary = null;
            if (primaryExists)
            {
                primary = InspectProjection(
                    primaryPath,
                    validateCurrent: true,
                    timeline,
                    workspace,
                    currentProject,
                    currentPlayback);
                if (primary.Success && primary.Value != null)
                {
                    return Result<PlaybackCommandProjectionLoadSnapshot>.Ok(
                        ProjectionSnapshot(
                            primary.Value,
                            CommandWorkspaceLoadSource.Primary,
                            Array.Empty<CommandWorkspaceStoreNotice>()));
                }
            }

            if (backupExists)
            {
                Result<InspectedDocument<PlaybackCommandProjection>> backup =
                    InspectProjection(
                        backupPath,
                        validateCurrent: true,
                        timeline,
                        workspace,
                        currentProject,
                        currentPlayback);
                if (backup.Success && backup.Value != null)
                {
                    return Result<PlaybackCommandProjectionLoadSnapshot>.Ok(
                        ProjectionSnapshot(
                            backup.Value,
                            CommandWorkspaceLoadSource.Backup,
                            new[]
                            {
                                RecoveryNotice(primaryExists, "projection")
                            }));
                }

                return Result<PlaybackCommandProjectionLoadSnapshot>.Fail(
                    "Neither primary nor backup command projection is valid. "
                    + $"Primary: {primary?.Error ?? "missing"} Backup: {backup.Error}");
            }

            return Result<PlaybackCommandProjectionLoadSnapshot>.Fail(
                "Primary command projection is invalid and no backup exists: "
                + primary?.Error);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return Result<PlaybackCommandProjectionLoadSnapshot>.Fail(
                $"Command projection could not be inspected: {ex.Message}");
        }
    }

    public Result<CommandWorkspaceSaveSnapshot> SaveProjectionAtomic(
        string projectPathKey,
        PlaybackCommandProjection projection,
        CommandTimelineDocument timeline,
        ModWorkspaceManifest workspace,
        ProjectSnapshot currentProject,
        PlaybackArchiveSnapshot currentPlayback)
    {
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(currentProject);
        ArgumentNullException.ThrowIfNull(currentPlayback);

        Result context = ValidateContext(
            projectPathKey,
            workspace,
            currentProject,
            currentPlayback);
        if (!context.Success)
        {
            return Result<CommandWorkspaceSaveSnapshot>.Fail(context.Error);
        }

        Result projectionValidation = ValidateProjectionAgainstCurrent(
            projection,
            timeline,
            workspace,
            currentProject,
            currentPlayback);
        if (!projectionValidation.Success)
        {
            return Result<CommandWorkspaceSaveSnapshot>.Fail(
                projectionValidation.Error);
        }

        Result<string> pathResult = ResolveDocumentPath(
            projectPathKey,
            ProjectionFileName);
        if (!pathResult.Success || pathResult.Value == null)
        {
            return Result<CommandWorkspaceSaveSnapshot>.Fail(pathResult.Error);
        }

        Result<byte[]> serialized = Serialize(projection, "command projection");
        if (!serialized.Success || serialized.Value == null)
        {
            return Result<CommandWorkspaceSaveSnapshot>.Fail(serialized.Error);
        }

        lock (_writeGate)
        {
            return SaveAtomicCore(
                pathResult.Value,
                projection,
                serialized.Value,
                (path, validateCurrent) => InspectProjection(
                    path,
                    validateCurrent,
                    timeline,
                    workspace,
                    currentProject,
                    currentPlayback),
                ProjectionsEqual,
                document => document.WorkspaceId,
                workspace.WorkspaceId,
                "command projection");
        }
    }

    public Result<CommandWorkspaceSaveSnapshot> CompileAndSaveProjectionAtomic(
        string projectPathKey,
        CommandTimelineDocument timeline,
        ModWorkspaceManifest workspace,
        ProjectSnapshot currentProject,
        PlaybackArchiveSnapshot currentPlayback)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(currentProject);
        ArgumentNullException.ThrowIfNull(currentPlayback);

        Result<PlaybackCommandProjection> projection = _compiler.Compile(
            currentProject,
            currentPlayback,
            workspace,
            timeline);
        return projection.Success && projection.Value != null
            ? SaveProjectionAtomic(
                projectPathKey,
                projection.Value,
                timeline,
                workspace,
                currentProject,
                currentPlayback)
            : Result<CommandWorkspaceSaveSnapshot>.Fail(
                $"Command projection compilation failed: {projection.Error}");
    }

    private Result<InspectedDocument<CommandTimelineDocument>> InspectTimeline(
        string path,
        bool validateCurrent,
        ModWorkspaceManifest workspace,
        ProjectSnapshot currentProject,
        PlaybackArchiveSnapshot currentPlayback)
    {
        Result<JsonDocument> jsonResult = ReadJson(path, "command timeline");
        if (!jsonResult.Success || jsonResult.Value == null)
        {
            return Result<InspectedDocument<CommandTimelineDocument>>.Fail(
                jsonResult.Error);
        }

        using JsonDocument json = jsonResult.Value;
        Result<int> schema = ReadSchemaVersion(
            json.RootElement,
            "command timeline");
        if (!schema.Success)
        {
            return Result<InspectedDocument<CommandTimelineDocument>>.Fail(
                schema.Error);
        }

        if (schema.Value != CommandTimelineDocument.CurrentSchemaVersion)
        {
            return Result<InspectedDocument<CommandTimelineDocument>>.Ok(new(
                null,
                schema.Value,
                schema.Value > CommandTimelineDocument.CurrentSchemaVersion
                    ? CommandWorkspaceDocumentStatus.FutureSchema
                    : CommandWorkspaceDocumentStatus.UnsupportedLegacySchema));
        }

        Result shape = ValidateTimelineJsonShape(json.RootElement);
        if (!shape.Success)
        {
            return Result<InspectedDocument<CommandTimelineDocument>>.Fail(
                shape.Error);
        }

        try
        {
            CommandTimelineDocument? timeline =
                json.RootElement.Deserialize<CommandTimelineDocument>(
                    _serializerOptions);
            if (timeline == null)
            {
                return Result<InspectedDocument<CommandTimelineDocument>>.Fail(
                    "Command timeline JSON did not produce a document.");
            }

            Result bounds = ValidateTimelineBounds(timeline);
            if (!bounds.Success)
            {
                return Result<InspectedDocument<CommandTimelineDocument>>.Fail(
                    bounds.Error);
            }

            if (validateCurrent)
            {
                Result<PlaybackCommandProjection> compiled = _compiler.Compile(
                    currentProject,
                    currentPlayback,
                    workspace,
                    timeline);
                if (!compiled.Success)
                {
                    return Result<InspectedDocument<CommandTimelineDocument>>.Fail(
                        $"Command timeline is not valid for current snapshots: {compiled.Error}");
                }
            }

            return Result<InspectedDocument<CommandTimelineDocument>>.Ok(new(
                timeline,
                schema.Value,
                CommandWorkspaceDocumentStatus.Ready));
        }
        catch (JsonException ex)
        {
            return Result<InspectedDocument<CommandTimelineDocument>>.Fail(
                $"Command timeline JSON is invalid: {ex.Message}");
        }
    }

    private Result<InspectedDocument<PlaybackCommandProjection>> InspectProjection(
        string path,
        bool validateCurrent,
        CommandTimelineDocument timeline,
        ModWorkspaceManifest workspace,
        ProjectSnapshot currentProject,
        PlaybackArchiveSnapshot currentPlayback)
    {
        Result<JsonDocument> jsonResult = ReadJson(path, "command projection");
        if (!jsonResult.Success || jsonResult.Value == null)
        {
            return Result<InspectedDocument<PlaybackCommandProjection>>.Fail(
                jsonResult.Error);
        }

        using JsonDocument json = jsonResult.Value;
        Result<int> schema = ReadSchemaVersion(
            json.RootElement,
            "command projection");
        if (!schema.Success)
        {
            return Result<InspectedDocument<PlaybackCommandProjection>>.Fail(
                schema.Error);
        }

        if (schema.Value != PlaybackCommandProjection.CurrentSchemaVersion)
        {
            return Result<InspectedDocument<PlaybackCommandProjection>>.Ok(new(
                null,
                schema.Value,
                schema.Value > PlaybackCommandProjection.CurrentSchemaVersion
                    ? CommandWorkspaceDocumentStatus.FutureSchema
                    : CommandWorkspaceDocumentStatus.UnsupportedLegacySchema));
        }

        Result shape = ValidateProjectionJsonShape(json.RootElement);
        if (!shape.Success)
        {
            return Result<InspectedDocument<PlaybackCommandProjection>>.Fail(
                shape.Error);
        }

        try
        {
            PlaybackCommandProjection? projection =
                json.RootElement.Deserialize<PlaybackCommandProjection>(
                    _serializerOptions);
            if (projection == null)
            {
                return Result<InspectedDocument<PlaybackCommandProjection>>.Fail(
                    "Command projection JSON did not produce a document.");
            }

            Result bounds = ValidateProjectionBounds(projection);
            if (!bounds.Success)
            {
                return Result<InspectedDocument<PlaybackCommandProjection>>.Fail(
                    bounds.Error);
            }

            if (validateCurrent)
            {
                Result current = ValidateProjectionAgainstCurrent(
                    projection,
                    timeline,
                    workspace,
                    currentProject,
                    currentPlayback);
                if (!current.Success)
                {
                    return Result<InspectedDocument<PlaybackCommandProjection>>.Fail(
                        current.Error);
                }
            }

            return Result<InspectedDocument<PlaybackCommandProjection>>.Ok(new(
                projection,
                schema.Value,
                CommandWorkspaceDocumentStatus.Ready));
        }
        catch (JsonException ex)
        {
            return Result<InspectedDocument<PlaybackCommandProjection>>.Fail(
                $"Command projection JSON is invalid: {ex.Message}");
        }
    }

    private Result ValidateProjectionAgainstCurrent(
        PlaybackCommandProjection projection,
        CommandTimelineDocument timeline,
        ModWorkspaceManifest workspace,
        ProjectSnapshot currentProject,
        PlaybackArchiveSnapshot currentPlayback)
    {
        Result<PlaybackCommandProjection> rebuilt = _compiler.Compile(
            currentProject,
            currentPlayback,
            workspace,
            timeline);
        if (!rebuilt.Success
            || rebuilt.Value == null
            || !ProjectionsEqual(projection, rebuilt.Value))
        {
            string reason = rebuilt.Success
                ? "the rebuilt projection differs"
                : rebuilt.Error;
            return Result.Fail(
                "Command projection cannot be reproduced by the trusted compiler: "
                + reason);
        }

        Result<IPlaybackCommandIndex> binding = _binder.Bind(
            currentPlayback,
            projection);
        return binding.Success
            ? Result.Ok()
            : Result.Fail(
                $"Command projection binding failed: {binding.Error}");
    }

    private Result<CommandWorkspaceSaveSnapshot> SaveAtomicCore<T>(
        string primaryPath,
        T requested,
        byte[] bytes,
        Func<string, bool, Result<InspectedDocument<T>>> inspect,
        Func<T, T, bool> equals,
        Func<T, string> workspaceId,
        string expectedWorkspaceId,
        string label)
        where T : class
    {
        string? directory = Path.GetDirectoryName(primaryPath);
        if (string.IsNullOrEmpty(directory))
        {
            return Result<CommandWorkspaceSaveSnapshot>.Fail(
                $"The {label} path has no parent directory.");
        }

        string backupPath = GetBackupPath(primaryPath);
        string lockPath = primaryPath + ".lock";
        string temporaryPath = primaryPath + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            Result pathSafety = EnsureNoReparsePoints(primaryPath);
            if (!pathSafety.Success)
            {
                return Result<CommandWorkspaceSaveSnapshot>.Fail(
                    pathSafety.Error);
            }

            Directory.CreateDirectory(directory);
            foreach (string path in new[] { primaryPath, backupPath, lockPath })
            {
                pathSafety = EnsureNoReparsePoints(path);
                if (!pathSafety.Success)
                {
                    return Result<CommandWorkspaceSaveSnapshot>.Fail(
                        pathSafety.Error);
                }
            }

            Result<FileStream> lockResult = AcquireWriteLock(lockPath, label);
            if (!lockResult.Success || lockResult.Value == null)
            {
                return Result<CommandWorkspaceSaveSnapshot>.Fail(
                    lockResult.Error);
            }

            using (lockResult.Value)
            {
                bool replacedExisting = File.Exists(primaryPath);
                if (replacedExisting)
                {
                    Result<InspectedDocument<T>> existing = inspect(
                        primaryPath,
                        false);
                    if (!existing.Success
                        || existing.Value?.Document == null
                        || existing.Value.Status
                            != CommandWorkspaceDocumentStatus.Ready)
                    {
                        return Result<CommandWorkspaceSaveSnapshot>.Fail(
                            $"Existing {label} is unreadable or uses an unsupported schema; refusing to overwrite it.");
                    }

                    if (!string.Equals(
                            workspaceId(existing.Value.Document),
                            expectedWorkspaceId,
                            StringComparison.Ordinal))
                    {
                        return Result<CommandWorkspaceSaveSnapshot>.Fail(
                            $"Existing {label} belongs to another workspace.");
                    }
                }

                WriteTemporaryFile(temporaryPath, bytes);
                Result<InspectedDocument<T>> temporary = inspect(
                    temporaryPath,
                    true);
                if (!temporary.Success
                    || temporary.Value?.Document == null
                    || temporary.Value.Status != CommandWorkspaceDocumentStatus.Ready
                    || !equals(requested, temporary.Value.Document))
                {
                    string reason = temporary.Success
                        ? $"Temporary {label} content changed during verification."
                        : temporary.Error;
                    return Result<CommandWorkspaceSaveSnapshot>.Fail(
                        $"Temporary {label} validation failed: {reason}");
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

                Result<InspectedDocument<T>> committed = inspect(
                    primaryPath,
                    true);
                if (!committed.Success
                    || committed.Value?.Document == null
                    || committed.Value.Status != CommandWorkspaceDocumentStatus.Ready
                    || !equals(requested, committed.Value.Document))
                {
                    string reason = committed.Success
                        ? $"Committed {label} differs from the requested document."
                        : committed.Error;
                    return Result<CommandWorkspaceSaveSnapshot>.Fail(
                        $"Committed {label} validation failed: {reason}");
                }

                return Result<CommandWorkspaceSaveSnapshot>.Ok(new(
                    primaryPath,
                    replacedExisting,
                    replacedExisting && File.Exists(backupPath)));
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            return Result<CommandWorkspaceSaveSnapshot>.Fail(
                $"The {label} cannot be written: {ex.Message}");
        }
        catch (IOException ex)
        {
            return Result<CommandWorkspaceSaveSnapshot>.Fail(
                $"The {label} atomic save failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            return Result<CommandWorkspaceSaveSnapshot>.Fail(
                $"Unexpected {label} save failure ({ex.GetType().Name}): {ex.Message}");
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private Result<JsonDocument> ReadJson(string path, string label)
    {
        Result safety = EnsureNoReparsePoints(path);
        if (!safety.Success)
        {
            return Result<JsonDocument>.Fail(safety.Error);
        }

        Result<byte[]> bytes = ReadFile(path, label);
        if (!bytes.Success || bytes.Value == null)
        {
            return Result<JsonDocument>.Fail(bytes.Error);
        }

        try
        {
            return Result<JsonDocument>.Ok(JsonDocument.Parse(
                bytes.Value,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = _options.MaxJsonDepth
                }));
        }
        catch (JsonException ex)
        {
            return Result<JsonDocument>.Fail(
                $"The {label} JSON is invalid: {ex.Message}");
        }
    }

    private Result<byte[]> ReadFile(string path, string label)
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
                return Result<byte[]>.Fail($"The {label} is empty.");
            }

            if (stream.Length > _options.MaxFileSizeBytes)
            {
                return Result<byte[]>.Fail(
                    $"The {label} is {stream.Length} bytes, exceeding the {_options.MaxFileSizeBytes}-byte limit.");
            }

            byte[] bytes = new byte[(int)stream.Length];
            int offset = 0;
            while (offset < bytes.Length)
            {
                int read = stream.Read(bytes, offset, bytes.Length - offset);
                if (read == 0)
                {
                    return Result<byte[]>.Fail(
                        $"The {label} ended before its declared length.");
                }

                offset += read;
            }

            return Result<byte[]>.Ok(bytes);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Result<byte[]>.Fail(
                $"The {label} cannot be accessed: {ex.Message}");
        }
        catch (IOException ex)
        {
            return Result<byte[]>.Fail(
                $"The {label} could not be read: {ex.Message}");
        }
    }

    private Result<byte[]> Serialize<T>(T document, string label)
    {
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
                document,
                _serializerOptions);
            return bytes.LongLength <= _options.MaxFileSizeBytes
                ? Result<byte[]>.Ok(bytes)
                : Result<byte[]>.Fail(
                    $"Serialized {label} is {bytes.LongLength} bytes, exceeding the {_options.MaxFileSizeBytes}-byte limit.");
        }
        catch (Exception ex)
        {
            return Result<byte[]>.Fail(
                $"The {label} could not be serialized ({ex.GetType().Name}): {ex.Message}");
        }
    }

    private Result ValidateContext(
        string projectPathKey,
        ModWorkspaceManifest workspace,
        ProjectSnapshot project,
        PlaybackArchiveSnapshot playback)
    {
        Result manifest = ModWorkspaceCompatibilityGate.ValidateManifest(workspace);
        if (!manifest.Success)
        {
            return manifest;
        }

        if (workspace.SchemaVersion != ModWorkspaceManifest.CurrentSchemaVersion)
        {
            return Result.Fail(
                "Command files require the current workspace schema.");
        }

        if (!IsSha256(projectPathKey)
            || !SameSha(projectPathKey, workspace.Source.ProjectPathKey))
        {
            return Result.Fail(
                "Command workspace folder key must equal the manifest project path key.");
        }

        if (project.Source == null
            || playback.Source == null
            || project.Nodes == null
            || playback.Records == null
            || !SameSha(project.Source.PathKey, workspace.Source.ProjectPathKey)
            || !SameSha(
                project.Source.RevisionSha256,
                workspace.Source.ProjectRevisionSha256)
            || !SameSha(playback.Source.PathKey, workspace.Source.PlaybackPathKey)
            || !SameSha(
                playback.Source.RevisionSha256,
                workspace.Source.PlaybackRevisionSha256)
            || !string.Equals(
                playback.SchemaName,
                workspace.Source.PlaybackSchemaName,
                StringComparison.Ordinal))
        {
            return Result.Fail(
                "Command workspace manifest does not match the exact current AAP/AAS snapshots.");
        }

        return Result.Ok();
    }

    private Result ValidateTimelineBounds(CommandTimelineDocument timeline)
    {
        if (timeline.SchemaVersion != CommandTimelineDocument.CurrentSchemaVersion
            || !Guid.TryParseExact(timeline.WorkspaceId, "D", out _)
            || !IsSha256(timeline.ProjectPathKey)
            || !IsSha256(timeline.ProjectRevisionSha256)
            || timeline.Entries == null)
        {
            return Result.Fail(
                "Command timeline root identity or entry collection is invalid.");
        }

        if (timeline.Entries.Count > _options.MaxEntries)
        {
            return Result.Fail(
                $"Command timeline has {timeline.Entries.Count} entries, exceeding the {_options.MaxEntries}-entry limit.");
        }

        var locations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var commandIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int totalCommands = 0;
        for (int entryIndex = 0; entryIndex < timeline.Entries.Count; entryIndex++)
        {
            CommandTimelineEntry? entry = timeline.Entries[entryIndex];
            if (entry?.Scene == null
                || entry.Commands == null
                || !IsSceneKey(entry.Scene))
            {
                return Result.Fail(
                    $"Command timeline entry {entryIndex} is malformed.");
            }

            if (!locations.Add(Location(entry.Scene)))
            {
                return Result.Fail(
                    $"Command timeline contains duplicate scene {Location(entry.Scene)}.");
            }

            totalCommands += entry.Commands.Count;
            if (totalCommands > _options.MaxCommands)
            {
                return Result.Fail(
                    $"Command timeline exceeds the {_options.MaxCommands}-command limit.");
            }

            for (int commandIndex = 0; commandIndex < entry.Commands.Count; commandIndex++)
            {
                AuthoringCommand? command = entry.Commands[commandIndex];
                if (command == null
                    || !Guid.TryParseExact(command.CommandId, "D", out _)
                    || !commandIds.Add(command.CommandId)
                    || command.Order != commandIndex
                    || command.Phase != CommandTimelinePhase.SceneEnter
                    || !IsBoundedIdentifier(command.CommandType)
                    || command.Directive == null
                    || command.Directive.Length > _options.MaxDirectiveCharacters)
                {
                    return Result.Fail(
                        $"Command timeline command {entryIndex}:{commandIndex} is malformed, duplicated, or oversized.");
                }
            }
        }

        return Result.Ok();
    }

    private Result ValidateProjectionBounds(PlaybackCommandProjection projection)
    {
        if (projection.SchemaVersion != PlaybackCommandProjection.CurrentSchemaVersion
            || !Guid.TryParseExact(projection.WorkspaceId, "D", out _)
            || !IsSha256(projection.SourceTimelineSha256)
            || !IsSha256(projection.ProjectPathKey)
            || !IsSha256(projection.ProjectRevisionSha256)
            || !IsSha256(projection.PlaybackPathKey)
            || !IsSha256(projection.PlaybackRevisionSha256)
            || string.IsNullOrWhiteSpace(projection.PlaybackSchemaName)
            || projection.PlaybackSchemaName.Length
                > _options.MaxSchemaNameCharacters
            || projection.Batches == null)
        {
            return Result.Fail(
                "Command projection root identity, schema, or batch collection is invalid.");
        }

        if (projection.Batches.Count > _options.MaxEntries)
        {
            return Result.Fail(
                $"Command projection has {projection.Batches.Count} batches, exceeding the {_options.MaxEntries}-batch limit.");
        }

        var locations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var commandIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int totalCommands = 0;
        int previousRecordIndex = -1;
        for (int batchIndex = 0; batchIndex < projection.Batches.Count; batchIndex++)
        {
            PlaybackCommandBatch? batch = projection.Batches[batchIndex];
            if (batch?.Scene == null
                || batch.CompiledScript == null
                || batch.Commands == null
                || batch.Commands.Count == 0
                || !IsSceneKey(batch.Scene)
                || !locations.Add(Location(batch.Scene))
                || batch.PlaybackRecordIndex <= previousRecordIndex
                || !IsSha256(batch.PlaybackRecordFingerprint)
                || !IsSha256(batch.CompiledScript.Sha256)
                || batch.CompiledScript.Utf16Length <= 0
                || batch.CompiledScript.LineCount < 1)
            {
                return Result.Fail(
                    $"Command projection batch {batchIndex} is malformed or out of order.");
            }

            previousRecordIndex = batch.PlaybackRecordIndex;
            totalCommands += batch.Commands.Count;
            if (totalCommands > _options.MaxCommands)
            {
                return Result.Fail(
                    $"Command projection exceeds the {_options.MaxCommands}-command limit.");
            }

            for (int commandIndex = 0; commandIndex < batch.Commands.Count; commandIndex++)
            {
                PlaybackCommandInstruction? command = batch.Commands[commandIndex];
                if (command == null
                    || !Guid.TryParseExact(command.CommandId, "D", out _)
                    || !commandIds.Add(command.CommandId)
                    || command.Order != commandIndex
                    || command.Phase != CommandTimelinePhase.SceneEnter
                    || !IsBoundedIdentifier(command.CommandType)
                    || !IsBoundedIdentifier(command.RequiredCapability)
                    || string.IsNullOrWhiteSpace(command.CanonicalDirective)
                    || command.CanonicalDirective.Length
                        > _options.MaxDirectiveCharacters)
                {
                    return Result.Fail(
                        $"Command projection instruction {batchIndex}:{commandIndex} is malformed, duplicated, or oversized.");
                }
            }
        }

        return Result.Ok();
    }

    private static Result ValidateTimelineJsonShape(JsonElement root)
    {
        Result rootResult = ValidateExactProperties(
            root,
            TimelineRootProperties,
            "command timeline root");
        if (!rootResult.Success)
        {
            return rootResult;
        }

        if (!root.TryGetProperty("entries", out JsonElement entries)
            || entries.ValueKind != JsonValueKind.Array)
        {
            return Result.Fail(
                "Command timeline entries must be a JSON array.");
        }

        int entryIndex = 0;
        foreach (JsonElement entry in entries.EnumerateArray())
        {
            Result entryResult = ValidateExactProperties(
                entry,
                TimelineEntryProperties,
                $"command timeline entry {entryIndex}");
            if (!entryResult.Success)
            {
                return entryResult;
            }

            if (!entry.TryGetProperty("scene", out JsonElement scene))
            {
                return Result.Fail(
                    $"Command timeline entry {entryIndex} has no scene object.");
            }

            Result sceneResult = ValidateExactProperties(
                scene,
                SceneProperties,
                $"command timeline entry {entryIndex} scene");
            if (!sceneResult.Success)
            {
                return sceneResult;
            }

            if (!entry.TryGetProperty("commands", out JsonElement commands)
                || commands.ValueKind != JsonValueKind.Array)
            {
                return Result.Fail(
                    $"Command timeline entry {entryIndex} commands must be an array.");
            }

            int commandIndex = 0;
            foreach (JsonElement command in commands.EnumerateArray())
            {
                Result commandResult = ValidateExactProperties(
                    command,
                    AuthoringCommandProperties,
                    $"command timeline command {entryIndex}:{commandIndex}");
                if (!commandResult.Success)
                {
                    return commandResult;
                }

                commandIndex++;
            }

            entryIndex++;
        }

        return Result.Ok();
    }

    private static Result ValidateProjectionJsonShape(JsonElement root)
    {
        Result rootResult = ValidateExactProperties(
            root,
            ProjectionRootProperties,
            "command projection root");
        if (!rootResult.Success)
        {
            return rootResult;
        }

        if (!root.TryGetProperty("batches", out JsonElement batches)
            || batches.ValueKind != JsonValueKind.Array)
        {
            return Result.Fail(
                "Command projection batches must be a JSON array.");
        }

        int batchIndex = 0;
        foreach (JsonElement batch in batches.EnumerateArray())
        {
            Result batchResult = ValidateExactProperties(
                batch,
                BatchProperties,
                $"command projection batch {batchIndex}");
            if (!batchResult.Success)
            {
                return batchResult;
            }

            if (!batch.TryGetProperty("scene", out JsonElement scene))
            {
                return Result.Fail(
                    $"Command projection batch {batchIndex} has no scene object.");
            }

            Result sceneResult = ValidateExactProperties(
                scene,
                SceneProperties,
                $"command projection batch {batchIndex} scene");
            if (!sceneResult.Success)
            {
                return sceneResult;
            }

            if (!batch.TryGetProperty(
                    "compiledScript",
                    out JsonElement compiledScript))
            {
                return Result.Fail(
                    $"Command projection batch {batchIndex} has no compiledScript object.");
            }

            Result scriptResult = ValidateExactProperties(
                compiledScript,
                CompiledScriptProperties,
                $"command projection batch {batchIndex} compiledScript");
            if (!scriptResult.Success)
            {
                return scriptResult;
            }

            if (!batch.TryGetProperty("commands", out JsonElement commands)
                || commands.ValueKind != JsonValueKind.Array)
            {
                return Result.Fail(
                    $"Command projection batch {batchIndex} commands must be an array.");
            }

            int commandIndex = 0;
            foreach (JsonElement command in commands.EnumerateArray())
            {
                Result commandResult = ValidateExactProperties(
                    command,
                    CompiledCommandProperties,
                    $"command projection instruction {batchIndex}:{commandIndex}");
                if (!commandResult.Success)
                {
                    return commandResult;
                }

                commandIndex++;
            }

            batchIndex++;
        }

        return Result.Ok();
    }

    private static Result ValidateExactProperties(
        JsonElement element,
        IReadOnlySet<string> allowed,
        string context)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return Result.Fail($"The {context} must be a JSON object.");
        }

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

        string? missing = allowed.FirstOrDefault(
            property => !seen.Contains(property));
        return missing == null
            ? Result.Ok()
            : Result.Fail(
                $"Missing JSON property '{missing}' in {context}.");
    }

    private static Result<int> ReadSchemaVersion(
        JsonElement root,
        string label)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Result<int>.Fail(
                $"The {label} root must be a JSON object.");
        }

        int count = 0;
        int value = 0;
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
                || !property.Value.TryGetInt32(out value)
                || value <= 0)
            {
                return Result<int>.Fail(
                    $"The {label} schemaVersion must be a positive Int32 value.");
            }
        }

        return count switch
        {
            1 => Result<int>.Ok(value),
            0 => Result<int>.Fail($"The {label} schemaVersion is missing."),
            _ => Result<int>.Fail($"The {label} schemaVersion is duplicated.")
        };
    }

    private Result<string> ResolveDocumentPath(
        string projectPathKey,
        string fileName)
    {
        if (!IsSha256(projectPathKey))
        {
            return Result<string>.Fail(
                "Command workspace project key must be a 64-character hexadecimal SHA-256 value.");
        }

        try
        {
            string directory = Path.GetFullPath(Path.Combine(
                _rootDirectory,
                projectPathKey.ToUpperInvariant()));
            if (!directory.StartsWith(_rootPrefix, _pathComparison))
            {
                return Result<string>.Fail(
                    "Command workspace path resolves outside the configured root.");
            }

            return Result<string>.Ok(Path.Combine(directory, fileName));
        }
        catch (Exception ex) when (
            ex is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return Result<string>.Fail(
                $"Command workspace path is invalid: {ex.Message}");
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
                        $"Command workspace path contains a reparse-point directory: {current}");
                }
            }
        }

        if ((File.Exists(targetPath) || Directory.Exists(targetPath))
            && IsReparsePoint(targetPath))
        {
            return Result.Fail(
                $"Command workspace target is a reparse point: {targetPath}");
        }

        return Result.Ok();
    }

    private Result<FileStream> AcquireWriteLock(
        string lockPath,
        string label)
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
            $"The {label} write lock could not be acquired after {_options.LockRetryAttempts} attempts: {lastError?.Message}");
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

    private static CommandTimelineLoadSnapshot TimelineSnapshot(
        InspectedDocument<CommandTimelineDocument> inspected,
        CommandWorkspaceLoadSource source,
        IReadOnlyList<CommandWorkspaceStoreNotice> notices) => new(
        inspected.Document,
        inspected.SchemaVersion,
        inspected.Status,
        source,
        notices);

    private static PlaybackCommandProjectionLoadSnapshot ProjectionSnapshot(
        InspectedDocument<PlaybackCommandProjection> inspected,
        CommandWorkspaceLoadSource source,
        IReadOnlyList<CommandWorkspaceStoreNotice> notices) => new(
        inspected.Document,
        inspected.SchemaVersion,
        inspected.Status,
        source,
        notices);

    private static CommandWorkspaceStoreNotice RecoveryNotice(
        bool primaryExists,
        string label) => new(
        primaryExists ? "PrimaryRejected" : "PrimaryMissing",
        primaryExists
            ? $"The primary command {label} was unreadable, malformed, stale, or non-reproducible; the previous validated backup was loaded."
            : $"The primary command {label} is missing; the previous validated backup was loaded.");

    private static bool TimelinesEqual(
        CommandTimelineDocument expected,
        CommandTimelineDocument actual)
    {
        if (expected.SchemaVersion != actual.SchemaVersion
            || !string.Equals(expected.WorkspaceId, actual.WorkspaceId, StringComparison.Ordinal)
            || !string.Equals(expected.ProjectPathKey, actual.ProjectPathKey, StringComparison.Ordinal)
            || !string.Equals(expected.ProjectRevisionSha256, actual.ProjectRevisionSha256, StringComparison.Ordinal)
            || expected.Entries.Count != actual.Entries.Count)
        {
            return false;
        }

        for (int entryIndex = 0; entryIndex < expected.Entries.Count; entryIndex++)
        {
            CommandTimelineEntry left = expected.Entries[entryIndex];
            CommandTimelineEntry right = actual.Entries[entryIndex];
            if (!SceneKeysEqual(left.Scene, right.Scene)
                || left.Commands.Count != right.Commands.Count)
            {
                return false;
            }

            for (int commandIndex = 0; commandIndex < left.Commands.Count; commandIndex++)
            {
                if (left.Commands[commandIndex] != right.Commands[commandIndex])
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool ProjectionsEqual(
        PlaybackCommandProjection expected,
        PlaybackCommandProjection actual)
    {
        if (expected.SchemaVersion != actual.SchemaVersion
            || !string.Equals(expected.WorkspaceId, actual.WorkspaceId, StringComparison.Ordinal)
            || !string.Equals(expected.SourceTimelineSha256, actual.SourceTimelineSha256, StringComparison.Ordinal)
            || !string.Equals(expected.ProjectPathKey, actual.ProjectPathKey, StringComparison.Ordinal)
            || !string.Equals(expected.ProjectRevisionSha256, actual.ProjectRevisionSha256, StringComparison.Ordinal)
            || !string.Equals(expected.PlaybackPathKey, actual.PlaybackPathKey, StringComparison.Ordinal)
            || !string.Equals(expected.PlaybackRevisionSha256, actual.PlaybackRevisionSha256, StringComparison.Ordinal)
            || !string.Equals(expected.PlaybackSchemaName, actual.PlaybackSchemaName, StringComparison.Ordinal)
            || expected.Batches.Count != actual.Batches.Count)
        {
            return false;
        }

        for (int batchIndex = 0; batchIndex < expected.Batches.Count; batchIndex++)
        {
            PlaybackCommandBatch left = expected.Batches[batchIndex];
            PlaybackCommandBatch right = actual.Batches[batchIndex];
            if (!SceneKeysEqual(left.Scene, right.Scene)
                || left.PlaybackRecordIndex != right.PlaybackRecordIndex
                || !string.Equals(
                    left.PlaybackRecordFingerprint,
                    right.PlaybackRecordFingerprint,
                    StringComparison.Ordinal)
                || left.CompiledScript != right.CompiledScript
                || left.Commands.Count != right.Commands.Count)
            {
                return false;
            }

            for (int commandIndex = 0; commandIndex < left.Commands.Count; commandIndex++)
            {
                if (left.Commands[commandIndex] != right.Commands[commandIndex])
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool SceneKeysEqual(SceneKey left, SceneKey right) =>
        left.SceneIndex == right.SceneIndex
        && string.Equals(left.NodeGuid, right.NodeGuid, StringComparison.Ordinal)
        && string.Equals(left.Fingerprint, right.Fingerprint, StringComparison.Ordinal);

    private static bool IsSceneKey(SceneKey key) =>
        Guid.TryParseExact(key.NodeGuid, "D", out _)
        && key.SceneIndex >= 0
        && IsSha256(key.Fingerprint);

    private static bool IsBoundedIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && value.All(character =>
            character is >= '0' and <= '9'
            or >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or '.' or '-' or '_' or '/');

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool SameSha(string? left, string? right) =>
        IsSha256(left)
        && IsSha256(right)
        && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string Location(SceneKey scene) =>
        $"{scene.NodeGuid.Trim().ToLowerInvariant()}:{scene.SceneIndex}";

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

    private static void ValidateOptions(JsonCommandWorkspaceStoreOptions options)
    {
        if (options.MaxFileSizeBytes <= 0
            || options.MaxFileSizeBytes > int.MaxValue
            || options.MaxJsonDepth <= 0
            || options.MaxEntries <= 0
            || options.MaxCommands <= 0
            || options.MaxDirectiveCharacters <= 0
            || options.MaxSchemaNameCharacters <= 0
            || options.LockRetryAttempts <= 0
            || options.LockRetryDelayMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Command workspace limits and retry settings are invalid.");
        }
    }

    private sealed record InspectedDocument<T>(
        T? Document,
        int SchemaVersion,
        CommandWorkspaceDocumentStatus Status)
        where T : class;
}
