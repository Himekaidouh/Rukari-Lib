using Rukari.CharacterVoice.Core;
using Rukari.Lib;
using Rukari.Lib.Editor;
using Rukari.Lib.Tools;
using Rukari.Lib.Voices;

namespace Rukari.CharacterVoice.Runtime;

internal sealed class VoiceToolPage : IDisposable
{
    internal const string PageId = "rukari.charactervoice.authoring";
    private readonly IVoiceAuthoringService _voice;
    private readonly IEditorDocumentService _editor;
    private readonly VoiceFolderController _folders;
    private readonly Func<ModResult<VoiceImportProject>> _captureProject;
    private readonly Func<VoiceImportProject, VoiceFolderScan, CancellationToken, Task<VoiceImportResult>> _import;
    private readonly Func<long> _clock;
    private IReadOnlyList<VoiceResource> _catalog = Array.Empty<VoiceResource>();
    private VoiceSelection? _selection;
    private VoiceImportProject? _folderProject;
    private Task<VoiceImportResult>? _importTask;
    private CancellationTokenSource? _importCancellation;
    private string? _importProjectIdentity;
    private string? _selectedResource;
    private readonly string _backupRoot;
    private VoiceOverrideCleanupPlan? _cleanupPlan;
    private string _status = "选择语音文件夹，或从已导入声音中选择。";
    private int _folderRevision;
    private long _nextProjectCheck;
    private bool _showFolder;
    private bool _catalogLoaded;
    private bool _disposed;
    internal Func<string>? ReadPublicationStatus { get; set; }

    internal VoiceToolPage(IVoiceAuthoringService voice, IEditorDocumentService editor,
        Func<ModResult<VoiceImportProject>> captureProject,
        Func<VoiceImportProject, VoiceFolderScan, CancellationToken, Task<VoiceImportResult>> import,
        VoiceFolderController? folders = null, Func<long>? clock = null, string? backupRoot = null)
    {
        _voice = voice;
        _editor = editor;
        _captureProject = captureProject;
        _import = import;
        _folders = folders ?? new VoiceFolderController();
        _clock = clock ?? (() => Environment.TickCount64);
        // Backups of the project manifest must never land inside the project tree: the editor enumerates it.
        _backupRoot = string.IsNullOrWhiteSpace(backupRoot)
            ? Path.Combine(Path.GetTempPath(), "rukari-voice-cleanup")
            : backupRoot;
    }

    private bool Busy => _folders.IsBusy || _importTask is not null;

    // Called by the existing voice behaviour even when the shared page is hidden. A project switch
    // must cancel the old operation without waiting for the user to reopen the toolbox.
    internal void Tick(bool forceProjectCheck = false)
    {
        if (_disposed) return;
        bool completion = _folders.HasPendingCompletion || _importTask?.IsCompleted == true;
        // Inactive pages do not poll native state. Busy background work checks four times a second;
        // opening the page, user actions, and completed results always validate the project now.
        if (_folderProject is not null
            && (forceProjectCheck || completion || (Busy && _clock() >= _nextProjectCheck)))
        {
            _nextProjectCheck = _clock() + 250;
            var currentProject = _captureProject();
            if (!currentProject.Success || !SameProject(currentProject.Value.Identity, _folderProject.Identity))
            {
                _importCancellation?.Cancel();
                _folders.Clear();
                _folderProject = null;
                _showFolder = false;
                _catalog = Array.Empty<VoiceResource>();
                _catalogLoaded = false;
                _selectedResource = null;
                _selection = null;
                _status = "项目已切换，请在当前项目重新选择语音文件夹。";
            }
        }
        bool wasScanning = _folders.IsBusy;
        _folders.Poll();
        if (_folderRevision != _folders.Revision)
        {
            _folderRevision = _folders.Revision;
            if (_folderProject is not null && _folders.Current is not null) _showFolder = true;
        }
        if (wasScanning && !_folders.IsBusy && _folderProject is not null) _status = _folders.Status;
        PollImport();
    }

