namespace AzureArchive.VideoTools.Tests;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 2
            && string.Equals(args[0], "--aas-tail", StringComparison.Ordinal))
        {
            return AasTailInspector.Inspect(args[1]);
        }

        if (args.Length == 2
            && string.Equals(args[0], "--aap-character-positions", StringComparison.Ordinal))
        {
            return AapCharacterPositionInspector.Inspect(args[1]);
        }

        if (args.Length == 6
            && string.Equals(args[0], "--resolve-scene", StringComparison.Ordinal))
        {
            return SceneIdentitySmokeVerifier.Verify(
                args[1],
                args[2],
                args[3],
                args[4],
                args[5]);
        }

        if (args.Length == 6
            && string.Equals(
                args[0],
                "--write-command-proof-workspace",
                StringComparison.Ordinal))
        {
            return CommandWorkspaceProofWriter.Write(
                args[1],
                args[2],
                args[3],
                args[4],
                args[5]);
        }

        (string Name, Action Run)[] tests =
        {
            (nameof(EditorInputSceneCaptureProofTests.UniqueSynchronizedInputsSelectTheActualRowContext), EditorInputSceneCaptureProofTests.UniqueSynchronizedInputsSelectTheActualRowContext),
            (nameof(EditorInputSceneCaptureProofTests.AnEstablishedOrAmbiguousSelectionCannotUseTheFallback), EditorInputSceneCaptureProofTests.AnEstablishedOrAmbiguousSelectionCannotUseTheFallback),
            (nameof(EditorInputSceneCaptureProofTests.EveryActiveRowNeedsValidOwnershipAndScriptMembership), EditorInputSceneCaptureProofTests.EveryActiveRowNeedsValidOwnershipAndScriptMembership),
            (nameof(EditorInputSceneCaptureProofTests.DuplicateActualIndicesAreRejectedEvenWithOneMatchingInput), EditorInputSceneCaptureProofTests.DuplicateActualIndicesAreRejectedEvenWithOneMatchingInput),
            (nameof(EditorInputSceneCaptureProofTests.DialogueAndPromptMatchingAreExactAndOrdinal), EditorInputSceneCaptureProofTests.DialogueAndPromptMatchingAreExactAndOrdinal),
            (nameof(EditorInputSceneCaptureProofTests.MultipleCompleteInputMatchesRemainAmbiguousIncludingEmptyRows), EditorInputSceneCaptureProofTests.MultipleCompleteInputMatchesRemainAmbiguousIncludingEmptyRows),
            (nameof(EditorInputSceneCaptureProofTests.HiddenRowsCannotSupplyOrAmbiguateTheVisibleMatch), EditorInputSceneCaptureProofTests.HiddenRowsCannotSupplyOrAmbiguateTheVisibleMatch),
            (nameof(EditorInputSceneCaptureProofTests.AnyFramePointerOrProjectChangeRejectsTheCapture), EditorInputSceneCaptureProofTests.AnyFramePointerOrProjectChangeRejectsTheCapture),
            (nameof(EditorInputSceneCaptureProofTests.InputsOrRowsChangingDuringTheReadCannotBecomeASnapshot), EditorInputSceneCaptureProofTests.InputsOrRowsChangingDuringTheReadCannotBecomeASnapshot),
            (nameof(EditorInputSceneCaptureProofTests.MissingInputsContextOrRearrangingCacheCannotCapture), EditorInputSceneCaptureProofTests.MissingInputsContextOrRearrangingCacheCannotCapture),
            (nameof(EditorInputSceneCaptureProofTests.SharedConfirmationRequiresTheCapturedContextAndBothCompleteTexts), EditorInputSceneCaptureProofTests.SharedConfirmationRequiresTheCapturedContextAndBothCompleteTexts),
            (nameof(EditorInputSceneCaptureProofTests.MissingSharedSnapshotNeverConfirmsAProvisionalRow), EditorInputSceneCaptureProofTests.MissingSharedSnapshotNeverConfirmsAProvisionalRow),
            (nameof(VisualEditorSlotPreferenceTests.AReadGapPreservesOnlyTheSameLinesUiTarget), VisualEditorSlotPreferenceTests.AReadGapPreservesOnlyTheSameLinesUiTarget),
            (nameof(VisualEditorSlotPreferenceTests.AnotherLineDropsThePreviousTargetIncludingOnReturn), VisualEditorSlotPreferenceTests.AnotherLineDropsThePreviousTargetIncludingOnReturn),
            (nameof(VisualEditorSlotPreferenceTests.InvalidTargetsCannotReplaceAValidPreference), VisualEditorSlotPreferenceTests.InvalidTargetsCannotReplaceAValidPreference),
            (nameof(CharacterPresetBezierTests.BezierSolvesTimeCoordinateInsteadOfUsingTimeAsTheParameter), CharacterPresetBezierTests.BezierSolvesTimeCoordinateInsteadOfUsingTimeAsTheParameter),
            (nameof(CharacterPresetBezierTests.BezierHandlesVerticalTangentsReversedHandlesAndExactEndpoints), CharacterPresetBezierTests.BezierHandlesVerticalTangentsReversedHandlesAndExactEndpoints),
            (nameof(CharacterPresetBezierTests.BezierNamedPresetsHaveExpectedTimingAndSymmetry), CharacterPresetBezierTests.BezierNamedPresetsHaveExpectedTimingAndSymmetry),
            (nameof(CharacterPresetBezierTests.BezierRejectsInvalidCoordinatesAndFailsClosedForNonFiniteTime), CharacterPresetBezierTests.BezierRejectsInvalidCoordinatesAndFailsClosedForNonFiniteTime),
            (nameof(CharacterPresetBezierTests.BezierDirectiveRoundTripsAllKindsAndInvariantFloatPrecision), CharacterPresetBezierTests.BezierDirectiveRoundTripsAllKindsAndInvariantFloatPrecision),
            (nameof(CharacterPresetBezierTests.BezierMalformedDirectiveNeverPassesParserCompilerOrExtraction), CharacterPresetBezierTests.BezierMalformedDirectiveNeverPassesParserCompilerOrExtraction),
            (nameof(CharacterPresetBezierTests.OmittedBezierPreservesLegacyCanonicalStringsAndDefaultMotion), CharacterPresetBezierTests.OmittedBezierPreservesLegacyCanonicalStringsAndDefaultMotion),
            (nameof(CharacterPresetBezierTests.ExplicitLinearPreservesCyclicMotionButCanChangeHeadbuttTiming), CharacterPresetBezierTests.ExplicitLinearPreservesCyclicMotionButCanChangeHeadbuttTiming),
            (nameof(CharacterPresetBezierTests.BezierSpinWarpsEveryYawTurnWithoutLosingWholeRevolutions), CharacterPresetBezierTests.BezierSpinWarpsEveryYawTurnWithoutLosingWholeRevolutions),
            (nameof(CharacterPresetBezierTests.BezierHeadbuttPreservesAllThreeStageBoundariesAndDirection), CharacterPresetBezierTests.BezierHeadbuttPreservesAllThreeStageBoundariesAndDirection),
            (nameof(CharacterPresetBezierTests.BezierOscillationKeepsAmplitudeAreaAndCycleDuration), CharacterPresetBezierTests.BezierOscillationKeepsAmplitudeAreaAndCycleDuration),
            (nameof(CharacterPresetBezierTests.BezierOscillationStillEntersAndLeavesAtRest), CharacterPresetBezierTests.BezierOscillationStillEntersAndLeavesAtRest),
            (nameof(CharacterPresetBezierTests.BezierInfinitePresetsRemainPeriodicAndFiniteAtHugeTimes), CharacterPresetBezierTests.BezierInfinitePresetsRemainPeriodicAndFiniteAtHugeTimes),
            (nameof(CharacterPresetBezierTests.BezierSurvivesEditorCompileProjectionSerializationAndPlaybackBinding), CharacterPresetBezierTests.BezierSurvivesEditorCompileProjectionSerializationAndPlaybackBinding),
            (nameof(SharedDirectiveCompilationTests.ForeignChildRoutesNeverExecuteThroughTheAavtParent), SharedDirectiveCompilationTests.ForeignChildRoutesNeverExecuteThroughTheAavtParent),
            (nameof(SharedDirectiveCompilationTests.ForeignReservedRoutesBlockEditorWritesButIndependentRoutesDoNot), SharedDirectiveCompilationTests.ForeignReservedRoutesBlockEditorWritesButIndependentRoutesDoNot),
            (nameof(EmbeddedAavtDirectiveTests.ForeignChildRoutesAreExcludedFromSavedCommandsAndPreviewChains), EmbeddedAavtDirectiveTests.ForeignChildRoutesAreExcludedFromSavedCommandsAndPreviewChains),
            (nameof(SharedDirectiveCompilationTests.SharedRouteWhitespaceAndMissingArgumentsCannotDisappearSilently), SharedDirectiveCompilationTests.SharedRouteWhitespaceAndMissingArgumentsCannotDisappearSilently),
            (nameof(SharedDirectiveCompilationTests.MixedNamespacesShareFinalIdentityWithoutChangingOfficialBytes), SharedDirectiveCompilationTests.MixedNamespacesShareFinalIdentityWithoutChangingOfficialBytes),
            (nameof(SharedDirectiveCompilationTests.RegisteredAavtFilteringPreservesUnregisteredFlBytes), SharedDirectiveCompilationTests.RegisteredAavtFilteringPreservesUnregisteredFlBytes),
            (nameof(SharedDirectiveCompilationTests.MixedFlCapturesReplaceOwnCanonicalCommandsAndPermitDeletion), SharedDirectiveCompilationTests.MixedFlCapturesReplaceOwnCanonicalCommandsAndPermitDeletion),
            (nameof(SharedDirectiveCompilationTests.ForeignFlArgumentsChangeCompiledIdentityButKeepOwnCanonicalCommands), SharedDirectiveCompilationTests.ForeignFlArgumentsChangeCompiledIdentityButKeepOwnCanonicalCommands),
            (nameof(SharedDirectiveCompilationTests.NestedCleanReturnCannotDeleteOwnCaptureButNextCompileCan), SharedDirectiveCompilationTests.NestedCleanReturnCannotDeleteOwnCaptureButNextCompileCan),
            (nameof(SharedDirectiveCompilationTests.ForeignOnlyNestedCaptureDoesNotSuppressAnAavtDeletion), SharedDirectiveCompilationTests.ForeignOnlyNestedCaptureDoesNotSuppressAnAavtDeletion),
            (nameof(SharedDirectiveCompilationTests.VoiceOnlyChildRouteStillGivesEffectsAnExplicitTombstone), SharedDirectiveCompilationTests.VoiceOnlyChildRouteStillGivesEffectsAnExplicitTombstone),
            (nameof(SharedDirectiveCompilationTests.VoiceCanFilterIndependentlyWithoutClaimingEffectsOrForeignLines), SharedDirectiveCompilationTests.VoiceCanFilterIndependentlyWithoutClaimingEffectsOrForeignLines),
            (nameof(PlaybackScriptIndexTests.SharedDirectiveSnapshotsSeparateProjectionIndexes), PlaybackScriptIndexTests.SharedDirectiveSnapshotsSeparateProjectionIndexes),
            (nameof(SharedVoiceAuthoringTests.ChangedSelectionAndRevisionCannotWrite), SharedVoiceAuthoringTests.ChangedSelectionAndRevisionCannotWrite),
            (nameof(SharedVoiceAuthoringTests.SelectionEventsInvalidateEvenIdenticalReplacementRows), SharedVoiceAuthoringTests.SelectionEventsInvalidateEvenIdenticalReplacementRows),
            (nameof(SharedVoiceAuthoringTests.RemovedResourceAndForeignTokensCannotWrite), SharedVoiceAuthoringTests.RemovedResourceAndForeignTokensCannotWrite),
            (nameof(SharedVoiceAuthoringTests.SuccessfulEditsRotateTokensAndNoOpsDoNotWrite), SharedVoiceAuthoringTests.SuccessfulEditsRotateTokensAndNoOpsDoNotWrite),
            (nameof(SharedVoiceAuthoringTests.WrongThreadStoppedAndFailedWritesAreContained), SharedVoiceAuthoringTests.WrongThreadStoppedAndFailedWritesAreContained),
            (nameof(VoiceAuthoringTests.AddsCanonicalVoiceAndReportsDocumentRevisions), VoiceAuthoringTests.AddsCanonicalVoiceAndReportsDocumentRevisions),
            (nameof(VoiceAuthoringTests.ReplacesOnlyVoiceLineAndPreservesMixedNewlines), VoiceAuthoringTests.ReplacesOnlyVoiceLineAndPreservesMixedNewlines),
            (nameof(VoiceAuthoringTests.RemovesOnlyVoiceLineIncludingItsOwnTerminator), VoiceAuthoringTests.RemovesOnlyVoiceLineIncludingItsOwnTerminator),
            (nameof(VoiceAuthoringTests.RepeatedVoiceWritesAndAbsentClearsAreIdempotent), VoiceAuthoringTests.RepeatedVoiceWritesAndAbsentClearsAreIdempotent),
            (nameof(VoiceAuthoringTests.AppendingVoiceRespectsExistingDocumentTermination), VoiceAuthoringTests.AppendingVoiceRespectsExistingDocumentTermination),
            (nameof(VoiceAuthoringTests.StaleRevisionsRejectAddReplaceRemoveAndNoOp), VoiceAuthoringTests.StaleRevisionsRejectAddReplaceRemoveAndNoOp),
            (nameof(VoiceAuthoringTests.InvalidSourceDocumentsCannotBeSilentlyRepaired), VoiceAuthoringTests.InvalidSourceDocumentsCannotBeSilentlyRepaired),
            (nameof(VoiceAuthoringTests.CatalogKeysCannotInjectLinesOrEscapeResourceNamespace), VoiceAuthoringTests.CatalogKeysCannotInjectLinesOrEscapeResourceNamespace),
            (nameof(VoiceAuthoringTests.CatalogStemsEndingInAudioExtensionsRoundTripWithoutBeingStripped), VoiceAuthoringTests.CatalogStemsEndingInAudioExtensionsRoundTripWithoutBeingStripped),
            (nameof(VoiceAuthoringTests.VoiceEditsHonorLegacyAliasValidationSetting), VoiceAuthoringTests.VoiceEditsHonorLegacyAliasValidationSetting),
            (nameof(ImportedVoiceCatalogTests.SameFilenameInDifferentDirectoriesGetsDistinctKeysAndLabels), ImportedVoiceCatalogTests.SameFilenameInDifferentDirectoriesGetsDistinctKeysAndLabels),
            (nameof(ImportedVoiceCatalogTests.AmbiguousNativeSuffixesAndFormatsAreExcluded), ImportedVoiceCatalogTests.AmbiguousNativeSuffixesAndFormatsAreExcluded),
            (nameof(ImportedVoiceCatalogTests.CanonicalKeysKeepExtensionLikeStemsAndSkipInvalidPaths), ImportedVoiceCatalogTests.CanonicalKeysKeepExtensionLikeStemsAndSkipInvalidPaths),
            (nameof(ImportedVoiceCatalogTests.DuplicatePhysicalPathsAreDeduplicatedAndEntriesAreSorted), ImportedVoiceCatalogTests.DuplicatePhysicalPathsAreDeduplicatedAndEntriesAreSorted),
            (nameof(ImportedVoiceCatalogTests.ShortBindingKeysResolveOnlyUniqueSegmentSuffixes), ImportedVoiceCatalogTests.ShortBindingKeysResolveOnlyUniqueSegmentSuffixes),
            (nameof(ImportedVoiceCatalogTests.ExactBindingKeyTakesPriorityAndRepeatedCatalogKeysAreNotAmbiguous), ImportedVoiceCatalogTests.ExactBindingKeyTakesPriorityAndRepeatedCatalogKeysAreNotAmbiguous),
            (nameof(VoicePlaybackGateTests.LoadingHoldsNativeWaitAndOrdinaryAdvance), VoicePlaybackGateTests.LoadingHoldsNativeWaitAndOrdinaryAdvance),
            (nameof(VoicePlaybackGateTests.AudioMustStartBeforeFalseCanMeanFinished), VoicePlaybackGateTests.AudioMustStartBeforeFalseCanMeanFinished),
            (nameof(VoicePlaybackGateTests.CompletedAudioReleasesBothWaits), VoicePlaybackGateTests.CompletedAudioReleasesBothWaits),
            (nameof(VoicePlaybackGateTests.LateLoadAndPlaybackCannotReviveReplacedRequest), VoicePlaybackGateTests.LateLoadAndPlaybackCannotReviveReplacedRequest),
            (nameof(VoicePlaybackGateTests.CancelInvalidatesPendingLoadEvenWhenSameClipIsReused), VoicePlaybackGateTests.CancelInvalidatesPendingLoadEvenWhenSameClipIsReused),
            (nameof(VoicePlaybackGateTests.LoadingFailureCannotLeaveDialoguePermanentlyBlocked), VoicePlaybackGateTests.LoadingFailureCannotLeaveDialoguePermanentlyBlocked),
            (nameof(VoicePlaybackGateTests.LoadedAudioThatNeverStartsAlsoReleasesWait), VoicePlaybackGateTests.LoadedAudioThatNeverStartsAlsoReleasesWait),
            (nameof(VoicePlaybackGateTests.NativeVoiceIdentifiersCannotAcquireTheModGate), VoicePlaybackGateTests.NativeVoiceIdentifiersCannotAcquireTheModGate),
            (nameof(VoiceDirectiveTests.MixedInstructionsOnlyRecognizeVoiceNamespace), VoiceDirectiveTests.MixedInstructionsOnlyRecognizeVoiceNamespace),
            (nameof(VoiceDirectiveTests.ResourceKeysNormalizeWithoutLosingCaseOrOtherDots), VoiceDirectiveTests.ResourceKeysNormalizeWithoutLosingCaseOrOtherDots),
            (nameof(VoiceDirectiveTests.InvalidResourceKeysNeverProduceBindings), VoiceDirectiveTests.InvalidResourceKeysNeverProduceBindings),
            (nameof(VoiceDirectiveTests.DuplicateAndWrongArityDirectivesAreErrors), VoiceDirectiveTests.DuplicateAndWrongArityDirectivesAreErrors),
            (nameof(VoiceDirectiveTests.NativeAliasesRequireOwnedCanonicalRelativeKeys), VoiceDirectiveTests.NativeAliasesRequireOwnedCanonicalRelativeKeys),
            (nameof(VoiceDirectiveTests.RemovingOrInvalidatingDirectiveClearsOnlyOwnedBinding), VoiceDirectiveTests.RemovingOrInvalidatingDirectiveClearsOnlyOwnedBinding),
            (nameof(VoiceDirectiveTests.BindingCanBeAddedReplacedAndReapplied), VoiceDirectiveTests.BindingCanBeAddedReplacedAndReapplied),
            (nameof(VoiceDirectiveTests.NativeVoiceBindingsAreNeverOverwritten), VoiceDirectiveTests.NativeVoiceBindingsAreNeverOverwritten),
            (nameof(VoiceDirectiveTests.PlaceholderIdentifiersCanBeBoundWithoutAssumingGuidSemantics), VoiceDirectiveTests.PlaceholderIdentifiersCanBeBoundWithoutAssumingGuidSemantics),
            (nameof(VoiceIntegrationTests.VoiceIsRemovedFromNativeCommandsAndCanCoexistWithSceneCommands), VoiceIntegrationTests.VoiceIsRemovedFromNativeCommandsAndCanCoexistWithSceneCommands),
            (nameof(VoiceIntegrationTests.InvalidVoiceIsRemovedButRejected), VoiceIntegrationTests.InvalidVoiceIsRemovedButRejected),
            (nameof(VoiceIntegrationTests.ExistingComposerEditsPreserveVoiceBinding), VoiceIntegrationTests.ExistingComposerEditsPreserveVoiceBinding),
            (nameof(SpineLobbyThumbnailFitTests.KeiLobbyBoundsFitInsidePortraitCard), SpineLobbyThumbnailFitTests.KeiLobbyBoundsFitInsidePortraitCard),
            (nameof(SpineLobbyThumbnailFitTests.WideAndTallBoundsUseTheLimitingCardDimension), SpineLobbyThumbnailFitTests.WideAndTallBoundsUseTheLimitingCardDimension),
            (nameof(SpineLobbyThumbnailFitTests.AlreadyFittingBoundsAreCenteredWithoutEnlarging), SpineLobbyThumbnailFitTests.AlreadyFittingBoundsAreCenteredWithoutEnlarging),
            (nameof(SpineLobbyThumbnailFitTests.RepeatedInitializationDoesNotKeepShrinkingTheThumbnail), SpineLobbyThumbnailFitTests.RepeatedInitializationDoesNotKeepShrinkingTheThumbnail),
            (nameof(SpineLobbyThumbnailFitTests.FlippedSkeletonsKeepTheirOrientationAndFit), SpineLobbyThumbnailFitTests.FlippedSkeletonsKeepTheirOrientationAndFit),
            (nameof(SpineLobbyThumbnailFitTests.InvalidGeometryDoesNotProduceATransform), SpineLobbyThumbnailFitTests.InvalidGeometryDoesNotProduceATransform),
            (nameof(SpineLobbyAnimationTests.OrdinaryCharactersKeepNativePlayback), SpineLobbyAnimationTests.OrdinaryCharactersKeepNativePlayback),
            (nameof(SpineLobbyAnimationTests.MainAndAttachmentSelectionsResolveToSamePlan), SpineLobbyAnimationTests.MainAndAttachmentSelectionsResolveToSamePlan),
            (nameof(SpineLobbyAnimationTests.MissingCompanionsAreNeverInvented), SpineLobbyAnimationTests.MissingCompanionsAreNeverInvented),
            (nameof(SpineLobbyAnimationTests.EntranceAndNumberedTalksPlayOnce), SpineLobbyAnimationTests.EntranceAndNumberedTalksPlayOnce),
            (nameof(SpineLobbyAnimationTests.IdleAndOtherAnimationsKeepRequestedLoop), SpineLobbyAnimationTests.IdleAndOtherAnimationsKeepRequestedLoop),
            (nameof(SpineLobbyAnimationTests.MissingOrDifferentlyCasedAnimationsAreNotHandled), SpineLobbyAnimationTests.MissingOrDifferentlyCasedAnimationsAreNotHandled),
            (nameof(SpineLobbyAnimationTests.PairingRequiresExactSuffixAndExistingName), SpineLobbyAnimationTests.PairingRequiresExactSuffixAndExistingName),
            (nameof(SpineLobbyAnimationTests.TalkNamesWithoutNumericIdentifierKeepRequestedLoop), SpineLobbyAnimationTests.TalkNamesWithoutNumericIdentifierKeepRequestedLoop),
            (nameof(PlayerRuntimeContextTests.ClassifiesPreviewAndPlayback), PlayerRuntimeContextTests.ClassifiesPreviewAndPlayback),
            (nameof(PlayerRuntimeContextTests.ManualControlsArePlaybackOnlyAndFailClosed), PlayerRuntimeContextTests.ManualControlsArePlaybackOnlyAndFailClosed),
            (nameof(AutoDialogueDelayTests.ReplacesOnlyOfficialTwoSecondRuntimeTier), AutoDialogueDelayTests.ReplacesOnlyOfficialTwoSecondRuntimeTier),
            (nameof(PlaybackSceneDispatchGateTests.AuthorizesEachNewSceneWindowOnceIncludingReplay), PlaybackSceneDispatchGateTests.AuthorizesEachNewSceneWindowOnceIncludingReplay),
            (nameof(PlaybackSceneDispatchGateTests.RejectsNonCanonicalOrOutOfOrderBatches), PlaybackSceneDispatchGateTests.RejectsNonCanonicalOrOutOfOrderBatches),
            (nameof(PlaybackSceneDispatchGateTests.AuthorizesSlotPendingInstructionsWithFamilyAwareValidation), PlaybackSceneDispatchGateTests.AuthorizesSlotPendingInstructionsWithFamilyAwareValidation),
            (nameof(AapProjectReaderTests.ParseKnownProjectStructure), AapProjectReaderTests.ParseKnownProjectStructure),
            (nameof(AapProjectReaderTests.ParsesOfficialCharacterPositionTransition), AapProjectReaderTests.ParsesOfficialCharacterPositionTransition),
            (nameof(AapProjectReaderTests.CanonicalFingerprintIgnoresFormattingAndPropertyOrder), AapProjectReaderTests.CanonicalFingerprintIgnoresFormattingAndPropertyOrder),
            (nameof(AapProjectReaderTests.TypeMetadataRemainsPlainData), AapProjectReaderTests.TypeMetadataRemainsPlainData),
            (nameof(AapProjectReaderTests.InvalidJsonFailsWithoutThrowing), AapProjectReaderTests.InvalidJsonFailsWithoutThrowing),
            (nameof(AapProjectReaderTests.MissingNodeCollectionFailsClearly), AapProjectReaderTests.MissingNodeCollectionFailsClearly),
            (nameof(AapProjectReaderTests.FoundationComposesReaderAndPureServices), AapProjectReaderTests.FoundationComposesReaderAndPureServices),
            (nameof(AasScenarioReaderTests.ParsesSyntheticGenericScenarioArchive), AasScenarioReaderTests.ParsesSyntheticGenericScenarioArchive),
            (nameof(AasScenarioReaderTests.InvalidRootOffsetFailsWithoutThrowing), AasScenarioReaderTests.InvalidRootOffsetFailsWithoutThrowing),
            (nameof(AasScenarioReaderTests.RecordLimitAndWrongExtensionFailClosed), AasScenarioReaderTests.RecordLimitAndWrongExtensionFailClosed),
            (nameof(AasScenarioReaderTests.InvalidUtf8FailsClosed), AasScenarioReaderTests.InvalidUtf8FailsClosed),
            (nameof(ProjectPlaybackMapperTests.MapsOnlyExclusiveDialogueMatches), ProjectPlaybackMapperTests.MapsOnlyExclusiveDialogueMatches),
            (nameof(ProjectPlaybackMapperTests.UniqueAnchorMapsAnEntireContiguousNode), ProjectPlaybackMapperTests.UniqueAnchorMapsAnEntireContiguousNode),
            (nameof(ProjectPlaybackMapperTests.ContinuousCompilerStateDeltasDoNotBreakTextIdentity), ProjectPlaybackMapperTests.ContinuousCompilerStateDeltasDoNotBreakTextIdentity),
            (nameof(ProjectPlaybackMapperTests.DuplicateSemanticRecordsRemainAmbiguous), ProjectPlaybackMapperTests.DuplicateSemanticRecordsRemainAmbiguous),
            (nameof(ProjectPlaybackMapperTests.OlderOrDifferentlyNamedPlaybackIsReported), ProjectPlaybackMapperTests.OlderOrDifferentlyNamedPlaybackIsReported),
            (nameof(ProjectPlaybackMapperTests.NonPositionalPlaybackRecordIsRejected), ProjectPlaybackMapperTests.NonPositionalPlaybackRecordIsRejected),
            (nameof(ObservedSceneIdentityResolverTests.MapsExactCompiledScriptIdentityToProjectScene), ObservedSceneIdentityResolverTests.MapsExactCompiledScriptIdentityToProjectScene),
            (nameof(ObservedSceneIdentityResolverTests.EditorProjectionMapsSanitizedContaminatedPlaybackWithoutMutation), ObservedSceneIdentityResolverTests.EditorProjectionMapsSanitizedContaminatedPlaybackWithoutMutation),
            (nameof(ObservedSceneIdentityResolverTests.DuplicateCompiledScriptsRemainAmbiguous), ObservedSceneIdentityResolverTests.DuplicateCompiledScriptsRemainAmbiguous),
            (nameof(ObservedSceneIdentityResolverTests.ExactPlaybackRecordWithoutProjectMappingIsReported), ObservedSceneIdentityResolverTests.ExactPlaybackRecordWithoutProjectMappingIsReported),
            (nameof(ObservedSceneIdentityResolverTests.OlderPlaybackCanBeObservedButIsNotTrusted), ObservedSceneIdentityResolverTests.OlderPlaybackCanBeObservedButIsNotTrusted),
            (nameof(ObservedSceneIdentityResolverTests.UnrelatedAmbiguityDoesNotInvalidateSelectedSceneIdentity), ObservedSceneIdentityResolverTests.UnrelatedAmbiguityDoesNotInvalidateSelectedSceneIdentity),
            (nameof(ObservedSceneIdentityResolverTests.FileNameMismatchInvalidatesSelectedSceneTrust), ObservedSceneIdentityResolverTests.FileNameMismatchInvalidatesSelectedSceneTrust),
            (nameof(ObservedSceneIdentityResolverTests.MalformedIdentityFailsClosed), ObservedSceneIdentityResolverTests.MalformedIdentityFailsClosed),
            (nameof(ContinuationProjectionCompilerTests.CompilesShaBoundContiguousAppendChain), ContinuationProjectionCompilerTests.CompilesShaBoundContiguousAppendChain),
            (nameof(ContinuationProjectionCompilerTests.RejectsUnmappedMemberOfContinuationChain), ContinuationProjectionCompilerTests.RejectsUnmappedMemberOfContinuationChain),
            (nameof(ContinuationProjectionCompilerTests.RejectsNonContiguousPlaybackChain), ContinuationProjectionCompilerTests.RejectsNonContiguousPlaybackChain),
            (nameof(ContinuationProjectionCompilerTests.RejectsStaleProjectRevision), ContinuationProjectionCompilerTests.RejectsStaleProjectRevision),
            (nameof(ContinuationProjectionCompilerTests.RejectsOlderPlaybackArchive), ContinuationProjectionCompilerTests.RejectsOlderPlaybackArchive),
            (nameof(ContinuationProjectionCompilerTests.RejectsInvalidPlaybackFingerprint), ContinuationProjectionCompilerTests.RejectsInvalidPlaybackFingerprint),
            (nameof(ContinuationProjectionCompilerTests.DisabledRuleProducesNoRuntimeInstruction), ContinuationProjectionCompilerTests.DisabledRuleProducesNoRuntimeInstruction),
            (nameof(ContinuationProjectionCompilerTests.MalformedProjectSnapshotFailsWithoutThrowing), ContinuationProjectionCompilerTests.MalformedProjectSnapshotFailsWithoutThrowing),
            (nameof(ContinuationEngineTests.BuildsAppendChainAndResetsAtOrdinaryScene), ContinuationEngineTests.BuildsAppendChainAndResetsAtOrdinaryScene),
            (nameof(ContinuationEngineTests.StaleFingerprintDoesNotActivateRule), ContinuationEngineTests.StaleFingerprintDoesNotActivateRule),
            (nameof(ContinuationEngineTests.FirstSceneRuleIsRejected), ContinuationEngineTests.FirstSceneRuleIsRejected),
            (nameof(ContinuationEngineTests.DifferentProjectPathKeyFailsClosed), ContinuationEngineTests.DifferentProjectPathKeyFailsClosed),
            (nameof(ContinuationEngineTests.ChangedRevisionWithMatchingFingerprintRemainsActive), ContinuationEngineTests.ChangedRevisionWithMatchingFingerprintRemainsActive),
            (nameof(ContinuationEngineTests.MalformedRuleCollectionFailsWithoutThrowing), ContinuationEngineTests.MalformedRuleCollectionFailsWithoutThrowing),
            (nameof(ContinuationEngineTests.ReconcilerMovesRuleAfterInsertion), ContinuationEngineTests.ReconcilerMovesRuleAfterInsertion),
            (nameof(ContinuationEngineTests.ReconcilerRefusesAmbiguousFingerprint), ContinuationEngineTests.ReconcilerRefusesAmbiguousFingerprint),
            (nameof(JsonContinuationStoreTests.RoundTripsAndRotatesAtomicBackup), JsonContinuationStoreTests.RoundTripsAndRotatesAtomicBackup),
            (nameof(JsonContinuationStoreTests.ReportsRecoveryWhenPrimaryIsCorrupt), JsonContinuationStoreTests.ReportsRecoveryWhenPrimaryIsCorrupt),
            (nameof(JsonContinuationStoreTests.RejectsTraversalAbsolutePathsAndOfficialExtensions), JsonContinuationStoreTests.RejectsTraversalAbsolutePathsAndOfficialExtensions),
            (nameof(JsonContinuationStoreTests.FailedReplacementLeavesPrimaryUntouched), JsonContinuationStoreTests.FailedReplacementLeavesPrimaryUntouched),
            (nameof(JsonContinuationStoreTests.RejectsUnknownJsonPropertiesAndKeepsOfficialFilesUntouched), JsonContinuationStoreTests.RejectsUnknownJsonPropertiesAndKeepsOfficialFilesUntouched),
            (nameof(JsonContinuationStoreTests.RejectsInvalidDocumentBeforeCreatingFiles), JsonContinuationStoreTests.RejectsInvalidDocumentBeforeCreatingFiles),
            (nameof(JsonContinuationStoreTests.MissingSidecarReturnsNoneWithoutCreatingRoot), JsonContinuationStoreTests.MissingSidecarReturnsNoneWithoutCreatingRoot),
            (nameof(JsonContinuationStoreTests.ContendingWriterCannotReplacePrimary), JsonContinuationStoreTests.ContendingWriterCannotReplacePrimary),
            (nameof(JsonContinuationProjectionStoreTests.RoundTripsAndRotatesValidatedBackup), JsonContinuationProjectionStoreTests.RoundTripsAndRotatesValidatedBackup),
            (nameof(JsonContinuationProjectionStoreTests.RejectsStaleAapAndAasIdentities), JsonContinuationProjectionStoreTests.RejectsStaleAapAndAasIdentities),
            (nameof(JsonContinuationProjectionStoreTests.RejectsCurrentRecordFingerprintDrift), JsonContinuationProjectionStoreTests.RejectsCurrentRecordFingerprintDrift),
            (nameof(JsonContinuationProjectionStoreTests.LoadsBackupOnlyWhenItMatchesCurrentSnapshots), JsonContinuationProjectionStoreTests.LoadsBackupOnlyWhenItMatchesCurrentSnapshots),
            (nameof(JsonContinuationProjectionStoreTests.RejectsIncompleteIntermediateAppendChain), JsonContinuationProjectionStoreTests.RejectsIncompleteIntermediateAppendChain),
            (nameof(JsonContinuationProjectionStoreTests.RejectsTamperedDialogueSemanticsBeforeWriting), JsonContinuationProjectionStoreTests.RejectsTamperedDialogueSemanticsBeforeWriting),
            (nameof(JsonContinuationProjectionStoreTests.RejectsHandcraftedAmbiguousTextMapping), JsonContinuationProjectionStoreTests.RejectsHandcraftedAmbiguousTextMapping),
            (nameof(JsonContinuationProjectionStoreTests.RejectsTraversalOfficialAndSourceSidecarExtensions), JsonContinuationProjectionStoreTests.RejectsTraversalOfficialAndSourceSidecarExtensions),
            (nameof(JsonContinuationProjectionStoreTests.RejectsUnknownAndDuplicateJsonProperties), JsonContinuationProjectionStoreTests.RejectsUnknownAndDuplicateJsonProperties),
            (nameof(JsonContinuationProjectionStoreTests.MissingProjectionReturnsNoneWithoutCreatingRoot), JsonContinuationProjectionStoreTests.MissingProjectionReturnsNoneWithoutCreatingRoot),
            (nameof(JsonContinuationProjectionStoreTests.ContendingWriterCannotReplaceProjection), JsonContinuationProjectionStoreTests.ContendingWriterCannotReplaceProjection),
            (nameof(JsonContinuationProjectionStoreTests.FailedReplacementLeavesProjectionUntouched), JsonContinuationProjectionStoreTests.FailedReplacementLeavesProjectionUntouched),
            (nameof(PlaybackProjectionBinderTests.FindsInstructionByExactPlaybackRecord), PlaybackProjectionBinderTests.FindsInstructionByExactPlaybackRecord),
            (nameof(PlaybackProjectionBinderTests.BindsPlaybackWithoutProjectionForObservationOnly), PlaybackProjectionBinderTests.BindsPlaybackWithoutProjectionForObservationOnly),
            (nameof(PlaybackProjectionBinderTests.RejectsStaleProjectionPlaybackIdentity), PlaybackProjectionBinderTests.RejectsStaleProjectionPlaybackIdentity),
            (nameof(PlaybackProjectionBinderTests.RejectsStaleInstructionFingerprint), PlaybackProjectionBinderTests.RejectsStaleInstructionFingerprint),
            (nameof(PlaybackProjectionBinderTests.RejectsOutOfRangeObservedRecord), PlaybackProjectionBinderTests.RejectsOutOfRangeObservedRecord),
            (nameof(CharacterPresetTests.ParsesFourKindsAndCanonicalizesAcrossCultures), CharacterPresetTests.ParsesFourKindsAndCanonicalizesAcrossCultures),
            (nameof(CharacterPresetTests.RejectsMalformedIrrelevantAndUnsafePresetProperties), CharacterPresetTests.RejectsMalformedIrrelevantAndUnsafePresetProperties),
            (nameof(CharacterPresetTests.AcceptsExactLimitsAndRejectsNonFiniteNumbers), CharacterPresetTests.AcceptsExactLimitsAndRejectsNonFiniteNumbers),
            (nameof(CharacterPresetTests.NeutralAndFiniteDurationNeverAccumulateResidualPose), CharacterPresetTests.NeutralAndFiniteDurationNeverAccumulateResidualPose),
            (nameof(CharacterPresetTests.SwayRespectsAmplitudeDirectionAndPeriod), CharacterPresetTests.SwayRespectsAmplitudeDirectionAndPeriod),
            (nameof(CharacterPresetTests.SpinUsesYawForCompleteRevolutionsAndSelectedDirection), CharacterPresetTests.SpinUsesYawForCompleteRevolutionsAndSelectedDirection),
            (nameof(CharacterPresetTests.SquashKeepsPositiveReciprocalAxesAndArea), CharacterPresetTests.SquashKeepsPositiveReciprocalAxesAndArea),
            (nameof(CharacterPresetTests.OscillatingPresetsEaseFromAndBackToRest), CharacterPresetTests.OscillatingPresetsEaseFromAndBackToRest),
            (nameof(CharacterPresetTests.ZeroCyclesRunsUntilReleasedEvenAtVeryLargeTime), CharacterPresetTests.ZeroCyclesRunsUntilReleasedEvenAtVeryLargeTime),
            (nameof(CharacterPresetTests.HeadbuttHasThreeDirectionalStagesAndExactRecovery), CharacterPresetTests.HeadbuttHasThreeDirectionalStagesAndExactRecovery),
            (nameof(CharacterPresetPipelineTests.ExtractsSameSlotPresetTransformAndSpineSeparately), CharacterPresetPipelineTests.ExtractsSameSlotPresetTransformAndSpineSeparately),
            (nameof(CharacterPresetPipelineTests.UpsertReplacesOnlyTheMatchingSameSlotResource), CharacterPresetPipelineTests.UpsertReplacesOnlyTheMatchingSameSlotResource),
            (nameof(CharacterPresetPipelineTests.RemoveHonorsRevisionAndPreservesOtherFamiliesByteForByte), CharacterPresetPipelineTests.RemoveHonorsRevisionAndPreservesOtherFamiliesByteForByte),
            (nameof(CharacterPresetPipelineTests.PreviewLeasesSeparatePresetDeletionAndExplicitTombstones), CharacterPresetPipelineTests.PreviewLeasesSeparatePresetDeletionAndExplicitTombstones),
            (nameof(CharacterPresetPipelineTests.AllTwentyOneResourcesCompileBindDispatchAndStayOutOfPresetInheritance), CharacterPresetPipelineTests.AllTwentyOneResourcesCompileBindDispatchAndStayOutOfPresetInheritance),
            (nameof(CharacterPresetPipelineTests.SidecarCompositionOverridesOnlyTheSamePresetKey), CharacterPresetPipelineTests.SidecarCompositionOverridesOnlyTheSamePresetKey),
            (nameof(CharacterPresetPipelineTests.ForeignPresetRoutesCannotLeakIntoSavedProjection), CharacterPresetPipelineTests.ForeignPresetRoutesCannotLeakIntoSavedProjection),
            (nameof(CharacterPresetPipelineTests.CapabilitiesCanonicalTextAndDuplicatePresetKeysFailClosed), CharacterPresetPipelineTests.CapabilitiesCanonicalTextAndDuplicatePresetKeysFailClosed),
            (nameof(CharacterPresetPipelineTests.RawPresetLinesInPlaybackRemainContaminationErrors), CharacterPresetPipelineTests.RawPresetLinesInPlaybackRemainContaminationErrors),
            (nameof(CharacterPresetPoseOverlayTests.RelativeOverlayPreservesAuthoredPoseAndFlip), CharacterPresetPoseOverlayTests.RelativeOverlayPreservesAuthoredPoseAndFlip),
            (nameof(CharacterPresetPoseOverlayTests.RepeatedFramesRestoreWithoutAccumulation), CharacterPresetPoseOverlayTests.RepeatedFramesRestoreWithoutAccumulation),
            (nameof(CharacterPresetPoseOverlayTests.ExternalAxisWritesBecomeNextBaseline), CharacterPresetPoseOverlayTests.ExternalAxisWritesBecomeNextBaseline),
            (nameof(CharacterPresetPoseOverlayTests.RotationOwnershipHandlesUnityAngleWrapping), CharacterPresetPoseOverlayTests.RotationOwnershipHandlesUnityAngleWrapping),
            (nameof(CharacterPresetPoseOverlayTests.ScaleOwnershipPreservesTinyAndZeroAuthoredValues), CharacterPresetPoseOverlayTests.ScaleOwnershipPreservesTinyAndZeroAuthoredValues),
            (nameof(CharacterPresetPoseOverlayTests.NextDialogueWatermarkRejectsDelayedOlderPresets), CharacterPresetPoseOverlayTests.NextDialogueWatermarkRejectsDelayedOlderPresets),
            (nameof(CharacterPresetPoseOverlayTests.SameRowWithoutMessagesDoesNotEndDialogue), CharacterPresetPoseOverlayTests.SameRowWithoutMessagesDoesNotEndDialogue),
            (nameof(CharacterPresetPoseOverlayTests.EmptyPlaybackRowAdvanceEndsDialogue), CharacterPresetPoseOverlayTests.EmptyPlaybackRowAdvanceEndsDialogue),
            (nameof(CharacterPresetPoseOverlayTests.SnapshotProjectionExcludesOwnedRotationWithoutWritingPose), CharacterPresetPoseOverlayTests.SnapshotProjectionExcludesOwnedRotationWithoutWritingPose),
            (nameof(CharacterPresetPoseOverlayTests.TiltCleanupPreservesExternalPitchAndYawWhileRemovingOwnedRoll), CharacterPresetPoseOverlayTests.TiltCleanupPreservesExternalPitchAndYawWhileRemovingOwnedRoll),
            (nameof(CharacterPresetPoseOverlayTests.YawOverlayPreservesAuthoredTiltFlipAndScaleThroughoutTurns), CharacterPresetPoseOverlayTests.YawOverlayPreservesAuthoredTiltFlipAndScaleThroughoutTurns),
            (nameof(CharacterPresetPoseOverlayTests.YawOwnershipAcceptsEquivalentUnityEulerRepresentations), CharacterPresetPoseOverlayTests.YawOwnershipAcceptsEquivalentUnityEulerRepresentations),
            (nameof(CharacterPresetPoseOverlayTests.RepeatedYawFramesAndCompletionRestoreTheSameAuthoredPose), CharacterPresetPoseOverlayTests.RepeatedYawFramesAndCompletionRestoreTheSameAuthoredPose),
            (nameof(CharacterPresetPoseOverlayTests.ExternalRotationDuringYawIsKeptAsACompleteNewBaseline), CharacterPresetPoseOverlayTests.ExternalRotationDuringYawIsKeptAsACompleteNewBaseline),
            (nameof(CharacterPresetPoseOverlayTests.YawSnapshotsExcludeTemporaryTurnAndRestoreTheAuthoredScreenRotation), CharacterPresetPoseOverlayTests.YawSnapshotsExcludeTemporaryTurnAndRestoreTheAuthoredScreenRotation),
            (nameof(CharacterTransformTests.ParsesAbsoluteScreenSpaceCommand), CharacterTransformTests.ParsesAbsoluteScreenSpaceCommand),
            (nameof(CharacterTransformTests.ParsesRelativeAndResetCommands), CharacterTransformTests.ParsesRelativeAndResetCommands),
            (nameof(CharacterTransformTests.RejectsMalformedAndUnsafeCommands), CharacterTransformTests.RejectsMalformedAndUnsafeCommands),
            (nameof(CharacterTransformTests.PlansAbsoluteSetAndDeterministicFlip), CharacterTransformTests.PlansAbsoluteSetAndDeterministicFlip),
            (nameof(CharacterTransformTests.ScreenRotationKeepsItsSignAcrossHorizontalFlip), CharacterTransformTests.ScreenRotationKeepsItsSignAcrossHorizontalFlip),
            (nameof(CharacterTransformTests.DispatchConversionPreservesCommandedTweenPath), CharacterTransformTests.DispatchConversionPreservesCommandedTweenPath),
            (nameof(CharacterTransformTests.FlipOnlyAndReplayPreserveScreenRotation), CharacterTransformTests.FlipOnlyAndReplayPreserveScreenRotation),
            (nameof(CharacterTransformTests.RelativeRotationUsesScreenDirectionWhileFlipped), CharacterTransformTests.RelativeRotationUsesScreenDirectionWhileFlipped),
            (nameof(CharacterTransformTests.PlansRelativeMovementFromCurrentState), CharacterTransformTests.PlansRelativeMovementFromCurrentState),
            (nameof(CharacterTransformTests.PlansRotationThroughShortestArc), CharacterTransformTests.PlansRotationThroughShortestArc),
            (nameof(CharacterTransformTests.SetRotationBeyond360PerformsDeliberateMultiTurn), CharacterTransformTests.SetRotationBeyond360PerformsDeliberateMultiTurn),
            (nameof(CharacterTransformTests.SetRotationWithin360TakesShortestPathFromLiveValue), CharacterTransformTests.SetRotationWithin360TakesShortestPathFromLiveValue),
            (nameof(CharacterTransformTests.ResetAfterMultiTurnKeepsVisualContinuity), CharacterTransformTests.ResetAfterMultiTurnKeepsVisualContinuity),
            (nameof(CharacterTransformTests.ValidatorBoundsMultiTurnRotation), CharacterTransformTests.ValidatorBoundsMultiTurnRotation),
            (nameof(CharacterTransformTests.MultiTurnContinuityAcrossFlipAndReplayRestore), CharacterTransformTests.MultiTurnContinuityAcrossFlipAndReplayRestore),
            (nameof(CharacterTransformTests.ResetRestoresCapturedBaseline), CharacterTransformTests.ResetRestoresCapturedBaseline),
            (nameof(CharacterTransformTests.BaselineStoreKeepsFirstSnapshotAndInvalidatesOnSceneChange), CharacterTransformTests.BaselineStoreKeepsFirstSnapshotAndInvalidatesOnSceneChange),
            (nameof(CharacterTransformTests.ReplayRestoreTouchesOnlyCommandAxes), CharacterTransformTests.ReplayRestoreTouchesOnlyCommandAxes),
            (nameof(CharacterTransformTests.RepeatedRelativeReplayStartsFromCapturedBaseline), CharacterTransformTests.RepeatedRelativeReplayStartsFromCapturedBaseline),
            (nameof(CharacterTransformTests.InheritedStartIsIdempotentAcrossRepeatedFlipApplication), CharacterTransformTests.InheritedStartIsIdempotentAcrossRepeatedFlipApplication),
            (nameof(CharacterTransformTests.InheritedRelativeOffsetsAlwaysUseOfficialBaseline), CharacterTransformTests.InheritedRelativeOffsetsAlwaysUseOfficialBaseline),
            (nameof(CharacterTransformTests.InheritedFlipKeepsScreenRotationSign), CharacterTransformTests.InheritedFlipKeepsScreenRotationSign),
            (nameof(CharacterTransformTests.UncontrolledAxesKeepCurrentLiveValues), CharacterTransformTests.UncontrolledAxesKeepCurrentLiveValues),
            (nameof(CharacterTransformTests.FlipOnlyInheritanceKeepsOtherAxesAtLiveValues), CharacterTransformTests.FlipOnlyInheritanceKeepsOtherAxesAtLiveValues),
            (nameof(CharacterTransformTests.OriginBaselineStoreClaimsProvisionalAndRecapturesBoundaries), CharacterTransformTests.OriginBaselineStoreClaimsProvisionalAndRecapturesBoundaries),
            (nameof(CharacterTransformTests.OriginBaselineStoreAllowsResetOnlyOfficialCapture), CharacterTransformTests.OriginBaselineStoreAllowsResetOnlyOfficialCapture),
            (nameof(SceneCameraTests.ParsesSetMoveAndReset), SceneCameraTests.ParsesSetMoveAndReset),
            (nameof(SceneCameraTests.RejectsMalformedOrUnsafeValues), SceneCameraTests.RejectsMalformedOrUnsafeValues),
            (nameof(SceneCameraTests.PlansPersistentStateAndReplayRestore), SceneCameraTests.PlansPersistentStateAndReplayRestore),
            (nameof(SceneCameraTests.ResetReturnsToNeutralCamera), SceneCameraTests.ResetReturnsToNeutralCamera),
            (nameof(SceneCameraTests.CanonicalizesPublicCameraFamily), SceneCameraTests.CanonicalizesPublicCameraFamily),
            (nameof(SceneCameraTests.ExtractsPublicCameraAndRejectsDuplicates), SceneCameraTests.ExtractsPublicCameraAndRejectsDuplicates),
            (nameof(SceneCameraTests.DispatchGateAllowsFiveSlotsAndOneCamera), SceneCameraTests.DispatchGateAllowsFiveSlotsAndOneCamera),
            (nameof(VisualEditorTests.MapsStoryCoordinatesThroughDynamicViewport), VisualEditorTests.MapsStoryCoordinatesThroughDynamicViewport),
            (nameof(VisualEditorTests.ViewportZoomOutKeepsOffScreenCharactersReachable), VisualEditorTests.ViewportZoomOutKeepsOffScreenCharactersReachable),
            (nameof(VisualEditorTests.ComposesSlotAnchorsWithLocalCharacterOffsets), VisualEditorTests.ComposesSlotAnchorsWithLocalCharacterOffsets),
            (nameof(VisualEditorTests.OccupiedSlotHitTestingFollowsItsRotatedVisual), VisualEditorTests.OccupiedSlotHitTestingFollowsItsRotatedVisual),
            (nameof(VisualEditorTests.SlotClickDoesNotBecomeADragUntilPointerMoves), VisualEditorTests.SlotClickDoesNotBecomeADragUntilPointerMoves),
            (nameof(VisualEditorTests.ScreenTextSequenceKeepsDistinctMarkersAndFindsPreviousText), VisualEditorTests.ScreenTextSequenceKeepsDistinctMarkersAndFindsPreviousText),
            (nameof(VisualEditorTests.ScreenTextTemplateKeepsTheLastCompleteUserSettings), VisualEditorTests.ScreenTextTemplateKeepsTheLastCompleteUserSettings),
            (nameof(VisualEditorTests.CameraFrameMovesIndependentlyFromTheStoryCanvas), VisualEditorTests.CameraFrameMovesIndependentlyFromTheStoryCanvas),
            (nameof(VisualEditorTests.BuildsAndReadsCameraGuiDrafts), VisualEditorTests.BuildsAndReadsCameraGuiDrafts),
            (nameof(VisualEditorTests.BuildsOccupiedAndPendingDragDrafts), VisualEditorTests.BuildsOccupiedAndPendingDragDrafts),
            (nameof(VisualEditorTests.BuildsCombinedInspectorAndResetDrafts), VisualEditorTests.BuildsCombinedInspectorAndResetDrafts),
            (nameof(VisualEditorTests.ReadsExistingDraftsWithoutLosingTheirSemantics), VisualEditorTests.ReadsExistingDraftsWithoutLosingTheirSemantics),
            (nameof(VisualEditorTests.SuppressesOnlyTheVisibleEditorInputRegion), VisualEditorTests.SuppressesOnlyTheVisibleEditorInputRegion),
            (nameof(EditorCommandDocumentTests.ReadsAavtDocumentWithoutChangingOfficialText), EditorCommandDocumentTests.ReadsAavtDocumentWithoutChangingOfficialText),
            (nameof(EditorCommandDocumentTests.UpsertsOneSlotAndPreservesEveryOtherLine), EditorCommandDocumentTests.UpsertsOneSlotAndPreservesEveryOtherLine),
            (nameof(EditorCommandDocumentTests.AppendsPendingWithoutTimingAndRejectsStaleRevision), EditorCommandDocumentTests.AppendsPendingWithoutTimingAndRejectsStaleRevision),
            (nameof(EditorCommandDocumentTests.UpsertsSingletonCameraWithoutReplacingCharacters), EditorCommandDocumentTests.UpsertsSingletonCameraWithoutReplacingCharacters),
            (nameof(EditorCommandDocumentTests.SetContinueAppendsRemovesAndIsIdempotent), EditorCommandDocumentTests.SetContinueAppendsRemovesAndIsIdempotent),
            (nameof(EditorCommandDocumentTests.ParsesOfficialScreenTextAndRequiresItsFinalSemicolon), EditorCommandDocumentTests.ParsesOfficialScreenTextAndRequiresItsFinalSemicolon),
            (nameof(EditorCommandDocumentTests.UpsertsOfficialScreenTextWithoutTouchingOtherDirectives), EditorCommandDocumentTests.UpsertsOfficialScreenTextWithoutTouchingOtherDirectives),
            (nameof(EditorCommandDocumentTests.RemovesScreenTextAndRefusesToCollapseMultipleAuthoredLines), EditorCommandDocumentTests.RemovesScreenTextAndRefusesToCollapseMultipleAuthoredLines),
            (nameof(EditorCommandDocumentTests.AddsOfficialClearScreenTextWithoutChangingOtherLines), EditorCommandDocumentTests.AddsOfficialClearScreenTextWithoutChangingOtherLines),
            (nameof(EditorCommandDocumentTests.UndoStoreRequiresTheExactAppliedSceneRevision), EditorCommandDocumentTests.UndoStoreRequiresTheExactAppliedSceneRevision),
            (nameof(CommandTimelineTests.CanonicalizesCharacterTransformDirective), CommandTimelineTests.CanonicalizesCharacterTransformDirective),
            (nameof(CommandTimelineTests.CompilesStrictShaBoundProjection), CommandTimelineTests.CompilesStrictShaBoundProjection),
            (nameof(CommandTimelineTests.RejectsEnabledCommandsForSameSlot), CommandTimelineTests.RejectsEnabledCommandsForSameSlot),
            (nameof(CommandTimelineTests.RejectsPositionCommandDuringOfficialPositionTransition), CommandTimelineTests.RejectsPositionCommandDuringOfficialPositionTransition),
            (nameof(CommandTimelineTests.ResolvesDuplicateCompiledScriptIdentityByPlaybackRow), CommandTimelineTests.ResolvesDuplicateCompiledScriptIdentityByPlaybackRow),
            (nameof(CommandTimelineTests.RejectsCommandOrderGap), CommandTimelineTests.RejectsCommandOrderGap),
            (nameof(CommandTimelineTests.RejectsMissingWorkspaceCapability), CommandTimelineTests.RejectsMissingWorkspaceCapability),
            (nameof(CommandTimelineTests.RejectsStaleTimelineRevision), CommandTimelineTests.RejectsStaleTimelineRevision),
            (nameof(CommandTimelineTests.AllowsOlderPlaybackWhenCommandTargetMapsExactly), CommandTimelineTests.AllowsOlderPlaybackWhenCommandTargetMapsExactly),
            (nameof(CommandTimelineTests.BindsByCompiledScriptAndSnapshotsProjection), CommandTimelineTests.BindsByCompiledScriptAndSnapshotsProjection),
            (nameof(CommandTimelineTests.BinderRejectsStaleScriptIdentity), CommandTimelineTests.BinderRejectsStaleScriptIdentity),
            (nameof(CommandTimelineTests.BinderReportsAmbiguousObservedIdentity), CommandTimelineTests.BinderReportsAmbiguousObservedIdentity),
            (nameof(CommandTimelineTests.WindowObserverFindsOneCommandBatchAndIgnoresNoise), CommandTimelineTests.WindowObserverFindsOneCommandBatchAndIgnoresNoise),
            (nameof(CommandTimelineTests.WindowObserverDispatchesUniqueCommandAcrossMultipleRecords), CommandTimelineTests.WindowObserverDispatchesUniqueCommandAcrossMultipleRecords),
            (nameof(CommandTimelineTests.WindowObserverRejectsMultipleCommandRecords), CommandTimelineTests.WindowObserverRejectsMultipleCommandRecords),
            (nameof(CommandTimelineTests.WindowObserverFailsClosedForMultipleOrAmbiguousRecords), CommandTimelineTests.WindowObserverFailsClosedForMultipleOrAmbiguousRecords),
            (nameof(CommandTimelineTests.TimelineEditorUpsertsBySceneAndPhysicalSlot), CommandTimelineTests.TimelineEditorUpsertsBySceneAndPhysicalSlot),
            (nameof(CommandTimelineTests.TimelineEditorRemovesAndRenumbersCommands), CommandTimelineTests.TimelineEditorRemovesAndRenumbersCommands),
            (nameof(EmbeddedAavtDirectiveTests.ExtractsAavtAndPreservesOfficialDirectives), EmbeddedAavtDirectiveTests.ExtractsAavtAndPreservesOfficialDirectives),
            (nameof(EmbeddedAavtDirectiveTests.RejectsMalformedAndDuplicateEmbeddedSlots), EmbeddedAavtDirectiveTests.RejectsMalformedAndDuplicateEmbeddedSlots),
            (nameof(EmbeddedAavtDirectiveTests.ExtractsContinueMarkerWithoutSlotCost), EmbeddedAavtDirectiveTests.ExtractsContinueMarkerWithoutSlotCost),
            (nameof(EmbeddedAavtDirectiveTests.ClassifiesCommandlessNonVisualCapturesForTheTombstoneGate), EmbeddedAavtDirectiveTests.ClassifiesCommandlessNonVisualCapturesForTheTombstoneGate),
            (nameof(EmbeddedAavtDirectiveTests.ContaminationScanStillRejectsRawContinueMarkerInPlayback), EmbeddedAavtDirectiveTests.ContaminationScanStillRejectsRawContinueMarkerInPlayback),
            (nameof(EmbeddedAavtDirectiveTests.ContinueIdentitySnapshotTracksCurrentProjectAndDeletion), EmbeddedAavtDirectiveTests.ContinueIdentitySnapshotTracksCurrentProjectAndDeletion),
            (nameof(EmbeddedAavtDirectiveTests.ContinueIdentitySnapshotRejectsSharedMarkedAndUnmarkedScript), EmbeddedAavtDirectiveTests.ContinueIdentitySnapshotRejectsSharedMarkedAndUnmarkedScript),
            (nameof(EmbeddedAavtDirectiveTests.CompilesProjectPromptsAndOverridesSameSlotSidecar), EmbeddedAavtDirectiveTests.CompilesProjectPromptsAndOverridesSameSlotSidecar),
            (nameof(EmbeddedAavtDirectiveTests.EmbeddedPreviewGuardRejectsStaleRecordAndSelection), EmbeddedAavtDirectiveTests.EmbeddedPreviewGuardRejectsStaleRecordAndSelection),
            (nameof(EmbeddedAavtDirectiveTests.RejectsPlaybackContainingRawEmbeddedDirective), EmbeddedAavtDirectiveTests.RejectsPlaybackContainingRawEmbeddedDirective),
            (nameof(SpineOverlayTests.ParsesPlayAndClearWithStickyDefaults), SpineOverlayTests.ParsesPlayAndClearWithStickyDefaults),
            (nameof(SpineOverlayTests.RejectsOutOfRangeTracksSlotsAndProperties), SpineOverlayTests.RejectsOutOfRangeTracksSlotsAndProperties),
            (nameof(SpineOverlayTests.CanonicalDirectiveRoundTripsExactly), SpineOverlayTests.CanonicalDirectiveRoundTripsExactly),
            (nameof(SpineOverlayTests.ExtractsOverlayBesideCharacterAndCameraOnTheSameSlot), SpineOverlayTests.ExtractsOverlayBesideCharacterAndCameraOnTheSameSlot),
            (nameof(SpineOverlayTests.SceneBudgetFitsFiveCharactersOneCameraAndEveryReservedTrack), SpineOverlayTests.SceneBudgetFitsFiveCharactersOneCameraAndEveryReservedTrack),
            (nameof(SpineOverlayTests.CleanupPlannerKeepsOverlayTracksApartAndRefusesForeignTracks), SpineOverlayTests.CleanupPlannerKeepsOverlayTracksApartAndRefusesForeignTracks),
            (nameof(SpineOverlayTests.UnknownNamespaceStillFailsClosedAndNamesTheOverlayForm), SpineOverlayTests.UnknownNamespaceStillFailsClosedAndNamesTheOverlayForm),
            (nameof(EditorSelectedSceneCaptureProofTests.TwoFreshSelectedReadsRetainTheCompleteInputProof), EditorSelectedSceneCaptureProofTests.TwoFreshSelectedReadsRetainTheCompleteInputProof),
            (nameof(EditorSelectedSceneCaptureProofTests.TokenRotationAndEmptyInputsDoNotEraseActualContextEvidence), EditorSelectedSceneCaptureProofTests.TokenRotationAndEmptyInputsDoNotEraseActualContextEvidence),
            (nameof(EditorSelectedSceneCaptureProofTests.EveryFullSceneDimensionMustSurviveTheSecondSelectedRead), EditorSelectedSceneCaptureProofTests.EveryFullSceneDimensionMustSurviveTheSecondSelectedRead),
            (nameof(EditorSelectedSceneCaptureProofTests.ContextDialogueAndPromptDriftCannotProduceASelectedProof), EditorSelectedSceneCaptureProofTests.ContextDialogueAndPromptDriftCannotProduceASelectedProof),
            (nameof(EditorSelectedSceneCaptureProofTests.MissingOrMalformedSelectedEvidenceFailsEvenWhenBothReadsMatch), EditorSelectedSceneCaptureProofTests.MissingOrMalformedSelectedEvidenceFailsEvenWhenBothReadsMatch),
            (nameof(EditorSelectedSceneCaptureProofTests.SharedDialogueMustMatchTheActualSelectedSceneSnapshot), EditorSelectedSceneCaptureProofTests.SharedDialogueMustMatchTheActualSelectedSceneSnapshot),
            (nameof(EditorSelectedSceneCaptureProofTests.SelectedProofMatchesTheExistingFallbackFormatWithoutRelaxingItsGuard), EditorSelectedSceneCaptureProofTests.SelectedProofMatchesTheExistingFallbackFormatWithoutRelaxingItsGuard),
            (nameof(EditorSelectedSceneCaptureProofTests.RepeatedSelectedDataListPreservesTheRealFirstLogAndCompletesOnce), EditorSelectedSceneCaptureProofTests.RepeatedSelectedDataListPreservesTheRealFirstLogAndCompletesOnce),
            (nameof(EditorSelectedSceneCaptureProofTests.ChangedSelectedEvidenceStartsFreshWithoutBorrowingTheOldFirstLog), EditorSelectedSceneCaptureProofTests.ChangedSelectedEvidenceStartsFreshWithoutBorrowingTheOldFirstLog),
            (nameof(EditorSelectedSceneCaptureProofTests.DuplicateSelectedEvidenceCannotRearmAnInvalidFirstLog), EditorSelectedSceneCaptureProofTests.DuplicateSelectedEvidenceCannotRearmAnInvalidFirstLog),
            (nameof(EditorDataListCascadeProofTests.CompleteEqualProofReusesZeroOpaqueRequestAndEmptyInputs), EditorDataListCascadeProofTests.CompleteEqualProofReusesZeroOpaqueRequestAndEmptyInputs),
            (nameof(EditorDataListCascadeProofTests.EveryCapturedDimensionMustRemainIdentical), EditorDataListCascadeProofTests.EveryCapturedDimensionMustRemainIdentical),
            (nameof(EditorDataListCascadeProofTests.ClosedNewAndRegressedWindowsNeverReuseTheOriginalEpisode), EditorDataListCascadeProofTests.ClosedNewAndRegressedWindowsNeverReuseTheOriginalEpisode),
            (nameof(EditorDataListCascadeProofTests.MissingInvalidOrInternallyMismatchedProofsNeverReuse), EditorDataListCascadeProofTests.MissingInvalidOrInternallyMismatchedProofsNeverReuse),
            (nameof(EditorDataListCascadeProofTests.DuplicateDataListPreservesTheRealFirstLogAndCompletesOnce), EditorDataListCascadeProofTests.DuplicateDataListPreservesTheRealFirstLogAndCompletesOnce),
            (nameof(EditorDataListCascadeProofTests.DuplicateDataListCannotResetARejectedFirstLog), EditorDataListCascadeProofTests.DuplicateDataListCannotResetARejectedFirstLog),
            (nameof(EditorDataListCascadeProofTests.PromptDriftBeginsANewGenerationWithoutBorrowingTheOldFirstLog), EditorDataListCascadeProofTests.PromptDriftBeginsANewGenerationWithoutBorrowingTheOldFirstLog),
            (nameof(EditorDataListCascadeProofTests.ReturningToAnEarlierSceneCannotReviveItsHistoricalEpisode), EditorDataListCascadeProofTests.ReturningToAnEarlierSceneCannotReviveItsHistoricalEpisode),
            (nameof(ManagedSelectionLogCorrelationTests.ZeroRequestIsOpaqueAndPreservesTheObservedScript), ManagedSelectionLogCorrelationTests.ZeroRequestIsOpaqueAndPreservesTheObservedScript),
            (nameof(ManagedSelectionLogCorrelationTests.NoMarkerAndLaterMessagesNeverSupplyAnIdentity), ManagedSelectionLogCorrelationTests.NoMarkerAndLaterMessagesNeverSupplyAnIdentity),
            (nameof(ManagedSelectionLogCorrelationTests.ReusedRequestCannotReviveASupersededMarker), ManagedSelectionLogCorrelationTests.ReusedRequestCannotReviveASupersededMarker),
            (nameof(ManagedSelectionLogCorrelationTests.BackgroundFirstMessageCannotBeReplacedByAMainThreadMessage), ManagedSelectionLogCorrelationTests.BackgroundFirstMessageCannotBeReplacedByAMainThreadMessage),
            (nameof(ManagedSelectionLogCorrelationTests.MissingCaptureOrMainThreadProofCannotPublish), ManagedSelectionLogCorrelationTests.MissingCaptureOrMainThreadProofCannotPublish),
            (nameof(ManagedSelectionLogCorrelationTests.LateClosedOrDifferentWindowMessagesAreRejected), ManagedSelectionLogCorrelationTests.LateClosedOrDifferentWindowMessagesAreRejected),
            (nameof(ManagedSelectionLogCorrelationTests.InvalidOrMissingFirstScriptDoesNotFallBackToALaterOne), ManagedSelectionLogCorrelationTests.InvalidOrMissingFirstScriptDoesNotFallBackToALaterOne),
            (nameof(ManagedSelectionLogCorrelationTests.FreshSceneDriftOrUnavailableSceneConsumesOnlyThatCandidate), ManagedSelectionLogCorrelationTests.FreshSceneDriftOrUnavailableSceneConsumesOnlyThatCandidate),
            (nameof(ManagedSelectionLogCorrelationTests.ConfirmationRejectsWrongThreadAndWindowRegression), ManagedSelectionLogCorrelationTests.ConfirmationRejectsWrongThreadAndWindowRegression),
            (nameof(ManagedSelectionLogCorrelationTests.SameSelectionCascadeMayFinishBeforeThePumpButPublishesOnce), ManagedSelectionLogCorrelationTests.SameSelectionCascadeMayFinishBeforeThePumpButPublishesOnce),
            (nameof(ManagedSelectionLogCorrelationTests.DeferredFirstLogRequiresExplicitEligiblePreviewProofAndPublishesOnce), ManagedSelectionLogCorrelationTests.DeferredFirstLogRequiresExplicitEligiblePreviewProofAndPublishesOnce),
            (nameof(ManagedSelectionLogCorrelationTests.OriginalBeginRemainsStrictOnlyAndStrictCaptureNeverDefers), ManagedSelectionLogCorrelationTests.OriginalBeginRemainsStrictOnlyAndStrictCaptureNeverDefers),
            (nameof(ManagedSelectionLogCorrelationTests.OverlappingOpenWindowTimingRetainsOnlyTheRealFirstLog), ManagedSelectionLogCorrelationTests.OverlappingOpenWindowTimingRetainsOnlyTheRealFirstLog),
            (nameof(ManagedSelectionLogCorrelationTests.FirstAfterCloseCannotBindEarlierWindowMessageOrFallBack), ManagedSelectionLogCorrelationTests.FirstAfterCloseCannotBindEarlierWindowMessageOrFallBack),
            (nameof(ManagedSelectionLogCorrelationTests.DeferredProofRequiresExactLeaseGenerationRequestAndOriginalCutoff), ManagedSelectionLogCorrelationTests.DeferredProofRequiresExactLeaseGenerationRequestAndOriginalCutoff),
            (nameof(ManagedSelectionLogCorrelationTests.DeferredProofMustMatchTheFirstScriptExactlyOnce), ManagedSelectionLogCorrelationTests.DeferredProofMustMatchTheFirstScriptExactlyOnce),
            (nameof(ManagedSelectionLogCorrelationTests.DeferredProofRequiresFreshUnchangedFullSceneIncludingProject), ManagedSelectionLogCorrelationTests.DeferredProofRequiresFreshUnchangedFullSceneIncludingProject),
            (nameof(ManagedSelectionLogCorrelationTests.DeferredConfirmationRequiresMainThreadAndAllPreviewContextFields), ManagedSelectionLogCorrelationTests.DeferredConfirmationRequiresMainThreadAndAllPreviewContextFields),
            (nameof(ManagedSelectionLogCorrelationTests.DeferredWindowsRequireEligibilityAndMonotonicIssuedAndClosedWatermarks), ManagedSelectionLogCorrelationTests.DeferredWindowsRequireEligibilityAndMonotonicIssuedAndClosedWatermarks),
            (nameof(ManagedSelectionLogCorrelationTests.SupersededDeferredRequestCannotConsumeTheNewerReusedOpaqueRequest), ManagedSelectionLogCorrelationTests.SupersededDeferredRequestCannotConsumeTheNewerReusedOpaqueRequest),
            (nameof(ManagedSelectionLogCorrelationTests.PriorStrictConfirmationCannotSupplyTheNewReusedRequestGeneration), ManagedSelectionLogCorrelationTests.PriorStrictConfirmationCannotSupplyTheNewReusedRequestGeneration),
            (nameof(ManagedSelectionLogCorrelationTests.InvalidBackgroundMissingOrSkippedFirstLogCannotCreateDeferredIdentity), ManagedSelectionLogCorrelationTests.InvalidBackgroundMissingOrSkippedFirstLogCannotCreateDeferredIdentity),
            (nameof(ManagedSelectionLogCorrelationTests.RegressedOrInvalidTimingCannotBeHeldForDeferredProof), ManagedSelectionLogCorrelationTests.RegressedOrInvalidTimingCannotBeHeldForDeferredProof),
            (nameof(EditorPreviewLeaseTests.ZeroOpaqueRequestAuthorizesWithoutChangingTheSceneOrOtherGuards), EditorPreviewLeaseTests.ZeroOpaqueRequestAuthorizesWithoutChangingTheSceneOrOtherGuards),
            (nameof(PlayerAdvanceObservationWindowTests.DroppedCompletedWindowStillAdvancesItsCloseWatermark), PlayerAdvanceObservationWindowTests.DroppedCompletedWindowStillAdvancesItsCloseWatermark),
            (nameof(PlayerAdvanceObservationWindowTests.LifecycleSnapshotTracksActualOpenCompletedAndUnpairedClose), PlayerAdvanceObservationWindowTests.LifecycleSnapshotTracksActualOpenCompletedAndUnpairedClose),
            (nameof(PlayerAdvanceObservationWindowTests.LifecycleSnapshotSeparatesOverlappingActiveWindowFromPriorClose), PlayerAdvanceObservationWindowTests.LifecycleSnapshotSeparatesOverlappingActiveWindowFromPriorClose),
            (nameof(PlayerAdvanceObservationWindowTests.DroppedSupersededWindowClosesBeforeTheNextWindowOpens), PlayerAdvanceObservationWindowTests.DroppedSupersededWindowClosesBeforeTheNextWindowOpens),
            (nameof(PlayerAdvanceObservationWindowTests.DroppedPostfixWithoutPrefixCannotLookLikeAnOpenLogWindow), PlayerAdvanceObservationWindowTests.DroppedPostfixWithoutPrefixCannotLookLikeAnOpenLogWindow),
            (nameof(EditorPreviewWindowLeaseTests.ConfirmedGenerationReplaysOncePerActualWindow), EditorPreviewWindowLeaseTests.ConfirmedGenerationReplaysOncePerActualWindow),
            (nameof(EditorPreviewWindowLeaseTests.ReplayRequiresActualWindowAndConfirmedObservation), EditorPreviewWindowLeaseTests.ReplayRequiresActualWindowAndConfirmedObservation),
            (nameof(EditorPreviewWindowLeaseTests.ReplayRejectsOldGenerationsAndRegressedWindows), EditorPreviewWindowLeaseTests.ReplayRejectsOldGenerationsAndRegressedWindows),
            (nameof(EditorPreviewWindowLeaseTests.ReplayRequiresUnchangedGenerationRequestObservationHashAndScene), EditorPreviewWindowLeaseTests.ReplayRequiresUnchangedGenerationRequestObservationHashAndScene),
            (nameof(EditorPreviewWindowLeaseTests.ReplayStillRequiresPreviewRuntimeExactCountTimingAndCanonicalCommands), EditorPreviewWindowLeaseTests.ReplayStillRequiresPreviewRuntimeExactCountTimingAndCanonicalCommands),
            (nameof(EditorPreviewWindowLeaseTests.LegacyEntryRemainsOneShotAndBothEntriesRejectSupersededGenerations), EditorPreviewWindowLeaseTests.LegacyEntryRemainsOneShotAndBothEntriesRejectSupersededGenerations),
            (nameof(EditorPreviewLeaseTests.NegativeOpaqueRequestsFailForCapturedAndLiveSelections), EditorPreviewLeaseTests.NegativeOpaqueRequestsFailForCapturedAndLiveSelections),
            (nameof(EditorPreviewLeaseTests.AuthorizesExactEditorPreviewOnceAndMarksPendingDeferred), EditorPreviewLeaseTests.AuthorizesExactEditorPreviewOnceAndMarksPendingDeferred),
            (nameof(EditorPreviewLeaseTests.RejectsEveryLiveIdentityDriftAndNonEditorWindows), EditorPreviewLeaseTests.RejectsEveryLiveIdentityDriftAndNonEditorWindows),
            (nameof(EditorPreviewLeaseTests.EnforcesCanonicalSixResourceLimitAndExplicitTombstones), EditorPreviewLeaseTests.EnforcesCanonicalSixResourceLimitAndExplicitTombstones),
            (nameof(EditorPreviewLeaseTests.PlansFieldAndWholeCommandCleanupFromResourceUnion), EditorPreviewLeaseTests.PlansFieldAndWholeCommandCleanupFromResourceUnion),
            (nameof(EditorPreviewLeaseTests.ConsecutiveDataListGenerationsEachAuthorizeExactlyOnce), EditorPreviewLeaseTests.ConsecutiveDataListGenerationsEachAuthorizeExactlyOnce),
            (nameof(EditorPreviewLeaseTests.SecondGreenPreviewWithoutSelectionEventStillAuthorizes), EditorPreviewLeaseTests.SecondGreenPreviewWithoutSelectionEventStillAuthorizes),
            (nameof(EditorPreviewLeaseTests.StaleSiblingWindowsWithinOneGenerationDoNotDoubleExecute), EditorPreviewLeaseTests.StaleSiblingWindowsWithinOneGenerationDoNotDoubleExecute),
            (nameof(EditorPreviewLeaseTests.TombstoneAuthorizationOverridesOlderOverlayFootprint), EditorPreviewLeaseTests.TombstoneAuthorizationOverridesOlderOverlayFootprint),
            (nameof(EditorPreviewLeaseTests.SilentSlotInheritanceValuesDoNotAccumulateAcrossRepeats), EditorPreviewLeaseTests.SilentSlotInheritanceValuesDoNotAccumulateAcrossRepeats),
            (nameof(LiveProjectKeyComposerTests.ComposeProducesDocumentedDeterministic24HexKey), LiveProjectKeyComposerTests.ComposeProducesDocumentedDeterministic24HexKey),
            (nameof(LiveProjectKeyComposerTests.ComposerIsDeterministicAndSeparatesDistinctTriples), LiveProjectKeyComposerTests.ComposerIsDeterministicAndSeparatesDistinctTriples),
            (nameof(LiveProjectKeyComposerTests.ComposerRejectsAllEmptyWhitespaceAndMissingInputs), LiveProjectKeyComposerTests.ComposerRejectsAllEmptyWhitespaceAndMissingInputs),
            (nameof(LiveProjectKeyComposerTests.EffectiveKeyPrecedenceRootedPathBeatsAdoptedLiveKey), LiveProjectKeyComposerTests.EffectiveKeyPrecedenceRootedPathBeatsAdoptedLiveKey),
            (nameof(LiveProjectKeyComposerTests.PathArrivalRotatesEffectiveKeyExactlyOnce), LiveProjectKeyComposerTests.PathArrivalRotatesEffectiveKeyExactlyOnce),
            (nameof(LiveProjectKeyComposerTests.UnsavedToSavedAdoptedKeyRotationIsSingleAndDeterministic), LiveProjectKeyComposerTests.UnsavedToSavedAdoptedKeyRotationIsSingleAndDeterministic),
            (nameof(EmbeddedDiagnosticsTests.BoundaryCaptureLineRecordsFieldsWithoutScriptContent), EmbeddedDiagnosticsTests.BoundaryCaptureLineRecordsFieldsWithoutScriptContent),
            (nameof(EmbeddedDiagnosticsTests.ContaminationFailureCarriesStableReasonAndRecord), EmbeddedDiagnosticsTests.ContaminationFailureCarriesStableReasonAndRecord),
            (nameof(AdvanceWindowAdmissionPolicyTests.CompletedWindowsKeepTheirExistingAdmission), AdvanceWindowAdmissionPolicyTests.CompletedWindowsKeepTheirExistingAdmission),
            (nameof(AdvanceWindowAdmissionPolicyTests.SupersededWindowIsAdmissibleOnlyWhenItCapturedIdentity), AdvanceWindowAdmissionPolicyTests.SupersededWindowIsAdmissibleOnlyWhenItCapturedIdentity),
            (nameof(AdvanceWindowAdmissionPolicyTests.OverflowAndConflictAlwaysFailClosed), AdvanceWindowAdmissionPolicyTests.OverflowAndConflictAlwaysFailClosed),
            (nameof(AdvanceWindowAdmissionPolicyTests.SameDrainDuplicateSuppressesOnlyOneGenerationOfOneUpdate), AdvanceWindowAdmissionPolicyTests.SameDrainDuplicateSuppressesOnlyOneGenerationOfOneUpdate),
            (nameof(MainThreadStallPolicyTests.FeltFreezesAreAlwaysReported), MainThreadStallPolicyTests.FeltFreezesAreAlwaysReported),
            (nameof(MainThreadStallPolicyTests.OnlyANewSessionMaximumIsReportedBelowTheThreshold), MainThreadStallPolicyTests.OnlyANewSessionMaximumIsReportedBelowTheThreshold),
            (nameof(MainThreadStallPolicyTests.BlameSplitsOnHowMuchOfTheGapOurOwnUpdateTook), MainThreadStallPolicyTests.BlameSplitsOnHowMuchOfTheGapOurOwnUpdateTook),
            (nameof(ProjectPublicationConsistencyTests.AasOlderThanAapIsASaveWithoutABuild), ProjectPublicationConsistencyTests.AasOlderThanAapIsASaveWithoutABuild),
            (nameof(ProjectPublicationConsistencyTests.AasAtOrAfterAapIsConsistent), ProjectPublicationConsistencyTests.AasAtOrAfterAapIsConsistent),
            (nameof(ProjectPublicationConsistencyTests.MissingWriteTimesStayUnknown), ProjectPublicationConsistencyTests.MissingWriteTimesStayUnknown),
            (nameof(ProjectPublicationConsistencyTests.GraceIsGrantedOnlyOncePerLoadCycle), ProjectPublicationConsistencyTests.GraceIsGrantedOnlyOncePerLoadCycle),
            (nameof(ProjectPublicationConsistencyTests.GraceWindowIsWallClockAndCloses), ProjectPublicationConsistencyTests.GraceWindowIsWallClockAndCloses),
            (nameof(PreviewCameraChainTests.FoldsAncestorCameraThroughCommandlessScenes), PreviewCameraChainTests.FoldsAncestorCameraThroughCommandlessScenes),
            (nameof(PreviewCameraChainTests.FoldsSetThenMoveInChronologicalOrder), PreviewCameraChainTests.FoldsSetThenMoveInChronologicalOrder),
            (nameof(PreviewCameraChainTests.ResetTruncatesEarlierAncestorCamera), PreviewCameraChainTests.ResetTruncatesEarlierAncestorCamera),
            (nameof(PreviewCameraChainTests.SceneWithoutAncestorCameraKeepsTheOfficialComposition), PreviewCameraChainTests.SceneWithoutAncestorCameraKeepsTheOfficialComposition),
            (nameof(PreviewCameraChainTests.AmbiguousIncomingStopsTheCameraWalk), PreviewCameraChainTests.AmbiguousIncomingStopsTheCameraWalk),
            (nameof(PreviewCameraChainTests.IndexAndResolverInheritTheCameraFromTheProjectGraph), PreviewCameraChainTests.IndexAndResolverInheritTheCameraFromTheProjectGraph),
            (nameof(EmbeddedDiagnosticsTests.InvalidEmbeddedSceneErrorCarriesStableReason), EmbeddedDiagnosticsTests.InvalidEmbeddedSceneErrorCarriesStableReason),
            (nameof(PreviewChainTests.SameNodeChainFoldsThroughCommandlessScenes), PreviewChainTests.SameNodeChainFoldsThroughCommandlessScenes),
            (nameof(PreviewChainTests.SelectionNodeInheritsFromPreSelectionScene), PreviewChainTests.SelectionNodeInheritsFromPreSelectionScene),
            (nameof(PreviewChainTests.MergeAmbiguityStopsChain), PreviewChainTests.MergeAmbiguityStopsChain),
            (nameof(PreviewChainTests.OfficialPositionMoveTruncatesButKeepsOwnCommand), PreviewChainTests.OfficialPositionMoveTruncatesButKeepsOwnCommand),
            (nameof(PreviewChainTests.RotationAndFlipInheritThroughOlderOfficialMove), PreviewChainTests.RotationAndFlipInheritThroughOlderOfficialMove),
            (nameof(PreviewChainTests.OccupantChangeTruncatesChain), PreviewChainTests.OccupantChangeTruncatesChain),
            (nameof(PreviewChainTests.OccupantReturnAfterAbsenceStartsFresh), PreviewChainTests.OccupantReturnAfterAbsenceStartsFresh),
            (nameof(PreviewChainTests.SlotPendingSeedsOccupiedPreviewAcrossEmptyEntryScenes), PreviewChainTests.SlotPendingSeedsOccupiedPreviewAcrossEmptyEntryScenes),
            (nameof(PreviewChainTests.CurrentSceneOfficialMoveOwnsPositionOnly), PreviewChainTests.CurrentSceneOfficialMoveOwnsPositionOnly),
            (nameof(PreviewChainTests.CyclicGraphFailsClosed), PreviewChainTests.CyclicGraphFailsClosed),
            (nameof(PreviewChainTests.LineageIdentityChangesOnlyAtCharacterBoundaries), PreviewChainTests.LineageIdentityChangesOnlyAtCharacterBoundaries),
            (nameof(PreviewChainTests.HoshinoContinuousLineageResetReturnsToOrigin), PreviewChainTests.HoshinoContinuousLineageResetReturnsToOrigin),
            (nameof(PreviewChainTests.FoldComposesAbsoluteRelativeAndFlip), PreviewChainTests.FoldComposesAbsoluteRelativeAndFlip),
            (nameof(PreviewChainTests.FoldResetClearsAccumulatedChain), PreviewChainTests.FoldResetClearsAccumulatedChain),
            (nameof(PreviewChainTests.SetOnlyMarksTouchedAxesAsControlled), PreviewChainTests.SetOnlyMarksTouchedAxesAsControlled),
            (nameof(PreviewChainTests.FoldPreservesAxisControlAcrossSetMoveReset), PreviewChainTests.FoldPreservesAxisControlAcrossSetMoveReset),
            (nameof(PreviewChainTests.DirectiveIndexBuildsCanonicalLookupFromAdditionalPrompt), PreviewChainTests.DirectiveIndexBuildsCanonicalLookupFromAdditionalPrompt),
            (nameof(PreviewChainTests.DirectiveIndexIgnoresCameraResources), PreviewChainTests.DirectiveIndexIgnoresCameraResources),
            (nameof(PreviewChainTests.DirectiveIndexSkipsScenesWithMalformedDirectives), PreviewChainTests.DirectiveIndexSkipsScenesWithMalformedDirectives),
            (nameof(PreviewChainTests.DirectiveIndexFeedsResolverEndToEnd), PreviewChainTests.DirectiveIndexFeedsResolverEndToEnd),
            (nameof(PreviewChainTests.ResolverExposesEmptyOccupantForUnoccupiedSlots), PreviewChainTests.ResolverExposesEmptyOccupantForUnoccupiedSlots),
            (nameof(LiveProjectGraphTests.ComposeBuildsIsomorphicSnapshot), LiveProjectGraphTests.ComposeBuildsIsomorphicSnapshot),
            (nameof(LiveProjectGraphTests.ComposeRejectsInvalidScriptNodeGuid), LiveProjectGraphTests.ComposeRejectsInvalidScriptNodeGuid),
            (nameof(LiveProjectGraphTests.NullSceneRecordKeepsIndexAlignment), LiveProjectGraphTests.NullSceneRecordKeepsIndexAlignment),
            (nameof(LiveProjectGraphTests.RevisionChangesWhenAnySceneFingerprintChanges), LiveProjectGraphTests.RevisionChangesWhenAnySceneFingerprintChanges),
            (nameof(LiveProjectGraphTests.ChainResolvesAcrossUnsavedMiddleNodeEndToEnd), LiveProjectGraphTests.ChainResolvesAcrossUnsavedMiddleNodeEndToEnd),
            (nameof(LiveProjectGraphTests.OfficialTransitionSurvivesLiveCharacterMapping), LiveProjectGraphTests.OfficialTransitionSurvivesLiveCharacterMapping),
            (nameof(AutoProjectDiscoveryTests.ScannerPairsByNameAndReportsMissingPlayback), AutoProjectDiscoveryTests.ScannerPairsByNameAndReportsMissingPlayback),
            (nameof(AutoProjectDiscoveryTests.ScannerFailsClosedWithoutProjectsDirectory), AutoProjectDiscoveryTests.ScannerFailsClosedWithoutProjectsDirectory),
            (nameof(AutoProjectDiscoveryTests.ArbiterSelectsUniquePairAcrossProjects), AutoProjectDiscoveryTests.ArbiterSelectsUniquePairAcrossProjects),
            (nameof(AutoProjectDiscoveryTests.ArbiterFailsClosedWhenTwoProjectsShareScript), AutoProjectDiscoveryTests.ArbiterFailsClosedWhenTwoProjectsShareScript),
            (nameof(AutoProjectDiscoveryTests.ArbiterReportsNotFoundForUnknownIdentity), AutoProjectDiscoveryTests.ArbiterReportsNotFoundForUnknownIdentity),
            (nameof(AutoProjectDiscoveryTests.SnapshotCacheRevalidatesByWriteTimeAndLength), AutoProjectDiscoveryTests.SnapshotCacheRevalidatesByWriteTimeAndLength),
            (nameof(PlaybackScriptIndexTests.IndexedResolutionMatchesTheRecordScanForEveryRecord), PlaybackScriptIndexTests.IndexedResolutionMatchesTheRecordScanForEveryRecord),
            (nameof(PlaybackScriptIndexTests.EditedArchiveIsNeverServedByAStaleIndex), PlaybackScriptIndexTests.EditedArchiveIsNeverServedByAStaleIndex),
            (nameof(PlaybackScriptIndexTests.ProjectionKeyIsReadLiveOnEveryResolution), PlaybackScriptIndexTests.ProjectionKeyIsReadLiveOnEveryResolution),
            (nameof(PlaybackScriptIndexTests.IndexedResolutionReportsContractViolationsIdentically), PlaybackScriptIndexTests.IndexedResolutionReportsContractViolationsIdentically),
            (nameof(PlaybackScriptIndexTests.StampSetDecidesCacheValidationInsteadOfTheFileSystem), PlaybackScriptIndexTests.StampSetDecidesCacheValidationInsteadOfTheFileSystem),
            (nameof(PlaybackScriptIndexTests.MissingAndUnstampedPathsStillFallBackToTheFileSystem), PlaybackScriptIndexTests.MissingAndUnstampedPathsStillFallBackToTheFileSystem),
            (nameof(PlaybackScriptIndexTests.StampCaptureDescribesADataRootAndIgnoresUnrelatedPaths), PlaybackScriptIndexTests.StampCaptureDescribesADataRootAndIgnoresUnrelatedPaths),
            (nameof(PlaybackScriptIndexTests.ScannerUsesStampsForPlaybackPresence), PlaybackScriptIndexTests.ScannerUsesStampsForPlaybackPresence),
            (nameof(PlaybackScriptIndexTests.ArbitrationReadsOnlyTheMatchingProjectArchive), PlaybackScriptIndexTests.ArbitrationReadsOnlyTheMatchingProjectArchive),
            (nameof(PlaybackScriptIndexTests.ArbitrationDecisionsMatchWithAndWithoutThePreFilter), PlaybackScriptIndexTests.ArbitrationDecisionsMatchWithAndWithoutThePreFilter),
            (nameof(SlotPendingTests.CanonicalizesSlotPendingDirective), SlotPendingTests.CanonicalizesSlotPendingDirective),
            (nameof(SlotPendingTests.ExtractorAcceptsCharPendingNamespace), SlotPendingTests.ExtractorAcceptsCharPendingNamespace),
            (nameof(SlotPendingTests.ExtractorRejectsSameSlotAcrossFamilies), SlotPendingTests.ExtractorRejectsSameSlotAcrossFamilies),
            (nameof(SlotPendingTests.EmbeddedCompilerEmitsPendingFamilyProjection), SlotPendingTests.EmbeddedCompilerEmitsPendingFamilyProjection),
            (nameof(SlotPendingTests.TimelineCompilerRejectsOfficialTransitionForPendingPosition), SlotPendingTests.TimelineCompilerRejectsOfficialTransitionForPendingPosition),
            (nameof(SlotPendingTests.StoreLastWriteWinsConsumesAndExpires), SlotPendingTests.StoreLastWriteWinsConsumesAndExpires),
            (nameof(SlotPendingTests.TargetPlannerComputesFromEntryState), SlotPendingTests.TargetPlannerComputesFromEntryState),
            (nameof(PlaybackDispatchCanaryGateTests.AuthorizesExactBatchOnlyOnce), PlaybackDispatchCanaryGateTests.AuthorizesExactBatchOnlyOnce),
            (nameof(PlaybackDispatchCanaryGateTests.RejectsDriftWithoutConsumingBudget), PlaybackDispatchCanaryGateTests.RejectsDriftWithoutConsumingBudget),
            (nameof(PlaybackDispatchCanaryGateTests.AuthorizesReplayExactlyTwice), PlaybackDispatchCanaryGateTests.AuthorizesReplayExactlyTwice),
            (nameof(PlaybackDispatchCanaryGateTests.RejectsUnsafePolicy), PlaybackDispatchCanaryGateTests.RejectsUnsafePolicy),
            (nameof(ManagedDispatchScheduleTests.SceneDispatchWaitsForSameFrameLateUpdate), ManagedDispatchScheduleTests.SceneDispatchWaitsForSameFrameLateUpdate),
            (nameof(ManagedDispatchScheduleTests.NonSceneDispatchKeepsNextUpdateBoundary), ManagedDispatchScheduleTests.NonSceneDispatchKeepsNextUpdateBoundary),
            (nameof(PreviewChainApplyPolicyTests.SceneCommandDispatchWithCapabilityAppliesChain), PreviewChainApplyPolicyTests.SceneCommandDispatchWithCapabilityAppliesChain),
            (nameof(PreviewChainApplyPolicyTests.OtherStagesNeverApplyChainEvenWhenEnabled), PreviewChainApplyPolicyTests.OtherStagesNeverApplyChainEvenWhenEnabled),
            (nameof(PreviewChainApplyPolicyTests.DisabledCapabilityNeverAppliesChain), PreviewChainApplyPolicyTests.DisabledCapabilityNeverAppliesChain),
            (nameof(PreviewChainApplyPolicyTests.PolicySignatureIsModeIndependentByDesign), PreviewChainApplyPolicyTests.PolicySignatureIsModeIndependentByDesign),
            (nameof(ModWorkspaceCompatibilityTests.ReadyRequiresExactSourcesAndCapabilities), ModWorkspaceCompatibilityTests.ReadyRequiresExactSourcesAndCapabilities),
            (nameof(ModWorkspaceCompatibilityTests.OlderPluginAndMissingCapabilitiesFailClosed), ModWorkspaceCompatibilityTests.OlderPluginAndMissingCapabilitiesFailClosed),
            (nameof(ModWorkspaceCompatibilityTests.SourceRevisionDriftFailsClosed), ModWorkspaceCompatibilityTests.SourceRevisionDriftFailsClosed),
            (nameof(ModWorkspaceCompatibilityTests.FutureSchemaIsReadableMetadataButNeverExecutable), ModWorkspaceCompatibilityTests.FutureSchemaIsReadableMetadataButNeverExecutable),
            (nameof(ModWorkspaceCompatibilityTests.RejectsNonCanonicalVersionsAndDuplicateCapabilities), ModWorkspaceCompatibilityTests.RejectsNonCanonicalVersionsAndDuplicateCapabilities),
            (nameof(ModWorkspaceCompatibilityTests.MigrationPipelinePreservesImmutableIdentity), ModWorkspaceCompatibilityTests.MigrationPipelinePreservesImmutableIdentity),
            (nameof(JsonModWorkspaceStoreTests.RoundTripsAndRotatesAtomicBackup), JsonModWorkspaceStoreTests.RoundTripsAndRotatesAtomicBackup),
            (nameof(JsonModWorkspaceStoreTests.CorruptPrimaryRecoversValidatedBackup), JsonModWorkspaceStoreTests.CorruptPrimaryRecoversValidatedBackup),
            (nameof(JsonModWorkspaceStoreTests.FutureSchemaIsAuthoritativeAndNeverFallsBack), JsonModWorkspaceStoreTests.FutureSchemaIsAuthoritativeAndNeverFallsBack),
            (nameof(JsonModWorkspaceStoreTests.RefusesToOverwriteFutureSchema), JsonModWorkspaceStoreTests.RefusesToOverwriteFutureSchema),
            (nameof(JsonModWorkspaceStoreTests.SourceMismatchLoadsReadOnlyAndDoesNotUseBackup), JsonModWorkspaceStoreTests.SourceMismatchLoadsReadOnlyAndDoesNotUseBackup),
            (nameof(JsonModWorkspaceStoreTests.RejectsInvalidKeysAndManifestFolderMismatch), JsonModWorkspaceStoreTests.RejectsInvalidKeysAndManifestFolderMismatch),
            (nameof(JsonModWorkspaceStoreTests.RejectsUnknownAndDuplicateCurrentProperties), JsonModWorkspaceStoreTests.RejectsUnknownAndDuplicateCurrentProperties),
            (nameof(JsonModWorkspaceStoreTests.MissingWorkspaceDoesNotCreateRoot), JsonModWorkspaceStoreTests.MissingWorkspaceDoesNotCreateRoot),
            (nameof(JsonModWorkspaceStoreTests.ContendingWriterCannotReplaceManifest), JsonModWorkspaceStoreTests.ContendingWriterCannotReplaceManifest),
            (nameof(JsonCommandWorkspaceStoreTests.RoundTripsTimelineAndProjectionWithAtomicBackups), JsonCommandWorkspaceStoreTests.RoundTripsTimelineAndProjectionWithAtomicBackups),
            (nameof(JsonCommandWorkspaceStoreTests.CorruptTimelinePrimaryRecoversValidatedBackup), JsonCommandWorkspaceStoreTests.CorruptTimelinePrimaryRecoversValidatedBackup),
            (nameof(JsonCommandWorkspaceStoreTests.FutureTimelineIsAuthoritativeAndCannotBeOverwritten), JsonCommandWorkspaceStoreTests.FutureTimelineIsAuthoritativeAndCannotBeOverwritten),
            (nameof(JsonCommandWorkspaceStoreTests.RejectsUnknownDuplicateAndNumericEnumJson), JsonCommandWorkspaceStoreTests.RejectsUnknownDuplicateAndNumericEnumJson),
            (nameof(JsonCommandWorkspaceStoreTests.CorruptProjectionPrimaryRecoversReproducibleBackup), JsonCommandWorkspaceStoreTests.CorruptProjectionPrimaryRecoversReproducibleBackup),
            (nameof(JsonCommandWorkspaceStoreTests.RejectsHandcraftedProjectionThatCannotBeRecompiled), JsonCommandWorkspaceStoreTests.RejectsHandcraftedProjectionThatCannotBeRecompiled),
            (nameof(JsonCommandWorkspaceStoreTests.FutureProjectionIsAuthoritativeAndCannotBeOverwritten), JsonCommandWorkspaceStoreTests.FutureProjectionIsAuthoritativeAndCannotBeOverwritten),
            (nameof(JsonCommandWorkspaceStoreTests.MissingAndInvalidKeysNeverCreateWorkspaceRoot), JsonCommandWorkspaceStoreTests.MissingAndInvalidKeysNeverCreateWorkspaceRoot),
            (nameof(JsonCommandWorkspaceStoreTests.ContendingWriterCannotReplaceTimeline), JsonCommandWorkspaceStoreTests.ContendingWriterCannotReplaceTimeline)
        };

        int failures = 0;
        foreach ((string name, Action run) in tests)
        {
            try
            {
                run();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {name}: {ex.Message}");
            }
        }

        Console.WriteLine($"TEST SUMMARY total={tests.Length} failed={failures}");
        if (failures != 0)
        {
            return 1;
        }

        if (args.Length == 2
            && string.Equals(args[0], "--aap-directory", StringComparison.Ordinal))
        {
            return AapSmokeVerifier.VerifyDirectory(args[1]);
        }

        if (args.Length == 2
            && string.Equals(args[0], "--aas-directory", StringComparison.Ordinal))
        {
            return AasSmokeVerifier.VerifyDirectory(args[1]);
        }

        if (args.Length == 3
            && string.Equals(args[0], "--mapping-directories", StringComparison.Ordinal))
        {
            return MappingSmokeVerifier.VerifyDirectories(args[1], args[2]);
        }

        if (args.Length == 3
            && string.Equals(
                args[0],
                "--command-workspace-directories",
                StringComparison.Ordinal))
        {
            return CommandWorkspaceSmokeVerifier.VerifyDirectories(
                args[1],
                args[2]);
        }

        if (args.Length == 2
            && string.Equals(args[0], "--bench-graph-size", StringComparison.Ordinal))
        {
            return ArbitrationBenchmark.ReportGraphSizes(args[1]);
        }

        if (args.Length == 3
            && string.Equals(args[0], "--check-mod-abi", StringComparison.Ordinal))
        {
            return ModAbiCheck.Run(args[1], args[2]);
        }

        if (args.Length is >= 2 and <= 4
            && string.Equals(args[0], "--bench-arbitration", StringComparison.Ordinal))
        {
            return ArbitrationBenchmark.Run(
                args[1],
                args.Length >= 3 ? args[2] : null,
                args.Length >= 4 && int.TryParse(args[3], out int parsed) ? parsed : 3);
        }

        if (args.Length != 0)
        {
            Console.Error.WriteLine(
                "Usage: AzureArchive.VideoTools.Tests "
                + "[--aap-directory <path> | --aas-directory <path> "
                + "| --mapping-directories <project-path> <save-path> "
                + "| --command-workspace-directories <project-path> <save-path> "
                + "| --write-command-proof-workspace <aap> <aas> <workspace-root> <record-index> <directive> "
                + "| --aas-tail <save-path> "
                + "| --aap-character-positions <aap-or-directory> "
                + "| --bench-arbitration <data-root> [pair-name] [repetitions] "
                + "| --check-mod-abi <mods-root> <shared-library-path> "
                + "| --resolve-scene <aap> <aas> <sha256> <length> <lines>]");
            return 2;
        }

        return 0;
    }
}
