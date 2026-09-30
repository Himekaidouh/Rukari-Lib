using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Continuity;

public sealed class ContinuationEngine : IContinuationEngine
{
    public Result<ContinuationPlan> Build(
        ProjectSnapshot project,
        ContinuationDocument metadata)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(metadata);

        Result validation = ValidateDocument(project, metadata);
        if (!validation.Success)
        {
            return Result<ContinuationPlan>.Fail(validation.Error);
        }

        var issues = new List<ContinuationIssue>();
        if (!string.Equals(
                metadata.ProjectRevisionSha256,
                project.Source.RevisionSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new ContinuationIssue(
                ContinuationIssueSeverity.Information,
                "ProjectRevisionChanged",
                "The project revision changed; rules are accepted only when their scene fingerprints still match."));
        }

        SceneSnapshot[] allScenes = project.ScriptNodes
            .SelectMany(node => node.Scenes)
            .ToArray();

        Dictionary<(string NodeGuid, int SceneIndex), SceneSnapshot> scenesByLocation =
            allScenes
                .GroupBy(scene =>
                    (NormalizeGuid(scene.Key.NodeGuid), scene.Key.SceneIndex))
                .ToDictionary(group => group.Key, group => group.First());

        var activeRules = new HashSet<SceneKey>();
        foreach (ContinuationRule rule in metadata.Rules.Where(rule => rule.Enabled))
        {
            if (rule.Scene.SceneIndex <= 0)
            {
                issues.Add(new ContinuationIssue(
                    ContinuationIssueSeverity.Error,
                    "FirstSceneCannotContinue",
                    "Scene index 0 has no previous scene and cannot continue dialogue.",
                    rule.Scene));
                continue;
            }

            SceneKey normalizedRuleKey = Normalize(rule.Scene);
            SceneSnapshot? exactScene = allScenes.FirstOrDefault(
                scene => KeysEqual(scene.Key, normalizedRuleKey));
            if (exactScene != null)
            {
                if (!activeRules.Add(exactScene.Key))
                {
                    issues.Add(new ContinuationIssue(
                        ContinuationIssueSeverity.Information,
                        "DuplicateRule",
                        "A duplicate continuation rule was ignored.",
                        rule.Scene));
                }

                continue;
            }

            if (scenesByLocation.TryGetValue(
                    (NormalizeGuid(rule.Scene.NodeGuid), rule.Scene.SceneIndex),
                    out SceneSnapshot? sceneAtLocation))
            {
                issues.Add(new ContinuationIssue(
                    ContinuationIssueSeverity.Warning,
                    "StaleSceneFingerprint",
                    "The scene still exists at this index, but its content fingerprint changed. The rule was not activated.",
                    sceneAtLocation.Key));
            }
            else
            {
                issues.Add(new ContinuationIssue(
                    ContinuationIssueSeverity.Warning,
                    "MissingScene",
                    "The continuation rule no longer resolves to a scene. The rule was not activated.",
                    rule.Scene));
            }
        }

        var entries = new List<DialoguePlanEntry>();
        foreach (StoryNodeSnapshot node in project.ScriptNodes)
        {
            string visibleText = string.Empty;
            int chainStart = 0;

            foreach (SceneSnapshot scene in node.Scenes.OrderBy(scene => scene.Key.SceneIndex))
            {
                bool append = activeRules.Contains(scene.Key);
                if (append)
                {
                    string lockedPrefix = visibleText;
                    visibleText += scene.DialogueText;
                    entries.Add(new DialoguePlanEntry(
                        scene.Key,
                        DialogueOperation.Append,
                        lockedPrefix,
                        scene.DialogueText,
                        visibleText,
                        chainStart));
                }
                else
                {
                    visibleText = scene.DialogueText;
                    chainStart = scene.Key.SceneIndex;
                    entries.Add(new DialoguePlanEntry(
                        scene.Key,
                        DialogueOperation.Replace,
                        string.Empty,
                        scene.DialogueText,
                        visibleText,
                        chainStart));
                }
            }
        }

        return Result<ContinuationPlan>.Ok(new ContinuationPlan(
            project.Source,
            entries.AsReadOnly(),
            issues.AsReadOnly()));
    }

    private static Result ValidateDocument(
        ProjectSnapshot project,
        ContinuationDocument metadata)
    {
        if (metadata.SchemaVersion != ContinuationDocument.CurrentSchemaVersion)
        {
            return Result.Fail(
                $"Unsupported continuation schema version {metadata.SchemaVersion}; expected {ContinuationDocument.CurrentSchemaVersion}.");
        }

        if (string.IsNullOrWhiteSpace(metadata.ProjectId))
        {
            return Result.Fail("Continuation metadata has no project ID.");
        }

        if (metadata.Rules == null)
        {
            return Result.Fail("Continuation metadata has no rules collection.");
        }

        for (int index = 0; index < metadata.Rules.Count; index++)
        {
            ContinuationRule? rule = metadata.Rules[index];
            if (rule?.Scene == null
                || string.IsNullOrWhiteSpace(rule.Scene.NodeGuid)
                || string.IsNullOrWhiteSpace(rule.Scene.Fingerprint))
            {
                return Result.Fail($"Continuation rule {index} has an invalid scene key.");
            }
        }

        if (!string.Equals(
                metadata.ProjectPathKey,
                project.Source.PathKey,
                StringComparison.OrdinalIgnoreCase))
        {
            return Result.Fail("Continuation metadata belongs to a different project path key.");
        }

        return Result.Ok();
    }

    private static SceneKey Normalize(SceneKey key) => new(
        NormalizeGuid(key.NodeGuid),
        key.SceneIndex,
        key.Fingerprint.ToUpperInvariant());

    private static bool KeysEqual(SceneKey left, SceneKey right) =>
        left.SceneIndex == right.SceneIndex
        && string.Equals(NormalizeGuid(left.NodeGuid), NormalizeGuid(right.NodeGuid), StringComparison.Ordinal)
        && string.Equals(left.Fingerprint, right.Fingerprint, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeGuid(string value) => value.Trim().ToLowerInvariant();
}

