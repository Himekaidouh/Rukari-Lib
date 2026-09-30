using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AzureArchive.VideoTools.Api;
using AzureArchive.VideoTools.Core;
using AzureArchive.VideoTools.Core.Mapping;
using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Formats.Aap;
using AzureArchive.VideoTools.Formats.Aas;
using AzureArchive.VideoTools.Core.Results;
using AzureArchive.VideoTools.Runtime;
using BepInEx.Configuration;
using Rukari.Lib;
using Rukari.Lib.Commands;

namespace AzureArchive.VideoTools.Interop;

internal sealed class ConfiguredSceneResolutionService : IConfiguredSceneResolutionService
{
    private const string CapabilityId = "Editor.ConfiguredSceneResolution";
    private readonly object _lock = new();
    private readonly RuntimeCapabilityService _capabilities;
    private readonly ConfigEntry<bool> _enabled;
    private readonly ConfigEntry<string> _projectPath;
    private readonly ConfigEntry<string> _playbackPath;
    private readonly ConfigEntry<bool> _autoDiscoverEnabled;
    private readonly ConfigEntry<string> _autoDataRoot;
    private readonly ConfigEntry<bool> _embeddedEditorCommandsEnabled;
    private readonly ConfigEntry<bool> _acceptLegacyCharacterDirectiveAlias;
    private readonly ConfigEntry<bool> _liveProjectKeyEnabled;
    private readonly EmbeddedAavtDirectiveExtractor _embeddedExtractor = new();
    private ResolutionRequest? _pending;
    private ConfiguredSceneResolutionSnapshot? _current;
    private bool _workerRunning;

    public ConfiguredSceneResolutionService(
        ConfigFile config,
        RuntimeCapabilityService capabilities)
    {
        ArgumentNullException.ThrowIfNull(config);
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _enabled = config.Bind(
            "SceneIdentityFiles",
            "Enabled",
            false,
            "Resolve managed scene identities against explicitly configured read-only AAP/AAS files.");
        _projectPath = config.Bind(
            "SceneIdentityFiles",
            "ProjectPath",
            string.Empty,
            "Absolute path to the AAP project used for read-only scene identity resolution.");
        _playbackPath = config.Bind(
            "SceneIdentityFiles",
            "PlaybackPath",
            string.Empty,
            "Absolute path to the matching AAS playback archive used for read-only resolution.");
        _autoDiscoverEnabled = config.Bind(
            "SceneIdentityFiles",
            "AutoDiscoverEnabled",
            false,
            "AutoAllProjects: discover same-name AAP/AAS pairs under the game data root and bind the pair matching the currently open project by scene script identity. Overrides ProjectPath/PlaybackPath when enabled.");
        _autoDataRoot = config.Bind(
            "SceneIdentityFiles",
            "AutoDataRoot",
            string.Empty,
            "Optional absolute data root containing projects/ and saves/. Empty derives the root from the game's persistent data path.");
        _embeddedEditorCommandsEnabled = config.Bind(
            "EditorEmbeddedCommands",
            "Enabled",
            true,
            "Read #aavt directives from the Environment/Additional Prompt field, sanitize them before official parsing, and compile them into playback commands.");
        _acceptLegacyCharacterDirectiveAlias = config.Bind(
            "EditorEmbeddedCommands",
            "AcceptLegacyCharacterDirectiveAlias",
            true,
            "Accept legacy #char directives in addition to the namespaced #aavt;char syntax.");
        _liveProjectKeyEnabled = config.Bind(
            "SceneIdentityFiles",
            "LiveProjectKeyFromStudioCommon",
            false,
            "STAGE-GATED (default off): adopt a session live project key from StudioCommon projectName/savedProjectName/entryNode.guid when no trusted AAP path and no active auto-discovered pair exist. Disabled behavior is unchanged and stays fail-closed on _unknown_project.");
        LiveEditorProjectKeySource.Configure(_liveProjectKeyEnabled.Value);
        _capabilities.Bound(
            CapabilityId,
            "explicit config paths + managed background AAP/AAS readers initialized");
        ActiveProjectPairSource.Configure(
            _autoDiscoverEnabled.Value,
            _autoDataRoot.Value,
            path => new AapProjectReader().Read(path),
            path => new AasScenarioReader().Read(path),
            compiledScriptProjection: null,
            projectionCacheKey: null,
            projectionSnapshot: CaptureEditorIdentityProjection);
        Plugin.Logger.LogInfo(
            $"Configured scene resolver initialized: enabled={_enabled.Value}; "
            + $"projectSet={!string.IsNullOrWhiteSpace(_projectPath.Value)}; "
            + $"playbackSet={!string.IsNullOrWhiteSpace(_playbackPath.Value)}; "
            + $"autoDiscover={_autoDiscoverEnabled.Value}; "
            + "worker=managed-background-read-only.");
    }

