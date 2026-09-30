using Rukari.Lib;
using Rukari.Lib.Voices;

namespace SharedVoiceClient;

// This client needs only the public API. It knows no AAVT implementation or game objects.
// A real BepInEx consumer declares a hard dependency on rukari.lib.runtime.
public static class VoiceClient
{
    public static Task<ModResult<VoiceSelection>> ReadSelectionAsync(CancellationToken cancellation = default) =>
        Invoke(service => service.ReadSelection(), cancellation);

    public static Task<ModResult<IReadOnlyList<VoiceResource>>> ReadCatalogAsync(CancellationToken cancellation = default) =>
        Invoke(service => service.ReadCatalog(), cancellation);

    // Pass the reviewed selection; never silently recapture a different line on conflict.
    public static Task<ModResult<VoiceEditResult>> ApplyAsync(
        VoiceSelection reviewedSelection, string? resourceId, CancellationToken cancellation = default) =>
        Invoke(service => service.Apply(new(reviewedSelection.Token, reviewedSelection.Revision, resourceId)), cancellation);

    private static Task<ModResult<T>> Invoke<T>(
        Func<IVoiceAuthoringService, ModResult<T>> action, CancellationToken cancellation)
    {
        var runtime = ModServices.Current;
        if (runtime == null) return Task.FromResult(ModResult<T>.Fail(ModErrorCode.NotReady, "Mod Runtime is unavailable."));
        // Resolve inside the queued operation, so an unloaded provider cannot be retained in the queue.
        return runtime.InvokeAsync(() =>
        {
            var service = runtime.GetService<IVoiceAuthoringService>();
            return service.Success ? action(service.Value) : ModResult<T>.Fail(service.Error!);
        }, cancellation);
    }
}
