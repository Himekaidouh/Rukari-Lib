using System.Security.Cryptography;
using System.Text;
using AzureArchive.VideoTools.Core.Mapping;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Formats.Aap;
using AzureArchive.VideoTools.Formats.Aas;
using AzureArchive.VideoTools.Runtime;

namespace AzureArchive.VideoTools.Tests;

internal static class OfficialProjectStoragePathTests
{
    public static void CustomAuthoringPathSelectsItsExactSavedPair()
    {
        using var files = new TemporaryAapDirectory();
        string root = CreateRoot(files.Root, "user-selected-storage");
        WritePair(root, "alpha", "shared");
        WritePair(root, "beta", "shared");
        var paths = new OfficialProjectPathSnapshot(1, Project(root, "beta"), Resource(root, "projects", "beta"), false);
        var resolved = OfficialProjectStoragePaths.Resolve(paths, string.Empty, Path.Combine(files.Root, "unrelated-default"));
        AssertEx.True(resolved.Success, resolved.Error);
        var binding = AssertEx.NotNull(resolved.Value);
        AssertEx.Equal(root, binding.DataRoot);
        AssertEx.Equal(Project(root, "beta"), binding.ProjectFilePath);
        AssertEx.Equal(1, binding.Restrict(AssertEx.NotNull(new ProjectPairScanner().Scan(root).Value)).Count);
        var arbitration = new ActiveProjectPairArbiter(path => new AapProjectReader().Read(path),
            path => new AasScenarioReader().Read(path)).Arbitrate(Identity("shared"),
                binding.Restrict(AssertEx.NotNull(new ProjectPairScanner().Scan(root).Value)));
        AssertEx.True(arbitration.Success, string.Join("|", arbitration.Diagnostics));
        AssertEx.Equal("beta", AssertEx.NotNull(arbitration.Pair).Name);
    }

    public static void LivePlaybackResourceOverridesARetainedOtherEditorProject()
    {
        using var files = new TemporaryAapDirectory();
        string oldRoot = CreateRoot(files.Root, "old-editor-root");
        string liveRoot = CreateRoot(files.Root, "new-playback-root");
        WritePair(oldRoot, "old", "shared");
        WritePair(liveRoot, "live", "shared");
        var resolved = OfficialProjectStoragePaths.Resolve(new(4, Project(oldRoot, "old"),
            Resource(liveRoot, "saves", "live"), false), string.Empty, files.Root);
        AssertEx.True(resolved.Success, resolved.Error);
        var binding = AssertEx.NotNull(resolved.Value);
        AssertEx.Equal(liveRoot, binding.DataRoot);
        AssertEx.Equal("official-playback-resource-path", binding.Source);
        AssertEx.Equal(Path.Combine(liveRoot, "saves", "live.aas"), binding.PlaybackFilePath);
        var foreign = new ProjectPairScanner().Scan(oldRoot);
        AssertEx.Equal(0, binding.Restrict(AssertEx.NotNull(foreign.Value)).Count);
    }

    public static void InvalidCustomEvidenceCannotFallBackToAnExistingDefaultPair()
    {
        using var files = new TemporaryAapDirectory();
        string defaultRoot = CreateRoot(files.Root, "data");
        WritePair(defaultRoot, "valid", "shared");
        string custom = CreateRoot(files.Root, "custom");
        WritePair(custom, "valid", "shared");
        foreach (var paths in new[] {
            new OfficialProjectPathSnapshot(1, string.Empty, Path.Combine(custom, "unrecognized", "valid"), false),
            new OfficialProjectPathSnapshot(1, "projects/valid.aap", string.Empty, false),
            new OfficialProjectPathSnapshot(1, Project(custom, "missing"), string.Empty, false),
            new OfficialProjectPathSnapshot(1, string.Empty, Resource(custom, "saves", "missing"), false),
            new OfficialProjectPathSnapshot(1, string.Empty, string.Empty, true),
            new OfficialProjectPathSnapshot(1, Project(custom, "valid"), Resource(custom, "projects", "other"), false),
            new OfficialProjectPathSnapshot(1, Project(defaultRoot, "valid"), Resource(custom, "projects", "valid"), false)
        })
            AssertEx.False(OfficialProjectStoragePaths.Resolve(paths, string.Empty, files.Root).Success);
        AssertEx.False(OfficialProjectStoragePaths.Resolve(new(1, Project(custom, "valid"), string.Empty, false),
            defaultRoot, files.Root).Success);
    }

