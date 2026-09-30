using System.Reflection;
using HarmonyLib;
using Rukari.Lib.Commands;

namespace Rukari.Lib.Runtime.Commands;

// Reuses the three managed-string boundaries used by the stable feature mods.
// No native object, value-type argument, or live field is passed to a provider.
internal static class EmbeddedDirectivePatch
{
    private const string HarmonyId = "rukari.lib.runtime.embedded-directives";
    private static Harmony? _harmony;
    private static EmbeddedDirectiveService? _service;
    private static Action<string>? _log;
    [ThreadStatic] private static DirectiveCompilationTracker? _tracker;

    internal static bool Install(EmbeddedDirectiveService service, Action<string> log)
    {
        if (_harmony is not null) return true;
        MethodInfo? getter = AccessTools.PropertyGetter(typeof(Script), nameof(Script.ScriptKr));
        MethodInfo? standalone = AccessTools.Method(typeof(Script), "CompileScriptStandalone");
        MethodInfo? continuous = AccessTools.Method(typeof(Script), nameof(Script.CompileScriptContinuous));
        if (getter?.ReturnType != typeof(string) || standalone?.ReturnType != typeof(string)
            || continuous?.ReturnType != typeof(string))
        {
            log("Shared directives: the three expected managed-string compiler boundaries were not found.");
            return false;
        }

        var harmony = new Harmony(HarmonyId);
        _service = service;
        _log = log;
        try
        {
            Patch(harmony, getter, nameof(ScriptPrefix), nameof(ScriptPostfix), nameof(ScriptFinalizer));
            Patch(harmony, standalone, nameof(StandalonePrefix), nameof(StandalonePostfix), nameof(StandaloneFinalizer));
            Patch(harmony, continuous, nameof(ContinuousPrefix), nameof(ContinuousPostfix), nameof(ContinuousFinalizer));
            _harmony = harmony;
            return true;
        }
        catch (Exception exception)
        {
            try { harmony.UnpatchSelf(); }
            catch (Exception cleanup) { Log("Shared directives partial-install cleanup: " + cleanup.Message); }
            _service = null;
            Log("Shared directive hooks unavailable: " + exception.GetType().Name + ": " + exception.Message);
            return false;
        }
    }

    internal static void Uninstall()
    {
        _service = null;
        Harmony? harmony = _harmony;
        _harmony = null;
        try { harmony?.UnpatchSelf(); }
        catch (Exception exception) { Log("Shared directive hook cleanup: " + exception.Message); }
        _tracker = null;
    }

    private static void Patch(Harmony harmony, MethodInfo target, string prefix, string postfix, string finalizer) =>
        harmony.Patch(target,
            prefix: new HarmonyMethod(typeof(EmbeddedDirectivePatch), prefix),
            postfix: new HarmonyMethod(typeof(EmbeddedDirectivePatch), postfix),
            finalizer: new HarmonyMethod(typeof(EmbeddedDirectivePatch), finalizer));

    private static void ScriptPrefix(out CompilationScope? __state) => Begin(DirectiveCompilationBoundary.ScriptText, out __state);
    private static void StandalonePrefix(out CompilationScope? __state) => Begin(DirectiveCompilationBoundary.Standalone, out __state);
    private static void ContinuousPrefix(out CompilationScope? __state) => Begin(DirectiveCompilationBoundary.Continuous, out __state);
    private static void ScriptFinalizer(CompilationScope? __state) => End(__state);
    private static void StandaloneFinalizer(CompilationScope? __state) => End(__state);
    private static void ContinuousFinalizer(CompilationScope? __state) => End(__state);
    private static void ScriptPostfix(ref string __result, CompilationScope? __state) => Process(ref __result, __state);
    private static void StandalonePostfix(ref string __result, CompilationScope? __state) => Process(ref __result, __state);
    private static void ContinuousPostfix(ref string __result, CompilationScope? __state) => Process(ref __result, __state);

    private static void Begin(DirectiveCompilationBoundary boundary, out CompilationScope? state)
    {
        state = null;
        var tracker = _tracker ??= new DirectiveCompilationTracker();
        tracker.Begin(boundary);
        state = new CompilationScope(tracker, boundary);
    }

    private static void End(CompilationScope? state)
    {
        // Other Harmony prefixes can fail or skip ours. A missing state must not unwind an outer frame.
        try { state?.End(); }
        catch (Exception exception) { Log("Shared directive scope cleanup: " + exception.Message); }
    }

    private sealed class CompilationScope
    {
        private readonly DirectiveCompilationTracker _tracker;
        private readonly DirectiveCompilationBoundary _boundary;
        private int _ended;
        internal CompilationScope(DirectiveCompilationTracker tracker, DirectiveCompilationBoundary boundary)
            => (_tracker, _boundary) = (tracker, boundary);
        internal long CompilationId => _tracker.CompilationId;
        internal DirectiveCompilationBoundary Boundary => _boundary;
        internal bool IsAuthoritative => _tracker.IsAuthoritative(_boundary);
        internal void End()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0) _tracker.End(_boundary);
        }
    }

    private static void Process(ref string result, CompilationScope? state)
    {
        EmbeddedDirectiveService? service = _service;
        if (service is null || state is null) return;
        try
        {
            result = service.Process(result ?? string.Empty, state.Boundary,
                state.CompilationId, state.IsAuthoritative);
        }
        catch (Exception exception)
        {
            Log("Shared directive boundary failed: " + exception.GetType().Name + ": " + exception.Message);
        }
    }

    private static void Log(string message)
    {
        try { _log?.Invoke(message); }
        catch { /* Logging must never disrupt the official compiler. */ }
    }
}
