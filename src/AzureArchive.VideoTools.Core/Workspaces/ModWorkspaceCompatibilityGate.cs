using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Workspaces;

public sealed class ModWorkspaceCompatibilityGate : IModWorkspaceCompatibilityGate
{
    public Result<ModWorkspaceCompatibilitySnapshot> Evaluate(
        ModWorkspaceManifest manifest,
        ModWorkspaceRuntimeContext context)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(context);

        Result manifestValidation = ValidateManifest(manifest);
        if (!manifestValidation.Success)
        {
            return Result<ModWorkspaceCompatibilitySnapshot>.Fail(
                manifestValidation.Error);
        }

        Result contextValidation = ValidateContext(context);
        if (!contextValidation.Success)
        {
            return Result<ModWorkspaceCompatibilitySnapshot>.Fail(
                contextValidation.Error);
        }

        if (manifest.SchemaVersion > ModWorkspaceManifest.CurrentSchemaVersion)
        {
            return Result<ModWorkspaceCompatibilitySnapshot>.Ok(Blocked(
                ModWorkspaceCompatibilityStatus.FutureSchema,
                $"Workspace schema {manifest.SchemaVersion} is newer than supported schema {ModWorkspaceManifest.CurrentSchemaVersion}."));
        }

        if (manifest.SchemaVersion < ModWorkspaceManifest.OldestSupportedSchemaVersion)
        {
            return Result<ModWorkspaceCompatibilitySnapshot>.Ok(Blocked(
                ModWorkspaceCompatibilityStatus.UnsupportedLegacySchema,
                $"Workspace schema {manifest.SchemaVersion} is older than the oldest supported schema {ModWorkspaceManifest.OldestSupportedSchemaVersion}."));
        }

        if (manifest.SchemaVersion < ModWorkspaceManifest.CurrentSchemaVersion)
        {
            return Result<ModWorkspaceCompatibilitySnapshot>.Ok(Blocked(
                ModWorkspaceCompatibilityStatus.RequiresMigration,
                $"Workspace schema {manifest.SchemaVersion} must be migrated to schema {ModWorkspaceManifest.CurrentSchemaVersion} before use."));
        }

        ParsedPluginVersion currentVersion = ParsePluginVersion(context.PluginVersion);
        ParsedPluginVersion minimumVersion = ParsePluginVersion(
            manifest.MinimumPluginVersion);
        if (currentVersion.CompareTo(minimumVersion) < 0)
        {
            return Result<ModWorkspaceCompatibilitySnapshot>.Ok(Blocked(
                ModWorkspaceCompatibilityStatus.PluginTooOld,
                $"Plugin {context.PluginVersion} is older than the workspace minimum {manifest.MinimumPluginVersion}."));
        }

        string[] missingCapabilities = manifest.RequiredCapabilities
            .Where(required => !context.AvailableCapabilities.Contains(required))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (missingCapabilities.Length != 0)
        {
            return Result<ModWorkspaceCompatibilitySnapshot>.Ok(new(
                ModWorkspaceCompatibilityStatus.MissingCapabilities,
                CanReadMetadata: true,
                CanWrite: false,
                CanExecute: false,
                "The workspace requires capabilities that are unavailable in this plugin build.",
                Array.AsReadOnly(missingCapabilities)));
        }

        if (!SourceMatches(manifest.Source, context.CurrentSource))
        {
            return Result<ModWorkspaceCompatibilitySnapshot>.Ok(Blocked(
                ModWorkspaceCompatibilityStatus.SourceMismatch,
                "The workspace source hashes do not match the selected AAP/AAS pair."));
        }

