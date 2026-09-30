using System;
using System.Reflection;
using AzureArchive.VideoTools.Core.Playback;
using HarmonyLib;

namespace AzureArchive.VideoTools.Runtime;

internal static class AutoDialogueDelayPatch
{
    private static bool _installed;
    private static bool _replacementLogged;
    private static bool _failureLogged;

    public static bool Install()
    {
        if (_installed)
        {
            return true;
        }

        MethodInfo? target = AccessTools.Method(
            typeof(Test),
            "DelayedAdvance",
            Type.EmptyTypes);
        MethodInfo? prefix = AccessTools.Method(
            typeof(AutoDialogueDelayPatch),
            nameof(Prefix),
            Type.EmptyTypes);
        if (target == null || prefix == null)
        {
            Plugin.Logger.LogError(
                "Auto dialogue delay patch was not installed: exact Test.DelayedAdvance or its zero-argument prefix was not found.");
            return false;
        }

        try
        {
            var harmony = new Harmony(Plugin.Guid + ".auto-dialogue-delay");
            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            _installed = true;
            Plugin.Logger.LogInfo(
                "Auto dialogue delay patch installed on Test.DelayedAdvance; official stored tier 2 remains unchanged and is mapped to 2.5 seconds only at the runtime wait boundary.");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError(
                $"Auto dialogue delay patch installation failed; official timing remains unchanged: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public static void Prefix()
    {
        try
        {
            Test? player = Test.Instance;
            if (ReferenceEquals(player, null))
            {
                return;
            }

            float officialSeconds = player.autoDelayInSeconds;
            float mappedSeconds = AutoDialogueDelayPolicy.MapRuntimeSeconds(
                officialSeconds);
            if (mappedSeconds == officialSeconds)
            {
                return;
            }

            player.autoDelayInSeconds = mappedSeconds;
            if (!_replacementLogged)
            {
                _replacementLogged = true;
                Plugin.Logger.LogInfo(
                    "Auto dialogue delay runtime tier mapped: 2.0 -> 2.5 seconds; official user setting was not rewritten.");
            }
        }
        catch (Exception ex)
        {
            if (_failureLogged)
            {
                return;
            }

            _failureLogged = true;
            Plugin.Logger.LogWarning(
                $"Auto dialogue delay mapping failed open; official timing remains active: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
