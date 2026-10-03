namespace Rukari.Lib.Tools;

/// <summary>A managed list view. Indices refer to the provider's original, unmodified list.</summary>
public sealed record ToolListNavigationView(
    string Query,
    int Page,
    int PageCount,
    int? MatchedIndex,
    IReadOnlyList<int> ItemIndices,
    IReadOnlyList<int> VisibleIndices);

/// <summary>
/// Keeps list navigation independently for each page and resource context. The renderer may discard focus,
/// native controls and its current view on hide without discarding this model. This never selects a resource,
/// creates an editor token, or invokes a provider action. Use the model from one UI thread.
/// </summary>
public sealed class ToolListNavigation
{
    private readonly Dictionary<(string PageId, string ContextId), NavigationState> _states = new();

    /// <summary>
    /// Reads the retained query and page, clamping only when the list or page size changed. Labels are an
    /// immutable provider snapshot. Repeated reads of that snapshot return the same view without allocations.
    /// A fresh snapshot refreshes the search hit but never relocates the page: otherwise a redraw would undo
    /// the user's subsequent next/previous page presses.
    /// </summary>
    public ToolListNavigationView Read(string pageId, string contextId, ToolListSearchMode mode,
        IReadOnlyList<string> labels, int pageSize)
    {
        NavigationState state = State(pageId, contextId, mode, labels);
        int size = Math.Max(1, pageSize);
        if (state.CachedView is { } cached && ReferenceEquals(state.CachedLabels, labels)
            && state.CachedLabelCount == labels.Count && state.CachedPageSize == size && state.CachedMode == mode
            && cached.Query == state.Query && cached.Page == state.Page) return cached;

        if (mode == ToolListSearchMode.Locate
            && (!ReferenceEquals(state.MatchedAgainst, labels) || state.MatchedLabelCount != labels.Count))
        {
            state.MatchedIndex = FindMatch(labels, state.Query.Trim());
            state.MatchedAgainst = labels;
            state.MatchedLabelCount = labels.Count;
        }
        IReadOnlyList<int> indices = ItemIndices(state, mode, labels);
        int pages = indices.Count == 0 ? 1 : 1 + (indices.Count - 1) / size;
        state.Page = Math.Clamp(state.Page, 0, pages - 1);

        int? match = mode == ToolListSearchMode.Locate ? state.MatchedIndex : null;

        int first = state.Page * size;
        int count = Math.Min(size, indices.Count - first);
        var visible = new int[count];
        for (int i = 0; i < count; i++) visible[i] = indices[first + i];
        var view = new ToolListNavigationView(state.Query, state.Page, pages, match, indices, Array.AsReadOnly(visible));
        state.CachedLabels = labels;
        state.CachedLabelCount = labels.Count;
        state.CachedPageSize = size;
        state.CachedMode = mode;
        state.CachedView = view;
        return view;
    }

    /// <summary>
    /// A changed locate query jumps to its first exact name match, or its first containing name when there is
    /// no exact match. It leaves the full list visible. Empty or unmatched queries preserve the current page.
    /// Filter retains the legacy behaviour: a changed query filters rows and starts at page zero.
    /// </summary>
    public ToolListNavigationView SetQuery(string pageId, string contextId, ToolListSearchMode mode,
        IReadOnlyList<string> labels, int pageSize, string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        NavigationState state = State(pageId, contextId, mode, labels);
        if (!string.Equals(state.Query, query, StringComparison.Ordinal))
        {
            state.Query = query;
            state.MatchedIndex = null;
            state.MatchedAgainst = null;
            if (mode == ToolListSearchMode.Filter) state.Page = 0;
            else
            {
                int? match = FindMatch(labels, query.Trim());
                state.MatchedAgainst = labels;
                state.MatchedLabelCount = labels.Count;
                if (match is int index)
                {
                    state.MatchedIndex = index;
                    state.Page = index / Math.Max(1, pageSize);
                }
            }
        }
        return Read(pageId, contextId, mode, labels, pageSize);
    }

