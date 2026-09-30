using System.Security.Cryptography;
using System.Text;
using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Mapping;

public enum ObservedSceneIdentityStatus
{
    NotFound = 0,
    AmbiguousPlaybackScript = 1,
    PlaybackRecordOnly = 2,
    Mapped = 3
}

public sealed record ObservedCompiledSceneIdentity(
    string CompiledScriptSha256,
    int CompiledScriptLength,
    int CompiledScriptLineCount)
{
    /// <summary>
    /// Engine row index (<c>Test.cur</c>) observed in the same advance window,
    /// or -1 when unknown (2026-09-18). Cards with identical dialogue text —
    /// empty camera-only cards in particular — compile to identical scripts, so
    /// the identity alone cannot tell their records apart; the row index can.
    /// </summary>
    public int PlaybackRowIndex { get; init; } = -1;
}

public sealed record ObservedSceneIdentityResolution(
    ObservedSceneIdentityStatus Status,
    int? PlaybackRecordIndex,
    SceneKey? Scene,
    IReadOnlyList<int> CandidatePlaybackRecordIndices,
    bool SelectedSceneTrusted,
    bool ArchivePairFresh,
    bool PairingTrusted,
    IReadOnlyList<ProjectPlaybackMappingIssue> MappingIssues);

public interface IObservedSceneIdentityResolver
{
    Result<ObservedSceneIdentityResolution> Resolve(
        ObservedCompiledSceneIdentity identity,
        ProjectSnapshot project,
        PlaybackArchiveSnapshot playback);
}

/// <summary>
/// Lets a caller rule out a playback archive before paying for its paired
/// project archive. Project archives are JSON and dominate a full sweep, while
/// playback archives are compact binary, so asking the cheap half first is what
/// keeps a first-time sweep from parsing every project in the data root.
///
/// <c>Ok(false)</c> is a promise that <see cref="IObservedSceneIdentityResolver.Resolve"/>
/// would report <see cref="ObservedSceneIdentityStatus.NotFound"/> for this
/// playback, and <c>Fail</c> carries the failure that resolution would report.
/// An implementation with nothing cheap to add must answer <c>Ok(true)</c>.
/// </summary>
public interface IPlaybackScriptPreFilter
{
    Result<bool> MayContainScript(
        ObservedCompiledSceneIdentity identity,
        PlaybackArchiveSnapshot playback);
}

