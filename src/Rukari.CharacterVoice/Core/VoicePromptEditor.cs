using System.Text;
using Rukari.Lib;

namespace Rukari.CharacterVoice.Core;

/// <summary>Changes only this module's voice directive; preserves every unrelated line verbatim.</summary>
internal static class VoicePromptEditor
{
    // This independently installed module removes only its own compiler-output lines.
    // The stored additional-prompt document is never changed by this sanitizer.
    internal static string SanitizeForNative(string source)
    {
        var output = new StringBuilder(source.Length);
        int offset = 0;
        while (offset < source.Length)
        {
            int contentEnd = offset;
            while (contentEnd < source.Length && source[contentEnd] is not ('\r' or '\n')) contentEnd++;
            int lineEnd = contentEnd;
            if (lineEnd < source.Length && source[lineEnd] == '\r') lineEnd++;
            if (lineEnd < source.Length && source[lineEnd] == '\n') lineEnd++;
            if (!VoiceDirectivePolicy.IsVoiceDirectiveLine(source[offset..contentEnd]))
                output.Append(source, offset, lineEnd - offset);
            offset = lineEnd;
        }
        return output.ToString();
    }

    internal static ModResult<string> SetBinding(string source, string? resourceId)
    {
        VoiceDirectiveParseResult current = VoiceDirectivePolicy.Parse(source);
        if (!current.Success)
            return ModResult<string>.Fail(ModErrorCode.InvalidArgument, string.Join(" | ", current.Errors));

        string? replacement = null;
        if (resourceId is not null)
        {
            if (!VoiceDirectivePolicy.TryDecodeNativeIdentifier(VoiceDirectivePolicy.NativeIdentifierPrefix + resourceId,
                    out string key) || !StringComparer.Ordinal.Equals(key, resourceId))
                return ModResult<string>.Fail(ModErrorCode.InvalidArgument, "语音资源 ID 无效，请从资源列表重新选择。");
            replacement = "#aavt;voice;" + key;
            // The retained parser removes one extension. A resource stem may itself end in .ogg,
            // .wav or .mp3; preserve such stems with a disposable parser suffix.
            if (!StringComparer.Ordinal.Equals(VoiceDirectivePolicy.Parse(replacement).ResourceKey, key))
                replacement += ".ogg";
        }

        var output = new StringBuilder(source.Length + (replacement?.Length ?? 0) + 2);
        bool replaced = false;
        int offset = 0;
        while (offset < source.Length)
        {
            int contentEnd = offset;
            while (contentEnd < source.Length && source[contentEnd] is not ('\r' or '\n')) contentEnd++;
            int lineEnd = contentEnd;
            if (lineEnd < source.Length && source[lineEnd] == '\r') lineEnd++;
            if (lineEnd < source.Length && source[lineEnd] == '\n') lineEnd++;
            if (VoiceDirectivePolicy.IsVoiceDirectiveLine(source[offset..contentEnd]))
            {
                replaced = true;
                if (replacement is not null)
                    output.Append(replacement).Append(source, contentEnd, lineEnd - contentEnd);
            }
            else output.Append(source, offset, lineEnd - offset);
            offset = lineEnd;
        }
        if (!replaced && replacement is not null)
        {
            if (output.Length > 0 && output[^1] is not ('\r' or '\n'))
                output.Append(source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n");
            output.Append(replacement);
        }
        string updated = output.ToString();
        VoiceDirectiveParseResult verified = VoiceDirectivePolicy.Parse(updated);
        return verified.Success && StringComparer.Ordinal.Equals(verified.ResourceKey, resourceId)
            ? ModResult<string>.Ok(updated)
            : ModResult<string>.Fail(ModErrorCode.ProviderFailed, "语音指令未能保持请求的资源，请重新读取台词。");
    }
}
