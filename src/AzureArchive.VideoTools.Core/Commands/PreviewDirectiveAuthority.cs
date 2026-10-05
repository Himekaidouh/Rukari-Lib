namespace AzureArchive.VideoTools.Core.Commands;

/// <summary>Distinguishes an authorized deletion from an unobserved scene.</summary>
public static class PreviewDirectiveAuthority
{
    public static string? Resolve(bool sceneIsAuthoritative, string? overlayDirective, Func<string?> savedLookup)
    {
        ArgumentNullException.ThrowIfNull(savedLookup);
        // The bool returned by a dictionary TryGet is not edit authority. In particular,
        // an authorized empty scene must not revive a deleted directive from the saved index.
        return sceneIsAuthoritative ? overlayDirective : savedLookup();
    }
}
