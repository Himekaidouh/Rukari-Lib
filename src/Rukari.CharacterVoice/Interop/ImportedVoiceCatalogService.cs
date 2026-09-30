using Rukari.Lib;
using Rukari.CharacterVoice.Core;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace Rukari.CharacterVoice.Interop;

internal sealed record ImportedVoiceCatalogEntry(string ResourceKey, string DisplayName);

/// <summary>Combines native sound overrides with the bounded, project-owned import index.</summary>
internal static class ImportedVoiceCatalogService
{
    private static int _mainThreadId;

    // Call during plugin/GUI initialization on the Unity main thread, before offering the refresh button.
    internal static void InitializeOnMainThread()
    {
        int current = Environment.CurrentManagedThreadId;
        if (_mainThreadId == 0) _mainThreadId = current;
        else if (_mainThreadId != current) throw new InvalidOperationException("语音资源列表只能在主线程初始化。");
    }

    internal static ModResult<IReadOnlyList<ImportedVoiceCatalogEntry>> ReadOnMainThread()
    {
        if (_mainThreadId == 0)
            return ModResult<IReadOnlyList<ImportedVoiceCatalogEntry>>.Fail(ModErrorCode.NotReady, "语音资源列表尚未在游戏主线程初始化。");
        if (Environment.CurrentManagedThreadId != _mainThreadId)
            return ModResult<IReadOnlyList<ImportedVoiceCatalogEntry>>.Fail(ModErrorCode.WrongThread, "语音资源列表只能在游戏主线程读取。");
        try
        {
            ScenarioResourceManager resources = ScenarioResourceManager.Instance;
            if (ReferenceEquals(resources, null))
                return ModResult<IReadOnlyList<ImportedVoiceCatalogEntry>>.Fail(ModErrorCode.NotReady, "资源库尚未就绪，请进入编辑器后刷新。");

            var accepted = new List<ImportedVoiceCatalogEntry>();
            var errors = new List<string>();
            var project = ProjectVoiceImportStore.CaptureCurrentProjectOnMainThread();
            if (project.Success)
            {
                try
                {
                    foreach (ProjectVoiceImportEntry entry in ProjectVoiceImportPolicy.ReadCatalog(project.Value.RootPath))
                        accepted.Add(new(entry.ResourceKey, entry.DisplayName));
                }
                catch (Exception ex) { errors.Add("项目语音索引：" + ex.Message); }
            }
            try
            {
                var snapshot = new List<ImportedSoundOverrideSnapshot>();
                int observedCount = 0;
                Snapshot(resources.LocalOverrideData, snapshot, "当前项目", ref observedCount);
                Snapshot(resources.GlobalOverrideData, snapshot, "全局资源库", ref observedCount);
                foreach (ImportedVoiceCatalogCandidate candidate in ImportedVoiceCatalogPolicy.CreateCandidates(snapshot))
                {
                    // Imported hashes have their own resolver; never feed them into the native sound bridge.
                    if (ProjectVoiceImportPolicy.IsImportKey(candidate.ResourceKey)) continue;
                    if (!resources.TryGetSoundOverridePath(candidate.ResourceKey, out string resolved)
                        || string.IsNullOrWhiteSpace(resolved)
                        || !string.Equals(Path.GetFullPath(resolved), candidate.FullPath, StringComparison.OrdinalIgnoreCase)
                        || !File.Exists(resolved)) continue;
                    accepted.Add(new(candidate.ResourceKey, candidate.DisplayName));
                }
            }
            catch (Exception ex) { errors.Add("原生音效资源：" + ex.Message); }
            // One unavailable provider must not hide successfully imported files from the other.
            if (accepted.Count == 0 && errors.Count > 0)
                return ModResult<IReadOnlyList<ImportedVoiceCatalogEntry>>.Fail(ModErrorCode.ProviderFailed,
                    string.Join("；", errors));
            return ModResult<IReadOnlyList<ImportedVoiceCatalogEntry>>.Ok(accepted
                .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray());
        }
        catch (Exception ex)
        {
            return ModResult<IReadOnlyList<ImportedVoiceCatalogEntry>>.Fail(ModErrorCode.ProviderFailed, $"无法读取当前音效资源：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Snapshot(ResourceOverrideData data, List<ImportedSoundOverrideSnapshot> output, string source, ref int observedCount)
    {
        if (ReferenceEquals(data, null)) throw new InvalidOperationException($"{source}尚未加载。");
        var sounds = data.SoundOverrides;
        if (ReferenceEquals(sounds, null)) throw new InvalidOperationException($"{source}没有音效资源集合。");
        int count = sounds.Count;
        if (count < 0 || count > ImportedVoiceCatalogPolicy.MaximumOverridePaths - observedCount)
            throw new InvalidOperationException($"音效资源超过 {ImportedVoiceCatalogPolicy.MaximumOverridePaths} 项，已停止列举以避免编辑器长时间无响应。");
        observedCount += count;
        if (count == 0) return;
        string basePath = data.basePath ?? string.Empty;
        // CopyTo takes a string reference array. Do not receive/box the IL2CPP HashSet enumerator struct.
        var copied = new Il2CppStringArray(count);
        sounds.CopyTo(copied, 0, count);
        if (sounds.Count != count) throw new InvalidOperationException("资源列表在读取时发生变化，请刷新重试。");
        for (int index = 0; index < count; index++)
        {
            string path = copied[index];
            if (string.IsNullOrWhiteSpace(path)) continue;
            try
            {
                output.Add(new(path, Path.GetFullPath(Path.Combine(basePath, path))));
            }
            catch (ArgumentException) { /* An invalid manifest path is not offered as a voice. */ }
            catch (NotSupportedException) { }
            catch (PathTooLongException) { }
        }
    }
}
