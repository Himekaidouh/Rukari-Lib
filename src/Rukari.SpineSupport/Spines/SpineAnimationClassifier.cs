namespace Rukari.SpineSupport.Spines;

/// <summary>
/// What one animation actually keys, counted against the skeleton it belongs to (2026-09-21).
/// <para>
/// This is the whole point of the classifier: a name tells us almost nothing about whether an
/// animation can be layered — the wallpaper's own convention puts a whole reaction in
/// <c>Talk_01_M</c> and its attachments in <c>Talk_01_A</c>, while a skeleton the author has just
/// started may call an overlay <c>Arm_Raise</c> or anything else. What <em>is</em> decidable is
/// which bones and slots a timeline touches, so the overlay list is built from that and names are
/// only used where they genuinely carry meaning (the base loop and the reaction families our own
/// lobby code already recognises).
/// </para>
/// </summary>
public sealed record SpineAnimationKeyProfile(
    string Name,
    IReadOnlyList<string> BoneNames,
    IReadOnlyList<string> SlotNames,
    bool KeysDrawOrder,
    bool KeysDeform,
    int TimelineCount)
{
    public static SpineAnimationKeyProfile Empty(string name) =>
        new(name, Array.Empty<string>(), Array.Empty<string>(), false, false, 0);
}

/// <summary>What an animation is useful for, decided from its key profile.</summary>
public enum SpineAnimationRole
{
    /// <summary><c>Idle*</c> / <c>Start_Idle*</c>: the base loop or the entrance.</summary>
    Base = 0,

    /// <summary>A reaction family the lobby code already drives (<c>Talk</c>/<c>Pat</c>/<c>Pinch</c>/<c>Look</c>).</summary>
    Reaction = 1,

    /// <summary>Keys nothing usable (Spine's <c>Dummy</c>), or only system slots such as the eyes.</summary>
    Placeholder = 2,

    /// <summary>Keys nearly the whole skeleton: it replaces the pose instead of layering onto it.</summary>
    Replacement = 3,

    /// <summary>Keys a subset: the only kind worth offering as an overlay.</summary>
    OverlayCandidate = 4
}

/// <summary>One classified animation, with everything the page needs to explain the verdict.</summary>
public sealed record SpineAnimationVerdict(
    string Name,
    SpineAnimationRole Role,
    int BoneCount,
    int SlotCount,
    int SharedWithBaseCount,
    int IkDrivenCount,
    string Reason)
{
    /// <summary>
    /// True for the numbering convention the author uses for unified expression differentials
    /// (<c>01</c>, <c>02</c>, optionally with a short suffix). Those are driven by the official
    /// character card, not layered by us, and there are a lot of them — the page counts them and,
    /// by default, leaves them out of the list behind a switch.
    /// </summary>
    public bool Differential { get; init; }
}

/// <summary>
/// Classifies a skeleton's animations for the overlay page (2026-09-21).
/// <para>
/// Deliberately conservative in one direction only: a name that we cannot explain is still offered
/// as an overlay candidate, because hiding something the author made is worse than listing
/// something they will not use — the row carries its key counts and its clashes so the choice is
/// informed either way.
/// </para>
/// </summary>
public static class SpineAnimationClassifier
{
    /// <summary>Keying this share of a skeleton's bones means the animation poses the whole body.</summary>
    public const double ReplacementBoneFraction = 0.8;

    /// <summary>Slot names that mark an animation as system/expression-only when it keys no bones.</summary>
    private static readonly string[] SystemSlotHints = { "eye", "eyes" };

    /// <summary>
    /// Whether a name is one of the numbered expression differentials (<c>01</c>, <c>1</c>,
    /// <c>02_R</c>). Digits only, with at most a short letter suffix; anything that carries a word
    /// is treated as an ordinary name so a real animation can never be filtered out by accident.
    /// </summary>
    public static bool IsDifferentialName(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        int index = 0;
        while (index < name.Length && name[index] >= '0' && name[index] <= '9') index++;
        if (index == 0) return false;
        if (index == name.Length) return true;
        if (name[index] != '_') return false;
        int letters = 0;
        for (index++; index < name.Length; index++)
        {
            char character = name[index];
            if ((character < 'a' || character > 'z') && (character < 'A' || character > 'Z'))
            {
                return false;
            }

            letters++;
        }

        return letters is >= 1 and <= 3;
    }

