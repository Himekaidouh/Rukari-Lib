using Rukari.Lib.Commands;

namespace Rukari.Lib.Runtime.Commands;

// The host owns the native boundary. Feature mods own only their route leases.
internal static class EmbeddedDirectiveHost
{
    private static EmbeddedDirectiveService? _service;
    private static IDisposable? _registration;

    internal static void Initialize(IModRuntime runtime, Action<string> log)
    {
        if (_service is not null) return;
        if (!runtime.IsMainThread)
            throw new InvalidOperationException("Embedded directive initialization requires the main thread.");

        var service = new EmbeddedDirectiveService(
            () => runtime.State == RuntimeState.Ready, () => runtime.IsMainThread, log);
        try
        {
        if (!EmbeddedDirectivePatch.Install(service, log))
        {
            service.Dispose();
            log("Shared embedded directives unavailable; no directive capability was published.");
            return;
        }

        var registration = runtime.RegisterService<IEmbeddedDirectiveService>(Plugin.Guid, service,
            new CapabilityInfo(EmbeddedDirectiveCapabilities.Compilation, Plugin.Guid, Plugin.Version,
                CapabilityLevel.Experimental,
                "Extracts registered mod directives at the shared compilation boundary; observers do not represent playback events."));
        if (!registration.Success)
        {
            EmbeddedDirectivePatch.Uninstall();
            service.Dispose();
            log(registration.Error!.Message);
            return;
        }
        _service = service;
        _registration = registration.Value;
        log("Shared embedded directive service ready; compiler hooks have one owner in Rukari lib.");
        }
        catch (Exception exception)
        {
            _registration?.Dispose();
            _registration = null;
            EmbeddedDirectivePatch.Uninstall();
            service.Dispose();
            _service = null;
            log("Shared embedded directive initialization failed: " + exception.Message);
        }
    }

    internal static void Shutdown()
    {
        _registration?.Dispose();
        _registration = null;
        try { EmbeddedDirectivePatch.Uninstall(); }
        finally { _service?.Dispose(); _service = null; }
    }
}
