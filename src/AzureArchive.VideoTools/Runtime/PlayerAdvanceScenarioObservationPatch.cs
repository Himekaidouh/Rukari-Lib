using System;
using System.Reflection;
using AzureArchive.VideoTools.Interop;
using FlatData;
using HarmonyLib;

namespace AzureArchive.VideoTools.Runtime;

internal static class PlayerAdvanceScenarioObservationPatch
{
    private static PlayerCommandObservationStage _stage;
    private static long _prefixOnlySequence;

    public static bool Install(PlayerCommandObservationStage stage)
    {
        _stage = stage;
        if (stage == PlayerCommandObservationStage.Disabled)
        {
            return false;
        }

        MethodInfo? target = AccessTools.Method(
            typeof(Test),
            "AdvanceScenario",
            new[] { typeof(bool), typeof(IScenarioScriptExcel) });
        MethodInfo? prefix = AccessTools.Method(
            typeof(PlayerAdvanceScenarioObservationPatch),
            nameof(Prefix),
            new[] { typeof(bool) });
        MethodInfo? postfix = stage >= PlayerCommandObservationStage.WindowCapture
            ? AccessTools.Method(
                typeof(PlayerAdvanceScenarioObservationPatch),
                nameof(Postfix),
                new[] { typeof(bool) })
            : null;

        if (target == null || prefix == null
            || (stage >= PlayerCommandObservationStage.WindowCapture && postfix == null))
        {
            Plugin.Logger.LogError(
                "Player command observation patch was not installed: exact target or managed run-original callback was not found.");
            return false;
        }

        try
        {
            var harmony = new Harmony(Plugin.Guid + ".player-command-observation");
            harmony.Patch(
                target,
                prefix: new HarmonyMethod(prefix),
                postfix: postfix == null ? null : new HarmonyMethod(postfix));
            Plugin.Logger.LogInfo(
                $"Player command observation patch installed: stage={stage}; "
                + "target=Test.AdvanceScenario(Boolean,IScenarioScriptExcel); callbackState=Harmony-managed-runOriginal; nativeObjectsMarshaled=false.");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError(
                $"Player command observation patch installation failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public static void Prefix(bool __runOriginal)
    {
        // Voice admission runs before this prefix. A blocked attempt is not a
        // scene window: issuing a sequence would prematurely age slot pendings.
        if (!__runOriginal) return;

        if (_stage == PlayerCommandObservationStage.PrefixOnly)
        {
            PlayerCommandObservationRuntime.ObservePrefixOnly(
                System.Threading.Interlocked.Increment(ref _prefixOnlySequence));
            return;
        }

        Interop.OfficialStoragePathBridge.CaptureOnMainThread(force: true);
        PlayerAdvanceObservationWindow.Open(ReadPlaybackRow(), Interop.ActiveProjectPairSource.StorageRevision);
    }

    public static void Postfix(bool __runOriginal)
    {
        if (!__runOriginal) return;
        PlayerAdvanceObservationWindow.Close(ReadPlaybackRow());
    }

    private static bool _rowReadFailureReported;

    /// <summary>
    /// Engine row index of the card currently playing (<c>Test.cur</c>), or -1
    /// when it cannot be read. Cards with identical dialogue compile to the same
    /// script, so this row is what tells their playback records apart. Typed
    /// access on purpose: the reflection helper resolves PROPERTIES only, and
    /// reading <c>Test.Instance</c> through it failed silently (2026-09-18).
    /// </summary>
    private static int ReadPlaybackRow()
    {
        try
        {
            Test? player = Test.Instance;
            return player == null ? -1 : player.cur;
        }
        catch (Exception ex)
        {
            if (!_rowReadFailureReported)
            {
                _rowReadFailureReported = true;
                Plugin.Logger.LogWarning(
                    $"Playback row read failed (row-indexed command matching disabled "
                    + $"for this session): {ex.GetType().Name}: {ex.Message}");
            }

            return -1;
        }
    }
}
