using System;
using System.Security.Cryptography;
using System.Text;

namespace AzureArchive.VideoTools.Core.Projects;

/// <summary>
/// Composes the session-scoped live editor project key from the three
/// StudioCommon inputs (<c>projectName</c>, <c>savedProjectName</c>,
/// <c>entryNode.guid</c>). The key is a plain managed SHA-256 prefix so it can
/// stand in for the rooted-AAP-path hash wherever a canonical 24-hex
/// <c>SceneAddress.ProjectKey</c> is required while the project is unsaved.
/// </summary>
/// <remarks>
/// Residual duplicate-triple risk (documented by design): the key is derived
/// only from the visible project name pair and the entry-node GUID. Two
/// different AAP files that report byte-equal values for all three inputs in
/// the same studio session compose the same key. The entry-node GUID makes a
/// collision inside one open editor implausible, but it is not a content
/// hash of the AAP; the rooted-path source always takes precedence when a
/// trusted archive path has been observed (see
/// <see cref="EffectiveProjectKeyState"/>).
/// </remarks>
public static class LiveProjectKeyComposer
{
    private const string VersionTag = "aavt-live-project/v1";
    public const int KeyLength = 24;
    public const string UnknownProjectSentinel = "_unknown_project";

    /// <summary>
    /// True when all three inputs are present and not whitespace-only. An
    /// all-empty input is unavailable — never guessed — and every partial
    /// triple fails closed as well.
    /// </summary>
    public static bool IsValidInput(
        string? projectName,
        string? savedProjectName,
        string? entryNodeGuid)
    {
        return HasContent(projectName)
            && HasContent(savedProjectName)
            && HasContent(entryNodeGuid);
    }

    /// <summary>
    /// SHA-256 over <c>"aavt-live-project/v1\n{p}\n{s}\n{g}"</c>, truncated to
    /// the upper 24 hex characters. Deterministic for identical inputs.
    /// </summary>
    public static string Compose(
        string projectName,
        string savedProjectName,
        string entryNodeGuid)
    {
        if (!IsValidInput(projectName, savedProjectName, entryNodeGuid))
        {
            throw new ArgumentException(
                "A live project key requires non-empty projectName, savedProjectName, and entryNodeGuid inputs.");
        }

        // The guid arrives as an Il2CppSystem.Guid ToString() ("D") copy; trim
        // surrounding whitespace so formatting noise cannot fork the key.
        string source = $"{VersionTag}\n{projectName.Trim()}\n{savedProjectName.Trim()}\n{entryNodeGuid.Trim()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..KeyLength];
    }

    private static bool HasContent(string? value) =>
        !string.IsNullOrWhiteSpace(value);
}

/// <summary>
/// Pure state machine behind <c>ScenarioFileService</c>'s effective project
/// key. Fixed precedence under one lock:
/// rooted AAP path hash (source A) &gt; adopted live StudioCommon key
/// (source B) &gt; <see cref="LiveProjectKeyComposer.UnknownProjectSentinel"/>.
/// A rotation edge is raised exactly once whenever the effective key value
/// changes for any reason; idempotent re-observations never rotate.
/// </summary>
public sealed class EffectiveProjectKeyState
{
    private readonly object _lock = new();
    private string _rootedPath = string.Empty;
    private string _rootedPathKey = string.Empty;
    private string _adoptedLiveKey = string.Empty;
    private bool _rotatedPending;

    /// <summary>The effective 24-hex key, or the unchanged unknown sentinel.</summary>
    public string EffectiveKey
    {
        get
        {
            lock (_lock)
            {
                return EffectiveKeyNoLock();
            }
        }
    }

    public bool HasCanonicalKey
    {
        get
        {
            lock (_lock)
            {
                return IsCanonical24Hex(EffectiveKeyNoLock());
            }
        }
    }

    /// <summary>
    /// Source A: observe a normalized rooted AAP path. Empty/null input is a
    /// no-op (the observer guards whitespace before calling). A changed rooted
    /// path replaces any previous one and rotates when the effective value
    /// changes.
    /// </summary>
    public void ObserveRootedPath(string? normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return;
        }

        string candidateKey = HashPath(normalizedPath!);
        lock (_lock)
        {
            if (string.Equals(_rootedPathKey, candidateKey, StringComparison.Ordinal))
            {
                return;
            }

            string before = EffectiveKeyNoLock();
            _rootedPath = normalizedPath!;
            _rootedPathKey = candidateKey;
            RaiseRotatedIfChanged(before, EffectiveKeyNoLock());
        }
    }

    /// <summary>
    /// Source B: adopt a composed live StudioCommon key. Returns true exactly
    /// when adoption changed the effective key (a real rotation). A rooted
    /// path keeps precedence; adoption underneath it changes nothing.
    /// </summary>
    public bool AdoptLiveProjectKey(string? liveKey24Hex)
    {
        if (!IsCanonical24Hex(liveKey24Hex))
        {
            return false;
        }

        lock (_lock)
        {
            if (string.Equals(_adoptedLiveKey, liveKey24Hex, StringComparison.Ordinal))
            {
                return false;
            }

            string before = EffectiveKeyNoLock();
            _adoptedLiveKey = liveKey24Hex!;
            string after = EffectiveKeyNoLock();
            bool rotated = !string.Equals(before, after, StringComparison.Ordinal);
            if (rotated)
            {
                _rotatedPending = true;
            }

            return rotated;
        }
    }

    /// <summary>Edge-triggered: true only for the first consume after a change.</summary>
    public bool ConsumeRotated()
    {
        lock (_lock)
        {
            if (!_rotatedPending)
            {
                return false;
            }

            _rotatedPending = false;
            return true;
        }
    }

    private string EffectiveKeyNoLock()
    {
        if (_rootedPathKey.Length != 0)
        {
            return _rootedPathKey;
        }

        if (_adoptedLiveKey.Length != 0)
        {
            return _adoptedLiveKey;
        }

        return LiveProjectKeyComposer.UnknownProjectSentinel;
    }

    private void RaiseRotatedIfChanged(string before, string after)
    {
        if (!string.Equals(before, after, StringComparison.Ordinal))
        {
            _rotatedPending = true;
        }
    }

    private static string HashPath(string normalizedPath) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)))[..LiveProjectKeyComposer.KeyLength];

    internal static bool IsCanonical24Hex(string? value)
    {
        if (value == null || value.Length != LiveProjectKeyComposer.KeyLength)
        {
            return false;
        }

        for (int index = 0; index < value.Length; index++)
        {
            if (!Uri.IsHexDigit(value[index]))
            {
                return false;
            }
        }

        return true;
    }
}
