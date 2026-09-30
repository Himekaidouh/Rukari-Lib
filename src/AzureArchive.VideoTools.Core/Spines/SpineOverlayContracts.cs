using AzureArchive.VideoTools.Core.Results;
using Rukari.Lib.Spines;

namespace AzureArchive.VideoTools.Core.Spines;

/// <summary>What a spine overlay directive asks for (2026-09-21).</summary>
public enum SpineOverlayOperation
{
    /// <summary>Put one animation on a reserved track and leave it there until it is cleared.</summary>
    Play = 0,

    /// <summary>Fade the reserved track out and forget it.</summary>
    Clear = 1
}

/// <summary>How the overlay's keys combine with what the lower tracks already applied.</summary>
public enum SpineOverlayBlend
{
    /// <summary>Overwrite the keyed bones and slots. The default, and what 3.8 assets are authored for.</summary>
    Replace = 0,

    /// <summary>Add the keyed values as deltas; the author must key deltas rather than poses.</summary>
    Add = 1
}

/// <summary>One parsed overlay directive: which character, which animation, on which reserved track.</summary>
/// <param name="PublicSlot">The 1-based character slot the overlay drives.</param>
/// <param name="Operation">Play an animation or clear the track.</param>
/// <param name="AnimationName">The animation to play; empty for a clear.</param>
/// <param name="TrackIndex">The reserved track the overlay owns.</param>
/// <param name="MixMilliseconds">Fade-in, set per track entry.</param>
/// <param name="FadeMilliseconds">Fade-out used when the track is cleared.</param>
/// <param name="Loop">Whether the entry repeats forever.</param>
/// <param name="Blend">Replace the keyed values or add them as deltas.</param>
/// <param name="Hold">
/// Whether a <em>non-looping</em> overlay must keep its last frame until it is cleared. Null means
/// "whatever the provider is configured to do", which is why it is not part of the canonical text
/// when the author did not ask: a directive that said nothing about holding must stay byte-identical
/// to what earlier builds produced.
/// </param>
public sealed record SpineOverlayCommand(
    int PublicSlot,
    SpineOverlayOperation Operation,
    string AnimationName,
    int TrackIndex,
    int MixMilliseconds,
    int FadeMilliseconds,
    bool Loop,
    SpineOverlayBlend Blend,
    bool? Hold = null);

/// <summary>
/// The syntax side of the reserved-track contract: the bounds a directive is held to before it is
/// ever handed to the provider.
/// <para>
/// The numbers themselves live in <see cref="SpineOverlayTracks"/> (Rukari lib) because the parser,
/// the provider that writes the track and the release path that clears it all have to agree; this
/// type only gives them the name the command family uses.
/// </para>
/// </summary>
public static class SpineOverlayTrackPolicy
{
    public const int FirstReservedTrack = SpineOverlayTracks.First;
    public const int LastReservedTrack = SpineOverlayTracks.Last;
    public const int DefaultTrack = SpineOverlayTracks.Default;
    public const int DefaultFadeMilliseconds = SpineOverlayTracks.DefaultFadeMilliseconds;
    public const int MaxFadeMilliseconds = SpineOverlayTracks.MaxFadeMilliseconds;
    public const int MaxMixMilliseconds = SpineOverlayTracks.MaxMixMilliseconds;
    public const int FirstPublicSlot = SpineOverlayTracks.FirstPublicSlot;
    public const int LastPublicSlot = SpineOverlayTracks.LastPublicSlot;

    /// <summary>Longest animation name accepted; the engine's own longest is far below this.</summary>
    public const int MaxAnimationNameLength = 120;

    public static bool IsReserved(int trackIndex) => SpineOverlayTracks.IsReserved(trackIndex);
}

public interface ISpineOverlayDirectiveParser
{
    Result<SpineOverlayCommand> Parse(string directive);
}

/// <summary>
/// Bounds that hold for every overlay command, however it was parsed. Kept separate from the
/// parser so the lease, the projection and the runtime all fail for exactly the same reasons.
/// </summary>
public static class SpineOverlayCommandValidator
{
    public static Result Validate(SpineOverlayCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.PublicSlot < SpineOverlayTrackPolicy.FirstPublicSlot
            || command.PublicSlot > SpineOverlayTrackPolicy.LastPublicSlot)
        {
            return Result.Fail(
                $"Spine overlay slot must be {SpineOverlayTrackPolicy.FirstPublicSlot}"
                + $"..{SpineOverlayTrackPolicy.LastPublicSlot}.");
        }

        if (!SpineOverlayTrackPolicy.IsReserved(command.TrackIndex))
        {
            return Result.Fail(
                $"Spine overlay track must stay inside the reserved range "
                + $"{SpineOverlayTrackPolicy.FirstReservedTrack}..{SpineOverlayTrackPolicy.LastReservedTrack}.");
        }

        if (command.MixMilliseconds < 0
            || command.MixMilliseconds > SpineOverlayTrackPolicy.MaxMixMilliseconds)
        {
            return Result.Fail(
                $"Spine overlay mix must be 0..{SpineOverlayTrackPolicy.MaxMixMilliseconds} ms.");
        }

        if (command.FadeMilliseconds < 0
            || command.FadeMilliseconds > SpineOverlayTrackPolicy.MaxFadeMilliseconds)
        {
            return Result.Fail(
                $"Spine overlay fade must be 0..{SpineOverlayTrackPolicy.MaxFadeMilliseconds} ms.");
        }

        if (command.Operation == SpineOverlayOperation.Clear)
        {
            if (command.AnimationName.Length != 0)
            {
                return Result.Fail("A spine overlay clear must not name an animation.");
            }

            return command.Hold.HasValue
                ? Result.Fail("Property 'hold' is not valid for spine overlay clear.")
                : Result.Ok();
        }

        if (command.Hold.HasValue && command.Loop)
        {
            // A looping entry never ends, so it has no last frame to hold. Saying so is more useful
            // than silently ignoring the property.
            return Result.Fail(
                "Property 'hold' only applies to a non-looping overlay; write loop=false.");
        }

        if (command.AnimationName.Length == 0)
        {
            return Result.Fail("A spine overlay play must name an animation.");
        }

        if (command.AnimationName.Length > SpineOverlayTrackPolicy.MaxAnimationNameLength)
        {
            return Result.Fail(
                $"Spine overlay animation name must be at most "
                + $"{SpineOverlayTrackPolicy.MaxAnimationNameLength} characters.");
        }

        // The canonical directive is ';'-separated and is compared byte for byte by the lease, so
        // a name that could split it (or hide a property) is refused instead of being escaped.
        foreach (char character in command.AnimationName)
        {
            if (character == ';' || char.IsControl(character))
            {
                return Result.Fail("Spine overlay animation name must not contain ';' or control characters.");
            }
        }

        return Result.Ok();
    }
}
