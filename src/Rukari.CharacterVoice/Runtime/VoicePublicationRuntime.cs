using System.Security.Cryptography;
using AzureArchive.Automation;
using Rukari.CharacterVoice.Core;
using Rukari.CharacterVoice.Interop;
using Rukari.Lib;

namespace Rukari.CharacterVoice.Runtime;

/// <summary>Only an observed successful game publication can produce the mod-owned companion directory.</summary>
internal static class VoicePublicationRuntime
{
    [ThreadStatic] private static Scope? _current;
    internal static string LastStatus { get; private set; } = string.Empty;
    private static string _statusRoot = string.Empty;

    internal static string ReadCurrentStatus()
    {
        var current = ProjectVoiceImportStore.CaptureCurrentProjectOnMainThread();
        return current.Success && current.Value is not null
            && current.Value.RootPath.Equals(_statusRoot, StringComparison.OrdinalIgnoreCase) ? LastStatus : string.Empty;
    }

    internal sealed class Scope : IDisposable
    {
        internal readonly Scope? Parent;
        internal readonly string Root;
        internal readonly string Name;
        private bool _disposed;
        internal Scope(string root) { Root = root; Name = Path.GetFileName(root); Parent = _current; _current = this; }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (ReferenceEquals(_current, this)) _current = Parent;
        }
    }
    internal sealed record Promotion(string SourceRoot, string ProjectName, string Destination, string Hash);

    internal static ModResult<VoicePublicationIdentity> CaptureExplicitIdentity()
    {
        // All native access stays in this synchronous main-thread capture. No wrapper is retained.
        var project = ProjectVoiceImportStore.CaptureCurrentProjectOnMainThread();
        if (!project.Success) return ModResult<VoicePublicationIdentity>.Fail(project.Error!);
        if (project.Value is null)
            return ModResult<VoicePublicationIdentity>.Fail(ModErrorCode.NotReady, "当前配音资源工程不可用。");
        AuthoringEditorSession? session = AuthoringEditorSession.Current;
        if (ReferenceEquals(session, null) || session.WasCollected || session.Pointer == IntPtr.Zero)
            return ModResult<VoicePublicationIdentity>.Fail(ModErrorCode.NotReady, "当前编辑器会话不可用。");
        return ModResult<VoicePublicationIdentity>.Ok(new(project.Value.RootPath, session.FilePath ?? string.Empty));
    }

    internal static IDisposable EnterExplicitScope(string validatedRoot) => new Scope(validatedRoot);

    internal static Scope EnterCompile(AuthoringEditorSession? session, string? expectedName = null)
    {
        // No native wrappers cross a thread or survive this capture. No global 'last project' fallback.
        string root = string.Empty;
        try
        {
            var result = ProjectVoiceImportStore.CaptureCurrentProjectOnMainThread();
            if (!result.Success || result.Value is null) return new Scope(string.Empty);
            root = result.Value.RootPath;
            if (ReferenceEquals(session, null) || session.WasCollected || session.Pointer == IntPtr.Zero
                || !ProjectVoiceImportPolicy.MatchesCompilingProject(root, session.FilePath ?? string.Empty, expectedName))
                throw new IOException("当前资源目录与正在保存的工程不一致，未同步配音。");
            return new Scope(root);
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning("[voice] unable to capture the compiling project: " + ex.Message);
            _statusRoot = root;
            LastStatus = "配音同步已暂停：" + ex.Message;
            // An invalid nested compile must also mask any valid outer scope until it returns.
            return new Scope(string.Empty);
        }
    }

    internal static Scope EnterCompile(Studio.Scripts.StudioCommon common)
    {
        try { return EnterCompile(AuthoringEditorSession.Current, common.projectName ?? string.Empty); }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning("[voice] unable to identify the compiling editor: " + ex.Message);
            return new Scope(string.Empty);
        }
    }

    internal static Promotion? BeforePromotion(string temporary, string destination)
    {
        try
        {
            Scope? scope = _current;
            if (scope is null || scope.Root.Length == 0
                || !Path.GetExtension(destination).Equals(".aas", StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileNameWithoutExtension(destination).Equals(scope.Name, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(Path.Combine(scope.Root, ProjectVoiceImportPolicy.DirectoryName, "index.json"))) return null;
            // Promotion is observed, never assumed from Compile returning normally. A failed safe-save may return quietly.
            if (!File.Exists(temporary)) return null;
            return new(scope.Root, scope.Name, Path.GetFullPath(destination), Digest(temporary));
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning("[voice] voice publication observation failed: " + ex.Message);
            return null;
        }
    }

    internal static void AfterPromotion(Promotion? promotion)
    {
        if (promotion is null) return;
        _statusRoot = promotion.SourceRoot;
        try
        {
            if (!File.Exists(promotion.Destination) || Digest(promotion.Destination) != promotion.Hash)
                throw new IOException("游戏未完成本次 AAS 写入，未同步配音。");
            LastStatus = "正在同步作品配音；首次发布的等待时间取决于音频总量。";
            VoicePublicationResult result = ProjectVoiceImportPolicy.PublishForArchive(promotion.SourceRoot,
                promotion.ProjectName, promotion.Destination, CancellationToken.None);
            LastStatus = $"作品配音已同步：{result.Total} 段。分享时同时带上同名 .rukari 文件夹。";
            if (!result.Unchanged)
                Plugin.Logger.LogInfo($"[voice] published audio: copied={result.Copied}; total={result.Total}; root='{result.ResourceRoot}'; archiveUntouched=True.");
        }
        catch (Exception ex)
        {
            LastStatus = "剧情已发布，配音文件未同步：" + ex.Message;
            // The official archive has already committed; never report this as an exception from the game's safe-save.
            Plugin.Logger.LogWarning("[voice] " + LastStatus);
        }
    }

    private static string Digest(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var digest = SHA256.Create();
        return Convert.ToHexString(digest.ComputeHash(input));
    }
}
