using System.Globalization;
using AzureArchive.VideoTools.Core.Characters;
using AzureArchive.VideoTools.Core.Commands;

namespace AzureArchive.VideoTools.Tests;

internal static class CharacterPresetTests
{
    public static void ParsesFourKindsAndCanonicalizesAcrossCultures()
    {
        var compiler = new CharacterPresetCommandFamilyCompiler();
        string[] directives =
        {
            "#fx;1;sway;amplitude=25.5;frequency=2.5;cycles=0;direction=left",
            "#FX;2;SPIN;cycles=4;frequency=1.5;direction=RIGHT",
            "#fx;3;headbutt;back=30;forward=65;duration=900;direction=left",
            "#fx;5;squash;amplitude=0.3;frequency=4;cycles=5"
        };
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            foreach (string directive in directives)
            {
                var result = compiler.Canonicalize(directive);
                AssertEx.True(result.Success, result.Error);
                CanonicalTimelineCommand canonical = AssertEx.NotNull(result.Value);
                AssertEx.Equal(CharacterPresetCommandFamilyCompiler.CommandTypeId, canonical.CommandType);
                AssertEx.Equal(CharacterPresetCommandFamilyCompiler.CapabilityId, canonical.RequiredCapability);
                AssertEx.Equal(canonical.Directive, AssertEx.NotNull(compiler.Canonicalize(canonical.Directive).Value).Directive);
                var extracted = new EmbeddedAavtDirectiveExtractor().Extract("#aavt;" + canonical.Directive[1..]);
                AssertEx.Equal(0, extracted.Errors.Count);
                AssertEx.Equal(canonical.Directive, extracted.Commands.Single().CanonicalDirective);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }

        AssertEx.Equal(0, Parse(directives[0]).Cycles);
        AssertEx.Equal(-1, Parse(directives[0]).Direction);
        AssertEx.Equal(900, Parse(directives[2]).DurationMilliseconds);
        AssertEx.Equal("#fx;1;sway;amplitude=0;frequency=3;cycles=3;direction=right",
            AssertEx.NotNull(compiler.Canonicalize("#fx;1;sway;amplitude=-0").Value).Directive);
    }

    public static void RejectsMalformedIrrelevantAndUnsafePresetProperties()
    {
        string[] invalid =
        {
            "", "#fx;3", "#fx;0;sway", "#fx;6;sway", "#fx;+3;sway", "#fx;x;sway",
            "#fx;1;clear", "#fx;1;unknown", "#fx;1;sway;", "#fx;1;sway;amplitude=",
            "#fx;1;sway;amplitude=2;AMPLITUDE=3", "#fx;1;sway;direction=clockwise",
            "#fx;1;sway;direction=", "#fx;1;sway;frequency=0.0999", "#fx;1;sway;frequency=10.01",
            "#fx;1;spin;frequency=5.01", "#fx;1;spin;amplitude=1", "#fx;1;sway;amplitude=-0.1",
            "#fx;1;sway;amplitude=90.01", "#fx;1;squash;amplitude=-0.1", "#fx;1;squash;amplitude=0.701",
            "#fx;1;sway;cycles=-1", "#fx;1;sway;cycles=101", "#fx;1;sway;cycles=1.5",
            "#fx;1;sway;cycles=2147483648", "#fx;1;sway;duration=100", "#fx;1;sway;back=20",
            "#fx;1;headbutt;frequency=1", "#fx;1;headbutt;cycles=1", "#fx;1;headbutt;amplitude=1",
            "#fx;1;headbutt;back=-1", "#fx;1;headbutt;back=90.1", "#fx;1;headbutt;forward=-1",
            "#fx;1;headbutt;forward=120.1", "#fx;1;headbutt;duration=99", "#fx;1;headbutt;duration=10001",
            "#fx;1;headbutt;duration=1e3", "#fx;1;sway;frequency=1,5", "#fx;1;sway;frequency=1=2"
        };
        var parser = new CharacterPresetDirectiveParser();
        foreach (string directive in invalid)
        {
            AssertEx.False(parser.Parse(directive).Success, "Must reject: " + directive);
        }
    }

