namespace Rukari.CharacterVoice.Core;

// A caller-side managed boundary also catches failures while the tick method is being
// resolved/JIT-compiled, before that method's own exception handlers can execute.
internal sealed class VoicePlaybackTickGuard
{
    internal bool Suspended { get; private set; }

    internal void Run(Action tick, Action<Exception> onFailure)
    {
        if (Suspended) return;
        try { tick(); }
        catch (Exception error)
        {
            // Set this before reporting/cleanup so either failure cannot reopen the
            // per-frame call or prevent the caller's other work from continuing.
            Suspended = true;
            try { onFailure(error); }
            catch (Exception) { }
        }
    }
}
