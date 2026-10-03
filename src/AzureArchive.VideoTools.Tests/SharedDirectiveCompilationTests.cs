using AzureArchive.VideoTools.Core.Commands;
using Rukari.Lib.Commands;

namespace AzureArchive.VideoTools.Tests;

internal static class SharedDirectiveCompilationTests
{
    public static void ForeignChildRoutesNeverExecuteThroughTheAavtParent()
    {
        using var service = new EmbeddedDirectiveService(static () => true, static () => true, null);
        var tracker = new AavtCompilationCaptureTracker();
        EmbeddedDirectiveCompilationCapture? captured = null;
        AssertEx.True(service.Register("rukari.moreeffects", new[] { "#aavt", "#char" },
            compilation => captured = tracker.Capture(compilation, true)).Success);
        AssertEx.True(service.Register("foreign", new[] { "#aavt;camera", "#aavt;voice" }, static _ => { }).Success);
        service.Process("#aavt;camera;set;x=10\n#aavt;voice;intro.ogg\n正文",
            DirectiveCompilationBoundary.Continuous, 70, true);
        EmbeddedDirectiveCompilationCapture foreign = AssertEx.NotNull(captured);
        AssertEx.Equal("正文", foreign.Extraction.SanitizedText);
        AssertEx.Equal(0, foreign.Extraction.Commands.Count);
        AssertEx.False(foreign.Extraction.HasEmbeddedDirectives);
        AssertEx.False(foreign.Extraction.HasOnlyNonVisualDirectives);
        AssertEx.False(foreign.AavtSeenEarlierInCompilation);
        // A malformed own command keeps its original line number after foreign lines are excluded.
        service.Process("#aavt;camera;set;x=10\n#aavt;char;3;invalid\n正文",
            DirectiveCompilationBoundary.Continuous, 71, true);
        AssertEx.True(AssertEx.NotNull(captured).Extraction.Errors.Any(error => error.StartsWith("Line 2:", StringComparison.Ordinal)));
    }

    public static void ForeignReservedRoutesBlockEditorWritesButIndependentRoutesDoNot()
    {
        using var service = new EmbeddedDirectiveService(static () => true, static () => true, null);
        AssertEx.True(service.Register("rukari.moreeffects", new[] { "#aavt", "#char" }, static _ => { }).Success);
        AssertEx.True(service.Register("foreign", new[] { "#aavt;camera", "#other" }, static _ => { }).Success);
        var snapshot = service.CaptureSanitizer();
        AssertEx.True(AavtDirectiveOwnershipPolicy.FindEditorConflict("#aavt;camera;set;x=1", snapshot) != null);
        AssertEx.True(AavtDirectiveOwnershipPolicy.FindEditorConflict("#other;flash\n#aavt;char;3;set;x=1", snapshot) == null);
    }

    public static void SharedRouteWhitespaceAndMissingArgumentsCannotDisappearSilently()
    {
        using var service = new EmbeddedDirectiveService(static () => true, static () => true, null);
        var tracker = new AavtCompilationCaptureTracker();
        EmbeddedDirectiveCompilationCapture? captured = null;
        AssertEx.True(service.Register("effects", new[] { "#aavt", "#char" }, compilation =>
            captured = tracker.Capture(compilation, true)).Success);
        service.Process(" #aavt ; char ;3;set;x=500\n正文", DirectiveCompilationBoundary.Standalone, 90, true);
        EmbeddedDirectiveCompilationCapture spaced = AssertEx.NotNull(captured);
        AssertEx.Equal(1, spaced.Extraction.Commands.Count);
        AssertEx.Equal(0, spaced.Extraction.Errors.Count);
        service.Process("#char\n正文", DirectiveCompilationBoundary.Standalone, 91, true);
        EmbeddedDirectiveCompilationCapture malformed = AssertEx.NotNull(captured);
        AssertEx.True(malformed.Extraction.HasEmbeddedDirectives);
        AssertEx.True(malformed.Extraction.Errors.Count > 0);
        AssertEx.False(malformed.Extraction.HasOnlyNonVisualDirectives);
    }

