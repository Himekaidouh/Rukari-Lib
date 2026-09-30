using Rukari.Lib.Tools;

namespace Rukari.Lib.Tests;

/// <summary>
/// A hosted page that owns native or expensive state is told when its panel appears and disappears. What the
/// renderer depends on is "paired and never overlapping": exactly one show per hide, nothing repeated while the
/// page stays on screen, and a hide even when the panel was closed by collapsing the tree rather than by the page.
/// </summary>
internal static class ToolPanelSessionTests
{
    internal static void ShowAndHideCallbacksFireOncePerTransition()
    {
        var session = new ToolPanelSession();
        var first = new MinimalPage("first");
        var second = new MinimalPage("second");

        Check.True(session.Current is null, "Nothing is showing before the first frame.");
        Check.True(session.Advance(null) is null, "A closed panel that stays closed is not a transition.");

        ToolPanelTransition opened = Require(session.Advance(first), "Opening a page is one transition.");
        Check.True(opened.Hidden is null, "Nothing was showing before the first page.");
        Check.Same(first, opened.Shown, "The page that opened is the one that is told.");
        Check.Same(first, session.Current, "The session remembers which page owns the panel.");
        Check.True(session.Advance(first) is null, "A page that stays on screen is not told again every frame.");

        ToolPanelTransition switched = Require(session.Advance(second), "Switching page is one transition.");
        Check.Same(first, switched.Hidden, "The page that lost the panel is hidden first.");
        Check.Same(second, switched.Shown, "…and the page that took it is shown second.");

        ToolPanelTransition closed = Require(session.Advance(null), "Closing the panel is one transition.");
        Check.Same(second, closed.Hidden, "Closing the panel hides the page that owned it.");
        Check.True(closed.Shown is null, "Nothing is shown when the panel closes.");
        Check.True(session.Advance(null) is null, "An already closed panel is not hidden a second time.");
        Check.True(session.Current is null, "A closed session owns no page.");

        // The renderer is thrown away while a page is showing: that page still has to hear about it, or its own
        // canvas would outlive the panel that owned it.
        session.Advance(first);
        ToolPanelTransition torn = Require(session.Reset(), "Tearing the renderer down hides the page.");
        Check.Same(first, torn.Hidden, "The page on screen is the one told.");
        Check.True(session.Reset() is null, "Reset is idempotent, so a repeated teardown cannot hide twice.");
        Check.True(session.Current is null, "A reset session owns no page.");
    }

    private static ToolPanelTransition Require(ToolPanelTransition? transition, string message) =>
        transition ?? throw new InvalidOperationException(message);

    /// <summary>
    /// A page that only draws has to stay a two-member class. If this type stops compiling, IToolPanelContent grew
    /// a member — and every already-compiled mod that implements it would then fail to LOAD with a
    /// TypeLoadException, which is the failure the optional sibling interfaces exist to prevent. Width and
    /// lifecycle are deliberately NOT implemented here.
    /// </summary>
    private sealed class MinimalPage : IToolPanelContent
    {
        internal MinimalPage(string id) => Id = id;

        internal string Id { get; }

        public float PreferredHeight => 400f;

        public void Draw(IToolPanelSurface surface) => _ = surface.Width;
    }
}
