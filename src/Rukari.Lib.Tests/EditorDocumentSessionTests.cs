using Rukari.Lib.Editor;

namespace Rukari.Lib.Tests;

internal static class EditorDocumentSessionTests
{
    internal static void RepeatedReadPreservesDraftUntilSelectionChanges()
    {
        var fixture = new Fixture();
        var first = fixture.Read();
        var second = fixture.Read();
        Check.Equal(first.SelectionToken, second.SelectionToken, "A harmless refresh must not invalidate an open draft.");
        Check.Equal(first.Revision, second.Revision, "Unchanged content must keep its revision.");
        Check.Equal("原台词", second.DialogueText, "The feature needs the actual line text.");
        Check.Equal("original", second.AdditionalPrompt, "The feature needs the current complete prompt.");
    }

    internal static void StaleRevisionAndForeignTokenCannotOverwriteTheLine()
    {
        var fixture = new Fixture();
        var draft = fixture.Read();
        Check.Failure(ModErrorCode.Conflict, fixture.Session.Replace(new(draft.SelectionToken, "outdated", "bad-revision")));
        Check.Failure(ModErrorCode.Conflict, fixture.Session.Replace(new("another-selection", draft.Revision, "bad-token")));
        Check.Equal(0, fixture.Backend.WriteCalls, "Conflicting drafts must be rejected before reaching the writer.");
        Check.Equal("original", fixture.Backend.Current.AdditionalPrompt, "Rejected drafts must preserve the editor.");

        fixture.Backend.Current = fixture.Backend.Current with { AdditionalPrompt = "native edit" };
        Check.Failure(ModErrorCode.Conflict, fixture.Session.Replace(Request(draft, "stale draft")));
        Check.Equal("native edit", fixture.Backend.Current.AdditionalPrompt, "An external change must win over an older draft.");
        var refreshed = fixture.Read();
        Check.True(refreshed.SelectionToken != draft.SelectionToken, "Observed edits invalidate the old draft token.");
        Check.True(refreshed.Revision != draft.Revision, "An edited prompt must carry a different revision.");
    }

    internal static void IdenticalTextOnDifferentLinesDoesNotShareSelectionIdentity()
    {
        var fixture = new Fixture();
        var lineA = fixture.Read();
        fixture.Backend.Current = fixture.Backend.Current with { ContextId = "project:line-b" };
        var lineB = fixture.Read();
        Check.Equal(lineA.Revision, lineB.Revision, "This regression requires identical prompt content.");
        Check.True(lineA.SelectionToken != lineB.SelectionToken, "Different lines must have different selection tokens.");
        Check.Failure(ModErrorCode.Conflict, fixture.Session.Replace(Request(lineA, "wrong target")));
        Check.Equal(0, fixture.Backend.WriteCalls, "Text equality must never authorize writing another line.");
    }

    internal static void ReturningToSameLineCannotReviveAnOldDraft()
    {
        var fixture = new Fixture();
        var documentA = fixture.Backend.Current;
        var oldDraft = fixture.Read();
        fixture.Backend.Current = documentA with { ContextId = "project:line-b" };
        _ = fixture.Read();
        fixture.Backend.Current = documentA;
        var returned = fixture.Read();
        Check.Equal(oldDraft.ContextId, returned.ContextId, "The user returned to the original line.");
        Check.Equal(oldDraft.Revision, returned.Revision, "The line content has not changed.");
        Check.True(oldDraft.SelectionToken != returned.SelectionToken, "A to B to A must require a fresh draft.");
        Check.Failure(ModErrorCode.Conflict, fixture.Session.Replace(Request(oldDraft, "abandoned draft")));
    }

    internal static void ExplicitSelectionInvalidationWorksWithoutPollingIntermediateLine()
    {
        var fixture = new Fixture();
        var oldDraft = fixture.Read();
        // A native selection callback can fire while the feature UI does not render line B.
        fixture.Session.InvalidateSelection();
        var returned = fixture.Read();
        Check.True(oldDraft.SelectionToken != returned.SelectionToken, "Selection events must invalidate drafts even without an intermediate read.");
        Check.Failure(ModErrorCode.Conflict, fixture.Session.Replace(Request(oldDraft, "late write")));
        Check.Equal(0, fixture.Backend.WriteCalls, "An invalidated draft must never reach the writer.");
    }

