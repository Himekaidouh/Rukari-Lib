using System.Security.Cryptography;
using System.Text;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Commands;

public static class CommandIdentity
{
    public static CompiledScriptIdentity CompiledScript(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        return new CompiledScriptIdentity(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script))),
            script.Length,
            CountLines(script));
    }

    public static Result<string> Timeline(CommandTimelineDocument? timeline)
    {
        if (timeline?.Entries == null
            || timeline.WorkspaceId == null
            || timeline.ProjectPathKey == null
            || timeline.ProjectRevisionSha256 == null)
        {
            return Result<string>.Fail(
                "Command timeline cannot be fingerprinted because its root is incomplete.");
        }

        foreach (CommandTimelineEntry? entry in timeline.Entries)
        {
            if (entry?.Scene == null
                || entry.Commands == null
                || entry.Scene.NodeGuid == null
                || entry.Scene.Fingerprint == null
                || entry.Commands.Any(command =>
                    command == null
                    || command.CommandId == null
                    || command.CommandType == null
                    || command.Directive == null))
            {
                return Result<string>.Fail(
                    "Command timeline cannot be fingerprinted because an entry is incomplete.");
            }
        }

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("AzureArchive.VideoTools.CommandTimeline/v1");
            writer.Write(timeline.SchemaVersion);
            writer.Write(timeline.WorkspaceId);
            writer.Write(timeline.ProjectPathKey);
            writer.Write(timeline.ProjectRevisionSha256);
            writer.Write(timeline.Entries.Count);
            foreach (CommandTimelineEntry entry in timeline.Entries)
            {
                writer.Write(entry.Scene.NodeGuid);
                writer.Write(entry.Scene.SceneIndex);
                writer.Write(entry.Scene.Fingerprint);
                writer.Write(entry.Commands.Count);
                foreach (AuthoringCommand command in entry.Commands)
                {
                    writer.Write(command.CommandId);
                    writer.Write(command.Order);
                    writer.Write((int)command.Phase);
                    writer.Write(command.CommandType);
                    writer.Write(command.Directive);
                    writer.Write(command.Enabled);
                }
            }
        }

        return Result<string>.Ok(
            Convert.ToHexString(SHA256.HashData(stream.ToArray())));
    }

    internal static bool TryNormalizeSha256(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (value is not { Length: 64 } || !value.All(Uri.IsHexDigit))
        {
            return false;
        }

        normalized = value.ToUpperInvariant();
        return true;
    }

    internal static int CountLines(string text)
    {
        int count = 1;
        foreach (char character in text)
        {
            if (character == '\n')
            {
                count++;
            }
        }

        return count;
    }
}
