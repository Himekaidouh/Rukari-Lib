using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;

namespace Rukari.CharacterVoice.Runtime;

/// <summary>
/// The editor's publish entries, watched end to end, plus the publish call the interface never makes.
///
/// <para>
/// The editor's save button runs <c>StudioCommon.Save(false)</c> and then <c>StudioCommon.Compile()</c> — the
/// no-argument overload, which only writes a build folder under <c>.builds</c>. Nothing in the interface ever reaches
/// <c>StudioCommon.Compile(string fileName)</c>, and with it never called no playable file is ever published, which is
/// exactly what a "save as AAS" button that produces nothing looks like from the outside.
/// </para>
///
/// <para>
/// So after the editor's own build finishes, this calls the named overload with the project name — the same call the
/// missing entry would make — and records what it returned or threw. The rest of the probe logs the publish entries
/// (<c>StudioCommon.Save(bool)</c>, its auto save/compile coroutine, the operation manager's compile hook and
/// <c>Utils.Util.PromoteSafeSave(temp, dest)</c>) with the exception when one fails, because a publish that silently
/// does nothing and a publish that fails look identical from the outside.
/// </para>
/// </summary>
internal static class PublishProbe
{
    private const int MaximumReports = 40;
    private static readonly HashSet<string> Reported = new(StringComparer.Ordinal);
    private static bool _publish;
    private static bool _publishing;

    internal static void Install(ConfigFile config)
    {
        ArgumentNullException.ThrowIfNull(config);
        bool enabled = config.Bind("Diagnostics", "ProbePublish", false,
            "Log the editor's publish entries — StudioCommon.Save(bool), CoAutoSaveAndCompile, the operation "
            + "manager's compile hook and Utils.Util.PromoteSafeSave(temp, dest) — with the exception when one "
            + "fails, so a 'save as AAS' button that produces nothing says where it stopped. Diagnostic only.").Value;
        _publish = config.Bind("Compile", "PublishPlayableAfterBuild", true,
            "The editor's save button only builds into .builds; the overload that produces a playable file, "
            + "StudioCommon.Compile(fileName), is never called by any interface action. After a build finishes, call it "
            + "with the project name so the playable file the editor is supposed to publish actually appears. "
            + "Restart required.").Value;

        var harmony = new Harmony(Plugin.Guid + ".publish-probe");
        int patched = 0;
        if (enabled)
        {
            patched += Hook(harmony, "Studio.Scripts.StudioCommon", "Save", new[] { typeof(bool) }, "StudioCommon.Save(bool)");
            patched += Hook(harmony, "Studio.Scripts.StudioCommon", "CoAutoSaveAndCompile", Type.EmptyTypes, "CoAutoSaveAndCompile");
            patched += Hook(harmony, "Studio.Scripts.OperationManagement.OperationManager", "OnCompile", Type.EmptyTypes, "OperationManager.OnCompile");
        }

        // Reuse the previously observed string-only safe-save boundary once, with this Harmony owner.
        // Sidecar publication is independent of the legacy extra-compile workaround.
        try
        {
            var safeSave = AccessTools.Method(typeof(Utils.Util), "PromoteSafeSave", new[] { typeof(string), typeof(string) })
                ?? throw new MissingMethodException("Utils.Util.PromoteSafeSave(string,string)");
            harmony.Patch(safeSave,
                prefix: new HarmonyMethod(typeof(PublishProbe), nameof(PromotePrefix)),
                postfix: new HarmonyMethod(typeof(PublishProbe), nameof(PromotePostfix)));
            patched++;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning("[voice] companion audio publisher unavailable: " + ex.Message);
        }

        if (_publish)
        {
            patched += Hook(harmony, "Studio.Scripts.StudioCommon", "Compile", Type.EmptyTypes, "StudioCommon.Compile()", publishAfter: true);
        }

        Plugin.Logger.LogInfo($"Publish probe installed: {patched} hook(s); publishAfterBuild={_publish}.");
    }

