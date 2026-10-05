using AzureArchive.VideoTools.Core.Projects;

namespace AzureArchive.VideoTools.Tests;

internal static class OfficialProjectPathMismatchDiagnosticTests
{
    public static void MismatchReportsBothOfficialPathsWithoutBindingAnotherProject()
    {
        using var files = new TemporaryAapDirectory();
        string root = Path.Combine(files.Root, "selected-storage");
        string otherRoot = Path.Combine(files.Root, "retained-storage");
        string resource = Path.Combine(root, "projects", "current");
        var cases = new[]
        {
            (Project: Path.Combine(root, "projects", "old.aap"), Recognized: true, RootMatches: true, NameMatches: false),
            (Project: Path.Combine(otherRoot, "projects", "current.aap"), Recognized: true, RootMatches: false, NameMatches: true),
            (Project: Path.Combine(root, "exports", "current.aap"), Recognized: false, RootMatches: false, NameMatches: false)
        };
        foreach (var item in cases)
        {
            var resolved = OfficialProjectStoragePaths.Resolve(
                new OfficialProjectPathSnapshot(17, item.Project, resource, false), string.Empty, files.Root);
            AssertEx.False(resolved.Success);
            AssertEx.True(resolved.Value == null);
            AssertEx.True(resolved.Error.StartsWith("reason=official-project-resource-path-mismatch;", StringComparison.Ordinal), resolved.Error);
            AssertEx.True(resolved.Error.Contains("revision=17", StringComparison.Ordinal), resolved.Error);
            AssertEx.True(resolved.Error.Contains($"projectLayoutRecognized={item.Recognized}", StringComparison.Ordinal), resolved.Error);
            AssertEx.True(resolved.Error.Contains($"rootMatches={item.RootMatches}", StringComparison.Ordinal), resolved.Error);
            AssertEx.True(resolved.Error.Contains($"nameMatches={item.NameMatches}", StringComparison.Ordinal), resolved.Error);
            AssertEx.True(resolved.Error.Contains($"projectFilePath='{item.Project}'", StringComparison.Ordinal), resolved.Error);
            AssertEx.True(resolved.Error.Contains($"resourceDirectory='{resource}'", StringComparison.Ordinal), resolved.Error);
        }
    }
}
