using System.Globalization;
using System.Text.Json;
using AzureArchive.VideoTools.Core.Characters;
using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Mapping;
using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Core.Projects;

namespace AzureArchive.VideoTools.Tests;

internal static class CharacterPresetBezierTests
{
    public static void BezierSolvesTimeCoordinateInsteadOfUsingTimeAsTheParameter()
    {
        // Independent analytic oracle: x(u)=u^3 and y(u)=3u^2-2u^3.
        // At u=1/2 the clock is only 1/8 through, but the motion is halfway.
        var curve = new CharacterPresetBezier(0, 0, 0, 1);
        Near(0.5, curve.Evaluate(0.125), 1e-12);
        Near(0.028, curve.Evaluate(0.001), 1e-12);
        Near(0.972, curve.Evaluate(0.729), 1e-12);
        AssertEx.True(Math.Abs(curve.Evaluate(0.125) - 0.04296875) > 0.4,
            "Evaluating y at the clock value directly is not a timing curve.");

        var reflected = new CharacterPresetBezier(1, 0, 1, 1);
        Near(0.5, reflected.Evaluate(0.875), 1e-12);
        Near(0.028, reflected.Evaluate(0.271), 1e-12);
    }

    public static void BezierHandlesVerticalTangentsReversedHandlesAndExactEndpoints()
    {
        var reversed = new CharacterPresetBezier(1, 0, 0, 1);
        AssertEx.True(reversed.IsValid);
        // At u=1/4 and 3/4, x=7/16 and 9/16, y=5/32 and 27/32.
        Near(0.15625, reversed.Evaluate(0.4375), 1e-12);
        Near(0.5, reversed.Evaluate(0.5), 1e-12);
        Near(0.84375, reversed.Evaluate(0.5625), 1e-12);
        foreach (float x1 in new[] { 0f, 0.5f, 1f })
        foreach (float y1 in new[] { 0f, 1f })
        foreach (float x2 in new[] { 0f, 0.5f, 1f })
        foreach (float y2 in new[] { 0f, 1f })
        {
            var curve = new CharacterPresetBezier(x1, y1, x2, y2);
            AssertEx.Equal(0d, curve.Evaluate(0));
            AssertEx.Equal(1d, curve.Evaluate(1));
            AssertEx.Equal(0d, curve.Evaluate(-1));
            AssertEx.Equal(1d, curve.Evaluate(2));
            double previous = 0;
            foreach (double time in new[] { 1e-12, 1e-6, 0.1, 0.25, 0.499999, 0.5, 0.500001, 0.75, 0.9, 1 - 1e-6, 1 - 1e-12 })
            {
                double value = curve.Evaluate(time);
                AssertEx.True(double.IsFinite(value) && value >= 0 && value <= 1);
                AssertEx.True(value + 1e-12 >= previous, "Valid control points must not reverse time.");
                previous = value;
            }
        }
    }

    public static void BezierNamedPresetsHaveExpectedTimingAndSymmetry()
    {
        Near(0.31535681, CharacterPresetBezier.EaseIn.Evaluate(0.5), 1e-7);
        Near(0.68464319, CharacterPresetBezier.EaseOut.Evaluate(0.5), 1e-7);
        Near(0.5, CharacterPresetBezier.EaseInOut.Evaluate(0.5), 1e-7);
        for (int i = 0; i <= 100; i++)
        {
            double time = i / 100d;
            AssertEx.Equal(time, CharacterPresetBezier.Linear.Evaluate(time));
            AssertEx.Equal(time, new CharacterPresetBezier(1, 1, 0, 0).Evaluate(time));
            Near(CharacterPresetBezier.EaseIn.Evaluate(time),
                1 - CharacterPresetBezier.EaseOut.Evaluate(1 - time), 1e-7);
            Near(CharacterPresetBezier.EaseInOut.Evaluate(time),
                1 - CharacterPresetBezier.EaseInOut.Evaluate(1 - time), 1e-7);
        }
    }

