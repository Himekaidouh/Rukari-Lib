using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;

namespace Rukari.CharacterVoice.Runtime;

/// <summary>
/// Observes the existing compile and safe-save boundaries without requesting another compile.
/// <para>
/// The retired automatic publish workaround used to call <c>StudioCommon.Compile(string)</c> after every editor
/// build, including automatic builds. That added a complete synchronous publication to ordinary editing.
/// Manual publication is already requested explicitly by the shared editor save service. This observer only
/// supplies project scopes and notices actual safe-save promotions for companion voice publication.
/// </para>
/// </summary>
internal static class PublishProbe
{
    private const int MaximumReports = 40;
    private static readonly HashSet<string> Reported = new(StringComparer.Ordinal);

    internal static void Install(ConfigFile config)
    {
        ArgumentNullException.ThrowIfNull(config);
        bool enabled = config.Bind("Diagnostics", "ProbePublish", false,
            "Log the editor's publish entries — StudioCommon.Save(bool), CoAutoSaveAndCompile, the operation "
            + "manager's compile hook and Utils.Util.PromoteSafeSave(temp, dest) — with the exception when one "
            + "fails, so a 'save as AAS' button that produces nothing says where it stopped. Diagnostic only.").Value;
        // Keep the old key readable for existing profiles, but it no longer authorizes any action.
        // Even an existing true value must not append a full publish to automatic editor builds.
        var retiredPublish = config.Bind("Compile", "PublishPlayableAfterBuild", false,
            "Retired: automatic builds no longer trigger an additional playable-file publication. "
            + "Use the shared Save and Publish button to publish explicitly. This key is ignored.");
        retiredPublish.Value = false;

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

        // The already validated Compile() boundary retains only its scope prefix/finalizer.
        // There is deliberately no postfix and no call to any compile or save API here.
        patched += Hook(harmony, "Studio.Scripts.StudioCommon", "Compile", Type.EmptyTypes,
            "StudioCommon.Compile()", compileScope: true);

        Plugin.Logger.LogInfo($"Publish observer installed: {patched} hook(s); automaticAdditionalPublication=false; manual publication remains explicit.");
    }

    private static int Hook(Harmony harmony, string typeName, string method, Type[] arguments, string label,
        bool failures = false, bool compileScope = false)
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
                prefix: new HarmonyMethod(typeof(PublishProbe), compileScope ? nameof(CompilePrefix) : nameof(Prefix)),
                finalizer: compileScope ? new HarmonyMethod(typeof(PublishProbe), nameof(CompileFinalizer))
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
