using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;

namespace Rukari.CharacterVoice.Runtime;

/// <summary>
/// The file copy that aborts every compile.
///
/// <para>
/// The editor registers a voice placeholder for a line whose lobby animation implies a voice: a GUID and the relative
/// path <c>voices/&lt;guid&gt;.ogg</c>, with no file behind it — the audio is meant to be imported by hand afterwards.
/// Its own compiler then packs every referenced voice by copying it out of the project
/// (<c>AuthoringCompiler.Compile → AuthoringResourceCatalog.StageReferencedResources</c>, AuthoringResourceExport.cs:54),
/// and a copy whose source is missing throws <c>Access to the path … is denied</c>, which aborts the whole compile:
/// no build, no playable file. One such entry is enough to break the project, and every retry repeats it.
/// </para>
///
/// <para>
/// The file genuinely does not exist, so there is nothing to copy and nothing to report: this skips exactly that
/// copy — a source inside the open project's <c>voices</c> folder that is not there — and leaves every other copy
/// untouched. A voice whose audio really exists is copied normally, so a real voice still reaches the build.
/// </para>
/// </summary>
internal static class VoiceStageCopyGuard
{
    private const int MaximumReports = 40;
    private static readonly HashSet<string> Reported = new(StringComparer.Ordinal);
    private static bool _enabled;
    private static bool _installed;

    internal static void Install(ConfigFile config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (_installed) return;
        _installed = true;
        _enabled = config.Bind("Compile", "SkipMissingVoiceStageCopy", true,
            "The editor registers voice placeholders (voices/<guid>.ogg) for lines whose lobby animation implies a "
            + "voice, but never creates the file. Its compiler then copies every referenced voice out of the project, "
            + "and a missing source aborts the whole compile with 'Access to the path … is denied' — so no playable "
            + "file is produced. Skip exactly that copy: a missing file inside the project's voices folder is not "
            + "copied, and every other copy is left alone. Restart required.").Value;
        if (!_enabled) return;

        var harmony = new Harmony(Plugin.Guid + ".voice-stage-copy");
        int patched = 0;
        foreach (MethodInfo method in AccessTools.GetDeclaredMethods(typeof(Il2CppSystem.IO.File)))
        {
            if (method.Name != "Copy") continue;
            try
            {
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(VoiceStageCopyGuard), nameof(Prefix)));
                patched++;
            }
            catch (Exception)
            {
                // A refused overload must not stop the rest.
            }
        }

        Plugin.Logger.LogInfo($"Voice stage-copy guard installed: {patched} Copy overload(s); a missing project voice "
            + "is skipped instead of aborting the compile.");
    }

    /// <summary>False skips the copy; true runs it unchanged.</summary>
    public static bool Prefix(string sourceFileName)
    {
        if (!_enabled || string.IsNullOrEmpty(sourceFileName)) return true;
        // Cheap rejection first: File.Copy is used all over the game.
        if (sourceFileName.IndexOf("voices", StringComparison.OrdinalIgnoreCase) < 0) return true;
        try
        {
            if (System.IO.File.Exists(sourceFileName)) return true;
            // No session, no main thread, no cache: the path shape is the whole test.
            if (!IsProjectVoicePath(sourceFileName)) return true;
            Report(sourceFileName,
                $"[voice] compile staging skipped '{sourceFileName}': the placeholder has no audio file, and copying a "
                + "missing file is what aborted the build.");
            return false;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"[voice] stage-copy guard failed for '{sourceFileName}': {ex.GetType().Name}: {ex.Message}");
            return true;
        }
    }

    /// <summary>
    /// Shape only, never the session: the save preparation runs this copy on a background task, where the editor's
    /// session cannot be read at all — that is exactly how a missing voice kept stalling the save while the compile
    /// hook looked fine. <c>…\projects\&lt;project&gt;\voices\&lt;file&gt;</c> is a project voice, wherever it is called from.
    /// </summary>
    private static bool IsProjectVoicePath(string path)
    {
        string normalized = path.Replace('/', System.IO.Path.DirectorySeparatorChar);
        int projects = normalized.IndexOf(
            System.IO.Path.DirectorySeparatorChar + "projects" + System.IO.Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
        if (projects < 0) return false;
        int voices = normalized.IndexOf(
            System.IO.Path.DirectorySeparatorChar + "voices" + System.IO.Path.DirectorySeparatorChar,
            projects, StringComparison.OrdinalIgnoreCase);
        return voices > projects;
    }

    private static void Report(string key, string message)
    {
        lock (Reported)
        {
            if (Reported.Count >= MaximumReports || !Reported.Add(key)) return;
        }

        Plugin.Logger.LogInfo(message);
    }
}
