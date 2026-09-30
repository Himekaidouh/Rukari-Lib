using Rukari.CharacterVoice.Core;
using Rukari.CharacterVoice.Runtime;
using Rukari.Lib;

namespace Rukari.CharacterVoice.Interop;

/// <summary>Captures native project identity on the main thread, then imports managed file snapshots.</summary>
internal sealed class ProjectVoiceImportStore
{
    /// <summary>How many recently used project roots a lookup may walk. The content hash is the
    /// identity, so a hit under any of them is the same audio; the cap only bounds the cost of a
    /// miss (one small index read per root).</summary>
    private const int MaximumRememberedRoots = 4;

    private static int _mainThreadId;
    private static readonly object RootsGate = new();
    private static readonly List<string> RememberedRoots = new();
    private readonly VoiceImportProject _project;

    internal ProjectVoiceImportStore(VoiceImportProject project)
    {
        _project = new(ProjectVoiceImportPolicy.NormalizeIdentity(project.RootPath));
        // Opening the authoring page for a project is the strongest statement of which project
        // this session's voices belong to, and it happens before any playback.
        PromoteRoot(_project.RootPath);
    }

    internal static void InitializeOnMainThread()
    {
        int current = Environment.CurrentManagedThreadId;
        if (_mainThreadId == 0) _mainThreadId = current;
        else if (_mainThreadId != current) throw new InvalidOperationException("语音导入必须在游戏主线程初始化。");
    }

    /// <summary>Moves a root to the front of the remembered list, dropping the oldest beyond the cap.</summary>
    internal static void PromoteRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return;
        string normalized;
        try { normalized = ProjectVoiceImportPolicy.NormalizeIdentity(root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { return; }
        if (normalized.Length == 0) return;
        lock (RootsGate)
        {
            RememberedRoots.RemoveAll(item => string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase));
            RememberedRoots.Insert(0, normalized);
            while (RememberedRoots.Count > MaximumRememberedRoots)
                RememberedRoots.RemoveAt(RememberedRoots.Count - 1);
        }
    }

    /// <summary>
    /// The project roots an imported voice may live under, best candidate first: the live override
    /// root (what the editor is showing, or the played package during formal playback) followed by
    /// the roots this session has actually imported into. Playback resolves through this list because
    /// the live root is the played package there, which never carries <c>rukari-voices/</c>.
    /// </summary>
    internal static IReadOnlyList<string> ResolutionCandidates()
    {
        var candidates = new List<string>(MaximumRememberedRoots + 1);
        ModResult<VoiceImportProject> live = CaptureCurrentProjectOnMainThread();
        if (live.Success && live.Value is not null && live.Value.RootPath.Length != 0)
            candidates.Add(live.Value.RootPath);
        lock (RootsGate)
        {
            foreach (string root in RememberedRoots)
            {
                if (!candidates.Contains(root, StringComparer.OrdinalIgnoreCase)) candidates.Add(root);
            }
        }
        return candidates;
    }

    internal static ModResult<VoiceImportProject> CaptureCurrentProjectOnMainThread()
    {
        if (_mainThreadId == 0) return ModResult<VoiceImportProject>.Fail(ModErrorCode.NotReady, "语音导入尚未初始化。");
        if (_mainThreadId != Environment.CurrentManagedThreadId)
            return ModResult<VoiceImportProject>.Fail(ModErrorCode.WrongThread, "当前项目必须在游戏主线程读取。");
        try
        {
            ScenarioResourceManager resources = ScenarioResourceManager.Instance;
            if (ReferenceEquals(resources, null) || ReferenceEquals(resources.LocalOverrideData, null))
                return ModResult<VoiceImportProject>.Fail(ModErrorCode.NotReady, "请先打开项目。");
            // Only the managed path leaves this call; no native resource wrapper crosses threads.
            string root = resources.LocalOverrideData.basePath ?? string.Empty;
            return ModResult<VoiceImportProject>.Ok(new(ProjectVoiceImportPolicy.NormalizeIdentity(root)));
        }
        catch (Exception ex)
        {
            return ModResult<VoiceImportProject>.Fail(ModErrorCode.NotReady, "当前项目目录不可用：" + ex.Message);
        }
    }

    internal Task<VoiceImportResult> ImportAsync(VoiceFolderScan scan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scan);
        var files = scan.Files.Select(file => new VoiceImportSource(file.FullPath, file.RelativePath,
            file.Length, file.Extension, file.LastWriteTimeUtcTicks)).ToArray();
        return ProjectVoiceImportPolicy.ImportAsync(_project, scan.RootPath, files, scan.SkippedFiles, cancellationToken);
    }
}
