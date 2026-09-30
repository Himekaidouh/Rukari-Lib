using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Formats.Aap;

namespace AzureArchive.VideoTools.Tests;

internal static class EmbeddedDiagnosticsTests
{
    public static void BoundaryCaptureLineRecordsFieldsWithoutScriptContent()
    {
        const string original =
            "#wait;100\r\n"
            + " #aavt;char;3;move;dx=500;duration=800;easing=EASEINOUT \r\n"
            + "#hidemenu";

        EmbeddedAavtExtraction extracted =
            new EmbeddedAavtDirectiveExtractor().Extract(original);
        CompiledScriptIdentity sanitized =
            CommandIdentity.CompiledScript(extracted.SanitizedText);

        string line = EmbeddedDirectiveDiagnostics.BoundaryCaptureLine(
            "CompileScriptContinuous",
            original,
            extracted,
            sanitized);

        AssertEx.True(line.Contains("boundary=CompileScriptContinuous"), line);
        AssertEx.True(line.Contains($"originalChars={original.Length}"), line);
        AssertEx.True(line.Contains("originalLines=3"), line);
        AssertEx.True(line.Contains("removedLines=1"), line);
        AssertEx.True(line.Contains("commands=1"), line);
        AssertEx.True(line.Contains("errors=0"), line);
        AssertEx.True(line.Contains("continue=False"), line);
        AssertEx.True(line.Contains("nonVisualOnly=False"), line);
        AssertEx.True(line.Contains($"sanitizedSha256={sanitized.Sha256}"), line);
        AssertEx.True(line.Contains($"sanitizedChars={sanitized.Utf16Length}"), line);
        AssertEx.True(line.Contains($"sanitizedLines={sanitized.LineCount}"), line);
        AssertEx.False(line.Contains("#aavt"), line);
        AssertEx.False(line.Contains("#char"), line);
        AssertEx.False(line.Contains("dx=500"), line);
        AssertEx.False(line.Contains("easing="), line);
        AssertEx.False(line.Contains("#wait"), line);
        AssertEx.False(line.Contains("#hidemenu"), line);
        AssertEx.False(line.Contains(extracted.SanitizedText), line);
    }

    public static void ContaminationFailureCarriesStableReasonAndRecord()
    {
        ProjectSnapshot project = SyntheticProject();
        PlaybackArchiveSnapshot contaminated = Playback(
            project,
            "A",
            "compiled A\n#aavt;char;3;move;dx=500");

        var result = new EmbeddedProjectCommandCompiler().Compile(
            project,
            contaminated,
            "0.7.35");

        AssertEx.False(result.Success);
        AssertEx.True(
            result.Error.Contains("reason=aas-record-contaminated"),
            result.Error);
        AssertEx.True(result.Error.Contains("record=0"), result.Error);
        AssertEx.True(result.Error.Contains("Recompile"), result.Error);
        AssertEx.False(result.Error.Contains("dx=500"), result.Error);
    }

    public static void InvalidEmbeddedSceneErrorCarriesStableReason()
    {
        using var files = new TemporaryAapDirectory();
        string projectPath = files.Write(
            "invalid-embedded.aap",
            SyntheticAap.Project(new[]
            {
                SyntheticAap.Scene("A", additionalPrompt: "#wait;20\n#aavt;camera;set;zoom=0")
            }));
        var read = new AapProjectReader().Read(projectPath);
        AssertEx.True(read.Success, read.Error);
        ProjectSnapshot project = AssertEx.NotNull(read.Value);
        PlaybackArchiveSnapshot playback = Playback(project, "A", "compiled A\n#wait;20");

        var result = new EmbeddedProjectCommandCompiler().Compile(
            project,
            playback,
            "0.7.35");

        AssertEx.False(result.Success);
        AssertEx.True(
            result.Error.Contains("reason=embedded-command-invalid"),
            result.Error);
        AssertEx.True(
            result.Error.Contains(SyntheticAap.ScriptNodeGuid + ":0"),
            result.Error);
        AssertEx.False(result.Error.Contains("camera;set"), result.Error);
    }

    private static PlaybackArchiveSnapshot Playback(
        ProjectSnapshot project,
        string dialogue,
        string compiledScript)
    {
        var source = new PlaybackArchiveSourceSnapshot(
            Path.ChangeExtension(project.Source.FullPath, ".aas"),
            new string('B', 64),
            new string('C', 64),
            1,
            project.Source.LastWriteTimeUtc + TimeSpan.FromSeconds(1));
        var record = new PlaybackRecordSnapshot(
            0,
            0,
            0,
            0,
            string.Empty,
            0,
            0,
            0,
            string.Empty,
            compiledScript,
            dialogue,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            false,
            new string('E', 64));
        return new PlaybackArchiveSnapshot(
            source,
            "synthetic/v1",
            Array.AsReadOnly(new[] { record }));
    }

    private static ProjectSnapshot SyntheticProject()
    {
        using var files = new TemporaryAapDirectory();
        string path = files.Write(
            "embedded-diagnostics.aap",
            SyntheticAap.Project(new[] { SyntheticAap.Scene("A") }));
        var read = new AapProjectReader().Read(path);
        AssertEx.True(read.Success, read.Error);
        return AssertEx.NotNull(read.Value);
    }
}
