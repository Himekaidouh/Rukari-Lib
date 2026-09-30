namespace AzureArchive.VideoTools.Core.Voices;

public sealed record VoiceDirectiveParseResult(
    bool HasDirective,
    bool Success,
    string? ResourceKey,
    string? NativeVoiceIdentifier,
    IReadOnlyList<string> Errors);

public sealed record VoiceBindingResult(
    bool HasDirective,
    bool Success,
    string? VoiceIdentifier,
    IReadOnlyList<string> Errors);

/// <summary>
/// Interprets one voice binding in the existing multiline extra-instruction field.
/// This policy neither loads audio nor changes native objects.
/// </summary>
public static class VoiceDirectivePolicy
{
    public const string NativeIdentifierPrefix = "aavt/voice/";

    public static VoiceDirectiveParseResult Parse(string? prompt)
    {
        if (string.IsNullOrEmpty(prompt))
        {
            return new(false, true, null, null, Array.Empty<string>());
        }

        var errors = new List<string>();
        string? resourceKey = null;
        int directiveCount = 0;
        string[] lines = prompt.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            if (!IsVoiceDirectiveLine(line))
            {
                continue;
            }

            directiveCount++;
            string[] parts = line.Split(';');
            if (parts.Length != 3)
            {
                errors.Add($"第 {index + 1} 行语音指令需要且只能提供一个资源键：#aavt;voice;<资源键>。");
                continue;
            }

            if (!TryNormalizeResourceKey(parts[2], stripAudioExtension: true, out string normalizedKey, out string error))
            {
                errors.Add($"第 {index + 1} 行语音资源键无效：{error}");
                continue;
            }

            resourceKey = normalizedKey;
        }

        if (directiveCount > 1)
        {
            errors.Add("同一句只能设置一条语音指令；请删除重复的 #aavt;voice 行。");
        }

        bool success = errors.Count == 0;
        return new(
            directiveCount > 0,
            success,
            success ? resourceKey : null,
            success && resourceKey is not null ? NativeIdentifierPrefix + resourceKey : null,
            errors.ToArray());
    }

    public static bool IsVoiceDirectiveLine(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return false;
        }

        int firstSeparator = line.IndexOf(';');
        if (firstSeparator < 0 || !line[..firstSeparator].Trim().Equals("#aavt", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int secondSeparator = line.IndexOf(';', firstSeparator + 1);
        string command = secondSeparator < 0 ? line[(firstSeparator + 1)..] : line[(firstSeparator + 1)..secondSeparator];
        return command.Trim().Equals("voice", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Decodes only an owned identifier with a normalized relative resource key.
    /// Audio extensions are removed at directive parsing, never during decoding:
    /// an existing resource stem may itself end in an extension-like suffix.
    /// </summary>
    public static bool TryDecodeNativeIdentifier(string? identifier, out string resourceKey)
    {
        resourceKey = string.Empty;
        if (!IsOwnedIdentifier(identifier))
        {
            return false;
        }

        string candidate = identifier![NativeIdentifierPrefix.Length..];
        if (!TryNormalizeResourceKey(candidate, stripAudioExtension: false, out string normalizedKey, out _)
            || !candidate.Equals(normalizedKey, StringComparison.Ordinal))
        {
            return false;
        }

        resourceKey = normalizedKey;
        return true;
    }

    public static VoiceBindingResult ResolveBinding(
        string? prompt,
        string? existingVoice,
        bool existingVoiceHasResource = true)
    {
        VoiceDirectiveParseResult parsed = Parse(prompt);
        bool ownsExisting = IsOwnedIdentifier(existingVoice);
        string? preservedVoice = ownsExisting ? null : existingVoice;
        if (!parsed.Success || !parsed.HasDirective)
        {
            // Undo, deletion and malformed edits must not leave an earlier binding active.
            return new(parsed.HasDirective, parsed.Success, preservedVoice, parsed.Errors);
        }

        if (!string.IsNullOrEmpty(existingVoice) && !ownsExisting && existingVoiceHasResource)
        {
            return new(
                true,
                false,
                existingVoice,
                new[] { "此句已有原版语音；语音指令不会覆盖它。请先移除其中一种语音绑定。" });
        }

        return new(true, true, parsed.NativeVoiceIdentifier, Array.Empty<string>());
    }

    private static bool IsOwnedIdentifier(string? identifier) =>
        identifier?.StartsWith(NativeIdentifierPrefix, StringComparison.Ordinal) == true;

    private static bool TryNormalizeResourceKey(
        string value,
        bool stripAudioExtension,
        out string resourceKey,
        out string error)
    {
        resourceKey = string.Empty;
        error = string.Empty;
        if (value.Any(char.IsControl))
        {
            error = "不能包含控制字符。";
            return false;
        }

        string normalized = value.Trim().Replace('\\', '/');
        if (stripAudioExtension
            && (normalized.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)))
        {
            normalized = normalized[..^4].TrimEnd();
        }

        if (normalized.Length == 0 || string.IsNullOrWhiteSpace(normalized))
        {
            error = "资源键不能为空。";
            return false;
        }

        if (normalized[0] == '/' || normalized.Contains(':') || normalized.Contains(';'))
        {
            error = "需要资源库中的相对资源键，不能使用绝对路径、网址、冒号或分号。";
            return false;
        }

        if (normalized.Split('/').Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            error = "相对路径不能包含空目录、. 或 .. 目录。";
            return false;
        }

        resourceKey = normalized;
        return true;
    }
}
