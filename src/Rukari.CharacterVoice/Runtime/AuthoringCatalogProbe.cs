using System.Diagnostics;
using System.Text;
using AzureArchive.Automation;
using Rukari.CharacterVoice.Core;
using Rukari.CharacterVoice.Interop;
using Rukari.Lib;

namespace Rukari.CharacterVoice.Runtime;

/// <summary>
/// Read-only reconnaissance of the editor's own resource catalog (<c>AzureArchive.Automation</c>).
///
/// <para>
/// The cleanup action rewrites the project manifest on disk, and the editor answers that with
/// <c>A project resource manifest changed outside the open project; reopen it before editing resources.</c> — it
/// holds the manifest in memory and refuses to edit or save until the project is reopened. The editor's own entry
/// points exist (<c>AuthoringResourceCatalog.DeleteLocal</c>, <c>PersistWorkingManifest</c>), but the identifier they
/// expect is not documented anywhere reachable from outside the game.
/// </para>
///
/// <para>
/// This probe asks the running editor instead of guessing: it records the automation session, the catalog version,
/// which spelling of a manifest entry the catalog actually knows, and the editor's own tool vocabulary. It only
/// reads — no delete, no import, no alias, no persist — and it runs once per project so a session that keeps the
/// same project open cannot spam the log.
/// </para>
/// </summary>
internal static class AuthoringCatalogProbe
{
    private const int MaximumText = 1200;
    private const int MaximumReport = 200_000;
    private static readonly string[] ProbeKinds = { "voice", "sound", "bgm", "bg", "popup", "character" };
    private static string? _lastProject;
    private static bool _busy;
    private static long _nextCheck;
    private static int _failures;
    private static bool _abandoned;

    /// <summary>
    /// Runs once per project, on the game main thread, and never throws into the caller's frame. A project switch is
    /// deliberately re-probed: the interesting case is a project whose manifest still names audio that is gone, and
    /// the first project a session opens is not necessarily that one.
    /// </summary>
    internal static void TickOnMainThread(bool enabled, string diagnosticsRoot, bool runAuthoringCompile = true)
    {
        if (!enabled || _busy || _abandoned) return;
        // Waiting for a project to open must not cost a native call every frame. A failure is retried, but only
        // three times and with one line each: an interface this probe cannot reach must not fill the log.
        long now = Environment.TickCount64;
        if (now < _nextCheck) return;
        _nextCheck = now + 1000;
        try
        {
            if (ReferenceEquals(AuthoringEditorSession.Current, null)) return;
            ModResult<VoiceImportProject> project = ProjectVoiceImportStore.CaptureCurrentProjectOnMainThread();
            if (!project.Success) return;
            string identity = project.Value.Identity;
            if (string.Equals(identity, _lastProject, StringComparison.OrdinalIgnoreCase)) return;
            _busy = true;
            var report = new StringBuilder();
            try
            {
                Run(project.Value.RootPath, identity, report, runAuthoringCompile);
                _lastProject = identity;
            }
            finally
            {
                _busy = false;
            }

            Publish(report, diagnosticsRoot);
        }
        catch (Exception ex)
        {
            _failures++;
            Plugin.Logger.LogWarning($"Authoring catalog probe failed ({_failures}/3): {ex.GetType().Name}: {ex.Message}");
            if (_failures >= 3)
            {
                _abandoned = true;
                Plugin.Logger.LogWarning("Authoring catalog probe gave up; no further attempts this session.");
            }
        }
    }

