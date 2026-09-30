using System;
using System.Reflection;

namespace AzureArchive.VideoTools.Interop;

internal static class InteropObjectGuard
{
    public static bool IsAlive(object? value)
    {
        if (ReferenceEquals(value, null))
        {
            return false;
        }

        try
        {
            PropertyInfo? wasCollected = value.GetType().GetProperty("WasCollected", BindingFlags.Public | BindingFlags.Instance);
            if (wasCollected?.GetValue(value) is bool collected && collected)
            {
                return false;
            }

            PropertyInfo? pointer = value.GetType().GetProperty("Pointer", BindingFlags.Public | BindingFlags.Instance);
            if (pointer?.GetValue(value) is IntPtr ptr && ptr == IntPtr.Zero)
            {
                return false;
            }
        }
        catch
        {
            // A missing lifetime helper is not enough to reject an otherwise live wrapper.
        }

        return true;
    }
}
