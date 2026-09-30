using AzureArchive.VideoTools.Core.Characters;
using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Mapping;
using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Core.Results;
using AzureArchive.VideoTools.Core.Workspaces;
using AzureArchive.VideoTools.Formats.Aap;

namespace AzureArchive.VideoTools.Tests;

internal static class SlotPendingTests
{
    public static void CanonicalizesSlotPendingDirective()
    {
        var compiler = new SlotPendingCommandFamilyCompiler();

        var set = compiler.Canonicalize(
            " #CHARP ; 3 ; SET ; x = 0500 ; flipX=TRUE ; y=-120 ; rotation=-0 ");
        AssertEx.True(set.Success, set.Error);
        CanonicalTimelineCommand setCommand = AssertEx.NotNull(set.Value);
        AssertEx.Equal(SlotPendingCommandFamilyCompiler.CommandTypeId, setCommand.CommandType);
        AssertEx.Equal(SlotPendingCommandFamilyCompiler.CapabilityId, setCommand.RequiredCapability);
        AssertEx.Equal(3, setCommand.PublicSlot);
        AssertEx.Equal(
            "#charp;3;set;x=500;y=-120;rotation=0;flipX=true",
            setCommand.Directive);

        var move = compiler.Canonicalize("#charp;2;move;dx=-200;drotation=720");
        AssertEx.True(move.Success, move.Error);
        AssertEx.Equal(
            "#charp;2;move;dx=-200;drotation=720",
            AssertEx.NotNull(move.Value).Directive);

        var reset = compiler.Canonicalize("#charp;3;reset");
        AssertEx.False(reset.Success);
        AssertEx.True(
            reset.Error.Contains("reset", StringComparison.OrdinalIgnoreCase),
            reset.Error);

        var duration = compiler.Canonicalize("#charp;3;move;dx=100;duration=250");
        AssertEx.False(duration.Success);

        var easing = compiler.Canonicalize("#charp;3;set;x=1;easing=easeIn");
        AssertEx.False(easing.Success);
    }

    public static void ExtractorAcceptsCharPendingNamespace()
    {
        var extractor = new EmbeddedAavtDirectiveExtractor();
        EmbeddedAavtExtraction extraction = extractor.Extract(
            "普通台词\r\n#aavt;charPending;3;set;x=100;flipX=true\n尾随文本");

        AssertEx.Equal(0, extraction.Errors.Count);
        AssertEx.Equal(1, extraction.RemovedLineCount);
        AssertEx.Equal(1, extraction.Commands.Count);
        AssertEx.True(AssertEx.NotNull(extraction.Commands[0]).CanonicalDirective.StartsWith(
            "#charp;3;set;",
            StringComparison.Ordinal));
        AssertEx.True(extraction.SanitizedText.Contains("普通台词", StringComparison.Ordinal));
        AssertEx.True(extraction.SanitizedText.Contains("尾随文本", StringComparison.Ordinal));
        AssertEx.False(extraction.SanitizedText.Contains("charPending", StringComparison.Ordinal));
    }

    public static void ExtractorRejectsSameSlotAcrossFamilies()
    {
        var extractor = new EmbeddedAavtDirectiveExtractor();
        EmbeddedAavtExtraction extraction = extractor.Extract(
            "#aavt;char;3;move;dx=100\n#aavt;charPending;3;set;x=200");

        AssertEx.Equal(1, extraction.Commands.Count);
        AssertEx.True(extraction.Commands.Single().CanonicalDirective.StartsWith(
            "#char;",
            StringComparison.Ordinal));
        AssertEx.Equal(1, extraction.Errors.Count);
        AssertEx.True(
            extraction.Errors[0].Contains("already has an AAVT character directive",
                StringComparison.Ordinal),
            extraction.Errors[0]);
    }