    internal static void OptionalInvalidationCapabilityRejectsAnOldDraftWithoutReadingLineB()
    {
        var fixture = new Fixture();
        IEditorDocumentService documents = fixture.Session;
        var invalidation = documents as IEditorSelectionInvalidation;
        Check.True(invalidation != null, "The existing document service must supply the optional capability.");
        var documentA = fixture.Backend.Current;
        var oldDraft = fixture.Read();
        fixture.Backend.Current = documentA with { ContextId = "project:line-b" };
        int readsBeforeInvalidation = fixture.Backend.ReadCalls;

        Check.True(Check.Success(invalidation!.InvalidateSelection()), "The ready main thread must invalidate the token.");
        Check.Equal(readsBeforeInvalidation, fixture.Backend.ReadCalls,
            "Event invalidation must not read line B or enter the native backend.");
        fixture.Backend.Current = documentA;
        var returned = fixture.Read();
        Check.Equal(oldDraft.ContextId, returned.ContextId);
        Check.Equal(oldDraft.Revision, returned.Revision);
        Check.True(oldDraft.SelectionToken != returned.SelectionToken,
            "A to B to A must reject the old draft even when B was never read.");
        Check.Failure(ModErrorCode.Conflict, documents.Replace(Request(oldDraft, "abandoned draft")));
        Check.Equal(0, fixture.Backend.WriteCalls, "The event-invalidated draft must never reach the writer.");
    }

    internal static void OptionalInvalidationGuardsPreserveTheTokenAndNeverTouchTheBackend()
    {
        var fixture = new Fixture();
        IEditorSelectionInvalidation invalidation = fixture.Session;
        var draft = fixture.Read();
        int readsBeforeInvalidation = fixture.Backend.ReadCalls;
        fixture.Ready = false;
        Check.Failure(ModErrorCode.NotReady, invalidation.InvalidateSelection());
        fixture.Ready = true;
        Check.OnWorker(() =>
        {
            Check.Failure(ModErrorCode.WrongThread, invalidation.InvalidateSelection());
            return true;
        });
        Check.Equal(readsBeforeInvalidation, fixture.Backend.ReadCalls,
            "Rejected invalidation must not read the backend.");
        Check.Equal(0, fixture.Backend.WriteCalls, "Rejected invalidation must not write the backend.");
        Check.Equal(draft.SelectionToken, fixture.Read().SelectionToken,
            "Not-ready or worker requests must not discard a valid main-thread draft.");

        fixture.Backend.OnWrite = () =>
        {
            int readsDuringWrite = fixture.Backend.ReadCalls;
            Check.Failure(ModErrorCode.Busy, invalidation.InvalidateSelection());
            Check.Equal(readsDuringWrite, fixture.Backend.ReadCalls,
                "Reentrant invalidation must not enter the backend.");
        };
        var written = fixture.Replace(draft, "outer write");
        Check.Equal("outer write", written.Selection.AdditionalPrompt,
            "Rejected reentrant invalidation must not interfere with the guarded transaction.");
        Check.Equal(1, fixture.Backend.WriteCalls);
    }

    internal static void ExternalEditPreventsSharedUndoFromOverwritingIt()
    {
        var fixture = new Fixture();
        var written = fixture.Replace(fixture.Read(), "feature directive");
        Check.True(written.Selection.CanUndo, "A confirmed feature edit should support undo.");
        fixture.Backend.Current = fixture.Backend.Current with { AdditionalPrompt = "user's later edit" };
        Check.True(!fixture.Read().CanUndo, "The shared undo is no longer valid after another edit.");
        Check.Failure(ModErrorCode.Conflict, fixture.Session.Undo());
        Check.Equal("user's later edit", fixture.Backend.Current.AdditionalPrompt, "Undo must preserve the user's later edit.");
        Check.Equal(1, fixture.Backend.WriteCalls, "Blocked undo must not invoke another write.");
    }

