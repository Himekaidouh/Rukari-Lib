using System.Reflection;
using Rukari.CharacterVoice.Core;
using Rukari.CharacterVoice.Interop;
using BepInEx.Configuration;
using FlatData;
using HarmonyLib;
using Rukari.Lib;
using Rukari.Lib.Commands;

namespace Rukari.CharacterVoice.Runtime;

/// <summary>Headless authoring and resource bridge into the existing native voice pipeline.</summary>
internal static class VoiceSupportPatch
{
    private static readonly HashSet<string> Reported = new(StringComparer.Ordinal);
    private static int _mainThreadId;
    [ThreadStatic] private static bool _resolving;
    private static bool _installed;
    private static IDisposable? _directiveRegistration;

    public static bool Install(ConfigFile config)
    {
        if (_installed) return true;
        if (!config.Bind("Voice", "Enabled", true,
                "Enable #aavt;voice;<imported sound key> through the native voice channel. Restart required.").Value)
            return false;

        _mainThreadId = Environment.CurrentManagedThreadId;
        var harmony = new Harmony(Plugin.Guid + ".voice-bindings");
        try
        {
            var directives = ModServices.Current?.GetService<IEmbeddedDirectiveService>();
            if (directives is not { Success: true, Value: not null })
                throw new InvalidOperationException("The shared Rukari compiler service is not ready.");
            var registration = directives.Value.Register(
                Plugin.Guid, new[] { "#aavt;voice" }, static _ => { });
            if (!registration.Success || registration.Value == null)
                throw new InvalidOperationException("Voice directive registration failed: " + registration.Error?.Message);
            _directiveRegistration = registration.Value;

            // Do not patch VoiceJp: its native implementation is shared with unrelated getters.
            // Compile's real dialogue path calls preSubScript before VoiceExists/resource collection.
            MethodInfo export = AccessTools.PropertyGetter(typeof(Script), "preSubScript")
                ?? throw new MissingMethodException("Script.get_preSubScript");
            MethodInfo preview = Require(typeof(Utils.ScenarioUtil), "ParseScript",
                typeof(IScenarioScriptExcel), typeof(Test), typeof(Enums.Language));
            MethodInfo exists = Require(typeof(ScenarioResourceManager), "VoiceExists", typeof(string));
            MethodInfo path = Require(typeof(ScenarioResourceManager), "TryGetVoiceOverridePath",
                typeof(string), typeof(string).MakeByRefType());
            harmony.Patch(export, prefix: new HarmonyMethod(typeof(VoiceSupportPatch), nameof(ExportPrefix)));
            harmony.Patch(preview, prefix: new HarmonyMethod(typeof(VoiceSupportPatch), nameof(PreviewPrefix)));
            harmony.Patch(exists, postfix: new HarmonyMethod(typeof(VoiceSupportPatch), nameof(VoiceExistsPostfix)));
            harmony.Patch(path, postfix: new HarmonyMethod(typeof(VoiceSupportPatch), nameof(VoicePathPostfix)));
            // The shared host removes voice lines for independent voice installations too.
            // Resource binding stays on the existing voice hooks; compilation callbacks must
            // never play audio or mutate authoring data simply because a getter was read.
            if (!VoicePlaybackRuntime.Install(config))
                throw new InvalidOperationException("Voice playback guard was not installed.");
            _installed = true;
            Plugin.Logger.LogInfo("Voice candidate installed: extra-instruction binding; native voice resource bridge; live acceptance pending.");
            return true;
        }
        catch (Exception ex)
        {
            try { harmony.UnpatchSelf(); }
            finally
            {
                _directiveRegistration?.Dispose();
                _directiveRegistration = null;
            }
            Report("install", $"Voice candidate installation failed: {ex.GetType().Name}: {ex.Message}", true);
            return false;
        }
    }

