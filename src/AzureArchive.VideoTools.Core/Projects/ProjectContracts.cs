using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Projects;

public interface IProjectReader
{
    Result<ProjectSnapshot> Read(string path);
}

public enum StoryNodeKind
{
    Unknown = 0,
    Entry = 1,
    Script = 2,
    Selection = 3,
    Exit = 4
}

public enum ProjectDiagnosticSeverity
{
    Information = 0,
    Warning = 1,
    Error = 2
}

public sealed record ProjectDiagnostic(
    ProjectDiagnosticSeverity Severity,
    string Code,
    string Message,
    string? NodeGuid = null,
    int? SceneIndex = null);

public sealed record ProjectSourceSnapshot(
    string FullPath,
    string PathKey,
    string RevisionSha256,
    long Length,
    DateTimeOffset LastWriteTimeUtc);

public sealed record ProjectPreviewSnapshot(
    long? BackgroundId,
    string Header,
    string Title);

public sealed record SceneKey(
    string NodeGuid,
    int SceneIndex,
    string Fingerprint);

public sealed record ProjectCharacterSnapshot(
    int PhysicalSlot,
    string Identifier,
    string FaceId,
    int StartingPosition,
    int EndingPosition,
    int Emoticon,
    int Action,
    int Effect,
    int Appear,
    int ShapeOverride)
{
    public bool HasOfficialPositionTransition =>
        StartingPosition != EndingPosition;
}

public sealed record SceneSnapshot(
    SceneKey Key,
    string SourceType,
    string DialogueText,
    bool IsDialogue,
    string PopupFileName,
    uint BackgroundEffect,
    uint BackgroundName,
    string AdditionalPrompt,
    string PlaceText,
    string BackgroundFriendlyName,
    string SoundReference,
    string VoiceReference,
    uint Transition,
    long BgmId,
    long SelectionGroup)
{
    public int SpeakerSlot { get; init; }

    public IReadOnlyList<int> HighlightedSlots { get; init; } =
        Array.Empty<int>();

    public IReadOnlyList<ProjectCharacterSnapshot> Characters { get; init; } =
        Array.Empty<ProjectCharacterSnapshot>();
}

public sealed record StoryNodeSnapshot(
    int SourceIndex,
    StoryNodeKind Kind,
    string SourceType,
    string NodeGuid,
    bool HasPersistentGuid,
    string Name,
    IReadOnlyList<string> ConnectionGuids,
    IReadOnlyList<SceneSnapshot> Scenes);

public sealed record ProjectSnapshot(
    ProjectSourceSnapshot Source,
    string SourceType,
    string ProjectName,
    ProjectPreviewSnapshot Preview,
    IReadOnlyList<StoryNodeSnapshot> Nodes,
    IReadOnlyList<ProjectDiagnostic> Diagnostics)
{
    public IEnumerable<StoryNodeSnapshot> ScriptNodes =>
        Nodes.Where(node => node.Kind == StoryNodeKind.Script);
}
