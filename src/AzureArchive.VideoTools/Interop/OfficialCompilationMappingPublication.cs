using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Mapping;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Formats.Sidecar;
using AzureArchive.VideoTools.Runtime;

namespace AzureArchive.VideoTools.Interop;

internal static class OfficialCompilationMappingPublication
{
    internal static void Install() => OfficialCompilationMappingCapture.Install(Accept);

    private static void Accept(OfficialCompilationMappingCaptureReceipt receipt)
    {
        // The adapter hands off only copied values. Disk verification and our sidecar write do
        // not touch Unity wrappers and may finish away from the game's compilation frame.
        _ = Task.Run(() => Publish(receipt));
    }

    private static void Publish(OfficialCompilationMappingCaptureReceipt receipt)
    {
        try
        {
            var savedScenes = receipt.Project.ScriptNodes.SelectMany(node => node.Scenes)
                .ToDictionary(scene => $"{scene.Key.NodeGuid}:{scene.Key.SceneIndex}", StringComparer.OrdinalIgnoreCase);
            var entries = new List<CompilationMappingEntry>();
            foreach (OfficialCompiledSceneRecord captured in receipt.Records)
            {
                if (!savedScenes.TryGetValue($"{captured.Scene.NodeGuid}:{captured.Scene.SceneIndex}", out SceneSnapshot? scene)
                    || scene.Key != captured.Scene || scene.AdditionalPrompt != captured.SourceAdditionalPrompt
                    || captured.PlaybackRecordIndex < 0 || captured.PlaybackRecordIndex >= receipt.Playback.Records.Count)
                    throw new InvalidDataException("copied export scene differs from the saved source");
                var record = receipt.Playback.Records[captured.PlaybackRecordIndex];
                entries.Add(new(captured.Scene, captured.PlaybackRecordIndex,
                    record.Fingerprint, CommandIdentity.CompiledScript(record.CompiledScript)));
            }

            var evidence = new CompilationMappingEvidence(CompilationMappingEvidence.CurrentSchemaVersion,
                receipt.Project.Source.RevisionSha256, receipt.Project.Source.Length,
                receipt.Playback.Source.RevisionSha256, receipt.Playback.Source.Length, entries.AsReadOnly());
            var result = new JsonCompilationMappingEvidenceStore().Save(evidence, receipt.Project, receipt.Playback);
            if (!result.Success)
            {
                Plugin.Logger.LogWarning("compile-mapping-publication: proof rejected; " + result.Error);
                return;
            }
            ActiveProjectPairSource.InvalidateArchiveSnapshots();
            PlayerCommandObservationRuntime.RequestIndexReload();
            Plugin.Logger.LogInfo($"compile-mapping-publication: saved; scenes={entries.Count}; "
                + $"projectRevision={evidence.ProjectRevisionSha256}; playbackRevision={evidence.PlaybackRevisionSha256}");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning("compile-mapping-publication: proof omitted; " + ex.GetType().Name + ": " + ex.Message);
        }
    }
}
