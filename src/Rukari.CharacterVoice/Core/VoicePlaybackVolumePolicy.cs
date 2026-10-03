namespace Rukari.CharacterVoice.Core;

internal static class VoicePlaybackVolumePolicy
{
    internal const float DefaultPercent = 50f;
    internal const float MaximumPercent = 100f;

    // The midpoint preserves the current output. This is a 0..2 strength control, not a
    // second percentage that silently halves voices when upgrading an existing profile.
    internal static float Normalize(double percent) => !double.IsFinite(percent) ? DefaultPercent
        : (float)Math.Round(Math.Clamp(percent, 0, MaximumPercent), MidpointRounding.AwayFromZero);

    internal static bool IsNeutral(double percent) => Normalize(percent) == DefaultPercent;

    internal static float FromPointer(float x, float left, float width) => width <= 0 || !float.IsFinite(width)
        ? DefaultPercent : Normalize((x - left) / width * MaximumPercent);

    internal static float ScaleOfficialVolume(float officialVolume, double percent)
        => !float.IsFinite(officialVolume) ? 0f
            : Math.Clamp(Math.Clamp(officialVolume, 0f, 1f) * (Normalize(percent) / DefaultPercent), 0f, 1f);
}