public sealed class ObservedSceneIdentityResolver :
    IObservedSceneIdentityResolver,
    IPlaybackScriptPreFilter
{
    private readonly IProjectPlaybackMapper _playbackMapper;
    private readonly Func<string, string> _compiledScriptProjection;
    private readonly Func<string>? _projectionCacheKey;

    public ObservedSceneIdentityResolver(
        IProjectPlaybackMapper? playbackMapper = null,
        Func<string, string>? compiledScriptProjection = null)
        : this(playbackMapper, compiledScriptProjection, projectionCacheKey: null)
    {
    }

    /// <summary>
    /// Adds an opt-in projected-script index cache. <paramref name="projectionCacheKey"/>
    /// must identify the projection currently in effect and must change whenever
    /// that projection would: the index it labels is reused for every later
    /// resolution against the same playback snapshot. Passing null keeps the
    /// original record-by-record scan, so existing callers are unaffected.
    /// </summary>
    public ObservedSceneIdentityResolver(
        IProjectPlaybackMapper? playbackMapper,
        Func<string, string>? compiledScriptProjection,
        Func<string>? projectionCacheKey)
    {
        _playbackMapper = playbackMapper ?? new ConservativeProjectPlaybackMapper();
        _compiledScriptProjection = compiledScriptProjection ?? (script => script);
        _projectionCacheKey = projectionCacheKey;
    }

    /// <summary>
    /// Answers whether this playback snapshot could carry the observed script,
    /// without needing the paired project. Shares its identity validation and
    /// its candidate filter with <see cref="Resolve"/>, so a false answer means
    /// exactly what resolution's NotFound means.
    /// </summary>
    public Result<bool> MayContainScript(
        ObservedCompiledSceneIdentity identity,
        PlaybackArchiveSnapshot playback)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(playback);

        string? identityError = ValidateIdentity(identity, out string normalizedHash);
        if (identityError != null)
        {
            return Result<bool>.Fail(identityError);
        }

        if (playback.Records == null)
        {
            return Result<bool>.Fail("Playback snapshot has no record collection.");
        }

        string? projectionKey = _projectionCacheKey?.Invoke();
        if (projectionKey == null)
        {
            // Without an index there is nothing cheap to ask, so abstain and
            // let the caller run the full resolution it would have run anyway.
            return Result<bool>.Ok(true);
        }

        Result<PlaybackScriptIndex> index = PlaybackScriptIndexCache.GetOrBuild(
            playback,
            projectionKey,
            _compiledScriptProjection);
        if (!index.Success || index.Value == null)
        {
            return Result<bool>.Fail(index.Error);
        }

        var candidates = new List<int>();
        index.Value.CollectCandidates(
            normalizedHash,
            identity.CompiledScriptLength,
            identity.CompiledScriptLineCount,
            candidates);
        return Result<bool>.Ok(candidates.Count > 0);
    }

    public Result<ObservedSceneIdentityResolution> Resolve(
        ObservedCompiledSceneIdentity identity,
        ProjectSnapshot project,
        PlaybackArchiveSnapshot playback)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(playback);

        string? identityError = ValidateIdentity(identity, out string normalizedHash);
        if (identityError != null)
        {
            return Result<ObservedSceneIdentityResolution>.Fail(identityError);
        }

        if (playback.Records == null)
        {
            return Result<ObservedSceneIdentityResolution>.Fail(
                "Playback snapshot has no record collection.");
        }

        var candidates = new List<int>();
        string? projectionKey = _projectionCacheKey?.Invoke();
        if (projectionKey != null)
        {
            Result<PlaybackScriptIndex> index = PlaybackScriptIndexCache.GetOrBuild(
                playback,
                projectionKey,
                _compiledScriptProjection);
            if (!index.Success || index.Value == null)
            {
                return Result<ObservedSceneIdentityResolution>.Fail(index.Error);
            }

            index.Value.CollectCandidates(
                normalizedHash,
                identity.CompiledScriptLength,
                identity.CompiledScriptLineCount,
                candidates);
        }
        else
        {
            string? scanError = ScanRecords(identity, normalizedHash, playback, candidates);
            if (scanError != null)
            {
                return Result<ObservedSceneIdentityResolution>.Fail(scanError);
            }
        }

        if (candidates.Count == 0)
        {
            return Result<ObservedSceneIdentityResolution>.Ok(new ObservedSceneIdentityResolution(
                ObservedSceneIdentityStatus.NotFound,
                null,
                null,
                Array.Empty<int>(),
                false,
                false,
                false,
                Array.Empty<ProjectPlaybackMappingIssue>()));
        }

        if (candidates.Count > 1)
        {
            return Result<ObservedSceneIdentityResolution>.Ok(new ObservedSceneIdentityResolution(
                ObservedSceneIdentityStatus.AmbiguousPlaybackScript,
                null,
                null,
                candidates.AsReadOnly(),
                false,
                false,
                false,
                Array.Empty<ProjectPlaybackMappingIssue>()));
        }

        int recordIndex = candidates[0];
        Result<ProjectPlaybackMappingReport> mappingResult = _playbackMapper.Map(project, playback);
        if (!mappingResult.Success || mappingResult.Value == null)
        {
            return Result<ObservedSceneIdentityResolution>.Fail(
                $"Project/playback mapping failed: {mappingResult.Error}");
        }

        ProjectPlaybackMappingReport report = mappingResult.Value;
        ScenePlaybackMapping[] mappedScenes = report.Scenes
            .Where(scene => scene.PlaybackRecordIndex == recordIndex)
            .ToArray();
        if (mappedScenes.Length > 1)
        {
            return Result<ObservedSceneIdentityResolution>.Fail(
                $"Playback record {recordIndex} was mapped to more than one project scene.");
        }

        bool archivePairFresh = IsArchivePairFresh(report.Issues);
        if (mappedScenes.Length == 0)
        {
            return Result<ObservedSceneIdentityResolution>.Ok(new ObservedSceneIdentityResolution(
                ObservedSceneIdentityStatus.PlaybackRecordOnly,
                recordIndex,
                null,
                candidates.AsReadOnly(),
                false,
                archivePairFresh,
                false,
                report.Issues));
        }

        bool selectedSceneTrusted = !HasIssue(report.Issues, "FileNameMismatch");
        bool pairingTrusted = !report.Issues.Any(issue =>
            issue.Severity >= ProjectPlaybackMappingIssueSeverity.Warning);
        return Result<ObservedSceneIdentityResolution>.Ok(new ObservedSceneIdentityResolution(
            ObservedSceneIdentityStatus.Mapped,
            recordIndex,
            mappedScenes[0].Scene,
            candidates.AsReadOnly(),
            selectedSceneTrusted,
            archivePairFresh,
            pairingTrusted,
            report.Issues));
    }

    /// <summary>
    /// The original record-by-record resolution. It stays the default for
    /// callers that supply no projection cache key, and it remains the
    /// reference behaviour the indexed path has to reproduce exactly.
    /// Returns the failure message, or null on success.
    /// </summary>
    private string? ScanRecords(
        ObservedCompiledSceneIdentity identity,
        string normalizedHash,
        PlaybackArchiveSnapshot playback,
        List<int> candidates)
    {
        for (int index = 0; index < playback.Records.Count; index++)
        {
            PlaybackRecordSnapshot? record = playback.Records[index];
            if (record == null
                || record.RecordIndex != index
                || record.CompiledScript == null)
            {
                return
                    $"Playback record {index} is null or violates the positional compiled-script contract.";
            }

            string projectedScript;
            try
            {
                projectedScript = _compiledScriptProjection(record.CompiledScript)
                    ?? throw new InvalidOperationException(
                        "Compiled-script identity projection returned null.");
            }
            catch (Exception ex)
            {
                return $"Playback record {index} identity projection failed: {ex.Message}";
            }

            if (projectedScript.Length != identity.CompiledScriptLength
                || CountLines(projectedScript) != identity.CompiledScriptLineCount)
            {
                continue;
            }

            string recordHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(projectedScript)));
            if (string.Equals(recordHash, normalizedHash, StringComparison.Ordinal))
            {
                candidates.Add(index);
            }
        }

        return null;
    }

    /// <summary>
    /// The identity checks both entry points share, so their failure messages
    /// cannot drift apart. Returns the error, or null with a normalized hash.
    /// </summary>
    private static string? ValidateIdentity(
        ObservedCompiledSceneIdentity identity,
        out string normalizedHash)
    {
        if (!TryNormalizeSha256(identity.CompiledScriptSha256, out normalizedHash))
        {
            return "Observed scene identity must contain exactly 64 hexadecimal SHA-256 characters.";
        }

        if (identity.CompiledScriptLength < 0 || identity.CompiledScriptLineCount < 1)
        {
            return "Observed scene identity has an invalid script length or line count.";
        }

        return null;
    }

    private static bool IsArchivePairFresh(
        IReadOnlyList<ProjectPlaybackMappingIssue> issues) =>
        !HasIssue(issues, "FileNameMismatch")
        && !HasIssue(issues, "PlaybackOlderThanProject");

    private static bool HasIssue(
        IReadOnlyList<ProjectPlaybackMappingIssue> issues,
        string code) => issues.Any(issue =>
            string.Equals(issue.Code, code, StringComparison.Ordinal));

    private static int CountLines(string text)
    {
        int count = 1;
        foreach (char character in text)
        {
            if (character == '\n')
            {
                count++;
            }
        }

        return count;
    }

    private static bool TryNormalizeSha256(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (value == null || value.Length != 64)
        {
            return false;
        }

        foreach (char character in value)
        {
            bool isHex = character is >= '0' and <= '9'
                or >= 'a' and <= 'f'
                or >= 'A' and <= 'F';
            if (!isHex)
            {
                return false;
            }
        }

        normalized = value.ToUpperInvariant();
        return true;
    }
}
