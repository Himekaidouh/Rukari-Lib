using Rukari.Lib.Tools;

namespace Rukari.Lib.Tests;

internal static class ToolListNavigationTests
{
    internal static void ClosingReopeningAndSwitchingPagesRetainOnlyTheirOwnNavigation()
    {
        var model = new ToolListNavigation();
        string[] labels = Names(28);
        model.SetQuery("voice", "project-a", ToolListSearchMode.Locate, labels, 4, "voice-17");
        model.SetPage("voice", "project-a", ToolListSearchMode.Locate, labels, 4, 6);
        // Close/open destroys the render view, not the model; another page is independent meanwhile.
        model.SetQuery("effects", "project-a", ToolListSearchMode.Locate, labels, 4, "voice-2");
        ToolListNavigationView reopened = model.Read("voice", "project-a", ToolListSearchMode.Locate, labels, 4);
        Check.Equal("voice-17", reopened.Query, "Reopening restores the search text.");
        Check.Equal(6, reopened.Page, "Reopening restores the manually selected page.");
        Check.Equal(0, model.Read("effects", "project-a", ToolListSearchMode.Locate, labels, 4).Page,
            "Another page retains its independent navigation.");
    }

    internal static void DifferentProjectsAndSourcesNeverShareQueriesOrPages()
    {
        var model = new ToolListNavigation();
        string[] labels = Names(24);
        model.SetQuery("voice", "project-a/imported", ToolListSearchMode.Locate, labels, 4, "voice-19");
        model.SetPage("voice", "project-a/folder", ToolListSearchMode.Locate, labels, 4, 2);
        ToolListNavigationView otherProject = model.Read("voice", "project-b/imported", ToolListSearchMode.Locate, labels, 4);
        Check.Equal(string.Empty, otherProject.Query, "A new project starts without another project's query.");
        Check.Equal(0, otherProject.Page, "A new project starts at its own first page.");
        ToolListNavigationView folder = model.Read("voice", "project-a/folder", ToolListSearchMode.Locate, labels, 4);
        Check.Equal(string.Empty, folder.Query, "Folder browsing does not inherit the imported catalog query.");
        Check.Equal(2, folder.Page, "The folder's manually selected page is retained.");
        Check.Equal(4, model.Read("voice", "project-a/imported", ToolListSearchMode.Locate, labels, 4).Page,
            "Returning to a context restores that context's page.");
    }

    internal static void LocatePrefersExactNamesAndOtherwiseTheFirstContainingName()
    {
        var model = new ToolListNavigation();
        string[] labels = { "prefix-greeting.wav", "other.wav", "greeting.wav.tail", "GREETING.WAV", "greeting.wav" };
        ToolListNavigationView exact = model.SetQuery("voice", "a", ToolListSearchMode.Locate, labels, 2, "greeting.wav");
        Check.Equal<int?>(3, exact.MatchedIndex, "The first exact match beats an earlier substring match.");
        Check.Equal(1, exact.Page, "Locate opens the original list page containing the match.");
        Check.True(exact.ItemIndices.SequenceEqual(new[] { 0, 1, 2, 3, 4 }), "Locate preserves every original row.");
        Check.True(exact.VisibleIndices.SequenceEqual(new[] { 2, 3 }), "The surrounding original rows stay visible.");
        ToolListNavigationView partial = model.SetQuery("voice", "a", ToolListSearchMode.Locate, labels, 2, "greeting");
        Check.Equal<int?>(0, partial.MatchedIndex, "With no exact match, locate chooses the first containing row.");
        Check.Equal(0, partial.Page, "Partial matching uses the full list's ordering.");
    }

    internal static void RedrawAndRepeatedQueriesNeverUndoManualPaging()
    {
        var model = new ToolListNavigation();
        string[] labels = Names(30);
        model.SetQuery("voice", "a", ToolListSearchMode.Locate, labels, 5, "voice-12");
        model.SetPage("voice", "a", ToolListSearchMode.Locate, labels, 5, 5);
        for (int frame = 0; frame < 100; frame++)
            Check.Equal(5, model.Read("voice", "a", ToolListSearchMode.Locate, labels, 5).Page,
                "A redraw must not relocate to a previous match.");
        Check.Equal(5, model.SetQuery("voice", "a", ToolListSearchMode.Locate, labels, 5, "voice-12").Page,
            "Submitting unchanged search text must not relocate either.");
        Check.Equal(1, model.SetQuery("voice", "a", ToolListSearchMode.Locate, labels, 5, "voice-7").Page,
            "A genuinely changed query performs a new locate.");
    }

