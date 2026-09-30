using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Rukari.Lib.Commands;
using Studio.Scripts.Window;

namespace Rukari.Lib.Runtime.Commands;

/// <summary>
/// Publishes registered rows into the editor's own 「指令格式」 panel.
///
/// <para>
/// Two hooks were tried and the log settled it. The panel never calls its own
/// <c>GetAuthoringReference()</c> JSON, and its entry list <c>CommandEntries</c> is a native field whose
/// interop property is a <em>field accessor</em> — Il2CppInterop refuses to patch it ("is a field accessor,
/// it can't be patched"), so a getter postfix only ever saw our own managed reads while the native panel kept
/// reading the field directly. This version therefore appends the registered rows to that static field
/// <em>before</em> the panel builds its rows: official rows stay exactly where they are, ours are appended once
/// and guarded by syntax, and the row layout, colours and click-to-insert behaviour remain the editor's own.
/// </para>
/// </summary>
internal static class CommandHelpPatch
{
    private const string HarmonyId = "rukari.lib.runtime.command-help";
    private static readonly HashSet<string> Reported = new(StringComparer.Ordinal);
    private static Harmony? _harmony;
    private static Func<IReadOnlyList<CommandHelpEntry>>? _snapshot;
    private static Action<string>? _log;

    internal static bool Install(Func<IReadOnlyList<CommandHelpEntry>> snapshot, Action<string> log)
    {
        _snapshot = snapshot;
        _log = log;
        try
        {
            MethodInfo? window = AccessTools.Method(typeof(AdditionalPromptCommandHelp), "BuildWindow");
            MethodInfo? scrollView = AccessTools.Method(typeof(AdditionalPromptCommandHelp), "BuildScrollView");
            if (window is null && scrollView is null)
            {
                _snapshot = null;
                log("Command help: neither BuildWindow nor BuildScrollView was found on this build.");
                return false;
            }
            var publish = new HarmonyMethod(typeof(CommandHelpPatch), nameof(PublishPrefix));
            var harmony = new Harmony(HarmonyId);
            // Both builders are patched because either one may be the first to read the entry field; publishing
            // twice is harmless, the syntax guard makes it idempotent.
            if (window is not null) harmony.Patch(window, prefix: publish);
            if (scrollView is not null)
            {
                harmony.Patch(scrollView, prefix: publish,
                    postfix: new HarmonyMethod(typeof(CommandHelpPatch), nameof(BuildScrollViewPostfix)));
            }
            _harmony = harmony;
            return true;
        }
        catch (Exception ex)
        {
            _snapshot = null;
            log("Command help patch failed: " + ex.GetType().Name + ": " + ex.Message);
            return false;
        }
    }

    internal static void Uninstall()
    {
        try { _harmony?.UnpatchSelf(); }
        catch (Exception ex) { ReportOnce("unpatch-error", "Command help unpatch failed: " + ex.Message); }
        _harmony = null;
        _snapshot = null;
    }

    /// <summary>
    /// Appends the registered rows to the panel's own entry field. The official array is never edited: a new
    /// array is written back that starts with every official row, so the panel renders both in its own style.
    /// </summary>
    private static void PublishPrefix()
    {
        try
        {
            Func<IReadOnlyList<CommandHelpEntry>>? snapshot = _snapshot;
            if (snapshot is null) return;
            IReadOnlyList<CommandHelpEntry> rows = snapshot();
            if (rows.Count == 0) return;
            var current = AdditionalPromptCommandHelp.CommandEntries;
            var merged = new List<AdditionalPromptCommandHelp.CommandEntry>();
            var existing = new List<string>();
            if (current is not null)
                foreach (var entry in current)
                {
                    if (entry is null) continue;
                    merged.Add(entry);
                    existing.Add(entry.syntax ?? string.Empty);
                }
            IReadOnlyList<CommandHelpEntry> missing = CommandHelpPolicy.SelectMissing(rows, existing);
            if (missing.Count == 0) return;
            foreach (CommandHelpEntry row in missing)
                merged.Add(new AdditionalPromptCommandHelp.CommandEntry(row.Section, row.Syntax, row.Description));
            AdditionalPromptCommandHelp.CommandEntries =
                new Il2CppReferenceArray<AdditionalPromptCommandHelp.CommandEntry>(merged.ToArray());
            ReportOnce("published", $"Command help: published {missing.Count} mod row(s) into the panel's entry field "
                + $"({existing.Count} official + {missing.Count} mod = {merged.Count}).");
        }
        catch (Exception ex)
        {
            ReportOnce("publish-error", "Command help publish failed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>
    /// Reports how many rows the panel built after publishing. A count above the official-only baseline is
    /// evidence the rows reached the window rather than just the field.
    /// </summary>
    private static void BuildScrollViewPostfix(AdditionalPromptCommandHelp __instance)
    {
        try
        {
            Func<IReadOnlyList<CommandHelpEntry>>? snapshot = _snapshot;
            int modRows = snapshot is null ? 0 : snapshot().Count;
            int listed = AdditionalPromptCommandHelp.CommandEntries?.Count ?? 0;
            int built = __instance.commandRows?.Count ?? 0;
            ReportOnce("panel", $"Command help: panel list={listed}; rows built={built}; mod rows={modRows}.");
        }
        catch (Exception ex)
        {
            ReportOnce("panel-error", "Command help panel check failed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static void ReportOnce(string key, string message)
    {
        Action<string>? log = _log;
        if (log is null) return;
        lock (Reported)
        {
            if (!Reported.Add(key)) return;
        }
        log(message);
    }
}
