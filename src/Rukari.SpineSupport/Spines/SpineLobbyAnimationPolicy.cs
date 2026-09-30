namespace Rukari.SpineSupport.Spines;

public sealed record SpineLobbyAnimationPlan(
    string PrimaryAnimationName,
    string? CompanionAnimationName,
    bool Loop,
    bool ReturnToIdle,
    float FadeSeconds,
    bool AdvanceWhenFinished);

/// <summary>
/// Resolves a selected, existing Spine animation without changing its saved name.
/// Ordinary character catalogs keep the native playback behavior.
///
/// <para>
/// A lobby is recognised by carrying both <c>Idle_01</c> and <c>Start_Idle_01</c>. Its one-shot animations are the
/// entrance and the reaction families the game uses for touch interaction — <c>Talk</c>, <c>Pat</c> (patting),
/// <c>Pinch</c> and <c>Look</c>, each optionally with an <c>End</c> variant and a <c>_M</c>/<c>_A</c> track pair.
/// They all play once and fade back to <c>Idle_01</c> instead of looping, and the entrance additionally asks the
/// playback to move on to the next line once it has played out.
/// </para>
/// </summary>
public static class SpineLobbyAnimationPolicy
{
    /// <summary>The animation every lobby opens with; the official side hardcodes the same name.</summary>
    public const string EntranceAnimationName = "Start_Idle_01";

    /// <summary>
    /// The reaction families a lobby uses for its touch interactions. Their animations are one-shot and may have a
    /// companion track: <c>Pat_01_M</c> pairs with <c>Pat_01_A</c>, <c>PatEnd_01_M</c> with <c>PatEnd_01_A</c>.
    /// </summary>
    private static readonly string[] ReactionFamilies = { "Talk", "Pat", "Pinch", "Look" };

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

        bool isEntrance = primaryName == EntranceAnimationName;
        bool isReaction = IsReactionAnimation(primaryName);
        bool oneShot = isEntrance || isReaction;
        return new SpineLobbyAnimationPlan(
            primaryName,
            companionName,
            oneShot ? false : requestedLoop,
            oneShot,
            isEntrance ? 1f : isReaction ? 0.2f : 0f,
            isEntrance);
    }

    /// <summary>
    /// True for the lobby's reaction animations: <c>Talk_01_M</c>, <c>PatEnd_01_A</c>, <c>Pinch_02_M</c>,
    /// <c>Look_01_M</c> … — a reaction family, an optional <c>End</c>, a number and a track letter.
    /// </summary>
    public static bool IsReactionAnimation(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length < 8) return false;
        if (!name.EndsWith("_M", StringComparison.Ordinal) && !name.EndsWith("_A", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (string family in ReactionFamilies)
        {
            if (!name.StartsWith(family, StringComparison.Ordinal)) continue;
            string middle = name[family.Length..^2];
            if (middle.StartsWith("End", StringComparison.Ordinal)) middle = middle[3..];
            if (middle.Length < 2 || middle[0] != '_') return false;
            for (int index = 1; index < middle.Length; index++)
            {
                if (middle[index] < '0' || middle[index] > '9') return false;
            }

            return true;
        }

        return false;
    }

    private static bool IsLobbyCatalog(HashSet<string> names) =>
        names.Contains("Idle_01") && names.Contains(EntranceAnimationName);
}
