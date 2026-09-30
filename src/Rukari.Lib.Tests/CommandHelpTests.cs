using Rukari.Lib.Commands;

namespace Rukari.Lib.Tests;

/// <summary>
/// The editor's command-format help is built from a JSON array that may be cached between builds, so what a mod
/// publishes has to be idempotent and bounded. These checks cover the game-free rules only.
/// </summary>
internal static class CommandHelpTests
{
    internal static void OnlyOneRowPerSyntaxReachesThePanel()
    {
        var published = new[]
        {
            new CommandHelpEntry("语音", "#aavt;voice;<key>", "第一句说明。"),
            new CommandHelpEntry("语音", "#aavt;VOICE;<key>", "大小写不同，但语法已被占用。"),
            new CommandHelpEntry("镜头", "#aavt;camera;reset", "第二句说明。"),
        };
        var selected = CommandHelpPolicy.SelectMissing(published, new[] { "  #aavt;voice;<key>  " });
        Equal(1, selected.Count);
        Equal("#aavt;camera;reset", selected[0].Syntax);

        // Publishing twice against an array that already holds our rows adds nothing.
        var second = CommandHelpPolicy.SelectMissing(published, published.Select(row => row.Syntax));
        Equal(0, second.Count);
    }

    internal static void WhitespaceAndControlCharactersAreFoldedAway()
    {
        True(CommandHelpPolicy.TryNormalize(" 语音 ", "#aavt;voice;\r\n<key>", "让这一句\t播放语音。  ",
            out CommandHelpEntry entry, out string error), error);
        Equal("语音", entry.Section);
        Equal("#aavt;voice; <key>", entry.Syntax);
        Equal("让这一句 播放语音。", entry.Description);
    }

    internal static void EmptyOrOversizedRowsAreRejected()
    {
        True(!CommandHelpPolicy.TryNormalize("", "#aavt;voice", "说明", out _, out _));
        True(!CommandHelpPolicy.TryNormalize("语音", "   ", "说明", out _, out _));
        True(!CommandHelpPolicy.TryNormalize("语音", "#aavt;voice", "", out _, out _));
        string longSection = new('分', CommandHelpPolicy.MaximumSectionLength + 1);
        True(!CommandHelpPolicy.TryNormalize(longSection, "#aavt;voice", "说明", out _, out _));
        string longSyntax = "#aavt;voice;" + new string('x', CommandHelpPolicy.MaximumSyntaxLength);
        True(!CommandHelpPolicy.TryNormalize("语音", longSyntax, "说明", out _, out _));
        True(CommandHelpPolicy.TryNormalize("语音", "#aavt;voice", "说明", out _, out string ok) && ok.Length == 0);
    }

    private static void True(bool value, string message = "")
    {
        if (!value) throw new InvalidOperationException("命令帮助规则断言失败。" + message);
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}; got {actual}.");
    }
}
