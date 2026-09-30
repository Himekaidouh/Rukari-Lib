using System;
using System.Reflection;
using FlatData;
using HarmonyLib;

namespace AzureArchive.VideoTools.Runtime;

/// <summary>
/// Read-only observation of the player dialogue pipeline.
/// Round 3: watches every write to the dialogue/speaker UILabels plus the
/// queued typewriter lifecycle, to establish exactly how the previous line
/// disappears and what the typewriter receives. Only lengths are logged by
/// default; short content prefixes require an explicit opt-in flag.
/// Every IL2CPP access is guarded; failures degrade to a single warning.
/// </summary>
internal static class DialogueFlowObservationProbe
{
    private static bool _installed;
    private static int _sequence;
    private static bool _accessWarningLogged;
    private static bool _includeContent;
    private static UILabel? _dialogLabel;
    private static UILabel? _nameLabel;
    private static UILabel? _nicknameLabel;

    public static bool Install(bool includeContent)
    {
        if (_installed)
        {
            return true;
        }

        _includeContent = includeContent;

        MethodInfo? clear = AccessTools.Method(typeof(Test), "ClearDialogText");
        MethodInfo? parseScript = AccessTools.Method(
            typeof(Utils.ScenarioUtil),
            "ParseScript",
            new[] { typeof(IScenarioScriptExcel), typeof(Test), typeof(Enums.Language) });
        MethodInfo? typewriterStart = AccessTools.Method(
            typeof(ScenarioAnimation.TextTypewriterAnimation),
            "Start");
        MethodInfo? typewriterComplete = AccessTools.Method(
            typeof(ScenarioAnimation.TextTypewriterAnimation),
            "Complete",
            new[] { typeof(bool) });
        MethodInfo? queuedStart = AccessTools.Method(
            typeof(ScenarioAnimation.QueuedTypewriterAnimation),
            "Start");
        MethodInfo? queuedComplete = AccessTools.Method(
            typeof(ScenarioAnimation.QueuedTypewriterAnimation),
            "Complete",
            new[] { typeof(bool) });
        MethodInfo? queuedCancel = AccessTools.Method(
            typeof(ScenarioAnimation.QueuedTypewriterAnimation),
            "Cancel",
            new[] { typeof(bool) });
        MethodInfo? queuedSubAnim = AccessTools.Method(
            typeof(ScenarioAnimation.QueuedTypewriterAnimation),
            "OnSubAnimComplete");

        if (clear == null || parseScript == null
            || typewriterStart == null || typewriterComplete == null
            || queuedStart == null || queuedComplete == null
            || queuedCancel == null || queuedSubAnim == null
            )
        {
            Plugin.Logger.LogError(
                "Dialogue flow probe was not installed: one or more targets were not found "
                + $"clear={clear != null} parseScript={parseScript != null} "
                + $"typewriterStart={typewriterStart != null} typewriterComplete={typewriterComplete != null} "
                + $"queuedStart={queuedStart != null} queuedComplete={queuedComplete != null} "
                + $"queuedCancel={queuedCancel != null} queuedSubAnim={queuedSubAnim != null}.");
            return false;
        }

        try
        {
            var harmony = new Harmony(Plugin.Guid + ".dialogue-flow-probe");
            harmony.Patch(
                clear,
                prefix: new HarmonyMethod(AccessTools.Method(
                    typeof(DialogueFlowObservationProbe), nameof(ClearDialogPrefix))),
                postfix: new HarmonyMethod(AccessTools.Method(
                    typeof(DialogueFlowObservationProbe), nameof(ClearDialogPostfix))));
            harmony.Patch(
                parseScript,
                postfix: new HarmonyMethod(AccessTools.Method(
                    typeof(DialogueFlowObservationProbe), nameof(ParseScriptPostfix))));
            harmony.Patch(
                typewriterStart,
                prefix: new HarmonyMethod(AccessTools.Method(
                    typeof(DialogueFlowObservationProbe), nameof(TypewriterStartPrefix))),
                postfix: new HarmonyMethod(AccessTools.Method(
                    typeof(DialogueFlowObservationProbe), nameof(TypewriterStartPostfix))));
            harmony.Patch(
                typewriterComplete,
                postfix: new HarmonyMethod(AccessTools.Method(
                    typeof(DialogueFlowObservationProbe), nameof(TypewriterCompletePostfix))));
            harmony.Patch(
                queuedStart,
                postfix: new HarmonyMethod(AccessTools.Method(
                    typeof(DialogueFlowObservationProbe), nameof(QueuedStartPostfix))));
            harmony.Patch(
                queuedComplete,
                postfix: new HarmonyMethod(AccessTools.Method(
                    typeof(DialogueFlowObservationProbe), nameof(QueuedCompletePostfix))));
            harmony.Patch(
                queuedCancel,
                postfix: new HarmonyMethod(AccessTools.Method(
                    typeof(DialogueFlowObservationProbe), nameof(QueuedCancelPostfix))));
            harmony.Patch(
                queuedSubAnim,
                postfix: new HarmonyMethod(AccessTools.Method(
                    typeof(DialogueFlowObservationProbe), nameof(QueuedSubAnimPostfix))));
            _installed = true;
            Plugin.Logger.LogInfo(
                "Dialogue flow observation probe installed (round 3); "
                + "targets=Test.ClearDialogText, Utils.ScenarioUtil.ParseScript, "
                + "TextTypewriterAnimation.Start/Complete, QueuedTypewriterAnimation."
                + "Start/Complete/Cancel/OnSubAnimComplete, UILabel.set_text (dialog "
                + "labels only); data=lengths"
                + (_includeContent ? "+short-content" : string.Empty)
                + "; observationOnly=True.");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError(
                $"Dialogue flow probe installation failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public static void ClearDialogPrefix()
    {
        PlayerCommandObservationLog.Append(
            $"{PlayerCommandObservationRuntime.CreateStamp()} dialog-flow seq={++_sequence}; "
            + $"event=clear-prefix; window={PlayerCommandObservationRuntime.LastObservedWindowSequence}; "
            + $"textLen={SafeLabelLength(_dialogLabel)}; nameLen={SafeLabelLength(_nameLabel)}; "
            + $"nicknameLen={SafeLabelLength(_nicknameLabel)}");
    }

    public static void ClearDialogPostfix()
    {
        PlayerCommandObservationLog.Append(
            $"{PlayerCommandObservationRuntime.CreateStamp()} dialog-flow seq={++_sequence}; "
            + $"event=clear-postfix; window={PlayerCommandObservationRuntime.LastObservedWindowSequence}; "
            + $"textLen={SafeLabelLength(_dialogLabel)}; nameLen={SafeLabelLength(_nameLabel)}; "
            + $"nicknameLen={SafeLabelLength(_nicknameLabel)}");
    }

    public static void ParseScriptPostfix(Utils.ScenarioUtil.ScriptInfo? __result)
    {
        if (__result == null)
        {
            PlayerCommandObservationLog.Append(
                $"{PlayerCommandObservationRuntime.CreateStamp()} dialog-flow seq={++_sequence}; "
                + "event=parse-script; result=null");
            return;
        }

        int textLength = Safe(() => __result.text?.Length ?? -1);
        int prefixLength = Safe(() => __result.textPrefix?.Length ?? -1);
        int suffixLength = Safe(() => __result.textSuffix?.Length ?? -1);
        int speakerIdLength = Safe(() => __result.speakerID?.Length ?? -1);
        PlayerCommandObservationLog.Append(
            $"{PlayerCommandObservationRuntime.CreateStamp()} dialog-flow seq={++_sequence}; "
            + "event=parse-script; "
            + $"textLen={textLength}; prefixLen={prefixLength}; suffixLen={suffixLength}; "
            + $"speakerIdLen={speakerIdLength}");
    }

    public static void TypewriterStartPrefix(
        ScenarioAnimation.TextTypewriterAnimation __instance)
    {
        RefreshCachedLabels(__instance);
        PlayerCommandObservationLog.Append(
            $"{PlayerCommandObservationRuntime.CreateStamp()} dialog-flow seq={++_sequence}; "
            + "event=typewriter-start-prefix");
    }

    public static void TypewriterStartPostfix(
        ScenarioAnimation.TextTypewriterAnimation __instance)
    {
        PlayerCommandObservationLog.Append(
            $"{PlayerCommandObservationRuntime.CreateStamp()} dialog-flow seq={++_sequence}; "
            + "event=typewriter-start; "
            + $"targetTxt={Describe(Safe(() => __instance.targetTxt))}; "
            + $"labelBefore={Describe(Safe(() => __instance.text?.text))}");
    }

    public static void TypewriterCompletePostfix(
        ScenarioAnimation.TextTypewriterAnimation __instance)
    {
        PlayerCommandObservationLog.Append(
            $"{PlayerCommandObservationRuntime.CreateStamp()} dialog-flow seq={++_sequence}; "
            + "event=typewriter-complete; "
            + $"targetTxt={Describe(Safe(() => __instance.targetTxt))}; "
            + $"labelAfter={Describe(Safe(() => __instance.text?.text))}");
    }

    public static void QueuedStartPostfix()
    {
        LogQueued("queued-start");
    }

    public static void QueuedCompletePostfix()
    {
        LogQueued("queued-complete");
    }

    public static void QueuedCancelPostfix()
    {
        LogQueued("queued-cancel");
    }

    public static void QueuedSubAnimPostfix()
    {
        LogQueued("queued-sub-anim");
    }

    private static void LogQueued(string eventName)
    {
        PlayerCommandObservationLog.Append(
            $"{PlayerCommandObservationRuntime.CreateStamp()} dialog-flow seq={++_sequence}; "
            + $"event={eventName}; window={PlayerCommandObservationRuntime.LastObservedWindowSequence}");
    }

    private static void RefreshCachedLabels(
        ScenarioAnimation.TextTypewriterAnimation instance)
    {
        Safe(() =>
        {
            _dialogLabel = instance.text;
            return true;
        });
        Safe(() =>
        {
            Test? test = Test.Instance;
            if (ReferenceEquals(test, null))
            {
                return true;
            }

            _nameLabel = test.nameText;
            _nicknameLabel = test.nicknameText;
            return true;
        });
    }

    private static string Describe(string? value)
    {
        if (value == null)
        {
            return "null";
        }

        if (!_includeContent)
        {
            return $"len={value.Length}";
        }

        string escaped = value
            .Replace("\\", "\\\\")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n")
            .Replace(";", "\\;");
        return escaped.Length <= 24
            ? $"'{escaped}'"
            : $"'{escaped[..24]}�?{escaped.Length - 24}'";
    }

    private static int SafeLabelLength(UILabel? label)
    {
        if (ReferenceEquals(label, null))
        {
            return -2;
        }

        return Safe(() => label.text?.Length ?? -1);
    }

    private static T Safe<T>(Func<T> access)
    {
        try
        {
            return access();
        }
        catch (Exception ex)
        {
            if (!_accessWarningLogged)
            {
                _accessWarningLogged = true;
                PlayerCommandObservationLog.Append(
                    $"{PlayerCommandObservationRuntime.CreateStamp()} dialog-flow warn=true; "
                    + $"reason=il2cpp-access-failed; type={ex.GetType().Name}; "
                    + "action=suppress-further-warnings");
            }

            return default!;
        }
    }
}
