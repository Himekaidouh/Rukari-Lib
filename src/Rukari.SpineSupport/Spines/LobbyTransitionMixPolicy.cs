namespace Rukari.SpineSupport.Spines;

/// <summary>
/// How long a lobby blends from the animation it is playing into the next one.
///
/// <para>
/// Spine crossfades every <c>SetAnimation</c> by <c>AnimationStateData.DefaultMix</c>, and AA leaves that at zero,
/// so a lobby cuts between animations. One number is enough for most transitions; two cases deserve their own:
/// a fade back into <c>Idle_01</c> after a reaction or the entrance is the one the eye notices, and re-selecting the
/// animation that is already playing must not fade at all — a fade there would restart the motion from a blend of
/// itself and look like a hitch.
/// </para>
/// </summary>
internal static class LobbyTransitionMixPolicy
{
    /// <summary>Blend used for everything that has no rule of its own.</summary>
    internal const float DefaultMixSeconds = 0.2f;

    /// <summary>Blend used when the lobby settles back into its idle animation.</summary>
    internal const float EndToIdleMixSeconds = 0.35f;

    /// <summary>The idle animation every lobby returns to.</summary>
    internal const string IdleAnimationName = "Idle_01";

    /// <summary>Seconds to spend blending <paramref name="from"/> into <paramref name="to"/>.</summary>
    internal static float MixFor(string? from, string? to, float defaultMix, float endToIdleMix)
    {
        if (string.IsNullOrEmpty(to) || defaultMix <= 0f) return 0f;
        if (string.IsNullOrEmpty(from)) return Clamp(defaultMix);
        // The same animation is not a transition: blending it with itself delays the motion for no reason.
        if (string.Equals(from, to, StringComparison.Ordinal)) return 0f;
        bool settling = string.Equals(to, IdleAnimationName, StringComparison.Ordinal)
            && (SpineLobbyAnimationPolicy.IsReactionAnimation(from)
                || string.Equals(from, SpineLobbyAnimationPolicy.EntranceAnimationName, StringComparison.Ordinal));
        float seconds = settling && endToIdleMix > 0f ? endToIdleMix : defaultMix;
        return Clamp(seconds);
    }

    /// <summary>A blend longer than a second is a cut with extra steps; keep it inside what an eye reads as motion.</summary>
    private static float Clamp(float seconds) => seconds <= 0f ? 0f : seconds > 1f ? 1f : seconds;
}