    internal ToolPageSnapshot Snapshot()
    {
        Tick(forceProjectCheck: true);
        if (!_catalogLoaded) RefreshCatalog(preserveStatus: true);
        var current = _voice.ReadSelection();
        bool changedSelection = _selection is not null && (!current.Success || current.Value.Token != _selection.Token);
        _selection = current.Success ? current.Value : null;
        if (changedSelection)
        {
            _selectedResource = null;
            if (!Busy && !_showFolder) _status = "台词或修订已变化，请重新选择声音。";
        }
        var document = _editor.ReadSelection();
        string boundName = _selection?.ResourceId is string bound
            ? _catalog.FirstOrDefault(item => item.Id == bound)?.DisplayName ?? bound
            : "未绑定本模块语音";
        string summary = _selection is null ? current.Error?.Message ?? "请先在编辑器选择台词。"
            : $"当前台词：{_selection.DialogueText}\n当前绑定：{boundName}";
        string publication = ReadPublicationStatus?.Invoke() ?? string.Empty;
        if (publication.Length != 0) summary += "\n" + publication;
        VoiceFolderScan? scan = _showFolder ? _folders.Current : null;
        if (scan is not null)
            summary = $"语音文件夹：{scan.RootPath}\n{scan.Files.Count} 个音频 · {(scan.Recursive ? "含子目录" : "仅当前目录")}\n导入后从声音列表选择并绑定台词。";
        bool selectedIsAvailable = _selectedResource is not null && _catalog.Any(item => item.Id == _selectedResource);
        IReadOnlyList<ToolListItem> items = scan is not null
            ? scan.Files.Select((file, index) => new ToolListItem("folder:" + index, file.RelativePath, Enabled: false)).ToArray()
            : _catalog.Select(item => new ToolListItem(item.Id, item.DisplayName, item.Id == _selectedResource, !Busy && _selection is not null)).ToArray();
        return new(summary,
            new ToolButton[]
            {
                new("choose-folder", "选择语音文件夹", !Busy),
                new("refresh", _showFolder ? "刷新文件夹" : "刷新声音列表", !Busy),
                new("show-source", _showFolder ? "查看已导入声音" : "查看文件夹文件", !Busy && _folders.Current is not null),
                // One slot serves both modes: browsing a folder needs the recursive toggle, the normal voice view has
                // room for the cleanup action — and a page carries at most 8 actions, so a ninth one would make the
                // whole page unreadable ("工具暂时无法读取当前状态").
                scan is not null
                    ? new("recursive", _folders.Recursive ? "包含子目录：开" : "包含子目录：关", !Busy, _folders.Recursive)
                    : new("cleanup-voices",
                        _cleanupPlan is null ? "清理失效语音条目" : $"确认删除 {_cleanupPlan.Missing.Count} 条",
                        !Busy),
                scan is not null
                    ? new("import-folder", "导入此文件夹音频", !Busy && _folderProject is not null && scan.Files.Count > 0)
                    : new("apply", "应用所选声音", !Busy && _selection is not null && selectedIsAvailable),
                new("remove", "移除本句语音", !Busy && _selection?.ResourceId is not null),
                new("undo", "撤销最近编辑", !Busy && document.Success && document.Value.CanUndo),
                new("cancel", "取消当前操作", Busy)
            }, items, _status, ItemActionId: "select", AllowSearch: true);
    }

