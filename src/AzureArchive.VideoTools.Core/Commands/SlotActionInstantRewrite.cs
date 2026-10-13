using AzureArchive.VideoTools.Core.Cameras;
using AzureArchive.VideoTools.Core.Characters;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Commands;

/// <summary>
/// Rewrites one canonical slot action into its instantaneous form, for a forced
/// finish. The engine has already moved on by then, so the remaining segments must
/// write their endpoint instead of animating towards it: a tween that is still
/// running when the next row starts makes that row inherit a mid-animation
/// position instead of the endpoint the author wrote.
/// <para>
/// A relative destination keeps its exact value, so applying the rewritten segments
/// in their authored order still accumulates to the same endpoint.
/// </para>
/// <para>
/// Only families whose canonical form carries a real <c>duration=</c> token are
/// rewritten. A preset is defined by its motion, so it has no honest instantaneous
/// form and the caller keeps the authored directive. Every rewrite is re-parsed
/// before it is returned: a substitution the family's own parser does not accept as
/// a zero-duration command is reported as a failure, and the caller falls back to
/// the authored form.
/// </para>
/// </summary>
public static class SlotActionInstantRewrite
{
    private const string DurationToken = "duration=";

    public static Result<string> ToInstant(string canonicalDirective)
    {
        ArgumentNullException.ThrowIfNull(canonicalDirective);
        if (canonicalDirective.StartsWith("#char;", StringComparison.Ordinal))
        {
            return Rewrite(
                canonicalDirective,
                static text =>
                {
                    Result<CharacterTransformCommand> parsed =
                        new CharacterTransformDirectiveParser().Parse(text);
                    return parsed.Success && parsed.Value != null
                        ? parsed.Value.DurationMilliseconds
                        : -1;
                },
                "character transform");
        }

        if (canonicalDirective.StartsWith(
                SceneCameraCommandFamilyCompiler.CanonicalRootToken + ";",
                StringComparison.Ordinal))
        {
            return Rewrite(
                canonicalDirective,
                static text =>
                {
                    Result<SceneCameraCommand> parsed = new SceneCameraDirectiveParser().Parse(text);
                    return parsed.Success && parsed.Value != null
                        ? parsed.Value.DurationMilliseconds
                        : -1;
                },
                "scene camera");
        }

        return Result<string>.Fail(
            "only character transform and scene camera actions have a canonical instantaneous form.");
    }

    private static Result<string> Rewrite(
        string canonicalDirective,
        Func<string, int> durationOf,
        string family)
    {
        if (durationOf(canonicalDirective) == 0)
        {
            // Already instantaneous: nothing to rewrite and nothing to risk.
            return Result<string>.Ok(canonicalDirective);
        }

        int token = canonicalDirective.IndexOf(DurationToken, StringComparison.Ordinal);
        // A second token would make the substitution ambiguous, so refuse instead of
        // guessing which one carries the duration.
        if (token < 0 || canonicalDirective.LastIndexOf(DurationToken, StringComparison.Ordinal) != token)
        {
            return Result<string>.Fail($"{family} directive has no single duration token to rewrite.");
        }

        int valueStart = token + DurationToken.Length;
        int valueEnd = valueStart;
        while (valueEnd < canonicalDirective.Length
            && canonicalDirective[valueEnd] is >= '0' and <= '9')
        {
            valueEnd++;
        }

        if (valueEnd == valueStart)
        {
            return Result<string>.Fail($"{family} duration token carries no value.");
        }

        string rewritten = string.Concat(
            canonicalDirective.AsSpan(0, valueStart),
            "0",
            canonicalDirective.AsSpan(valueEnd));
        return durationOf(rewritten) == 0
            ? Result<string>.Ok(rewritten)
            : Result<string>.Fail($"{family} instantaneous form was rejected by its own parser.");
    }
}
