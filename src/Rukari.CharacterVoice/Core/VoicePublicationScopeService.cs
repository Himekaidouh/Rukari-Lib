using Rukari.Lib;
using Rukari.Lib.Editor;

namespace Rukari.CharacterVoice.Core;

/// <summary>Only managed strings leave the native identity capture operation.</summary>
internal sealed record VoicePublicationIdentity(string ResourceRoot, string ProjectFile);

/// <summary>Validates explicit publication admission without invoking a save or compile operation.</summary>
internal sealed class VoicePublicationScopeService : IEditorPublicationScopeService, IDisposable
{
    private readonly Func<bool> _isReady;
    private readonly Func<bool> _isMainThread;
    private readonly Func<ModResult<VoicePublicationIdentity>> _capture;
    private readonly Func<string, IDisposable> _enter;
    private int _stopped;

    internal VoicePublicationScopeService(Func<bool> isReady, Func<bool> isMainThread,
        Func<ModResult<VoicePublicationIdentity>> capture, Func<string, IDisposable> enter)
    {
        _isReady = isReady;
        _isMainThread = isMainThread;
        _capture = capture;
        _enter = enter;
    }

    public ModResult<IDisposable> Begin(string expectedProjectName)
    {
        if (Volatile.Read(ref _stopped) != 0 || !_isReady())
            return ModResult<IDisposable>.Fail(ModErrorCode.NotReady, "配音发布服务尚未就绪或已停止。");
        if (!_isMainThread())
            return ModResult<IDisposable>.Fail(ModErrorCode.WrongThread, "配音发布作用域只能在游戏主线程开启。");
        if (string.IsNullOrWhiteSpace(expectedProjectName)
            || expectedProjectName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || expectedProjectName is "." or "..")
            return ModResult<IDisposable>.Fail(ModErrorCode.InvalidArgument, "手动发布需要有效的当前工程名称。");
        try
        {
            ModResult<VoicePublicationIdentity> captured = _capture();
            if (!captured.Success) return ModResult<IDisposable>.Fail(captured.Error!);
            if (captured.Value is null
                || !ProjectVoiceImportPolicy.MatchesCompilingProject(captured.Value.ResourceRoot,
                    captured.Value.ProjectFile, expectedProjectName))
                return ModResult<IDisposable>.Fail(ModErrorCode.Conflict, "当前资源目录、编辑器会话与待发布工程不一致。");
            IDisposable ticket = _enter(captured.Value.ResourceRoot)
                ?? throw new InvalidOperationException("配音发布服务未提供有效作用域票据。");
            return ModResult<IDisposable>.Ok(ticket);
        }
        catch (Exception ex)
        {
            return ModResult<IDisposable>.Fail(ModErrorCode.ProviderFailed,
                "无法开启配音发布作用域：" + ex.GetType().Name);
        }
    }

    public void Dispose() => Interlocked.Exchange(ref _stopped, 1);
}
