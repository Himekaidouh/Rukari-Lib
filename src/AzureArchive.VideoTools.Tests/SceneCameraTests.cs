using AzureArchive.VideoTools.Core.Cameras;
using AzureArchive.VideoTools.Core.Characters;
using AzureArchive.VideoTools.Core.Commands;

namespace AzureArchive.VideoTools.Tests;

internal static class SceneCameraTests
{
    public static void ParsesSetMoveAndReset()
    {
        var parser = new SceneCameraDirectiveParser();
        SceneCameraCommand set = AssertEx.NotNull(parser.Parse(
            "#camera;set;x=400;y=-120;zoom=1.5;duration=800;easing=easeInOut").Value);
        AssertEx.Equal(SceneCameraOperation.Set, set.Operation);
        AssertEx.Equal(400f, set.X);
        AssertEx.Equal(-120f, set.Y);
        AssertEx.Equal(1.5f, set.Zoom);
        AssertEx.Equal(800, set.DurationMilliseconds);
        AssertEx.Equal(CharacterTransformEasing.EaseInOut, set.Easing);

        SceneCameraCommand move = AssertEx.NotNull(parser.Parse(
            "#camera;move;dx=-200;dy=50;dzoom=0.25;duration=350;easing=easeOut").Value);
        AssertEx.Equal(SceneCameraOperation.Move, move.Operation);
        AssertEx.Equal(-200f, move.DeltaX);
        AssertEx.Equal(50f, move.DeltaY);
        AssertEx.Equal(0.25f, move.DeltaZoom);

        SceneCameraCommand reset = AssertEx.NotNull(
            parser.Parse("#camera;reset;duration=500;easing=linear").Value);
        AssertEx.Equal(SceneCameraOperation.Reset, reset.Operation);
    }

    public static void RejectsMalformedOrUnsafeValues()
    {
        var parser = new SceneCameraDirectiveParser();
        string[] invalid =
        {
            "#camera;set",
            "#camera;move",
            "#camera;set;zoom=0",
            "#camera;set;zoom=9",
            "#camera;set;x=NaN",
            "#camera;move;dx=1;x=2",
            "#camera;reset;x=0",
            "#camera;set;x=1;x=2",
            "#camera;set;x=1;duration=-1",
            "#camera;set;x=1;easing=bezier"
        };

        foreach (string directive in invalid)
        {
            AssertEx.False(parser.Parse(directive).Success, $"Expected rejection: {directive}");
        }

        SceneCameraCommand underflow = AssertEx.NotNull(
            parser.Parse("#camera;move;dzoom=-2").Value);
        AssertEx.False(new SceneCameraPlanner().Plan(
            underflow,
            SceneCameraState.Default,
            new SceneCameraBaseline("scene-underflow", SceneCameraState.Default)).Success);
    }

    public static void PlansPersistentStateAndReplayRestore()
    {
        var planner = new SceneCameraPlanner();
        var baseline = new SceneCameraBaseline("scene-b", new(100f, 20f, 1.25f));
        SceneCameraCommand command = AssertEx.NotNull(
            new SceneCameraDirectiveParser().Parse(
                "#camera;move;dx=300;dzoom=0.5;duration=1000;easing=easeInOut").Value);

        SceneCameraTarget target = AssertEx.NotNull(
            planner.Plan(command, baseline.State, baseline).Value);
        AssertEx.Equal(new SceneCameraState(400f, 20f, 1.75f), target.State);
        AssertEx.True(target.PositionChanged);
        AssertEx.True(target.ZoomChanged);

        SceneCameraTarget restored = AssertEx.NotNull(
            planner.PlanReplayRestore(
                command,
                new SceneCameraState(target.State.X, 70f, target.State.Zoom),
                baseline).Value);
        AssertEx.Equal(new SceneCameraState(100f, 70f, 1.25f), restored.State);
        AssertEx.Equal(0, restored.DurationMilliseconds);

        SceneCameraTarget replayed = AssertEx.NotNull(
            planner.Plan(command, restored.State, baseline).Value);
        AssertEx.Equal(new SceneCameraState(400f, 70f, 1.75f), replayed.State);
    }

