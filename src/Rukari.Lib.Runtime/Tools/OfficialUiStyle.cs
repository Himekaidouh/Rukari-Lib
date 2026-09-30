extern alias unitycore;

using System.Text;
using Studio.Scripts.Window;
using Color = unitycore::UnityEngine.Color;

namespace Rukari.Lib.Runtime.Tools;

/// <summary>
/// The editor's own style reference. AA publishes the command-format panel's whole look as public static
/// fields — window and row geometry, and every colour it draws with — so our own drawing can use the game's
/// values instead of guessing at them. Reading only: nothing is written, and a failure leaves our own palette
/// in place.
/// </summary>
internal static class OfficialUiStyle
{
    /// <summary>Logs the official values once so they can be reused as our theme and as a style sheet.</summary>
    internal static void Log(Action<string> log)
    {
        try
        {
            var text = new StringBuilder("Editor style reference (official static values)");
            text.Append("; window=").Append(AdditionalPromptCommandHelp.WindowWidth)
                .Append('x').Append(AdditionalPromptCommandHelp.WindowHeight);
            text.Append("; view=").Append(AdditionalPromptCommandHelp.ViewWidth)
                .Append('x').Append(AdditionalPromptCommandHelp.ViewHeight);
            text.Append("; content=").Append(AdditionalPromptCommandHelp.ContentWidth);
            text.Append("; rowHeight=").Append(AdditionalPromptCommandHelp.CommandRowHeight);
            text.Append("; buttonObject=").Append(AdditionalPromptCommandHelp.ButtonObjectName);
            text.Append("; windowObject=").Append(AdditionalPromptCommandHelp.WindowObjectName);
            text.Append("; colors:");
            Append(text, "button", AdditionalPromptCommandHelp.ButtonColor);
            Append(text, "buttonHover", AdditionalPromptCommandHelp.ButtonHoverColor);
            Append(text, "buttonPressed", AdditionalPromptCommandHelp.ButtonPressedColor);
            Append(text, "popupBackground", AdditionalPromptCommandHelp.PopupBackgroundColor);
            Append(text, "popupMask", AdditionalPromptCommandHelp.PopupMaskColor);
            Append(text, "titleUnderline", AdditionalPromptCommandHelp.TitleUnderlineColor);
            Append(text, "heading", AdditionalPromptCommandHelp.HeadingColor);
            Append(text, "body", AdditionalPromptCommandHelp.BodyColor);
            Append(text, "mutedBody", AdditionalPromptCommandHelp.MutedBodyColor);
            Append(text, "commandRow", AdditionalPromptCommandHelp.CommandRowColor);
            Append(text, "commandRowHover", AdditionalPromptCommandHelp.CommandRowHoverColor);
            Append(text, "commandRowSelected", AdditionalPromptCommandHelp.CommandRowSelectedColor);
            Append(text, "scrollTrack", AdditionalPromptCommandHelp.ScrollTrackColor);
            Append(text, "scrollThumb", AdditionalPromptCommandHelp.ScrollThumbColor);
            log(text.ToString());
        }
        catch (Exception ex)
        {
            log("Editor style reference unavailable: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static void Append(StringBuilder text, string name, Color color) =>
        text.Append(' ').Append(name).Append('=').Append(Hex(color));

    private static string Hex(Color color) =>
        $"#{Byte(color.r):X2}{Byte(color.g):X2}{Byte(color.b):X2}{Byte(color.a):X2}";

    private static int Byte(float value) => (int)Math.Round(Math.Clamp(value, 0f, 1f) * 255f);
}