    public static void EmbeddedCompilerEmitsPendingFamilyProjection()
    {
        using var files = new TemporaryAapDirectory();
        string path = files.Write(
            "pending.aap",
            SyntheticAap.Project(new[]
            {
                SyntheticAap.Scene(
                    "A",
                    additionalPrompt: "#aavt;charPending;3;set;x=500;flipX=true"),
                SyntheticAap.Scene("B")
            }));
        ProjectSnapshot project = AssertEx.NotNull(new AapProjectReader().Read(path).Value);
        PlaybackArchiveSnapshot playback = Playback(
            project,
            new[] { "A", "B" },
            new[] { "compiled A", "compiled B" });

        var result = new EmbeddedProjectCommandCompiler().Compile(
            project,
            playback,
            "0.7.38");

        AssertEx.True(result.Success, result.Error);
        EmbeddedProjectCommandCompilation compilation = AssertEx.NotNull(result.Value);
        AssertEx.Equal(1, compilation.CommandCount);
        PlaybackCommandBatch batch = compilation.Projection.Batches.Single();
        AssertEx.Equal(0, batch.PlaybackRecordIndex);
        AssertEx.Equal(
            SlotPendingCommandFamilyCompiler.CommandTypeId,
            batch.Commands[0].CommandType);
        AssertEx.Equal(
            SlotPendingCommandFamilyCompiler.CapabilityId,
            batch.Commands[0].RequiredCapability);
        AssertEx.True(batch.Commands[0].CanonicalDirective.StartsWith(
            "#charp;",
            StringComparison.Ordinal));

        var transformOnly = new EmbeddedProjectCommandCompiler().Compile(
            ReadProject(files, "transform-only.aap", "A", "B"),
            Playback(
                ReadProject(files, "transform-only.aap", "A", "B"),
                new[] { "A", "B" },
                new[] { "compiled A", "compiled B" }),
            "0.7.38");
        AssertEx.True(transformOnly.Success, transformOnly.Error);
    }

    public static void TimelineCompilerRejectsOfficialTransitionForPendingPosition()
    {
        using var files = new TemporaryAapDirectory();
        string path = files.Write(
            "pending-official-conflict.aap",
            SyntheticAap.Project(new[]
            {
                SyntheticAap.SceneWithCharacterTransition("A", 3, 3, 4)
            }));
        ProjectSnapshot project = AssertEx.NotNull(
            new AapProjectReader().Read(path).Value);
        PlaybackArchiveSnapshot playback = Playback(project, new[] { "A" }, new[] { "compiled A" });
        ModWorkspaceManifest workspace = Workspace(project, playback);
        SceneSnapshot scene = project.ScriptNodes.Single().Scenes.Single();

        var conflict = new CommandTimelineCompiler().Compile(
            project,
            playback,
            workspace,
            Timeline(
                project,
                workspace,
                new CommandTimelineEntry(
                    scene.Key,
                    Array.AsReadOnly(new[]
                    {
                        PendingCommand("#charp;3;set;x=500")
                    }))));
        AssertEx.False(conflict.Success);
        AssertEx.True(
            conflict.Error.Contains("official position transition", StringComparison.Ordinal),
            conflict.Error);

        var rotationOnly = new CommandTimelineCompiler().Compile(
            project,
            playback,
            workspace,
            Timeline(
                project,
                workspace,
                new CommandTimelineEntry(
                    scene.Key,
                    Array.AsReadOnly(new[]
                    {
                        PendingCommand("#charp;3;set;rotation=15")
                    }))));
        AssertEx.True(rotationOnly.Success, rotationOnly.Error);
    }