    public static void ResetReturnsToNeutralCamera()
    {
        var planner = new SceneCameraPlanner();
        var baseline = new SceneCameraBaseline("scene-c", new(250f, -80f, 2f));
        SceneCameraCommand reset = AssertEx.NotNull(
            new SceneCameraDirectiveParser().Parse(
                "#camera;reset;duration=600;easing=easeOut").Value);
        SceneCameraTarget target = AssertEx.NotNull(
            planner.Plan(reset, baseline.State, baseline).Value);
        AssertEx.Equal(SceneCameraState.Default, target.State);
        AssertEx.True(target.IsReset);
    }

    public static void CanonicalizesPublicCameraFamily()
    {
        var result = new SceneCameraCommandFamilyCompiler().Canonicalize(
            "#camera;set;zoom=1.5;x=400;duration=800;easing=easeInOut");
        AssertEx.True(result.Success, result.Error);
        CanonicalTimelineCommand command = AssertEx.NotNull(result.Value);
        AssertEx.Equal(SceneCameraCommandFamilyCompiler.CommandTypeId, command.CommandType);
        AssertEx.Equal(SceneCameraCommandFamilyCompiler.SingletonResourceSlot, command.PublicSlot);
        AssertEx.Equal(
            "#camera;set;x=400;zoom=1.5;duration=800;easing=easeInOut",
            command.Directive);
    }

    public static void ExtractsPublicCameraAndRejectsDuplicates()
    {
        var extractor = new EmbeddedAavtDirectiveExtractor();
        EmbeddedAavtExtraction extracted = extractor.Extract(
            "#wait;100\n#aavt;camera;set;x=400;zoom=1.5;duration=800;easing=easeOut\n#bgshake");
        AssertEx.Equal(0, extracted.Errors.Count);
        AssertEx.Equal(1, extracted.Commands.Count);
        AssertEx.Equal("#wait;100\n#bgshake", extracted.SanitizedText);
        AssertEx.Equal(
            "#camera;set;x=400;zoom=1.5;duration=800;easing=easeOut",
            extracted.Commands[0].CanonicalDirective);
        AssertEx.Equal(0, extracted.Commands[0].PublicSlot);

        EmbeddedAavtExtraction duplicate = extractor.Extract(
            "#aavt;camera;set;zoom=1.5\n#aavt;camera;move;dx=100");
        AssertEx.Equal(1, duplicate.Errors.Count);
    }

    public static void DispatchGateAllowsFiveSlotsAndOneCamera()
    {
        var commands = new List<PlaybackCommandInstruction>();
        for (int slot = 1; slot <= 5; slot++)
        {
            commands.Add(new PlaybackCommandInstruction(
                Guid.NewGuid().ToString("D"),
                commands.Count,
                CommandTimelinePhase.SceneEnter,
                CharacterTransformCommandFamilyCompiler.CommandTypeId,
                CharacterTransformCommandFamilyCompiler.CapabilityId,
                $"#char;{slot};set;x={slot};duration=0;easing=linear"));
        }

        commands.Add(new PlaybackCommandInstruction(
            Guid.NewGuid().ToString("D"),
            commands.Count,
            CommandTimelinePhase.SceneEnter,
            SceneCameraCommandFamilyCompiler.CommandTypeId,
            SceneCameraCommandFamilyCompiler.CapabilityId,
            "#camera;set;zoom=1.5;duration=500;easing=easeInOut"));
        string fingerprint = new string('A', 64);
        var batch = new PlaybackCommandBatch(
            new AzureArchive.VideoTools.Core.Projects.SceneKey(
                Guid.NewGuid().ToString("D"), 0, fingerprint),
            0,
            fingerprint,
            new CompiledScriptIdentity(new string('B', 64), 1, 1),
            commands.AsReadOnly());

        var result = new PlaybackSceneDispatchGate().TryAuthorize(1, batch);
        AssertEx.True(result.Success, result.Error);
        AssertEx.Equal(6, AssertEx.NotNull(result.Value).Commands.Count);
    }
}