    public static void AcceptsExactLimitsAndRejectsNonFiniteNumbers()
    {
        string[] valid =
        {
            "#fx;1;sway;amplitude=0;frequency=0.1;cycles=0",
            "#fx;5;sway;amplitude=90;frequency=10;cycles=100",
            "#fx;1;spin;frequency=5;cycles=100", "#fx;1;spin;frequency=0.1;cycles=0",
            "#fx;1;squash;amplitude=0.7;frequency=10;cycles=100",
            "#fx;1;squash;amplitude=0;frequency=0.1;cycles=0",
            "#fx;1;headbutt;back=0;forward=0;duration=100",
            "#fx;5;headbutt;back=90;forward=120;duration=10000"
        };
        foreach (string directive in valid) AssertEx.True(new CharacterPresetDirectiveParser().Parse(directive).Success, directive);
        foreach (string number in new[] { "NaN", "Infinity", "-Infinity", "1e100" })
        {
            foreach (string prefix in new[] { "#fx;1;sway;amplitude=", "#fx;1;squash;frequency=", "#fx;1;headbutt;back=", "#fx;1;headbutt;forward=" })
                AssertEx.False(new CharacterPresetDirectiveParser().Parse(prefix + number).Success, prefix + number);
        }

        CharacterPresetCommand source = Parse("#fx;1;sway");
        CharacterPresetCommand[] invalid =
        {
            source with { Kind = (CharacterPresetKind)99 }, source with { Direction = 0 },
            source with { PublicSlot = -1 }, source with { Cycles = -1 },
            source with { Amplitude = float.NaN }, source with { FrequencyHz = float.PositiveInfinity },
            source with { BackDegrees = float.NegativeInfinity }, source with { ForwardDegrees = float.NaN }
        };
        AssertEx.False(CharacterPresetCommandValidator.Validate(null).Success);
        foreach (CharacterPresetCommand command in invalid)
        {
            AssertEx.False(CharacterPresetCommandValidator.Validate(command).Success);
            AssertNeutral(CharacterPresetEvaluator.Sample(command, 0.125), completed: true);
        }
    }

    public static void NeutralAndFiniteDurationNeverAccumulateResidualPose()
    {
        foreach (string kind in new[] { "sway", "spin", "squash", "headbutt" })
        {
            CharacterPresetCommand command = Parse("#fx;1;" + kind);
            foreach (double time in new[] { 0d, -1d, double.NaN, double.NegativeInfinity, double.PositiveInfinity })
                AssertNeutral(CharacterPresetEvaluator.Sample(command, time), completed: false);
            double duration = kind == "headbutt" ? command.DurationMilliseconds / 1000d : command.Cycles / (double)command.FrequencyHz;
            AssertEx.False(CharacterPresetEvaluator.Sample(command, duration - 1e-6).Completed);
            AssertNeutral(CharacterPresetEvaluator.Sample(command, duration), completed: true);
            AssertNeutral(CharacterPresetEvaluator.Sample(command, double.MaxValue), completed: true);
        }
    }

    public static void SwayRespectsAmplitudeDirectionAndPeriod()
    {
        CharacterPresetCommand command = Parse("#fx;3;sway;amplitude=30;frequency=2;cycles=3;direction=right");
        Near(-30, CharacterPresetEvaluator.Sample(command, 0.125).RotationDegrees);
        Near(30, CharacterPresetEvaluator.Sample(command, 0.375).RotationDegrees);
        AssertNeutral(CharacterPresetEvaluator.Sample(command, 0.5), completed: false);
        Near(-30, CharacterPresetEvaluator.Sample(command, 0.625).RotationDegrees);
        Near(30, CharacterPresetEvaluator.Sample(command with { Direction = -1 }, 0.125).RotationDegrees);
        for (int i = 0; i <= 1500; i++)
        {
            AssertEx.True(Math.Abs(CharacterPresetEvaluator.Sample(command, i / 1000d).RotationDegrees) <= 30.0001f);
            Near(0, CharacterPresetEvaluator.Sample(command, i / 1000d).YawDegrees);
        }
    }

    public static void SpinUsesYawForCompleteRevolutionsAndSelectedDirection()
    {
        CharacterPresetCommand command = Parse("#fx;2;spin;frequency=2;cycles=3;direction=left");
        Near(-90, CharacterPresetEvaluator.Sample(command, 0.125).YawDegrees);
        Near(-180, CharacterPresetEvaluator.Sample(command, 0.25).YawDegrees);
        Near(-360, CharacterPresetEvaluator.Sample(command, 0.5).YawDegrees);
        Near(-900, CharacterPresetEvaluator.Sample(command, 1.25).YawDegrees);
        Near(900, CharacterPresetEvaluator.Sample(command with { Direction = 1 }, 1.25).YawDegrees);
        for (int index = 0; index < 1500; index++)
        {
            CharacterPresetFrame frame = CharacterPresetEvaluator.Sample(command, index / 1000d);
            Near(0, frame.RotationDegrees);
            Near(1, frame.ScaleX);
            Near(1, frame.ScaleY);
        }
        AssertNeutral(CharacterPresetEvaluator.Sample(command, 1.5), completed: true);
    }

    public static void SquashKeepsPositiveReciprocalAxesAndArea()
    {
        CharacterPresetCommand command = Parse("#fx;5;squash;amplitude=0.7;frequency=2;cycles=4");
        CharacterPresetFrame tall = CharacterPresetEvaluator.Sample(command, 0.125);
        CharacterPresetFrame wide = CharacterPresetEvaluator.Sample(command, 0.375);
        AssertEx.True(tall.ScaleY > 1 && tall.ScaleX < 1);
        AssertEx.True(wide.ScaleY < 1 && wide.ScaleX > 1);
        Near(tall.ScaleY, wide.ScaleX);
        Near(tall.ScaleX, wide.ScaleY);
        for (int i = 0; i <= 2000; i++)
        {
            CharacterPresetFrame sample = CharacterPresetEvaluator.Sample(command, i / 1000d);
            AssertEx.True(sample.ScaleX > 0 && sample.ScaleY > 0);
            Near(1, sample.ScaleX * sample.ScaleY, 0.000001);
            Near(0, sample.RotationDegrees);
            Near(0, sample.YawDegrees);
        }
        AssertNeutral(CharacterPresetEvaluator.Sample(command, 0.5), completed: false);
        AssertNeutral(CharacterPresetEvaluator.Sample(command, 2), completed: true);
    }

