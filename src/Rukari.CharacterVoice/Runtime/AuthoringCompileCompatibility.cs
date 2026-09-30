using System.Reflection;

namespace Rukari.CharacterVoice.Runtime;

/// <summary>Resolve the two inspected AA session signatures without tying the plugin to either ABI.</summary>
internal static class AuthoringCompileCompatibility
{
    internal static MethodInfo Resolve(Type sessionType)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
        MethodInfo? current = sessionType.GetMethod("Compile", flags, null, new[] { typeof(bool) }, null);
        if (current is not null && current.GetParameters()[0].Name == "publishToSaves") return current;
        return sessionType.GetMethod("Compile", flags, null, Type.EmptyTypes, null)
            ?? throw new MissingMethodException(sessionType.FullName, "Compile() / Compile(bool publishToSaves)");
    }

    internal static object? InvokeBuildOnly(object session)
    {
        MethodInfo method = Resolve(session.GetType());
        // AAfix3 defaults this argument to false. Diagnostics must never request publication.
        return method.Invoke(session, method.GetParameters().Length == 0 ? null : new object[] { false });
    }
}