    /// <summary>Name shapes our lobby code already drives; they have their own playback path.</summary>
    public static bool IsReactionName(string name)    {
        if (string.IsNullOrEmpty(name) || name.Length < 8) return false;
        if (!name.EndsWith("_M", StringComparison.Ordinal)
            && !name.EndsWith("_A", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (string family in new[] { "Talk", "Pat", "Pinch", "Look" })
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

    /// <summary>The base loop and the entrance, by the names the official side and our policy use.</summary>
    public static bool IsBaseName(string name) =>
        name.StartsWith("Idle", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Start_Idle", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether every slot this animation keys looks like a system/expression slot.</summary>
    private static bool IsSystemOnly(IReadOnlyList<string> slots)
    {
        if (slots.Count == 0) return false;
        foreach (string slot in slots)
        {
            bool looksSystem = false;
            foreach (string hint in SystemSlotHints)
            {
                if (slot.Contains(hint, StringComparison.OrdinalIgnoreCase))
                {
                    looksSystem = true;
                    break;
                }
            }

            if (!looksSystem) return false;
        }

        return true;
    }

    public static SpineAnimationVerdict Classify(
        SpineAnimationKeyProfile profile,
        int totalBones,
        int totalSlots,
        SpineAnimationKeyProfile? baseProfile,
        IReadOnlyCollection<string> ikDrivenBones)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(ikDrivenBones);

        int shared = 0;
        int ikDriven = 0;
        if (baseProfile != null)
        {
            var baseBones = new HashSet<string>(baseProfile.BoneNames, StringComparer.Ordinal);
            foreach (string bone in profile.BoneNames)
            {
                if (baseBones.Contains(bone)) shared++;
            }
        }

        var ik = new HashSet<string>(ikDrivenBones, StringComparer.Ordinal);
        foreach (string bone in profile.BoneNames)
        {
            if (ik.Contains(bone)) ikDriven++;
        }

        (SpineAnimationRole role, string reason) = Decide(profile, totalBones);
        return new SpineAnimationVerdict(
            profile.Name,
            role,
            profile.BoneNames.Count,
            profile.SlotNames.Count,
            shared,
            ikDriven,
            reason)
        {
            Differential = IsDifferentialName(profile.Name)
        };
    }

    private static (SpineAnimationRole Role, string Reason) Decide(
        SpineAnimationKeyProfile profile,
        int totalBones)
    {
        int bones = profile.BoneNames.Count;
        int slots = profile.SlotNames.Count;
        bool keysAnything = profile.TimelineCount > 0
            && (bones > 0 || slots > 0 || profile.KeysDeform || profile.KeysDrawOrder);
        if (!keysAnything)
        {
            return (SpineAnimationRole.Placeholder, "keys nothing");
        }

        if (IsBaseName(profile.Name))
        {
            return (SpineAnimationRole.Base, "idle/entrance name");
        }

        if (IsReactionName(profile.Name))
        {
            return (SpineAnimationRole.Reaction, "reaction family; has its own path");
        }

        if (bones == 0 && IsSystemOnly(profile.SlotNames))
        {
            return (SpineAnimationRole.Placeholder, "keys only system slots");
        }

        if (totalBones > 0 && bones >= totalBones * ReplacementBoneFraction)
        {
            return (SpineAnimationRole.Replacement, $"keys {bones}/{totalBones} bones");
        }

        return (SpineAnimationRole.OverlayCandidate, "keys a subset");
    }

    /// <summary>Short tag for the list row, so the verdict is visible without opening anything.</summary>
    public static string TagFor(SpineAnimationRole role) => role switch
    {
        SpineAnimationRole.OverlayCandidate => "可叠加",
        SpineAnimationRole.Base => "基础",
        SpineAnimationRole.Reaction => "反应",
        SpineAnimationRole.Placeholder => "占位",
        _ => "整体替换"
    };

    /// <summary>
    /// One list row: name, verdict tag, what it keys, and the two clashes that matter —
    /// targets shared with the base loop, and targets an IK constraint will overwrite anyway.
    /// </summary>
    public static string Describe(SpineAnimationVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        var parts = new List<string>
        {
            verdict.Name,
            verdict.Differential ? "差分（官方管）" : TagFor(verdict.Role),
            $"{verdict.BoneCount}骨/{verdict.SlotCount}槽"
        };
        if (verdict.SharedWithBaseCount > 0)
        {
            parts.Add($"与Idle重叠{verdict.SharedWithBaseCount}");
        }

        if (verdict.IkDrivenCount > 0)
        {
            parts.Add($"IK驱动{verdict.IkDrivenCount}");
        }

        return string.Join(" · ", parts);
    }
}
