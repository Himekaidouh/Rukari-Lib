using Rukari.Lib.Tools;

namespace Rukari.Lib.Captions;

/// <summary>Everything the placement needs to know about one caption and the screen it must fit on.</summary>
public sealed record CaptionPlacementRequest
{
    /// <summary>Screen width in physical pixels.</summary>
    public float ScreenWidth { get; init; }

    /// <summary>Screen height in physical pixels. Y grows upward, like every other rectangle in the library.</summary>
    public float ScreenHeight { get; init; }

    /// <summary>Width the caption would like at its own font size, in physical pixels.</summary>
    public float TextWidth { get; init; }

    /// <summary>Height the caption would like at its own font size, in physical pixels.</summary>
    public float TextHeight { get; init; }

    /// <summary>
    /// Rectangles the caption must not cover: the official dialogue window, the characters on screen, the corners
    /// the interface keeps for itself. Overlapping the background is fine; covering a face is not.
    /// </summary>
    public IReadOnlyList<ToolInputRect>? Forbidden { get; init; }

    /// <summary>
    /// True while the scene has no dialogue window of its own — a pure CG, a cutscene, a moment with nothing but a
    /// song. The caption then owns far more of the screen and is drawn bigger, which is what it is for.
    /// </summary>
    public bool Cinematic { get; init; }

    /// <summary>Fraction of the screen kept clear on every side, so a caption never touches an edge.</summary>
    public float SafeMarginFraction { get; init; } = .035f;

    /// <summary>Seed of this line's own small offset, so consecutive captions do not land on the same pixel.</summary>
    public uint Seed { get; init; }

    /// <summary>Lean the timeline already gave this line, in degrees. It widens the rectangle that has to fit.</summary>
    public float TiltDegrees { get; init; }

    /// <summary>An author's pinned centre, 0..1, which replaces the automatic search entirely.</summary>
    public double? AnchorX { get; init; }

    /// <summary>An author's pinned centre, 0..1, where 1 is the top edge.</summary>
    public double? AnchorY { get; init; }

    /// <summary>
    /// How much of the screen's width the caption may use, from <see cref="UsableStart"/>. An editor keeps its own
    /// preview pane on the right, so a caption that spreads across the whole window would sit on top of the very
    /// picture it is meant to accompany; a sheet played on its own leaves this at one.
    /// </summary>
    public float UsableFraction { get; init; } = 1f;

    /// <summary>
    /// Where the usable area starts, as a fraction of the screen's width. A preview shown inside an editor's own
    /// pane sets this to that pane's left edge and <see cref="UsableFraction"/> to its width, so the caption is
    /// judged where it will really appear.
    /// </summary>
    public float UsableStart { get; init; }

    /// <summary>
    /// Reserved, and no longer read by the policy. A preview now takes exactly the placement the scene will use —
    /// inside the editor's own preview pane — so what the author judges is what will be shown.
    /// </summary>
    public bool PreviewStrip { get; init; }
}

/// <summary>Where a caption was placed, how big it ended up, and how much of it lands on something it should not.</summary>
public readonly record struct CaptionPlacement(
    ToolInputRect Rect,
    float FontScale,
    float OverlapFraction,
    int BandIndex,
    bool Pinned)
{
    /// <summary>True when no candidate was free, so the renderer should dim the line instead of hiding a face.</summary>
    public bool OverlapsSomething => OverlapFraction > .001f;
}

/// <summary>
/// Chooses where a caption goes. The rule is not "random": a caption is placed in one of a few bands the layout is
/// designed around, nudged a little so consecutive lines do not sit on the same pixel, and scored by how much of it
/// would land on something the scene needs — a character, the dialogue window, a corner of the interface. The best
/// candidate wins, and a caption that cannot avoid everything is dimmed rather than dropped.
/// </summary>
public static class CaptionPlacementPolicy
{
    /// <summary>How far a line may be nudged from its band's centre, as a fraction of the screen.</summary>
    public const float MaximumNudgeFraction = .05f;

    /// <summary>Bands a caption may use while the scene has a dialogue window of its own.</summary>
    private static readonly (float X, float Y)[] DialogueBands =
    {
        (.5f, .80f), (.5f, .71f), (.32f, .76f), (.68f, .76f)
    };

    /// <summary>
    /// Bands a caption may use when the scene is nothing but a picture and a song. The extra high and middle ones
    /// are what makes the layer worth having in a pure CG: there is no dialogue window to avoid, so the text may
    /// take the centre of the frame.
    /// </summary>
    private static readonly (float X, float Y)[] CinematicBands =
    {
        (.5f, .80f), (.5f, .68f), (.5f, .55f), (.30f, .72f), (.70f, .72f), (.5f, .42f)
    };

    /// <summary>Font sizes are multiplied by this while the scene is cinematic, up to the sheet's own size.</summary>
    public const float CinematicFontBoost = 1.35f;

