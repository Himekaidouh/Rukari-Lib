extern alias unitycore;

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using AzureArchive.VideoTools.Core.Commands;
using FlatData;
using HarmonyLib;
using GameObject = unitycore::UnityEngine.GameObject;

namespace AzureArchive.VideoTools.Runtime;

/// <summary>
/// Continue-dialogue feature (dialogue.continue/v1).
///
/// Authoring: a bare <c>#aavt;continue</c> line in the Environment Additional
/// Prompt of a scene marks that scene as continuing from the previously
/// visible dialogue line. The sanitizer strips the line and remembers the
/// sanitized compiled-script identity here (persisted as opaque SHA-256
/// hashes only).
///
/// Playback: ParseScript receives the row about to be displayed. When its
/// ScriptKr identity is a known continue identity, the currently visible
/// dialogue label content (everything before the transparency mask) is
/// captured as the preserved prefix and the feature arms. While armed, every
/// write to that dialog label is prefixed with the preserved text, so the
/// official typewriter reveals only the new suffix exactly like its native
/// transparency-mask mechanic. A row without the marker disarms, restoring
/// the default overwrite behavior.
/// </summary>
internal static class ContinueDialogueRuntime
{
    private const string MarkerLine = "#aavt;continue";
    private const string TransparencyMask = "[00000000]";
    private const int MaximumStoredIdentities = 4096;

    private static readonly object Gate = new();
    private static readonly HashSet<string> ContinueIdentities =
        new(StringComparer.OrdinalIgnoreCase);
    private static bool _installed;
    private static bool _logLabelWrites;
    private static bool _includeContent;
    private static bool _pendingContinue;
    private static bool _dialogueChainActive;
    private static string? _pendingIdentity;
    private static string? _lastArmedIdentity;
    private static string? _lastPrefixAtArm;
    private static bool _suppressEmptyClear;
    private static bool _armed;
    private static string _preservedPrefix = string.Empty;
    private static string _lastDialogVisible = string.Empty;
    private static UILabel? _dialogLabel;
    private static GameObject? _dialogPanel;
    private static bool _holdPanelVisible;
    private static int _panelReactivations;
    private static string? _identityFilePath;

