namespace AzureArchive.VideoTools.Core.Commands;

/// <summary>
/// How one dialogue row asked its effects to be laid out, as the row itself says it (2026-10-13).
///
/// <para>
/// The marker is a line in the row's own additional prompt — <c>#aavt;queue</c> — so the answer lives with the content
/// it describes: it survives saving, it can be typed by hand, and every window that writes a layer into that row is
/// writing into a row that already says how it plays. Nothing about it is stored in the editor.
/// </para>
///
/// <para>
/// A row without the marker keeps every layer on its own track, which is what rows did before the marker existed, so
/// adding the marker is the only way to change a row's layout and no existing content moves.
/// </para>
/// </summary>
public enum EffectQueueMode
{
    /// <summary>No marker: each layer keeps its own track and they all start together.</summary>
    IndependentTracks = 0,

    /// <summary>
    /// <c>#aavt;queue</c>: the whole row is one queue, and each effect starts where the one before it ended —
    /// 人物-人物-镜头, one after another, whatever layer each effect belongs to.
    /// </summary>
    OneQueue = 1,
}