    /// <summary>Chooses the placement of one caption, or an all-zero rectangle when the screen is unusable.</summary>
    public static CaptionPlacement Place(CaptionPlacementRequest request)
    {
        if (!float.IsFinite(request.ScreenWidth) || !float.IsFinite(request.ScreenHeight)
            || request.ScreenWidth <= 1 || request.ScreenHeight <= 1)
            return default;

        float margin = Math.Max(0, request.SafeMarginFraction) * request.ScreenWidth;
        // The usable area is what is left after the part of the screen the scene keeps for itself. Every band's x
        // is a fraction of THIS area, so a caption spreads across the room it has instead of crowding one edge.
        float usableStart = Math.Clamp(request.UsableStart, 0f, .9f) * request.ScreenWidth;
        float usableWidth = Math.Max(64f, Math.Min(request.ScreenWidth - usableStart,
            request.ScreenWidth * Math.Clamp(request.UsableFraction, .2f, 1f)));
        float available = Math.Max(16f, usableWidth - margin * 2);
        float usableLeft = usableStart + margin;
        float usableRight = usableStart + usableWidth - margin;

        // The caption fits itself first: a long line is scaled down until it fits the window, which is what keeps
        // "it must show itself completely" true without the placement having to guess later. There is deliberately
        // no lower bound on the scale: a caption that would be unreadable is the author's to shorten, and silently
        // letting it run off the screen would be worse.
        float fontScale = request.TextWidth > available && request.TextWidth > 0
            ? available / request.TextWidth
            : 1f;
        float width = Math.Max(1f, request.TextWidth * fontScale);
        float height = Math.Max(1f, request.TextHeight * fontScale);

        // A leaned rectangle needs a slightly larger box, so a tilted caption cannot poke past the safe area.
        double radians = Math.Abs(request.TiltDegrees) * Math.PI / 180.0;
        float boxWidth = (float)(width * Math.Cos(radians) + height * Math.Sin(radians));
        float boxHeight = (float)(width * Math.Sin(radians) + height * Math.Cos(radians));

        bool pinned = request.AnchorX.HasValue && request.AnchorY.HasValue;
        if (pinned)
        {
            var pinnedRect = new ToolInputRect(
                Clamp(usableStart + (float)request.AnchorX!.Value * usableWidth - boxWidth / 2,
                    usableLeft, Math.Max(usableLeft, usableRight - boxWidth)),
                Clamp((float)request.AnchorY!.Value * request.ScreenHeight, boxHeight / 2, request.ScreenHeight - boxHeight / 2) - boxHeight / 2,
                boxWidth, boxHeight);
            return new CaptionPlacement(pinnedRect, fontScale,
                Overlap(pinnedRect, request.Forbidden), -1, true);
        }

        (float X, float Y)[] bands = request.Cinematic ? CinematicBands : DialogueBands;
        float verticalHalf = boxHeight / 2;
        float horizontalHalf = boxWidth / 2;
        // The nudge is per line and bounded, so a run of captions reads as alive without any of them wandering off.
        float nudgeX = MaximumNudgeFraction * usableWidth * Nudge(request.Seed, 1);
        float nudgeY = MaximumNudgeFraction * request.ScreenHeight * Nudge(request.Seed, 2);

        int best = -1;
        float bestOverlap = float.MaxValue;
        ToolInputRect bestRect = default;
        for (int i = 0; i < bands.Length; i++)
        {
            float centreX = usableStart + bands[i].X * usableWidth + nudgeX;
            float centreY = bands[i].Y * request.ScreenHeight + nudgeY;
            var rect = new ToolInputRect(
                Clamp(centreX - horizontalHalf, usableLeft, Math.Max(usableLeft, usableRight - boxWidth)),
                Clamp(centreY - verticalHalf, margin, request.ScreenHeight - margin - boxHeight),
                boxWidth, boxHeight);
            float overlap = Overlap(rect, request.Forbidden);
            // A tie keeps the earlier band, so the preferred position is the one a calm scene uses.
            if (overlap < bestOverlap - .0001f)
            {
                bestOverlap = overlap;
                best = i;
                bestRect = rect;
            }
        }
        if (best < 0) return default;
        return new CaptionPlacement(bestRect, fontScale, bestOverlap, best, false);
    }

    /// <summary>
    /// How much of <paramref name="rect"/> lands on the rectangles it must avoid, as a fraction of its own area.
    /// Zero means it covers nothing that matters.
    /// </summary>
    public static float Overlap(ToolInputRect rect, IReadOnlyList<ToolInputRect>? forbidden)
    {
        if (forbidden is null || forbidden.Count == 0 || !rect.IsValid) return 0;
        float covered = 0;
        foreach (ToolInputRect other in forbidden)
        {
            if (!other.IsValid) continue;
            float width = Math.Min(rect.X + rect.Width, other.X + other.Width) - Math.Max(rect.X, other.X);
            float height = Math.Min(rect.Y + rect.Height, other.Y + other.Height) - Math.Max(rect.Y, other.Y);
            if (width > 0 && height > 0) covered += width * height;
        }
        return Math.Clamp(covered / (rect.Width * rect.Height), 0f, 1f);
    }

    /// <summary>
    /// How much a caption is dimmed when it could not avoid what is under it. Covering a face is worse than being
    /// faint, so the layer answers a forced overlap with transparency rather than with a move.
    /// </summary>
    public static float AlphaFor(CaptionPlacement placement) =>
        placement.OverlapFraction <= .001f ? 1f : Math.Clamp(1f - placement.OverlapFraction * 1.6f, .25f, 1f);

    /// <summary>
    /// Font scale for a scene. A cinematic one — a pure CG, a cutscene, a song with nothing else on screen — may
    /// draw the caption larger, because there is no dialogue window for it to compete with. The caller multiplies
    /// the sheet's font size by this before measuring, so the placement scores the size that will really be drawn.
    /// </summary>
    public static float FontScaleForScene(bool cinematic) => cinematic ? CinematicFontBoost : 1f;

    private static float Nudge(uint seed, int channel)
    {
        unchecked
        {
            uint hash = seed ^ (uint)(channel * 2654435761u);
            hash ^= hash >> 15;
            hash *= 0x2545F491u;
            hash ^= hash >> 13;
            return (hash & 0xFFFF) / 32767.5f - 1f;
        }
    }

    private static float Clamp(float value, float minimum, float maximum) =>
        maximum < minimum ? minimum : Math.Clamp(value, minimum, maximum);
}
