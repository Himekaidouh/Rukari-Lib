namespace AzureArchive.VideoTools.Core.Playback;

/// <summary>
/// Preserves the official integer slider/storage contract while replacing the
/// two-second runtime tier with a more useful two-and-a-half-second delay.
/// </summary>
public static class AutoDialogueDelayPolicy
{
    public const float OfficialReplacedDelaySeconds = 2f;
    public const float ReplacementDelaySeconds = 2.5f;

    private const float MatchToleranceSeconds = 0.001f;

    public static float MapRuntimeSeconds(float officialDelaySeconds)
    {
        if (!float.IsFinite(officialDelaySeconds))
        {
            return officialDelaySeconds;
        }

        return MathF.Abs(officialDelaySeconds - OfficialReplacedDelaySeconds)
            <= MatchToleranceSeconds
            ? ReplacementDelaySeconds
            : officialDelaySeconds;
    }
}