    public static void DefaultAndExplicitRootsRetainLegacyDiscoveryWithoutLiveEvidence()
    {
        using var files = new TemporaryAapDirectory();
        string root = CreateRoot(files.Root, "data");
        WritePair(root, "one", "one");
        WritePair(root, "two", "two");
        var empty = new OfficialProjectPathSnapshot(0, string.Empty, string.Empty, false);
        var defaultBinding = OfficialProjectStoragePaths.Resolve(empty, string.Empty, files.Root);
        AssertEx.True(defaultBinding.Success, defaultBinding.Error);
        AssertEx.Equal("default-persistent-data-path", AssertEx.NotNull(defaultBinding.Value).Source);
        AssertEx.Equal(2, defaultBinding.Value!.Restrict(AssertEx.NotNull(new ProjectPairScanner().Scan(root).Value)).Count);
        var explicitBinding = OfficialProjectStoragePaths.Resolve(empty, root, string.Empty);
        AssertEx.True(explicitBinding.Success, explicitBinding.Error);
        AssertEx.Equal(root, AssertEx.NotNull(explicitBinding.Value).DataRoot);
        AssertEx.False(OfficialProjectStoragePaths.Resolve(empty, "relative-data", files.Root).Success);
    }

    public static void PathChangesInvalidateOldWorkAndEquivalentSpellingsStayStable()
    {
        using var files = new TemporaryAapDirectory();
        string firstRoot = CreateRoot(files.Root, "first");
        string secondRoot = CreateRoot(files.Root, "second");
        WritePair(firstRoot, "same", "first");
        WritePair(secondRoot, "same", "second");
        var tracker = new OfficialProjectPathTracker();
        AssertEx.True(tracker.Observe(Project(firstRoot, "same"), Resource(firstRoot, "projects", "same"), false));
        var first = tracker.Current;
        AssertEx.False(tracker.Observe(Project(firstRoot, "same"), Resource(firstRoot, "projects", "same")
            + Path.DirectorySeparatorChar, false));
        AssertEx.True(tracker.IsCurrent(first.Revision));
        AssertEx.True(tracker.Observe(string.Empty, Resource(secondRoot, "saves", "same"), false));
        AssertEx.False(tracker.IsCurrent(first.Revision));
        var second = OfficialProjectStoragePaths.Resolve(tracker.Current, string.Empty, files.Root);
        AssertEx.True(second.Success, second.Error);
        AssertEx.Equal(secondRoot, AssertEx.NotNull(second.Value).DataRoot);
        AssertEx.Equal(0, second.Value!.Restrict(AssertEx.NotNull(new ProjectPairScanner().Scan(firstRoot).Value)).Count);
        AssertEx.True(tracker.Observe(string.Empty, string.Empty, true));
        AssertEx.False(OfficialProjectStoragePaths.Resolve(tracker.Current, string.Empty, files.Root).Success);
    }

    public static void AuthoringFileAloneIsSufficientButPlaybackNeedsItsRealArchive()
    {
        using var files = new TemporaryAapDirectory();
        string root = CreateRoot(files.Root, "relocated-data");
        WritePair(root, "work", "saved");
        var authoring = OfficialProjectStoragePaths.Resolve(new(1, Project(root, "work"), string.Empty, false),
            string.Empty, files.Root);
        AssertEx.True(authoring.Success, authoring.Error);
        AssertEx.Equal("official-authoring-file-path", AssertEx.NotNull(authoring.Value).Source);
        File.Delete(Path.Combine(root, "saves", "work.aas"));
        AssertEx.False(OfficialProjectStoragePaths.Resolve(new(2, string.Empty,
            Resource(root, "saves", "work"), false), string.Empty, files.Root).Success);
        // Merely having the same script in a different saved project does not restore trust.
        WritePair(root, "other", "saved");
        AssertEx.False(OfficialProjectStoragePaths.Resolve(new(2, string.Empty,
            Resource(root, "saves", "work"), false), string.Empty, files.Root).Success);
    }

