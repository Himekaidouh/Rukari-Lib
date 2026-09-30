using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Spines;

namespace AzureArchive.VideoTools.Tests;

/// <summary>
/// The <c>#aavt;spine</c> family (2026-09-21): the syntax the author types, the canonical form the
/// lease compares, and the resource rules that keep an overlay and a character transform from
/// cancelling each other out.
/// </summary>
internal static class SpineOverlayTests
{
    public static void ParsesPlayAndClearWithStickyDefaults()
    {
        var parser = new SpineOverlayDirectiveParser();
        SpineOverlayCommand play = AssertEx.NotNull(parser.Parse("#spine;3;Add_Arm").Value);
        AssertEx.Equal(3, play.PublicSlot);
        AssertEx.Equal(SpineOverlayOperation.Play, play.Operation);
        AssertEx.Equal("Add_Arm", play.AnimationName);
        AssertEx.Equal(SpineOverlayTrackPolicy.DefaultTrack, play.TrackIndex);
        AssertEx.Equal(SpineOverlayTrackPolicy.FirstReservedTrack, play.TrackIndex);
        AssertEx.Equal(0, play.MixMilliseconds);
        AssertEx.Equal(SpineOverlayTrackPolicy.DefaultFadeMilliseconds, play.FadeMilliseconds);
        AssertEx.True(play.Loop, "An overlay must stay put until it is cleared, so the default loops.");
        AssertEx.Equal(SpineOverlayBlend.Replace, play.Blend);

        SpineOverlayCommand explicitValues = AssertEx.NotNull(parser.Parse(
            "#spine;5;Add_Hair;track=21;mix=150;fade=400;loop=false;blend=add").Value);
        AssertEx.Equal(5, explicitValues.PublicSlot);
        AssertEx.Equal("Add_Hair", explicitValues.AnimationName);
        AssertEx.Equal(21, explicitValues.TrackIndex);
        AssertEx.Equal(150, explicitValues.MixMilliseconds);
        AssertEx.Equal(400, explicitValues.FadeMilliseconds);
        AssertEx.False(explicitValues.Loop);
        AssertEx.Equal(SpineOverlayBlend.Add, explicitValues.Blend);

        SpineOverlayCommand clear = AssertEx.NotNull(parser.Parse("#spine;3;clear").Value);
        AssertEx.Equal(SpineOverlayOperation.Clear, clear.Operation);
        AssertEx.Equal(string.Empty, clear.AnimationName);
        AssertEx.Equal(3, clear.PublicSlot);
        AssertEx.False(clear.Hold.HasValue, "Saying nothing about holding must stay 'provider decides'.");

        // The hold switch is opt-in per directive: a one-shot that must keep its last frame says so.
        SpineOverlayCommand held = AssertEx.NotNull(
            parser.Parse("#spine;3;Add_Arm;loop=false;hold=true").Value);
        AssertEx.False(held.Loop);
        AssertEx.Equal(true, held.Hold);
        SpineOverlayCommand released = AssertEx.NotNull(
            parser.Parse("#spine;3;Add_Arm;loop=false;hold=false").Value);
        AssertEx.Equal(false, released.Hold);

        // "clear" is a reserved word, so the property form is the escape hatch for an asset that
        // really owns an animation with that name.
        SpineOverlayCommand namedClear = AssertEx.NotNull(
            parser.Parse("#spine;3;clear;animation=clear").Value);
        AssertEx.Equal(SpineOverlayOperation.Play, namedClear.Operation);
        AssertEx.Equal("clear", namedClear.AnimationName);
    }

    public static void RejectsOutOfRangeTracksSlotsAndProperties()
    {
        var parser = new SpineOverlayDirectiveParser();
        string[] invalid =
        {
            "#spine;3",
            "#spine;3;",
            "#spine;0;Add_Arm",
            "#spine;6;Add_Arm",
            "#spine;x;Add_Arm",
            "#spine;3;Add_Arm;track=19",
            "#spine;3;Add_Arm;track=30",
            "#spine;3;Add_Arm;track=2",
            "#spine;3;Add_Arm;track=4",
            "#spine;3;Add_Arm;mix=-1",
            "#spine;3;Add_Arm;fade=5001",
            "#spine;3;Add_Arm;loop=yes",
            "#spine;3;Add_Arm;blend=multiply",
            "#spine;3;Add_Arm;duration=100",
            "#spine;3;Add_Arm;track=20;track=21",
            "#spine;3;clear;loop=true",
            "#spine;3;clear;hold=true",
            "#spine;3;Add_Arm;hold=maybe",
            "#spine;3;Add_Arm;hold=true",
            "#spine;3;clear;animation=",
            "#spine;3;Bad;Name"
        };

        foreach (string directive in invalid)
        {
            AssertEx.False(parser.Parse(directive).Success, $"Expected rejection: {directive}");
        }

        // The validator is also the guard for commands built by hand rather than parsed.
        AssertEx.False(
            SpineOverlayCommandValidator.Validate(new SpineOverlayCommand(
                3,
                SpineOverlayOperation.Play,
                "Bad;Name",
                SpineOverlayTrackPolicy.DefaultTrack,
                0,
                SpineOverlayTrackPolicy.DefaultFadeMilliseconds,
                true,
                SpineOverlayBlend.Replace)).Success,
            "A name that could split the canonical directive must be refused.");
    }