    internal static void TwoFeaturesShareOnlyTheLatestUndoTransaction()
    {
        var fixture = new Fixture();
        var stageDraft = fixture.Read();
        var stageEdit = fixture.Replace(stageDraft, "original\n#stage;camera");
        var voiceEdit = fixture.Replace(stageEdit.Selection, "original\n#stage;camera\n#voice;sample");
        Check.True(voiceEdit.Selection.CanUndo, "The latest voice transaction should be undoable.");
        var undone = Check.Success(fixture.Session.Undo());
        Check.Equal(stageEdit.Selection.AdditionalPrompt, undone.Selection.AdditionalPrompt,
            "Undoing the voice operation must retain the prior stage operation.");
        Check.True(undone.Changed, "The latest operation must really be reverted.");
        Check.True(!undone.Selection.CanUndo, "A consumed shared undo must not expose a second feature's private history.");
        Check.Failure(ModErrorCode.Conflict, fixture.Session.Undo());
        Check.Failure(ModErrorCode.Conflict, fixture.Session.Replace(Request(voiceEdit.Selection, "stale voice retry")));
        Check.Equal(3, fixture.Backend.WriteCalls, "Only two writes and one undo should reach the backend.");
    }

    internal static void UnchangedWriteDoesNotConsumeOrReplaceTheExistingUndo()
    {
        var fixture = new Fixture();
        var edited = fixture.Replace(fixture.Read(), "new directive");
        var repeated = fixture.Replace(edited.Selection, "new directive");
        Check.True(!repeated.Changed, "Applying identical content is a no-op.");
        Check.True(repeated.Selection.CanUndo, "A no-op must preserve the previous real transaction.");
        Check.Equal(1, fixture.Backend.WriteCalls, "A no-op must not trigger native write side effects.");
        Check.Equal("original", Check.Success(fixture.Session.Undo()).Selection.AdditionalPrompt,
            "Undo after a no-op should still restore the original prompt.");
    }

    internal static void FailedWriteDoesNotArmUndoAndRequiresAFreshDraft()
    {
        var fixture = new Fixture();
        var draft = fixture.Read();
        fixture.Backend.WriteOverride = (_, _) => ModResult<EditorBackendDocument>.Fail(ModErrorCode.Conflict, "backend lost selection");
        Check.Failure(ModErrorCode.Conflict, fixture.Session.Replace(Request(draft, "not applied")));
        Check.Equal("original", fixture.Backend.Current.AdditionalPrompt, "A failed transaction has no confirmed replacement.");
        var fresh = fixture.Read();
        Check.True(!fresh.CanUndo, "A failed attempt must not create an undo entry.");
        Check.True(fresh.SelectionToken != draft.SelectionToken, "A failed native attempt requires fresh identity observation.");
        Check.Failure(ModErrorCode.Conflict, fixture.Session.Replace(Request(draft, "unsafe retry")));
        fixture.Backend.WriteOverride = null;
        Check.True(fixture.Replace(fresh, "confirmed retry").Selection.CanUndo, "A failed attempt must not leave the session busy forever.");
    }

    internal static void IncorrectWriteReadbackIsRejectedWithoutArmingUndo()
    {
        var fixture = new Fixture();
        var draft = fixture.Read();
        fixture.Backend.WriteOverride = (expected, text) => ModResult<EditorBackendDocument>.Ok(expected with
        {
            ContextId = "another line",
            AdditionalPrompt = text
        });
        Check.Failure(ModErrorCode.ProviderFailed, fixture.Session.Replace(Request(draft, "intended text")));
        Check.True(!fixture.Read().CanUndo, "A success response for another line must not establish undo authority.");
        Check.Equal("original", fixture.Backend.Current.AdditionalPrompt, "The session must not add a compensating write to an unverified line.");
        Check.Equal(1, fixture.Backend.WriteCalls, "An incorrect readback must not trigger an unguarded second write.");
    }

    internal static void ProviderExceptionsAndUnreadableSelectionInvalidateTheDraft()
    {
        var fixture = new Fixture();
        var draft = fixture.Read();
        fixture.Backend.WriteOverride = (_, _) => throw new InvalidOperationException("write failed");
        Check.Failure(ModErrorCode.ProviderFailed, fixture.Session.Replace(Request(draft, "unconfirmed")));
        fixture.Backend.WriteOverride = null;
        fixture.Backend.ReadOverride = () => ModResult<EditorBackendDocument>.Fail(ModErrorCode.NotFound, "no synchronized line");
        Check.Failure(ModErrorCode.NotFound, fixture.Session.ReadSelection());
        fixture.Backend.ReadOverride = null;
        Check.Failure(ModErrorCode.Conflict, fixture.Session.Replace(Request(draft, "old draft")));
        Check.True(!fixture.Read().CanUndo, "Provider exceptions must not fabricate undo history.");
    }

