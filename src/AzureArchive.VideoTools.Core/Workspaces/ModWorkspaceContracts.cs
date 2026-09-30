using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Workspaces;

public sealed record ModWorkspaceSourceBinding(
    string ProjectPathKey,
    string ProjectRevisionSha256,
    string PlaybackPathKey,
    string PlaybackRevisionSha256,
    string PlaybackSchemaName);

public sealed record ModWorkspaceManifest(
    int SchemaVersion,
    string WorkspaceId,
    string CreatedWithPluginVersion,
    string MinimumPluginVersion,
    ModWorkspaceSourceBinding Source,
    IReadOnlyList<string> RequiredCapabilities)
{
    public const int CurrentSchemaVersion = 1;
    public const int OldestSupportedSchemaVersion = 1;
}

public sealed record ModWorkspaceRuntimeContext(
    string PluginVersion,
    ModWorkspaceSourceBinding CurrentSource,
    IReadOnlySet<string> AvailableCapabilities);

public enum ModWorkspaceCompatibilityStatus
{
    Ready = 0,
    NotFound = 1,
    RequiresMigration = 2,
    FutureSchema = 3,
    UnsupportedLegacySchema = 4,
    PluginTooOld = 5,
    MissingCapabilities = 6,
    SourceMismatch = 7,
    Invalid = 8
}

public sealed record ModWorkspaceCompatibilitySnapshot(
    ModWorkspaceCompatibilityStatus Status,
    bool CanReadMetadata,
    bool CanWrite,
    bool CanExecute,
    string Message,
    IReadOnlyList<string> MissingCapabilities)
{
    public static ModWorkspaceCompatibilitySnapshot NotFound() => new(
        ModWorkspaceCompatibilityStatus.NotFound,
        CanReadMetadata: false,
        CanWrite: false,
        CanExecute: false,
        "No Mod workspace exists for the selected project.",
        Array.Empty<string>());
}

public enum ModWorkspaceLoadSource
{
    None = 0,
    Primary = 1,
    Backup = 2
}

public sealed record ModWorkspaceStoreNotice(string Code, string Message);

public sealed record ModWorkspaceLoadSnapshot(
    ModWorkspaceManifest? Manifest,
    int DetectedSchemaVersion,
    ModWorkspaceLoadSource Source,
    ModWorkspaceCompatibilitySnapshot Compatibility,
    IReadOnlyList<ModWorkspaceStoreNotice> Notices)
{
    public bool RecoveredFromBackup => Source == ModWorkspaceLoadSource.Backup;
}

public sealed record ModWorkspaceSaveSnapshot(
    string WorkspaceDirectory,
    string ManifestPath,
    bool ReplacedExisting,
    bool BackupCreated);

public interface IModWorkspaceCompatibilityGate
{
    Result<ModWorkspaceCompatibilitySnapshot> Evaluate(
        ModWorkspaceManifest manifest,
        ModWorkspaceRuntimeContext context);
}

public interface IModWorkspaceStore
{
    Result<ModWorkspaceLoadSnapshot> TryLoad(
        string projectPathKey,
        ModWorkspaceRuntimeContext context);

    Result<ModWorkspaceSaveSnapshot> SaveAtomic(
        string projectPathKey,
        ModWorkspaceManifest manifest,
        ModWorkspaceRuntimeContext context);
}

public interface IModWorkspaceMigration
{
    int SourceSchemaVersion { get; }

    int TargetSchemaVersion { get; }

    Result<ModWorkspaceManifest> Migrate(ModWorkspaceManifest source);
}