    public ConfiguredSceneResolutionSnapshot? Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    internal void Enqueue(EditorSceneIdentitySnapshot identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!_enabled.Value)
        {
            SetCurrent(new ConfiguredSceneResolutionSnapshot(
                identity.ObservationSequence,
                ConfiguredSceneResolutionStatus.Disabled,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                null,
                string.Empty,
                null,
                string.Empty,
                false,
                false,
                false,
                "SceneIdentityFiles.Enabled is false."));
            return;
        }

        lock (_lock)
        {
            _pending = new ResolutionRequest(
                identity,
                _projectPath.Value ?? string.Empty,
                _playbackPath.Value ?? string.Empty);
            if (_workerRunning)
            {
                return;
            }

            _workerRunning = true;
        }

        _ = Task.Run(ProcessPending);
    }

    private void ProcessPending()
    {
        while (true)
        {
            ResolutionRequest request;
            lock (_lock)
            {
                if (_pending == null)
                {
                    _workerRunning = false;
                    return;
                }

                request = _pending;
                _pending = null;
            }

            try
            {
                Resolve(
                    request.Identity,
                    request.ProjectPath,
                    request.PlaybackPath);
            }
            catch (Exception ex)
            {
                PublishFailure(
                    request.Identity,
                    ConfiguredSceneResolutionStatus.Failed,
                    string.Empty,
                    string.Empty,
                    $"Unhandled managed resolver error: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void Resolve(
        EditorSceneIdentitySnapshot identity,
        string configuredProjectPath,
        string configuredPlaybackPath)
    {
        if (_autoDiscoverEnabled.Value)
        {
            ResolveAuto(identity);
            return;
        }

        bool projectPathValid = TryNormalizePath(
            configuredProjectPath,
            ".aap",
            out string projectPath);
        bool playbackPathValid = TryNormalizePath(
            configuredPlaybackPath,
            ".aas",
            out string playbackPath);
        if (!projectPathValid || !playbackPathValid)
        {
            PublishFailure(
                identity,
                ConfiguredSceneResolutionStatus.NotConfigured,
                projectPath,
                playbackPath,
                "Configured paths must be existing absolute .aap and .aas files.");
            return;
        }

        var projectResult = new AapProjectReader().Read(projectPath);
        var playbackResult = new AasScenarioReader().Read(playbackPath);
        if (!projectResult.Success
            || projectResult.Value == null
            || !playbackResult.Success
            || playbackResult.Value == null)
        {
            PublishFailure(
                identity,
                ConfiguredSceneResolutionStatus.Failed,
                projectPath,
                playbackPath,
                $"Read failed: AAP={projectResult.Error}; AAS={playbackResult.Error}");
            return;
        }

        PublishResolvedIdentity(
            identity,
            projectResult.Value,
            playbackResult.Value,
            projectPath,
            playbackPath,
            "explicit-config");
    }

    private EditorIdentityProjectionSnapshot CaptureEditorIdentityProjection()
    {
        var directives = ModServices.Current?.GetService<IEmbeddedDirectiveService>();
        if (directives is { Success: true, Value: not null })
        {
            IEmbeddedDirectiveSanitizer snapshot = directives.Value.CaptureSanitizer();
            return new(snapshot.Sanitize, "editor-projection:rukari-directives-v1:" + snapshot.Revision);
        }

        // Retain the old managed fallback for hosts without the new service. Capture both
        // flags now; changing settings during a background sweep cannot relabel its cache.
        bool enabled = _embeddedEditorCommandsEnabled.Value;
        bool legacy = _acceptLegacyCharacterDirectiveAlias.Value;
        return enabled
            ? new(script => _embeddedExtractor.Extract(script, legacy).SanitizedText,
                legacy ? "editor-projection:embedded+legacy-alias" : "editor-projection:embedded")
            : new(static script => script, "editor-projection:identity");
    }

    private void ResolveAuto(EditorSceneIdentitySnapshot identity)
    {
        var observed = new ObservedCompiledSceneIdentity(
            identity.CompiledScriptSha256,
            identity.CompiledScriptLength,
            identity.CompiledScriptLineCount);
        Result<ActivePairBinding> binding =
            ActiveProjectPairSource.ResolveActive(observed);
        if (!binding.Success || binding.Value == null)
        {
            PublishFailure(
                identity,
                ConfiguredSceneResolutionStatus.Failed,
                string.Empty,
                string.Empty,
                binding.Error,
                "auto-discover");
            return;
        }

        ActivePairBinding pair = binding.Value;
        // FIX B source 1: a successful auto-discover resolution proves the
        // trusted AAP path of the open project. Push the already-managed path
        // string into the file service (rooted candidates win precedence
        // there); only a managed string crosses, so this is safe from this
        // background Task.Run worker. Idempotent re-reads are no-ops inside
        // ObserveProject; the rotation edge fires exactly once per change.
        Plugin.Host.FilesInternal.ObserveProject(
            pair.Pair.AapPath,
            "auto-discover-trusted-bind");
        if (Plugin.Host.FilesInternal.ConsumeProjectKeyRotated())
        {
            EmbeddedEditorPreviewLeaseCache.OnProjectKeyRotated(
                "trusted-path-arrived",
                ShortKey(Plugin.Host.FilesInternal.EffectiveProjectKey));
        }

        PublishResolution(
            identity,
            pair.Resolution,
            pair.Project,
            pair.Playback,
            pair.Pair.AapPath,
            pair.Pair.AasPath,
            "auto-discover",
            $"auto-discovered project '{pair.Pair.Name}'");
        if (ActiveProjectPairSource.ConsumePairChanged())
        {
            Plugin.Logger.LogInfo(
                $"Auto project discovery active pair changed to '{pair.Pair.Name}'; "
                + "requesting player command index reload.");
            PlayerCommandObservationRuntime.RequestIndexReload();
        }
    }

    private void PublishResolvedIdentity(
        EditorSceneIdentitySnapshot identity,
        ProjectSnapshot project,
        PlaybackArchiveSnapshot playback,
        string projectPath,
        string playbackPath,
        string sourceLabel)
    {
        EditorIdentityProjectionSnapshot projection = CaptureEditorIdentityProjection();
        var resolveResult = new ObservedSceneIdentityResolver(
            playbackMapper: null,
            compiledScriptProjection: projection.Project,
            projectionCacheKey: () => projection.CacheKey).Resolve(
            new ObservedCompiledSceneIdentity(
                identity.CompiledScriptSha256,
                identity.CompiledScriptLength,
                identity.CompiledScriptLineCount),
            project,
            playback);
        if (!resolveResult.Success || resolveResult.Value == null)
        {
            PublishFailure(
                identity,
                ConfiguredSceneResolutionStatus.Failed,
                projectPath,
                playbackPath,
                $"Resolve failed: {resolveResult.Error}",
                sourceLabel);
            return;
        }

        PublishResolution(
            identity,
            resolveResult.Value,
            project,
            playback,
            projectPath,
            playbackPath,
            sourceLabel,
            diagnostic: null);
    }

    private void PublishResolution(
        EditorSceneIdentitySnapshot identity,
        ObservedSceneIdentityResolution resolution,
        ProjectSnapshot project,
        PlaybackArchiveSnapshot playback,
        string projectPath,
        string playbackPath,
        string sourceLabel,
        string? diagnostic)
    {
        SceneKey? scene = resolution.Scene;
        string issues = string.Join(
            ",",
            resolution.MappingIssues.Select(issue => issue.Code));
        var snapshot = new ConfiguredSceneResolutionSnapshot(
            identity.ObservationSequence,
            ConvertStatus(resolution.Status),
            projectPath,
            playbackPath,
            project.Source.RevisionSha256,
            playback.Source.RevisionSha256,
            resolution.PlaybackRecordIndex,
            scene?.NodeGuid ?? string.Empty,
            scene?.SceneIndex,
            scene?.Fingerprint ?? string.Empty,
            resolution.SelectedSceneTrusted,
            resolution.ArchivePairFresh,
            resolution.PairingTrusted,
            issues.Length == 0 ? (diagnostic ?? "none") : issues);
        SetCurrent(snapshot);

        if (snapshot.Status == ConfiguredSceneResolutionStatus.Mapped
            && snapshot.SelectedSceneTrusted)
        {
            _capabilities.Verified(
                CapabilityId,
                "managed identity uniquely resolved to a trusted selected AAP/AAS scene");
            if (snapshot.ArchivePairFresh && snapshot.PairingTrusted)
            {
                Plugin.Logger.LogInfo(FormatResult(snapshot, sourceLabel));
            }
            else
            {
                Plugin.Logger.LogWarning(FormatResult(snapshot, sourceLabel));
            }
        }
        else
        {
            _capabilities.Degraded(
                CapabilityId,
                $"status={snapshot.Status}; selectedTrusted={snapshot.SelectedSceneTrusted}; "
                + $"archiveFresh={snapshot.ArchivePairFresh}; issues={snapshot.Diagnostic}");
            Plugin.Logger.LogWarning(FormatResult(snapshot, sourceLabel));
        }
    }

    private void PublishFailure(
        EditorSceneIdentitySnapshot identity,
        ConfiguredSceneResolutionStatus status,
        string projectPath,
        string playbackPath,
        string diagnostic,
        string sourceLabel = "explicit-config")
    {
        var snapshot = new ConfiguredSceneResolutionSnapshot(
            identity.ObservationSequence,
            status,
            projectPath,
            playbackPath,
            string.Empty,
            string.Empty,
            null,
            string.Empty,
            null,
            string.Empty,
            false,
            false,
            false,
            diagnostic);
        SetCurrent(snapshot);
        _capabilities.Degraded(CapabilityId, diagnostic);
        Plugin.Logger.LogWarning(FormatResult(snapshot, sourceLabel));
    }

    private void SetCurrent(ConfiguredSceneResolutionSnapshot snapshot)
    {
        lock (_lock)
        {
            if (_current == null
                || snapshot.ObservationSequence >= _current.ObservationSequence)
            {
                _current = snapshot;
            }
        }
    }

    private static ConfiguredSceneResolutionStatus ConvertStatus(
        ObservedSceneIdentityStatus status) => status switch
        {
            ObservedSceneIdentityStatus.NotFound => ConfiguredSceneResolutionStatus.NotFound,
            ObservedSceneIdentityStatus.AmbiguousPlaybackScript =>
                ConfiguredSceneResolutionStatus.AmbiguousPlaybackScript,
            ObservedSceneIdentityStatus.PlaybackRecordOnly =>
                ConfiguredSceneResolutionStatus.PlaybackRecordOnly,
            ObservedSceneIdentityStatus.Mapped => ConfiguredSceneResolutionStatus.Mapped,
            _ => ConfiguredSceneResolutionStatus.Failed
        };

    private static bool TryNormalizePath(
        string? value,
        string extension,
        out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            normalized = Path.GetFullPath(value.Trim());
            return Path.IsPathRooted(normalized)
                && string.Equals(
                    Path.GetExtension(normalized),
                    extension,
                    StringComparison.OrdinalIgnoreCase)
                && File.Exists(normalized);
        }
        catch
        {
            normalized = string.Empty;
            return false;
        }
    }

    private static string FormatResult(
        ConfiguredSceneResolutionSnapshot snapshot,
        string sourceLabel) =>
        "Configured scene resolution: "
        + $"sequence={snapshot.ObservationSequence}; status={snapshot.Status}; "
        + $"record={snapshot.PlaybackRecordIndex?.ToString() ?? "none"}; "
        + $"node={(snapshot.NodeGuid.Length == 0 ? "none" : snapshot.NodeGuid)}; "
        + $"scene={snapshot.SceneIndex?.ToString() ?? "none"}; "
        + $"selectedTrusted={snapshot.SelectedSceneTrusted}; "
        + $"archiveFresh={snapshot.ArchivePairFresh}; "
        + $"pairingTrusted={snapshot.PairingTrusted}; issues={snapshot.Diagnostic}; "
        + $"source={sourceLabel}-read-only-background-worker.";

    private static string ShortKey(string key) =>
        key.Length <= 16 ? key : key[..16];

    private sealed record ResolutionRequest(
        EditorSceneIdentitySnapshot Identity,
        string ProjectPath,
        string PlaybackPath);
}
