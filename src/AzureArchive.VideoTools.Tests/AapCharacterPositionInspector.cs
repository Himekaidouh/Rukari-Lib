using System.Text.Json;

namespace AzureArchive.VideoTools.Tests;

internal static class AapCharacterPositionInspector
{
    public static int Inspect(string path)
    {
        string fullPath = Path.GetFullPath(path);
        IEnumerable<string> files = Directory.Exists(fullPath)
            ? Directory.EnumerateFiles(fullPath, "*.aap", SearchOption.TopDirectoryOnly)
            : new[] { fullPath };

        int fileCount = 0;
        int characterCount = 0;
        int transitionCount = 0;

        foreach (string file in files.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(file))
            {
                Console.Error.WriteLine($"AAP file does not exist: {file}");
                return 1;
            }

            fileCount++;
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(file));
                if (!TryGetCollection(document.RootElement, "nodes", out JsonElement.ArrayEnumerator nodes))
                {
                    Console.Error.WriteLine($"SKIP file={Path.GetFileName(file)} reason=missing-nodes");
                    continue;
                }

                int nodeIndex = 0;
                foreach (JsonElement node in nodes)
                {
                    if (!TryGetCollection(node, "Scripts", out JsonElement.ArrayEnumerator scenes)
                        && !TryGetCollection(node, "scripts", out scenes))
                    {
                        nodeIndex++;
                        continue;
                    }

                    int sceneIndex = 0;
                    foreach (JsonElement scene in scenes)
                    {
                        if (!TryGetCollection(scene, "characters", out JsonElement.ArrayEnumerator characters))
                        {
                            sceneIndex++;
                            continue;
                        }

                        int recordIndex = 0;
                        foreach (JsonElement character in characters)
                        {
                            string name = GetString(character, "name");
                            if (!string.IsNullOrWhiteSpace(name))
                            {
                                int startingPos = GetInt32(character, "startingPos");
                                int endingPos = GetInt32(character, "endingPos");
                                bool transitions = startingPos != endingPos;
                                characterCount++;
                                if (transitions)
                                {
                                    transitionCount++;
                                    Console.WriteLine(
                                        $"TRANSITION file={Path.GetFileName(file)} node={nodeIndex} scene={sceneIndex} "
                                        + $"record={recordIndex} start={startingPos} end={endingPos} "
                                        + $"action={GetInt32(character, "action")} appear={GetInt32(character, "appear")} "
                                        + $"speaker={GetInt32(scene, "speakerSlotNum")} text={JsonSerializer.Serialize(GetString(scene, "text"))}");
                                }
                            }

                            recordIndex++;
                        }

                        sceneIndex++;
                    }

                    nodeIndex++;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"SKIP file={Path.GetFileName(file)} reason={ex.GetType().Name}:{ex.Message}");
            }
        }

        Console.WriteLine(
            $"AAP POSITION SUMMARY files={fileCount} characters={characterCount} transitions={transitionCount}");
        return 0;
    }

    private static bool TryGetCollection(
        JsonElement owner,
        string propertyName,
        out JsonElement.ArrayEnumerator values)
    {
        values = default;
        if (owner.ValueKind != JsonValueKind.Object
            || !owner.TryGetProperty(propertyName, out JsonElement collection))
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

    private static string GetString(JsonElement owner, string propertyName)
    {
        return owner.ValueKind == JsonValueKind.Object
               && owner.TryGetProperty(propertyName, out JsonElement value)
               && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static int GetInt32(JsonElement owner, string propertyName)
    {
        return owner.ValueKind == JsonValueKind.Object
               && owner.TryGetProperty(propertyName, out JsonElement value)
               && value.TryGetInt32(out int result)
            ? result
            : 0;
    }
}
