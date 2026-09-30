using System.Security.Cryptography;
using System.Text;
using AzureArchive.VideoTools.Core.Mapping;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Core.Results;
using AzureArchive.VideoTools.Formats.Aap;
using AzureArchive.VideoTools.Formats.Aas;

namespace AzureArchive.VideoTools.Tests;

internal static class AutoProjectDiscoveryTests
{
    public static void ScannerPairsByNameAndReportsMissingPlayback()
    {
        using var files = new TemporaryAapDirectory();
        string root = CreateDataRoot(files.Root);
        WriteProject(root, "alpha");
        WriteProject(root, "beta");
        WritePlayback(root, "alpha", "compiled-alpha");

        var scanned = AssertEx.NotNull(
            new ProjectPairScanner().Scan(root).Value);

        AssertEx.Equal(2, scanned.Count);
        AssertEx.Equal("alpha", scanned[0].Name);
        AssertEx.True(scanned[0].PlaybackPresent);
        AssertEx.Equal("beta", scanned[1].Name);
        AssertEx.False(scanned[1].PlaybackPresent);
    }

    public static void ScannerFailsClosedWithoutProjectsDirectory()
    {
        using var files = new TemporaryAapDirectory();
        string root = Path.Combine(files.Root, "empty-root");
        Directory.CreateDirectory(root);

        var result = new ProjectPairScanner().Scan(root);

        AssertEx.False(result.Success);
        AssertEx.True(result.Error.Contains("projects directory"), result.Error);
    }

    public static void ArbiterSelectsUniquePairAcrossProjects()
    {
        using var files = new TemporaryAapDirectory();
        string root = CreateDataRoot(files.Root);
        WriteProject(root, "alpha");
        WritePlayback(root, "alpha", "compiled-alpha-scene");
        WriteProject(root, "beta");
        WritePlayback(root, "beta", "compiled-beta-scene");
        IReadOnlyList<DiscoveredProjectPair> pairs = AssertEx.NotNull(
            new ProjectPairScanner().Scan(root).Value);

        var arbitration = new ActiveProjectPairArbiter(
            path => new AapProjectReader().Read(path),
            path => new AasScenarioReader().Read(path)).Arbitrate(
            Identity("compiled-beta-scene"),
            pairs);

        AssertEx.True(arbitration.Success, string.Join("|", arbitration.Diagnostics));
        AssertEx.Equal(
            ActivePairArbitrationStatus.Unique,
            arbitration.Status);
        AssertEx.NotNull(arbitration.Pair);
        AssertEx.Equal("beta", arbitration.Pair!.Name);
        AssertEx.Equal(0, arbitration.Resolution!.PlaybackRecordIndex);
        AssertEx.Equal(2, arbitration.ScannedPairCount);
    }

    public static void ArbiterFailsClosedWhenTwoProjectsShareScript()
    {
        using var files = new TemporaryAapDirectory();
        string root = CreateDataRoot(files.Root);
        const string sharedScript = "compiled-shared";
        WriteProject(root, "alpha");
        WritePlayback(root, "alpha", sharedScript);
        WriteProject(root, "beta");
        WritePlayback(root, "beta", sharedScript);
        IReadOnlyList<DiscoveredProjectPair> pairs = AssertEx.NotNull(
            new ProjectPairScanner().Scan(root).Value);

        var arbitration = new ActiveProjectPairArbiter(
            path => new AapProjectReader().Read(path),
            path => new AasScenarioReader().Read(path)).Arbitrate(
            Identity(sharedScript),
            pairs);

        AssertEx.Equal(
            ActivePairArbitrationStatus.AmbiguousCrossProject,
            arbitration.Status);
        AssertEx.True(arbitration.Pair == null, "expected no pair to be selected");
        AssertEx.True(
            arbitration.Diagnostics.Any(line => line.StartsWith("also-matched=", StringComparison.Ordinal)),
            string.Join("|", arbitration.Diagnostics));
    }

    public static void ArbiterReportsNotFoundForUnknownIdentity()
    {
        using var files = new TemporaryAapDirectory();
        string root = CreateDataRoot(files.Root);
        WriteProject(root, "alpha");
        WritePlayback(root, "alpha", "compiled-alpha-scene");
        IReadOnlyList<DiscoveredProjectPair> pairs = AssertEx.NotNull(
            new ProjectPairScanner().Scan(root).Value);

        var arbitration = new ActiveProjectPairArbiter(
            path => new AapProjectReader().Read(path),
            path => new AasScenarioReader().Read(path)).Arbitrate(
            Identity("compiled-from-another-project"),
            pairs);

        AssertEx.Equal(ActivePairArbitrationStatus.NotFound, arbitration.Status);
        AssertEx.True(arbitration.Pair == null, "expected no pair to be selected");
        AssertEx.Equal(1, arbitration.ScannedPairCount);
    }

    public static void SnapshotCacheRevalidatesByWriteTimeAndLength()
    {
        using var files = new TemporaryAapDirectory();
        string path = files.Write("cache.aap", SyntheticAap.Project(
            new[] { SyntheticAap.Scene("A") }));
        var reader = new AapProjectReader();
        var cache = new ArchiveSnapshotCache();
        int reads = 0;

        Func<string, Result<ProjectSnapshot>> readOnce = p =>
        {
            reads++;
            return reader.Read(p);
        };

        Result<ProjectSnapshot> first = cache.GetOrReadProject(path, readOnce);
        Result<ProjectSnapshot> second = cache.GetOrReadProject(path, readOnce);
        AssertEx.True(first.Success && second.Success);
        AssertEx.True(ReferenceEquals(first.Value, second.Value), "expected a cached snapshot hit");
        AssertEx.Equal(1, reads);

        File.WriteAllText(path, SyntheticAap.Project(new[]
        {
            SyntheticAap.Scene("A"),
            SyntheticAap.Scene("B")
        }));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        Result<ProjectSnapshot> third = cache.GetOrReadProject(path, readOnce);
        AssertEx.True(third.Success);
        AssertEx.Equal(2, reads, "expected the stale entry to be re-read");
        AssertEx.Equal(2, AssertEx.NotNull(third.Value).ScriptNodes.Single().Scenes.Count);
    }

    private static string CreateDataRoot(string parent)
    {
        string root = Path.Combine(parent, "data");
        Directory.CreateDirectory(Path.Combine(root, "projects"));
        Directory.CreateDirectory(Path.Combine(root, "saves"));
        return root;
    }

    private static void WriteProject(string root, string name)
    {
        string path = Path.Combine(root, "projects", $"{name}.aap");
        File.WriteAllText(path, SyntheticAap.Project(
            new[] { SyntheticAap.Scene($"{name}-scene") }));
    }

    private static void WritePlayback(string root, string name, string compiledScript)
    {
        byte[] bytes = SyntheticAas.Archive(new SyntheticAasRecord(
            CompiledScript: compiledScript,
            TextJp: $"{name}-scene",
            TextCn: $"{name}-scene",
            TextEn: $"{name}-scene"));
        File.WriteAllBytes(
            Path.Combine(root, "saves", $"{name}.aas"),
            bytes);
    }

    private static ObservedCompiledSceneIdentity Identity(string compiledScript)
    {
        string sha256 = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(compiledScript)));
        return new ObservedCompiledSceneIdentity(
            sha256,
            compiledScript.Length,
            compiledScript.Count(character => character == '\n') + 1);
    }
}
