using System;
using System.IO;
using AzureArchive.VideoTools.Api;
using AzureArchive.VideoTools.Core;
using AzureArchive.VideoTools.Core.Projects;

namespace AzureArchive.VideoTools.Interop;

internal sealed class ScenarioFileService : IScenarioFileService
{
    private readonly object _lock = new();
    private readonly RuntimeCapabilityService _capabilities;
    private readonly EffectiveProjectKeyState _effectiveKey = new();
    private string _projectReference = string.Empty;
    private string _saveReference = string.Empty;
    private string _lastSource = "startup";

    public ScenarioFileService(RuntimeCapabilityService capabilities)
    {
        _capabilities = capabilities;
    }

    public ScenarioFileSnapshot Current
    {
        get
        {
            lock (_lock)
            {
                return new ScenarioFileSnapshot(
                    _projectReference,
                    _saveReference,
                    _effectiveKey.EffectiveKey,
                    _lastSource);
            }
        }
    }

    /// <summary>24-hex effective key (rooted path &gt; adopted live key &gt; sentinel).</summary>
    internal string EffectiveProjectKey
    {
        get
        {
            lock (_lock)
            {
                return _effectiveKey.EffectiveKey;
            }
        }
    }

    public bool HasCanonicalProjectKey
    {
        get
        {
            lock (_lock)
            {
                return _effectiveKey.HasCanonicalKey;
            }
        }
    }

    /// <summary>Edge-triggered: true once per effective-key value change.</summary>
    public bool ConsumeProjectKeyRotated()
    {
        lock (_lock)
        {
            return _effectiveKey.ConsumeRotated();
        }
    }

    /// <summary>
    /// Source B: adopt a composed live StudioCommon key. Returns true when the
    /// adoption rotated the effective project key.
    /// </summary>
    internal bool AdoptLiveProjectKey(string? liveKey24Hex, out bool rotated)
    {
        lock (_lock)
        {
            rotated = _effectiveKey.AdoptLiveProjectKey(liveKey24Hex);
            if (rotated)
            {
                _lastSource = "live-studio-common";
            }

            return rotated;
        }
    }

    public void ObserveProject(string? value, string source)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        string candidate = Normalize(value);
        lock (_lock)
        {
            // Idempotent re-reads (the auto-discover trusted bind pushes the
            // same path on every resolution) must not churn state or logs.
            if (string.Equals(_projectReference, candidate, StringComparison.Ordinal))
            {
                return;
            }

            if (ShouldReplace(_projectReference, candidate))
            {
                _projectReference = candidate;
                _lastSource = source;
            }
        }

        // Source A: the rooted path hash owns precedence inside the shared
        // effective-key state; a changed path raises the rotation edge.
        _effectiveKey.ObserveRootedPath(candidate);
        _capabilities.Verified("Files.ProjectReference", $"captured by {source}");
    }

    public void ObserveSave(string? value, string source)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        string candidate = Normalize(value);
        lock (_lock)
        {
            if (string.Equals(_saveReference, candidate, StringComparison.Ordinal))
            {
                return;
            }

            if (ShouldReplace(_saveReference, candidate))
            {
                _saveReference = candidate;
                _lastSource = source;
            }
        }

        _capabilities.Verified("Files.SaveReference", $"captured by {source}");
    }

    private static string Normalize(string value)
    {
        value = value.Trim();
        if (!Path.IsPathRooted(value))
        {
            return value;
        }

        try
        {
            return Path.GetFullPath(value);
        }
        catch
        {
            return value;
        }
    }

    private static bool ShouldReplace(string current, string candidate)
    {
        if (string.IsNullOrWhiteSpace(current))
        {
            return true;
        }

        return Path.IsPathRooted(candidate) || !Path.IsPathRooted(current);
    }
}