    public static void MixedNamespacesShareFinalIdentityWithoutChangingOfficialBytes()
    {
        using var service = new EmbeddedDirectiveService(static () => true, static () => true, null);
        var tracker = new AavtCompilationCaptureTracker();
        EmbeddedDirectiveCompilationCapture? captured = null;
        AssertEx.True(service.Register("effects", new[] { "#aavt", "#char" }, compilation =>
            captured = tracker.Capture(compilation, true)).Success);
        AssertEx.True(service.Register("other", new[] { "#other" }, static _ => { }).Success);
        const string source = "#wait;100\r\n#aavt;char;3;set;x=500\n#other;flash\r\n正文\r\n#unregistered;keep";
        const string expected = "#wait;100\r\n正文\r\n#unregistered;keep";
        string official = service.Process(source, DirectiveCompilationBoundary.Continuous, 1, true);
        EmbeddedDirectiveCompilationCapture result = AssertEx.NotNull(captured);
        AssertEx.Equal(expected, official);
        AssertEx.Equal(expected, result.Extraction.SanitizedText);
        AssertEx.Equal(CommandIdentity.CompiledScript(expected), result.Identity);
        AssertEx.Equal(1, result.Extraction.Commands.Count);
        AssertEx.Equal(0, result.Extraction.Errors.Count);
        AssertEx.Equal(1, result.Extraction.RemovedLineCount);
        AssertEx.True(source.Contains("#other;flash", StringComparison.Ordinal));
    }

    public static void NestedCleanReturnCannotDeleteOwnCaptureButNextCompileCan()
    {
        using var service = new EmbeddedDirectiveService(static () => true, static () => true, null);
        var tracker = new AavtCompilationCaptureTracker();
        var captures = new List<EmbeddedDirectiveCompilationCapture>();
        AssertEx.True(service.Register("effects", new[] { "#aavt" }, compilation =>
            captures.Add(tracker.Capture(compilation, true))).Success);
        AssertEx.True(service.Register("other", new[] { "#other" }, static _ => { }).Success);

        string inner = service.Process("#aavt;continue\n#other;flash\n正文",
            DirectiveCompilationBoundary.Continuous, 10, true);
        service.Process(inner, DirectiveCompilationBoundary.ScriptText, 10, true);
        AssertEx.True(captures[0].Extraction.HasContinueDirective);
        AssertEx.False(captures[1].Extraction.HasEmbeddedDirectives);
        AssertEx.True(captures[1].AavtSeenEarlierInCompilation);

        // The next authored compile removed AAVT and kept a foreign line. It is a real tombstone.
        service.Process("#other;flash\n正文", DirectiveCompilationBoundary.Continuous, 11, true);
        AssertEx.Equal(captures[0].Identity, captures[2].Identity);
        AssertEx.False(captures[2].Extraction.HasEmbeddedDirectives);
        AssertEx.False(captures[2].AavtSeenEarlierInCompilation);
    }

    public static void ForeignOnlyNestedCaptureDoesNotSuppressAnAavtDeletion()
    {
        using var service = new EmbeddedDirectiveService(static () => true, static () => true, null);
        var tracker = new AavtCompilationCaptureTracker();
        var captures = new List<EmbeddedDirectiveCompilationCapture>();
        AssertEx.True(service.Register("effects", new[] { "#aavt" }, compilation =>
            captures.Add(tracker.Capture(compilation, true))).Success);
        AssertEx.True(service.Register("other", new[] { "#other" }, static _ => { }).Success);
        string inner = service.Process("#other;flash\n正文", DirectiveCompilationBoundary.Continuous, 20, true);
        service.Process(inner, DirectiveCompilationBoundary.ScriptText, 20, true);
        AssertEx.Equal(2, captures.Count);
        AssertEx.True(captures.All(capture => !capture.Extraction.HasEmbeddedDirectives));
        AssertEx.True(captures.All(capture => !capture.AavtSeenEarlierInCompilation));
    }

    public static void VoiceOnlyChildRouteStillGivesEffectsAnExplicitTombstone()
    {
        using var service = new EmbeddedDirectiveService(static () => true, static () => true, null);
        var tracker = new AavtCompilationCaptureTracker();
        EmbeddedDirectiveCompilationCapture? captured = null;
        int effectsOwned = -1;
        int voiceOwned = -1;
        AssertEx.True(service.Register("effects", new[] { "#aavt" }, compilation =>
        {
            effectsOwned = compilation.OwnedDirectives.Count;
            captured = tracker.Capture(compilation, true);
        }).Success);
        AssertEx.True(service.Register("rukari.charactervoice", new[] { "#aavt;voice" },
            compilation => voiceOwned = compilation.OwnedDirectives.Count).Success);
        string official = service.Process("#aavt;voice;characters/intro.ogg\n正文",
            DirectiveCompilationBoundary.Continuous, 30, true);
        EmbeddedDirectiveCompilationCapture result = AssertEx.NotNull(captured);
        AssertEx.Equal("正文", official);
        AssertEx.Equal(0, effectsOwned);
        AssertEx.Equal(1, voiceOwned);
        AssertEx.True(result.Extraction.HasOnlyNonVisualDirectives);
        AssertEx.False(result.Extraction.HasContinueDirective);
        AssertEx.Equal(0, result.Extraction.Commands.Count);
        AssertEx.Equal(0, result.Extraction.Errors.Count);
    }

