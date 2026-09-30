using System;
using System.Collections.Generic;
using System.Reflection;

namespace AzureArchive.VideoTools.Interop;

internal static class InteropMemberAccess
{
    private static readonly object LockObj = new();
    private static readonly Dictionary<(Type Type, string Name), PropertyInfo?> PropertyCache = new();

    public static T? Get<T>(object target, string propertyName)
    {
        if (!InteropObjectGuard.IsAlive(target))
        {
            return default;
        }

        PropertyInfo? property = ResolveProperty(target.GetType(), propertyName);
        if (property == null)
        {
            throw new MissingMemberException(target.GetType().FullName, propertyName);
        }

        object? value = property.GetValue(target);
        return value is T typed ? typed : default;
    }

    public static void Set(object target, string propertyName, object? value)
    {
        if (!InteropObjectGuard.IsAlive(target))
        {
            throw new InvalidOperationException($"Cannot set {propertyName} on a dead interop wrapper.");
        }

        PropertyInfo? property = ResolveProperty(target.GetType(), propertyName);
        if (property == null)
        {
            throw new MissingMemberException(target.GetType().FullName, propertyName);
        }

        property.SetValue(target, value);
    }

    public static T? GetStatic<T>(Type type, string propertyName)
    {
        PropertyInfo? property = ResolveProperty(type, propertyName);
        if (property == null)
        {
            throw new MissingMemberException(type.FullName, propertyName);
        }

        object? value = property.GetValue(null);
        return value is T typed ? typed : default;
    }

    private static PropertyInfo? ResolveProperty(Type type, string name)
    {
        lock (LockObj)
        {
            if (PropertyCache.TryGetValue((type, name), out PropertyInfo? cached))
            {
                return cached;
            }

            PropertyInfo? property = type.GetProperty(
                name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            PropertyCache[(type, name)] = property;
            return property;
        }
    }
}
