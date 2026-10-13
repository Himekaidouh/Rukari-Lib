namespace AzureArchive.VideoTools.Core.Commands;

/// <summary>
/// One alias edge: the identity the engine reports for a compiled script
/// (<paramref name="ReportedSha256"/>) belongs to the same compile as a capture
/// stored under an inner boundary identity (<paramref name="CapturedSha256"/>).
/// </summary>
public sealed record EditorPreviewIdentityAlias(string ReportedSha256, string CapturedSha256);

/// <summary>
/// Resolves engine-reported compiled-script identities to the boundary identity a
/// capture was stored under.
/// <para>
/// The embedded-directive capture happens at the nested continuous boundary,
/// while the editor selection and the managed Unity log carry the FINAL official
/// text of the script-text wrapper. Whenever the wrapper's official text differs
/// from the nested one, the generation's capture is unreachable by the identity
/// the engine reports and the preview lease fails closed (2026-10-13 field log:
/// 9 of 17 generations rejected with ambiguous-or-missing-window-intersection
/// although the window carried the selected script's own identity).
/// </para>
/// <para>
/// An edge is followed only when the reported identity matches exactly AND the
/// target identity really is a candidate of the same generation, so an edge that
/// does not describe reality changes nothing.
/// </para>
/// </summary>
public sealed class EditorPreviewIdentityAliasMap
{
    public const int MaximumDepth = 4;
    private readonly List<EditorPreviewIdentityAlias> _edges = new();

    public int Count => _edges.Count;

    /// <summary>
    /// Records that <paramref name="reported"/> and <paramref name="capturedSha256"/>
    /// describe the same compile. Self edges are not recorded: they carry no
    /// information because the reported identity is already a direct candidate key.
    /// </summary>
    public bool Add(CompiledScriptIdentity reported, string capturedSha256)
    {
        ArgumentNullException.ThrowIfNull(reported);
        if (string.IsNullOrEmpty(capturedSha256)
            || string.Equals(reported.Sha256, capturedSha256, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (EditorPreviewIdentityAlias edge in _edges)
        {
            if (string.Equals(edge.ReportedSha256, reported.Sha256, StringComparison.Ordinal)
                && string.Equals(edge.CapturedSha256, capturedSha256, StringComparison.Ordinal))
            {
                return false;
            }
        }

        // The newest edge for one reported identity wins: a later compile of the
        // same text is the one a later window belongs to.
        _edges.RemoveAll(edge =>
            string.Equals(edge.ReportedSha256, reported.Sha256, StringComparison.Ordinal));
        _edges.Add(new EditorPreviewIdentityAlias(reported.Sha256, capturedSha256));
        return true;
    }

    /// <summary>
    /// Walks the edges from <paramref name="reported"/> and returns the first
    /// identity that is really present in <paramref name="candidates"/>, or null
    /// when no edge explains it. The walk is bounded, so a cyclic pair of edges
    /// can never spin.
    /// </summary>
    public CompiledScriptIdentity? Resolve(
        CompiledScriptIdentity reported,
        IEnumerable<CompiledScriptIdentity> candidates)
    {
        ArgumentNullException.ThrowIfNull(reported);
        ArgumentNullException.ThrowIfNull(candidates);
        List<CompiledScriptIdentity> keys = candidates.ToList();
        string current = reported.Sha256;
        for (int depth = 0; depth < MaximumDepth; depth++)
        {
            EditorPreviewIdentityAlias? edge = null;
            for (int index = _edges.Count - 1; index >= 0; index--)
            {
                if (string.Equals(_edges[index].ReportedSha256, current, StringComparison.Ordinal))
                {
                    edge = _edges[index];
                    break;
                }
            }

            if (edge == null)
            {
                return null;
            }

            foreach (CompiledScriptIdentity key in keys)
            {
                if (string.Equals(key.Sha256, edge.CapturedSha256, StringComparison.Ordinal))
                {
                    return key;
                }
            }

            current = edge.CapturedSha256;
        }

        return null;
    }
}
