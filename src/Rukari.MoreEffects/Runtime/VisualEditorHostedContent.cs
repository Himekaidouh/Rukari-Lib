using Rukari.Lib.Tools;

namespace AzureArchive.VideoTools.Runtime;

/// <summary>
/// The shared toolbox page of this mod. The page itself owns nothing: it asks the panel for a sheet the size of
/// the editor's own content rectangle and hands the frame, the title, the close button and every input region to
/// the library, while <see cref="VisualEditorBehaviour"/> keeps drawing the editor's controls on its own canvas.
///
/// <para>
/// Why the request is padded: the editor's coordinates are authored against a 720 x max(900, storyH-160) story
/// rectangle. The renderer takes <c>Padding</c> on each side and a header row off the top before it hands the
/// content rectangle over, so the page asks for its rectangle PLUS that chrome. The content rectangle then comes
/// back exactly the size the editor already lays out in, which is what lets a migration that replaces the whole
/// panel frame leave every internal coordinate alone.
/// </para>
///
/// <para>
/// When the editor cannot host itself yet — the user is not in the editing preview, or the feature is disabled —
/// the page is a short explanatory sheet instead of an empty one. That state can flip at any time, so the page
/// reads it every frame and the panel resizes with it.
/// </para>
/// </summary>
internal sealed class VisualEditorHostedContent : IToolPanelContent, IToolPanelSizing, IToolPanelLifecycle
{
    private const float HintHeight = 240f;

    /// <summary>Whether the editor can take the panel over right now.</summary>
    private static bool CanHost => VisualEditorBehaviour.CanOpenFromToolbox;

    /// <inheritdoc/>
    public float PreferredHeight => CanHost ? VisualEditorBehaviour.HostedPreferredHeight : HintHeight;

    /// <inheritdoc/>
    public float PreferredWidth => CanHost ? VisualEditorBehaviour.HostedPreferredWidth : 0f;

    /// <inheritdoc/>
    public void OnShown() => VisualEditorBehaviour.HostedShown();

    /// <inheritdoc/>
    public void OnHidden() => VisualEditorBehaviour.HostedHidden();

    /// <inheritdoc/>
    public void Draw(IToolPanelSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (CanHost)
        {
            VisualEditorBehaviour.HostedDraw(surface);
            return;
        }

        DrawHint(surface);
    }

    /// <summary>
    /// The sheet shown while the page cannot become the editor. Written with the shared primitives on purpose:
    /// it is the one part of this page the library draws, and it is what the user sees instead of a blank panel.
    /// </summary>
    private static void DrawHint(IToolPanelSurface surface)
    {
        surface.Text("hint-title", "画面编辑器将在编辑预览中接管这一页。", surface.Row(30f), 19);
        surface.Space(6f);
        surface.Status("hint-status", VisualEditorBehaviour.HostedUnavailableReason(), surface.Row(26f));
        surface.Space(6f);
        surface.Text("hint-steps",
            "1. 在右侧 Script 节点上进入编辑预览。\n2. 再点一次本模块按钮，这一页就会变成编辑器。",
            surface.Row(64f), 17);
    }
}
