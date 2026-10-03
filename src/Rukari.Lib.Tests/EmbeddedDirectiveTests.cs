using Rukari.Lib.Commands;

namespace Rukari.Lib.Tests;

internal static class EmbeddedDirectiveTests
{
    internal static void OfficialAndForeignLinesKeepTheirExactBytes()
    {
        using var service = Ready();
        DirectiveCompilation? context = null;
        Register(service, "example", new[] { "#example.mod" }, item => context = item);
        const string input = "#wait;100\r\n \t#example.mod;ok \t\r\n#foreign;x\ntext #example.mod;x\r#example.mod-other;x\n\n#bgshake";
        const string expected = "#wait;100\r\n#foreign;x\ntext #example.mod;x\r#example.mod-other;x\n\n#bgshake";
        Equal(expected, service.Process(input, DirectiveCompilationBoundary.ScriptText, 71, true));
        Equal(input, context!.SourceText);
        Equal(expected, context.OfficialText);
        Equal(1, context.RemovedDirectives.Count);
        Equal(" \t#example.mod;ok \t", context.OwnedDirectives[0].Text);
        Equal(2, context.OwnedDirectives[0].LineNumber);
        Equal(71L, context.CompilationId);
        True(context.IsAuthoritative);
    }

    internal static void RegisteredAavtFilteringLeavesFlLinesByteExact()
    {
        using var service = Ready();
        DirectiveCompilation? context = null;
        Register(service, "rukari.moreeffects", new[] { "#aavt", "#char" }, item => context = item);
        IEmbeddedDirectiveSanitizer snapshot = service.CaptureSanitizer();
        long compilationId = 80;
        foreach (DirectiveCompilationBoundary boundary in Enum.GetValues<DirectiveCompilationBoundary>())
        foreach (string ending in new[] { "\r\n", string.Empty })
        {
            // FL argument grammar is owned by the peer; every unregistered byte is retained.
            string input = " \t#FL-Move;slot=2;x=1.25 \t\r\n#wait;100\r\n"
                + "#aavt;char;3;set;x=5\r\n正文\r\n#FL;legacy;value=7" + ending;
            string expected = " \t#FL-Move;slot=2;x=1.25 \t\r\n#wait;100\r\n"
                + "正文\r\n#FL;legacy;value=7" + ending;
            Equal(expected, service.Process(input, boundary, compilationId++, true));
            Equal(input, context!.SourceText);
            Equal(expected, context.OfficialText);
            Equal(boundary, context.Boundary);
            Equal(1, context.RemovedDirectives.Count);
            Equal(1, context.OwnedDirectives.Count);
            Equal("#aavt;char;3;set;x=5", context.OwnedDirectives[0].Text);
            Equal(3, context.OwnedDirectives[0].LineNumber);
            Equal("rukari.moreeffects", context.OwnedDirectives[0].OwnerId);
            Equal(expected, snapshot.Sanitize(input));
            Equal(input, snapshot.SanitizeExceptOwners(input, new[] { "rukari.moreeffects" }));
        }

        const string finalOwnedLine = "#FL-Move;slot=2;x=7\r\n#char;3;reset";
        Equal("#FL-Move;slot=2;x=7\r\n", service.Sanitize(finalOwnedLine));
        const string foreignOnly = "#FL-Move;slot=2;x=7\r\n#FL;legacy;value=8";
        Equal(foreignOnly, service.Process(foreignOnly, DirectiveCompilationBoundary.Continuous, compilationId, true));
        Equal(0, context!.OwnedDirectives.Count);
        Equal(0, context.RemovedDirectives.Count);
    }

