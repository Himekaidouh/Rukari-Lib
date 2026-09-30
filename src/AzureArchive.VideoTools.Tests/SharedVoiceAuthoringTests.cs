using Rukari.Lib;
using Rukari.Lib.Voices;
using AzureArchive.VideoTools.Core.Voices;

namespace AzureArchive.VideoTools.Tests;

internal static class SharedVoiceAuthoringTests
{
    public static void ChangedSelectionAndRevisionCannotWrite()
    {
        var backend = new Backend();
        using var session = Create(backend);
        var first = session.ReadSelection().Value!;
        backend.Document = backend.Document with { ContextId = "other-line-same-text-and-revision" };
        AssertEx.Equal(ModErrorCode.Conflict, session.Apply(new(first.Token, first.Revision, "voice-a")).Error!.Code);
        AssertEx.Equal(0, backend.Writes);
        var second = session.ReadSelection().Value!;
        backend.Document = backend.Document with { Revision = "changed" };
        AssertEx.Equal(ModErrorCode.Conflict, session.Apply(new(second.Token, second.Revision, null)).Error!.Code);
        AssertEx.Equal(0, backend.Writes);
    }

    public static void RemovedResourceAndForeignTokensCannotWrite()
    {
        var backend = new Backend();
        using var session = Create(backend);
        using var other = Create(backend);
        var first = session.ReadSelection().Value!;
        other.ReadSelection();
        AssertEx.Equal(ModErrorCode.Conflict, other.Apply(new(first.Token, first.Revision, "voice-a")).Error!.Code);
        backend.Resources.Clear();
        AssertEx.Equal(ModErrorCode.NotFound, session.Apply(new(first.Token, first.Revision, "voice-a")).Error!.Code);
        AssertEx.Equal(0, backend.Writes);
    }

    public static void SelectionEventsInvalidateEvenIdenticalReplacementRows()
    {
        var backend = new Backend();
        using var session = Create(backend);
        var first = session.ReadSelection().Value!;
        // Same managed content, revision and even a reused instance ID: a selection event
        // must still invalidate the lease before any write (including a no-op/remove).
        session.InvalidateSelection();
        AssertEx.Equal(ModErrorCode.Conflict, session.Apply(new(first.Token, first.Revision, "voice-a")).Error!.Code);
        AssertEx.Equal(0, backend.Writes);
        var refreshed = session.ReadSelection().Value!;
        AssertEx.False(first.Token == refreshed.Token);
        AssertEx.True(session.Apply(new(refreshed.Token, refreshed.Revision, "voice-a")).Success);
    }

    public static void SuccessfulEditsRotateTokensAndNoOpsDoNotWrite()
    {
        var backend = new Backend();
        using var session = Create(backend);
        var first = session.ReadSelection().Value!;
        var applied = session.Apply(new(first.Token, first.Revision, "voice-a"));
        AssertEx.True(applied.Success);
        AssertEx.True(applied.Value!.Changed);
        AssertEx.Equal(1, backend.Writes);
        AssertEx.False(first.Token == applied.Value.Selection.Token);
        AssertEx.Equal(ModErrorCode.Conflict, session.Apply(new(first.Token, first.Revision, null)).Error!.Code);
        var next = applied.Value.Selection;
        var same = session.Apply(new(next.Token, next.Revision, "voice-a"));
        AssertEx.True(same.Success);
        AssertEx.False(same.Value!.Changed);
        AssertEx.Equal(1, backend.Writes);
        var removed = session.Apply(new(next.Token, next.Revision, null));
        AssertEx.True(removed.Success);
        AssertEx.Equal<string?>(null, backend.Document.ResourceId);
        AssertEx.Equal(2, backend.Writes);
    }

    public static void WrongThreadStoppedAndFailedWritesAreContained()
    {
        var backend = new Backend();
        bool main = false;
        bool ready = true;
        using var session = new VoiceAuthoringSession(backend, () => ready, () => main);
        AssertEx.Equal(ModErrorCode.WrongThread, session.ReadSelection().Error!.Code);
        AssertEx.Equal(0, backend.Reads);
        main = true;
        var first = session.ReadSelection().Value!;
        backend.ThrowAfterWrite = true;
        AssertEx.Equal(ModErrorCode.ProviderFailed, session.Apply(new(first.Token, first.Revision, "voice-a")).Error!.Code);
        AssertEx.Equal(ModErrorCode.Conflict, session.Apply(new(first.Token, first.Revision, "voice-a")).Error!.Code);
        AssertEx.Equal(1, backend.Writes);
        ready = false;
        AssertEx.Equal(ModErrorCode.NotReady, session.ReadSelection().Error!.Code);
        ready = true;
        session.Dispose();
        AssertEx.Equal(ModErrorCode.NotReady, session.ReadCatalog().Error!.Code);
    }

    private static VoiceAuthoringSession Create(Backend backend) => new(backend, () => true, () => true);

    private sealed class Backend : IVoiceAuthoringBackend
    {
        public VoiceAuthoringDocument Document = new("line-a", "revision-a", "same dialogue", null);
        public List<VoiceResource> Resources = new() { new("voice-a", "Voice A") };
        public int Writes;
        public int Reads;
        public bool ThrowAfterWrite;
        public ModResult<IReadOnlyList<VoiceResource>> ReadCatalog() => ModResult<IReadOnlyList<VoiceResource>>.Ok(Resources);
        public ModResult<VoiceAuthoringDocument> ReadDocument()
        {
            Reads++;
            return ModResult<VoiceAuthoringDocument>.Ok(Document);
        }
        public ModResult<VoiceAuthoringDocument> Apply(VoiceAuthoringDocument expected, string? resourceId)
        {
            if (Document != expected) return ModResult<VoiceAuthoringDocument>.Fail(ModErrorCode.Conflict, "stale");
            Writes++;
            Document = Document with { Revision = "revision-" + Writes, ResourceId = resourceId };
            if (ThrowAfterWrite) throw new InvalidOperationException("readback failed after mutation");
            return ModResult<VoiceAuthoringDocument>.Ok(Document);
        }
    }
}