    public static void SameProjectPreviewToPlaybackKeepsItsDiskIdentity()
    {
        using var files = new TemporaryAapDirectory();
        string root = CreateRoot(files.Root, "custom");
        WritePair(root, "work", "saved");
        var tracker = new OfficialProjectPathTracker();
        AssertEx.True(tracker.Observe(Project(root, "work"), Resource(root, "projects", "work"), false));
        long revision = tracker.Current.Revision;
        AssertEx.False(tracker.Observe(string.Empty, Resource(root, "saves", "work"), false));
        AssertEx.True(tracker.IsCurrent(revision));
        AssertEx.Equal(Resource(root, "saves", "work"), tracker.Current.ResourceDirectory);
        var binding = OfficialProjectStoragePaths.Resolve(tracker.Current, string.Empty, files.Root);
        AssertEx.True(binding.Success, binding.Error);
        AssertEx.Equal("official-playback-resource-path", AssertEx.NotNull(binding.Value).Source);
    }

    public static void ClosedWindowsRetainTheirContextAcrossSeveralDrainPasses()
    {
        while (PlayerAdvanceObservationWindow.TryDequeue(out _)) { }
        try
        {
            // More than one runtime pass (128 events) of identical scripts/rows can remain queued.
            for (int index = 0; index < 260; index++)
            {
                PlayerAdvanceObservationWindow.Open(0, 10);
                PlayerAdvanceObservationWindow.Capture("shared-script");
                PlayerAdvanceObservationWindow.Close(0);
            }
            PlayerAdvanceObservationWindow.Open(0, 11);
            PlayerAdvanceObservationWindow.Capture("shared-script");
            PlayerAdvanceObservationWindow.Close(0);
            int stale = 0;
            int accepted = 0;
            while (PlayerAdvanceObservationWindow.TryDequeue(out var window))
            {
                AssertEx.NotNull(window);
                AssertEx.Equal("shared-script", window!.ManagedUnityMessages.Single());
                if (window.BelongsToStorage(11)) accepted++;
                else stale++;
            }
            AssertEx.Equal(260, stale);
            AssertEx.Equal(1, accepted);
        }
        finally { while (PlayerAdvanceObservationWindow.TryDequeue(out _)) { } }
    }

    public static void ChangedContextRejectsBackgroundPublicationAndSameFrameWindow()
    {
        var tracker = new OfficialProjectPathTracker();
        AssertEx.True(tracker.Observe(Path.Combine(Path.GetTempPath(), "first", "projects", "work.aap"),
            string.Empty, false));
        long queuedWorkerRevision = tracker.Current.Revision;
        while (PlayerAdvanceObservationWindow.TryDequeue(out _)) { }
        PlayerAdvanceObservationWindow.Open(2, queuedWorkerRevision);
        PlayerAdvanceObservationWindow.Capture("shared");
        PlayerAdvanceObservationWindow.Close(2);
        // This models a selection pump changing storage after the frame's first reload consumption.
        AssertEx.True(tracker.Observe(Path.Combine(Path.GetTempPath(), "second", "projects", "work.aap"),
            string.Empty, false));
        int writes = 0;
        AssertEx.False(tracker.CommitIfCurrent(queuedWorkerRevision, () => writes++));
        AssertEx.Equal(0, writes);
        AssertEx.True(tracker.CommitIfCurrent(tracker.Current.Revision, () => writes++));
        AssertEx.Equal(1, writes);
        AssertEx.True(PlayerAdvanceObservationWindow.TryDequeue(out var stale));
        AssertEx.False(AssertEx.NotNull(stale).BelongsToStorage(tracker.Current.Revision));
    }

    private static string CreateRoot(string parent, string name)
    {
        string root = Path.Combine(parent, name);
        Directory.CreateDirectory(Path.Combine(root, "projects"));
        Directory.CreateDirectory(Path.Combine(root, "saves"));
        return root;
    }
    private static string Project(string root, string name) => Path.Combine(root, "projects", name + ".aap");
    private static string Resource(string root, string branch, string name) => Path.Combine(root, branch, name);
    private static void WritePair(string root, string name, string script)
    {
        Directory.CreateDirectory(Resource(root, "projects", name));
        Directory.CreateDirectory(Resource(root, "saves", name));
        File.WriteAllText(Project(root, name), SyntheticAap.Project(new[] { SyntheticAap.Scene(name + "-scene") }));
        File.WriteAllBytes(Path.Combine(root, "saves", name + ".aas"), SyntheticAas.Archive(new SyntheticAasRecord(
            CompiledScript: script, TextJp: name + "-scene", TextCn: name + "-scene", TextEn: name + "-scene")));
    }
    private static ObservedCompiledSceneIdentity Identity(string script) => new(
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script))), script.Length,
        script.Count(character => character == '\n') + 1);
}
