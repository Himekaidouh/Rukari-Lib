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
    private string? _currentProjectIdentity;
    private string? _catalogProjectIdentity;
    private string? _projectContextError;
    private string? _selectedResource;
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
        VoiceFolderController? folders = null, Func<long>? clock = null)
    {
        _voice = voice;
        _editor = editor;
        _captureProject = captureProject;
        _import = import;
        _folders = folders ?? new VoiceFolderController();
        _clock = clock ?? (() => Environment.TickCount64);
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
        if (forceProjectCheck || (_folderProject is not null
            && (completion || (Busy && _clock() >= _nextProjectCheck))))
        {
            _nextProjectCheck = _clock() + 250;
            var currentProject = _captureProject();
            string? currentIdentity = currentProject.Success ? currentProject.Value.Identity : null;
            _projectContextError = currentProject.Success ? null
                : currentProject.Error?.Message ?? "请先保存并打开项目。";
            if (currentIdentity is not null && !SameProject(currentIdentity, _catalogProjectIdentity))
            {
                // Catalog-only pages also follow project switches. Never attach the previous
                // project's list or pending choice to the new project's navigation context.
                _catalog = Array.Empty<VoiceResource>();
                _catalogLoaded = false;
                _selectedResource = null;
                _selection = null;
            }
            if (currentIdentity is not null) _catalogProjectIdentity = currentIdentity;
            else
            {
                // A temporarily unavailable project has its own empty view. Retain the last
                // verified catalog for recovery, but never retain a choice that could be bound.
                _selectedResource = null;
                _selection = null;
            }
            _currentProjectIdentity = currentIdentity;
            if (_folderProject is not null && !SameProject(currentIdentity, _folderProject.Identity))
            {
                _importCancellation?.Cancel();
                _folders.Clear();
                _folderProject = null;
                _showFolder = false;
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
        if (_currentProjectIdentity is not null && !_catalogLoaded) RefreshCatalog(preserveStatus: true);
        var current = _voice.ReadSelection();
        bool changedSelection = _selection is not null && (!current.Success || current.Value.Token != _selection.Token);
        _selection = _currentProjectIdentity is not null && current.Success ? current.Value : null;
        if (changedSelection)
        {
            _selectedResource = null;
            if (!Busy && !_showFolder) _status = "台词或修订已变化，请重新选择声音。";
        }
        var document = _editor.ReadSelection();
        string boundName = _selection?.ResourceId is string bound
            ? _catalog.FirstOrDefault(item => item.Id == bound)?.DisplayName ?? bound
            : "未绑定本模块语音";
        string summary = _selection is null ? _projectContextError ?? current.Error?.Message ?? "请先在编辑器选择台词。"
            : $"当前台词：{_selection.DialogueText}\n当前绑定：{boundName}";
        string publication = ReadPublicationStatus?.Invoke() ?? string.Empty;
        if (publication.Length != 0) summary += "\n" + publication;
        VoiceFolderScan? scan = _showFolder ? _folders.Current : null;
        if (scan is not null)
            summary = $"语音文件夹：{scan.RootPath}\n{scan.Files.Count} 个音频 · {(scan.Recursive ? "含子目录" : "仅当前目录")}\n导入后从声音列表选择并绑定台词。";
        bool selectedIsAvailable = _selectedResource is not null && _catalog.Any(item => item.Id == _selectedResource);
        IReadOnlyList<ToolListItem> items = _currentProjectIdentity is null ? Array.Empty<ToolListItem>() : scan is not null
            ? scan.Files.Select((file, index) => new ToolListItem("folder:" + index, file.RelativePath, Enabled: false)).ToArray()
            : _catalog.Select(item => new ToolListItem(item.Id, item.DisplayName, item.Id == _selectedResource, !Busy && _selection is not null)).ToArray();
        return new(summary,
            new ToolButton[]
            {
                new("choose-folder", "选择语音文件夹", !Busy),
                new("refresh", _showFolder ? "刷新文件夹" : "刷新声音列表", !Busy),
                new("show-source", _showFolder ? "查看已导入声音" : "查看文件夹文件", !Busy && _folders.Current is not null),
                new("recursive", _folders.Recursive ? "包含子目录：开" : "包含子目录：关", scan is not null && !Busy, _folders.Recursive),
                scan is not null
                    ? new("import-folder", "导入此文件夹音频", !Busy && _folderProject is not null && scan.Files.Count > 0)
                    : new("apply", "应用所选声音", !Busy && _selection is not null && selectedIsAvailable),
                new("remove", "移除本句语音", !Busy && _selection?.ResourceId is not null),
                new("undo", "撤销最近编辑", !Busy && document.Success && document.Value.CanUndo),
                new("cancel", "取消当前操作", Busy)
            }, items, _status, ItemActionId: "select", AllowSearch: true)
        {
            // Changing dialogue/revision must not reset the user's position in a project-wide
            // sound list. Folder previews and imported sounds have separate navigation state.
            ListNavigation = new ToolListNavigationOptions(NavigationContext(scan), ToolListSearchMode.Locate)
        };
    }

    private string NavigationContext(VoiceFolderScan? scan)
    {
        if (_currentProjectIdentity is null) return "unavailable";
        string project = _currentProjectIdentity.ToUpperInvariant();
        return scan is null ? "catalog|" + project
            : "folder|" + project + "|" + scan.RootPath.ToUpperInvariant()
                + (scan.Recursive ? "|recursive" : "|direct");
    }

    internal void Handle(ToolAction action)
    {
        if (_disposed) return;
        Tick(forceProjectCheck: true);
        if (Busy && action.Id != "cancel") return;

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
                if (_currentProjectIdentity is not null && !_showFolder && action.Value is not null && _catalog.Any(item => item.Id == action.Value))
                {
                    var selected = _voice.ReadSelection();
                    _selection = selected.Success ? selected.Value : null;
                    _selectedResource = action.Value;
                    _status = _selection is null ? selected.Error?.Message ?? "请先选择台词。" : "已选择声音，点击应用写入当前台词。";
                }
                break;
            case "apply": if (!_showFolder && _selectedResource is not null) Apply(_selectedResource); break;
            case "remove": Apply(null); break;
            case "undo":
                var undone = _editor.Undo();
                _status = undone.Success ? "已撤销最近一次编辑。" : undone.Error?.Message ?? "撤销失败。";
                _selection = null;
                _selectedResource = null;
                break;
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
        if (_currentProjectIdentity is null)
        {
            _status = _projectContextError ?? "请先保存并打开项目。";
            return;
        }
        var result = _voice.ReadCatalog();
        _catalog = result.Success ? result.Value : Array.Empty<VoiceResource>();
        _catalogLoaded = true;
        if (_selectedResource is not null && !_catalog.Any(item => item.Id == _selectedResource)) _selectedResource = null;
        if (!preserveStatus || !result.Success)
            _status = result.Success ? $"找到 {_catalog.Count} 个已导入声音，输入名称并按回车可定位所在页。" : result.Error?.Message ?? "无法刷新声音列表。";
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