    internal static void ClearingLocateSearchPreservesTheBrowsedPage()
    {
        var model = new ToolListNavigation();
        string[] labels = Names(20);
        model.SetQuery("voice", "a", ToolListSearchMode.Locate, labels, 4, "voice-10");
        model.SetPage("voice", "a", ToolListSearchMode.Locate, labels, 4, 4);
        ToolListNavigationView cleared = model.Clear("voice", "a", ToolListSearchMode.Locate, labels, 4);
        Check.Equal(4, cleared.Page, "Clearing locate search retains the page the user browsed to.");
        Check.Equal(string.Empty, cleared.Query, "Only the query is cleared.");
        Check.Equal<int?>(null, cleared.MatchedIndex, "The old search hit is no longer claimed.");
        Check.Equal(4, model.Clear("voice", "a", ToolListSearchMode.Locate, labels, 4).Page,
            "Repeated clearing is idempotent.");
    }

    internal static void ExplicitLocateReturnsToCurrentMatchAndLeavesFilterPagingAlone()
    {
        var model = new ToolListNavigation();
        string[] labels = Names(30);
        model.SetQuery("voice", "a", ToolListSearchMode.Locate, labels, 5, "voice-12");
        model.SetPage("voice", "a", ToolListSearchMode.Locate, labels, 5, 5);
        ToolListNavigationView located = model.Locate("voice", "a", ToolListSearchMode.Locate, labels, 5);
        Check.Equal(2, located.Page, "Enter can explicitly return to the unchanged query's match.");
        Check.Equal("voice-12", located.Query, "Explicit locate does not change the user's text.");
        Check.Equal<int?>(12, located.MatchedIndex, "Explicit locate still identifies the original list index.");
        Check.Same(located, model.Read("voice", "a", ToolListSearchMode.Locate, labels, 5),
            "Redraw reuses the resulting view without another navigation request.");
        model.SetQuery("voice", "a", ToolListSearchMode.Locate, labels, 5, "missing");
        model.SetPage("voice", "a", ToolListSearchMode.Locate, labels, 5, 4);
        Check.Equal(4, model.Locate("voice", "a", ToolListSearchMode.Locate, labels, 5).Page,
            "Explicit locate with no match preserves the current page.");
        model.SetQuery("filter", "a", ToolListSearchMode.Filter, labels, 5, "voice");
        model.SetPage("filter", "a", ToolListSearchMode.Filter, labels, 5, 3);
        Check.Equal(3, model.Locate("filter", "a", ToolListSearchMode.Filter, labels, 5).Page,
            "Enter on a legacy filtering page does not force it to page zero.");
    }

    internal static void UnmatchedQueriesAndEmptyListsDoNotJumpOrThrow()
    {
        var model = new ToolListNavigation();
        string[] labels = Names(16);
        model.SetPage("voice", "a", ToolListSearchMode.Locate, labels, 4, 3);
        ToolListNavigationView absent = model.SetQuery("voice", "a", ToolListSearchMode.Locate, labels, 4, "missing");
        Check.Equal(3, absent.Page, "An unmatched locate keeps the current page.");
        Check.Equal<int?>(null, absent.MatchedIndex, "An unmatched locate returns no hit.");
        Check.Equal(16, absent.ItemIndices.Count, "An unmatched locate still displays the full catalog.");
        ToolListNavigationView empty = model.Read("voice", "a", ToolListSearchMode.Locate, Array.Empty<string>(), 0);
        Check.Equal(0, empty.Page, "An empty list is clamped to page zero.");
        Check.Equal(1, empty.PageCount, "Empty pagination has one harmless page.");
        Check.Equal(0, empty.VisibleIndices.Count, "An empty list has no visible indices.");
        Check.Equal("missing", empty.Query, "A temporarily empty list does not discard the search text.");
    }

    internal static void ChangedCatalogsAndPageSizesClampWithoutRelocatingStaleMatches()
    {
        var model = new ToolListNavigation();
        string[] labels = Names(30);
        model.SetQuery("voice", "a", ToolListSearchMode.Locate, labels, 5, "voice-21");
        model.SetPage("voice", "a", ToolListSearchMode.Locate, labels, 5, 5);
        ToolListNavigationView shorter = model.Read("voice", "a", ToolListSearchMode.Locate, Names(11), 5);
        Check.Equal(2, shorter.Page, "A shrunken list clamps to its last page.");
        Check.Equal<int?>(null, shorter.MatchedIndex, "An out-of-range match is dropped without a fresh search.");
        ToolListNavigationView resized = model.Read("voice", "a", ToolListSearchMode.Locate, Names(11), 10);
        Check.Equal(1, resized.Page, "A larger page size also clamps to an existing page.");
        Check.True(resized.VisibleIndices.SequenceEqual(new[] { 10 }), "The last partial page contains only valid rows.");

        model.SetQuery("voice", "a", ToolListSearchMode.Locate, labels, 5, "voice-7");
        string[] reordered = Names(30);
        (reordered[7], reordered[20]) = (reordered[20], reordered[7]);
        ToolListNavigationView stale = model.Read("voice", "a", ToolListSearchMode.Locate, reordered, 5);
        Check.Equal(1, stale.Page, "Reordering does not silently jump to the relocated name.");
        Check.Equal<int?>(20, stale.MatchedIndex, "A new snapshot refreshes the hit without changing the page or claiming the reused index.");
    }

