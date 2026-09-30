using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;

namespace Rukari.CharacterVoice.Runtime;

/// <summary>
/// The editor's own file calls, narrowed to the project's <c>voices</c> folder.
///
/// <para>
/// Adding a dialogue slot that plays a lobby <c>Talk_*</c> animation makes the editor reach for
/// <c>&lt;project&gt;\voices\&lt;fresh guid&gt;.ogg</c> and fail with <c>Access to the path … is denied.</c> — shown as
/// a notification and written to no log at all. The placeholder-registration hooks never saw that guid, so the call
/// comes from somewhere else entirely; this watches the game's own <c>System.IO.File</c> surface instead, so whichever
/// component does it is caught with its path, its arguments and its exception.
/// </para>
///
/// <para>
/// Only calls whose path contains the project voices folder are reported, and nothing is swallowed: the finalizer
/// logs and rethrows. <c>Exists</c> and <c>GetAttributes</c> are deliberately not hooked — they are far too hot and
/// cannot raise this error.
/// </para>
/// </summary>
internal static class GameVoiceFileProbe
{
    private const int MaximumReports = 40;
    private static readonly string[] HookedNames =
    {
        "Create", "CreateText", "Delete", "Copy", "Move", "Replace", "Open", "OpenRead", "OpenWrite", "OpenText",
        "AppendAllText", "AppendText", "ReadAllBytes", "WriteAllBytes", "ReadAllText", "WriteAllText", "ReadAllLines",
        "WriteAllLines"
    };

    private static readonly HashSet<string> Reported = new(StringComparer.Ordinal);
    private static bool _installed;

    internal static void Install(ConfigFile config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (_installed) return;
        _installed = true;
        if (!config.Bind("Diagnostics", "ProbeVoiceFileAccess", false,
                "Log every game-side file call whose path is inside the open project's voices folder, together with the "
                + "exception when one fails — the editor's 'Access to the path … is denied' notification is written "
                + "nowhere else. Diagnostic only.").Value)
        {
            return;
        }

        Type file = typeof(Il2CppSystem.IO.File);
        var harmony = new Harmony(Plugin.Guid + ".voice-file-probe");
        int patched = 0;
        foreach (MethodInfo method in AccessTools.GetDeclaredMethods(file))
        {
            if (Array.IndexOf(HookedNames, method.Name) < 0) continue;
            try
            {
                harmony.Patch(method,
                    prefix: new HarmonyMethod(typeof(GameVoiceFileProbe), nameof(Prefix)),
                    finalizer: new HarmonyMethod(typeof(GameVoiceFileProbe), nameof(Finalizer)));
                patched++;
            }
            catch (Exception)
            {
                // One overload that refuses to patch must not stop the rest; the report says how many landed.
            }
        }

        Plugin.Logger.LogInfo($"Voice file probe installed: {patched} hook(s) on {file.FullName}.");
    }

    public static void Prefix(MethodBase __originalMethod, object[] __args)
    {
        string? path = FirstPath(__args);
        if (path is null) return;
        Report("call:" + __originalMethod.Name + ":" + path,
            $"[voice] file {__originalMethod.Name} '{path}' args={Describe(__args)}");
    }

    public static Exception? Finalizer(MethodBase __originalMethod, object[] __args, Exception? __exception)
    {
        if (__exception is null) return null;
        string? path = FirstPath(__args);
        if (path is null) return __exception;
        Plugin.Logger.LogWarning($"[voice] file {__originalMethod.Name} '{path}' threw "
            + $"{__exception.GetType().Name}: {__exception.Message}");
        foreach (string line in (__exception.StackTrace ?? string.Empty).Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length != 0) Plugin.Logger.LogWarning("[voice]   at " + trimmed);
        }

        return __exception;
    }

    private static string? FirstPath(object[]? args)
    {
        if (args is null) return null;
        foreach (object? argument in args)
        {
            if (argument is not string text || text.Length == 0) continue;
            if (text.Contains("\\voices\\", StringComparison.OrdinalIgnoreCase)
                || text.Contains("/voices/", StringComparison.OrdinalIgnoreCase))
            {
                return text;
            }

            return null;
        }

        return null;
    }

    private static string Describe(object[]? args)
    {
        if (args is null) return "none";
        var parts = new List<string>(args.Length);
        foreach (object? argument in args)
        {
            parts.Add(argument switch
            {
                null => "null",
                string text => "'" + (text.Length <= 120 ? text : text[..120] + "…") + "'",
                _ => argument.GetType().Name
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
