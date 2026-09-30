namespace Rukari.Lib.Spines;

/// <summary>Identifiers for the spine overlay playback contract (2026-09-21).</summary>
public static class SpineOverlayCapabilities
{
    /// <summary>Capability ID for <see cref="ISpineOverlayCommandService"/> version 0.1.</summary>
    public const string Dispatch = "aavt.spine.overlay";
}

/// <summary>
/// The tracks the overlay provider owns, and the bounds every overlay shares.
/// <para>
/// The engine's own tracks are 0 (idle), 2 (main), 3 (our <c>_A</c> companion layer) and 4
/// (blinking), so the reserved range starts well above them. It lives in the contract rather than in
/// the provider or in the command syntax because three places have to agree on it: the parser that
/// refuses an out-of-range track, the provider that writes the track, and the release path that
/// clears it again.
/// </para>
/// </summary>
public static class SpineOverlayTracks
{
    /// <summary>First track the overlay feature owns.</summary>
    public const int First = 20;

    /// <summary>Last track the overlay feature owns; one overlay per track, so this is also how many can run at once.</summary>
    public const int Last = 29;

    /// <summary>Track used when a directive does not name one.</summary>
    public const int Default = First;

    /// <summary>Fade-out used when a directive does not name one.</summary>
    public const int DefaultFadeMilliseconds = 200;

    /// <summary>Largest fade a directive may ask for.</summary>
    public const int MaxFadeMilliseconds = 5000;

    /// <summary>Largest fade-in a directive may ask for.</summary>
    public const int MaxMixMilliseconds = 5000;

    /// <summary>First character slot an overlay may drive.</summary>
    public const int FirstPublicSlot = 1;

    /// <summary>Last character slot an overlay may drive.</summary>
    public const int LastPublicSlot = 5;

    /// <summary>Whether one track index belongs to the overlay feature.</summary>
    public static bool IsReserved(int trackIndex) => trackIndex >= First && trackIndex <= Last;
}

/// <summary>What one overlay request asks the provider to do.</summary>
public enum SpineOverlayRequestKind
{
    /// <summary>Put one animation on a reserved track and leave it there until it is cleared.</summary>
    Play = 0,

    /// <summary>Fade a reserved track out and forget it.</summary>
    Clear = 1
}

/// <summary>
/// One overlay request, in primitives only.
/// <para>
/// The provider owns spine and the reserved tracks; the shared command pipeline owns the
/// <c>#aavt;spine</c> syntax. Passing the parsed values rather than the canonical text keeps the
/// syntax in one assembly and the runtime knowledge in the other.
/// </para>
/// </summary>
/// <param name="SceneIdentity">The scene the directive belongs to; used only to release it later.</param>
/// <param name="PublicSlot">The 1-based character slot the overlay drives.</param>
/// <param name="Kind">Play an animation or clear the track.</param>
/// <param name="AnimationName">The animation to play; empty for <see cref="SpineOverlayRequestKind.Clear"/>.</param>
/// <param name="TrackIndex">The reserved track the overlay owns.</param>
/// <param name="MixMilliseconds">Fade-in, set per track entry so the asset-wide default mix never applies.</param>
/// <param name="FadeMilliseconds">Fade-out used when the track is cleared.</param>
/// <param name="Loop">Whether the entry repeats; an overlay that must stay put has to loop.</param>
/// <param name="Additive">Add the keys as deltas instead of replacing them.</param>
/// <param name="Hold">
/// Whether a non-looping overlay must keep its last frame until it is cleared. Null asks the
/// provider to use its configured default, so an author who said nothing about holding keeps
/// whatever behaviour the provider was set to.
/// </param>
/// <param name="StartAtEnd">
/// Whether the entry is placed at its last frame instead of played from the start. The preview
/// chain sets this for an inherited overlay: a card that never mentioned it must show the pose
/// continuous playback would already be in, not replay the animation.
/// </param>
public sealed record SpineOverlayRequest(
    string SceneIdentity,
    int PublicSlot,
    SpineOverlayRequestKind Kind,
    string AnimationName,
    int TrackIndex,
    int MixMilliseconds,
    int FadeMilliseconds,
    bool Loop,
    bool Additive,
    bool? Hold = null,
    bool StartAtEnd = false);

/// <summary>
/// What an overlay command did, as a managed snapshot. <see cref="Applied"/> is false when the
/// request was understood but had nowhere to land — a slot with no readable skeleton, or an
/// animation the skeleton does not own. That is a per-command authoring problem, so it is reported
/// rather than thrown: the rest of the card still has to be dispatched.
/// </summary>
/// <param name="PublicSlot">The character slot the command addressed.</param>
/// <param name="Operation">Either <c>play</c> or <c>clear</c>, for the dispatch log line.</param>
/// <param name="AnimationName">The animation the command named, if any.</param>
/// <param name="TrackIndex">The reserved track the command addressed.</param>
/// <param name="Applied">Whether a live skeleton actually received the change.</param>
/// <param name="Detail">Why it was applied or not; diagnostic text, never parsed.</param>
public sealed record SpineOverlayExecutionSnapshot(
    int PublicSlot,
    string Operation,
    string AnimationName,
    int TrackIndex,
    bool Applied,
    string Detail);

/// <summary>
/// Main-thread-only spine overlay execution for the <c>#aavt;spine</c> family. The provider keeps
/// every write inside its own reserved track range, sets mix per track entry rather than on the
/// shared <c>AnimationStateData</c>, and never touches the engine's own tracks.
/// </summary>
public interface ISpineOverlayCommandService
{
    /// <summary>Applies one overlay command to the live skeleton of <c>request.PublicSlot</c>.</summary>
    /// <param name="request">The parsed overlay command.</param>
    /// <returns>What happened; see <see cref="SpineOverlayExecutionSnapshot.Applied"/>.</returns>
    ModResult<SpineOverlayExecutionSnapshot> ApplyOverlay(SpineOverlayRequest request);

    /// <summary>
    /// Fades out every track this provider applied for one scene, and forgets them. The editor
    /// preview rebuilds the scene for the next card, so the overlays of the card being left behind
    /// have to be released explicitly; playback relies on this only when a scene is torn down.
    /// </summary>
    /// <param name="sceneIdentity">The scene whose overlays are released.</param>
    /// <param name="fadeMilliseconds">Fade-out for each released track.</param>
    /// <returns>How many tracks were released.</returns>
    ModResult<int> ReleaseScene(string sceneIdentity, int fadeMilliseconds);
}
