using AzureArchive.Automation;
using BepInEx.Configuration;
using HarmonyLib;
using Newtonsoft.Json.Linq;

namespace Rukari.CharacterVoice.Runtime;

/// <summary>
/// Compiling from the editor button stalls before it starts.
///
/// <para>
/// The button runs <c>AuthoringWorkbench.Persist</c>, which asks the session to save first and then compiles. That
/// save is prepared on a background task (<c>Preparing saved project in background…</c>) and, on this project, never
/// finishes: the revision stays unsaved, so the compile refuses with
/// <c>Save the current revision before compiling.</c> and every retry does the same. The session's own entry points
/// work — the same session compiles cleanly with <c>ok: true</c> once the revision is on disk.
/// </para>
///
/// <para>
/// So when a compile is asked for while the document is still dirty, this saves the revision through the session
/// first (the same call the editor's own <c>save_project</c> automation entry uses) and lets the compile proceed.
/// If that save fails, nothing changes: the original compile still raises exactly what it raised before.
/// </para>
/// </summary>
internal static class CompileRevisionGuard
{
    private static bool _enabled;

    internal static void Install(ConfigFile config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _enabled = config.Bind("Compile", "SaveRevisionBeforeCompile", true,
            "The editor's compile button saves the current revision through a background path that can stall, leaving "
            + "the project permanently dirty and the compile permanently refused ('Save the current revision before "
            + "compiling.'). When a compile is asked for while the document is dirty, save that revision through the "
            + "session first — the same call the editor's own save_project automation entry uses. Restart required.").Value;
        try
        {
            var harmony = new Harmony(Plugin.Guid + ".compile-revision");
            harmony.Patch(AuthoringCompileCompatibility.Resolve(typeof(AuthoringEditorSession)),
                prefix: new HarmonyMethod(typeof(CompileRevisionGuard), nameof(Prefix)),
                finalizer: new HarmonyMethod(typeof(CompileRevisionGuard), nameof(Finalizer)));
            Plugin.Logger.LogInfo($"Compile scope installed; saveRevisionBeforeCompile={_enabled}; companion audio observes actual AAS publication.");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"Compile revision guard not installed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Void prefix: the original compile always runs, saved or not.</summary>
    public static void Prefix(AuthoringEditorSession __instance, out VoicePublicationRuntime.Scope? __state)
    {
        __state = VoicePublicationRuntime.EnterCompile(__instance);
        if (!_enabled) return;
        try
        {
            AuthoringEditorSession? session = __instance;
            if (ReferenceEquals(session, null) || !session.Dirty) return;
            Plugin.Logger.LogInfo($"[voice] compile asked for while dirty (revision={session.Revision}); saving the "
                + "revision through the session first.");
            // Timed for the same reason as the publish call: this runs on the game's thread whenever the
            // editor compiles a dirty document, which includes opening a project.
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            JObject? receipt = session.Save();
            Plugin.Logger.LogInfo($"[voice] session Save() took {Elapsed(started)} ms "
                + $"at {DateTimeOffset.Now:HH:mm:ss.fff} and returned: {Text(receipt)}; "
                + $"after: dirty={session.Dirty}; revision={session.Revision}; status={session.LastStatus}");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"[voice] saving the revision before compiling failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static Exception? Finalizer(VoicePublicationRuntime.Scope? __state, Exception? __exception)
    {
        __state?.Dispose();
        return __exception;
    }

    /// <summary>Milliseconds since a <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> reading.</summary>
    private static long Elapsed(long startedTimestamp) =>
        (System.Diagnostics.Stopwatch.GetTimestamp() - startedTimestamp) * 1000L
        / System.Diagnostics.Stopwatch.Frequency;

    private static string Text(JObject? value)
    {
        try
        {
            string text = value?.ToString() ?? "<null>";
            return text.Length <= 1200 ? text : text[..1200] + $"…(+{text.Length - 1200})";
        }
        catch (Exception ex)
        {
            return "<" + ex.GetType().Name + ">";
        }
    }
}