    internal void Handle(ToolAction action)
    {
        if (_disposed) return;
        Tick(forceProjectCheck: true);
        if (Busy && action.Id != "cancel") return;
        // Arming the deletion is deliberate and immediate: anything else disarms it again.
        if (action.Id != "cleanup-voices") _cleanupPlan = null;
        switch (action.Id)
        {
            case "choose-folder":
                var project = _captureProject();
                if (!project.Success) { _status = project.Error?.Message ?? "请先保存并打开项目。"; break; }
                _folderProject = project.Value;
                _folders.Choose();
                _status = _folders.Status;
                break;
            case "refresh":
                if (_showFolder) { _folders.Rescan(); _status = _folders.Status; }
                else RefreshCatalog();
                break;
            case "show-source":
                if (_folders.Current is not null) _showFolder = !_showFolder;
                if (!_showFolder) RefreshCatalog();
                else _status = "确认以上音频后，点击“导入此文件夹音频”。";
                break;
            case "recursive": _folders.ToggleRecursive(); _status = _folders.Status; break;
            case "import-folder": StartImport(); break;
            case "cancel":
                _folders.Cancel();
                _importCancellation?.Cancel();
                _status = "正在取消；已经存在的语音和台词绑定保持不变。";
                break;
            case "select":
                if (!_showFolder && action.Value is not null && _catalog.Any(item => item.Id == action.Value))
                {
                    var selected = _voice.ReadSelection();
                    _selection = selected.Success ? selected.Value : null;
                    _selectedResource = action.Value;
                    _status = _selection is null ? selected.Error?.Message ?? "请先选择台词。" : "已选择声音，点击应用写入当前台词。";
                }
                break;
            case "apply": if (!_showFolder && _selectedResource is not null) Apply(_selectedResource); break;
            case "remove": Apply(null); break;
            case "cleanup-voices": CleanupVoices(); break;
            case "undo":
                var undone = _editor.Undo();
                _status = undone.Success ? "已撤销最近一次编辑。" : undone.Error?.Message ?? "撤销失败。";
                _selection = null;
                _selectedResource = null;
                break;
        }
    }

    /// <summary>
    /// The project's voice list can name audio that is not there — an imported lobby whose bundled voice was never
    /// brought along leaves dozens of entries behind, and the editor refuses to compile while they exist. The first
    /// click only reports what it found; the second one deletes exactly the missing entries, after copying the
    /// manifest and the editor's voice index next to our own settings.
    ///
    /// <para>
    /// This writes the project manifest on disk, which the editor treats as an outside change to a file it holds in
    /// memory: until the project is closed and reopened, saving and compiling stay locked. The warning is part of the
    /// confirmation text on purpose — the official catalog path (AzureArchive.Automation) is being measured, and
    /// until it is proven this action must say what it costs.
    /// </para>
    /// </summary>
    private void CleanupVoices()
    {
        if (_cleanupPlan is null)
        {
            var project = _captureProject();
            if (!project.Success) { _status = project.Error?.Message ?? "请先保存并打开项目。"; return; }
            try
            {
                VoiceOverrideCleanupPlan plan = VoiceOverrideCleanupPolicy.Inspect(project.Value.RootPath);
                if (plan.Missing.Count == 0)
                {
                    _status = plan.Total == 0
                        ? "这个项目还没有语音条目。"
                        : $"检查了 {plan.Total} 条语音，音频都在，无需清理。";
                    return;
                }

                _cleanupPlan = plan;
                _status = $"发现 {plan.Missing.Count} / {plan.Total} 条语音的音频文件不在项目里 —— "
                    + "编辑器保存或编译时会因此失败。再次点击按钮确认删除；删除前会把 manifest 与语音索引备份到本模块的配置目录。"
                    + "注意：这是直接改写工程清单文件，编辑器会认为清单被外部修改，"
                    + "删除后必须关闭并重新打开这个工程（或重启），保存与编译才会恢复。";
            }
            catch (Exception ex)
            {
                _status = "无法检查语音条目：" + ex.Message;
            }

            return;
        }

        VoiceOverrideCleanupPlan pending = _cleanupPlan;
        _cleanupPlan = null;
        try
        {
            VoiceOverrideCleanupResult result = VoiceOverrideCleanupPolicy.Apply(pending, _backupRoot);
            if (result.RemovedCount == 0)
            {
                _status = "没有需要删除的条目（音频已恢复，或条目已被清理过）。";
                return;
            }

            _status = $"已删除 {result.RemovedCount} 条失效语音，剩余 {result.RemainingCount} 条；"
                + $"备份：{result.BackupDirectory}。"
                + "编辑器在内存里持有工程清单，现在请关闭并重新打开这个工程（或重启），否则保存与编译会一直报"
                + "“工程资源清单被外部修改”。";
        }
        catch (Exception ex)
        {
            _status = "清理未完成：" + ex.Message;
        }
    }

