using Rukari.SpineSupport.Spines;

namespace Rukari.SpineSupport.Tests;

/// <summary>
/// The overlay list is built from what an animation keys, not from what it is called. These cases
/// are the real animation list of an imported Kei lobby plus the shapes a hand-made overlay has.
/// </summary>
internal static class SpineAnimationClassifierTests
{
    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
        }
    }

    private static SpineAnimationKeyProfile Profile(
        string name,
        int bones = 0,
        int slots = 0,
        int timelines = 1,
        string[]? slotNames = null)
    {
        var boneNames = new List<string>();
        for (int index = 0; index < bones; index++) boneNames.Add("Bone" + index);
        var names = slotNames ?? Enumerable.Range(0, slots).Select(index => "Slot" + index).ToArray();
        return new SpineAnimationKeyProfile(name, boneNames, names, false, false, timelines);
    }

    public static void NumberedDifferentialsAreRecognisedButNeverByAccident()
    {
        // The author's convention: a purely numbered name is a unified expression differential the
        // official card drives, and there are a lot of them (the story skeleton has 100 animations,
        // most of them numbered), so the page counts them and hides them behind a switch.
        foreach (string name in new[] { "01", "02", "1", "10", "02_R", "07_M" })
        {
            True(SpineAnimationClassifier.IsDifferentialName(name), $"'{name}' should count as a differential.");
        }

        foreach (string name in new[] { "Add_Arm", "Idle_01", "Talk_01_M", "Action_GazeCamera", "01Arm", "0x1", "", "0123456789_" })
        {
            True(!SpineAnimationClassifier.IsDifferentialName(name), $"'{name}' must never be filtered out.");
        }

        // When the switch is off the row still says what it is instead of claiming to be an overlay.
        SpineAnimationVerdict differential = SpineAnimationClassifier.Classify(
            Profile("01", bones: 6, slots: 20),
            146,
            53,
            null,
            Array.Empty<string>());
        True(differential.Differential, "A numbered name must be flagged on the verdict.");
        True(
            SpineAnimationClassifier.Describe(differential).Contains("差分"),
            "A differential row must say so when it is shown.");
    }

    public static void WritingAnOverlayReplacesItsOwnTrackAndNothingElse()
    {
        // The author's line already carries a character command and an overlay on track 21.
        const string text = "#aavt;char;3;set;x=100\n#aavt;spine;3;Action_GazeCamera;track=21;loop=false;hold=true\n正文台词";

        // Writing the same slot and track replaces that directive in place...
        string replaced = SpineOverlayLineEditor.Upsert(
            text,
            SpineOverlayLineEditor.BuildPlayLine(3, "Action_GazePhone", 21, false, true),
            3,
            21);
        True(
            replaced.Contains("Action_GazePhone", StringComparison.Ordinal)
            && !replaced.Contains("Action_GazeCamera", StringComparison.Ordinal),
            "The directive on that track must be replaced, not duplicated.");
        True(
            replaced.Contains("#aavt;char;3;set;x=100", StringComparison.Ordinal)
            && replaced.Contains("正文台词", StringComparison.Ordinal),
            "Other lines must survive untouched.");
        Equal(3, replaced.Split('\n').Length);

        // ...a different track is a different resource and is appended...
        string appended = SpineOverlayLineEditor.Upsert(
            text,
            SpineOverlayLineEditor.BuildPlayLine(3, "Eye_Blink", 20, false, true),
            3,
            20);
        Equal(4, appended.Split('\n').Length);
        True(appended.Contains("Action_GazeCamera", StringComparison.Ordinal), "The other track must stay.");

        // ...a directive that never named a track is still the default track's directive...
        string defaultTrack = SpineOverlayLineEditor.Upsert(
            "#aavt;spine;3;Add_Arm",
            SpineOverlayLineEditor.BuildClearLine(3, 20),
            3,
            20);
        True(
            defaultTrack.Contains("clear", StringComparison.Ordinal)
            && !defaultTrack.Contains("Add_Arm", StringComparison.Ordinal),
            "A clear must replace the play on the same (default) track.");

        // ...and another character's directive is never touched.
        string otherSlot = SpineOverlayLineEditor.Upsert(
            text,
            SpineOverlayLineEditor.BuildPlayLine(4, "Eye_Blink", 21, false, true),
            4,
            21);
        True(
            otherSlot.Contains("Action_GazeCamera", StringComparison.Ordinal)
            && otherSlot.Contains("Eye_Blink", StringComparison.Ordinal),
            "Another slot's overlay must be left alone.");

        // Line terminators are the author's, not ours.
        string crlf = SpineOverlayLineEditor.Upsert(
            "#aavt;char;2;reset\r\n正文",
            SpineOverlayLineEditor.BuildPlayLine(2, "Add_Arm", 20, false, true),
            2,
            20);
        True(crlf.Contains("\r\n", StringComparison.Ordinal), "CRLF text must stay CRLF.");
        True(crlf.EndsWith("#aavt;spine;2;Add_Arm;track=20;loop=false;hold=true", StringComparison.Ordinal),
            "An appended line must land after the existing ones.");
    }

    public static void TheRealLobbyListIsSplitTheWayTheAssetIsMade()
    {
        // The imported lobby carries 27 animations over ~200 bones, and none of them is an overlay.
        var baseProfile = Profile("Idle_01", bones: 200, slots: 150);
        var verdicts = new List<SpineAnimationVerdict>
        {
            SpineAnimationClassifier.Classify(
                Profile("Dummy", timelines: 0), 200, 150, baseProfile, Array.Empty<string>()),
            SpineAnimationClassifier.Classify(
                Profile("Eye_Close_01", slots: 2, slotNames: new[] { "Eye_L", "Eye_R" }),
                200, 150, baseProfile, Array.Empty<string>()),
            SpineAnimationClassifier.Classify(baseProfile, 200, 150, baseProfile, Array.Empty<string>()),
            SpineAnimationClassifier.Classify(
                Profile("Idle_01_R", bones: 200, slots: 150), 200, 150, baseProfile, Array.Empty<string>()),
            SpineAnimationClassifier.Classify(
                Profile("Start_Idle_01", bones: 200, slots: 150), 200, 150, baseProfile, Array.Empty<string>()),
            SpineAnimationClassifier.Classify(
                Profile("Talk_01_M", bones: 120, slots: 40), 200, 150, baseProfile, Array.Empty<string>()),
            SpineAnimationClassifier.Classify(
                Profile("Talk_01_A", slots: 5), 200, 150, baseProfile, Array.Empty<string>()),
            SpineAnimationClassifier.Classify(
                Profile("PatEnd_01_M", bones: 120, slots: 40), 200, 150, baseProfile, Array.Empty<string>())
        };

        Equal(SpineAnimationRole.Placeholder, verdicts[0].Role);   // Dummy keys nothing
        Equal(SpineAnimationRole.Placeholder, verdicts[1].Role);   // eye slots only = system
        Equal(SpineAnimationRole.Base, verdicts[2].Role);
        Equal(SpineAnimationRole.Base, verdicts[3].Role);          // Idle_01_R is an idle variant
        Equal(SpineAnimationRole.Base, verdicts[4].Role);          // Start_Idle_01 is the entrance
        Equal(SpineAnimationRole.Reaction, verdicts[5].Role);
        Equal(SpineAnimationRole.Reaction, verdicts[6].Role);
        Equal(SpineAnimationRole.Reaction, verdicts[7].Role);      // the End variant is still a reaction
        foreach (SpineAnimationVerdict verdict in verdicts)
        {
            True(
                verdict.Role != SpineAnimationRole.OverlayCandidate,
                $"{verdict.Name} must not be offered as an overlay ({verdict.Reason})");
        }
    }

    public static void SubsetAndFullBodyAnimationsAreSeparated()
    {
        var baseProfile = new SpineAnimationKeyProfile(
            "Idle_01",
            new[] { "Root", "Spine", "Arm_L", "Arm_R", "Head" },
            new[] { "Body" },
            false,
            false,
            3);

        // A hand-made overlay: a couple of bones, nothing else.
        SpineAnimationVerdict overlay = SpineAnimationClassifier.Classify(
            new SpineAnimationKeyProfile(
                "Arm_Raise",
                new[] { "Arm_L", "Arm_R" },
                Array.Empty<string>(),
                false,
                false,
                2),
            totalBones: 100,
            totalSlots: 40,
            baseProfile,
            new[] { "Arm_L" });

        Equal(SpineAnimationRole.OverlayCandidate, overlay.Role);
        Equal(2, overlay.BoneCount);
        Equal(2, overlay.SharedWithBaseCount);   // both bones also belong to the idle loop
        Equal(1, overlay.IkDrivenCount);         // one is IK driven, so that key is discarded
        string label = SpineAnimationClassifier.Describe(overlay);
        True(label.Contains("可叠加", StringComparison.Ordinal), label);
        True(label.Contains("与Idle重叠2", StringComparison.Ordinal), label);
        True(label.Contains("IK驱动1", StringComparison.Ordinal), label);

        // A pose that keys the whole skeleton replaces the body instead of layering onto it.
        SpineAnimationVerdict replacement = SpineAnimationClassifier.Classify(
            Profile("Extra_Pose", bones: 90, slots: 40),
            totalBones: 100,
            totalSlots: 40,
            baseProfile,
            Array.Empty<string>());
        Equal(SpineAnimationRole.Replacement, replacement.Role);

        // A partial animation with no bones at all is still an overlay when its slots are not system ones.
        SpineAnimationVerdict prop = SpineAnimationClassifier.Classify(
            new SpineAnimationKeyProfile("Prop_Swap", Array.Empty<string>(), new[] { "Prop" }, false, false, 1),
            100,
            40,
            baseProfile,
            Array.Empty<string>());
        Equal(SpineAnimationRole.OverlayCandidate, prop.Role);
    }

    public static void UnknownNamesStayAvailable()
    {
        // Compatibility rule: a name we cannot explain is still offered, never silently hidden.
        True(!SpineAnimationClassifier.IsBaseName("Handmade_01"), "not a base name");
        True(!SpineAnimationClassifier.IsReactionName("Handmade_01"), "not a reaction");
        True(!SpineAnimationClassifier.IsReactionName("PatEnd_01"), "no track letter");
        True(!SpineAnimationClassifier.IsReactionName("TalkEnd_M"), "no number");

        // One digit is accepted on purpose: this mirrors SpineLobbyAnimationPolicy, which requires
        // an underscore plus at least one digit rather than exactly two. Two digits stay the
        // convention because they sort, but a single digit must not hide an animation from us.
        True(SpineAnimationClassifier.IsReactionName("Talk_1_M"), "one digit is still a family name");

        SpineAnimationVerdict verdict = SpineAnimationClassifier.Classify(
            Profile("Handmade_01", bones: 3, slots: 1),
            100,
            40,
            null,
            Array.Empty<string>());
        Equal(SpineAnimationRole.OverlayCandidate, verdict.Role);
        Equal(0, verdict.SharedWithBaseCount);   // no base profile means no clash to report
    }
}
