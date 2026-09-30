using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AzureArchive.VideoTools.Runtime;

internal static class PlayerCommandObservationLog
{
    private static readonly object Gate = new();
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static string _path = string.Empty;

    public static string Initialize(PlayerCommandObservationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        lock (Gate)
        {
            bool dispatchCanary =
                options.Stage is PlayerCommandObservationStage.DispatchCanary
                    or PlayerCommandObservationStage.DispatchReplayCanary;
            bool sceneDispatch =
                options.Stage == PlayerCommandObservationStage.SceneCommandDispatch;
            bool realDispatch = dispatchCanary || sceneDispatch;
            string directory = Path.Combine(
                BepInEx.Paths.ConfigPath,
                "AzureArchive.VideoTools",
                "diagnostics");
            Directory.CreateDirectory(directory);
            _path = Path.Combine(
                directory,
                $"player-command-observation-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllLines(
                _path,
                new[]
                {
                    $"{DateTimeOffset.Now:O} player-command-observation start",
                    $"pluginVersion={Plugin.Version}",
                    $"stage={options.Stage}",
                    $"mode={(sceneDispatch ? "validated-scene-window-dispatch" : dispatchCanary ? "guarded-dispatch-canary" : "observation-only")}",
                    $"dispatcherCalls={(sceneDispatch ? "maximum-one-batch-per-valid-window" : dispatchCanary ? $"maximum-{options.DispatchCanaryMaximumExecutions}-authorized-attempts" : "false")}",
                    $"transformWrites={(sceneDispatch ? "validated-sidecar-commands-only" : dispatchCanary ? $"maximum-{options.DispatchCanaryMaximumExecutions}-authorized-commands" : "false")}",
                    $"sceneReplayTransformPolicy={(sceneDispatch ? "restore-command-axes-to-captured-scene-baseline" : "disabled")}",
                    "officialFileWrites=false",
                    "harmonyCallbackArguments=zero",
                    $"playerContextReads={(options.Stage is PlayerCommandObservationStage.PlaybackContextProbe or PlayerCommandObservationStage.SceneCommandDispatch ? "deferred-managed-update-only" : "false")}",
                    "manualControlsPolicy=playback-only-fail-closed",
                    "commandIndexReload=Ctrl+F6-discard-old-index-and-deferred-batches",
                    $"sceneCommandDispatchEnabled={options.SceneCommandDispatchEnabled}",
                    $"dispatchCanaryEnabled={options.DispatchCanaryEnabled}",
                    $"dispatchCanaryRecord={options.DispatchCanaryRecordIndex}",
                    $"dispatchCanaryCommandId={options.DispatchCanaryCommandId}",
                    $"dispatchCanaryMaximumExecutions={options.DispatchCanaryMaximumExecutions}"
                },
                Utf8NoBom);
            return _path;
        }
    }

    public static void Append(params string[] lines) => Append(lines.AsEnumerable());

    public static void Append(IEnumerable<string> lines)
    {
        lock (Gate)
        {
            if (string.IsNullOrWhiteSpace(_path))
            {
                return;
            }

            File.AppendAllLines(_path, lines, Utf8NoBom);
        }
    }
}
