using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Workspaces;

public sealed class ModWorkspaceMigrationPipeline
{
    private readonly IReadOnlyDictionary<int, IModWorkspaceMigration> _migrations;

    public ModWorkspaceMigrationPipeline(IEnumerable<IModWorkspaceMigration> migrations)
    {
        ArgumentNullException.ThrowIfNull(migrations);

        var bySource = new Dictionary<int, IModWorkspaceMigration>();
        foreach (IModWorkspaceMigration migration in migrations)
        {
            ArgumentNullException.ThrowIfNull(migration);
            if (migration.SourceSchemaVersion <= 0
                || migration.TargetSchemaVersion != migration.SourceSchemaVersion + 1)
            {
                throw new ArgumentException(
                    "Workspace migrations must advance exactly one positive schema version.",
                    nameof(migrations));
            }

            if (!bySource.TryAdd(migration.SourceSchemaVersion, migration))
            {
                throw new ArgumentException(
                    $"Multiple workspace migrations start at schema {migration.SourceSchemaVersion}.",
                    nameof(migrations));
            }
        }

        _migrations = bySource;
    }

    public Result<ModWorkspaceManifest> Migrate(
        ModWorkspaceManifest source,
        int targetSchemaVersion = ModWorkspaceManifest.CurrentSchemaVersion)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (targetSchemaVersion < source.SchemaVersion)
        {
            return Result<ModWorkspaceManifest>.Fail(
                "Workspace migrations cannot downgrade a schema.");
        }

        ModWorkspaceManifest current = source;
        while (current.SchemaVersion < targetSchemaVersion)
        {
            if (!_migrations.TryGetValue(
                    current.SchemaVersion,
                    out IModWorkspaceMigration? migration))
            {
                return Result<ModWorkspaceManifest>.Fail(
                    $"No migration is registered for workspace schema {current.SchemaVersion}.");
            }

            Result<ModWorkspaceManifest> migrated = migration.Migrate(current);
            if (!migrated.Success || migrated.Value == null)
            {
                return Result<ModWorkspaceManifest>.Fail(
                    $"Workspace migration {migration.SourceSchemaVersion}->{migration.TargetSchemaVersion} failed: {migrated.Error}");
            }

            ModWorkspaceManifest next = migrated.Value;
            if (next.SchemaVersion != migration.TargetSchemaVersion)
            {
                return Result<ModWorkspaceManifest>.Fail(
                    "Workspace migration returned an unexpected schema version.");
            }

            if (!string.Equals(
                    current.WorkspaceId,
                    next.WorkspaceId,
                    StringComparison.Ordinal)
                || current.Source != next.Source)
            {
                return Result<ModWorkspaceManifest>.Fail(
                    "Workspace migration changed immutable workspace or source identity.");
            }

            current = next;
        }

        return Result<ModWorkspaceManifest>.Ok(current);
    }
}