    /// <summary>Changes only navigation; no resource row is selected or applied.</summary>
    public ToolListNavigationView SetPage(string pageId, string contextId, ToolListSearchMode mode,
        IReadOnlyList<string> labels, int pageSize, int page)
    {
        State(pageId, contextId, mode, labels).Page = page;
        return Read(pageId, contextId, mode, labels, pageSize);
    }

    /// <summary>
    /// Explicitly locates the current query again, for Enter or a locate button after manual browsing.
    /// Repeated reads and unchanged query updates still preserve browsing. Filter ignores this request.
    /// </summary>
    public ToolListNavigationView Locate(string pageId, string contextId, ToolListSearchMode mode,
        IReadOnlyList<string> labels, int pageSize)
    {
        NavigationState state = State(pageId, contextId, mode, labels);
        if (mode == ToolListSearchMode.Filter) return Read(pageId, contextId, mode, labels, pageSize);
        state.MatchedIndex = FindMatch(labels, state.Query.Trim());
        state.MatchedAgainst = labels;
        state.MatchedLabelCount = labels.Count;
        if (state.MatchedIndex is int index) state.Page = index / Math.Max(1, pageSize);
        state.CachedView = null;
        return Read(pageId, contextId, mode, labels, pageSize);
    }

    /// <summary>Clears the query. Locate retains the current page; clearing a nonempty Filter query returns to page zero.</summary>
    public ToolListNavigationView Clear(string pageId, string contextId, ToolListSearchMode mode,
        IReadOnlyList<string> labels, int pageSize) =>
        SetQuery(pageId, contextId, mode, labels, pageSize, string.Empty);

    private NavigationState State(string pageId, string contextId, ToolListSearchMode mode,
        IReadOnlyList<string> labels)
    {
        ArgumentNullException.ThrowIfNull(pageId);
        ArgumentNullException.ThrowIfNull(contextId);
        ArgumentNullException.ThrowIfNull(labels);
        if (string.IsNullOrWhiteSpace(pageId)) throw new ArgumentException("A page ID is required.", nameof(pageId));
        if (mode is not ToolListSearchMode.Filter and not ToolListSearchMode.Locate)
            throw new ArgumentOutOfRangeException(nameof(mode));
        var key = (pageId, contextId);
        if (!_states.TryGetValue(key, out NavigationState? state))
        {
            state = new NavigationState();
            _states.Add(key, state);
        }
        return state;
    }

    private static IReadOnlyList<int> ItemIndices(NavigationState state, ToolListSearchMode mode,
        IReadOnlyList<string> labels)
    {
        string query = state.Query.Trim();
        if (mode == ToolListSearchMode.Filter && query.Length != 0)
        {
            var matched = new List<int>();
            for (int i = 0; i < labels.Count; i++)
                if ((labels[i] ?? string.Empty).Contains(query, StringComparison.OrdinalIgnoreCase)) matched.Add(i);
            return matched.AsReadOnly();
        }
        // Locate reads do not rebuild thousands of unchanged indices every frame.
        if (state.AllIndices.Count != labels.Count)
            state.AllIndices = Array.AsReadOnly(Enumerable.Range(0, labels.Count).ToArray());
        return state.AllIndices;
    }

    private static int? FindMatch(IReadOnlyList<string> labels, string query)
    {
        if (query.Length == 0) return null;
        int? firstContaining = null;
        for (int i = 0; i < labels.Count; i++)
        {
            string label = labels[i] ?? string.Empty;
            if (string.Equals(label, query, StringComparison.OrdinalIgnoreCase)) return i;
            if (firstContaining is null && label.Contains(query, StringComparison.OrdinalIgnoreCase))
                firstContaining = i;
        }
        return firstContaining;
    }

    private sealed class NavigationState
    {
        internal string Query = string.Empty;
        internal int Page;
        internal int? MatchedIndex;
        internal IReadOnlyList<string>? MatchedAgainst;
        internal int MatchedLabelCount;
        internal IReadOnlyList<int> AllIndices = Array.Empty<int>();
        internal IReadOnlyList<string>? CachedLabels;
        internal int CachedLabelCount;
        internal int CachedPageSize;
        internal ToolListSearchMode CachedMode;
        internal ToolListNavigationView? CachedView;
    }
}
