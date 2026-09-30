using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Formats.Aap;

namespace AzureArchive.VideoTools.Tests;

internal static class AapSmokeVerifier
{
    public static int VerifyDirectory(string directory)
    {
        string fullPath = Path.GetFullPath(directory);
        if (!Directory.Exists(fullPath))
        {
            Console.Error.WriteLine($"SMOKE FAIL directory does not exist: {fullPath}");
            return 1;
        }

        string[] files = Directory.GetFiles(fullPath, "*.aap", SearchOption.TopDirectoryOnly);
        var reader = new AapProjectReader();
        int failures = 0;
        int nodeCount = 0;
        int sceneCount = 0;
        int diagnosticErrors = 0;

        foreach (string file in files)
        {
            var result = reader.Read(file);
            if (!result.Success || result.Value == null)
            {
                failures++;
                Console.Error.WriteLine($"SMOKE FAIL {Path.GetFileName(file)}: {result.Error}");
                continue;
            }

            ProjectSnapshot project = result.Value;
            nodeCount += project.Nodes.Count;
            sceneCount += project.ScriptNodes.Sum(node => node.Scenes.Count);
            diagnosticErrors += project.Diagnostics.Count(
                diagnostic => diagnostic.Severity == ProjectDiagnosticSeverity.Error);
        }

        Console.WriteLine(
            $"SMOKE SUMMARY files={files.Length} failures={failures} nodes={nodeCount} "
            + $"scenes={sceneCount} diagnosticErrors={diagnosticErrors}");
        return failures == 0 && diagnosticErrors == 0 ? 0 : 1;
    }
}
