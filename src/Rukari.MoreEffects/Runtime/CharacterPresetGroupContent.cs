using AzureArchive.VideoTools.Core.Characters;
using AzureArchive.VideoTools.Core.Commands;
using Rukari.Lib.Tools;

namespace AzureArchive.VideoTools.Runtime;

/// <summary>A single registered preset page with an internal chooser and a parameter view.</summary>
internal sealed class CharacterPresetGroupContent : IToolPanelContent, IToolPanelSizing, IToolPanelLifecycle
{
    private const float NavigationHeight = 50f;

    /// <summary>
    /// The one line this window says about the layer it owns. The overlay model gives every window its own layer and
    /// one line to report it with: a layer is stacked inside its own window, in authored order, so what the author
    /// needs from the row is "how many of my effects are on it, and how long do they take" — not a timeline.
    /// </summary>
    private const float LayerStripHeight = 26f;

    private readonly CharacterPresetEditorSession _session = new();
    private readonly EffectLayerReader _layers = new(Plugin.Logger.LogInfo);
    private CharacterPresetToolContent? _details;
    private CharacterPresetKind _selectedKind;

    public float PreferredWidth => 500f;

    public float PreferredHeight =>
        (_details == null ? 560f : _details.PreferredHeight + NavigationHeight) + LayerStripHeight;

    public void OnShown()
    {
        // The row may have changed while the window was hidden, so the layer is re-read rather than re-shown.
        _layers.Read(Layer, force: true);
        ReturnToChoices();
    }

    public void OnHidden() => ReturnToChoices();

    public void Draw(IToolPanelSurface surface)
    {
        DrawLayerLine(surface);
        // Everything below the line is laid out against the remaining height, so the strip never overlaps a control.
        float top = Math.Max(0f, surface.Height - LayerStripHeight);
        CharacterPresetToolContent? details = _details;
        if (details != null)
        {
            ToolInputRect back = new(0, top - 40f, 208f, 40f);
            if (surface.Button("preset.group.back", "返回预设效果", back))
            {
                ReturnToChoices();
                return;
            }
            surface.Text("preset.group.current", CharacterPresetEditorSession.Title(_selectedKind, _session.SpinAxis),
                new ToolInputRect(220f, back.Y, Math.Max(1f, surface.Width - 220f), back.Height), 20);
            details.Draw(surface, LayerStripHeight + NavigationHeight);
            return;
        }

        surface.Text("preset.group.title", "预设效果", Band(surface, ref top, 30f), 20);
        surface.Status("preset.group.hint", "选择动作，再设置目标角色与参数。", Band(surface, ref top, 32f));
        top -= 12f;
        foreach (CharacterPresetKind kind in CharacterPresetEditorSession.EditableKinds)
        {
            string id = "preset.group.choose." + kind.ToString().ToLowerInvariant();
            if (surface.Button(id, CharacterPresetEditorSession.Title(kind), Band(surface, ref top, 48f)))
            {
                _selectedKind = kind;
                var next = new CharacterPresetToolContent(_session, kind);
                next.OnShown();
                _details = next;
                return;
            }
            surface.Status(id + ".description", CharacterPresetEditorSession.Description(kind),
                Band(surface, ref top, 28f));
            top -= 12f;
        }
    }

    /// <summary>The layer this window owns: the selected character's transient presets.</summary>
    private EffectLayer Layer => EffectLayerCatalog.Preset(_session.PublicSlot);

    private void DrawLayerLine(IToolPanelSurface surface)
    {
        EffectLayerSummary summary = _layers.Read(Layer);
        surface.Status("preset.layer.summary", summary.Line,
            new ToolInputRect(0f, Math.Max(0f, surface.Height - LayerStripHeight), surface.Width, LayerStripHeight));
    }

    private void ReturnToChoices()
    {
        CharacterPresetToolContent? details = _details;
        _details = null;
        // Paired even though the shared host only sees this group page: changing internal views
        // must abandon the old draft and its keyboard focus just like changing registered pages.
        details?.OnHidden();
    }

    private static ToolInputRect Band(IToolPanelSurface surface, ref float top, float height)
    {
        top -= height;
        return new ToolInputRect(0, top, surface.Width, height);
    }
}
