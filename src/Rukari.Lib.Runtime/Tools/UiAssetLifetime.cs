extern alias unitycore;

using HideFlags = unitycore::UnityEngine.HideFlags;
using Object = unitycore::UnityEngine.Object;
using Sprite = unitycore::UnityEngine.Sprite;
using Texture2D = unitycore::UnityEngine.Texture2D;

namespace Rukari.Lib.Runtime.Tools;

/// <summary>
/// Native ownership for the shared skin. A .NET cache of IL2CPP wrappers is not a native asset owner.
/// All calls belong on the game main thread; retained assets must be destroyed by their cache at shutdown.
/// </summary>
internal static class UiAssetLifetime
{
    internal static void Retain(Object value) => value.hideFlags |= HideFlags.DontUnloadUnusedAsset;

    internal static bool IsAlive(Object? value)
    {
        try
        {
            // Managed null alone misses a destroyed Unity object. This matches the settings-entry guard.
            return value is not null && !value.WasCollected && value.Pointer != IntPtr.Zero && value != null;
        }
        catch { return false; }
    }

    internal static bool IsAlive(Sprite? sprite, Texture2D? texture) => IsAlive(sprite) && IsAlive(texture);

    internal static void Destroy(Object? value)
    {
        // Never dereference a destroyed sprite to recover its texture: caches own both explicitly.
        try { if (IsAlive(value)) Object.Destroy(value); }
        catch { /* A teardown race must not prevent cleanup of the remaining owned assets. */ }
    }
}
