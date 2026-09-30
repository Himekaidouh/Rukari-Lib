using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using Studio.Scripts.OperationManagement;

namespace Rukari.CharacterVoice.Runtime;

/// <summary>
/// Traces the editor's own voice-placeholder registration.
///
/// <para>
/// Adding a dialogue slot that plays a lobby <c>Talk_*</c> animation makes the editor register a voice placeholder for
/// that line — a fresh GUID and the relative path <c>voices/&lt;guid&gt;.ogg</c> — and the user then imports the audio
/// by hand. That path is also where the editor currently fails with
/// <c>Access to the path '…\voices\&lt;guid&gt;.ogg' is denied.</c>, shown as a notification and written nowhere, so
/// the only way to see which call fails is from inside it.
/// </para>
///
/// <para>
/// Read-only: every hook logs its arguments and result and rethrows whatever the editor threw. The four methods are
/// patched by name because their signatures are the editor's own; a missing one is reported and skipped.
/// </para>
/// </summary>
internal static class VoicePlaceholderProbe
{
    private const int MaximumReports = 40;
    private static readonly HashSet<string> Reported = new(StringComparer.Ordinal);
    private static bool _enabled;
    private static bool _installed;

    internal static void Install(ConfigFile config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (_installed) return;
        _enabled = config.Bind("Diagnostics", "ProbeVoicePlaceholder", false,
            "Log every voice-placeholder registration the editor performs (identifier, path, whether it is missing) "
            + "and the exception when one fails — the 'Access to the path … is denied' notification is written nowhere "
            + "else. Diagnostic only; turn it off when the question is answered.").Value;
        _installed = true;
        if (!_enabled)
        {
            Plugin.Logger.LogInfo("Voice placeholder probe disabled by configuration.");
            return;
        }

        var harmony = new Harmony(Plugin.Guid + ".voice-placeholder");
        int patched = 0;
        patched += TryPatch(harmony, "AddIfAbsent", prefix: nameof(AddIfAbsentPrefix), finalizer: nameof(AddIfAbsentFinalizer));
        patched += TryPatch(harmony, "RemoveMissing", prefix: nameof(RemoveMissingPrefix));
        patched += TryPatch(harmony, "Placeholder", postfix: nameof(PlaceholderPostfix));
        patched += TryPatch(harmony, "DefinitelyMissing", postfix: nameof(DefinitelyMissingPostfix));
        patched += TryCompileHooks(harmony);
        Plugin.Logger.LogInfo($"Voice placeholder probe installed: {patched} hook(s) on "
            + $"{typeof(VoicePlaceholderRegistration).FullName} and {typeof(Studio.Scripts.StudioCommon).FullName}.");
    }

    /// <summary>
    /// The editor has two compile entries: the no-argument one only writes a build folder, the one that takes a file
    /// name is what produces a playable file. Which one the UI button reaches decides where a missing .aas comes from.
    /// </summary>
    private static int TryCompileHooks(Harmony harmony)
    {
        int patched = 0;
        try
        {
            MethodInfo? automatic = AccessTools.Method(typeof(Studio.Scripts.StudioCommon), "Compile", Type.EmptyTypes);
            if (automatic is not null)
            {
                harmony.Patch(automatic, prefix: new HarmonyMethod(typeof(VoicePlaceholderProbe), nameof(CompileAutomaticPrefix)));
                patched++;
            }

            MethodInfo? named = AccessTools.Method(typeof(Studio.Scripts.StudioCommon), "Compile", new[] { typeof(string) });
            if (named is not null)
            {
                harmony.Patch(named, prefix: new HarmonyMethod(typeof(VoicePlaceholderProbe), nameof(CompileNamedPrefix)));
                patched++;
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"Voice placeholder probe: compile hooks failed: {ex.GetType().Name}: {ex.Message}");
        }

        return patched;
    }

    public static void CompileAutomaticPrefix() =>
        Report("compile:auto", "[voice] StudioCommon.Compile() reached (no file name: build folder only).");

    public static void CompileNamedPrefix(string fileName) =>
        Report("compile:named:" + fileName, $"[voice] StudioCommon.Compile('{fileName}') reached (compile to a playable file).");

    private static int TryPatch(Harmony harmony, string name, string? prefix = null, string? postfix = null,
        string? finalizer = null)
    {
        try
        {
            MethodInfo? method = AccessTools.Method(typeof(VoicePlaceholderRegistration), name);
            if (method is null)
            {
                Plugin.Logger.LogWarning($"Voice placeholder probe: {name} not found.");
                return 0;
            }

            harmony.Patch(method,
                prefix: prefix is null ? null : new HarmonyMethod(typeof(VoicePlaceholderProbe), prefix),
                postfix: postfix is null ? null : new HarmonyMethod(typeof(VoicePlaceholderProbe), postfix),
                finalizer: finalizer is null ? null : new HarmonyMethod(typeof(VoicePlaceholderProbe), finalizer));
            return 1;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"Voice placeholder probe: {name} could not be hooked: {ex.GetType().Name}: {ex.Message}");
            return 0;
        }
    }

    public static void AddIfAbsentPrefix(string identifier) =>
        Report("add:" + identifier, $"[voice] placeholder AddIfAbsent '{identifier}'");

    public static Exception? AddIfAbsentFinalizer(string identifier, Exception? __exception)
    {
        if (__exception is null) return null;
        Plugin.Logger.LogWarning($"[voice] placeholder AddIfAbsent '{identifier}' threw {__exception.GetType().Name}: {__exception.Message}");
        foreach (string line in (__exception.StackTrace ?? string.Empty).Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length != 0) Plugin.Logger.LogWarning("[voice]   at " + trimmed);
        }

        return __exception;
    }

    public static void RemoveMissingPrefix(string identifier) =>
        Report("remove:" + identifier, $"[voice] placeholder RemoveMissing '{identifier}'");

    public static void PlaceholderPostfix(string identifier, ref string __result) =>
        Report("path:" + identifier, $"[voice] placeholder path for '{identifier}' = {__result}");

    public static void DefinitelyMissingPostfix(string root, string relative, ref bool __result) =>
        Report("missing:" + relative, $"[voice] placeholder DefinitelyMissing root='{root}' relative='{relative}' -> {__result}");

    private static void Report(string key, string message)
    {
        lock (Reported)
        {
            if (Reported.Count >= MaximumReports || !Reported.Add(key)) return;
        }

        Plugin.Logger.LogInfo(message);
    }
}
