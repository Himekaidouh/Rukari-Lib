using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Voices;
using AzureArchive.VideoTools.Interop;
using HarmonyLib;
using Rukari.Lib;
using Rukari.Lib.Commands;
using Studio.Scripts;

namespace AzureArchive.VideoTools.Runtime;

/// <summary>Consumes the shared compiler boundary and preserves command-only rows at the official script boundary.</summary>
internal static class EditorEmbeddedDirectivePatch
{
    private const int MaximumLoggedIdentities = 128;
    private static readonly object LogGate = new();
    private static readonly HashSet<string> LoggedIdentities = new(StringComparer.Ordinal);
    private static bool _acceptLegacyAlias;
    private static IDisposable? _registration;
    private static Harmony? _emptyDialogueAnchorHarmony;

    [ThreadStatic]
    private static AavtCompilationCaptureTracker? _captures;

    public static bool Install(bool acceptLegacyAlias)
    {
        if (_registration != null) return true;
        _acceptLegacyAlias = acceptLegacyAlias;
        var service = ModServices.Current?.GetService<IEmbeddedDirectiveService>();
        if (service is not { Success: true, Value: not null })
        {
            Plugin.Logger.LogError("Embedded directives unavailable: the shared Rukari compiler service is not ready.");
            return false;
        }

        string[] routes = acceptLegacyAlias ? new[] { "#aavt", "#char" } : new[] { "#aavt" };
        var registered = service.Value.Register(Plugin.Guid, routes, OnCompilation);
        if (!registered.Success || registered.Value == null)
        {
            Plugin.Logger.LogError("Embedded directive registration failed: " + registered.Error?.Message);
            return false;
        }

        _registration = registered.Value;
        InstallEmptyDialogueAnchor();
        Plugin.Logger.LogInfo("Embedded directives registered with the shared Rukari compiler service; all registered namespaces share one sanitized official result.");
        return true;
    }