    internal static void LongestTokenRouteOwnsEachLineInEitherRegistrationOrder()
    {
        foreach (bool parentFirst in new[] { true, false })
        {
            using var service = Ready();
            DirectiveCompilation? effects = null, voice = null;
            void Parent() => Register(service, "effects", new[] { "#AAVT", "#char" }, item => effects = item);
            void Child() => Register(service, "voice", new[] { " \t#aavt ;\t VOICE " }, item => voice = item);
            if (parentFirst) { Parent(); Child(); } else { Child(); Parent(); }
            const string input = "#aavt;char;3;set;x=5\r\n #AaVt ; vOiCe ; sample \n#aavt;voiceover;x\r#char;2;x\n#wait;1";
            Equal("#wait;1", service.Process(input, DirectiveCompilationBoundary.Standalone, 3, true));
            Equal(4, effects!.RemovedDirectives.Count);
            Equal(3, effects.OwnedDirectives.Count);
            Equal(1, voice!.OwnedDirectives.Count);
            Equal("voice", voice.OwnedDirectives[0].OwnerId);
            Equal("#aavt;voice", voice.OwnedDirectives[0].Route);
            Equal(effects.OfficialText, voice.OfficialText);
            Equal("#aavt", effects.OwnedDirectives[1].Route);
        }
    }

    internal static void AllModulesSeeTheFinalTextBeforeAnyCallbackExecutes()
    {
        using var service = Ready();
        var observations = new List<DirectiveCompilation>();
        Register(service, "first", new[] { "#first" }, observations.Add);
        Register(service, "second", new[] { "#second" }, observations.Add);
        Register(service, "absent", new[] { "#absent" }, observations.Add);
        service.Process("#first;x\n#second;y\n#wait;1", DirectiveCompilationBoundary.Continuous, 5, true);
        Equal(3, observations.Count);
        foreach (var item in observations)
        {
            Equal("#wait;1", item.OfficialText);
            Equal(2, item.RemovedDirectives.Count);
        }
        Equal(0, observations[2].OwnedDirectives.Count);
        observations.Clear();
        service.Process("#wait;1", DirectiveCompilationBoundary.Continuous, 6, true);
        Equal(3, observations.Count);
        True(observations.All(item => item.OwnedDirectives.Count == 0));
    }

    internal static void MalformedOwnedInstructionsStillReachTheirOwner()
    {
        using var service = Ready();
        DirectiveCompilation? result = null;
        Register(service, "example", new[] { "#example" }, item => result = item);
        const string input = "#example\n#example;;bad\n#example;unknown;\n#examples;x\n#example bad\n";
        Equal("#examples;x\n#example bad\n", service.Process(input, DirectiveCompilationBoundary.Standalone, 1, false));
        Equal(3, result!.OwnedDirectives.Count);
        True(!result.IsAuthoritative);
        Equal("#example", result.OwnedDirectives[0].Text);
    }

    internal static void MultiRouteRegistrationConflictsAreAtomic()
    {
        using var service = Ready();
        Register(service, "existing", new[] { "#taken" }, _ => { });
        long revision = service.Revision;
        var rejected = service.Register("candidate", new[] { "#fresh", " #TAKEN " }, _ => { });
        Equal(ModErrorCode.Conflict, rejected.Error!.Code);
        Equal(revision, service.Revision);
        Equal("#fresh;x\n", service.Sanitize("#fresh;x\n#taken;x"));
        var repeated = service.Register("candidate", new[] { "#fresh", "#FRESH" }, _ => { });
        Equal(ModErrorCode.Conflict, repeated.Error!.Code);
        Equal(revision, service.Revision);
        Register(service, "candidate", new[] { "#fresh", "#fresh;child" }, _ => { });
        Equal(string.Empty, service.Sanitize("#fresh;child;x\n#fresh;x"));
    }

    internal static void InvalidNamespacesAndExcessiveRegistrationsAreRejected()
    {
        using var service = Ready();
        foreach (string route in new[] { "", "#", "example", "#example;", "#example;;x", "#example *", "#example\nx", "#example;参数", "#-invalid", "#example;" + new string('a', 65), "#example;a;b;c;d;e;f;g;h" })
            Equal(ModErrorCode.InvalidArgument, service.Register("owner", new[] { route }, _ => { }).Error!.Code);
        Equal(ModErrorCode.InvalidArgument, service.Register(" ", new[] { "#example" }, _ => { }).Error!.Code);
        Equal(ModErrorCode.InvalidArgument, service.Register("owner", Array.Empty<string>(), _ => { }).Error!.Code);
        Equal(ModErrorCode.InvalidArgument, service.Register("owner", new[] { "#example" }, null!).Error!.Code);
        Equal(ModErrorCode.InvalidArgument, service.Register("owner", Enumerable.Range(0, 33).Select(i => "#r" + i).ToArray(), _ => { }).Error!.Code);
        for (int batch = 0; batch < 16; batch++)
            Register(service, "owner" + batch, Enumerable.Range(batch * 32, 32).Select(i => "#r" + i).ToArray(), _ => { });
        Equal(ModErrorCode.Busy, service.Register("overflow", new[] { "#overflow" }, _ => { }).Error!.Code);
        Equal("#overflow", service.Sanitize("#overflow"));
    }