    public static void StoreLastWriteWinsConsumesAndExpires()
    {
        var store = new SlotPendingStore(expiryWindowCount: 2);
        Result<CharacterTransformCommand> first =
            new SlotPendingCommandFamilyCompiler().ParseCanonical("#charp;3;set;x=100");
        Result<CharacterTransformCommand> second =
            new SlotPendingCommandFamilyCompiler().ParseCanonical("#charp;3;move;dx=50");
        AssertEx.True(first.Success && second.Success);

        Result<SlotPendingEntry> stored = store.Store(
            "scene-a",
            AssertEx.NotNull(first.Value),
            createdWindowSequence: 10);
        AssertEx.True(stored.Success, stored.Error);
        AssertEx.Equal(1, store.Count);

        Result<SlotPendingEntry> replaced = store.Store(
            "scene-a",
            AssertEx.NotNull(second.Value),
            createdWindowSequence: 12);
        AssertEx.True(replaced.Success, replaced.Error);
        AssertEx.Equal(1, store.Count);
        AssertEx.True(store.TryPeek(3, out SlotPendingEntry peeked));
        AssertEx.Equal(CharacterTransformOperation.Move, peeked.Command.Operation);

        AssertEx.True(store.TryConsume(3, out SlotPendingEntry consumed));
        AssertEx.Equal(CharacterTransformOperation.Move, consumed.Command.Operation);
        AssertEx.Equal(0, store.Count);
        AssertEx.False(store.TryConsume(3, out _));

        Result<CharacterTransformCommand> stale =
            new SlotPendingCommandFamilyCompiler().ParseCanonical("#charp;4;set;y=9");
        AssertEx.True(store.Store("scene-b", AssertEx.NotNull(stale.Value), 20).Success);
        IReadOnlyList<SlotPendingEntry> expired = store.ExpireBefore(22);
        AssertEx.Equal(0, expired.Count);
        expired = store.ExpireBefore(23);
        AssertEx.Equal(1, expired.Count);
        AssertEx.Equal(4, expired[0].PublicSlot);
        AssertEx.Equal(0, store.Count);

        store.Store("scene-c", AssertEx.NotNull(first.Value), 1);
        store.Clear();
        AssertEx.Equal(0, store.Count);
    }

    public static void TargetPlannerComputesFromEntryState()
    {
        var entryState = new CharacterTransformState(
            new CharacterVector3(2960f / 2f, 0f, -1f),
            new CharacterVector3(0f, 0f, 0f));

        Result<CharacterTransformTarget> absolute =
            SlotPendingTargetPlanner.Compute(
                Parse("#charp;3;set;x=500;y=-200;rotation=30"),
                entryState);
        AssertEx.True(absolute.Success, absolute.Error);
        CharacterTransformTarget absoluteTarget = AssertEx.NotNull(absolute.Value);
        AssertEx.Equal(500f, absoluteTarget.State.Position.X);
        AssertEx.Equal(-200f, absoluteTarget.State.Position.Y);
        AssertEx.Equal(-1f, absoluteTarget.State.Position.Z);
        AssertEx.Equal(30f, absoluteTarget.State.LocalEulerAngles.Z);
        AssertEx.True(absoluteTarget.PositionChanged);
        AssertEx.True(absoluteTarget.RotationChanged);
        AssertEx.Equal(0, absoluteTarget.DurationMilliseconds);

        Result<CharacterTransformTarget> omitted =
            SlotPendingTargetPlanner.Compute(
                Parse("#charp;3;set;flipX=true"),
                entryState);
        AssertEx.True(omitted.Success, omitted.Error);
        CharacterTransformTarget flipTarget = AssertEx.NotNull(omitted.Value);
        AssertEx.Equal(entryState.Position.X, flipTarget.State.Position.X);
        AssertEx.Equal(-180f, flipTarget.State.LocalEulerAngles.Y);
        AssertEx.False(flipTarget.PositionChanged);

        Result<CharacterTransformTarget> moved =
            SlotPendingTargetPlanner.Compute(
                Parse("#charp;3;move;dx=-300;drotation=-45"),
                entryState);
        AssertEx.True(moved.Success, moved.Error);
        CharacterTransformTarget moveTarget = AssertEx.NotNull(moved.Value);
        AssertEx.Equal(entryState.Position.X - 300f, moveTarget.State.Position.X);
        AssertEx.Equal(-45f, moveTarget.State.LocalEulerAngles.Z);

        Result<CharacterTransformTarget> reset =
            SlotPendingTargetPlanner.Compute(Parse("#charp;3;reset"), entryState);
        AssertEx.False(reset.Success);

        Result<CharacterTransformTarget> invalidState =
            SlotPendingTargetPlanner.Compute(
                Parse("#charp;3;set;x=1"),
                new CharacterTransformState(
                    new CharacterVector3(float.NaN, 0f, 0f),
                    new CharacterVector3(0f, 0f, 0f)));
        AssertEx.False(invalidState.Success);
    }

