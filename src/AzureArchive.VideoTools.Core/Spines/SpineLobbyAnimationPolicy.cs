namespace AzureArchive.VideoTools.Core.Spines;

public sealed record SpineLobbyAnimationPlan(
    string PrimaryAnimationName,
    string? CompanionAnimationName,
    bool Loop,
    bool ReturnToIdle,
    float FadeSeconds);

/// <summary>
/// Resolves a selected, existing Spine animation without changing its saved name.
/// Ordinary character catalogs keep the native playback behavior.
/// </summary>
public static class SpineLobbyAnimationPolicy
{
    public static bool IsLobbyCatalog(IEnumerable<string> availableAnimationNames)
    {
        var names = new HashSet<string>(availableAnimationNames, StringComparer.Ordinal);
        return IsLobbyCatalog(names);
    }

    public static SpineLobbyAnimationPlan? BuildPlan(
        IEnumerable<string> availableAnimationNames,
        string selectedName,
        bool requestedLoop)
    {
        var names = new HashSet<string>(availableAnimationNames, StringComparer.Ordinal);
        if (!IsLobbyCatalog(names) || !names.Contains(selectedName))
        {
            return null;
        }

        string primaryName = selectedName;
        string? companionName = null;
        if (selectedName.EndsWith("_M", StringComparison.Ordinal))
        {
            string candidate = selectedName[..^2] + "_A";
            if (names.Contains(candidate))
            {
                companionName = candidate;
            }
        }
        else if (selectedName.EndsWith("_A", StringComparison.Ordinal))
        {
            string candidate = selectedName[..^2] + "_M";
            if (names.Contains(candidate))
            {
                primaryName = candidate;
                companionName = selectedName;
            }
        }

        bool isEntrance = primaryName == "Start_Idle_01";
        bool isTalk = IsNumberedTalk(primaryName);
        bool returnToIdle = isEntrance || isTalk;
        return new SpineLobbyAnimationPlan(
            primaryName,
            companionName,
            returnToIdle ? false : requestedLoop,
            returnToIdle,
            isEntrance ? 1f : isTalk ? 0.2f : 0f);
    }

    private static bool IsLobbyCatalog(HashSet<string> names) =>
        names.Contains("Idle_01") && names.Contains("Start_Idle_01");

    private static bool IsNumberedTalk(string name)
    {
        if (!name.StartsWith("Talk_", StringComparison.Ordinal)
            || !(name.EndsWith("_M", StringComparison.Ordinal)
                || name.EndsWith("_A", StringComparison.Ordinal))
            || name.Length <= "Talk__M".Length)
        {
            return false;
        }

        for (int index = "Talk_".Length; index < name.Length - 2; index++)
        {
            if (name[index] < '0' || name[index] > '9')
            {
                return false;
            }
        }

        return true;
    }
}
