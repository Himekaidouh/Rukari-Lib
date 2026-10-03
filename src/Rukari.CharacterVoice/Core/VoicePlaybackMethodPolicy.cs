using System.Reflection;

namespace Rukari.CharacterVoice.Core;

// Signature metadata only. No invocation, body reading, optional argument rewriting or
// callback wrapper retention. The callback-capable official overload takes precedence.
internal static class VoicePlaybackMethodPolicy
{
    // In callback-capable mode, degraded playback observation must not turn into a
    // preloader veto that swallows an otherwise passed-through official start.
    internal static bool ShouldInterceptPreload(bool usesStartedCallback, bool pollingSuspended,
        bool admissionSuspended) => !admissionSuspended && (!usesStartedCallback || !pollingSuspended);

    internal static MethodInfo ResolveSetVoice(Type owner, Type startedCallbackType, out bool usesStartedCallback)
    {
        MethodInfo[] methods = owner.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
        MethodInfo? callback = Find(methods, owner, new[] { typeof(string), startedCallbackType });
        if (callback is not null) { usesStartedCallback = true; return callback; }
        MethodInfo? legacy = Find(methods, owner, new[] { typeof(string) });
        if (legacy is not null) { usesStartedCallback = false; return legacy; }
        throw new MissingMethodException(owner.FullName, "instance void SetVoice(string[, onStarted])");
    }

    private static MethodInfo? Find(IEnumerable<MethodInfo> methods, Type owner, Type[] arguments)
    {
        MethodInfo? match = null;
        foreach (MethodInfo method in methods)
        {
            if (method.Name != "SetVoice" || method.DeclaringType != owner || method.IsStatic
                || method.ReturnType != typeof(void) || method.ContainsGenericParameters) continue;
            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length != arguments.Length
                || !parameters.Select(p => p.ParameterType).SequenceEqual(arguments)) continue;
            if (match is not null) throw new AmbiguousMatchException("Multiple exact SetVoice declarations.");
            match = method;
        }
        return match;
    }
}
