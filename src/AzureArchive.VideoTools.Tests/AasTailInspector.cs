using System.Security.Cryptography;
using System.Text;
using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Formats.Aas;

namespace AzureArchive.VideoTools.Tests;

internal static class AasTailInspector
{
    private const int TailCount = 32;
    private const int MaximumValueLength = 500;

    public static int Inspect(string path)
    {
        var result = new AasScenarioReader().Read(path);
        if (!result.Success || result.Value == null)
        {
            Console.Error.WriteLine($"AAS TAIL FAIL {result.Error}");
            return 1;
        }

        PlaybackArchiveSnapshot archive = result.Value;
        int start = Math.Max(0, archive.Records.Count - TailCount);
        Console.WriteLine(
            $"AAS TAIL path={archive.Source.FullPath}; records={archive.Records.Count}; "
            + $"start={start}; revision={archive.Source.RevisionSha256}");

        for (int index = start; index < archive.Records.Count; index++)
        {
            PlaybackRecordSnapshot record = archive.Records[index];
            string scriptSha256 = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(record.CompiledScript)));
            int scriptLines = record.CompiledScript.Count(character => character == '\n') + 1;
            Console.WriteLine(
                $"record={record.RecordIndex}; group={record.GroupId}; "
                + $"selection={record.SelectionGroup}; textCn={Escape(record.TextCn)}; "
                + $"scriptLen={record.CompiledScript.Length}; scriptLines={scriptLines}; "
                + $"scriptSha256={scriptSha256}; "
                + $"script={Escape(record.CompiledScript)}");
        }

        return 0;
    }

    private static string Escape(string value)
    {
        string escaped = value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
        return escaped.Length <= MaximumValueLength
            ? escaped
            : escaped.Substring(0, MaximumValueLength) + "...";
    }
}
