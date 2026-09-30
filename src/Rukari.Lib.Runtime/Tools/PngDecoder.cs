extern alias unitycore;

using Texture2D = unitycore::UnityEngine.Texture2D;

namespace Rukari.Lib.Runtime.Tools;

/// <summary>
/// PNG decoding for the local atlas export.
///
/// This lives in its own file on purpose: <c>UnityEngine.ImageConversionModule</c> is referenced WITHOUT an
/// extern alias, while every other Unity module in this project has one, and an <c>extern alias</c> directive
/// hides the global namespace for that file. Texture2D still has to come through the CoreModule alias because
/// UnityEngine.dll only forwards its types.
/// </summary>
internal static class PngDecoder
{
    /// <summary>Fills the texture from PNG bytes. False leaves the texture untouched.</summary>
    internal static bool TryLoad(Texture2D texture, byte[] bytes) =>
        UnityEngine.ImageConversion.LoadImage(texture, bytes, markNonReadable: false);
}