    private void StartImport()
    {
        VoiceFolderScan? scan = _folders.Current;
        if (_folderProject is null || scan is null || scan.Files.Count == 0) return;
        var current = _captureProject();
        if (!current.Success || !SameProject(current.Value.Identity, _folderProject.Identity))
        {
            _status = "项目已变化，请重新选择语音文件夹。";
            return;
        }
        _importCancellation = new CancellationTokenSource();
        _importProjectIdentity = _folderProject.Identity;
        try
        {
            _importTask = _import(_folderProject, scan, _importCancellation.Token);
            _status = $"正在导入 {scan.Files.Count} 个音频到当前项目…";
        }
        catch (Exception ex)
        {
            _importCancellation.Dispose(); _importCancellation = null;
            _status = "无法开始导入：" + ex.Message;
        }
    }

    private void PollImport()
    {
        Task<VoiceImportResult>? task = _importTask;
        if (task is null || !task.IsCompleted) return;
        _importTask = null;
        bool sameProject = _folderProject is not null && SameProject(_folderProject.Identity, _importProjectIdentity);
        try
        {
            VoiceImportResult imported = task.GetAwaiter().GetResult();
            if (!sameProject) return;
            if (!SameProject(imported.ProjectRoot, _importProjectIdentity))
            {
                _status = "导入结果的项目不一致，请检查原项目资源。";
                return;
            }
            _showFolder = false;
            _selectedResource = null;
            RefreshCatalog(preserveStatus: true);
            _status = $"导入完成：新增 {imported.ImportedCount} 个，已有 {imported.AlreadyPresentCount} 个，跳过 {imported.SkippedFiles} 个。请从声音列表选择。";
            if (imported.RestoredNativeCount > 0) _status += $" 已恢复 {imported.RestoredNativeCount} 条原有资源路径。";
            if (imported.AmbiguousRestoreCount > 0) _status += $" {imported.AmbiguousRestoreCount} 个同名冲突未自动恢复。";
            if (imported.CorrectedContainers > 0) _status += $" {imported.CorrectedContainers} 个文件的扩展名与内容不符（例如 .wav 里其实是 Ogg），已按实际格式保存。";
            if (imported.UnrecognizedContainers > 0) _status += $" {imported.UnrecognizedContainers} 个文件无法识别音频格式，仍按扩展名保存，可能无法播放。";
        }
        catch (OperationCanceledException) { if (sameProject) _status = "已取消导入；原有声音和台词绑定保持不变。"; }
        catch (Exception ex) { if (sameProject) _status = "导入未完成：" + ex.Message; }
        finally
        {
            _importCancellation?.Dispose(); _importCancellation = null;
            _importProjectIdentity = null;
        }
    }

    private void RefreshCatalog(bool preserveStatus = false)
    {
        var result = _voice.ReadCatalog();
        _catalog = result.Success ? result.Value : Array.Empty<VoiceResource>();
        _catalogLoaded = true;
        if (_selectedResource is not null && !_catalog.Any(item => item.Id == _selectedResource)) _selectedResource = null;
        if (!preserveStatus || !result.Success)
            _status = result.Success ? $"找到 {_catalog.Count} 个已导入声音，可在列表中搜索。" : result.Error?.Message ?? "无法刷新声音列表。";
    }

    private void Apply(string? resourceId)
    {
        if (_selection is null) { _status = "请先选择台词。"; return; }
        var applied = _voice.Apply(new(_selection.Token, _selection.Revision, resourceId));
        if (applied.Success)
        {
            _selection = applied.Value.Selection;
            _status = applied.Value.Changed
                ? resourceId is null ? "已移除本句语音绑定。" : "语音已绑定到当前台词。"
                : "当前绑定已是所选状态。";
        }
        else
        {
            _status = applied.Error?.Message ?? "语音绑定失败。";
            _selection = null;
            _selectedResource = null;
        }
    }

    private static bool SameProject(string? a, string? b) => !string.IsNullOrEmpty(a)
        && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _folders.Dispose();
        _importCancellation?.Cancel();
        _importCancellation?.Dispose(); _importCancellation = null;
    }
}
