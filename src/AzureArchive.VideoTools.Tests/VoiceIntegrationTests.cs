using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Voices;

namespace AzureArchive.VideoTools.Tests;

internal static class VoiceIntegrationTests
{
    public static void VoiceIsRemovedFromNativeCommandsAndCanCoexistWithSceneCommands()
    {
        const string prompt = "#aavt;voice;line_001.wav\r\n#aavt;continue\r\n#aavt;char;3;set;x=20;y=0\r\n#clearST\r\n";
        EmbeddedAavtExtraction result = new EmbeddedAavtDirectiveExtractor().Extract(prompt);
        AssertEx.Equal(0, result.Errors.Count);
        AssertEx.Equal("#clearST\r\n", result.SanitizedText);
        AssertEx.Equal(3, result.RemovedLineCount);
        AssertEx.Equal(1, result.Commands.Count);
        AssertEx.True(result.HasContinueDirective);
        AssertEx.Equal("aavt/voice/line_001", VoiceDirectivePolicy.Parse(prompt).NativeVoiceIdentifier);
    }

    public static void InvalidVoiceIsRemovedButRejected()
    {
        const string prompt = "#aavt;voice;first\n#aavt;voice;second\n#clearST";
        EmbeddedAavtExtraction result = new EmbeddedAavtDirectiveExtractor().Extract(prompt);
        AssertEx.True(result.Errors.Count > 0);
        AssertEx.Equal("#clearST", result.SanitizedText);
        AssertEx.Equal(0, result.Commands.Count);
        AssertEx.Equal(2, result.RemovedLineCount);
    }

    public static void ExistingComposerEditsPreserveVoiceBinding()
    {
        const string prompt = "#aavt;voice;凯伊/line_001\r\n#clearST";
        var composer = new EditorCommandDocumentComposer();
        var result = composer.PreviewUpsert(prompt, EditorCommandDocumentComposer.Revision(prompt),
            "#aavt;char;3;set;x=20;y=0");
        AssertEx.True(result.Success);
        EditorCommandEditPreview changed = AssertEx.NotNull(result.Value);
        AssertEx.True(changed.UpdatedText.StartsWith(prompt, StringComparison.Ordinal));
        AssertEx.Equal("aavt/voice/凯伊/line_001", VoiceDirectivePolicy.Parse(changed.UpdatedText).NativeVoiceIdentifier);
        var continued = composer.SetContinue(changed.UpdatedText, changed.ResultRevisionSha256, true);
        AssertEx.Equal("aavt/voice/凯伊/line_001", VoiceDirectivePolicy.Parse(continued.UpdatedText).NativeVoiceIdentifier);
    }
}
