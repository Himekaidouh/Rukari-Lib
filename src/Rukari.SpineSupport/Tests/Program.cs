using Rukari.SpineSupport.Spines;
using Rukari.SpineSupport.Tests;

// The animation names below are the ones a real lobby spine carries (CH0345_home, exported 3.8.75):
// Idle_01, Idle_01_R, Idle_Light_01_R, Idle_Light_02_R, Start_Idle_01, Talk_01..05_M/_A,
// Pat_01_M, Pat_02_M, PatEnd_01_M/_A, Pinch_01_M, Pinch_02_M, PinchEnd_01_M/_A,
// Look_01_M, Look_02_M, LookEnd_01_M/_A.
string[] Lobby(string[] extra) => new[] { "Idle_01", "Start_Idle_01", "Idle_01_R", "Dummy", "Eye_Close_01" }
    .Concat(extra).ToArray();

var checks = new (string Name, Action Run)[]
{
    ("ACatalogWithoutTheEntranceIsNotALobby", () =>
    {
        // The whole feature must stay out of the way of ordinary character spines.
        var names = new[] { "Idle_01", "Idle_02", "Talk_01_M" };
        True(!SpineLobbyAnimationPolicy.IsLobbyCatalog(names));
        True(SpineLobbyAnimationPolicy.BuildPlan(names, "Talk_01_M", requestedLoop: true) is null);
    }),
    ("EntranceIsOneShotAndAsksThePlaybackToAdvance", () =>
    {
        var plan = SpineLobbyAnimationPolicy.BuildPlan(Lobby(Array.Empty<string>()), "Start_Idle_01", requestedLoop: true);
        True(plan is not null, "A lobby entrance must resolve.");
        Equal("Start_Idle_01", plan!.PrimaryAnimationName);
        True(!plan.Loop, "The entrance must not loop.");
        True(plan.ReturnToIdle, "The entrance must fade back to Idle_01.");
        Equal(1f, plan.FadeSeconds);
        True(plan.AdvanceWhenFinished, "The entrance is what the playback should move on from.");
    }),
    ("ReactionFamiliesAreOneShotAndNeverAdvance", () =>
    {
        // Pat/PatEnd/Pinch/Look are the touch reactions; Talk is the same shape.
        foreach (string reaction in new[]
                 {
                     "Talk_01_M", "Talk_05_A", "Pat_01_M", "Pat_02_M", "PatEnd_01_M", "PatEnd_01_A",
                     "Pinch_01_M", "Pinch_02_M", "PinchEnd_01_A", "Look_01_M", "Look_02_M", "LookEnd_01_M",
                 })
        {
            var plan = SpineLobbyAnimationPolicy.BuildPlan(Lobby(new[] { reaction }), reaction, requestedLoop: true);
            True(plan is not null, $"{reaction} must resolve in a lobby.");
            True(!plan!.Loop, $"{reaction} must not loop.");
            True(plan.ReturnToIdle, $"{reaction} must fade back to Idle_01.");
            Equal(0.2f, plan.FadeSeconds);
            True(!plan.AdvanceWhenFinished, $"{reaction} must not advance the dialogue; the touch wait does that.");
        }
    }),
    ("PinchAndPatEndPairWithTheirOtherTrack", () =>
    {
        var names = Lobby(new[] { "Pat_01_M", "Pat_01_A", "PatEnd_01_M", "PatEnd_01_A", "PinchEnd_01_M", "PinchEnd_01_A" });
        var fromM = SpineLobbyAnimationPolicy.BuildPlan(names, "Pat_01_M", requestedLoop: true);
        Equal("Pat_01_M", fromM!.PrimaryAnimationName);
        Equal("Pat_01_A", fromM.CompanionAnimationName);

        // Selecting the companion still plays the pair, with the M track as the primary — as before for Talk.
        var fromA = SpineLobbyAnimationPolicy.BuildPlan(names, "PatEnd_01_A", requestedLoop: true);
        Equal("PatEnd_01_M", fromA!.PrimaryAnimationName);
        Equal("PatEnd_01_A", fromA.CompanionAnimationName);

        // A lone track keeps working: Pat_02_M has no _A in the real asset.
        var alone = SpineLobbyAnimationPolicy.BuildPlan(Lobby(new[] { "Pat_02_M" }), "Pat_02_M", requestedLoop: true);
        Equal("Pat_02_M", alone!.PrimaryAnimationName);
        True(alone.CompanionAnimationName is null);
    }),
    ("OrdinaryLobbyAnimationsKeepTheRequestedLoop", () =>
    {
        var names = Lobby(new[] { "Idle_Light_01_R" });
        var looping = SpineLobbyAnimationPolicy.BuildPlan(names, "Idle_01_R", requestedLoop: true);
        True(looping!.Loop, "Idle_01_R is not one-shot.");
        True(!looping.ReturnToIdle);
        True(!looping.AdvanceWhenFinished);

        var held = SpineLobbyAnimationPolicy.BuildPlan(names, "Idle_Light_01_R", requestedLoop: false);
        True(!held!.Loop, "A caller that asked for no loop still gets no loop.");
    }),
    ("ReactionDetectionDoesNotOverreach", () =>
    {
        foreach (string yes in new[] { "Talk_01_M", "Talk_01_A", "Pat_01_M", "PatEnd_01_M", "Pinch_02_A", "LookEnd_01_M" })
        {
            True(SpineLobbyAnimationPolicy.IsReactionAnimation(yes), $"{yes} must be a reaction.");
        }

        foreach (string no in new[]
                 {
                     "Idle_01", "Idle_01_R", "Start_Idle_01", "Dummy", "Eye_Close_01", "Talk_01", "Talk_01_R",
                     "Talk01_M", "Pat_01_X", "Talk_x1_M", "Talk_01_M_", "TalkEnd_01_M_extra",
                 })
        {
            True(!SpineLobbyAnimationPolicy.IsReactionAnimation(no), $"{no} must not be a reaction.");
        }
    }),
    ("TouchRectCentresOnTheMarkerAndKeepsTheAuthoredSize", () =>
    {
        var rect = SpineLobbyTouchArea.Around(markerX: 900, markerY: 500, authoredWidth: 240, authoredHeight: 180,
            screenWidth: 2560, screenHeight: 1440);
        True(rect is not null);
        Equal(780, rect!.Value.X);
        Equal(410, rect.Value.Y);
        Equal(240, rect.Value.Width);
        Equal(180, rect.Value.Height);
    }),
    ("TouchRectIsClampedIntoTheScreen", () =>
    {
        var left = SpineLobbyTouchArea.Around(10, 10, 200, 200, 1920, 1080);
        Equal(0, left!.Value.X);
        Equal(0, left.Value.Y);

        var right = SpineLobbyTouchArea.Around(1915, 1075, 200, 200, 1920, 1080);
        Equal(1720, right!.Value.X);
        Equal(880, right.Value.Y);
    }),
    ("TouchRectFallsBackToADefaultSquareAndRefusesNonsense", () =>
    {
        var fallback = SpineLobbyTouchArea.Around(500, 500, authoredWidth: 0, authoredHeight: -20,
            screenWidth: 1280, screenHeight: 720);
        Equal(SpineLobbyTouchArea.DefaultSize, fallback!.Value.Width);
        Equal(SpineLobbyTouchArea.DefaultSize, fallback.Value.Height);

        // A marker that is not on screen means "keep the rectangle the author wrote", never a corner rect.
        True(SpineLobbyTouchArea.Around(-1, 500, 200, 200, 1280, 720) is null);
        True(SpineLobbyTouchArea.Around(500, 721, 200, 200, 1280, 720) is null);
        True(SpineLobbyTouchArea.Around(500, 500, 200, 200, 0, 720) is null);
    }),
    ("OnlyProjectAudioIsGuardedAndGameIdentifiersAreLeftAlone", () =>
    {
        // What a converted lobby drags in: the event's own file name, or the event name itself.
        True(ProjectAudioPolicy.IsProjectOnlyIdentifier("CH0335_MemorialLobby_1_1.wav"));
        True(ProjectAudioPolicy.IsProjectOnlyIdentifier("CH0335_MemorialLobby_5_2.OGG"));
        True(ProjectAudioPolicy.IsProjectOnlyIdentifier("sound/CH0335_MemorialLobby_3_1"));
        True(ProjectAudioPolicy.IsProjectOnlyIdentifier("  Sound/CH0345_MemorialLobby_1_1  "));
        // What a line keeps after its audio is gone: the editor writes the binding as a bare GUID, and the file it
        // used to name lived in the project's own voices folder.
        True(ProjectAudioPolicy.IsProjectOnlyIdentifier("c232433e-f2c6-4555-880a-ed751942472d"));
        True(ProjectAudioPolicy.IsProjectOnlyIdentifier("052B870B-1472-4135-87BB-82C1886F8188"));
        True(ProjectAudioPolicy.IsProjectOnlyIdentifier("voices/052b870b-1472-4135-87bb-82c1886f8188.ogg"));

        // What the game and the project really ship: no extension, no Sound/ prefix, no GUID shape. These must never
        // be touched, because suppressing them would hide a genuinely missing sound effect or voice line.
        True(!ProjectAudioPolicy.IsProjectOnlyIdentifier("SE_Bell_04"));
        True(!ProjectAudioPolicy.IsProjectOnlyIdentifier("CH0335_MemorialLobby_1_1"));
        True(!ProjectAudioPolicy.IsProjectOnlyIdentifier("aavt/voice/rukari-import/abc"));
        True(!ProjectAudioPolicy.IsProjectOnlyIdentifier("dialogue/hello"));
        // A GUID-shaped string is 36 characters in the 8-4-4-4-12 layout, nothing else.
        True(!ProjectAudioPolicy.IsProjectOnlyIdentifier("c232433e-f2c6-4555-880a-ed75194247"));
        True(!ProjectAudioPolicy.IsProjectOnlyIdentifier("c232433e-f2c6-4555-880a-ed751942472z"));
        True(!ProjectAudioPolicy.IsProjectOnlyIdentifier("c232433ef2c64555880aed751942472d0000"));
        True(!ProjectAudioPolicy.IsProjectOnlyIdentifier(""));
        True(!ProjectAudioPolicy.IsProjectOnlyIdentifier("   "));
        True(!ProjectAudioPolicy.IsProjectOnlyIdentifier(null));
    }),
    ("TransitionsBlendIntoIdleButNeverIntoThemselves", () =>
    {
        Equal(0.2f, LobbyTransitionMixPolicy.MixFor("Talk_01_M", "Talk_02_M", 0.2f, 0.35f));
        Equal(0.35f, LobbyTransitionMixPolicy.MixFor("Pat_01_M", "Idle_01", 0.2f, 0.35f));
        Equal(0.35f, LobbyTransitionMixPolicy.MixFor("Start_Idle_01", "Idle_01", 0.2f, 0.35f));
        Equal(0f, LobbyTransitionMixPolicy.MixFor("Idle_01", "Idle_01", 0.2f, 0.35f));
        Equal(0.2f, LobbyTransitionMixPolicy.MixFor(null, "Talk_01_M", 0.2f, 0.35f));
        Equal(0f, LobbyTransitionMixPolicy.MixFor("Talk_01_M", "Talk_02_M", 0f, 0.35f));
        Equal(0.2f, LobbyTransitionMixPolicy.MixFor("Look_01_M", "Idle_01", 0.2f, 0f));
        Equal(1f, LobbyTransitionMixPolicy.MixFor("Talk_01_M", "Talk_02_M", 9f, 0.35f));
    }),
    (nameof(SpineAnimationClassifierTests.TheRealLobbyListIsSplitTheWayTheAssetIsMade),
        SpineAnimationClassifierTests.TheRealLobbyListIsSplitTheWayTheAssetIsMade),
    (nameof(SpineAnimationClassifierTests.SubsetAndFullBodyAnimationsAreSeparated),
        SpineAnimationClassifierTests.SubsetAndFullBodyAnimationsAreSeparated),
    (nameof(SpineAnimationClassifierTests.UnknownNamesStayAvailable),
        SpineAnimationClassifierTests.UnknownNamesStayAvailable),
    (nameof(SpineAnimationClassifierTests.NumberedDifferentialsAreRecognisedButNeverByAccident),
        SpineAnimationClassifierTests.NumberedDifferentialsAreRecognisedButNeverByAccident),
    (nameof(SpineAnimationClassifierTests.WritingAnOverlayReplacesItsOwnTrackAndNothingElse),
        SpineAnimationClassifierTests.WritingAnOverlayReplacesItsOwnTrackAndNothingElse),
    (nameof(LobbyEntranceAdvancePolicyTests.OnlyTheCapturedEntranceEntryMayReportCompletion),
        LobbyEntranceAdvancePolicyTests.OnlyTheCapturedEntranceEntryMayReportCompletion),
    (nameof(LobbyEntranceAdvancePolicyTests.ClearedOrReplacedTracksNeverImpersonateACompletedEntrance),
        LobbyEntranceAdvancePolicyTests.ClearedOrReplacedTracksNeverImpersonateACompletedEntrance),
    (nameof(LobbyEntranceAdvancePolicyTests.InvalidTrackTimesDoNotAuthorizeOrKeepAnEntranceWatch),
        LobbyEntranceAdvancePolicyTests.InvalidTrackTimesDoNotAuthorizeOrKeepAnEntranceWatch),
    (nameof(LobbyEntranceAdvancePolicyTests.ALongVoiceNeverAcquiresAnEntranceTimeout),
        LobbyEntranceAdvancePolicyTests.ALongVoiceNeverAcquiresAnEntranceTimeout),
    (nameof(LobbyEntranceAdvancePolicyTests.AnOwnedVoiceTailDelayIsRespectedWithoutTheNativeVoiceFlag),
        LobbyEntranceAdvancePolicyTests.AnOwnedVoiceTailDelayIsRespectedWithoutTheNativeVoiceFlag),
    (nameof(LobbyEntranceAdvancePolicyTests.AVoiceThatEndedBeforeTheEntranceStillKeepsOfficialAutoWaiting),
        LobbyEntranceAdvancePolicyTests.AVoiceThatEndedBeforeTheEntranceStillKeepsOfficialAutoWaiting),
    (nameof(LobbyEntranceAdvancePolicyTests.AReportedVoiceWithNoAudibleClipIsNotProofOfFailure),
        LobbyEntranceAdvancePolicyTests.AReportedVoiceWithNoAudibleClipIsNotProofOfFailure),
    (nameof(LobbyEntranceAdvancePolicyTests.AnUnavailableAudioManagerDoesNotTurnIntoAConfirmedSilentLine),
        LobbyEntranceAdvancePolicyTests.AnUnavailableAudioManagerDoesNotTurnIntoAConfirmedSilentLine),
    (nameof(LobbyEntranceAdvancePolicyTests.OnlyTheSameSilentLineCanAdvanceAfterItsEntrance),
        LobbyEntranceAdvancePolicyTests.OnlyTheSameSilentLineCanAdvanceAfterItsEntrance),
    (nameof(LobbyEntranceAdvancePolicyTests.DisablingEntranceAdvanceAlsoKeepsSilentLinesWithTheOfficialPlayer),
        LobbyEntranceAdvancePolicyTests.DisablingEntranceAdvanceAlsoKeepsSilentLinesWithTheOfficialPlayer),
};
int failed = 0;
foreach ((string name, Action run) in checks)
{
    try
    {
        run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"FAIL {name}: {ex.Message}");
    }
}

Console.WriteLine($"{checks.Length - failed}/{checks.Length} independent spine support tests passed.");
return failed == 0 ? 0 : 1;

static void True(bool condition, string message = "Expected true.")
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
}
