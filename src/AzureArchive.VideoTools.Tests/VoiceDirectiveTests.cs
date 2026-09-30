using AzureArchive.VideoTools.Core.Voices;

namespace AzureArchive.VideoTools.Tests;

internal static class VoiceDirectiveTests
{
    public static void MixedInstructionsOnlyRecognizeVoiceNamespace()
    {
        const string prompt = "其他指令\r\n#aavt;camera;zoom;2\r\n  #AaVt ; VoIcE ;凯伊/Hello World.WAV\r\n#aavt;voiceover;ignored";
        VoiceDirectiveParseResult result = VoiceDirectivePolicy.Parse(prompt);
        AssertEx.True(result.HasDirective);
        AssertEx.True(result.Success);
        AssertEx.Equal("凯伊/Hello World", result.ResourceKey);
        AssertEx.Equal("aavt/voice/凯伊/Hello World", result.NativeVoiceIdentifier);
        AssertEx.Equal(0, result.Errors.Count);

        foreach (string line in new[] { "#aavt;voice", "#AAVT;VOICE;x", "  #aavt ; voice ; x;extra" })
        {
            AssertEx.True(VoiceDirectivePolicy.IsVoiceDirectiveLine(line));
        }

        foreach (string? line in new string?[] { null, "", "voice;x", "#other;voice;x", "#aavt;voiceover;x", "prefix #aavt;voice;x" })
        {
            AssertEx.False(VoiceDirectivePolicy.IsVoiceDirectiveLine(line));
        }
    }

    public static void ResourceKeysNormalizeWithoutLosingCaseOrOtherDots()
    {
        var cases = new Dictionary<string, string>
        {
            ["  凯伊\\Talk 01.MP3  "] = "凯伊/Talk 01",
            ["Dialogue.v2.Talk.ogg"] = "Dialogue.v2.Talk",
            ["A.b/Wave.WaV"] = "A.b/Wave",
            ["no_extension"] = "no_extension",
            ["some.other"] = "some.other",
            ["resource.wav.ogg"] = "resource.wav",
        };
        foreach ((string source, string expected) in cases)
        {
            VoiceDirectiveParseResult parsed = VoiceDirectivePolicy.Parse("#aavt;voice;" + source);
            AssertEx.True(parsed.Success);
            AssertEx.Equal(expected, parsed.ResourceKey);
            AssertEx.True(VoiceDirectivePolicy.TryDecodeNativeIdentifier(parsed.NativeVoiceIdentifier, out string decoded));
            AssertEx.Equal(expected, decoded);
        }
    }

    public static void InvalidResourceKeysNeverProduceBindings()
    {
        foreach (string key in new[]
        {
            "", "   ", ".wav", "/root", "\\root", "C:\\voice", "https://voice", "../voice", "a/../voice",
            "./voice", "a/./voice", "a//voice", "a/", "a\tvoice", "voice\t", "\tvoice", "a\0voice", "a\u007fvoice", "a;extra",
        })
        {
            VoiceDirectiveParseResult result = VoiceDirectivePolicy.Parse("#aavt;voice;" + key);
            AssertEx.True(result.HasDirective);
            AssertEx.False(result.Success);
            AssertEx.True(result.ResourceKey is null);
            AssertEx.True(result.NativeVoiceIdentifier is null);
            AssertEx.True(result.Errors.Count > 0);
        }
    }

    public static void DuplicateAndWrongArityDirectivesAreErrors()
    {
        foreach (string prompt in new[]
        {
            "#aavt;voice",
            "#aavt;voice;one;two",
            "#aavt;voice;one\n#aavt;voice;one",
            "#aavt;voice;one\r\n#aavt;voice;two",
            "#aavt;voice;\n#aavt;voice;valid",
        })
        {
            VoiceDirectiveParseResult parsed = VoiceDirectivePolicy.Parse(prompt);
            AssertEx.True(parsed.HasDirective);
            AssertEx.False(parsed.Success);
            AssertEx.True(parsed.NativeVoiceIdentifier is null);
        }
    }

    public static void NativeAliasesRequireOwnedCanonicalRelativeKeys()
    {
        foreach (string? alias in new string?[]
        {
            null, "", "normalvoice", "AAVT/voice/a", "aavt/VOICE/a", "aavt/voice/", "aavt/voice//absolute",
            "aavt/voice/a\\b", "aavt/voice/a/../b", "aavt/voice/a/./b", "aavt/voice/a//b",
            "aavt/voice/ a", "aavt/voice/a ", "aavt/voice/C:/a", "aavt/voice/a;b", "aavt/voice/a\tb",
        })
        {
            AssertEx.False(VoiceDirectivePolicy.TryDecodeNativeIdentifier(alias, out string key));
            AssertEx.Equal(string.Empty, key);
        }

        AssertEx.True(VoiceDirectivePolicy.TryDecodeNativeIdentifier("aavt/voice/凯伊/Talk.v2 01", out string validKey));
        AssertEx.Equal("凯伊/Talk.v2 01", validKey);
    }