    public static void OscillatingPresetsEaseFromAndBackToRest()
    {
        foreach (string kind in new[] { "sway", "squash" })
        {
            CharacterPresetCommand command = Parse($"#fx;1;{kind};frequency=1;cycles=2;amplitude={(kind == "sway" ? "12" : "0.7")}");
            double Offset(double time)
            {
                CharacterPresetFrame frame = CharacterPresetEvaluator.Sample(command, time);
                return kind == "sway" ? frame.RotationDegrees : frame.ScaleY - 1;
            }
            // A quarter-cycle ramp is smooth at rest; no instantaneous velocity
            // at attachment/release. Halving this interval reduces displacement
            // much faster than a sine wave with an abrupt start would.
            AssertEx.True(Math.Abs(Offset(0.0001)) < Math.Abs(Offset(0.001)) / 20);
            AssertEx.True(Math.Abs(Offset(1.9999)) < Math.Abs(Offset(1.999)) / 20);
            AssertEx.True(Math.Abs(Offset(0.001)) / 0.001 < 0.02);
            AssertEx.True(Math.Abs(Offset(1.999)) / 0.001 < 0.02);
        }
    }

    public static void ZeroCyclesRunsUntilReleasedEvenAtVeryLargeTime()
    {
        foreach (string kind in new[] { "sway", "spin", "squash" })
        {
            CharacterPresetCommand command = Parse($"#fx;1;{kind};frequency=2;cycles=0");
            CharacterPresetFrame early = CharacterPresetEvaluator.Sample(command, 0.625);
            CharacterPresetFrame late = CharacterPresetEvaluator.Sample(command, 50000.625);
            AssertEx.Equal(early, late);
            foreach (double time in new[] { 100d, 1000000d, double.MaxValue })
            {
                CharacterPresetFrame sample = CharacterPresetEvaluator.Sample(command, time);
                AssertEx.False(sample.Completed);
                AssertEx.True(float.IsFinite(sample.RotationDegrees) && float.IsFinite(sample.YawDegrees)
                    && float.IsFinite(sample.ScaleX) && float.IsFinite(sample.ScaleY));
                Near(1, sample.ScaleX * sample.ScaleY);
            }
        }
    }

    public static void HeadbuttHasThreeDirectionalStagesAndExactRecovery()
    {
        CharacterPresetCommand command = Parse("#fx;1;headbutt;back=20;forward=60;duration=1000;direction=right");
        Near(10, CharacterPresetEvaluator.Sample(command, 0.15).RotationDegrees);
        Near(20, CharacterPresetEvaluator.Sample(command, 0.30).RotationDegrees);
        Near(-20, CharacterPresetEvaluator.Sample(command, 0.375).RotationDegrees);
        Near(-60, CharacterPresetEvaluator.Sample(command, 0.45).RotationDegrees);
        Near(-30, CharacterPresetEvaluator.Sample(command, 0.725).RotationDegrees);
        foreach (double time in new[] { 0.15, 0.3, 0.375, 0.45, 0.725 })
        {
            CharacterPresetFrame right = CharacterPresetEvaluator.Sample(command, time);
            CharacterPresetFrame left = CharacterPresetEvaluator.Sample(command with { Direction = -1 }, time);
            Near(-right.RotationDegrees, left.RotationDegrees);
            Near(0, right.YawDegrees); Near(0, left.YawDegrees);
            Near(1, right.ScaleX); Near(1, right.ScaleY);
        }
        AssertNeutral(CharacterPresetEvaluator.Sample(command, 1), completed: true);
        foreach (double boundary in new[] { 0.3, 0.45, 1d })
        {
            double before = CharacterPresetEvaluator.Sample(command, boundary - 0.00001).RotationDegrees;
            double at = CharacterPresetEvaluator.Sample(command, boundary).RotationDegrees;
            Near(at, before, 0.00001);
        }
    }

    private static CharacterPresetCommand Parse(string directive)
    {
        var parsed = new CharacterPresetDirectiveParser().Parse(directive);
        AssertEx.True(parsed.Success, parsed.Error);
        return AssertEx.NotNull(parsed.Value);
    }

    private static void AssertNeutral(CharacterPresetFrame frame, bool completed)
    {
        AssertEx.Equal(CharacterPresetFrame.Neutral(completed), frame);
    }

    private static void Near(double expected, double actual, double tolerance = 0.0001) =>
        AssertEx.True(double.IsFinite(actual) && Math.Abs(expected - actual) <= tolerance,
            $"Expected {expected} ± {tolerance}, got {actual}.");
}
