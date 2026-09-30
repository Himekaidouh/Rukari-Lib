using AzureArchive.VideoTools.Core.Continuity;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Playback;

public sealed record DialogueSurfaceSnapshot(
    bool HasDialoguePanel,
    bool IsTyping,
    string VisibleText);

public sealed record DialogueCommand(
    DialogueOperation Operation,
    string ExpectedLockedPrefix,
    string Suffix,
    string ResultText,
    int TypewriterStartCharacterIndex)
{
    public static DialogueCommand FromPlan(DialoguePlanEntry entry) => new(
        entry.Operation,
        entry.LockedPrefix,
        entry.Suffix,
        entry.VisibleText,
        entry.TypewriterStartCharacterIndex);
}

public interface IPlaybackDialoguePort
{
    Result<DialogueSurfaceSnapshot> Capture();

    Result Replace(string text);

    Result Append(string expectedLockedPrefix, string suffix);

    Result Clear();

    Result StartTypewriter(int startCharacterIndex);
}