    private static void Run(string projectRoot, string identity, StringBuilder log, bool runAuthoringCompile)
    {
        log.AppendLine("rukari voice catalog probe v1 — read only; no delete, import, alias or persist is called.");
        log.AppendLine($"when={DateTime.Now:yyyy-MM-dd HH:mm:ss}; project={identity}");

        AuthoringEditorSession session = AuthoringEditorSession.Current;
        log.AppendLine($"session: projectId={Text(session.ProjectId)}; revision={Text(session.Revision)}; "
            + $"dirty={session.Dirty}; applying={session.IsApplying}; saving={session.SaveInProgress}; "
            + $"file={Text(session.FilePath)}; observedCatalogVersion={Text(session.observedCatalogVersion)}");
        string catalogVersion = AuthoringResourceCatalog.CatalogVersion ?? string.Empty;
        log.AppendLine($"catalog: version={Text(catalogVersion)}; workingManifestDirty={AuthoringResourceCatalog.WorkingManifestDirty}; "
            + $"kinds=[{Join(AuthoringResourceCatalog.Kinds)}]");

        VoiceOverrideCleanupPlan plan;
        try
        {
            plan = VoiceOverrideCleanupPolicy.Inspect(projectRoot);
            log.AppendLine($"manifest: total={plan.Total}; present={plan.Present.Count}; missing={plan.Missing.Count}");
        }
        catch (Exception ex)
        {
            log.AppendLine("manifest: <" + Describe(ex) + ">");
            plan = null!;
        }

        // Both versions are asked on purpose: the session's observation and the live catalog can disagree, and that
        // disagreement is exactly what locks the editor's save path.
        Il2CppSystem.Collections.Generic.HashSet<string>? liveKeys = TryKeys(catalogVersion, log, "catalogVersion");
        string observed = session.observedCatalogVersion ?? string.Empty;
        if (observed.Length != 0 && !string.Equals(observed, catalogVersion, StringComparison.Ordinal))
            TryKeys(observed, log, "observedCatalogVersion");

        if (plan is not null)
        {
            var sample = new List<VoiceOverrideEntry>();
            foreach (VoiceOverrideEntry entry in plan.Missing) { sample.Add(entry); if (sample.Count == 2) break; }
            if (plan.Present.Count != 0 && sample.Count < 3) sample.Add(plan.Present[0]);
            int index = 0;
            foreach (VoiceOverrideEntry entry in sample)
            {
                bool missing = plan.Missing.Contains(entry);
                log.AppendLine($"entry[{index++}]: '{entry.RelativePath}' missing={missing}");
                foreach (string candidate in VoiceCatalogKeyCandidates.For(entry.RelativePath))
                {
                    log.AppendLine($"  candidate '{candidate}': catalogKeys.Contains={Contains(liveKeys, candidate)}");
                    log.AppendLine($"    IdForNative(voice) = {Call(() => AuthoringResourceCatalog.IdForNative("voice", candidate))}");
                    string reference = Call(() => AuthoringResourceCatalog.ReadReference("voice", candidate)?.ToString());
                    log.AppendLine($"    ReadReference(voice) = {reference}");
                    string native = Unwrap(Call(() => AuthoringResourceCatalog.IdForNative("voice", candidate)));
                    if (native.Length != 0 && !native.StartsWith('<'))
                    {
                        log.AppendLine($"    Resolve('{native}') = "
                            + Call(() => AuthoringResourceCatalog.Resolve(native, "voice", catalogVersion, false)?.ToString()));
                    }
                }
            }
        }

        foreach (string kind in ProbeKinds)
            log.AppendLine($"search({kind}) = {Call(() => AuthoringResourceCatalog.Search(kind, null, null, null, 8, catalogVersion)?.ToString())}");

        log.AppendLine($"schema: tools=[{Join(AuthoringApiSchema.ToolNames)}]");
        log.AppendLine($"schema: resourceKinds=[{Join(AuthoringApiSchema.ResourceKinds)}]; lineClear=[{Join(AuthoringApiSchema.LineClear)}]");
        log.AppendLine("schema: capabilities = " + Call(() => AuthoringApiSchema.GetCapabilities()?.ToString()));
        log.AppendLine("catalog: capabilities = " + Call(() => AuthoringResourceCatalog.EnumCapabilities()?.ToString()));
        log.AppendLine($"schema: full = {Call(FullSchema)}");

        // The editor's own view of the open project, and its own compile receipt: the button in the UI only writes a
        // build folder, so the question "why is there no playable file" has to be asked through this channel.
        var clock = Stopwatch.StartNew();
        log.AppendLine($"context (elapsedMs={clock.ElapsedMilliseconds}) = {Call(() => AuthoringEditorSession.Current.Context()?.ToString())}");
        if (!runAuthoringCompile) return;
        clock.Restart();
        log.AppendLine($"compile (elapsedMs={clock.ElapsedMilliseconds}) = {Call(() => AuthoringCompileCompatibility.InvokeBuildOnly(AuthoringEditorSession.Current)?.ToString())}");
    }