    private static CharacterTransformCommand Parse(string canonical)
    {
        Result<CharacterTransformCommand> parsed =
            new SlotPendingCommandFamilyCompiler().ParseCanonical(canonical);
        AssertEx.True(parsed.Success, parsed.Error);
        return AssertEx.NotNull(parsed.Value);
    }

    private static AuthoringCommand PendingCommand(string directive, int order = 0) => new(
        Guid.NewGuid().ToString("D"),
        order,
        CommandTimelinePhase.SceneEnter,
        SlotPendingCommandFamilyCompiler.CommandTypeId,
        directive,
        Enabled: true);

    private static CommandTimelineDocument Timeline(
        ProjectSnapshot project,
        ModWorkspaceManifest workspace,
        params CommandTimelineEntry[] entries) => new(
        CommandTimelineDocument.CurrentSchemaVersion,
        workspace.WorkspaceId,
        project.Source.PathKey,
        project.Source.RevisionSha256,
        Array.AsReadOnly(entries));

    private static ModWorkspaceManifest Workspace(
        ProjectSnapshot project,
        PlaybackArchiveSnapshot playback) => new(
        ModWorkspaceManifest.CurrentSchemaVersion,
        Guid.NewGuid().ToString("D"),
        "0.7.17",
        "0.7.17",
        new ModWorkspaceSourceBinding(
            project.Source.PathKey,
            project.Source.RevisionSha256,
            playback.Source.PathKey,
            playback.Source.RevisionSha256,
            playback.SchemaName),
        Array.AsReadOnly(new[]
        {
            CharacterTransformCommandFamilyCompiler.CapabilityId,
            SlotPendingCommandFamilyCompiler.CapabilityId
        }));

    private static ProjectSnapshot ReadProject(
        TemporaryAapDirectory files,
        string fileName,
        params string[] dialogue)
    {
        string path = files.Write(
            fileName,
            SyntheticAap.Project(
                dialogue.Select(text => SyntheticAap.Scene(text)).ToArray()));
        var result = new AapProjectReader().Read(path);
        AssertEx.True(result.Success, result.Error);
        return AssertEx.NotNull(result.Value);
    }

    private static PlaybackArchiveSnapshot Playback(
        ProjectSnapshot project,
        IReadOnlyList<string> dialogue,
        IReadOnlyList<string> compiledScripts)
    {
        AssertEx.Equal(dialogue.Count, compiledScripts.Count);
        string fullPath = Path.ChangeExtension(project.Source.FullPath, ".aas");
        var source = new PlaybackArchiveSourceSnapshot(
            fullPath,
            new string('B', 64),
            new string('C', 64),
            dialogue.Count,
            project.Source.LastWriteTimeUtc + TimeSpan.FromSeconds(1));
        PlaybackRecordSnapshot[] records = dialogue
            .Select((text, index) => Record(index, text, compiledScripts[index]))
            .ToArray();
        return new PlaybackArchiveSnapshot(
            source,
            "synthetic/v1",
            Array.AsReadOnly(records));
    }

    private static PlaybackRecordSnapshot Record(
        int index,
        string text,
        string compiledScript) => new(
        index,
        0,
        0,
        0,
        string.Empty,
        0,
        0,
        0,
        string.Empty,
        compiledScript,
        text,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        false,
        index.ToString("X64"));
}
