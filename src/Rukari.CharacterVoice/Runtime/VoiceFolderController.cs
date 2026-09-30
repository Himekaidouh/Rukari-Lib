namespace Rukari.CharacterVoice.Runtime;

/// <summary>Main-thread view state with only directory selection and scanning delegated to workers.</summary>
internal sealed class VoiceFolderController : IDisposable
{
    private readonly IVoiceFolderPicker _picker;
    private readonly Func<string, bool, CancellationToken, VoiceFolderScan> _scan;
    private CancellationTokenSource? _cancellation;
    private Task<VoiceFolderScan?>? _pending;
    private bool _discardPending;
    private bool _disposed;

    internal VoiceFolderController(IVoiceFolderPicker? picker = null,
        Func<string, bool, CancellationToken, VoiceFolderScan>? scan = null)
    {
        _picker = picker ?? new WindowsVoiceFolderPicker();
        _scan = scan ?? VoiceFolderScanner.Scan;
    }

    internal VoiceFolderScan? Current { get; private set; }
    internal bool Recursive { get; private set; }
    internal bool IsBusy => _pending is not null;
    internal bool HasPendingCompletion => _pending?.IsCompleted == true;
    internal int Revision { get; private set; }
    internal string Status { get; private set; } = "支持 WAV / OGG / MP3；默认只扫描所选文件夹。";

    internal void Choose()
    {
        if (_disposed || IsBusy) return;
        Start(null);
    }

    internal void Rescan()
    {
        if (_disposed || IsBusy || Current is null) return;
        Start(Current.RootPath);
    }

    internal void ToggleRecursive()
    {
        if (_disposed || IsBusy) return;
        Recursive = !Recursive;
        if (Current is not null) Rescan();
        else Status = Recursive ? "将扫描所选文件夹及其子目录。" : "将只扫描所选文件夹，不含子目录。";
    }

    internal void Cancel()
    {
        if (!IsBusy) return;
        Status = "正在取消，原有列表将保留。";
        _discardPending = true;
        _cancellation?.Cancel();
    }

    internal void Clear()
    {
        Cancel();
        Current = null;
        Revision++;
        Status = "项目已切换，请重新选择语音文件夹。";
    }

    private void Start(string? folder)
    {
        bool recursive = Recursive;
        _discardPending = false;
        _cancellation = new CancellationTokenSource();
        CancellationToken token = _cancellation.Token;
        Status = folder is null ? "请在系统窗口中选择语音文件夹。" : "正在扫描语音文件夹…";
        Task<VoiceFolderScan?> worker = Task.Run(async () =>
        {
            string? selected = folder ?? await _picker.PickAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return selected is null ? null : _scan(selected, recursive, token);
        }, token);
        // Filesystem calls on a disconnected drive may not honor cancellation until they return.
        // Scanning is read-only: stop waiting now and discard any late result, without blocking UI.
        _pending = worker.WaitAsync(token);
        _ = worker.ContinueWith(completed => _ = completed.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    internal void Poll()
    {
        Task<VoiceFolderScan?>? pending = _pending;
        if (_disposed || pending is null || !pending.IsCompleted) return;
        _pending = null;
        try
        {
            VoiceFolderScan? scanned = pending.GetAwaiter().GetResult();
            if (_discardPending)
            {
                if (Status.StartsWith("正在取消", StringComparison.Ordinal)) Status = "已取消，原有列表保持不变。";
                return;
            }
            if (scanned is null) { Status = "已取消选择，原有列表保持不变。"; return; }
            Current = scanned;
            Revision++;
            Status = $"找到 {scanned.Files.Count} 个音频；跳过 {scanned.SkippedFiles} 个不支持、空白或不可读取项目。";
        }
        catch (OperationCanceledException)
        {
            if (Current is not null || !_discardPending || Status.StartsWith("正在取消", StringComparison.Ordinal))
                Status = "已取消，原有列表保持不变。";
        }
        catch (Exception ex) { if (!_discardPending) Status = "无法读取文件夹：" + ex.Message; }
        finally { _cancellation?.Dispose(); _cancellation = null; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
        // A late worker result is deliberately ignored; it can never write a resource or a dialogue.
    }
}
