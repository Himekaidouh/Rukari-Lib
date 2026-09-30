using AzureArchive.VideoTools.Formats.Aas;

namespace AzureArchive.VideoTools.Tests;

internal static class AasSmokeVerifier
{
    public static int VerifyDirectory(string directory)
    {
        string fullPath = Path.GetFullPath(directory);
        if (!Directory.Exists(fullPath))
        {
            Console.Error.WriteLine($"AAS SMOKE FAIL directory does not exist: {fullPath}");
            return 1;
        }

        string[] files = Directory.GetFiles(fullPath, "*.aas", SearchOption.TopDirectoryOnly);
        var reader = new AasScenarioReader();
        int failures = 0;
        int recordCount = 0;

        foreach (string file in files)
        {
            var result = reader.Read(file);
            if (!result.Success || result.Value == null)
            {
                failures++;
                Console.Error.WriteLine(
                    $"AAS SMOKE FAIL {Path.GetFileName(file)}: {result.Error}");
                continue;
            }

            recordCount += result.Value.Records.Count;
        }

        Console.WriteLine(
            $"AAS SMOKE SUMMARY files={files.Length} failures={failures} records={recordCount}");
        return failures == 0 ? 0 : 1;
    }
}
