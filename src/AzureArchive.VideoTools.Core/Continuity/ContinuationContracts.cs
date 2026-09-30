using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Continuity;

public enum DialogueOperation
{
    Replace = 0,
    Append = 1
}

public enum ContinuationIssueSeverity
{
    Information = 0,
    Warning = 1,
    Error = 2
}

public sealed record ContinuationRule(SceneKey Scene, bool Enabled = true);

public sealed record ContinuationDocument(
    int SchemaVersion,
    string ProjectId,
    string ProjectPathKey,
    string ProjectRevisionSha256,
    IReadOnlyList<ContinuationRule> Rules)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record ContinuationIssue(
    ContinuationIssueSeverity Severity,
    string Code,
    string Message,
    SceneKey? Scene = null);

public sealed record DialoguePlanEntry(
    SceneKey Scene,
    DialogueOperation Operation,
    string LockedPrefix,
    string Suffix,
    string VisibleText,
    int ChainStartSceneIndex)
{
    public int TypewriterStartCharacterIndex =>
        Operation == DialogueOperation.Append ? LockedPrefix.Length : 0;
}

public sealed record ContinuationPlan(
    ProjectSourceSnapshot Project,
    IReadOnlyList<DialoguePlanEntry> Entries,
    IReadOnlyList<ContinuationIssue> Issues);

public sealed record ContinuationRebaseResult(
    ContinuationDocument Document,
    IReadOnlyList<ContinuationIssue> Issues);

public enum ContinuationLoadSource
{
    None = 0,
    Primary = 1,
    Backup = 2
}

public sealed record ContinuationStoreNotice(
    string Code,
    string Message);

public sealed record ContinuationLoadSnapshot(
    ContinuationDocument? Document,
    ContinuationLoadSource Source,
    IReadOnlyList<ContinuationStoreNotice> Notices)
{
    public bool RecoveredFromBackup => Source == ContinuationLoadSource.Backup;
}

public sealed record ContinuationSaveSnapshot(
    string FullPath,
    bool ReplacedExisting,
    bool BackupCreated);

public interface IContinuationEngine
{
    Result<ContinuationPlan> Build(
        ProjectSnapshot project,
        ContinuationDocument metadata);
}

public interface IContinuationReconciler
{
    Result<ContinuationRebaseResult> Rebase(
        ContinuationDocument metadata,
        ProjectSnapshot currentProject);
}

public interface IContinuationStore
{
    Result<ContinuationLoadSnapshot> TryLoad(string sidecarPath);

    Result<ContinuationSaveSnapshot> SaveAtomic(
        string sidecarPath,
        ContinuationDocument document);
}