public sealed class ContinuationReconciler : IContinuationReconciler
{
    public Result<ContinuationRebaseResult> Rebase(
        ContinuationDocument metadata,
        ProjectSnapshot currentProject)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(currentProject);

        if (metadata.SchemaVersion != ContinuationDocument.CurrentSchemaVersion)
        {
            return Result<ContinuationRebaseResult>.Fail(
                $"Unsupported continuation schema version {metadata.SchemaVersion}.");
        }

        if (metadata.Rules == null)
        {
            return Result<ContinuationRebaseResult>.Fail(
                "Continuation metadata has no rules collection.");
        }

        for (int index = 0; index < metadata.Rules.Count; index++)
        {
            ContinuationRule? rule = metadata.Rules[index];
            if (rule?.Scene == null
                || string.IsNullOrWhiteSpace(rule.Scene.NodeGuid)
                || string.IsNullOrWhiteSpace(rule.Scene.Fingerprint))
            {
                return Result<ContinuationRebaseResult>.Fail(
                    $"Continuation rule {index} has an invalid scene key.");
            }
        }

        if (!string.Equals(
                metadata.ProjectPathKey,
                currentProject.Source.PathKey,
                StringComparison.OrdinalIgnoreCase))
        {
            return Result<ContinuationRebaseResult>.Fail(
                "Continuation metadata belongs to a different project path key.");
        }

        SceneSnapshot[] scenes = currentProject.ScriptNodes
            .SelectMany(node => node.Scenes)
            .ToArray();

        var issues = new List<ContinuationIssue>();
        var rebased = new Dictionary<(string NodeGuid, int SceneIndex), ContinuationRule>();

        foreach (ContinuationRule rule in metadata.Rules.Where(rule => rule.Enabled))
        {
            SceneSnapshot? exact = scenes.FirstOrDefault(scene => KeysEqual(scene.Key, rule.Scene));
            SceneSnapshot? resolved = exact;

            if (resolved == null)
            {
                SceneSnapshot[] fingerprintMatches = scenes
                    .Where(scene =>
                        string.Equals(scene.Key.NodeGuid, rule.Scene.NodeGuid, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(scene.Key.Fingerprint, rule.Scene.Fingerprint, StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                if (fingerprintMatches.Length == 1)
                {
                    resolved = fingerprintMatches[0];
                    issues.Add(new ContinuationIssue(
                        ContinuationIssueSeverity.Information,
                        "RuleMoved",
                        $"Continuation rule moved from scene {rule.Scene.SceneIndex} to {resolved.Key.SceneIndex}.",
                        resolved.Key));
                }
                else if (fingerprintMatches.Length > 1)
                {
                    issues.Add(new ContinuationIssue(
                        ContinuationIssueSeverity.Warning,
                        "AmbiguousFingerprint",
                        "Multiple scenes have the same fingerprint; the rule was not migrated automatically.",
                        rule.Scene));
                    continue;
                }
                else
                {
                    issues.Add(new ContinuationIssue(
                        ContinuationIssueSeverity.Warning,
                        "SceneRemovedOrChanged",
                        "No scene has the stored fingerprint; the rule was not migrated.",
                        rule.Scene));
                    continue;
                }
            }

            if (resolved.Key.SceneIndex <= 0)
            {
                issues.Add(new ContinuationIssue(
                    ContinuationIssueSeverity.Warning,
                    "MovedToFirstScene",
                    "The matched scene is now first in its node and can no longer continue dialogue.",
                    resolved.Key));
                continue;
            }

            var location = (resolved.Key.NodeGuid.ToLowerInvariant(), resolved.Key.SceneIndex);
            rebased[location] = new ContinuationRule(resolved.Key);
        }

        ContinuationRule[] normalizedRules = rebased.Values
            .OrderBy(rule => rule.Scene.NodeGuid, StringComparer.OrdinalIgnoreCase)
            .ThenBy(rule => rule.Scene.SceneIndex)
            .ToArray();

        var document = metadata with
        {
            ProjectRevisionSha256 = currentProject.Source.RevisionSha256,
            Rules = Array.AsReadOnly(normalizedRules)
        };

        return Result<ContinuationRebaseResult>.Ok(new ContinuationRebaseResult(
            document,
            issues.AsReadOnly()));
    }

    private static bool KeysEqual(SceneKey left, SceneKey right) =>
        left.SceneIndex == right.SceneIndex
        && string.Equals(left.NodeGuid, right.NodeGuid, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Fingerprint, right.Fingerprint, StringComparison.OrdinalIgnoreCase);
}
