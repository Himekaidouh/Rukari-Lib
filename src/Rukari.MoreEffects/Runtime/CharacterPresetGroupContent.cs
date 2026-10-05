using AzureArchive.VideoTools.Core.Characters;
using Rukari.Lib.Tools;

namespace AzureArchive.VideoTools.Runtime;

/// <summary>A single registered preset page with an internal chooser and a parameter view.</summary>
internal sealed class CharacterPresetGroupContent : IToolPanelContent, IToolPanelSizing, IToolPanelLifecycle
{
    private const float NavigationHeight = 50f;
    private readonly CharacterPresetEditorSession _session = new();
    private CharacterPresetToolContent? _details;
    private CharacterPresetKind _selectedKind;

    public float PreferredWidth => 500f;
    public float PreferredHeight => _details == null ? 560f : _details.PreferredHeight + NavigationHeight;

    public void OnShown() => ReturnToChoices();
    public void OnHidden() => ReturnToChoices();

    public void Draw(IToolPanelSurface surface)
    {
        CharacterPresetToolContent? details = _details;
        if (details != null)
        {
            ToolInputRect back = new(0, surface.Height - 40f, 208f, 40f);
            if (surface.Button("preset.group.back", "返回预设效果", back))
            {
                ReturnToChoices();
                return;
            }
            surface.Text("preset.group.current", CharacterPresetEditorSession.Title(_selectedKind, _session.SpinAxis),
                new ToolInputRect(220f, back.Y, Math.Max(1f, surface.Width - 220f), back.Height), 20);
            details.Draw(surface, NavigationHeight);
            return;
        }

        float top = surface.Height;
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