    internal static void Stop()
    {
        VoicePlaybackRuntime.Stop();
        _directiveRegistration?.Dispose();
        _directiveRegistration = null;
        if (!_installed) return;
        _installed = false;
        new Harmony(Plugin.Guid + ".voice-bindings").UnpatchSelf();
    }

    public static void ExportPrefix(Script __instance) => SyncBinding(__instance, "export-before-resource-collection");

    public static void PreviewPrefix(IScenarioScriptExcel scenario)
    {
        if (!OnMainThread() || ReferenceEquals(scenario, null)) return;
        try
        {
            // Only editor Script reference objects are writable here; AAS table wrappers are untouched.
            Script? script = scenario.TryCast<Script>();
            if (!ReferenceEquals(script, null)) SyncBinding(script!, "editor-preview");
        }
        catch (Exception ex)
        {
            Report("preview-error", $"Voice editor binding failed: {ex.GetType().Name}: {ex.Message}", true);
        }
    }

    private static void SyncBinding(Script script, string boundary)
    {
        if (!OnMainThread() || ReferenceEquals(script, null)) return;
        try
        {
            string prompt = script.additionalPrompt ?? string.Empty;
            var directives = ModServices.Current?.GetService<IEmbeddedDirectiveService>();
            if (directives is not { Success: true, Value: not null }) return;
            var snapshot = directives.Value.CaptureSanitizer();
            prompt = snapshot.SanitizeExceptOwners(prompt, new[] { Plugin.Guid });
            string existing = script.voice ?? string.Empty;
            VoiceDirectiveParseResult parsed = VoiceDirectivePolicy.Parse(prompt);
            bool owned = existing.StartsWith(VoiceDirectivePolicy.NativeIdentifierPrefix, StringComparison.Ordinal);
            if (!parsed.HasDirective && !owned) return;

            // New native Script instances contain an unmapped UUID in voice, not an audio binding.
            bool hasNativeResource = false;
            if (parsed.HasDirective && !owned && existing.Length != 0)
            {
                ScenarioResourceManager resources = ScenarioResourceManager.Instance;
                if (ReferenceEquals(resources, null))
                {
                    Report("no-resource-manager", "Voice binding postponed: native resource manager unavailable.", true);
                    return;
                }
                hasNativeResource = resources.VoiceExists(existing);
            }

            VoiceBindingResult binding = VoiceDirectivePolicy.ResolveBinding(prompt, existing, hasNativeResource);
            string target = binding.VoiceIdentifier ?? string.Empty;
            if (!string.Equals(existing, target, StringComparison.Ordinal)) script.voice = target;
            if (!binding.Success)
            {
                Report("binding-error-" + string.Join("|", binding.Errors),
                    "Voice binding rejected: " + string.Join(" | ", binding.Errors), true);
                return;
            }
            Report("binding-" + boundary, $"Voice binding synchronized: boundary={boundary}; nativeField=true; directArchiveWrite=false.");
        }
        catch (Exception ex)
        {
            Report("sync-error-" + boundary, $"Voice binding failed at {boundary}: {ex.GetType().Name}: {ex.Message}", true);
        }
    }

    public static void VoiceExistsPostfix(ScenarioResourceManager __instance, string voiceIdentifier, ref bool __result)
    {
        // Honor any actual native voice override before trying the retained SoundOverrides bridge.
        if (__result || !IsOwned(voiceIdentifier)) return;
        __result = TryResolve(__instance, voiceIdentifier, out _);
    }

    public static void VoicePathPostfix(ScenarioResourceManager __instance, string voiceIdentifier, ref string over, ref bool __result)
    {
        if (__result || !IsOwned(voiceIdentifier)) return;
        __result = TryResolve(__instance, voiceIdentifier, out string path);
        over = path;
    }

