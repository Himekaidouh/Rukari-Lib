using Spine;

namespace Rukari.SpineSupport.Runtime;

/// <summary>
/// Imported lobbies still carry the source game's audio events — <c>Sound/CH0345_MemorialLobby_1_1</c> pointing at
/// a <c>.wav</c> the official side would look up under its own <c>Audio/SE/</c> (and <c>.ogg</c> under
/// <c>Audio/VOC_JP/JP_</c>). Nobody ships those files with a converted lobby, and the official resolver reports
/// every miss, so this blanks each such event's audio path once: the event still fires, it simply has nothing to
/// play. Voice is added by hand afterwards, as usual.
///
/// <para>
/// Only the loaded asset in memory is touched — no file, and no official method is hooked. Events that carry no
/// audio path and are not named <c>Sound/…</c> are left exactly as they are, so a lobby whose audio really is
/// installed keeps working.
/// </para>
/// </summary>
internal static class LobbyAudioEvents
{
    /// <summary>Prefix the source lobby projects use for their sound events.</summary>
    private const string SoundPrefix = "Sound/";

    /// <summary>Mutes every audio-carrying event; returns how many were muted by this call.</summary>
    internal static int Mute(SkeletonData? data)
    {
        if (data is null || data.Pointer == IntPtr.Zero) return 0;
        int muted = 0;
        try
        {
            var events = data.Events;
            if (events is null) return 0;
            int count = events.Count;
            var items = events.Items;
            for (int index = 0; index < count; index++)
            {
                EventData? entry = items[index];
                if (entry is null || entry.WasCollected || entry.Pointer == IntPtr.Zero) continue;
                string name = entry.Name ?? "";
                string audio = entry.AudioPath ?? "";
                bool carriesAudio = audio.Length != 0;
                if (!carriesAudio && !name.StartsWith(SoundPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (!carriesAudio) continue;

                entry.AudioPath = "";
                muted++;
                Plugin.Logger.LogInfo(
                    $"[lobby] spine audio event muted: '{name}' no longer points at '{audio}' "
                    + "(the audio is not shipped with a converted lobby; voice is added by hand).");
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"[lobby] spine audio events could not be muted: {ex.GetType().Name}: {ex.Message}");
        }

        return muted;
    }
}
