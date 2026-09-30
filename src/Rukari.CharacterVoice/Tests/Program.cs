using Rukari.CharacterVoice.Core;
using Rukari.Lib;
using Rukari.Lib.Voices;

// A valid owned voice identifier: the gate refuses to open for anything that does not decode.
const string OwnedVoice = "aavt/voice/rukari-import/0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

var checks = new (string Name, Action Run)[]
{
    (nameof(VoicePublicationTests.CompilationIdentityRejectsPreviewAndSameNameDifferentProjects), VoicePublicationTests.CompilationIdentityRejectsPreviewAndSameNameDifferentProjects),
    (nameof(VoicePublicationTests.PublishesCompanionWithoutChangingOfficialFiles), VoicePublicationTests.PublishesCompanionWithoutChangingOfficialFiles),
    (nameof(VoicePublicationTests.RelocatedCompanionWorksWithoutEditorRootsAndIgnoresZipTimestampRounding), VoicePublicationTests.RelocatedCompanionWorksWithoutEditorRootsAndIgnoresZipTimestampRounding),
    (nameof(VoicePublicationTests.RestoredNativeEntriesAreExportedIntoOwnedStorage), VoicePublicationTests.RestoredNativeEntriesAreExportedIntoOwnedStorage),
    (nameof(VoicePublicationTests.FailedBatchPreservesPreviousPublicationAndForeignFiles), VoicePublicationTests.FailedBatchPreservesPreviousPublicationAndForeignFiles),
    (nameof(VoicePublicationTests.RepeatedPublishSkipsUnchangedBytesButDetectsChangedAudio), VoicePublicationTests.RepeatedPublishSkipsUnchangedBytesButDetectsChangedAudio),
    (nameof(VoicePublicationTests.InvalidArchiveNamePathsAndCancellationDoNotPublish), VoicePublicationTests.InvalidArchiveNamePathsAndCancellationDoNotPublish),
    (nameof(VoicePublicationTests.ConcurrentImportRefusesPublicationWithoutBlocking), VoicePublicationTests.ConcurrentImportRefusesPublicationWithoutBlocking),
    (nameof(AuthoringCompileCompatibilityTests.LegacySessionStillCompiles), AuthoringCompileCompatibilityTests.LegacySessionStillCompiles),
    (nameof(AuthoringCompileCompatibilityTests.CurrentSessionNeverPublishesDuringDiagnosticCompile), AuthoringCompileCompatibilityTests.CurrentSessionNeverPublishesDuringDiagnosticCompile),
    (nameof(AuthoringCompileCompatibilityTests.UnknownSignaturesAreRejected), AuthoringCompileCompatibilityTests.UnknownSignaturesAreRejected),
    (nameof(VoicePlaybackDelayTests.ZeroReleasesImmediatelyWithoutAnotherFrame), VoicePlaybackDelayTests.ZeroReleasesImmediatelyWithoutAnotherFrame),
    (nameof(VoicePlaybackDelayTests.NativeWaitCannotExitBeforeTheDelayEvenWithoutUpdate), VoicePlaybackDelayTests.NativeWaitCannotExitBeforeTheDelayEvenWithoutUpdate),
    (nameof(VoicePlaybackDelayTests.TrailingSilenceStillBelongsToTheClipAndPollingDoesNotRestartDelay), VoicePlaybackDelayTests.TrailingSilenceStillBelongsToTheClipAndPollingDoesNotRestartDelay),
    (nameof(VoicePlaybackDelayTests.ManagedTickCanFinishDelayWithoutTouchingTheAudioEngine), VoicePlaybackDelayTests.ManagedTickCanFinishDelayWithoutTouchingTheAudioEngine),
    (nameof(VoicePlaybackDelayTests.ReplacementCancelAndFailureNeverWaitForAnOldTail), VoicePlaybackDelayTests.ReplacementCancelAndFailureNeverWaitForAnOldTail),
    (nameof(VoicePlaybackDelayTests.DelayIsBoundedAndOldBeginContractStillMeansZero), VoicePlaybackDelayTests.DelayIsBoundedAndOldBeginContractStillMeansZero),
    (nameof(VoicePlaybackDelayTests.SliderCapturesOutsideDragClampsAndSavesOnlyOnRelease), VoicePlaybackDelayTests.SliderCapturesOutsideDragClampsAndSavesOnlyOnRelease),
    (nameof(VoicePlaybackDelayTests.SettingsUseTheSharedLayoutAndDoNotWriteOnOpenOrUnrelatedClicks), VoicePlaybackDelayTests.SettingsUseTheSharedLayoutAndDoNotWriteOnOpenOrUnrelatedClicks),
    (nameof(ProjectVoiceImportTests.RunAll), ProjectVoiceImportTests.RunAll),
    (nameof(ProjectVoiceImportTests.ImportedVoiceResolvesAcrossKnownProjectRoots), ProjectVoiceImportTests.ImportedVoiceResolvesAcrossKnownProjectRoots),
    (nameof(VoiceOverrideCleanupTests.RunAll), VoiceOverrideCleanupTests.RunAll),
    (nameof(VoiceCatalogKeyCandidatesTests.RunAll), VoiceCatalogKeyCandidatesTests.RunAll),
    ("PlayingWaitHasItsOwnDeadlineSoTheDialogueCannotBlockForever", () =>
    {
        // Dead end 1: the Playing phase blocks advance, but the Loading/Starting timeout never covered it,
        // so a clip that never reports as finished left the dialogue permanently blocked.
        var gate = new VoicePlaybackGate();
        long request = gate.Begin(OwnedVoice, 0, loadingTimeout: 10);
        True(gate.MarkStarting(request, 1, startTimeout: 1));
        True(gate.ObservePlayback(request, nativePlaying: true, now: 2, playbackTimeout: 300));
        Equal(VoicePlaybackPhase.Playing, gate.Phase);
        True(gate.BlocksAdvance(false, false));
        True(!gate.Expire(301.9), "The playback deadline must not fire early.");
        True(gate.Expire(302), "An owned voice that never finishes must still release the wait.");
        Equal(VoicePlaybackPhase.Failed, gate.Phase);
        Equal("playback-timeout", gate.LastReason);
        True(!gate.BlocksAdvance(false, false), "A released wait must stop blocking dialogue advance.");
    }),
    ("LoadingTimeoutStillReleasesItsOwnDeadline", () =>
    {
        var gate = new VoicePlaybackGate();
        True(!gate.Expire(0));
        gate.Begin(OwnedVoice, 0, loadingTimeout: 10);
        True(!gate.Expire(9.9));
        True(gate.Expire(10));
        Equal("loading-timeout", gate.LastReason);
        Equal(VoicePlaybackPhase.Failed, gate.Phase);
    }),
    ("StartingWithoutPlaybackCannotOutliveItsOwnTimeout", () =>
    {
        // The second dead end was that a suspended poller stopped calling Expire at all. From the state side
        // the wait must therefore be self-bounding, independent of who polls it.
        var gate = new VoicePlaybackGate();
        long request = gate.Begin(OwnedVoice, 0, loadingTimeout: 10);
        True(gate.MarkStarting(request, 1, startTimeout: 1));
        True(gate.HoldNativeVoiceWait, "Starting still holds the native predicate, so it needs its timeout.");
        True(!gate.Expire(1.9));
        True(gate.Expire(2));
        Equal("playback-did-not-start", gate.LastReason);
        Equal(VoicePlaybackPhase.Failed, gate.Phase);
    }),
    ("NormalPlaybackCompletionIsNotPunishedByTheSafetyDeadline", () =>
    {
        var gate = new VoicePlaybackGate();
        long request = gate.Begin(OwnedVoice, 0, loadingTimeout: 10);
        True(gate.MarkStarting(request, 1, startTimeout: 1));
        True(gate.ObservePlayback(request, true, now: 2, playbackTimeout: 300));
        // A clip that finishes normally must complete well before the deadline and never report a timeout.
        True(gate.ObservePlayback(request, nativePlaying: false, now: 5, playbackTimeout: 300));
        Equal(VoicePlaybackPhase.Completed, gate.Phase);
        Equal("audio-finished", gate.LastReason);
        True(!gate.Expire(1000), "A completed request must never be failed by a stale deadline.");
    }),
    ("CanceledWaitIsNeverResurrectedByADeadline", () =>
    {
        var gate = new VoicePlaybackGate();
        long request = gate.Begin(OwnedVoice, 0, loadingTimeout: 10);
        gate.Cancel("test-cancel");
        True(!gate.Active);
        True(!gate.Expire(1000), "A canceled wait must not become active again.");
        True(!gate.Fail(request, "late"), "A stale sequence must not fail the canceled gate.");
    }),
    ("ImportedBindingRemainsSelectableBeyondNativeCatalogLimit", () =>
    {
        string imported = "rukari-import/" + new string('a', 64);
        string[] keys = Enumerable.Range(0, ImportedVoiceCatalogPolicy.MaximumOverridePaths)
            .Select(index => "native/" + index).Append(imported).ToArray();
        Equal(imported, ImportedVoiceCatalogPolicy.ResolveBindingKey(imported, keys));
    }),
    (nameof(VoiceFolderTests.PickerCancellationCompletesWhileNativeWorkerIsStillOpening), VoiceFolderTests.PickerCancellationCompletesWhileNativeWorkerIsStillOpening),
    (nameof(VoiceFolderTests.NativeFolderWorkerUsesStaAndPropagatesFailures), VoiceFolderTests.NativeFolderWorkerUsesStaAndPropagatesFailures),
    (nameof(VoiceFolderTests.NativeFolderComSetupWorksWithoutOpeningAWindow), VoiceFolderTests.NativeFolderComSetupWorksWithoutOpeningAWindow),
    (nameof(VoiceFolderTests.ClearAndDisposeDiscardAnAlreadyRunningScannersLateResult), VoiceFolderTests.ClearAndDisposeDiscardAnAlreadyRunningScannersLateResult),
    (nameof(VoiceFolderTests.ScannerRejectsLinkedAncestorsAndSkipsLinkedChildrenWhenSupported), VoiceFolderTests.ScannerRejectsLinkedAncestorsAndSkipsLinkedChildrenWhenSupported),
    (nameof(VoiceToolPageTests.HiddenIdlePageDoesNotPollNativeProjectStateEveryFrame), VoiceToolPageTests.HiddenIdlePageDoesNotPollNativeProjectStateEveryFrame),
    (nameof(VoiceToolPageTests.ProjectSwitchCancelsFolderImportWithoutApplyingToAnotherLine), VoiceToolPageTests.ProjectSwitchCancelsFolderImportWithoutApplyingToAnotherLine),
    (nameof(VoiceToolPageTests.SuccessfulFolderImportRetainsExistingSoundsAndRequiresExplicitBinding), VoiceToolPageTests.SuccessfulFolderImportRetainsExistingSoundsAndRequiresExplicitBinding),
    (nameof(VoiceToolPageTests.DisposedVoicePageCancelsWorkersAndIgnoresTheirResults), VoiceToolPageTests.DisposedVoicePageCancelsWorkersAndIgnoresTheirResults),
    (nameof(VoiceFolderTests.FolderScanAcceptsSupportedFormatsAndKeepsDuplicateNamesDistinct), VoiceFolderTests.FolderScanAcceptsSupportedFormatsAndKeepsDuplicateNamesDistinct),
    (nameof(VoiceFolderTests.ScanCancellationAndLimitsNeverPublishAPartialFolder), VoiceFolderTests.ScanCancellationAndLimitsNeverPublishAPartialFolder),
    (nameof(VoiceFolderTests.CancelledOrFailedFolderChoicePreservesThePreviousList), VoiceFolderTests.CancelledOrFailedFolderChoicePreservesThePreviousList),
    (nameof(VoiceFolderTests.FolderChoiceRunsOffTheCallerThreadAndAllowsCancellation), VoiceFolderTests.FolderChoiceRunsOffTheCallerThreadAndAllowsCancellation),
    (nameof(VoiceFolderTests.UnloadDiscardsLateFolderResults), VoiceFolderTests.UnloadDiscardsLateFolderResults),
    ("ReplacePreservesOtherModulesExactly", () =>
    {
        const string source = "#aavt;char;1;move;10\r\n  #aavt;voice;old\r\n#aavt;continue\r\n#st;原文\n";
        Equal("#aavt;char;1;move;10\r\n#aavt;voice;new\r\n#aavt;continue\r\n#st;原文\n", VoicePromptEditor.SetBinding(source, "new").Value);
    }),
    ("RemovalPreservesUnrelatedUnknownInstructions", () =>
    {
        const string source = "#future;opaque\n#aavt;voice;old\n#aavt;camera;opaque";
        Equal("#future;opaque\n#aavt;camera;opaque", VoicePromptEditor.SetBinding(source, null).Value);
    }),
    ("ExtensionLikeStemsRoundTrip", () =>
    {
        var result = VoicePromptEditor.SetBinding(string.Empty, "dialogue/hello.ogg");
        True(result.Success);
        Equal("dialogue/hello.ogg", VoiceDirectivePolicy.Parse(result.Value).ResourceKey);
    }),
    ("DuplicateVoiceCannotSilentlyOverwrite", () =>
        Equal(ModErrorCode.InvalidArgument, VoicePromptEditor.SetBinding("#aavt;voice;a\n#aavt;voice;b", "new").Error?.Code)),
    ("RemovalWithoutVoiceIsNoOp", () =>
    {
        const string source = "raw\r\n#aavt;continue\r\n";
        Equal(source, VoicePromptEditor.SetBinding(source, null).Value);
    }),
    ("SanitizerIsIdempotentAndVoiceOnly", () =>
    {
        const string input = "#aavt;char;1;move;10\r\n#aavt;voice;bad;extra\n#aavt;continue\r\n#aavt;camera;opaque\n";
        const string expected = "#aavt;char;1;move;10\r\n#aavt;continue\r\n#aavt;camera;opaque\n";
        string once = VoicePromptEditor.SanitizeForNative(input);
        Equal(expected, once);
        Equal(expected, VoicePromptEditor.SanitizeForNative(once));
    }),
    ("SanitizerDoesNotStripOtherCommandNames", () =>
    {
        const string source = "#aavt;voices;data\n#aavt;voiceover;data\ntext #aavt;voice;a";
        Equal(source, VoicePromptEditor.SanitizeForNative(source));
    }),
    ("SharedSelectionTokenPreventsSameLineABA", () =>
    {
        var backend = new Backend();
        using var session = Session(backend);
        VoiceSelection selected = session.ReadSelection().Value;
        backend.Document = backend.Document with { EditorSelectionToken = "editor-token-after-leaving-and-returning" };
        Equal(ModErrorCode.Conflict, session.Apply(new(selected.Token, selected.Revision, "hello")).Error?.Code);
        Equal(0, backend.Writes);
    }),
    ("ApplyAcceptsNewSharedTokenAfterSameLineWrite", () =>
    {
        var backend = new Backend();
        using var session = Session(backend);
        VoiceSelection selected = session.ReadSelection().Value;
        var applied = session.Apply(new(selected.Token, selected.Revision, "hello"));
        True(applied.Success);
        Equal("hello", applied.Value.Selection.ResourceId);
        Equal(1, backend.Writes);
    }),
    ("FailedWriteInvalidatesOldVoiceSelection", () =>
    {
        var backend = new Backend { FailWrite = true };
        using var session = Session(backend);
        VoiceSelection selected = session.ReadSelection().Value;
        True(!session.Apply(new(selected.Token, selected.Revision, "hello")).Success);
        backend.FailWrite = false;
        Equal(ModErrorCode.Conflict, session.Apply(new(selected.Token, selected.Revision, "hello")).Error?.Code);
        Equal(1, backend.Writes);
    }),
};
int failed = 0;
foreach (var check in checks)
{
    try { check.Run(); Console.WriteLine("PASS " + check.Name); }
    catch (Exception error) { failed++; Console.Error.WriteLine("FAIL " + check.Name + ": " + error); }
}
Console.WriteLine($"{checks.Length - failed}/{checks.Length} independent voice tests passed.");
return failed == 0 ? 0 : 1;

