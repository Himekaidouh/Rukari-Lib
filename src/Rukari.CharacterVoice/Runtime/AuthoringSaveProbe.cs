using System.Reflection;
using Newtonsoft.Json.Linq;
using AzureArchive.Automation;
using BepInEx.Configuration;
using HarmonyLib;

namespace Rukari.CharacterVoice.Runtime;

/// <summary>
/// The editor's save and compile entries, with their own receipts.
///
/// <para>
/// Compiling refuses to start with <c>Save the current revision before compiling.</c> until the current revision is
/// on disk, so a save that silently does nothing leaves the project permanently uncompilable and no playable file is
/// ever produced. Both entries return a structured receipt (<c>ok</c>, <c>warnings</c>, the written paths), and the
/// session exposes what it believes about the document — this records that state on the way in and the receipt on
/// the way out, so the editor explains the failure itself.
/// </para>
///
/// <para>Read-only with respect to files: it calls nothing, it only observes what the editor calls.</para>
/// </summary>
internal static class AuthoringSaveProbe
{
    private const int MaximumReports = 30;
    private static readonly HashSet<string> Reported = new(StringComparer.Ordinal);
    private static bool _installed;

    internal static void Install(ConfigFile config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (_installed) return;
        _installed = true;
        if (!config.Bind("Diagnostics", "ProbeAuthoringSave", false,
                "Log the editor's own save and compile calls with the session state on the way in (dirty, revision, "
                + "file, saving) and its receipt on the way out — 'Save the current revision before compiling.' is what "
                + "a save that did nothing looks like from the outside. Diagnostic only.").Value)
        {
            return;
        }

        Type session = typeof(AuthoringEditorSession);
        var harmony = new Harmony(Plugin.Guid + ".authoring-save-probe");
        int patched = 0;
        patched += Hook(harmony, session, "Save", Type.EmptyTypes, nameof(SavePrefix), nameof(SavePostfix));
        Type[] compileArguments = AuthoringCompileCompatibility.Resolve(session).GetParameters()
            .Select(parameter => parameter.ParameterType).ToArray();
        patched += Hook(harmony, session, "Compile", compileArguments, nameof(CompilePrefix), nameof(CompilePostfix));
        patched += Hook(harmony, session, "Check", new[] { typeof(string), typeof(string), typeof(bool), typeof(bool) },
            nameof(CheckPrefix), null);
        Plugin.Logger.LogInfo($"Authoring save probe installed: {patched} hook(s) on {session.FullName}.");
    }

    private static int Hook(Harmony harmony, Type type, string name, Type[] arguments, string? prefix, string? postfix)
    {
        try
        {
            MethodInfo? method = AccessTools.Method(type, name, arguments);
            if (method is null)
            {
                Plugin.Logger.LogWarning($"Authoring save probe: {name} not found.");
                return 0;
            }

            harmony.Patch(method,
                prefix: prefix is null ? null : new HarmonyMethod(typeof(AuthoringSaveProbe), prefix),
                postfix: postfix is null ? null : new HarmonyMethod(typeof(AuthoringSaveProbe), postfix));
            return 1;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"Authoring save probe: {name} could not be hooked: {ex.GetType().Name}: {ex.Message}");
            return 0;
        }
    }

    public static void SavePrefix() => Report("save:in", "[voice] session Save() called; " + State());

    public static void SavePostfix(ref JObject __result) =>
        Report("save:out", "[voice] session Save() returned: " + Describe(__result) + "; after: " + State());

    public static void CompilePrefix() => Report("compile:in", "[voice] session Compile() called; " + State());

    public static void CompilePostfix(ref JObject __result) =>
        Report("compile:out", "[voice] session Compile() returned: " + Describe(__result));

    /// <summary>The guard that answers "Save the current revision before compiling."</summary>
    public static void CheckPrefix(string session, string expectedRevision, bool inputGuard, bool checkResources) =>
        Report($"check:{expectedRevision}", $"[voice] session Check(session='{session}', expectedRevision='{expectedRevision}', "
            + $"inputGuard={inputGuard}, checkResources={checkResources}); {State()}");

    private static string State()
    {
        try
        {
            AuthoringEditorSession? current = AuthoringEditorSession.Current;
            if (ReferenceEquals(current, null)) return "no session";
            return $"dirty={current.Dirty}; revision={current.Revision}; saving={current.SaveInProgress}; "
                + $"applying={current.IsApplying}; file={current.FilePath}; status={current.LastStatus}";
        }
        catch (Exception ex)
        {
            return "<" + ex.GetType().Name + ": " + ex.Message + ">";
        }
    }

    private static string Describe(JObject? value)
    {
        try
        {
            string text = value?.ToString() ?? "<null>";
            return text.Length <= 1500 ? text : text[..1500] + $"…(+{text.Length - 1500})";
        }
        catch (Exception ex)
        {
            return "<" + ex.GetType().Name + ">";
        }
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
