namespace Rukari.Lib.Voices;

/// <summary>Identifiers for the preview voice authoring contracts.</summary>
public static class VoiceCapabilities
{
    /// <summary>Capability ID for <see cref="IVoiceAuthoringService"/> version 0.1.</summary>
    public const string Authoring = "aavt.voice.authoring";
}

/// <summary>A selectable voice asset, without exposing a filesystem path.</summary>
/// <param name="Id">An opaque resource identifier used in voice edit requests.</param>
/// <param name="DisplayName">The name presented to the author.</param>
public sealed record VoiceResource(string Id, string DisplayName);

/// <summary>A managed snapshot of the currently selected dialogue's voice binding.</summary>
/// <param name="Token">An opaque, temporary selection token. Do not persist it or interpret its contents.</param>
/// <param name="Revision">An opaque revision checked when applying an edit.</param>
/// <param name="DialogueText">Dialogue text for display only; it is not the selection identity.</param>
/// <param name="ResourceId">The provider-managed resource ID, or null when no binding exists in that provider's scope. Inspect capability details; other native voice bindings may exist.</param>
public sealed record VoiceSelection(string Token, string Revision, string DialogueText, string? ResourceId);

/// <summary>A request to change the voice binding of a previously captured selection.</summary>
/// <param name="SelectionToken">The temporary token from <see cref="VoiceSelection.Token"/>.</param>
/// <param name="ExpectedRevision">The revision from the selection snapshot, used to reject stale edits.</param>
/// <param name="ResourceId">A resource ID to bind, or null to remove the binding. Removal does not stop playback.</param>
public sealed record VoiceEditRequest(string SelectionToken, string ExpectedRevision, string? ResourceId);

/// <summary>The resulting selection snapshot after a voice binding edit.</summary>
/// <param name="Selection">A fresh selection snapshot and revision.</param>
/// <param name="Changed">Whether this operation changed the binding.</param>
public sealed record VoiceEditResult(VoiceSelection Selection, bool Changed);

/// <summary>
/// Synchronous, main-thread-only voice authoring. Use <see cref="IModRuntime.InvokeAsync{T}"/> to call it
/// from other threads. Returned records are managed snapshots and must not contain native objects.
/// This contract edits bindings; it does not expose voice playback, pause, stop, or automatic dialogue advance.
/// </summary>
public interface IVoiceAuthoringService
{
    /// <summary>Reads the available voice resources as a managed snapshot.</summary>
    ModResult<IReadOnlyList<VoiceResource>> ReadCatalog();

    /// <summary>Captures the current dialogue selection and its voice binding.</summary>
    ModResult<VoiceSelection> ReadSelection();

    /// <summary>Applies a binding edit, rejecting expired selections and mismatched revisions.</summary>
    ModResult<VoiceEditResult> Apply(VoiceEditRequest request);
}