static void True(bool value, string? message = null) { if (!value) throw new InvalidOperationException(message ?? "Expected true."); }
static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}, actual {actual}.");
}
static VoiceAuthoringSession Session(Backend backend) => new(backend, () => true, () => true);

internal sealed class Backend : IVoiceAuthoringBackend
{
    internal VoiceAuthoringDocument Document = new("line-a", "r1", "台词", null, "editor-token-a");
    internal int Writes;
    internal bool FailWrite;
    public ModResult<IReadOnlyList<VoiceResource>> ReadCatalog() => ModResult<IReadOnlyList<VoiceResource>>.Ok(new[] { new VoiceResource("hello", "声音") });
    public ModResult<VoiceAuthoringDocument> ReadDocument() => ModResult<VoiceAuthoringDocument>.Ok(Document);
    public ModResult<VoiceAuthoringDocument> Apply(VoiceAuthoringDocument expected, string? resourceId)
    {
        Writes++;
        if (FailWrite) return ModResult<VoiceAuthoringDocument>.Fail(ModErrorCode.ProviderFailed, "Simulated failed write.");
        Document = Document with { ResourceId = resourceId, Revision = "r" + (Writes + 1), EditorSelectionToken = "editor-write-" + Writes };
        return ModResult<VoiceAuthoringDocument>.Ok(Document);
    }
}