    internal static void RemovedLeasesCannotRemoveReplacementRegistrations()
    {
        using var service = Ready();
        IDisposable old = Register(service, "old", new[] { "#example" }, _ => { });
        old.Dispose();
        long removedRevision = service.Revision;
        old.Dispose();
        Equal(removedRevision, service.Revision);
        int calls = 0;
        Register(service, "new", new[] { "#example" }, _ => calls++);
        old.Dispose();
        Equal(string.Empty, service.Process("#example", DirectiveCompilationBoundary.ScriptText, 1, true));
        Equal(1, calls);
    }

    internal static void WorkerSanitizationHasNoCallbacksAndUsesImmutableSnapshots()
    {
        int mainThread = Environment.CurrentManagedThreadId;
        using var service = new EmbeddedDirectiveService(() => true, () => Environment.CurrentManagedThreadId == mainThread, null);
        int callbacks = 0;
        IDisposable first = Register(service, "first", new[] { "#first" }, _ => callbacks++);
        IEmbeddedDirectiveSanitizer old = service.CaptureSanitizer();
        first.Dispose();
        Register(service, "second", new[] { "#second" }, _ => callbacks++);
        IEmbeddedDirectiveSanitizer current = service.CaptureSanitizer();
        True(current.Revision > old.Revision);
        Equal("#second", Task.Run(() => old.Sanitize("#first\n#second")).GetAwaiter().GetResult());
        Equal("#first\n", Task.Run(() => current.Sanitize("#first\n#second")).GetAwaiter().GetResult());
        Equal("#first\n", Task.Run(() => service.Sanitize("#first\n#second")).GetAwaiter().GetResult());
        Equal(0, callbacks);
        var workerRegistration = Task.Run(() => service.Register("bad", new[] { "#bad" }, _ => { })).GetAwaiter().GetResult();
        Equal(ModErrorCode.WrongThread, workerRegistration.Error!.Code);
    }

    internal static void CallbackFailuresAndLoggerFailuresDoNotAffectOtherMods()
    {
        int logs = 0, healthyCalls = 0;
        using var service = new EmbeddedDirectiveService(() => true, () => true, _ => { logs++; throw new InvalidOperationException("logger"); });
        Register(service, "broken", new[] { "#broken" }, _ => throw new InvalidOperationException("callback"));
        Register(service, "healthy", new[] { "#healthy" }, _ => healthyCalls++);
        Equal("#wait;1", service.Process("#broken\n#healthy\n#wait;1", DirectiveCompilationBoundary.ScriptText, 1, true));
        Equal(1, logs);
        Equal(1, healthyCalls);
    }

    internal static void OwnerFilteredSnapshotsResolveForeignChildrenBeforeRetainingParents()
    {
        using var service = Ready();
        int callbacks = 0;
        Register(service, "effects", new[] { "#aavt" }, _ => callbacks++);
        Register(service, "voice", new[] { "#aavt;voice" }, _ => callbacks++);
        Register(service, "foreign", new[] { "#aavt;camera", "#foreign" }, _ => callbacks++);
        const string input = "#aavt;char;3;set;x=5\r\n#aavt ; camera ; reset\n#aavt;voice;sample\r#foreign;x\n#wait;1\n#unregistered;x";
        const string expected = "#aavt;char;3;set;x=5\r\n#aavt;voice;sample\r#wait;1\n#unregistered;x";
        IEmbeddedDirectiveSanitizer snapshot = service.CaptureSanitizer();
        Equal(expected, snapshot.SanitizeExceptOwners(input, new[] { "effects", "voice" }));
        Equal("#aavt;char;3;set;x=5\r\n#wait;1\n#unregistered;x", snapshot.SanitizeExceptOwners(input, new[] { "effects" }));
        Equal(snapshot.Sanitize(input), snapshot.SanitizeExceptOwners(input, Array.Empty<string>()));
        Equal(snapshot.Sanitize(input), snapshot.SanitizeExceptOwners(input, new[] { "EFFECTS" }));
        Equal(0, callbacks);
    }

