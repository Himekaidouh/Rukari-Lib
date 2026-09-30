using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Playback;

public interface IPlaybackArchiveReader
{
    Result<PlaybackArchiveSnapshot> Read(string path);
}

public sealed record PlaybackArchiveSourceSnapshot(
    string FullPath,
    string PathKey,
    string RevisionSha256,
    long Length,
    DateTimeOffset LastWriteTimeUtc);

public sealed record PlaybackRecordSnapshot(
    int RecordIndex,
    long GroupId,
    long SelectionGroup,
    long BgmId,
    string Sound,
    uint Transition,
    uint BackgroundName,
    uint BackgroundEffect,
    string PopupFileName,
    string CompiledScript,
    string TextJp,
    string TextTh,
    string TextTw,
    string TextCn,
    string TextEn,
    string VoiceJp,
    bool TeenMode,
    string Fingerprint);

public sealed record PlaybackArchiveSnapshot(
    PlaybackArchiveSourceSnapshot Source,
    string SchemaName,
    IReadOnlyList<PlaybackRecordSnapshot> Records);