    public static void RemovingOrInvalidatingDirectiveClearsOnlyOwnedBinding()
    {
        foreach (string? prompt in new string?[] { null, "", "#aavt;camera;zoom;2", "#aavt;voice;", "#aavt;voice;one\n#aavt;voice;one" })
        {
            VoiceBindingResult result = VoiceDirectivePolicy.ResolveBinding(prompt, "aavt/voice/previous");
            AssertEx.True(result.VoiceIdentifier is null);

            VoiceBindingResult native = VoiceDirectivePolicy.ResolveBinding(prompt, "NativeVoice_01");
            AssertEx.Equal("NativeVoice_01", native.VoiceIdentifier);
        }

        VoiceBindingResult malformedOldAlias = VoiceDirectivePolicy.ResolveBinding(null, "aavt/voice/../bad");
        AssertEx.True(malformedOldAlias.Success);
        AssertEx.True(malformedOldAlias.VoiceIdentifier is null);
    }

    public static void BindingCanBeAddedReplacedAndReapplied()
    {
        foreach (string? oldVoice in new string?[] { null, "", "aavt/voice/old", "aavt/voice/凯伊/Talk 01" })
        {
            VoiceBindingResult bound = VoiceDirectivePolicy.ResolveBinding("#aavt;voice;凯伊\\Talk 01.ogg", oldVoice);
            AssertEx.True(bound.HasDirective);
            AssertEx.True(bound.Success);
            AssertEx.Equal("aavt/voice/凯伊/Talk 01", bound.VoiceIdentifier);
            AssertEx.Equal(0, bound.Errors.Count);
        }
    }

    public static void NativeVoiceBindingsAreNeverOverwritten()
    {
        VoiceBindingResult conflict = VoiceDirectivePolicy.ResolveBinding("#aavt;voice;new", "NativeVoice_01");
        AssertEx.True(conflict.HasDirective);
        AssertEx.False(conflict.Success);
        AssertEx.Equal("NativeVoice_01", conflict.VoiceIdentifier);
        AssertEx.True(conflict.Errors.Count > 0);

        foreach (string? voice in new string?[] { null, "", "NativeVoice_01", "AAVT/voice/not-owned" })
        {
            VoiceBindingResult untouched = VoiceDirectivePolicy.ResolveBinding("#aavt;camera;zoom;2", voice);
            AssertEx.False(untouched.HasDirective);
            AssertEx.True(untouched.Success);
            AssertEx.Equal(voice, untouched.VoiceIdentifier);
        }
    }

    public static void PlaceholderIdentifiersCanBeBoundWithoutAssumingGuidSemantics()
    {
        const string nativeIdentifier = "561dc5ac-97f8-469b-9b57-8ad8d425fb48";
        VoiceBindingResult placeholder = VoiceDirectivePolicy.ResolveBinding(
            "#aavt;voice;凯伊/Talk 01", nativeIdentifier, existingVoiceHasResource: false);
        AssertEx.True(placeholder.Success);
        AssertEx.Equal("aavt/voice/凯伊/Talk 01", placeholder.VoiceIdentifier);

        VoiceBindingResult actualResource = VoiceDirectivePolicy.ResolveBinding(
            "#aavt;voice;凯伊/Talk 01", nativeIdentifier, existingVoiceHasResource: true);
        AssertEx.False(actualResource.Success);
        AssertEx.Equal(nativeIdentifier, actualResource.VoiceIdentifier);

        VoiceBindingResult notAGuid = VoiceDirectivePolicy.ResolveBinding(
            "#aavt;voice;new", "non-guid-placeholder", existingVoiceHasResource: false);
        AssertEx.True(notAGuid.Success);
        AssertEx.Equal("aavt/voice/new", notAGuid.VoiceIdentifier);

        VoiceBindingResult unmodified = VoiceDirectivePolicy.ResolveBinding(
            "#aavt;camera;zoom;2", nativeIdentifier, existingVoiceHasResource: false);
        AssertEx.Equal(nativeIdentifier, unmodified.VoiceIdentifier);
    }
}
