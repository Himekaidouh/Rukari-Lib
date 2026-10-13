namespace AzureArchive.VideoTools.Core.Commands;

/// <summary>
/// Which queue the layer surfaces write when an author presses 应用 (2026-10-13).
///
/// <para>
/// It is one switch for the whole editor rather than one per window, because the thing it decides is a property of the
/// row: whether the row plays as one sequence (人物-人物-镜头, each waiting for the one before it) or as independent
/// tracks that all start together. A window that wrote its own answer would leave the row half in each mode, and the
/// layer it does not own would keep the offsets of the other one.
/// </para>
///
/// <para>
/// The mode is not stored in the document. What it produces — the <c>at=</c> offsets at the joints — is, and that is
/// the part playback reads; this flag only says what the next 应用 should do. It belongs to the editing session, so it
/// starts each session at its default, and the two modes are one press apart.
/// </para>
/// </summary>
public static class EffectQueueMode
{
    private static bool _serial = true;

    /// <summary>
    /// Whether 应用 reads the whole row as one queue. On by default: the row reads as the order its effects are
    /// stacked in, and an author who wants the layers to run side by side says so once.
    /// </summary>
    public static bool Serial
    {
        get => _serial;
        set => _serial = value;
    }

    /// <summary>Flips the mode and says what it became, for a button that has one label to update.</summary>
    public static bool Toggle()
    {
        _serial = !_serial;
        return _serial;
    }

    /// <summary>What the switch reads. The label is the mode, not the action: it says what the next 应用 will write.</summary>
    public static string Label => _serial ? "队列：串成一条" : "队列：各层并行";

    /// <summary>One sentence for a status line or a log, saying what the mode means for the row.</summary>
    public static string Describe => _serial
        ? "整行按层顺序排成一条队列：后一段等前一段结束（写 at= 偏移）。"
        : "各层各自成轨，同时开始，互不等待。";
}