    internal static void OwnerFilteredSnapshotsKeepOwnershipStableAcrossRegistrationChanges()
    {
        using var service = Ready();
        Register(service, "effects", new[] { "#aavt" }, _ => { });
        IDisposable child = Register(service, "foreign", new[] { "#aavt;camera" }, _ => { });
        IEmbeddedDirectiveSanitizer old = service.CaptureSanitizer();
        child.Dispose();
        IEmbeddedDirectiveSanitizer current = service.CaptureSanitizer();
        const string input = "#aavt;camera;reset\r\n#wait;1";
        Equal("#wait;1", Task.Run(() => old.SanitizeExceptOwners(input, new[] { "effects" })).GetAwaiter().GetResult());
        Equal(input, Task.Run(() => current.SanitizeExceptOwners(input, new[] { "effects" })).GetAwaiter().GetResult());
        True(current.Revision > old.Revision);
        service.Dispose();
        Equal("#wait;1", old.SanitizeExceptOwners(input, new[] { "effects" }));
        Equal(input, current.SanitizeExceptOwners(input, new[] { "effects" }));
    }

    internal static void NestedDispatchRetainsItsOwnImmutableContext()
    {
        using var service = Ready();
        var observations = new List<DirectiveCompilation>();
        Register(service, "nested", new[] { "#nested" }, item =>
        {
            observations.Add(item);
            if (item.CompilationId == 1)
                Equal("inner", service.Process("#nested;inside\ninner", DirectiveCompilationBoundary.Continuous, 2, false));
        });
        Equal("outer", service.Process("#nested;outside\nouter", DirectiveCompilationBoundary.ScriptText, 1, true));
        Equal(2, observations.Count);
        Equal("#nested;outside", observations[0].OwnedDirectives[0].Text);
        Equal("outer", observations[0].OfficialText);
        Equal("#nested;inside", observations[1].OwnedDirectives[0].Text);
        Equal("inner", observations[1].OfficialText);
        True(observations[0].RemovedDirectives is not RemovedDirective[]);
    }

    internal static void CallbackRegistryChangesTakeEffectAtTheNextSnapshot()
    {
        using var service = Ready();
        IDisposable? lateLease = null;
        int lateCalls = 0, replacementCalls = 0;
        Register(service, "first", new[] { "#first" }, _ =>
        {
            if (lateLease is null) return;
            lateLease.Dispose();
            lateLease = null;
            Register(service, "replacement", new[] { "#late" }, _ => replacementCalls++);
        });
        lateLease = Register(service, "late", new[] { "#late" }, _ => lateCalls++);
        Equal(string.Empty, service.Process("#late", DirectiveCompilationBoundary.ScriptText, 1, true));
        Equal(0, lateCalls);
        Equal(0, replacementCalls);
        Equal(string.Empty, service.Process("#late", DirectiveCompilationBoundary.ScriptText, 2, true));
        Equal(1, replacementCalls);
    }

    internal static void ReadinessAndShutdownNeverSilentlyConsumeUnownedText()
    {
        bool ready = false;
        int calls = 0;
        using var service = new EmbeddedDirectiveService(() => ready, () => true, null);
        Equal(ModErrorCode.NotReady, service.Register("example", new[] { "#example" }, _ => calls++).Error!.Code);
        Equal("#example", service.Sanitize("#example"));
        ready = true;
        Register(service, "example", new[] { "#example" }, _ => calls++);
        IEmbeddedDirectiveSanitizer old = service.CaptureSanitizer();
        ready = false;
        Equal("#example", service.Process("#example", DirectiveCompilationBoundary.ScriptText, 1, true));
        ready = true;
        service.Dispose();
        long stoppedRevision = service.Revision;
        service.Dispose();
        Equal(stoppedRevision, service.Revision);
        True(stoppedRevision > old.Revision);
        Equal("#example", service.Process("#example", DirectiveCompilationBoundary.ScriptText, 2, true));
        Equal("#example", service.Sanitize("#example"));
        Equal(string.Empty, old.Sanitize("#example"));
        Equal(0, calls);
        Equal(ModErrorCode.NotReady, service.Register("example", new[] { "#example" }, _ => { }).Error!.Code);
    }

