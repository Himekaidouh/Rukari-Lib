using System.Globalization;

namespace AzureArchive.VideoTools.Core.Commands;

/// <summary>
/// One layer of one dialogue row: the resource a single tool surface owns, the name the author reads, and the
/// surface that owns it (2026-10-13).
///
/// <para>
/// The key is a <em>resource</em> key — the string <see cref="CommandResourceIdentity.KeyFor(EmbeddedAavtCommand)"/>
/// gives the directive — and not a track name. Those are two different vocabularies and the difference matters here:
/// the queue planner merges both camera scopes onto its single <c>camera</c> track, while the document keeps
/// <c>camera</c> and <c>camera:background</c> as two resources with two writers. A layer is a unit of ownership, so
/// the document's key is the one that decides it.
/// </para>
/// </summary>
public sealed record EffectLayer(string ResourceKey, string Label, string Window);

/// <summary>
/// One directive together with the layer it belongs to: what a window holds while it is stacking effects that are not
/// written yet (2026-10-13). The pair is the whole of what a layer summary needs, and keeping it paired is what stops
/// a caller from counting another layer's directive into its own.
/// </summary>
public readonly record struct EffectLayerDirective(string ResourceKey, string CanonicalDirective);

/// <summary>
/// The layers this mod's surfaces write, and who writes each. One resource, one writer: a layer with two owners is
/// exactly how effects used to overwrite each other, so the table is stated once, here, instead of being implied by
/// whichever page happens to be open.
/// </summary>
public static class EffectLayerCatalog
{
    /// <summary>The surface that owns both camera scopes.</summary>
    public const string CameraWindow = "镜头效果";

    /// <summary>The surface that owns one character's presets.</summary>
    public const string PresetWindow = "预设效果";

    /// <summary>The page that owns one character's transform.</summary>
    public const string PictureWindow = "更多的画面效果";

    /// <summary>The whole-picture camera. Singleton: a row has at most one, whatever its scope.</summary>
    public static EffectLayer Camera { get; } = new(CommandResourceIdentity.CameraKey, "镜头", CameraWindow);

    /// <summary>The background camera: a separate resource with a separate writer, not a property of the camera.</summary>
    public static EffectLayer BackgroundCamera { get; } =
        new(CommandResourceIdentity.BackgroundCameraKey, "背景镜头", CameraWindow);

    /// <summary>One character's transform layer.</summary>
    public static EffectLayer Character(int publicSlot) =>
        new("character:" + Number(publicSlot), "角色 " + Number(publicSlot), PictureWindow);

    /// <summary>One character's transient preset layer.</summary>
    public static EffectLayer Preset(int publicSlot) =>
        new("preset:" + Number(publicSlot), "预设 " + Number(publicSlot), PresetWindow);

    /// <summary>
    /// The layer a resource key names, or null when this build does not know that layer (2026-10-13).
    ///
    /// <para>
    /// It exists so that a draft can decide, once, which layer it belongs to and whether anything here can write it:
    /// the camera scopes, one character's transform, one character's presets and one spine track are the layers that
    /// have names, and a key outside them is refused rather than filed under a guess.
    /// </para>
    /// </summary>
    public static EffectLayer? ForResourceKey(string? resourceKey)
    {
        if (string.IsNullOrEmpty(resourceKey)) return null;
        if (string.Equals(resourceKey, Camera.ResourceKey, StringComparison.Ordinal)) return Camera;
        if (string.Equals(resourceKey, BackgroundCamera.ResourceKey, StringComparison.Ordinal)) return BackgroundCamera;

        int separator = resourceKey.IndexOf(':');
        if (separator <= 0 || separator + 1 >= resourceKey.Length) return null;
        string prefix = resourceKey[..separator];
        string slot = resourceKey[(separator + 1)..];
        // A spine layer carries a second number for its track; every prefix keeps the first one as its slot.
        int track = 1;
        int secondSeparator = slot.IndexOf(':');
        if (secondSeparator >= 0)
        {
            track = int.TryParse(slot[(secondSeparator + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int read)
                ? read
                : 0;
            slot = slot[..secondSeparator];
        }

        if (!int.TryParse(slot, NumberStyles.Integer, CultureInfo.InvariantCulture, out int publicSlot)) return null;
        return prefix switch
        {
            "character" => Character(publicSlot),
            "preset" => Preset(publicSlot),
            "spine" => new EffectLayer(
                resourceKey,
                "Spine " + slot + " 轨 " + track.ToString(CultureInfo.InvariantCulture),
                "Spine 叠加"),
            _ => null
        };
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