    public static void VoiceCanFilterIndependentlyWithoutClaimingEffectsOrForeignLines()
    {
        using var service = new EmbeddedDirectiveService(static () => true, static () => true, null);
        var registration = service.Register("voice", new[] { "#aavt;voice" }, static _ => { });
        AssertEx.True(registration.Success);
        const string source = "#aavt;voice;intro.ogg\r\n#aavt;char;3;set;x=10\n#other;keep\n#wait;100";
        AssertEx.Equal("#aavt;char;3;set;x=10\n#other;keep\n#wait;100",
            service.Process(source, DirectiveCompilationBoundary.Standalone, 40, true));
        registration.Value.Dispose();
        AssertEx.Equal(source, service.Sanitize(source));
    }

    public static void RegisteredAavtFilteringPreservesUnregisteredFlBytes()
    {
        using var service = new EmbeddedDirectiveService(static () => true, static () => true, null);
        var tracker = new AavtCompilationCaptureTracker();
        EmbeddedDirectiveCompilationCapture? captured = null;
        AssertEx.True(service.Register("rukari.moreeffects", new[] { "#aavt", "#char" },
            compilation => captured = tracker.Capture(compilation, true)).Success);

        // FL stays unregistered here: argument interpretation belongs to the peer mod.
        foreach (DirectiveCompilationBoundary boundary in Enum.GetValues<DirectiveCompilationBoundary>())
        foreach (string ending in new[] { "\r\n", string.Empty })
        {
            string source = " \t#FL-Move;slot=2;x=1.25 \t\r\n"
                + "#aavt;char;3;set;x=10\r\n"
                + "正文\r\n#FL-Move;slot=4;x=-3" + ending;
            string expected = " \t#FL-Move;slot=2;x=1.25 \t\r\n"
                + "正文\r\n#FL-Move;slot=4;x=-3" + ending;
            string official = service.Process(source, boundary, 100, true);
            EmbeddedDirectiveCompilationCapture result = AssertEx.NotNull(captured);
            AssertEx.Equal(expected, official);
            AssertEx.Equal(expected, result.Extraction.SanitizedText);
            AssertEx.Equal(CommandIdentity.CompiledScript(expected), result.Identity);
            AssertEx.Equal(1, result.Extraction.RemovedLineCount);
            AssertEx.Equal(0, result.Extraction.Errors.Count);
            AssertEx.Equal(2, result.Extraction.Commands.Single().LineNumber);
            AssertEx.Equal("#char;3;set;x=10;duration=0;easing=linear",
                result.Extraction.Commands.Single().CanonicalDirective);
        }

        // Removing our last line must preserve the preceding foreign CRLF.
        AssertEx.Equal("#FL-Move;slot=2;x=7\r\n",
            service.Process("#FL-Move;slot=2;x=7\r\n#aavt;char;3;reset",
                DirectiveCompilationBoundary.Continuous, 101, true));
    }