    internal static void UnchangedSnapshotsReuseTheirViewAndNewSnapshotsRefreshHitsWithoutJumping()
    {
        var model = new ToolListNavigation();
        string[] labels = Names(24);
        ToolListNavigationView found = model.SetQuery("voice", "a", ToolListSearchMode.Locate, labels, 4, "voice-10");
        for (int frame = 0; frame < 100; frame++)
            Check.Same(found, model.Read("voice", "a", ToolListSearchMode.Locate, labels, 4),
                "An unchanged snapshot must reuse its view instead of allocating every frame.");
        ToolListNavigationView manual = model.SetPage("voice", "a", ToolListSearchMode.Locate, labels, 4, 5);
        Check.Same(manual, model.Read("voice", "a", ToolListSearchMode.Locate, labels, 4), "Manual paging updates the cached view.");
        string[] refreshed = Names(24);
        (refreshed[10], refreshed[2]) = (refreshed[2], refreshed[10]);
        ToolListNavigationView changed = model.Read("voice", "a", ToolListSearchMode.Locate, refreshed, 4);
        Check.Equal(5, changed.Page, "A catalog refresh must not undo subsequent manual paging.");
        Check.Equal<int?>(2, changed.MatchedIndex, "The fresh snapshot reports the current first exact hit.");
        Check.Same(changed, model.Read("voice", "a", ToolListSearchMode.Locate, refreshed, 4), "The fresh view also stays cached.");
        ToolListNavigationView filtered = model.SetQuery("filter", "a", ToolListSearchMode.Filter, labels, 4, "voice-1");
        Check.Same(filtered, model.Read("filter", "a", ToolListSearchMode.Filter, labels, 4), "Filtering an unchanged snapshot is cached too.");
    }

    internal static void FilterKeepsLegacySubsetPagingAndQueryResetBehaviour()
    {
        var model = new ToolListNavigation();
        string[] labels = { "other", "voice-b", "other-2", "VOICE-a", "voice-c", "last" };
        ToolListNavigationView filtered = model.SetQuery("legacy", "a", ToolListSearchMode.Filter, labels, 2, " VOICE ");
        Check.True(filtered.ItemIndices.SequenceEqual(new[] { 1, 3, 4 }), "Filter trims the query and matches without case sensitivity.");
        Check.True(filtered.VisibleIndices.SequenceEqual(new[] { 1, 3 }), "Filtered pages preserve original item indices.");
        Check.Equal(2, filtered.PageCount, "Filter counts pages in the filtered subset.");
        Check.Equal(1, model.SetPage("legacy", "a", ToolListSearchMode.Filter, labels, 2, 1).Page,
            "Filtered next page remains available.");
        ToolListNavigationView cleared = model.Clear("legacy", "a", ToolListSearchMode.Filter, labels, 2);
        Check.Equal(0, cleared.Page, "Legacy filter clearing returns to page zero.");
        Check.Equal(6, cleared.ItemIndices.Count, "Clearing filter restores every row.");
        ToolListNavigationView absent = model.SetQuery("legacy", "a", ToolListSearchMode.Filter, labels, 2, "missing");
        Check.Equal(0, absent.ItemIndices.Count, "A missing filter match produces an empty subset.");
        Check.Equal(0, absent.Page, "An empty filtered subset is clamped.");
    }

    internal static void NavigationNeverMutatesResourceLabelsOrCallsAnApplyAction()
    {
        var model = new ToolListNavigation();
        string[] labels = Names(18);
        string[] original = labels.ToArray();
        IReadOnlyList<string> readOnly = Array.AsReadOnly(labels);
        model.SetQuery("voice", "a", ToolListSearchMode.Locate, readOnly, 4, "voice-9");
        model.SetPage("voice", "a", ToolListSearchMode.Locate, readOnly, 4, int.MaxValue);
        model.Clear("voice", "a", ToolListSearchMode.Locate, readOnly, 4);
        model.Read("voice", "a", ToolListSearchMode.Filter, readOnly, 4);
        Check.True(original.SequenceEqual(labels), "Navigation cannot rewrite or reorder the provider's resource labels.");
        Check.True(typeof(ToolListNavigation).GetMethods().Where(method => method.DeclaringType == typeof(ToolListNavigation))
            .SelectMany(method => method.GetParameters()).All(parameter => !typeof(Delegate).IsAssignableFrom(parameter.ParameterType)),
            "The navigation API has no apply/select callback; binding remains an explicit provider operation.");
        Check.Equal(0, model.SetPage("voice", "a", ToolListSearchMode.Locate, readOnly, 4, int.MinValue).Page,
            "Out-of-range page requests clamp without selecting a row.");
    }

    private static string[] Names(int count) => Enumerable.Range(0, count).Select(index => $"voice-{index}").ToArray();
}
