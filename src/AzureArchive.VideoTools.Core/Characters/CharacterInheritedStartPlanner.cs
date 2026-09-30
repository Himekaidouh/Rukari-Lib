using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Characters;

/// <summary>
/// Pure managed target for an inherited editor-preview character state.
/// Position Z and rotation X are not controlled by character directives and
/// therefore remain at their current live values.
/// </summary>
public sealed record CharacterInheritedStartTarget(
    CharacterTransformState State,
    bool PositionChanged,
    bool RotationChanged);

/// <summary>
/// Composes a folded preview-chain state over the transform that the official
/// scene created. The calculation is intentionally independent of the current
/// transformed X/Y/Yaw/tilt for axes the chain explicitly controlled, so
/// applying it repeatedly cannot accumulate relative movement or toggle a
/// horizontal flip back off.
/// Axes that no folded command ever touched are not inherited at all: they
/// keep the live value the current official scene produced instead of being
/// overwritten with the lineage's official baseline.
/// </summary>
public sealed class CharacterInheritedStartPlanner
{
    public Result<CharacterInheritedStartTarget> Plan(
        PreviewChainSlotState inheritedStart,
        CharacterTransformState current,
        CharacterTransformBaseline officialBaseline)
    {
        if (inheritedStart == null)
        {
            return Result<CharacterInheritedStartTarget>.Fail(
                "Inherited character start state is missing.");
        }

        if (current == null
            || !current.Position.IsFinite
            || !current.LocalEulerAngles.IsFinite)
        {
            return Result<CharacterInheritedStartTarget>.Fail(
                "Current character transform is invalid.");
        }

        if (officialBaseline == null
            || string.IsNullOrWhiteSpace(officialBaseline.SceneIdentity)
            || string.IsNullOrWhiteSpace(officialBaseline.OccupantIdentifier))
        {
            return Result<CharacterInheritedStartTarget>.Fail(
                "Official character transform baseline is invalid.");
        }

        if (inheritedStart.PublicSlot != officialBaseline.PublicSlot)
        {
            return Result<CharacterInheritedStartTarget>.Fail(
                "Inherited character start state belongs to a different slot.");
        }

        CharacterTransformState official = officialBaseline.State;
        if (!official.Position.IsFinite || !official.LocalEulerAngles.IsFinite)
        {
            return Result<CharacterInheritedStartTarget>.Fail(
                "Official character transform baseline is invalid.");
        }

        if (!IsValid(inheritedStart.X)
            || !IsValid(inheritedStart.Y)
            || !IsValid(inheritedStart.RotationZ)
            || inheritedStart.FoldedCommandCount < 0)
        {
            return Result<CharacterInheritedStartTarget>.Fail(
                "Inherited character start values are invalid.");
        }

        float positionX = ResolveAxis(inheritedStart.X, official.Position.X, current.Position.X);
        float positionY = ResolveAxis(inheritedStart.Y, official.Position.Y, current.Position.Y);
        // Flip is a TOGGLE against the live orientation, not an absolute
        // baseline heading: the captured origin eulerY can itself be flipped
        // when a character instance survives across previews, and
        // "poisonedOfficial.Y + halfTurn" then collapses back to unflipped
        // (360 → 0 through the shortest arc). Toggling
        // IsHorizontallyFlipped(current) toward FlippedFromOfficial is
        // idempotent and independent of the baseline heading.
        float eulerY = current.LocalEulerAngles.Y;
        if (inheritedStart.FlipControlled)
        {
            bool currentlyFlipped = CharacterScreenRotation.IsHorizontallyFlipped(eulerY);
            bool flipMismatch = currentlyFlipped != inheritedStart.FlippedFromOfficial;
            if (flipMismatch)
            {
                float turn = CharacterAngleMath.HalfTurnDegrees;
                if (eulerY >= 0f)
                {
                    eulerY += turn;
                }
                else
                {
                    eulerY -= turn;
                }
            }
        }
        float screenEulerZ = NearestEquivalentPreservingCurrent(
            ResolveAxis(
                inheritedStart.RotationZ,
                official.LocalEulerAngles.Z,
                current.LocalEulerAngles.Z),
            current.LocalEulerAngles.Z);

        var state = new CharacterTransformState(
            new CharacterVector3(positionX, positionY, current.Position.Z),
            new CharacterVector3(
                current.LocalEulerAngles.X,
                eulerY,
                screenEulerZ));
        if (!state.Position.IsFinite || !state.LocalEulerAngles.IsFinite)
        {
            return Result<CharacterInheritedStartTarget>.Fail(
                "Computed inherited character start is invalid.");
        }

        return Result<CharacterInheritedStartTarget>.Ok(
            new CharacterInheritedStartTarget(
                state,
                positionX != current.Position.X || positionY != current.Position.Y,
                eulerY != current.LocalEulerAngles.Y
                    || screenEulerZ != current.LocalEulerAngles.Z));
    }

    /// <summary>
    /// Controlled axes resolve against the lineage's official baseline;
    /// uncontrolled axes keep the live value the current scene produced so
    /// the planner never writes an axis that AAVT did not command.
    /// </summary>
    private static float ResolveAxis(
        PreviewChainAxisState axis,
        float official,
        float current) => !axis.IsControlled
            ? current
            : axis.HasAbsoluteValue
                ? axis.AbsoluteValue
                : official + axis.RelativeFromOfficial;

    private static bool IsValid(PreviewChainAxisState axis) =>
        float.IsFinite(axis.RelativeFromOfficial)
        && float.IsFinite(axis.AbsoluteValue);

    private static float NearestEquivalentPreservingCurrent(float target, float current)
    {
        float delta = CharacterAngleMath.Normalize360(target - current);
        if (delta >= CharacterAngleMath.HalfTurnDegrees)
        {
            delta -= CharacterAngleMath.FullTurnDegrees;
        }

        return current + delta;
    }
}