    internal static void NotReadyAndWrongThreadNeverTouchTheBackend()
    {
        var fixture = new Fixture();
        fixture.Ready = false;
        Check.Failure(ModErrorCode.NotReady, fixture.Session.ReadSelection());
        Check.Failure(ModErrorCode.NotReady, fixture.Session.Replace(new("token", "revision", "blocked")));
        Check.Failure(ModErrorCode.NotReady, fixture.Session.Undo());
        fixture.Ready = true;
        Check.OnWorker(() =>
        {
            Check.Failure(ModErrorCode.WrongThread, fixture.Session.ReadSelection());
            Check.Failure(ModErrorCode.WrongThread, fixture.Session.Replace(new("token", "revision", "blocked")));
            Check.Failure(ModErrorCode.WrongThread, fixture.Session.Undo());
            return true;
        });
        Check.Equal(0, fixture.Backend.ReadCalls, "Rejected operations must not read live state on the wrong thread or before readiness.");
        Check.Equal(0, fixture.Backend.WriteCalls, "Rejected operations must never mutate the backend.");
        _ = fixture.Read();
        Check.Equal(1, fixture.Backend.ReadCalls, "The main thread should still work after rejected requests.");
    }

    internal static void ReentrantReadReplaceAndUndoAreBusyUntilWriteCompletes()
    {
        var fixture = new Fixture();
        var draft = fixture.Read();
        fixture.Backend.OnWrite = () =>
        {
            Check.Failure(ModErrorCode.Busy, fixture.Session.ReadSelection());
            Check.Failure(ModErrorCode.Busy, fixture.Session.Replace(Request(draft, "nested write")));
            Check.Failure(ModErrorCode.Busy, fixture.Session.Undo());
        };
        var result = fixture.Replace(draft, "outer write");
        Check.Equal("outer write", result.Selection.AdditionalPrompt, "Nested callbacks must not interfere with the outer transaction.");
        Check.Equal(1, fixture.Backend.WriteCalls, "Only the outer transaction may enter the writer.");
        fixture.Backend.OnWrite = null;
        Check.Equal("original", Check.Success(fixture.Session.Undo()).Selection.AdditionalPrompt,
            "The guard must be released after the completed outer write.");
    }

    private static EditorDocumentEditRequest Request(EditorDocumentSnapshot snapshot, string text)
        => new(snapshot.SelectionToken, snapshot.Revision, text);

    private sealed class Fixture
    {
        internal readonly FakeBackend Backend = new();
        internal readonly EditorDocumentSession Session;
        internal bool Ready = true;
        internal Fixture()
        {
            int mainThread = Environment.CurrentManagedThreadId;
            Session = new(Backend, () => Ready, () => Environment.CurrentManagedThreadId == mainThread);
        }
        internal EditorDocumentSnapshot Read() => Check.Success(Session.ReadSelection());
        internal EditorDocumentEditResult Replace(EditorDocumentSnapshot snapshot, string text)
            => Check.Success(Session.Replace(Request(snapshot, text)));
    }

    // A synchronous native-facing boundary substitute. The expected context is checked at write time,
    // while injected outcomes exercise transaction behavior without loading any Unity/IL2CPP code.
    private sealed class FakeBackend : IEditorDocumentBackend
    {
        internal EditorBackendDocument Current = new("project:line-a", "原台词", "original");
        internal int ReadCalls;
        internal int WriteCalls;
        internal Action? OnWrite;
        internal Func<ModResult<EditorBackendDocument>>? ReadOverride;
        internal Func<EditorBackendDocument, string, ModResult<EditorBackendDocument>>? WriteOverride;

        public ModResult<EditorBackendDocument> Read()
        {
            ReadCalls++;
            return ReadOverride?.Invoke() ?? ModResult<EditorBackendDocument>.Ok(Current);
        }

        public ModResult<EditorBackendDocument> Write(EditorBackendDocument expected, string text)
        {
            WriteCalls++;
            OnWrite?.Invoke();
            if (WriteOverride != null) return WriteOverride(expected, text);
            if (Current != expected)
                return ModResult<EditorBackendDocument>.Fail(ModErrorCode.Conflict, "The backend selection changed before write.");
            Current = Current with { AdditionalPrompt = text };
            return ModResult<EditorBackendDocument>.Ok(Current);
        }
    }
}