    private static string FullSchema()
    {
        var clock = Stopwatch.StartNew();
        string text = AuthoringApiSchema.GetSchema()?.ToString() ?? "<null>";
        clock.Stop();
        return $"length={text.Length}; elapsedMs={clock.ElapsedMilliseconds}";
    }

    private static Il2CppSystem.Collections.Generic.HashSet<string>? TryKeys(string version, StringBuilder log, string label)
    {
        try
        {
            var keys = AuthoringResourceCatalog.AvailableVoiceKeys(version);
            int count = ReferenceEquals(keys, null) ? -1 : keys!.Count;
            log.AppendLine($"AvailableVoiceKeys({label}='{version}') → count={count}");
            return keys;
        }
        catch (Exception ex)
        {
            log.AppendLine($"AvailableVoiceKeys({label}='{version}') → <{Describe(ex)}>");
            return null;
        }
    }

    private static bool Contains(Il2CppSystem.Collections.Generic.HashSet<string>? keys, string candidate)
    {
        if (ReferenceEquals(keys, null)) return false;
        try { return keys!.Contains(candidate); }
        catch (Exception) { return false; }
    }

    private static string Call(Func<string?> action)
    {
        try { return Shorten(action() ?? "<null>"); }
        catch (Exception ex) { return "<" + Describe(ex) + ">"; }
    }

    private static string Text(string? value) => string.IsNullOrEmpty(value) ? "<empty>" : value!;

    private static string Unwrap(string value) => value.StartsWith('<') || value == "<null>" ? string.Empty : value;

    private static string Join(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray? values)
    {
        if (values is null) return "<null>";
        try
        {
            var text = new StringBuilder();
            for (int index = 0; index < values.Length; index++)
            {
                if (index != 0) text.Append(", ");
                text.Append(values[index]);
            }

            return Shorten(text.ToString());
        }
        catch (Exception ex) { return "<" + Describe(ex) + ">"; }
    }

    private static string Join(Il2CppSystem.Collections.Generic.HashSet<string>? values)
    {
        if (ReferenceEquals(values, null)) return "<null>";
        try
        {
            var text = new StringBuilder();
            var seen = 0;
            var enumerator = values!.GetEnumerator();
            while (enumerator.MoveNext() && seen < 40)
            {
                if (seen != 0) text.Append(", ");
                text.Append(enumerator.Current);
                seen++;
            }

            return Shorten($"count={values.Count}; [{text}]");
        }
        catch (Exception ex) { return "<" + Describe(ex) + ">"; }
    }

    private static string Shorten(string value) =>
        value.Length <= MaximumText ? value : value[..MaximumText] + $"…(+{value.Length - MaximumText})";

    private static string Describe(Exception error) =>
        error.GetType().Name + ": " + Shorten(error.Message);

    private static void Publish(StringBuilder report, string diagnosticsRoot)
    {
        string text = report.ToString();
        if (text.Length > MaximumReport) text = text[..MaximumReport];
        string directory = string.IsNullOrWhiteSpace(diagnosticsRoot)
            ? Path.GetTempPath()
            : diagnosticsRoot;
        try
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"authoring-catalog-probe-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Plugin.Logger.LogInfo("Authoring catalog probe written: " + path);
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning("Authoring catalog probe could not be written: " + ex.Message);
        }

        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.TrimEnd('\r');
            if (trimmed.Length != 0) Plugin.Logger.LogInfo("[probe] " + trimmed);
        }
    }
}
