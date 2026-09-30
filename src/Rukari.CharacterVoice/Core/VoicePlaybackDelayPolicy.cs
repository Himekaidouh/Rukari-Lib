namespace Rukari.CharacterVoice.Core;

internal static class VoicePlaybackDelayPolicy
{
    internal const float MaximumSeconds = 10f;

    internal static double Normalize(double seconds) => !double.IsFinite(seconds) ? 0
        : Math.Round(Math.Clamp(seconds, 0, MaximumSeconds) * 10, MidpointRounding.AwayFromZero) / 10;

    internal static float FromPointer(float x, float left, float width) => width <= 0 || !float.IsFinite(width)
        ? 0 : (float)Normalize((x - left) / width * MaximumSeconds);
}