    internal static void EmptyAndSingleLineDocumentsRetainTheirBoundaries()
    {
        using var service = Ready();
        int calls = 0;
        Register(service, "example", new[] { "#example" }, _ => calls++);
        Equal(string.Empty, service.Process(string.Empty, DirectiveCompilationBoundary.ScriptText, 1, true));
        Equal(1, calls);
        Equal("\n", service.Sanitize("#example\r\n\n"));
        Equal("before\r", service.Sanitize("before\r#example"));
        Equal("after\n", service.Sanitize("#example\rafter\n"));
        Equal(string.Empty, service.Sanitize("#example\n#example\r#example\r\n#example"));
    }

    internal static void CompilationFamiliesKeepSeparateAuthorityAndSharedCorrelation()
    {
        var tracker = new DirectiveCompilationTracker();
        long id = tracker.Begin(DirectiveCompilationBoundary.ScriptText);
        True(tracker.IsAuthoritative(DirectiveCompilationBoundary.ScriptText));
        Equal(id, tracker.Begin(DirectiveCompilationBoundary.Continuous));
        True(tracker.IsAuthoritative(DirectiveCompilationBoundary.Continuous));
        Equal(id, tracker.Begin(DirectiveCompilationBoundary.Continuous));
        True(!tracker.IsAuthoritative(DirectiveCompilationBoundary.Continuous));
        tracker.End(DirectiveCompilationBoundary.Continuous);
        True(tracker.IsAuthoritative(DirectiveCompilationBoundary.Continuous));
        tracker.End(DirectiveCompilationBoundary.Continuous);
        Equal(id, tracker.Begin(DirectiveCompilationBoundary.Standalone));
        True(!tracker.IsAuthoritative(DirectiveCompilationBoundary.Standalone));
        tracker.End(DirectiveCompilationBoundary.Standalone);
        True(tracker.IsAuthoritative(DirectiveCompilationBoundary.ScriptText));
        tracker.End(DirectiveCompilationBoundary.ScriptText);
        Equal(0L, tracker.CompilationId);
        True(!tracker.IsAuthoritative(DirectiveCompilationBoundary.ScriptText));
        long next = tracker.Begin(DirectiveCompilationBoundary.Standalone);
        True(next > id);
        tracker.End(DirectiveCompilationBoundary.Standalone);
    }

    internal static void CompilationTrackerCanUnwindAnExceptionAndStartFresh()
    {
        var tracker = new DirectiveCompilationTracker();
        long first = tracker.Begin(DirectiveCompilationBoundary.ScriptText);
        try
        {
            tracker.Begin(DirectiveCompilationBoundary.Continuous);
            try { throw new InvalidOperationException("compiler failure"); }
            finally { tracker.End(DirectiveCompilationBoundary.Continuous); }
        }
        catch (InvalidOperationException) { }
        finally { tracker.End(DirectiveCompilationBoundary.ScriptText); }
        Equal(0L, tracker.CompilationId);
        True(tracker.Begin(DirectiveCompilationBoundary.Continuous) > first);
        True(tracker.IsAuthoritative(DirectiveCompilationBoundary.Continuous));
        tracker.End(DirectiveCompilationBoundary.Continuous);
        var secondThread = new DirectiveCompilationTracker();
        long otherId = secondThread.Begin(DirectiveCompilationBoundary.ScriptText);
        True(otherId > first);
        secondThread.End(DirectiveCompilationBoundary.ScriptText);
    }

    private static EmbeddedDirectiveService Ready() => new(() => true, () => true, null);
    private static IDisposable Register(EmbeddedDirectiveService service, string owner, IReadOnlyList<string> routes, Action<DirectiveCompilation> callback)
    {
        var result = service.Register(owner, routes, callback);
        True(result.Success, result.Error?.Message ?? "Registration failed.");
        return result.Value;
    }
    private static void True(bool value, string message = "Assertion failed.")
    {
        if (!value) throw new InvalidOperationException(message);
    }
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}; got {actual}.");
    }
}