    public static bool Install(string? identityFilePath, bool includeContent, bool logLabelWrites)
    {
        if (_installed)
        {
            return true;
        }

        _includeContent = includeContent;
        _logLabelWrites = logLabelWrites;

        MethodInfo? parseScript = AccessTools.Method(
            typeof(Utils.ScenarioUtil),
            "ParseScript",
            new[] { typeof(IScenarioScriptExcel), typeof(Test), typeof(Enums.Language) });
        MethodInfo? labelSetText = AccessTools.PropertySetter(typeof(UILabel), "text");
        MethodInfo? typewriterStart = AccessTools.Method(
            typeof(ScenarioAnimation.TextTypewriterAnimation),
            "Start");
        MethodInfo? hideUi = AccessTools.Method(typeof(Test), "HideUI");
        MethodInfo? parsePostfix = typeof(ContinueDialogueRuntime).GetMethod(
            nameof(ParseScriptPostfix), BindingFlags.Public | BindingFlags.Static);
        MethodInfo? labelPrefix = typeof(ContinueDialogueRuntime).GetMethod(
            nameof(LabelSetTextPrefix), BindingFlags.Public | BindingFlags.Static);
        MethodInfo? typewriterStartPrefix = typeof(ContinueDialogueRuntime).GetMethod(
            nameof(TypewriterStartPrefix), BindingFlags.Public | BindingFlags.Static);
        if (parseScript == null || labelSetText == null || typewriterStart == null
            || hideUi == null
            || parsePostfix == null || labelPrefix == null
            || typewriterStartPrefix == null)
        {
            Plugin.Logger.LogError(
                "Continue dialogue was not installed: ParseScript, UILabel.set_text, "
                + $"TextTypewriterAnimation.Start, or Test.HideUI was not found "
                + $"parseScript={parseScript != null} "
                + $"labelSetText={labelSetText != null} "
                + $"typewriterStart={typewriterStart != null} "
                + $"hideUi={hideUi != null}.");
            return false;
        }

        try
        {
            _identityFilePath = identityFilePath;
            // v1 used an append-only, project-agnostic hash file. Those hashes
            // cannot prove that a marker still exists in the active AAP, so they
            // must never become executable after restart. The current AAP/AAS
            // index will replace this empty set with an authoritative snapshot.
            ReplaceContinueIdentities(
                Array.Empty<string>(),
                "startup-discard-untrusted-append-only-cache");
            var harmony = new Harmony(Plugin.Guid + ".dialogue-continue");
            harmony.Patch(parseScript, postfix: new HarmonyMethod(parsePostfix));
            harmony.Patch(labelSetText, prefix: new HarmonyMethod(labelPrefix));
            harmony.Patch(
                typewriterStart,
                prefix: new HarmonyMethod(typewriterStartPrefix));
            harmony.Patch(
                hideUi,
                prefix: new HarmonyMethod(AccessTools.Method(
                    typeof(ContinueDialogueRuntime), nameof(HideUIPrefix))));
            _installed = true;
            Plugin.Logger.LogInfo(
                $"Dialogue continue installed: identities={ContinueIdentities.Count}; "
                + "storage="
                + (string.IsNullOrEmpty(identityFilePath)
                    ? "memory-only"
                    : "authoritative-snapshot-file")
                + "; patches=Utils.ScenarioUtil.ParseScript postfix, UILabel.set_text "
                + "prefix (dialog label, armed rows only), TextTypewriterAnimation."
                + "Start prefix (prefix capture), Test.HideUI prefix (observation).");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError(
                $"Dialogue continue installation failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public static void RememberContinueIdentity(string sha256)
        => SetContinueIdentity(sha256, enabled: true, "editor-sanitizer");

    internal static void InvalidateStorageContext(string source)
    {
        lock (Gate)
        {
            ContinueIdentities.Clear();
            ResetPending();
            _pendingIdentity = _lastArmedIdentity = _lastPrefixAtArm = null;
            _dialogueChainActive = _suppressEmptyClear = _holdPanelVisible = false;
            _lastDialogVisible = string.Empty;
            _dialogLabel = null;
            _dialogPanel = null;
        }
        PlayerCommandObservationLog.Append($"{DateTimeOffset.Now:O} dialog-continue context-invalidated; "
            + $"source={EscapeShort(source)}; identities=0; armed=false; pending=false");
    }

    public static void ForgetContinueIdentity(string sha256, string source)
        => SetContinueIdentity(sha256, enabled: false, source);

    public static void SetContinueIdentity(string sha256, bool enabled, string source)
    {
        if (!IsValidIdentity(sha256) || string.IsNullOrWhiteSpace(source))
        {
            return;
        }

        bool changed;
        lock (Gate)
        {
            changed = enabled
                ? ContinueIdentities.Add(sha256)
                : ContinueIdentities.Remove(sha256);
            if (changed)
            {
                PersistIdentitySnapshotLocked();
            }
        }

        if (changed)
        {
            PlayerCommandObservationLog.Append(
                $"{DateTimeOffset.Now:O} dialog-continue identity-set; "
                + $"enabled={enabled}; sha16={sha256[..16]}; source={EscapeShort(source)}");
        }
    }

    public static void ReplaceContinueIdentities(
        IEnumerable<string> identities,
        string source)
    {
        ArgumentNullException.ThrowIfNull(identities);
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException("Identity source is required.", nameof(source));
        }

        string[] replacement = identities
            .Where(IsValidIdentity)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(identity => identity, StringComparer.Ordinal)
            .Take(MaximumStoredIdentities)
            .ToArray();
        lock (Gate)
        {
            ContinueIdentities.Clear();
            ContinueIdentities.UnionWith(replacement);
            PersistIdentitySnapshotLocked();
        }

        PlayerCommandObservationLog.Append(
            $"{DateTimeOffset.Now:O} dialog-continue identity-replace; "
            + $"count={replacement.Length}; source={EscapeShort(source)}");
    }

    public static void ParseScriptPostfix(
        Utils.ScenarioUtil.ScriptInfo? __result,
        IScenarioScriptExcel scenario,
        Test owner)
    {
        try
        {
            if (scenario == null || ReferenceEquals(owner, null))
            {
                ResetPending();
                return;
            }

            int textLength = Safe(() => __result?.text?.Length ?? -1);
            if (textLength <= 0)
            {
                // An empty/action row never consumes a pending marker.
                // Marked: HOLD row - the panel keeps the previous line while
                //         actions or sounds play, and the chain stays alive.
                // Unmarked: fully transparent - vanilla keeps whatever the
                //           label shows (there is no clearing code at all).
                if (IsContinueRow(scenario))
                {
                    _suppressEmptyClear = true;
                    _dialogueChainActive = true;
                    _holdPanelVisible = true;
                    _panelReactivations = 0;
                    PlayerCommandObservationLog.Append(
                        $"{PlayerCommandObservationRuntime.CreateStamp()} "
                        + "dialog-continue event=hold-row; "
                        + "action=suppress-clear+keep-panel");
                }

                return;
            }

            if (!TryGetContinueIdentity(scenario, out string? continueIdentity))
            {
                if (_dialogueChainActive || _armed)
                {
                    PlayerCommandObservationLog.Append(
                        $"{PlayerCommandObservationRuntime.CreateStamp()} "
                        + "dialog-continue event=disarm; reason=non-continue-row");
                }

                _dialogueChainActive = false;
                _holdPanelVisible = false;
                _lastArmedIdentity = null;
                _lastPrefixAtArm = null;
                ResetPending();
                return;
            }

            _pendingContinue = true;
            _pendingIdentity = continueIdentity;
            Safe(() =>
            {
                _dialogPanel = owner.dialogPanel;
                return true;
            });
            PlayerCommandObservationLog.Append(
                $"{PlayerCommandObservationRuntime.CreateStamp()} "
                + "dialog-continue event=mark; "
                + $"panel={PanelState()}");
        }
        catch (Exception ex)
        {
            ResetPending();
            PlayerCommandObservationLog.Append(
                $"{PlayerCommandObservationRuntime.CreateStamp()} "
                + $"dialog-continue event=error; type={ex.GetType().Name}; "
                + $"message={EscapeShort(ex.Message)}; action=disarmed");
        }
    }

    public static void TypewriterStartPrefix(
        ScenarioAnimation.TextTypewriterAnimation __instance)
    {
        // Always refresh the label cache: tracking must cover every row,
        // continue-marked or not.
        Safe(() =>
        {
            _dialogLabel = __instance.text;
            return true;
        });

        _holdPanelVisible = false;

        if (!_pendingContinue)
        {
            _armed = false;
            _preservedPrefix = string.Empty;
            return;
        }

        _pendingContinue = false;
        bool replaySameRow = !string.IsNullOrEmpty(_pendingIdentity)
            && string.Equals(_pendingIdentity, _lastArmedIdentity, StringComparison.Ordinal)
            && !string.IsNullOrEmpty(_lastPrefixAtArm);
        _preservedPrefix = replaySameRow
            ? _lastPrefixAtArm!
            : _lastDialogVisible;
        _lastPrefixAtArm = _preservedPrefix;
        _lastArmedIdentity = _pendingIdentity;
        _armed = true;
        _dialogueChainActive = true;
        PlayerCommandObservationLog.Append(
            $"{PlayerCommandObservationRuntime.CreateStamp()} "
            + $"dialog-continue event=arm; preservedLen={_preservedPrefix.Length}; "
            + $"source={(replaySameRow ? "replay-restore" : "tracked")}");
    }

    /// <summary>
    /// Per-frame watchdog: while a hold row keeps the dialogue panel alive,
    /// re-activate it if the player hid it for a textless row. Called from
    /// both Update and LateUpdate so a same-frame hide never reaches
    /// rendering.
    /// </summary>
    public static void UpdateTick()
    {
        if (!_holdPanelVisible
            || ReferenceEquals(_dialogPanel, null)
            || _panelReactivations >= 3600)
        {
            return;
        }

        try
        {
            if (!_dialogPanel.activeSelf)
            {
                _dialogPanel.SetActive(true);
                _panelReactivations++;
                if (_panelReactivations == 1 || _panelReactivations % 120 == 0)
                {
                    PlayerCommandObservationLog.Append(
                        $"{PlayerCommandObservationRuntime.CreateStamp()} "
                        + $"dialog-continue event=panel-reactivate; count={_panelReactivations}");
                }
            }
        }
        catch (Exception)
        {
            // Panel wrapper became invalid; drop the hold quietly.
            _holdPanelVisible = false;
        }
    }

    private static bool IsContinueRow(IScenarioScriptExcel scenario) =>
        TryGetContinueIdentity(scenario, out _);

    private static bool TryGetContinueIdentity(
        IScenarioScriptExcel scenario,
        out string? sha256)
    {
        sha256 = null;
        string? scriptKr = TryReadScriptKr(scenario);
        if (string.IsNullOrEmpty(scriptKr))
        {
            return false;
        }

        CompiledScriptIdentity identity = CommandIdentity.CompiledScript(scriptKr);
        sha256 = identity.Sha256;
        lock (Gate)
        {
            return ContinueIdentities.Contains(sha256);
        }
    }

    private static string? TryReadScriptKr(IScenarioScriptExcel scenario)
    {
        try
        {
            return scenario.ScriptKr;
        }
        catch (Exception)
        {
        }

        try
        {
            GenericScenarioExcel? casted =
                scenario.TryCast<GenericScenarioExcel>();
            if (casted != null)
            {
                return casted.ScriptKr;
            }
        }
        catch (Exception)
        {
        }

        return null;
    }

    private static string SafeWrapperTypeName(IScenarioScriptExcel scenario)
    {
        try
        {
            return scenario.GetType().Name;
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    private static string EscapeShort(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "none";
        }

        string escaped = message
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Replace(";", "\\;");
        return escaped.Length <= 96 ? escaped : escaped[..96];
    }

    public static void LabelSetTextPrefix(UILabel __instance, ref string value)
    {
        if (ReferenceEquals(__instance, null)
            || !ReferenceEquals(__instance, _dialogLabel))
        {
            return;
        }

        // Chain-active suppression: an empty action row must not wipe the
        // panel; its clear write is replaced with the preserved text.
        if (_suppressEmptyClear
            && !_armed
            && string.IsNullOrEmpty(value))
        {
            value = _lastDialogVisible;
            _suppressEmptyClear = false;
            if (_logLabelWrites)
            {
                PlayerCommandObservationLog.Append(
                    $"{PlayerCommandObservationRuntime.CreateStamp()} dialog-flow seq=0; "
                    + "event=suppress-empty; role=dialog; "
                    + $"restored={Describe(value)}");
            }
        }

        if (!string.IsNullOrEmpty(value))
        {
            _lastDialogVisible = CutAtMask(value);
        }

        if (_logLabelWrites)
        {
            PlayerCommandObservationLog.Append(
                $"{PlayerCommandObservationRuntime.CreateStamp()} dialog-flow seq=0; "
                + $"event=label-set; role=dialog; newValue={Describe(value)}");
        }

        if (!_armed || string.IsNullOrEmpty(value))
        {
            return;
        }

        value = _preservedPrefix + value;
        _lastDialogVisible = CutAtMask(value);
    }

    private static string CutAtMask(string value)
    {
        int cut = value.IndexOf(TransparencyMask, StringComparison.Ordinal);
        return cut >= 0 ? value[..cut] : value;
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

    private static void ResetPending()
    {
        _pendingContinue = false;
        Disarm();
    }

    private static void Disarm()
    {
        _armed = false;
        _preservedPrefix = string.Empty;
    }

    public static void HideUIPrefix()
    {
        PlayerCommandObservationLog.Append(
            $"{PlayerCommandObservationRuntime.CreateStamp()} "
            + $"dialog-continue event=hideui; window={PlayerCommandObservationRuntime.LastObservedWindowSequence}; "
            + $"panel={PanelState()}");
    }

    private static string PanelState()
    {
        if (ReferenceEquals(_dialogPanel, null))
        {
            return "uncached";
        }

        try
        {
            bool self = _dialogPanel.activeSelf;
            bool inHierarchy = _dialogPanel.activeInHierarchy;
            return $"self={self}; inHierarchy={inHierarchy}";
        }
        catch (Exception ex)
        {
            return $"read-failed:{ex.GetType().Name}";
        }
    }

    private static T Safe<T>(Func<T> access)
    {
        try
        {
            return access();
        }
        catch (Exception)
        {
            return default!;
        }
    }

    private static bool IsValidIdentity(string? identity)
    {
        if (identity == null || identity.Length != 64)
        {
            return false;
        }

        foreach (char character in identity)
        {
            bool hexadecimal = character is >= '0' and <= '9'
                or >= 'A' and <= 'F'
                or >= 'a' and <= 'f';
            if (!hexadecimal)
            {
                return false;
            }
        }

        return true;
    }

    private static void PersistIdentitySnapshotLocked()
    {
        if (string.IsNullOrEmpty(_identityFilePath))
        {
            return;
        }

        try
        {
            string? directory = Path.GetDirectoryName(_identityFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temporaryPath = _identityFilePath + ".tmp";
            string[] snapshot = ContinueIdentities
                .OrderBy(identity => identity, StringComparer.Ordinal)
                .ToArray();
            File.WriteAllLines(temporaryPath, snapshot);
            if (File.Exists(_identityFilePath))
            {
                File.Replace(temporaryPath, _identityFilePath, null);
            }
            else
            {
                File.Move(temporaryPath, _identityFilePath);
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning(
                $"Dialogue continue identity persistence failed: {ex.GetType().Name}: {ex.Message}");
            try
            {
                string temporaryPath = _identityFilePath + ".tmp";
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
