extern alias unitycore;

using System;
using System.Diagnostics;
using Rukari.Lib;
using Rukari.Lib.Editor;

namespace AzureArchive.VideoTools.Interop;

/// <summary>
/// On-demand save for the editor (2026-09-21): the three steps the automatic path runs, triggered by
/// the author instead of by waiting.
/// <para>
/// The interface's own save button runs <c>Save(false)</c> and then <c>Compile()</c>; the overload
/// that writes the playable archive — <c>Compile(fileName)</c> — is never reached from any interface
/// action, which is why a saved project can still play back its previous revision. This service
/// calls all three in order and reports them separately, so a partial result is never shown as
/// success. Nothing here is invented: every call is the editor's own API, reached through the live
/// <see cref="Studio.Scripts.StudioCommon"/> component.
/// </para>
/// </summary>
internal sealed class EditorSaveService : IEditorSaveService
{
    public ModResult<EditorSaveReceipt> SaveAndPublish()
    {
        IModRuntime? runtime = ModServices.Current;
        if (runtime != null && !runtime.IsMainThread)
        {
            return ModResult<EditorSaveReceipt>.Fail(
                ModErrorCode.WrongThread,
                "保存只能在游戏主线程上执行。");
        }

        Studio.Scripts.StudioCommon? common;
        try
        {
            // The probes only ever see this component as a Harmony __instance; the button has to find
            // it without a patch. It is a MonoBehaviour, so the official lookup is enough. The
            // generic overload is the one this repository already ships (caption runtime, audio
            // manager); the hazard recorded for this interop build is GetComponent*, not this.
            common = unitycore::UnityEngine.Object.FindObjectOfType<Studio.Scripts.StudioCommon>();
        }
        catch (Exception ex)
        {
            return ModResult<EditorSaveReceipt>.Fail(
                ModErrorCode.ProviderFailed,
                $"找不到编辑器会话：{ex.GetType().Name}: {ex.Message}");
        }

        if (common == null || common.WasCollected)
        {
            return ModResult<EditorSaveReceipt>.Fail(
                ModErrorCode.NotReady,
                "编辑器会话尚未就绪：先打开一个工程（项目模式 → 编辑）。");
        }

        string name = common.projectName ?? string.Empty;
        if (name.Length == 0)
        {
            return ModResult<EditorSaveReceipt>.Fail(
                ModErrorCode.NotReady,
                "编辑器没有工程名，保存被跳过。");
        }

        long started = Stopwatch.GetTimestamp();
        bool saved = false;
        bool compiled = false;
        bool published = false;
        try
        {
            common.Save(false);
            saved = true;
        }
        catch (Exception ex)
        {
            return ModResult<EditorSaveReceipt>.Ok(new EditorSaveReceipt(
                name, false, false, false, Elapsed(started),
                $"保存失败：{ex.GetType().Name}: {ex.Message}"));
        }

        try
        {
            common.Compile();
            compiled = true;
        }
        catch (Exception ex)
        {
            return ModResult<EditorSaveReceipt>.Ok(new EditorSaveReceipt(
                name, saved, false, false, Elapsed(started),
                $"编译失败（工程已保存）：{ex.GetType().Name}: {ex.Message}"));
        }

        try
        {
            // The overload that writes data\saves\<name>.aas, which formal playback reads.
            common.Compile(name);
            published = true;
        }
        catch (Exception ex)
        {
            return ModResult<EditorSaveReceipt>.Ok(new EditorSaveReceipt(
                name, saved, compiled, false, Elapsed(started),
                $"发布失败（工程与构建已写入）：{ex.GetType().Name}: {ex.Message}"));
        }

        return ModResult<EditorSaveReceipt>.Ok(new EditorSaveReceipt(
            name, saved, compiled, published, Elapsed(started), "保存 → 编译 → 发布 全部完成"));
    }

    private static int Elapsed(long startedTicks) =>
        (int)Math.Min(
            int.MaxValue,
            (long)((Stopwatch.GetTimestamp() - startedTicks) * 1000.0 / Stopwatch.Frequency));
}
