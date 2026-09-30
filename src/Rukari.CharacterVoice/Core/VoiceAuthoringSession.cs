using Rukari.Lib;
using Rukari.Lib.Voices;

namespace Rukari.CharacterVoice.Core;

// Backend-only identity; never part of the shared Mod API or saved in a project.
public sealed record VoiceAuthoringDocument(
    string ContextId, string Revision, string DialogueText, string? ResourceId, string EditorSelectionToken);

public interface IVoiceAuthoringBackend
{
    ModResult<IReadOnlyList<VoiceResource>> ReadCatalog();
    ModResult<VoiceAuthoringDocument> ReadDocument();
    ModResult<VoiceAuthoringDocument> Apply(
        VoiceAuthoringDocument expected, string? resourceId);
}

/// <summary>A managed selection lease around the existing, verified editor write path.</summary>
public sealed class VoiceAuthoringSession : IVoiceAuthoringService, IDisposable
{
    private readonly IVoiceAuthoringBackend _backend;
    private readonly Func<bool> _isReady;
    private readonly Func<bool> _isMainThread;
    private VoiceAuthoringDocument? _issued;
    private string? _token;
    private int _disposed;
    private long _selectionGeneration;
    private long _issuedGeneration;

    public VoiceAuthoringSession(IVoiceAuthoringBackend backend,
        Func<bool> isReady, Func<bool> isMainThread)
    {
        _backend = backend;
        _isReady = isReady;
        _isMainThread = isMainThread;
    }

    public ModResult<IReadOnlyList<VoiceResource>> ReadCatalog() => Guard(() =>
    {
        var result = _backend.ReadCatalog();
        if (!result.Success) return result;
        return result.Value != null
            ? ModResult<IReadOnlyList<VoiceResource>>.Ok(Array.AsReadOnly(result.Value.ToArray()))
            : ModResult<IReadOnlyList<VoiceResource>>.Fail(ModErrorCode.ProviderFailed, "语音目录返回了空结果。");
    });

    public ModResult<VoiceSelection> ReadSelection() => Guard(() =>
    {
        var result = _backend.ReadDocument();
        if (!result.Success || result.Value == null)
        {
            Invalidate();
            return Failure<VoiceSelection>(result.Error);
        }
        return ModResult<VoiceSelection>.Ok(Issue(result.Value));
    });

    public ModResult<VoiceEditResult> Apply(VoiceEditRequest request) => Guard(() =>
    {
        if (request == null || string.IsNullOrWhiteSpace(request.SelectionToken)
            || string.IsNullOrWhiteSpace(request.ExpectedRevision)
            || (request.ResourceId != null && string.IsNullOrWhiteSpace(request.ResourceId)))
            return ModResult<VoiceEditResult>.Fail(ModErrorCode.InvalidArgument, "需要有效的台词凭据、修订和资源 ID。");
        if (_issued == null || request.SelectionToken != _token
            || _issuedGeneration != Volatile.Read(ref _selectionGeneration)
            || request.ExpectedRevision != _issued.Revision)
            return Conflict();

        var current = _backend.ReadDocument();
        if (!current.Success || current.Value == null)
        {
            Invalidate();
            return Failure<VoiceEditResult>(current.Error);
        }
        if (current.Value != _issued)
        {
            Invalidate();
            return Conflict();
        }
        if (request.ResourceId != null)
        {
            var catalog = _backend.ReadCatalog();
            if (!catalog.Success || catalog.Value == null)
                return Failure<VoiceEditResult>(catalog.Error);
            if (!catalog.Value.Any(resource => resource.Id == request.ResourceId))
                return ModResult<VoiceEditResult>.Fail(ModErrorCode.NotFound, "所选语音资源已不可用，请刷新资源列表。");
        }
        if (_issuedGeneration != Volatile.Read(ref _selectionGeneration)) return Conflict();
        if (current.Value.ResourceId == request.ResourceId)
            return ModResult<VoiceEditResult>.Ok(new(Issue(current.Value), false));

        // The backend must revalidate the context/revision immediately before mutation.
        var result = _backend.Apply(current.Value, request.ResourceId);
        Invalidate(); // Even a failed native write may have changed state; require a fresh read.
        if (!result.Success || result.Value == null)
            return Failure<VoiceEditResult>(result.Error);
        if (result.Value.ContextId != current.Value.ContextId || result.Value.DialogueText != current.Value.DialogueText
            || result.Value.ResourceId != request.ResourceId)
            return ModResult<VoiceEditResult>.Fail(ModErrorCode.ProviderFailed,
                "语音写入后的读回不一致，请重新检查当前台词；不要自动重试。");
        return ModResult<VoiceEditResult>.Ok(new(Issue(result.Value), true));
    });

    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

    // Called by existing zero-argument/primitive selection notifications, never with native objects.
    public void InvalidateSelection() => Interlocked.Increment(ref _selectionGeneration);

    private ModResult<T> Guard<T>(Func<ModResult<T>> operation)
    {
        try
        {
            if (Volatile.Read(ref _disposed) != 0 || !_isReady())
                return ModResult<T>.Fail(ModErrorCode.NotReady, "语音服务尚未就绪或已停止。");
            if (!_isMainThread())
                return ModResult<T>.Fail(ModErrorCode.WrongThread, "请通过 Mod Runtime 在游戏主线程调用语音服务。");
            return operation();
        }
        catch (Exception ex)
        {
            Invalidate();
            return ModResult<T>.Fail(ModErrorCode.ProviderFailed, ex.Message);
        }
    }

    private VoiceSelection Issue(VoiceAuthoringDocument document)
    {
        long generation = Volatile.Read(ref _selectionGeneration);
        if (_issued != document || _token == null || _issuedGeneration != generation)
            _token = Guid.NewGuid().ToString("N");
        _issued = document;
        _issuedGeneration = generation;
        return new(_token, document.Revision, document.DialogueText, document.ResourceId);
    }

    private void Invalidate() { _issued = null; _token = null; }
    private static ModResult<VoiceEditResult> Conflict() => ModResult<VoiceEditResult>.Fail(
        ModErrorCode.Conflict, "当前台词或修订已变化，请重新读取后再应用。");
    private static ModResult<T> Failure<T>(ModError? error) => ModResult<T>.Fail(
        error?.Code ?? ModErrorCode.ProviderFailed, error?.Message ?? "语音服务未返回有效结果。");
}
