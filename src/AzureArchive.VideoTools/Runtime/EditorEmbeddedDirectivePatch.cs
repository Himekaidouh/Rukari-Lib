using System;
using System.Collections.Generic;
using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Interop;
using Rukari.Lib;
using Rukari.Lib.Commands;

namespace AzureArchive.VideoTools.Runtime;

/// <summary>Consumes the shared compiler boundary; this feature installs no compiler patch.</summary>
internal static class EditorEmbeddedDirectivePatch
{
    private const int MaximumLoggedIdentities = 128;
    private static readonly object LogGate = new();
    private static readonly HashSet<string> LoggedIdentities = new(StringComparer.Ordinal);
    private static bool _acceptLegacyAlias;
    private static IDisposable? _registration;

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
        Plugin.Logger.LogInfo("Embedded directives registered with the shared Rukari compiler service; all registered namespaces share one sanitized official result.");
        return true;
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
