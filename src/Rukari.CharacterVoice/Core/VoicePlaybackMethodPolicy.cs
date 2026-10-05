using System.Reflection;

namespace Rukari.CharacterVoice.Core;

// Resolve exact declarations and bind an open, strongly typed legacy replay delegate.
// No receiver or callback wrapper is retained. Callback-capable starts stay on the official path.
internal static class VoicePlaybackMethodPolicy
{
    // In callback-capable mode, degraded playback observation must not turn into a
    // preloader veto that swallows an otherwise passed-through official start.
    internal static bool ShouldInterceptPreload(bool usesStartedCallback, bool pollingSuspended,
        bool admissionSuspended) => !admissionSuspended && (!usesStartedCallback || !pollingSuspended);

    internal static bool AllowNativePreloadAfterFailure(bool usesStartedCallback,
        bool owned, ref bool result)
    {
        // This decision also covers the current call whose observation failed,
        // not just later calls that see the suspended admission flag.
        if (usesStartedCallback || !owned) return true;
        result = false;
        return false;
    }

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

    internal static Action<TReceiver, string>? BindLegacyReplay<TReceiver>(
        MethodInfo selectedMethod, bool usesStartedCallback) where TReceiver : class
    {
        ArgumentNullException.ThrowIfNull(selectedMethod);
        // The caller resolved the callback-capable declaration above. Never manufacture
        // a replay for it: its original caller owns the native onStarted argument.
        if (usesStartedCallback) return null;
        if (selectedMethod.DeclaringType != typeof(TReceiver)
            || selectedMethod.Name != "SetVoice" || selectedMethod.IsStatic
            || selectedMethod.ReturnType != typeof(void) || selectedMethod.ContainsGenericParameters
            || !selectedMethod.GetParameters().Select(parameter => parameter.ParameterType)
                .SequenceEqual(new[] { typeof(string) }))
            throw new ArgumentException("Legacy replay requires exact instance void SetVoice(string).", nameof(selectedMethod));
        // No optional argument substitution, MethodInfo.Invoke or live object boxing.
        // The open delegate receives a freshly acquired receiver at each real replay.
        return selectedMethod.CreateDelegate<Action<TReceiver, string>>();
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