    public static void BezierRejectsInvalidCoordinatesAndFailsClosedForNonFiniteTime()
    {
        foreach (CharacterPresetBezier curve in InvalidCurves())
        {
            AssertEx.False(curve.IsValid);
            AssertEx.Equal(0d, curve.Evaluate(0.5));
            CharacterPresetCommand command = Parse("#fx;1;sway") with { Bezier = curve };
            AssertEx.False(CharacterPresetCommandValidator.Validate(command).Success);
            AssertEx.Equal(CharacterPresetFrame.Neutral(true), CharacterPresetEvaluator.Sample(command, 0.5));
        }
        foreach (double time in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            AssertEx.Equal(0d, CharacterPresetBezier.EaseInOut.Evaluate(time));
    }

    public static void BezierDirectiveRoundTripsAllKindsAndInvariantFloatPrecision()
    {
        var curve = new CharacterPresetBezier(0.12345679f, 0.8765432f, 0.7654321f, 0.2345679f);
        var compiler = new CharacterPresetCommandFamilyCompiler();
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            foreach (string culture in new[] { "en-US", "fr-FR", "de-DE" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                AssertEx.Equal("0.12345679,0.8765432,0.7654321,0.2345679", curve.ToDirectiveValue());
                foreach (string kind in Kinds)
                {
                    string directive = $"#fx;3;{kind};BEZIER={curve.ToDirectiveValue()}";
                    CharacterPresetCommand parsed = Parse(directive);
                    AssertEx.Equal((CharacterPresetBezier?)curve, parsed.Bezier);
                    CanonicalTimelineCommand canonical = AssertEx.NotNull(compiler.Canonicalize(directive).Value);
                    AssertEx.True(canonical.Directive.EndsWith(";bezier=" + curve.ToDirectiveValue(), StringComparison.Ordinal));
                    AssertEx.Equal(parsed, Parse(canonical.Directive));
                    AssertEx.Equal(canonical, AssertEx.NotNull(compiler.Canonicalize(canonical.Directive).Value));
                }
            }
        }
        finally { CultureInfo.CurrentCulture = previous; }
        AssertEx.Equal("0,0,1,1", new CharacterPresetBezier(-0f, 0, 1, 1).ToDirectiveValue());
        AssertEx.Equal((CharacterPresetBezier?)new CharacterPresetBezier(1, 0, 0, 1),
            Parse("#fx;1;sway;bezier= 1 , 0 , 0 , 1 ").Bezier);
    }

    public static void BezierMalformedDirectiveNeverPassesParserCompilerOrExtraction()
    {
        var parser = new CharacterPresetDirectiveParser();
        var compiler = new CharacterPresetCommandFamilyCompiler();
        foreach (string property in new[]
        {
            "bezier=", "bezier=0,0,1", "bezier=0,0,1,1,1", "bezier=0,,1,1",
            "bezier=0;0;1;1", "bezier=NaN,0,1,1", "bezier=0,Infinity,1,1",
            "bezier=0,0,-Infinity,1", "bezier=0,0,1,NaN", "bezier=1.01,0,1,1",
            "bezier=0,-0.01,1,1", "bezier=0,0,1.01,1", "bezier=0,0,1,-0.01",
            "bezier=0,0,1,1e100", "bezier=0,0,1,one", "bezier=0,0,1,1=1",
            "bezier=0,0,1,1;BEZIER=0.42,0,0.58,1"
        })
        {
            string directive = "#fx;1;sway;" + property;
            AssertEx.False(parser.Parse(directive).Success, directive);
            AssertEx.False(compiler.Canonicalize(directive).Success, directive);
            EmbeddedAavtExtraction extraction = new EmbeddedAavtDirectiveExtractor().Extract("#aavt;" + directive[1..]);
            AssertEx.Equal(0, extraction.Commands.Count, directive);
            AssertEx.True(extraction.Errors.Count > 0, directive);
        }
    }

    public static void OmittedBezierPreservesLegacyCanonicalStringsAndDefaultMotion()
    {
        string[] canonical =
        {
            "#fx;1;sway;amplitude=12;frequency=3;cycles=3;direction=right",
            "#fx;1;spin;frequency=1;cycles=2;direction=right",
            "#fx;1;headbutt;back=20;forward=45;duration=650;direction=right",
            "#fx;1;squash;amplitude=0.15;frequency=2;cycles=3;direction=right"
        };
        var compiler = new CharacterPresetCommandFamilyCompiler();
        for (int i = 0; i < Kinds.Length; i++)
        {
            string directive = "#fx;1;" + Kinds[i];
            AssertEx.Equal(canonical[i], AssertEx.NotNull(compiler.Canonicalize(directive).Value).Directive);
            AssertEx.Equal<CharacterPresetBezier?>(null, Parse(directive).Bezier);
        }
        var oldConstructor = new CharacterPresetCommand(1, CharacterPresetKind.Spin, 0, 1, 2, 1, 0, 0, 0);
        AssertEx.Equal(Parse("#fx;1;spin"), oldConstructor);
        Near(45, CharacterPresetEvaluator.Sample(oldConstructor, 0.125).YawDegrees);
        CharacterPresetCommand headbutt = Parse("#fx;1;headbutt;back=20;forward=60;duration=1000");
        Near(3.125, CharacterPresetEvaluator.Sample(headbutt, 0.075).RotationDegrees);
        Near(7.5, CharacterPresetEvaluator.Sample(headbutt, 0.3375).RotationDegrees);
        Near(-50.625, CharacterPresetEvaluator.Sample(headbutt, 0.5875).RotationDegrees);
    }

    public static void ExplicitLinearPreservesCyclicMotionButCanChangeHeadbuttTiming()
    {
        foreach (string kind in new[] { "sway", "spin", "squash" })
        {
            CharacterPresetCommand original = Parse($"#fx;2;{kind};frequency=2;cycles=3");
            CharacterPresetCommand linear = original with { Bezier = CharacterPresetBezier.Linear };
            for (int i = 0; i <= 1500; i++)
            {
                CharacterPresetFrame before = CharacterPresetEvaluator.Sample(original, i / 1000d);
                CharacterPresetFrame after = CharacterPresetEvaluator.Sample(linear, i / 1000d);
                Near(before.RotationDegrees, after.RotationDegrees);
                Near(before.YawDegrees, after.YawDegrees);
                Near(before.ScaleX, after.ScaleX);
                Near(before.ScaleY, after.ScaleY);
                AssertEx.Equal(before.Completed, after.Completed);
            }
        }
        CharacterPresetCommand headbutt = Parse("#fx;1;headbutt;back=20;duration=1000");
        Near(3.125, CharacterPresetEvaluator.Sample(headbutt, 0.075).RotationDegrees);
        Near(5, CharacterPresetEvaluator.Sample(headbutt with { Bezier = CharacterPresetBezier.Linear }, 0.075).RotationDegrees);
    }

    public static void BezierSpinWarpsEveryYawTurnWithoutLosingWholeRevolutions()
    {
        CharacterPresetCommand command = Parse("#fx;2;spin;frequency=1;cycles=3;bezier=0,0,0,1");
        Near(180, CharacterPresetEvaluator.Sample(command, 0.125).YawDegrees);
        Near(540, CharacterPresetEvaluator.Sample(command, 1.125).YawDegrees);
        Near(900, CharacterPresetEvaluator.Sample(command, 2.125).YawDegrees);
        Near(-900, CharacterPresetEvaluator.Sample(command with { Direction = -1 }, 2.125).YawDegrees);
        foreach (double boundary in new[] { 1d, 2d })
        {
            Near(boundary * 360, CharacterPresetEvaluator.Sample(command, boundary).YawDegrees);
            Near(boundary * 360, CharacterPresetEvaluator.Sample(command, boundary - 1e-8).YawDegrees, 0.01);
            Near(boundary * 360, CharacterPresetEvaluator.Sample(command, boundary + 1e-8).YawDegrees, 0.01);
        }
        for (int i = 0; i < 3000; i++)
        {
            CharacterPresetFrame frame = CharacterPresetEvaluator.Sample(command, i / 1000d);
            Near(0, frame.RotationDegrees);
            Near(1, frame.ScaleX); Near(1, frame.ScaleY);
            AssertEx.False(frame.Completed);
        }
        AssertEx.Equal(CharacterPresetFrame.Neutral(true), CharacterPresetEvaluator.Sample(command, 3));
    }

    public static void BezierHeadbuttPreservesAllThreeStageBoundariesAndDirection()
    {
        CharacterPresetCommand command = Parse("#fx;1;headbutt;back=20;forward=60;duration=1000;bezier=0,0,0,1");
        // Each stage is independently halfway at one eighth of its duration.
        Near(10, CharacterPresetEvaluator.Sample(command, 0.0375).RotationDegrees);
        Near(-20, CharacterPresetEvaluator.Sample(command, 0.31875).RotationDegrees);
        Near(-30, CharacterPresetEvaluator.Sample(command, 0.51875).RotationDegrees);
        Near(20, CharacterPresetEvaluator.Sample(command, 0.3).RotationDegrees);
        Near(-60, CharacterPresetEvaluator.Sample(command, 0.45).RotationDegrees);
        foreach (double time in new[] { 0.0375, 0.3, 0.31875, 0.45, 0.51875, 0.999 })
        {
            CharacterPresetFrame right = CharacterPresetEvaluator.Sample(command, time);
            CharacterPresetFrame left = CharacterPresetEvaluator.Sample(command with { Direction = -1 }, time);
            Near(-right.RotationDegrees, left.RotationDegrees);
            Near(0, right.YawDegrees); Near(1, right.ScaleX); Near(1, right.ScaleY);
        }
        foreach (double boundary in new[] { 0.3, 0.45 })
        {
            double at = CharacterPresetEvaluator.Sample(command, boundary).RotationDegrees;
            Near(at, CharacterPresetEvaluator.Sample(command, boundary - 1e-8).RotationDegrees, 0.01);
            Near(at, CharacterPresetEvaluator.Sample(command, boundary + 1e-8).RotationDegrees, 0.01);
        }
        AssertEx.Equal(CharacterPresetFrame.Neutral(true), CharacterPresetEvaluator.Sample(command, 1));
    }

    public static void BezierOscillationKeepsAmplitudeAreaAndCycleDuration()
    {
        CharacterPresetCommand sway = Parse("#fx;3;sway;amplitude=30;frequency=1;cycles=3;bezier=0,0,0,1");
        CharacterPresetCommand squash = Parse("#fx;3;squash;amplitude=0.7;frequency=1;cycles=3;bezier=0,0,0,1");
        // x=1/64 corresponds to u=1/4, hence wave phase=5/32. The second
        // cycle has full envelope, making this a direct geometric oracle.
        double wave = Math.Sin(5 * Math.PI / 16);
        Near(-30 * wave, CharacterPresetEvaluator.Sample(sway, 1.015625).RotationDegrees);
        Near(Math.Exp(0.7f * wave), CharacterPresetEvaluator.Sample(squash, 1.015625).ScaleY);
        for (int i = 0; i <= 3000; i++)
        {
            double time = i / 1000d;
            CharacterPresetFrame tilt = CharacterPresetEvaluator.Sample(sway, time);
            CharacterPresetFrame scale = CharacterPresetEvaluator.Sample(squash, time);
            AssertEx.True(Math.Abs(tilt.RotationDegrees) <= 30.0001);
            Near(0, tilt.YawDegrees); Near(0, scale.YawDegrees); Near(0, scale.RotationDegrees);
            AssertEx.True(scale.ScaleX > 0 && scale.ScaleY > 0);
            AssertEx.True(scale.ScaleY >= Math.Exp(-0.7f) - 1e-6 && scale.ScaleY <= Math.Exp(0.7f) + 1e-6);
            Near(1, scale.ScaleX * scale.ScaleY, 1e-6);
        }
        foreach (CharacterPresetCommand command in new[] { sway, squash })
        {
            AssertEx.Equal(CharacterPresetFrame.Neutral(), CharacterPresetEvaluator.Sample(command, 0));
            AssertEx.Equal(CharacterPresetFrame.Neutral(), CharacterPresetEvaluator.Sample(command, 1));
            AssertEx.Equal(CharacterPresetFrame.Neutral(), CharacterPresetEvaluator.Sample(command, 2));
            AssertEx.False(CharacterPresetEvaluator.Sample(command, 2.999).Completed);
            AssertEx.Equal(CharacterPresetFrame.Neutral(true), CharacterPresetEvaluator.Sample(command, 3));
        }
    }

    public static void BezierOscillationStillEntersAndLeavesAtRest()
    {
        foreach (string kind in new[] { "sway", "squash" })
        foreach (string curve in new[] { "0,1,0,1", "1,0,1,0", "1,1,0,0", "0.42,0,0.58,1" })
        {
            CharacterPresetCommand command = Parse($"#fx;1;{kind};frequency=1;cycles=2;bezier={curve}");
            foreach (double time in new[] { 1e-6, 2 - 1e-6 })
            {
                CharacterPresetFrame frame = CharacterPresetEvaluator.Sample(command, time);
                Near(0, frame.RotationDegrees, 1e-6);
                Near(1, frame.ScaleX, 1e-6); Near(1, frame.ScaleY, 1e-6);
            }
            AssertEx.Equal(CharacterPresetFrame.Neutral(true), CharacterPresetEvaluator.Sample(command, 2));
        }
    }

    public static void BezierInfinitePresetsRemainPeriodicAndFiniteAtHugeTimes()
    {
        foreach (string kind in new[] { "sway", "spin", "squash" })
        {
            CharacterPresetCommand command = Parse($"#fx;1;{kind};frequency=2;cycles=0;bezier=1,0,0,1");
            AssertEx.Equal(CharacterPresetEvaluator.Sample(command, 1.15625),
                CharacterPresetEvaluator.Sample(command, 50001.15625));
            foreach (double time in new[] { 1e10, 1e100, double.MaxValue })
            {
                CharacterPresetFrame frame = CharacterPresetEvaluator.Sample(command, time);
                AssertEx.False(frame.Completed);
                AssertEx.True(float.IsFinite(frame.RotationDegrees) && float.IsFinite(frame.YawDegrees)
                    && float.IsFinite(frame.ScaleX) && float.IsFinite(frame.ScaleY));
                Near(1, frame.ScaleX * frame.ScaleY, 1e-6);
            }
            foreach (double time in new[] { -1d, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
                AssertEx.Equal(CharacterPresetFrame.Neutral(), CharacterPresetEvaluator.Sample(command, time));
        }
    }

    public static void BezierSurvivesEditorCompileProjectionSerializationAndPlaybackBinding()
    {
        const string before = "#wait;100\r\n  #aavt;char;3;set;rotation=20  \r\n";
        const string after = "\r\n#aavt;spine;3;Eye_Blink";
        string source = before + "#aavt;fx;3;sway" + after;
        const string incoming = "#aavt;fx;3;spin;cycles=3;bezier=0.12345679,0.8765432,0.7654321,0.2345679";
        var composer = new EditorCommandDocumentComposer();
        var editResult = composer.PreviewUpsert(source, EditorCommandDocumentComposer.Revision(source), incoming);
        AssertEx.True(editResult.Success, editResult.Error);
        EditorCommandEditPreview edit = AssertEx.NotNull(editResult.Value);
        AssertEx.True(edit.UpdatedText.StartsWith(before, StringComparison.Ordinal));
        AssertEx.True(edit.UpdatedText.EndsWith(after, StringComparison.Ordinal));
        EmbeddedAavtExtraction extraction = new EmbeddedAavtDirectiveExtractor().Extract(edit.UpdatedText);
        AssertEx.Equal(0, extraction.Errors.Count);
        AssertEx.Equal("#wait;100\r\n", extraction.SanitizedText);
        string canonical = extraction.Commands.Single(command => command.CanonicalDirective.StartsWith("#fx;", StringComparison.Ordinal)).CanonicalDirective;
        CharacterPresetBezier? expected = Parse(canonical).Bezier;
        AssertEx.True(expected.HasValue);

        var (project, playback) = SyntheticPair(edit.UpdatedText);
        var compilationResult = new EmbeddedProjectCommandCompiler().Compile(project, playback, "1.4.0");
        AssertEx.True(compilationResult.Success, compilationResult.Error);
        PlaybackCommandProjection projection = AssertEx.NotNull(compilationResult.Value).Projection;
        string serialized = JsonSerializer.Serialize(projection);
        PlaybackCommandProjection reloaded = AssertEx.NotNull(JsonSerializer.Deserialize<PlaybackCommandProjection>(serialized));
        var bound = new PlaybackCommandBinder().Bind(playback, reloaded);
        AssertEx.True(bound.Success, bound.Error);
        PlaybackCommandBatch batch = reloaded.Batches.Single();
        AssertEx.True(new PlaybackSceneDispatchGate().TryAuthorize(1, batch).Success);
        string restored = batch.Commands.Single(command => command.CommandType == CharacterPresetCommandFamilyCompiler.CommandTypeId).CanonicalDirective;
        AssertEx.Equal(canonical, restored);
        AssertEx.Equal(expected, Parse(restored).Bezier);
        AssertEx.Equal(edit.UpdatedText, project.ScriptNodes.Single().Scenes.Single().AdditionalPrompt);
    }

    private static readonly string[] Kinds = { "sway", "spin", "headbutt", "squash" };

    private static IEnumerable<CharacterPresetBezier> InvalidCurves()
    {
        foreach (float value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -0.001f, 1.001f })
        {
            yield return new(value, 0, 1, 1);
            yield return new(0, value, 1, 1);
            yield return new(0, 0, value, 1);
            yield return new(0, 0, 1, value);
        }
    }

    private static CharacterPresetCommand Parse(string directive)
    {
        var parsed = new CharacterPresetDirectiveParser().Parse(directive);
        AssertEx.True(parsed.Success, parsed.Error);
        return AssertEx.NotNull(parsed.Value);
    }

    private static void Near(double expected, double actual, double tolerance = 0.0001) =>
        AssertEx.True(double.IsFinite(actual) && Math.Abs(expected - actual) <= tolerance,
            $"Expected {expected} ± {tolerance}, got {actual}.");

    // Synthetic in-memory snapshots only; no author project is opened or saved.
    private static (ProjectSnapshot Project, PlaybackArchiveSnapshot Playback) SyntheticPair(string prompt)
    {
        const string nodeGuid = "11111111-2222-3333-4444-555555555555";
        var now = DateTimeOffset.Parse("2026-09-29T00:00:00Z", CultureInfo.InvariantCulture);
        var scene = new SceneSnapshot(new SceneKey(nodeGuid, 0, new string('A', 64)),
            "synthetic", "dialogue", true, string.Empty, 0, 0, prompt,
            string.Empty, string.Empty, string.Empty, string.Empty, 0, 0, 0);
        var node = new StoryNodeSnapshot(0, StoryNodeKind.Script, "synthetic", nodeGuid, true,
            "bezier", Array.Empty<string>(), new[] { scene });
        var project = new ProjectSnapshot(
            new ProjectSourceSnapshot(@"C:\synthetic\bezier.aap", new string('1', 64), new string('2', 64), 1, now),
            "synthetic", "bezier", new ProjectPreviewSnapshot(null, string.Empty, string.Empty),
            new[] { node }, Array.Empty<ProjectDiagnostic>());
        var record = new PlaybackRecordSnapshot(0, 0, 0, 0, string.Empty, 0, 0, 0, string.Empty,
            "compiled dialogue", "dialogue", string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
            false, new string('C', 64));
        var playback = new PlaybackArchiveSnapshot(
            new PlaybackArchiveSourceSnapshot(@"C:\synthetic\bezier.aas", new string('3', 64), new string('4', 64), 1, now.AddSeconds(1)),
            "synthetic/v1", new[] { record });
        return (project, playback);
    }
}
