using AzureArchive.VideoTools.Core.Continuity;
using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Formats.Aap;

namespace AzureArchive.VideoTools.Tests;

internal static class ContinuationEngineTests
{
    public static void BuildsAppendChainAndResetsAtOrdinaryScene()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot project = ReadProject(files, "chain.aap", "A", "B", "C", "D");
        SceneSnapshot[] scenes = project.ScriptNodes.Single().Scenes.ToArray();
        ContinuationDocument metadata = DocumentFor(project, scenes[1].Key, scenes[2].Key);

        var result = new ContinuationEngine().Build(project, metadata);
        AssertEx.True(result.Success, result.Error);
        ContinuationPlan plan = AssertEx.NotNull(result.Value);
        AssertEx.Equal(4, plan.Entries.Count);

        AssertEntry(plan.Entries[0], DialogueOperation.Replace, "", "A", "A", 0, 0);
        AssertEntry(plan.Entries[1], DialogueOperation.Append, "A", "B", "AB", 0, 1);
        AssertEntry(plan.Entries[2], DialogueOperation.Append, "AB", "C", "ABC", 0, 2);
        AssertEntry(plan.Entries[3], DialogueOperation.Replace, "", "D", "D", 3, 0);

        DialogueCommand command = DialogueCommand.FromPlan(plan.Entries[2]);
        AssertEx.Equal(DialogueOperation.Append, command.Operation);
        AssertEx.Equal("AB", command.ExpectedLockedPrefix);
        AssertEx.Equal("C", command.Suffix);
        AssertEx.Equal("ABC", command.ResultText);
        AssertEx.Equal(2, command.TypewriterStartCharacterIndex);
    }

    public static void StaleFingerprintDoesNotActivateRule()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot project = ReadProject(files, "stale.aap", "A", "B");
        SceneSnapshot scene = project.ScriptNodes.Single().Scenes[1];
        SceneKey staleKey = scene.Key with { Fingerprint = new string('0', 64) };
        ContinuationDocument metadata = DocumentFor(project, staleKey);

        ContinuationPlan plan = AssertEx.NotNull(
            new ContinuationEngine().Build(project, metadata).Value);
        AssertEx.Equal(DialogueOperation.Replace, plan.Entries[1].Operation);
        AssertEx.Contains(
            plan.Issues,
            issue => issue.Code == "StaleSceneFingerprint",
            "Expected a stale fingerprint issue.");
    }

    public static void FirstSceneRuleIsRejected()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot project = ReadProject(files, "first.aap", "A", "B");
        SceneSnapshot scene = project.ScriptNodes.Single().Scenes[0];

        ContinuationPlan plan = AssertEx.NotNull(
            new ContinuationEngine().Build(project, DocumentFor(project, scene.Key)).Value);
        AssertEx.Equal(DialogueOperation.Replace, plan.Entries[0].Operation);
        AssertEx.Contains(
            plan.Issues,
            issue => issue.Code == "FirstSceneCannotContinue",
            "Expected first-scene validation issue.");
    }

    public static void DifferentProjectPathKeyFailsClosed()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot project = ReadProject(files, "project.aap", "A", "B");
        SceneSnapshot scene = project.ScriptNodes.Single().Scenes[1];
        ContinuationDocument metadata = DocumentFor(project, scene.Key) with
        {
            ProjectPathKey = new string('F', 64)
        };

        var result = new ContinuationEngine().Build(project, metadata);
        AssertEx.False(result.Success);
        AssertEx.True(result.Error.Contains("different project", StringComparison.OrdinalIgnoreCase));
    }

    public static void ChangedRevisionWithMatchingFingerprintRemainsActive()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot project = ReadProject(files, "revision.aap", "A", "B");
        SceneSnapshot scene = project.ScriptNodes.Single().Scenes[1];
        ContinuationDocument metadata = DocumentFor(project, scene.Key) with
        {
            ProjectRevisionSha256 = new string('0', 64)
        };

        var result = new ContinuationEngine().Build(project, metadata);
        AssertEx.True(result.Success, result.Error);
        ContinuationPlan plan = AssertEx.NotNull(result.Value);
        AssertEx.Equal(DialogueOperation.Append, plan.Entries[1].Operation);
        AssertEx.Contains(
            plan.Issues,
            issue => issue.Code == "ProjectRevisionChanged",
            "Expected a changed-revision diagnostic.");
    }

    public static void MalformedRuleCollectionFailsWithoutThrowing()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot project = ReadProject(files, "malformed-rules.aap", "A", "B");
        ContinuationDocument metadata = DocumentFor(
            project,
            project.ScriptNodes.Single().Scenes[1].Key) with
        {
            Rules = null!
        };

        var build = new ContinuationEngine().Build(project, metadata);
        var rebase = new ContinuationReconciler().Rebase(metadata, project);
        AssertEx.False(build.Success);
        AssertEx.False(rebase.Success);
        AssertEx.True(build.Error.Contains("rules", StringComparison.OrdinalIgnoreCase));
        AssertEx.True(rebase.Error.Contains("rules", StringComparison.OrdinalIgnoreCase));
    }

    public static void ReconcilerMovesRuleAfterInsertion()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot original = ReadProject(files, "original.aap", "A", "B", "C");
        SceneKey originalRule = original.ScriptNodes.Single().Scenes[2].Key;
        ContinuationDocument metadata = DocumentFor(original, originalRule);

        ProjectSnapshot changed = ReadProject(files, "changed.aap", "A", "Inserted", "B", "C");
        changed = changed with
        {
            Source = changed.Source with { PathKey = original.Source.PathKey }
        };

        var result = new ContinuationReconciler().Rebase(metadata, changed);
        AssertEx.True(result.Success, result.Error);
        ContinuationRebaseResult rebased = AssertEx.NotNull(result.Value);
        ContinuationRule moved = rebased.Document.Rules.Single();
        AssertEx.Equal(3, moved.Scene.SceneIndex);
        AssertEx.Equal(originalRule.Fingerprint, moved.Scene.Fingerprint);
        AssertEx.Equal(changed.Source.RevisionSha256, rebased.Document.ProjectRevisionSha256);
        AssertEx.Contains(
            rebased.Issues,
            issue => issue.Code == "RuleMoved",
            "Expected a rule-moved diagnostic.");
    }

    public static void ReconcilerRefusesAmbiguousFingerprint()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot original = ReadProject(files, "original-ambiguous.aap", "A", "B");
        SceneKey originalRule = original.ScriptNodes.Single().Scenes[1].Key;
        ContinuationDocument metadata = DocumentFor(original, originalRule);

        ProjectSnapshot changed = ReadProject(files, "changed-ambiguous.aap", "A", "X", "B", "B");
        changed = changed with
        {
            Source = changed.Source with { PathKey = original.Source.PathKey }
        };

        ContinuationRebaseResult rebased = AssertEx.NotNull(
            new ContinuationReconciler().Rebase(metadata, changed).Value);
        AssertEx.Equal(0, rebased.Document.Rules.Count);
        AssertEx.Contains(
            rebased.Issues,
            issue => issue.Code == "AmbiguousFingerprint",
            "Expected an ambiguous fingerprint diagnostic.");
    }

    private static ProjectSnapshot ReadProject(
        TemporaryAapDirectory files,
        string fileName,
        params string[] dialogue)
    {
        string path = files.Write(
            fileName,
            SyntheticAap.Project(dialogue.Select(text => SyntheticAap.Scene(text)).ToArray()));
        var result = new AapProjectReader().Read(path);
        AssertEx.True(result.Success, result.Error);
        return AssertEx.NotNull(result.Value);
    }

    private static ContinuationDocument DocumentFor(
        ProjectSnapshot project,
        params SceneKey[] rules) => new(
        ContinuationDocument.CurrentSchemaVersion,
        Guid.NewGuid().ToString("D"),
        project.Source.PathKey,
        project.Source.RevisionSha256,
        Array.AsReadOnly(rules.Select(scene => new ContinuationRule(scene)).ToArray()));

    private static void AssertEntry(
        DialoguePlanEntry entry,
        DialogueOperation operation,
        string lockedPrefix,
        string suffix,
        string visibleText,
        int chainStart,
        int typewriterStart)
    {
        AssertEx.Equal(operation, entry.Operation);
        AssertEx.Equal(lockedPrefix, entry.LockedPrefix);
        AssertEx.Equal(suffix, entry.Suffix);
        AssertEx.Equal(visibleText, entry.VisibleText);
        AssertEx.Equal(chainStart, entry.ChainStartSceneIndex);
        AssertEx.Equal(typewriterStart, entry.TypewriterStartCharacterIndex);
    }
}
