using AzureArchive.VideoTools.Core.Results;
using AzureArchive.VideoTools.Core.Workspaces;

namespace AzureArchive.VideoTools.Tests;

internal static class ModWorkspaceCompatibilityTests
{
    public static void ReadyRequiresExactSourcesAndCapabilities()
    {
        var gate = new ModWorkspaceCompatibilityGate();
        Result<ModWorkspaceCompatibilitySnapshot> result = gate.Evaluate(
            Manifest(),
            Context());

        AssertEx.True(result.Success, result.Error);
        ModWorkspaceCompatibilitySnapshot snapshot = AssertEx.NotNull(result.Value);
        AssertEx.Equal(ModWorkspaceCompatibilityStatus.Ready, snapshot.Status);
        AssertEx.True(snapshot.CanReadMetadata);
        AssertEx.True(snapshot.CanWrite);
        AssertEx.True(snapshot.CanExecute);
    }

    public static void OlderPluginAndMissingCapabilitiesFailClosed()
    {
        var gate = new ModWorkspaceCompatibilityGate();

        ModWorkspaceRuntimeContext oldPlugin = Context() with
        {
            PluginVersion = "0.7.16"
        };
        ModWorkspaceCompatibilitySnapshot oldResult = AssertEx.NotNull(
            gate.Evaluate(Manifest(), oldPlugin).Value);
        AssertEx.Equal(ModWorkspaceCompatibilityStatus.PluginTooOld, oldResult.Status);
        AssertEx.False(oldResult.CanWrite);
        AssertEx.False(oldResult.CanExecute);

        ModWorkspaceRuntimeContext missingCapability = Context() with
        {
            AvailableCapabilities = new HashSet<string>(StringComparer.Ordinal)
            {
                "Player.CharacterTransform"
            }
        };
        ModWorkspaceCompatibilitySnapshot capabilityResult = AssertEx.NotNull(
            gate.Evaluate(Manifest(), missingCapability).Value);
        AssertEx.Equal(
            ModWorkspaceCompatibilityStatus.MissingCapabilities,
            capabilityResult.Status);
        AssertEx.Equal(1, capabilityResult.MissingCapabilities.Count);
        AssertEx.Equal(
            "Player.CharacterTransform.Dispatch",
            capabilityResult.MissingCapabilities[0]);
    }

    public static void SourceRevisionDriftFailsClosed()
    {
        var gate = new ModWorkspaceCompatibilityGate();
        ModWorkspaceRuntimeContext changedPlayback = Context() with
        {
            CurrentSource = Source() with
            {
                PlaybackRevisionSha256 = new string('F', 64)
            }
        };

        ModWorkspaceCompatibilitySnapshot result = AssertEx.NotNull(
            gate.Evaluate(Manifest(), changedPlayback).Value);
        AssertEx.Equal(ModWorkspaceCompatibilityStatus.SourceMismatch, result.Status);
        AssertEx.True(result.CanReadMetadata);
        AssertEx.False(result.CanWrite);
        AssertEx.False(result.CanExecute);
    }

    public static void FutureSchemaIsReadableMetadataButNeverExecutable()
    {
        var gate = new ModWorkspaceCompatibilityGate();
        ModWorkspaceManifest future = Manifest() with
        {
            SchemaVersion = ModWorkspaceManifest.CurrentSchemaVersion + 1
        };

        ModWorkspaceCompatibilitySnapshot result = AssertEx.NotNull(
            gate.Evaluate(future, Context()).Value);
        AssertEx.Equal(ModWorkspaceCompatibilityStatus.FutureSchema, result.Status);
        AssertEx.True(result.CanReadMetadata);
        AssertEx.False(result.CanWrite);
        AssertEx.False(result.CanExecute);
    }

    public static void RejectsNonCanonicalVersionsAndDuplicateCapabilities()
    {
        var gate = new ModWorkspaceCompatibilityGate();
        AssertEx.False(gate.Evaluate(
            Manifest() with { MinimumPluginVersion = "0.07.17" },
            Context()).Success);
        AssertEx.False(gate.Evaluate(
            Manifest() with
            {
                RequiredCapabilities = new[]
                {
                    "Player.CharacterTransform",
                    "Player.CharacterTransform"
                }
            },
            Context()).Success);
    }

    public static void MigrationPipelinePreservesImmutableIdentity()
    {
        ModWorkspaceManifest source = Manifest();
        var pipeline = new ModWorkspaceMigrationPipeline(new[]
        {
            new TestMigration(changeIdentity: false)
        });

        Result<ModWorkspaceManifest> migrated = pipeline.Migrate(source, 2);
        AssertEx.True(migrated.Success, migrated.Error);
        ModWorkspaceManifest result = AssertEx.NotNull(migrated.Value);
        AssertEx.Equal(2, result.SchemaVersion);
        AssertEx.Equal(source.WorkspaceId, result.WorkspaceId);
        AssertEx.Equal(source.Source, result.Source);

        var unsafePipeline = new ModWorkspaceMigrationPipeline(new[]
        {
            new TestMigration(changeIdentity: true)
        });
        AssertEx.False(unsafePipeline.Migrate(source, 2).Success);
        AssertEx.False(
            new ModWorkspaceMigrationPipeline(Array.Empty<IModWorkspaceMigration>())
                .Migrate(source, 2)
                .Success);
    }

    internal static ModWorkspaceManifest Manifest() => new(
        ModWorkspaceManifest.CurrentSchemaVersion,
        "d673b794-354d-4ea7-a4f7-cb885b6020e8",
        "0.7.17",
        "0.7.17",
        Source(),
        new[]
        {
            "Player.CharacterTransform",
            "Player.CharacterTransform.Dispatch"
        });

    internal static ModWorkspaceRuntimeContext Context() => new(
        "0.7.17",
        Source(),
        new HashSet<string>(StringComparer.Ordinal)
        {
            "Player.CharacterTransform",
            "Player.CharacterTransform.Dispatch",
            "Player.CharacterTransform.Reset"
        });

    internal static ModWorkspaceSourceBinding Source() => new(
        new string('A', 64),
        new string('B', 64),
        new string('C', 64),
        new string('D', 64),
        "synthetic/v1");

    private sealed class TestMigration : IModWorkspaceMigration
    {
        private readonly bool _changeIdentity;

        public TestMigration(bool changeIdentity)
        {
            _changeIdentity = changeIdentity;
        }

        public int SourceSchemaVersion => 1;

        public int TargetSchemaVersion => 2;

        public Result<ModWorkspaceManifest> Migrate(ModWorkspaceManifest source) =>
            Result<ModWorkspaceManifest>.Ok(source with
            {
                SchemaVersion = TargetSchemaVersion,
                WorkspaceId = _changeIdentity
                    ? "82a91036-5996-4357-93c6-dfc5b7b91faf"
                    : source.WorkspaceId
            });
    }
}
