using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using Rukari.SpineSupport.Spines;

namespace Rukari.SpineSupport.Runtime;

/// <summary>
/// The boundary every sound and voice lookup goes through:
/// <c>ScenarioResourceManager.GetVoiceAsync(string, Action&lt;AudioClip&gt;, bool suppressErrors)</c> and its sound
/// twin. A project can ask for audio that does not exist — a converted lobby still names the source game's
/// <c>.wav</c> files, and a line can keep a voice GUID whose <c>voices/&lt;guid&gt;.ogg</c> is gone — and the official
/// resolver answers with an Addressables exception plus
/// <c>Voice … not found in Audio/VOC_JP/JP_…</c> / <c>… not found in Audio/SE/</c>, which surfaces in the editor.
///
/// <para>
/// Its own <c>suppressErrors</c> flag is not enough: the log shows the flag already arriving as <c>true</c> while the
/// error was still raised (the Addressables miss is reported by a handler that never looks at it). So when the
/// identifier is one only a project produces <em>and</em> it cannot be resolved by either the project or the game,
/// the lookup is not performed at all: there is nothing to load, and the caller's callback simply never sees a clip.
/// Nothing resolvable is ever skipped, so every shipped sound effect and every voice that really exists keeps
/// working exactly as before; each drop is recorded once.
/// </para>
/// </summary>
internal static class LobbyBundledAudioPatch
{
    private const int MaximumReports = 60;
    private static readonly HashSet<string> Reported = new(StringComparer.Ordinal);
    private static Il2CppSystem.Collections.Generic.List<string>? _soundNames;
    private static long _soundNamesAt;
    private static bool _enabled;
    private static bool _logLookups;
    private static bool _installed;

    internal static bool Install(ConfigFile config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (_installed) return true;
        _enabled = config.Bind("LobbyAudio", "SuppressMissingBundledAudio", true,
            "A project can ask for audio that does not exist: a converted lobby still names the source game's .wav "
            + "files, and a line can keep a voice GUID whose voices/<guid>.ogg is gone. The official resolver reports "
            + "each miss as an Addressables exception plus a missing-resource error. When such an identifier cannot be "
            + "resolved by the project or the game, this skips the lookup instead. Only unresolvable project audio is "
            + "touched. Restart required.").Value;
        _logLookups = config.Bind("LobbyAudio", "LogResourceLookups", false,
            "Log every sound and voice the official resolver is asked for, with its suppress flag and whether it "
            + "resolves. Diagnostic only; leave off unless a missing-audio report needs to be traced. Restart required.").Value;

        var harmony = new Harmony(Plugin.Guid + ".bundled-audio");
        try
        {
            harmony.Patch(RequireMethod(typeof(ScenarioResourceManager), "GetSoundAsync"),
                prefix: new HarmonyMethod(typeof(LobbyBundledAudioPatch), nameof(SoundPrefix)));
            harmony.Patch(RequireMethod(typeof(ScenarioResourceManager), "GetVoiceAsync"),
                prefix: new HarmonyMethod(typeof(LobbyBundledAudioPatch), nameof(VoicePrefix)));
            _installed = true;
            Plugin.Logger.LogInfo("Project-audio guard installed: an unresolvable project sound or voice is not looked "
                + $"up at all, so the editor's missing-resource error never fires (enabled={_enabled}; logLookups={_logLookups}).");
            return true;
        }
        catch (Exception ex)
        {
            harmony.UnpatchSelf();
            Plugin.Logger.LogWarning($"Project-audio guard not installed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Runs before <c>GetSoundAsync(string soundIdentifier, Action&lt;AudioClip&gt; callback, bool suppressErrors)</c>.</summary>
    public static bool SoundPrefix(string soundIdentifier, bool suppressErrors) =>
        Guard("sound", soundIdentifier, suppressErrors, SoundIsResolvable);

    /// <summary>Runs before <c>GetVoiceAsync(string voiceIdentifier, Action&lt;AudioClip&gt; callback, bool suppressErrors)</c>.</summary>
    public static bool VoicePrefix(string voiceIdentifier, bool suppressErrors) =>
        Guard("voice", voiceIdentifier, suppressErrors, VoiceIsResolvable);

    /// <summary>False skips the official lookup; true lets it run unchanged.</summary>
    private static bool Guard(string kind, string? identifier, bool suppressErrors, Func<string, bool> resolvable)
    {
        if (!_enabled || string.IsNullOrWhiteSpace(identifier)) return true;
        try
        {
            bool projectOnly = ProjectAudioPolicy.IsProjectOnlyIdentifier(identifier);
            // The availability check costs native calls, so it only runs when its answer changes something.
            bool resolved = projectOnly && resolvable(identifier);
            if (_logLookups)
            {
                Report($"lookup:{kind}:{identifier}",
                    $"[lobby] {kind} lookup '{identifier}': suppressErrors={suppressErrors}; projectAudio={projectOnly}; resolvable={resolved}.");
            }

            if (!projectOnly || resolved) return true;
            Report($"skipped:{kind}:{identifier}",
                $"[lobby] {kind} '{identifier}' is not installed — neither the project nor the game has it; the lookup "
                + "is skipped so the editor's missing-resource error never fires. Voice and effects are added by hand afterwards.");
            return false;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"[lobby] project-audio guard failed for '{identifier}': {ex.GetType().Name}: {ex.Message}");
            return true;
        }
    }

    private static bool SoundIsResolvable(string identifier)
    {
        ScenarioResourceManager? manager = ScenarioResourceManager.Instance;
        if (ReferenceEquals(manager, null)) return false;
        if (manager.TryGetSoundOverridePath(identifier, out string path) && !string.IsNullOrEmpty(path)) return true;
        Il2CppSystem.Collections.Generic.List<string>? names = SoundNames();
        return !ReferenceEquals(names, null) && names!.Contains(identifier);
    }

    private static bool VoiceIsResolvable(string identifier)
    {
        ScenarioResourceManager? manager = ScenarioResourceManager.Instance;
        if (ReferenceEquals(manager, null)) return false;
        if (manager.TryGetVoiceOverridePath(identifier, out string path) && !string.IsNullOrEmpty(path)) return true;
        return manager.VoiceExists(identifier);
    }

    /// <summary>The game's own sound table is stable for a session; a failed probe is retried on the next miss.</summary>
    private static Il2CppSystem.Collections.Generic.List<string>? SoundNames()
    {
        long now = Environment.TickCount64;
        if (!ReferenceEquals(_soundNames, null) && now - _soundNamesAt < 10_000) return _soundNames;
        try
        {
            _soundNames = ScenarioResourceManager.Instance?.GetSoundNames();
            _soundNamesAt = now;
        }
        catch (Exception)
        {
            _soundNames = null;
        }

        return _soundNames;
    }

    private static MethodInfo RequireMethod(Type type, string name) =>
        AccessTools.Method(type, name) ?? throw new MissingMethodException(type.FullName, name);

    private static void Report(string key, string message)
    {
        lock (Reported)
        {
            if (Reported.Count >= MaximumReports || !Reported.Add(key)) return;
        }

        Plugin.Logger.LogInfo(message);
    }
}