    private static bool TryResolve(ScenarioResourceManager resources, string identifier, out string path)
    {
        path = string.Empty;
        if (!OnMainThread() || ReferenceEquals(resources, null) || _resolving) return false;
        try
        {
            _resolving = true;
            if (!VoiceDirectivePolicy.TryDecodeNativeIdentifier(identifier, out string key)) return false;
            if (ProjectVoiceImportPolicy.IsImportKey(key))
            {
                // This dedicated branch never calls native sound resolution, including when missing.
                // Native success has already won in both postfixes above.
                //
                // 2026-09-19: it walks every project root this session knows instead of only the live
                // one. During formal playback the live override root is the played package, which has
                // no rukari-voices/ at all — which is why an imported voice played in editor preview
                // and was silently skipped in playback. A miss is now reported with the exact roots
                // that were tried: staying silent here is what made that failure expensive.
                // The companion folder is resolved from the current playback root, so a cold start
                // or a moved work does not need any remembered editor session.
                var liveProject = ProjectVoiceImportStore.CaptureCurrentProjectOnMainThread();
                if (liveProject.Success && liveProject.Value is not null
                    && ProjectVoiceImportPolicy.TryResolvePublishedVoice(liveProject.Value.RootPath, key, out path))
                    return true;
                IReadOnlyList<string> candidates = ProjectVoiceImportStore.ResolutionCandidates();
                if (ProjectVoiceImportPolicy.TryResolveAcrossRoots(candidates, key, out path, out string usedRoot))
                {
                    ProjectVoiceImportStore.PromoteRoot(usedRoot);
                    if (candidates.Count > 0
                        && !string.Equals(candidates[0], usedRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        Report("resolved-remembered-root-" + key,
                            "Imported voice resolved from a remembered project root instead of the live one: "
                            + $"key={key}; used={usedRoot}; live={candidates[0]}");
                    }
                    return true;
                }
                Report("missing-import-" + key,
                    "Imported voice not found under any known project root, so this line plays silent: "
                    + $"key={key}; roots=[{string.Join(" | ", candidates)}]; "
                    + $"liveCapture={DescribeLiveCapture()}",
                    true);
                return false;
            }
            if (!resources.TryGetSoundOverridePath(key, out string resolved) || !File.Exists(resolved))
            {
                Report("missing-" + identifier, $"Voice resource not found in imported sounds: {key}", true);
                return false;
            }
            // Native GetAudioFormat is case-sensitive. Reject unsupported names before cold loading.
            string extension = Path.GetExtension(resolved);
            if (extension is not (".wav" or ".ogg" or ".mp3"))
            {
                Report("format-" + identifier, "Voice resource needs a lowercase .wav, .ogg or .mp3 file extension.", true);
                return false;
            }
            path = resolved;
            Report("resolved", "Voice resource resolved through the existing sound import; playback uses the independent native voice channel.");
            return true;
        }
        catch (Exception ex)
        {
            Report("resolve-error", $"Voice resource resolution failed: {ex.GetType().Name}: {ex.Message}", true);
            return false;
        }
        finally { _resolving = false; }
    }

    /// <summary>Why the live project could not be captured, for the miss report.</summary>
    private static string DescribeLiveCapture()
    {
        Rukari.Lib.ModResult<VoiceImportProject> live =
            ProjectVoiceImportStore.CaptureCurrentProjectOnMainThread();
        return live.Success && live.Value is not null
            ? live.Value.RootPath
            : live.Error?.Code + ":" + live.Error?.Message;
    }

    private static bool IsOwned(string? identifier) =>
        identifier?.StartsWith(VoiceDirectivePolicy.NativeIdentifierPrefix, StringComparison.Ordinal) == true;

    private static bool OnMainThread() => Environment.CurrentManagedThreadId == _mainThreadId;

    private static MethodInfo Require(Type type, string method, params Type[] arguments) =>
        AccessTools.Method(type, method, arguments) ?? throw new MissingMethodException(type.FullName, method);

    private static void Report(string key, string message, bool warning = false)
    {
        lock (Reported)
        {
            if (Reported.Count >= 128 || !Reported.Add(key)) return;
        }
        if (warning) Plugin.Logger.LogWarning(message);
        else Plugin.Logger.LogInfo(message);
    }
}