    public static void CanonicalDirectiveRoundTripsExactly()
    {
        var compiler = new SpineOverlayCommandFamilyCompiler();
        CanonicalTimelineCommand play = AssertEx.NotNull(compiler.Canonicalize("#spine;3;Add_Arm").Value);
        AssertEx.Equal(SpineOverlayCommandFamilyCompiler.CommandTypeId, play.CommandType);
        AssertEx.Equal(SpineOverlayCommandFamilyCompiler.CapabilityId, play.RequiredCapability);
        AssertEx.Equal(3, play.PublicSlot);
        AssertEx.Equal(
            "#spine;3;play;animation=Add_Arm;track=20;mix=0;fade=200;loop=true;blend=replace",
            play.Directive);

        // The lease compares the directive it was handed with the one the family produces, so the
        // canonical text has to be a fixed point.
        CanonicalTimelineCommand again = AssertEx.NotNull(compiler.Canonicalize(play.Directive).Value);
        AssertEx.Equal(play.Directive, again.Directive);
        AssertEx.Equal(play.CommandType, again.CommandType);
        AssertEx.Equal(play.PublicSlot, again.PublicSlot);

        CanonicalTimelineCommand clear = AssertEx.NotNull(compiler.Canonicalize("#spine;3;clear").Value);
        AssertEx.Equal("#spine;3;clear;fade=200", clear.Directive);
        AssertEx.Equal(
            clear.Directive,
            AssertEx.NotNull(compiler.Canonicalize(clear.Directive).Value).Directive);
        AssertEx.Equal(3, clear.PublicSlot);

        AssertEx.False(
            compiler.Canonicalize("#spine;3;Add_Arm;track=7").Success,
            "A family compiler must not accept a track outside the reserved range.");

        // The hold switch only appears when it was asked for: a directive that never mentioned it
        // canonicalizes to exactly what the previously deployed build produced.
        AssertEx.Equal(
            "#spine;3;play;animation=Add_Arm;track=20;mix=0;fade=200;loop=false;blend=replace;hold=true",
            AssertEx.NotNull(compiler.Canonicalize("#spine;3;Add_Arm;loop=false;hold=true").Value).Directive);
        CanonicalTimelineCommand held = AssertEx.NotNull(
            compiler.Canonicalize("#spine;3;Add_Arm;loop=false;hold=true").Value);
        AssertEx.Equal(
            held.Directive,
            AssertEx.NotNull(compiler.Canonicalize(held.Directive).Value).Directive);
    }

    public static void ExtractsOverlayBesideCharacterAndCameraOnTheSameSlot()
    {
        var extractor = new EmbeddedAavtDirectiveExtractor();
        EmbeddedAavtExtraction extracted = extractor.Extract(
            "正文台词\n#aavt;char;3;set;x=100\n#aavt;spine;3;Add_Arm;track=21\n#aavt;camera;set;zoom=1.35");

        AssertEx.Equal(3, extracted.Commands.Count);
        AssertEx.Equal(
            0,
            extracted.Errors.Count,
            "unexpected extraction errors: " + string.Join(" | ", extracted.Errors));
        AssertEx.False(
            extracted.SanitizedText.Contains("#aavt", StringComparison.Ordinal),
            "No AAVT line may survive into the text the native parser sees.");
        AssertEx.Equal(3, extracted.RemovedLineCount);

        EmbeddedAavtCommand overlay = extracted.Commands[1];
        AssertEx.Equal(3, overlay.PublicSlot);
        AssertEx.Equal(
            "#spine;3;play;animation=Add_Arm;track=21;mix=0;fade=200;loop=true;blend=replace",
            overlay.CanonicalDirective);

        // Two overlays for the same character are one resource too many, even though a character
        // transform and an overlay may share the slot number.
        // Two overlays for the same character are two resources as long as they claim different
        // tracks — that is exactly how eyes, arms and a prop are driven at the same time.
        EmbeddedAavtExtraction layered = extractor.Extract(
            "#aavt;spine;3;Eye_Blink;track=20\n#aavt;spine;3;Arm_Raise;track=21");
        AssertEx.Equal(2, layered.Commands.Count);
        AssertEx.Equal(
            0,
            layered.Errors.Count,
            "unexpected extraction errors: " + string.Join(" | ", layered.Errors));

        // The same track of the same character, twice, is the real conflict.
        EmbeddedAavtExtraction conflict = extractor.Extract(
            "#aavt;spine;3;Add_Arm\n#aavt;spine;3;Add_Hair");
        AssertEx.Equal(1, conflict.Commands.Count);
        AssertEx.Equal(1, conflict.Errors.Count);
        AssertEx.True(
            conflict.Errors[0].Contains("already has an AAVT spine overlay directive", StringComparison.Ordinal),
            "A second overlay on the same track must be refused by name: " + conflict.Errors[0]);

        EmbeddedAavtExtraction clearOnly = extractor.Extract("#aavt;spine;4;clear;fade=500");
        AssertEx.Equal(1, clearOnly.Commands.Count);
        AssertEx.Equal("#spine;4;clear;fade=500", clearOnly.Commands[0].CanonicalDirective);
    }

