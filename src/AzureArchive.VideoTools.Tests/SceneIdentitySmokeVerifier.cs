using AzureArchive.VideoTools.Core.Mapping;
using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Formats.Aap;
using AzureArchive.VideoTools.Formats.Aas;

namespace AzureArchive.VideoTools.Tests;

internal static class SceneIdentitySmokeVerifier
{
    public static int Verify(
        string projectPath,
        string playbackPath,
        string sha256,
        string lengthText,
        string lineCountText)
    {
        if (!int.TryParse(lengthText, out int length)
            || !int.TryParse(lineCountText, out int lineCount))
        {
            Console.Error.WriteLine("SCENE IDENTITY FAIL length and line count must be integers.");
            return 2;
        }

        var projectResult = new AapProjectReader().Read(projectPath);
        var playbackResult = new AasScenarioReader().Read(playbackPath);
        if (!projectResult.Success
            || projectResult.Value == null
            || !playbackResult.Success
            || playbackResult.Value == null)
        {
            Console.Error.WriteLine(
                $"SCENE IDENTITY FAIL AAP={projectResult.Error} AAS={playbackResult.Error}");
            return 1;
        }

        var resolveResult = new ObservedSceneIdentityResolver().Resolve(
            new ObservedCompiledSceneIdentity(sha256, length, lineCount),
            projectResult.Value,
            playbackResult.Value);
        if (!resolveResult.Success || resolveResult.Value == null)
        {
            Console.Error.WriteLine($"SCENE IDENTITY FAIL {resolveResult.Error}");
            return 1;
        }

        ObservedSceneIdentityResolution resolution = resolveResult.Value;
        SceneKey? scene = resolution.Scene;
        Console.WriteLine(
            $"SCENE IDENTITY status={resolution.Status}; "
            + $"record={resolution.PlaybackRecordIndex?.ToString() ?? "none"}; "
            + $"scene={(scene == null ? "none" : $"{scene.NodeGuid}:{scene.SceneIndex}")}; "
            + $"selectedTrusted={resolution.SelectedSceneTrusted}; "
            + $"archiveFresh={resolution.ArchivePairFresh}; "
            + $"pairingTrusted={resolution.PairingTrusted}; "
            + $"candidates={string.Join(',', resolution.CandidatePlaybackRecordIndices)}; "
            + $"issues={string.Join(',', resolution.MappingIssues.Select(issue => issue.Code))}");
        return resolution.Status == ObservedSceneIdentityStatus.Mapped ? 0 : 1;
    }
}
