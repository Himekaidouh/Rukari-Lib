using System;
using System.Collections.Generic;

namespace AzureArchive.VideoTools.Core;

internal static class PatchGuard
{
    private static readonly object LockObj = new();
    private static readonly HashSet<string> LoggedFailures = new(StringComparer.Ordinal);

    public static void Run(string capabilityId, string operation, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            string detail = $"{operation}: {Describe(ex)}";
            Plugin.Host.CapabilitiesInternal.Degraded(capabilityId, detail);

            lock (LockObj)
            {
                if (LoggedFailures.Add(capabilityId + "|" + operation))
                {
                    Plugin.Logger.LogError($"Compatibility API isolated {detail}");
                }
            }
        }
    }

    public static string Describe(Exception ex)
    {
        Exception actual = ex is System.Reflection.TargetInvocationException && ex.InnerException != null
            ? ex.InnerException
            : ex;
        return $"{actual.GetType().Name}: {actual.Message}";
    }
}