    public static void SceneBudgetFitsFiveCharactersOneCameraAndEveryReservedTrack()
    {
        AssertEx.Equal(21, EmbeddedAavtDirectiveExtractor.MaxCommandsPerScene);
        var lines = new List<string> { "正文台词", "#aavt;camera;set;zoom=1.35" };
        for (int slot = 1; slot <= SpineOverlayTrackPolicy.LastPublicSlot; slot++)
        {
            lines.Add($"#aavt;char;{slot};set;x={slot * 10}");
            lines.Add($"#aavt;fx;{slot};sway");
        }

        // One character, every reserved track: eyes, arms, hair, a prop — ten overlays at once.
        for (int track = SpineOverlayTrackPolicy.FirstReservedTrack;
            track <= SpineOverlayTrackPolicy.LastReservedTrack;
            track++)
        {
            lines.Add($"#aavt;spine;1;Add_Part_{track};track={track}");
        }

        EmbeddedAavtExtraction extracted =
            new EmbeddedAavtDirectiveExtractor().Extract(string.Join('\n', lines));
        AssertEx.Equal(EmbeddedAavtDirectiveExtractor.MaxCommandsPerScene, extracted.Commands.Count);
        AssertEx.Equal(
            0,
            extracted.Errors.Count,
            "unexpected extraction errors: " + string.Join(" | ", extracted.Errors));
    }

    public static void CleanupPlannerKeepsOverlayTracksApartAndRefusesForeignTracks()
    {
        var planner = new EditorPreviewResourceCleanupPlanner();
        var current = new EditorPreviewResourceFootprint(
            "editor-preview:scene",
            new[]
            {
                new EditorPreviewResourceFootprintEntry(
                    new EditorPreviewResourceKey(EditorPreviewResourceKind.SpineOverlay, 3, 20),
                    EditorPreviewResourceFields.SpineOverlayTrack,
                    EditorPreviewDispatchMode.Immediate),
                new EditorPreviewResourceFootprintEntry(
                    new EditorPreviewResourceKey(EditorPreviewResourceKind.SpineOverlay, 3, 21),
                    EditorPreviewResourceFields.SpineOverlayTrack,
                    EditorPreviewDispatchMode.Immediate),
                new EditorPreviewResourceFootprintEntry(
                    new EditorPreviewResourceKey(EditorPreviewResourceKind.CharacterSlot, 3),
                    EditorPreviewResourceFields.CharacterPositionX,
                    EditorPreviewDispatchMode.Immediate)
            });

        EditorPreviewResourceCleanupPlan plan =
            AssertEx.NotNull(planner.Plan(null, current).Value);
        AssertEx.Equal(3, plan.Resources.Count);

        // Two overlays of one character are two resources: leaving one behind keeps the other.
        var next = new EditorPreviewResourceFootprint(
            "editor-preview:scene",
            new[] { current.Entries[0] });
        EditorPreviewResourceCleanupPlan transition =
            AssertEx.NotNull(planner.Plan(current, next).Value);
        EditorPreviewResourceTransition kept = AssertEx.NotNull(
            transition.Resources.FirstOrDefault(resource => resource.Resource.TrackIndex == 20));
        EditorPreviewResourceTransition dropped = AssertEx.NotNull(
            transition.Resources.FirstOrDefault(resource => resource.Resource.TrackIndex == 21));
        AssertEx.False(kept.CommandRemoved);
        AssertEx.True(dropped.CommandRemoved);

        // A track outside the reserved range is not an overlay resource at all.
        var foreign = new EditorPreviewResourceFootprint(
            "editor-preview:scene",
            new[]
            {
                new EditorPreviewResourceFootprintEntry(
                    new EditorPreviewResourceKey(EditorPreviewResourceKind.SpineOverlay, 3, 7),
                    EditorPreviewResourceFields.SpineOverlayTrack,
                    EditorPreviewDispatchMode.Immediate)
            });
        AssertEx.False(planner.Plan(null, foreign).Success);
    }

    public static void UnknownNamespaceStillFailsClosedAndNamesTheOverlayForm()
    {
        EmbeddedAavtExtraction extracted =
            new EmbeddedAavtDirectiveExtractor().Extract("#aavt;spine-ish;3;Add_Arm");
        AssertEx.Equal(0, extracted.Commands.Count);
        AssertEx.Contains(
            extracted.Errors,
            error => error.Contains("spine;<slot>;<animation|clear>", StringComparison.Ordinal),
            "The rejection must tell the author the form that would have worked.");
    }
}