    private static void InstallEmptyDialogueAnchor()
    {
        if (_emptyDialogueAnchorHarmony != null) return;
        try
        {
            var harmony = new Harmony(Plugin.Guid + ".empty-dialogue-anchor");
            MethodInfo? getter = AccessTools.PropertyGetter(typeof(Script), nameof(Script.ScriptKr));
            MethodInfo? standalone = AccessTools.Method(typeof(Script), "CompileScriptStandalone");
            MethodInfo? continuous = AccessTools.Method(typeof(Script), nameof(Script.CompileScriptContinuous));
            if (getter == null || standalone == null || continuous == null)
                throw new MissingMethodException("The official ScriptKr compiler boundaries were not found.");

            var postfix = new HarmonyMethod(
                typeof(EditorEmbeddedDirectivePatch), nameof(AnchorOfficialScriptPostfix))
            {
                priority = Priority.Last
            };
            harmony.Patch(getter, postfix: postfix);
            harmony.Patch(standalone, postfix: postfix);
            harmony.Patch(continuous, postfix: postfix);
            _emptyDialogueAnchorHarmony = harmony;
            Plugin.Logger.LogInfo("Empty AAVT dialogue anchor installed on ScriptKr compiler results; official wait=1ms.");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning(
                "Empty AAVT dialogue anchor unavailable: "
                + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static void AnchorOfficialScriptPostfix(Script __instance, ref string __result)
    {
        try
        {
            string prompt = __instance.additionalPrompt ?? string.Empty;
            if (!NeedsEmptyDialogueAnchor(__instance, prompt)
                || ContainsOfficialWait(__result))
            {
                return;
            }

            string anchored = AppendOfficialAnchor(__result ?? string.Empty);
            CompiledScriptIdentity identity = CommandIdentity.CompiledScript(anchored);
            // The engine now reports the anchored text, while the embedded capture
            // for this row is keyed by the pre-anchor official text. Without this
            // edge the command-only row's preview window can never reach its own
            // candidate.
            EmbeddedEditorPreviewLeaseCache.AliasCaptureIdentity(
                identity,
                CommandIdentity.CompiledScript(__result ?? string.Empty).Sha256,
                "empty-dialogue-anchor");
            __result = anchored;
            int lines = __result.Count(character => character == '\n') + 1;
            Plugin.Logger.LogInfo(
                "AAVT command-only dialogue anchored with official wait=1ms; "
                + $"resultSha16={identity.Sha256[..16]}; resultLen={__result.Length}; resultLines={lines}.");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning(
                "AAVT command-only dialogue anchor failed: "
                + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static bool NeedsEmptyDialogueAnchor(Script script, string prompt)
    {
        if (!string.IsNullOrWhiteSpace(script.text)) return false;
        EmbeddedAavtExtraction extracted = new EmbeddedAavtDirectiveExtractor()
            .Extract(prompt, acceptLegacyCharAlias: false);
        VoiceDirectiveParseResult voice = VoiceDirectivePolicy.Parse(prompt);
        bool ownsAavtRow = extracted.Commands.Count > 0
            || extracted.HasContinueDirective
            || extracted.QueueMode == EffectQueueMode.OneQueue
            || voice.HasDirective;
        return extracted.Errors.Count == 0
            && voice.Errors.Count == 0
            && ownsAavtRow
            && string.IsNullOrWhiteSpace(extracted.SanitizedText);
    }

    private static bool ContainsOfficialWait(string? script)
    {
        using var reader = new StringReader(script ?? string.Empty);
        string? line;
        while ((line = reader.ReadLine()) != null)
            if (line.TrimStart().StartsWith("#wait;", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string AppendOfficialAnchor(string script)
    {
        const string anchor = "#wait;1";
        if (script.Length == 0) return anchor;
        string newline = script.Contains("\r\n", StringComparison.Ordinal)
            ? "\r\n" : script.Contains('\n') ? "\n" : "\r\n";
        return script.EndsWith("\r", StringComparison.Ordinal)
            || script.EndsWith("\n", StringComparison.Ordinal)
            ? script + anchor
            : script + newline + anchor;
    }

    private static void OnCompilation(DirectiveCompilation compilation)
    {
        string source = BoundaryName(compilation.Boundary);
        try
        {
            EmbeddedDirectiveCompilationCapture capture =
                (_captures ??= new AavtCompilationCaptureTracker()).Capture(
                    compilation, _acceptLegacyAlias);
            EmbeddedAavtExtraction extracted = capture.Extraction;
            if (!extracted.HasEmbeddedDirectives)
            {
                ClearStaleEntryIfDeleted(compilation, capture, source);
                return;
            }

            CompiledScriptIdentity identity = capture.Identity;
            EmbeddedEditorDirectiveCache.Replace(identity, extracted, source);
            // The host retains the original source-aware depth rule: a continuous compiler
            // nested inside ScriptKr is authoritative when it has no outer continuous frame.
            if (compilation.IsAuthoritative)
            {
                EmbeddedEditorPreviewLeaseCache.Capture(identity, extracted, source);
                LiveProjectGraphService.Invalidate("compile");
            }

            ContinueDialogueRuntime.SetContinueIdentity(
                identity.Sha256,
                extracted.HasContinueDirective,
                $"editor-compile:{source}");

            PlayerCommandObservationLog.Append(
                $"{DateTimeOffset.Now:O} "
                + EmbeddedDirectiveDiagnostics.BoundaryCaptureLine(
                    source, compilation.SourceText, extracted, identity));
            if (extracted.Errors.Count != 0)
            {
                LogOnce(
                    identity.Sha256 + ":" + source + ":invalid",
                    "Embedded editor directives were removed from official parsing but rejected: "
                    + string.Join(" | ", extracted.Errors)
                    + $"; source={source}.");
                return;
            }

            LogOnce(
                identity.Sha256 + ":" + source + ":clean",
                $"Embedded editor directives sanitized: commands={extracted.Commands.Count}; "
                + $"source={source}; cachedForValidatedPreview=true; "
                + "officialParserReceivesAavtLines=false; provisionalSceneBinding=false.");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError(
                $"Embedded editor directive capture failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void ClearStaleEntryIfDeleted(
        DirectiveCompilation compilation,
        EmbeddedDirectiveCompilationCapture capture,
        string source)
    {
        if (!compilation.IsAuthoritative || capture.AavtSeenEarlierInCompilation)
            return;

        try
        {
            // A clean outer return of an inner AAVT capture was rejected above. A foreign-only
            // compile is NOT such a return and remains authoritative for removing old effects.
            CompiledScriptIdentity identity = capture.Identity;
            EmbeddedEditorPreviewLeaseCache.CaptureTombstone(identity, source);
            LiveProjectGraphService.Invalidate("compile-tombstone");
            // The editor selection and the managed Unity log carry the FINAL
            // official text of this wrapper, while the capture happened at the
            // nested continuous boundary. Record the edge so the preview lease can
            // reach that capture by the identity the engine actually reports
            // (2026-10-13 field log: every AAVT row was rejected as an ambiguous
            // window intersection although the window carried the selected
            // script's own identity).
            EmbeddedEditorPreviewLeaseCache.AliasCaptureIdentity(
                identity, capture.PreviousAavtIdentitySha256, source);
            if (string.IsNullOrEmpty(compilation.OfficialText)) return;

            bool removed = EmbeddedEditorDirectiveCache.TryRemoveStale(
                identity,
                TimeSpan.Zero,
                $"stale-entry-shadowed-by-top-level-clean-compile; source={source}",
                out bool hadContinueDirective);
            if (removed && hadContinueDirective)
                ContinueDialogueRuntime.ForgetContinueIdentity(
                    identity.Sha256, $"editor-clean-compile:{source}");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError(
                $"Embedded preview stale-entry check failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string BoundaryName(DirectiveCompilationBoundary boundary) => boundary switch
    {
        DirectiveCompilationBoundary.Continuous => "CompileScriptContinuous",
        DirectiveCompilationBoundary.Standalone => "CompileScriptStandalone",
        _ => "ScriptKr"
    };

    private static void LogOnce(string identity, string message)
    {
        lock (LogGate)
        {
            if (LoggedIdentities.Count >= MaximumLoggedIdentities || !LoggedIdentities.Add(identity))
                return;
        }
        Plugin.Logger.LogWarning(message);
    }
}
