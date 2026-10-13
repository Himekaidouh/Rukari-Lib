using Rukari.Lib.Tools;

namespace Rukari.Lib.Tests;

/// <summary>
/// The composition arbitration. The engine's input-method switch is one global value, so two surfaces that each wrote
/// it for themselves used to fight — the loser being whichever happened to run later in the frame. These tests fix
/// the rule that replaces that: any owner keeps it on, and it goes off only when the last one lets go.
/// </summary>
internal static class ToolCompositionStateTests
{
    internal static void CompositionStaysOnWhileAnyOwnerWantsIt()
    {
        var state = new ToolCompositionState();
        Check.True(!state.Wanted, "Nothing wants composition to begin with.");
        Check.True(state.Set("drawer", true), "The first owner turns it on.");
        Check.True(state.Wanted, "One owner is enough to want it.");
        Check.True(!state.Set("window", true), "A second owner changes nothing about the answer.");
        Check.Equal(2, state.OwnerCount);
        // The drawer letting go must not switch composition off under the window that still wants it: this is the
        // exact interference that was possible when each surface wrote the global switch itself.
        Check.True(!state.Set("drawer", false), "Releasing one owner of two leaves composition on.");
        Check.True(state.Wanted, "The remaining owner still wants it.");
        Check.True(state.Set("window", false), "The last owner releasing turns it off.");
        Check.True(!state.Wanted, "Nobody wants it once every owner let go.");
        Check.Equal(0, state.OwnerCount);
    }

    internal static void TheCursorFollowsTheLastOwnerThatReportedOne()
    {
        var state = new ToolCompositionState();
        Check.True(!state.Cursor.IsValid, "There is no caret before anyone reports one.");
        state.Set("drawer", true, new ToolInputRect(10f, 20f, 100f, 30f));
        Check.Equal(10f, state.Cursor.X);
        state.Set("window", true, new ToolInputRect(400f, 500f, 120f, 30f));
        Check.Equal(400f, state.Cursor.X);
        // An owner that reports nothing (or nonsense) cannot move the caret away from the one that did.
        state.Set("drawer", true);
        Check.Equal(400f, state.Cursor.X, "A wish without a caret leaves the caret where it was.");
        state.Set("window", false);
        Check.Equal(400f, state.Cursor.X, "Releasing an owner does not move the caret while another owns it.");
        state.Set("drawer", false);
        Check.True(!state.Cursor.IsValid, "With no owners there is no caret to aim at.");
        state.Clear();
        Check.True(!state.Wanted && !state.Cursor.IsValid, "Clearing releases everything.");
    }

    internal static void UnknownAndRepeatedWishesCannotBreakTheArbitration()
    {
        var state = new ToolCompositionState();
        Check.True(!state.Set("", true), "An empty owner is ignored.");
        Check.True(!state.Set("   ", true), "A blank owner is ignored.");
        Check.True(!state.Wanted, "Neither of them wanted composition.");
        state.Set("a", true);
        Check.True(!state.Set("a", true), "The same owner asking twice does not change the answer.");
        Check.Equal(1, state.OwnerCount);
        Check.True(!state.Set("b", false), "Releasing an owner that never asked changes nothing.");
        // The owner set is bounded, so a page that leaked owners on every frame cannot grow it without limit.
        for (int i = 0; i < 64; i++) state.Set("owner" + i, true);
        Check.True(state.OwnerCount <= 32, "The owner set is bounded.");
        Check.True(state.Wanted, "It still wants composition after the bound was reached.");
        state.Clear();
        Check.True(!state.Wanted, "Clearing releases everyone.");
    }
}