    private static int Hook(Harmony harmony, string typeName, string method, Type[] arguments, string label,
        bool failures = false, bool publishAfter = false)
    {
        try
        {
            Type? type = AccessTools.TypeByName(typeName);
            if (type is null)
            {
                Plugin.Logger.LogWarning($"Publish probe: type {typeName} not found.");
                return 0;
            }

            MethodInfo? target = AccessTools.Method(type, method, arguments);
            if (target is null)
            {
                Plugin.Logger.LogWarning($"Publish probe: {typeName}.{method} not found.");
                return 0;
            }

            harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(PublishProbe), publishAfter ? nameof(CompilePrefix) : nameof(Prefix)),
                postfix: publishAfter ? new HarmonyMethod(typeof(PublishProbe), nameof(PublishPostfix)) : null,
                finalizer: publishAfter ? new HarmonyMethod(typeof(PublishProbe), nameof(CompileFinalizer))
                    : failures ? new HarmonyMethod(typeof(PublishProbe), nameof(Finalizer)) : null);
            Report("hooked:" + label, $"Publish probe hooked {label}.");
            return 1;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"Publish probe: {label} could not be hooked: {ex.GetType().Name}: {ex.Message}");
            return 0;
        }
    }

    public static void Prefix(MethodBase __originalMethod, object[] __args) =>
        Report("call:" + __originalMethod.DeclaringType?.Name + "." + __originalMethod.Name,
            $"[voice] publish {__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}({Describe(__args)})");

    public static void CompilePrefix(Studio.Scripts.StudioCommon __instance, out VoicePublicationRuntime.Scope? __state) =>
        __state = VoicePublicationRuntime.EnterCompile(__instance);

    public static Exception? CompileFinalizer(VoicePublicationRuntime.Scope? __state, Exception? __exception)
    {
        __state?.Dispose();
        return __exception;
    }

    public static void PromotePrefix(string __0, string __1, out VoicePublicationRuntime.Promotion? __state) =>
        __state = VoicePublicationRuntime.BeforePromotion(__0, __1);

    public static void PromotePostfix(VoicePublicationRuntime.Promotion? __state) =>
        VoicePublicationRuntime.AfterPromotion(__state);

    /// <summary>
    /// After the editor's own build finished, run the publish overload the interface never reaches.
    /// </summary>
    public static void PublishPostfix(object __instance)
    {
        if (!_publish || _publishing) return;
        try
        {
            if (__instance is not Studio.Scripts.StudioCommon common) return;
            string name = common.projectName ?? string.Empty;
            if (name.Length == 0)
            {
                Plugin.Logger.LogWarning("[voice] publish skipped: the editor has no project name.");
                return;
            }

            _publishing = true;
            // 2026-09-19: timed because this runs synchronously on the game's thread right after every
            // editor build — including the build the editor performs when a project is opened — and a
            // full named compile of a large project is a prime suspect for the game going unresponsive.
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            Plugin.Logger.LogInfo($"[voice] publish: calling StudioCommon.Compile('{name}') — the playable-file overload "
                + "no interface action reaches.");
            common.Compile(name);
            Plugin.Logger.LogInfo($"[voice] publish: StudioCommon.Compile('{name}') returned in "
                + $"{Elapsed(started)} ms at {DateTimeOffset.Now:HH:mm:ss.fff}.");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"[voice] publish failed: {ex.GetType().Name}: {ex.Message}");
            foreach (string line in (ex.StackTrace ?? string.Empty).Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length != 0) Plugin.Logger.LogWarning("[voice]   at " + trimmed);
            }
        }
        finally
        {
            _publishing = false;
        }
    }

    public static Exception? Finalizer(MethodBase __originalMethod, object[] __args, Exception? __exception)
    {
        if (__exception is null) return null;
        Plugin.Logger.LogWarning($"[voice] publish {__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}"
            + $"({Describe(__args)}) threw {__exception.GetType().Name}: {__exception.Message}");
        foreach (string line in (__exception.StackTrace ?? string.Empty).Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length != 0) Plugin.Logger.LogWarning("[voice]   at " + trimmed);
        }

        return __exception;
    }

    /// <summary>Milliseconds since a <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> reading.</summary>
    private static long Elapsed(long startedTimestamp) =>
        (System.Diagnostics.Stopwatch.GetTimestamp() - startedTimestamp) * 1000L
        / System.Diagnostics.Stopwatch.Frequency;

    private static string Describe(object[]? args)
    {
        if (args is null || args.Length == 0) return string.Empty;
        var parts = new List<string>(args.Length);
        foreach (object? argument in args)
        {
            parts.Add(argument switch
            {
                null => "null",
                string text => "'" + (text.Length <= 160 ? text : text[..160] + "…") + "'",
                _ => argument.ToString() ?? argument.GetType().Name
            });
        }

        return string.Join(", ", parts);
    }

    private static void Report(string key, string message)
    {
        lock (Reported)
        {
            if (Reported.Count >= MaximumReports || !Reported.Add(key)) return;
        }

        Plugin.Logger.LogInfo(message);
    }
}