        return Result<ModWorkspaceCompatibilitySnapshot>.Ok(new(
            ModWorkspaceCompatibilityStatus.Ready,
            CanReadMetadata: true,
            CanWrite: true,
            CanExecute: true,
            "Workspace schema, plugin version, capabilities, and source hashes are compatible.",
            Array.Empty<string>()));
    }

    public static Result ValidateManifest(ModWorkspaceManifest manifest)
    {
        if (manifest.SchemaVersion <= 0)
        {
            return Result.Fail("Workspace schema version must be positive.");
        }

        if (!Guid.TryParseExact(manifest.WorkspaceId, "D", out _))
        {
            return Result.Fail("Workspace ID must be a canonical UUID.");
        }

        if (!TryParsePluginVersion(manifest.CreatedWithPluginVersion, out _))
        {
            return Result.Fail(
                "Workspace creator version must use canonical major.minor.patch form.");
        }

        if (!TryParsePluginVersion(manifest.MinimumPluginVersion, out _))
        {
            return Result.Fail(
                "Workspace minimum plugin version must use canonical major.minor.patch form.");
        }

        if (ParsePluginVersion(manifest.CreatedWithPluginVersion).CompareTo(
                ParsePluginVersion(manifest.MinimumPluginVersion)) < 0)
        {
            return Result.Fail(
                "Workspace creator version cannot be older than its minimum plugin version.");
        }

        Result sourceValidation = ValidateSource(manifest.Source, "workspace");
        if (!sourceValidation.Success)
        {
            return sourceValidation;
        }

        if (manifest.RequiredCapabilities == null)
        {
            return Result.Fail("Workspace required capabilities collection is missing.");
        }

        var capabilities = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < manifest.RequiredCapabilities.Count; index++)
        {
            string? capability = manifest.RequiredCapabilities[index];
            if (!IsCapabilityId(capability))
            {
                return Result.Fail(
                    $"Workspace capability {index} is not a valid capability ID.");
            }

            if (!capabilities.Add(capability))
            {
                return Result.Fail(
                    $"Workspace contains duplicate capability '{capability}'.");
            }
        }

        return Result.Ok();
    }

    public static Result ValidateContext(ModWorkspaceRuntimeContext context)
    {
        if (!TryParsePluginVersion(context.PluginVersion, out _))
        {
            return Result.Fail(
                "Current plugin version must use canonical major.minor.patch form.");
        }

        Result sourceValidation = ValidateSource(context.CurrentSource, "current");
        if (!sourceValidation.Success)
        {
            return sourceValidation;
        }

        if (context.AvailableCapabilities == null)
        {
            return Result.Fail("Current capability set is missing.");
        }

        foreach (string capability in context.AvailableCapabilities)
        {
            if (!IsCapabilityId(capability))
            {
                return Result.Fail(
                    $"Current capability '{capability}' is not a valid capability ID.");
            }
        }

        return Result.Ok();
    }

    private static Result ValidateSource(
        ModWorkspaceSourceBinding? source,
        string label)
    {
        if (source == null)
        {
            return Result.Fail($"The {label} source binding is missing.");
        }

        if (!IsSha256(source.ProjectPathKey))
        {
            return Result.Fail(
                $"The {label} project path key must be a SHA-256 value.");
        }

        if (!IsSha256(source.ProjectRevisionSha256))
        {
            return Result.Fail(
                $"The {label} project revision must be a SHA-256 value.");
        }

        if (!IsSha256(source.PlaybackPathKey))
        {
            return Result.Fail(
                $"The {label} playback path key must be a SHA-256 value.");
        }

        if (!IsSha256(source.PlaybackRevisionSha256))
        {
            return Result.Fail(
                $"The {label} playback revision must be a SHA-256 value.");
        }

        if (string.IsNullOrWhiteSpace(source.PlaybackSchemaName)
            || source.PlaybackSchemaName.Length > 128
            || source.PlaybackSchemaName.Any(char.IsControl))
        {
            return Result.Fail(
                $"The {label} playback schema name is invalid.");
        }

        return Result.Ok();
    }

    private static bool SourceMatches(
        ModWorkspaceSourceBinding expected,
        ModWorkspaceSourceBinding actual) =>
        string.Equals(
            expected.ProjectPathKey,
            actual.ProjectPathKey,
            StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            expected.ProjectRevisionSha256,
            actual.ProjectRevisionSha256,
            StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            expected.PlaybackPathKey,
            actual.PlaybackPathKey,
            StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            expected.PlaybackRevisionSha256,
            actual.PlaybackRevisionSha256,
            StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            expected.PlaybackSchemaName,
            actual.PlaybackSchemaName,
            StringComparison.Ordinal);

    private static ModWorkspaceCompatibilitySnapshot Blocked(
        ModWorkspaceCompatibilityStatus status,
        string message) => new(
        status,
        CanReadMetadata: true,
        CanWrite: false,
        CanExecute: false,
        message,
        Array.Empty<string>());

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool IsCapabilityId(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && value.All(character =>
            IsAsciiLetterOrDigit(character)
            || character is '.' or '-' or '_');

    private static ParsedPluginVersion ParsePluginVersion(string value)
    {
        if (!TryParsePluginVersion(value, out ParsedPluginVersion parsed))
        {
            throw new InvalidOperationException(
                $"Plugin version '{value}' was not validated before comparison.");
        }

        return parsed;
    }

    private static bool TryParsePluginVersion(
        string? value,
        out ParsedPluginVersion parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string[] segments = value.Split('.');
        if (segments.Length != 3
            || !TryParseVersionPart(segments[0], out int major)
            || !TryParseVersionPart(segments[1], out int minor)
            || !TryParseVersionPart(segments[2], out int patch))
        {
            return false;
        }

        parsed = new ParsedPluginVersion(major, minor, patch);
        return true;
    }

    private static bool TryParseVersionPart(string value, out int result)
    {
        result = 0;
        return value.Length > 0
            && (value.Length == 1 || value[0] != '0')
            && value.All(character => character is >= '0' and <= '9')
            && int.TryParse(value, out result);
    }

    private static bool IsAsciiLetterOrDigit(char value) =>
        value is >= '0' and <= '9'
        or >= 'A' and <= 'Z'
        or >= 'a' and <= 'z';

    private readonly record struct ParsedPluginVersion(
        int Major,
        int Minor,
        int Patch) : IComparable<ParsedPluginVersion>
    {
        public int CompareTo(ParsedPluginVersion other)
        {
            int major = Major.CompareTo(other.Major);
            if (major != 0)
            {
                return major;
            }

            int minor = Minor.CompareTo(other.Minor);
            return minor != 0 ? minor : Patch.CompareTo(other.Patch);
        }
    }
}