    public static void MixedFlCapturesReplaceOwnCanonicalCommandsAndPermitDeletion()
    {
        using var service = new EmbeddedDirectiveService(static () => true, static () => true, null);
        var tracker = new AavtCompilationCaptureTracker();
        var captures = new List<EmbeddedDirectiveCompilationCapture>();
        AssertEx.True(service.Register("rukari.moreeffects", new[] { "#aavt", "#char" },
            compilation => captures.Add(tracker.Capture(compilation, true))).Success);
        const string foreignAndBody = "#FL-Move;slot=2;x=7\r\n正文";

        string first = service.Process("#aavt;char;3;set;x=10\r\n" + foreignAndBody,
            DirectiveCompilationBoundary.Continuous, 110, true);
        service.Process(first, DirectiveCompilationBoundary.ScriptText, 110, true);
        AssertEx.Equal(foreignAndBody, first);
        AssertEx.Equal("#char;3;set;x=10;duration=0;easing=linear",
            captures[0].Extraction.Commands.Single().CanonicalDirective);
        AssertEx.False(captures[0].AavtSeenEarlierInCompilation);
        AssertEx.False(captures[1].Extraction.HasEmbeddedDirectives);
        AssertEx.True(captures[1].AavtSeenEarlierInCompilation);

        string replacement = service.Process("#aavt;char;3;set;x=20\r\n" + foreignAndBody,
            DirectiveCompilationBoundary.Continuous, 111, true);
        service.Process(replacement, DirectiveCompilationBoundary.ScriptText, 111, true);
        AssertEx.Equal(captures[0].Identity, captures[2].Identity);
        AssertEx.Equal("#char;3;set;x=20;duration=0;easing=linear",
            captures[2].Extraction.Commands.Single().CanonicalDirective);
        AssertEx.False(captures[2].AavtSeenEarlierInCompilation);
        AssertEx.True(captures[3].AavtSeenEarlierInCompilation);

        string nonVisual = service.Process("#aavt;continue\r\n" + foreignAndBody,
            DirectiveCompilationBoundary.Continuous, 112, true);
        service.Process(nonVisual, DirectiveCompilationBoundary.ScriptText, 112, true);
        AssertEx.Equal(captures[0].Identity, captures[4].Identity);
        AssertEx.True(captures[4].Extraction.HasOnlyNonVisualDirectives);
        AssertEx.True(captures[4].Extraction.HasContinueDirective);
        AssertEx.Equal(0, captures[4].Extraction.Commands.Count);
        AssertEx.True(captures[5].AavtSeenEarlierInCompilation);

        // A new authored compile that deletes our final marker permits a tombstone;
        // the unregistered FL line gives no AAVT authority to suppress that deletion.
        string deleted = service.Process(foreignAndBody,
            DirectiveCompilationBoundary.Continuous, 113, true);
        service.Process(deleted, DirectiveCompilationBoundary.ScriptText, 113, true);
        AssertEx.Equal(captures[0].Identity, captures[6].Identity);
        foreach (EmbeddedDirectiveCompilationCapture result in captures.Skip(6))
        {
            AssertEx.Equal(foreignAndBody, result.Extraction.SanitizedText);
            AssertEx.False(result.Extraction.HasEmbeddedDirectives);
            AssertEx.False(result.Extraction.HasContinueDirective);
            AssertEx.Equal(0, result.Extraction.Commands.Count);
            AssertEx.Equal(0, result.Extraction.Errors.Count);
            AssertEx.False(result.AavtSeenEarlierInCompilation);
        }

        service.Process("正文", DirectiveCompilationBoundary.Continuous, 114, true);
        AssertEx.False(captures[8].Extraction.HasEmbeddedDirectives);
        AssertEx.False(captures[8].AavtSeenEarlierInCompilation);
        AssertEx.Equal(CommandIdentity.CompiledScript("正文"), captures[8].Identity);
        AssertEx.False(captures[8].Identity.Sha256 == captures[6].Identity.Sha256);
    }

    public static void ForeignFlArgumentsChangeCompiledIdentityButKeepOwnCanonicalCommands()
    {
        using var service = new EmbeddedDirectiveService(static () => true, static () => true, null);
        var tracker = new AavtCompilationCaptureTracker();
        var captures = new List<EmbeddedDirectiveCompilationCapture>();
        AssertEx.True(service.Register("rukari.moreeffects", new[] { "#aavt", "#char" },
            compilation => captures.Add(tracker.Capture(compilation, true))).Success);
        const string own = "#aavt;char;3;move;dx=10;duration=800\r\n";
        const string first = "#FL-Move;slot=2;x=7\r\n正文";
        const string changed = "#FL-Move;slot=2;x=8\r\n正文";
        AssertEx.Equal(first, service.Process(own + first, DirectiveCompilationBoundary.Continuous, 120, true));
        AssertEx.Equal(changed, service.Process(own + changed, DirectiveCompilationBoundary.Continuous, 121, true));
        AssertEx.Equal(CommandIdentity.CompiledScript(first), captures[0].Identity);
        AssertEx.Equal(CommandIdentity.CompiledScript(changed), captures[1].Identity);
        AssertEx.Equal(captures[0].Identity.Utf16Length, captures[1].Identity.Utf16Length);
        AssertEx.Equal(captures[0].Identity.LineCount, captures[1].Identity.LineCount);
        AssertEx.False(captures[0].Identity.Sha256 == captures[1].Identity.Sha256);
        AssertEx.Equal(captures[0].Extraction.Commands.Single(), captures[1].Extraction.Commands.Single());
        AssertEx.Equal("#char;3;move;dx=10;duration=800;easing=linear",
            captures[1].Extraction.Commands.Single().CanonicalDirective);
        AssertEx.True(captures.All(result => result.Extraction.Errors.Count == 0));
    }
}
