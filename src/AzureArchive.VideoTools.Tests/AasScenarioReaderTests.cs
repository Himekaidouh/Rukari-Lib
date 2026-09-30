using System.Buffers.Binary;
using System.Text;
using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Formats.Aas;

namespace AzureArchive.VideoTools.Tests;

internal static class AasScenarioReaderTests
{
    public static void ParsesSyntheticGenericScenarioArchive()
    {
        using var files = new TemporaryAapDirectory();
        var expected = new SyntheticAasRecord();
        string path = files.WriteBytes("synthetic.aas", SyntheticAas.Archive(expected));

        var result = new AasScenarioReader().Read(path);
        AssertEx.True(result.Success, result.Error);
        PlaybackArchiveSnapshot archive = AssertEx.NotNull(result.Value);
        AssertEx.Equal(AasScenarioReader.CurrentSchemaName, archive.SchemaName);
        AssertEx.Equal(1, archive.Records.Count);
        AssertEx.Equal(64, archive.Source.PathKey.Length);
        AssertEx.Equal(64, archive.Source.RevisionSha256.Length);

        PlaybackRecordSnapshot actual = archive.Records[0];
        AssertEx.Equal(0, actual.RecordIndex);
        AssertEx.Equal(expected.GroupId, actual.GroupId);
        AssertEx.Equal(expected.SelectionGroup, actual.SelectionGroup);
        AssertEx.Equal(expected.BgmId, actual.BgmId);
        AssertEx.Equal(expected.Sound, actual.Sound);
        AssertEx.Equal(expected.Transition, actual.Transition);
        AssertEx.Equal(expected.BackgroundName, actual.BackgroundName);
        AssertEx.Equal(expected.BackgroundEffect, actual.BackgroundEffect);
        AssertEx.Equal(expected.PopupFileName, actual.PopupFileName);
        AssertEx.Equal(expected.CompiledScript, actual.CompiledScript);
        AssertEx.Equal(expected.TextJp, actual.TextJp);
        AssertEx.Equal(expected.TextTh, actual.TextTh);
        AssertEx.Equal(expected.TextTw, actual.TextTw);
        AssertEx.Equal(expected.TextCn, actual.TextCn);
        AssertEx.Equal(expected.TextEn, actual.TextEn);
        AssertEx.Equal(expected.VoiceJp, actual.VoiceJp);
        AssertEx.Equal(expected.TeenMode, actual.TeenMode);
        AssertEx.Equal(64, actual.Fingerprint.Length);
    }

    public static void InvalidRootOffsetFailsWithoutThrowing()
    {
        using var files = new TemporaryAapDirectory();
        byte[] bytes = SyntheticAas.Archive(new SyntheticAasRecord());
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), uint.MaxValue);
        string path = files.WriteBytes("bad-root.aas", bytes);

        var result = new AasScenarioReader().Read(path);
        AssertEx.False(result.Success);
        AssertEx.True(
            result.Error.StartsWith(
                "AAS FlatBuffer is invalid or unsupported:",
                StringComparison.Ordinal));
    }

    public static void RecordLimitAndWrongExtensionFailClosed()
    {
        using var files = new TemporaryAapDirectory();
        byte[] bytes = SyntheticAas.Archive(new SyntheticAasRecord());
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20, 4), 2);
        string limitedPath = files.WriteBytes("too-many.aas", bytes);
        string wrongExtension = files.WriteBytes(
            "archive.bin",
            SyntheticAas.Archive(new SyntheticAasRecord()));

        var limited = new AasScenarioReader(
            new AasScenarioReaderOptions(MaxRecords: 1)).Read(limitedPath);
        var wrong = new AasScenarioReader().Read(wrongExtension);
        AssertEx.False(limited.Success);
        AssertEx.True(limited.Error.Contains("element limit", StringComparison.Ordinal));
        AssertEx.False(wrong.Success);
        AssertEx.True(wrong.Error.Contains(".aas", StringComparison.OrdinalIgnoreCase));
    }

    public static void InvalidUtf8FailsClosed()
    {
        using var files = new TemporaryAapDirectory();
        const string target = "unique-utf8-target";
        byte[] bytes = SyntheticAas.Archive(new SyntheticAasRecord(TextJp: target));
        byte[] encodedTarget = Encoding.UTF8.GetBytes(target);
        int targetOffset = FindSequence(bytes, encodedTarget);
        AssertEx.True(targetOffset >= 0, "Synthetic target string was not found.");
        bytes[targetOffset] = 0xFF;
        string path = files.WriteBytes("invalid-utf8.aas", bytes);

        var result = new AasScenarioReader().Read(path);
        AssertEx.False(result.Success);
        AssertEx.True(
            result.Error.Contains("UTF-8", StringComparison.Ordinal),
            $"Unexpected AAS error: {result.Error}");
    }

    private static int FindSequence(byte[] source, byte[] target)
    {
        for (int sourceIndex = 0; sourceIndex <= source.Length - target.Length; sourceIndex++)
        {
            bool matches = true;
            for (int targetIndex = 0; targetIndex < target.Length; targetIndex++)
            {
                if (source[sourceIndex + targetIndex] == target[targetIndex])
                {
                    continue;
                }

                matches = false;
                break;
            }

            if (matches)
            {
                return sourceIndex;
            }
        }

        return -1;
    }
}
