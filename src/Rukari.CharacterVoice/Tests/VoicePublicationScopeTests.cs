using Rukari.CharacterVoice.Core;
using Rukari.Lib;

internal static class VoicePublicationScopeTests
{
    internal static void NotReadyStoppedAndWrongThreadNeverCaptureNativeIdentity()
    {
        var f = new Fixture();
        using var service = f.Service();
        f.Ready = false;
        Check(service.Begin("work").Error?.Code == ModErrorCode.NotReady);
        f.Ready = true;
        f.MainThread = false;
        Check(service.Begin("work").Error?.Code == ModErrorCode.WrongThread);
        f.MainThread = true;
        service.Dispose();
        Check(service.Begin("work").Error?.Code == ModErrorCode.NotReady);
        Check(f.Captures == 0 && f.Entries == 0);
    }

    internal static void InvalidNamesNeverEnterPublicationScope()
    {
        var f = new Fixture();
        using var service = f.Service();
        foreach (string name in new[] { "", " ", ".", "..", "../work", "a\0b" })
            Check(service.Begin(name).Error?.Code == ModErrorCode.InvalidArgument);
        Check(f.Captures == 0 && f.Entries == 0);
    }

    internal static void UnavailableOrMismatchedProjectNeverProducesASuccessfulEmptyScope()
    {
        var f = new Fixture();
        using var service = f.Service();
        f.Failure = new(ModErrorCode.NotReady, "session unavailable");
        Check(service.Begin("work").Error == f.Failure);
        f.Failure = null;
        f.Identity = f.Identity with { ResourceRoot = "" };
        Check(service.Begin("work").Error?.Code == ModErrorCode.Conflict);
        Check(f.Entries == 0);
    }

    internal static void MatchingNameCannotAuthorizeAnotherProjectRoot()
    {
        var f = new Fixture();
        using var service = f.Service();
        f.Identity = f.Identity with { ProjectFile = Path.Combine(Path.GetTempPath(), "another-folder", "work.aap") };
        Check(service.Begin("work").Error?.Code == ModErrorCode.Conflict);
        Check(f.Entries == 0);
    }

    internal static void ValidScopeRequiresNoVoiceIndexAndItsTicketCoversTheWholeManualChain()
    {
        var f = new Fixture();
        using var service = f.Service();
        // These managed identity paths need not exist: scope admission does not inspect a voice index.
        ModResult<IDisposable> opened = service.Begin("work");
        Check(opened.Success && f.Captures == 1 && f.Entries == 1 && f.Exits == 0);
        Check(f.EnteredRoot == f.Identity.ResourceRoot);
        try
        {
            using (opened.Value)
            {
                Check(f.Exits == 0); // Save.
                Check(f.Exits == 0); // Compile.
                Check(f.Exits == 0); // Named publication.
                throw new IOException("simulated compile failure");
            }
        }
        catch (IOException) { }
        Check(f.Exits == 1);
    }

    internal static void CaptureOrEntryFaultsAreExplicitFailures()
    {
        var f = new Fixture();
        using var captureFault = new VoicePublicationScopeService(() => true, () => true,
            () => throw new InvalidOperationException(), _ => new Ticket(() => { }));
        Check(captureFault.Begin("work").Error?.Code == ModErrorCode.ProviderFailed);
        using var entryFault = new VoicePublicationScopeService(() => true, () => true,
            () => ModResult<VoicePublicationIdentity>.Ok(f.Identity), _ => throw new IOException());
        Check(entryFault.Begin("work").Error?.Code == ModErrorCode.ProviderFailed);
    }

    private static void Check(bool pass)
    {
        if (!pass) throw new InvalidOperationException("Voice publication scope check failed.");
    }

    private sealed class Fixture
    {
        internal bool Ready = true, MainThread = true;
        internal int Captures, Entries, Exits;
        internal string EnteredRoot = "";
        internal ModError? Failure;
        internal VoicePublicationIdentity Identity = new(
            Path.Combine(Path.GetTempPath(), "rukari-scope-fixture", "work"),
            Path.Combine(Path.GetTempPath(), "rukari-scope-fixture", "work.aap"));
        internal VoicePublicationScopeService Service() => new(() => Ready, () => MainThread, () =>
        {
            Captures++;
            return Failure is null ? ModResult<VoicePublicationIdentity>.Ok(Identity)
                : ModResult<VoicePublicationIdentity>.Fail(Failure);
        }, root =>
        {
            Entries++;
            EnteredRoot = root;
            return new Ticket(() => Exits++);
        });
    }

    private sealed class Ticket : IDisposable
    {
        private Action? _close;
        internal Ticket(Action close) => _close = close;
        public void Dispose() => Interlocked.Exchange(ref _close, null)?.Invoke();
    }
}
