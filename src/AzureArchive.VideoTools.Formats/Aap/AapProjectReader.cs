using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Formats.Aap;

public sealed class AapProjectReader : IProjectReader
{
    private readonly AapProjectReaderOptions _options;

    public AapProjectReader(AapProjectReaderOptions? options = null)
    {
        _options = options ?? new AapProjectReaderOptions();
        if (_options.MaxFileSizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Maximum file size must be positive.");
        }

        if (_options.MaxJsonDepth <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Maximum JSON depth must be positive.");
        }

        if (_options.StableReadAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Stable read attempts must be positive.");
        }
    }

    public Result<ProjectSnapshot> Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Result<ProjectSnapshot>.Fail("AAP path is empty.");
        }

        try
        {
            string fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
            {
                return Result<ProjectSnapshot>.Fail($"AAP file does not exist: {fullPath}");
            }

            Result<StableFileSnapshot> fileResult = ReadStable(fullPath);
            if (!fileResult.Success || fileResult.Value == null)
            {
                return Result<ProjectSnapshot>.Fail(fileResult.Error);
            }

            StableFileSnapshot file = fileResult.Value;
            using JsonDocument document = JsonDocument.Parse(
                file.Bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = _options.MaxJsonDepth
                });

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Result<ProjectSnapshot>.Fail("AAP root must be a JSON object.");
            }

            return ParseProject(fullPath, file, document.RootElement);
        }
        catch (JsonException ex)
        {
            return Result<ProjectSnapshot>.Fail($"AAP JSON is invalid: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Result<ProjectSnapshot>.Fail($"AAP file cannot be accessed: {ex.Message}");
        }
        catch (IOException ex)
        {
            return Result<ProjectSnapshot>.Fail($"AAP file could not be read: {ex.Message}");
        }
        catch (Exception ex)
        {
            return Result<ProjectSnapshot>.Fail(
                $"Unexpected AAP read failure ({ex.GetType().Name}): {ex.Message}");
        }
    }

    private Result<ProjectSnapshot> ParseProject(
        string fullPath,
        StableFileSnapshot file,
        JsonElement root)
    {
        var diagnostics = new List<ProjectDiagnostic>();
        if (!TryGetCollection(root, "nodes", out JsonElement.ArrayEnumerator nodeValues))
        {
            return Result<ProjectSnapshot>.Fail(
                "AAP project does not contain nodes.$values or a nodes array.");
        }

        var nodes = new List<StoryNodeSnapshot>();
        int sourceIndex = 0;
        foreach (JsonElement nodeElement in nodeValues)
        {
            if (nodeElement.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(new ProjectDiagnostic(
                    ProjectDiagnosticSeverity.Error,
                    "InvalidNode",
                    $"Node at source index {sourceIndex} is not an object."));
                sourceIndex++;
                continue;
            }

            nodes.Add(ParseNode(nodeElement, sourceIndex, diagnostics));
            sourceIndex++;
        }

        AddDuplicateGuidDiagnostics(nodes, diagnostics);

        string projectName = GetString(root, "ProjectName");
        if (string.IsNullOrWhiteSpace(projectName))
        {
            diagnostics.Add(new ProjectDiagnostic(
                ProjectDiagnosticSeverity.Warning,
                "MissingProjectName",
                "ProjectName is empty."));
        }

        var source = new ProjectSourceSnapshot(
            fullPath,
            ComputePathKey(fullPath),
            Convert.ToHexString(SHA256.HashData(file.Bytes)),
            file.Bytes.LongLength,
            file.LastWriteTimeUtc);

        var preview = new ProjectPreviewSnapshot(
            GetNullableInt64(root, "PreviewBgName"),
            GetString(root, "PreviewHeader"),
            GetString(root, "PreviewTitle"));

        return Result<ProjectSnapshot>.Ok(new ProjectSnapshot(
            source,
            GetString(root, "$type"),
            projectName,
            preview,
            nodes.AsReadOnly(),
            diagnostics.AsReadOnly()));
    }

    private static StoryNodeSnapshot ParseNode(
        JsonElement node,
        int sourceIndex,
        List<ProjectDiagnostic> diagnostics)
    {
        string sourceType = GetString(node, "$type");
        StoryNodeKind kind = ParseNodeKind(sourceType);
        string rawGuid = GetString(node, "Guid").Trim();
        bool hasPersistentGuid = Guid.TryParse(rawGuid, out Guid parsedGuid);
        string nodeGuid = hasPersistentGuid
            ? parsedGuid.ToString("D")
            : $"__unidentified_node_{sourceIndex}";

        if (!hasPersistentGuid && kind == StoryNodeKind.Script)
        {
            diagnostics.Add(new ProjectDiagnostic(
                ProjectDiagnosticSeverity.Error,
                "MissingScriptNodeGuid",
                "Script node has no valid persistent Guid.",
                nodeGuid));
        }

        string name = FirstNonEmpty(
            GetString(node, "NodeName"),
            GetString(node, "Title"),
            GetString(node, "Header"));

        IReadOnlyList<string> connections = ParseStringCollection(node, "ConnectionsTo");
        IReadOnlyList<SceneSnapshot> scenes = kind == StoryNodeKind.Script
            ? ParseScenes(node, nodeGuid, diagnostics)
            : Array.Empty<SceneSnapshot>();

        return new StoryNodeSnapshot(
            sourceIndex,
            kind,
            sourceType,
            nodeGuid,
            hasPersistentGuid,
            name,
            connections,
            scenes);
    }

    private static IReadOnlyList<SceneSnapshot> ParseScenes(
        JsonElement node,
        string nodeGuid,
        List<ProjectDiagnostic> diagnostics)
    {
        if (!TryGetCollection(node, "Scripts", out JsonElement.ArrayEnumerator scripts)
            && !TryGetCollection(node, "scripts", out scripts))
        {
            diagnostics.Add(new ProjectDiagnostic(
                ProjectDiagnosticSeverity.Warning,
                "MissingScripts",
                "Script node has no Scripts collection.",
                nodeGuid));
            return Array.Empty<SceneSnapshot>();
        }

        var scenes = new List<SceneSnapshot>();
        int sceneIndex = 0;
        foreach (JsonElement script in scripts)
        {
            if (script.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(new ProjectDiagnostic(
                    ProjectDiagnosticSeverity.Error,
                    "InvalidScene",
                    $"Scene {sceneIndex} is not an object.",
                    nodeGuid,
                    sceneIndex));
                sceneIndex++;
                continue;
            }

            string fingerprint = CanonicalJsonFingerprint.Compute(script);
            var key = new SceneKey(nodeGuid, sceneIndex, fingerprint);
            scenes.Add(new SceneSnapshot(
                key,
                GetString(script, "$type"),
                GetString(script, "text"),
                GetBoolean(script, "isDialogScript", defaultValue: true),
                GetString(script, "popup"),
                GetUInt32(script, "bgEffect"),
                GetUInt32(script, "bgName"),
                GetString(script, "additionalPrompt"),
                GetString(script, "placeText"),
                GetString(script, "bgFriendlyName"),
                GetString(script, "sound"),
                GetString(script, "voice"),
                GetUInt32(script, "transition"),
                GetInt64(script, "bgmId"),
                GetInt64(script, "selectionGroup"))
            {
                SpeakerSlot = GetInt32(script, "speakerSlotNum"),
                HighlightedSlots = ParseInt32Collection(
                    script,
                    "highlightedSlotNums"),
                Characters = ParseCharacters(script)
            });
            sceneIndex++;
        }

        return scenes.AsReadOnly();
    }

    private Result<StableFileSnapshot> ReadStable(string fullPath)
    {
        for (int attempt = 1; attempt <= _options.StableReadAttempts; attempt++)
        {
            var before = new FileInfo(fullPath);
            before.Refresh();
            if (before.Length > _options.MaxFileSizeBytes)
            {
                return Result<StableFileSnapshot>.Fail(
                    $"AAP file is {before.Length} bytes, exceeding the {_options.MaxFileSizeBytes}-byte read limit.");
            }

            byte[] bytes;
            using (var stream = new FileStream(
                       fullPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            {
                using var buffer = new MemoryStream(
                    before.Length <= int.MaxValue ? (int)before.Length : 0);
                stream.CopyTo(buffer);
                bytes = buffer.ToArray();
            }

            var after = new FileInfo(fullPath);
            after.Refresh();
            if (before.Length == after.Length
                && before.LastWriteTimeUtc == after.LastWriteTimeUtc
                && bytes.LongLength == after.Length)
            {
                return Result<StableFileSnapshot>.Ok(new StableFileSnapshot(
                    bytes,
                    new DateTimeOffset(after.LastWriteTimeUtc, TimeSpan.Zero)));
            }
        }

        return Result<StableFileSnapshot>.Fail(
            "AAP file changed while it was being read. Save the project and retry.");
    }

    private static bool TryGetCollection(
        JsonElement owner,
        string propertyName,
        out JsonElement.ArrayEnumerator values)
    {
        values = default;
        if (!owner.TryGetProperty(propertyName, out JsonElement collection))
        {
            return false;
        }

        if (collection.ValueKind == JsonValueKind.Array)
        {
            values = collection.EnumerateArray();
            return true;
        }

        if (collection.ValueKind == JsonValueKind.Object
            && collection.TryGetProperty("$values", out JsonElement wrapped)
            && wrapped.ValueKind == JsonValueKind.Array)
        {
            values = wrapped.EnumerateArray();
            return true;
        }

        return false;
    }

    private static IReadOnlyList<string> ParseStringCollection(
        JsonElement owner,
        string propertyName)
    {
        if (!TryGetCollection(owner, propertyName, out JsonElement.ArrayEnumerator values))
        {
            return Array.Empty<string>();
        }

        string[] result = values
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString() ?? string.Empty)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        return Array.AsReadOnly(result);
    }

    private static IReadOnlyList<int> ParseInt32Collection(
        JsonElement owner,
        string propertyName)
    {
        if (!TryGetCollection(owner, propertyName, out JsonElement.ArrayEnumerator values))
        {
            return Array.Empty<int>();
        }

        int[] result = values
            .Where(value => value.ValueKind == JsonValueKind.Number)
            .Select(value => value.TryGetInt32(out int number) ? number : 0)
            .ToArray();
        return Array.AsReadOnly(result);
    }

    private static IReadOnlyList<ProjectCharacterSnapshot> ParseCharacters(
        JsonElement script)
    {
        if (!TryGetCollection(script, "characters", out JsonElement.ArrayEnumerator values))
        {
            return Array.Empty<ProjectCharacterSnapshot>();
        }

        var result = new List<ProjectCharacterSnapshot>();
        int sourceIndex = 0;
        foreach (JsonElement character in values)
        {
            if (sourceIndex is >= 1 and <= 5
                && character.ValueKind == JsonValueKind.Object)
            {
                result.Add(new ProjectCharacterSnapshot(
                    sourceIndex,
                    GetString(character, "name"),
                    GetString(character, "faceId"),
                    GetInt32(character, "startingPos"),
                    GetInt32(character, "endingPos"),
                    GetInt32(character, "emoticon"),
                    GetInt32(character, "action"),
                    GetInt32(character, "effect"),
                    GetInt32(character, "appear"),
                    GetInt32(character, "shapeOverride")));
            }

            sourceIndex++;
        }

        return result.AsReadOnly();
    }

    private static StoryNodeKind ParseNodeKind(string sourceType)
    {
        string shortType = sourceType.Split(',')[0].Trim();
        return shortType switch
        {
            "EntryNodeData" => StoryNodeKind.Entry,
            "ScriptNodeData" => StoryNodeKind.Script,
            "SelectionNodeData" => StoryNodeKind.Selection,
            "ExitNodeData" => StoryNodeKind.Exit,
            _ => StoryNodeKind.Unknown
        };
    }

    private static void AddDuplicateGuidDiagnostics(
        IEnumerable<StoryNodeSnapshot> nodes,
        List<ProjectDiagnostic> diagnostics)
    {
        foreach (IGrouping<string, StoryNodeSnapshot> duplicate in nodes
                     .Where(node => node.HasPersistentGuid)
                     .GroupBy(node => node.NodeGuid, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            diagnostics.Add(new ProjectDiagnostic(
                ProjectDiagnosticSeverity.Error,
                "DuplicateNodeGuid",
                $"Node Guid {duplicate.Key} appears {duplicate.Count()} times.",
                duplicate.Key));
        }
    }

    private static string GetString(JsonElement owner, string propertyName)
    {
        if (!owner.TryGetProperty(propertyName, out JsonElement value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Null => string.Empty,
            _ => value.GetRawText()
        };
    }

    private static bool GetBoolean(
        JsonElement owner,
        string propertyName,
        bool defaultValue)
    {
        if (!owner.TryGetProperty(propertyName, out JsonElement value))
        {
            return defaultValue;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => defaultValue
        };
    }

    private static long? GetNullableInt64(JsonElement owner, string propertyName)
    {
        if (!owner.TryGetProperty(propertyName, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String
            && long.TryParse(value.GetString(), out number))
        {
            return number;
        }

        return null;
    }

    private static long GetInt64(JsonElement owner, string propertyName) =>
        GetNullableInt64(owner, propertyName) ?? 0L;

    private static int GetInt32(JsonElement owner, string propertyName)
    {
        if (!owner.TryGetProperty(propertyName, out JsonElement value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), out number))
        {
            return number;
        }

        return 0;
    }

    private static uint GetUInt32(JsonElement owner, string propertyName)
    {
        if (!owner.TryGetProperty(propertyName, out JsonElement value))
        {
            return 0U;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out uint number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String
            && uint.TryParse(value.GetString(), out number))
        {
            return number;
        }

        return 0U;
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static string ComputePathKey(string fullPath)
    {
        string normalized = Path.GetFullPath(fullPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

        if (OperatingSystem.IsWindows())
        {
            normalized = normalized.ToUpperInvariant();
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    private sealed record StableFileSnapshot(
        byte[] Bytes,
        DateTimeOffset LastWriteTimeUtc);
}
