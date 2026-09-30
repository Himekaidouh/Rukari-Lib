using System.Buffers.Binary;
using System.Text;

namespace AzureArchive.VideoTools.Tests;

internal static class SyntheticAas
{
    private static readonly int[] FieldOffsets =
    {
        8,
        16,
        24,
        32,
        36,
        40,
        44,
        48,
        52,
        56,
        60,
        64,
        68,
        72,
        76,
        80
    };

    public static byte[] Archive(SyntheticAasRecord record)
    {
        const int rootTable = 12;
        const int vector = 20;
        const int vectorSlot = 24;
        const int recordVtable = 28;
        const int recordTable = 64;
        const int recordObjectLength = 88;

        byte[] bytes = new byte[4096];
        WriteUInt32(bytes, 0, rootTable);
        WriteUInt16(bytes, 4, 6);
        WriteUInt16(bytes, 6, 8);
        WriteUInt16(bytes, 8, 4);
        WriteInt32(bytes, rootTable, rootTable - 4);
        WriteUInt32(bytes, rootTable + 4, vector - (rootTable + 4));
        WriteUInt32(bytes, vector, 1);
        WriteUInt32(bytes, vectorSlot, recordTable - vectorSlot);

        WriteUInt16(bytes, recordVtable, 36);
        WriteUInt16(bytes, recordVtable + 2, recordObjectLength);
        for (int index = 0; index < FieldOffsets.Length; index++)
        {
            WriteUInt16(bytes, recordVtable + 4 + index * 2, FieldOffsets[index]);
        }

        WriteInt32(bytes, recordTable, recordTable - recordVtable);
        WriteInt64(bytes, recordTable + FieldOffsets[0], record.GroupId);
        WriteInt64(bytes, recordTable + FieldOffsets[1], record.SelectionGroup);
        WriteInt64(bytes, recordTable + FieldOffsets[2], record.BgmId);
        WriteUInt32(bytes, recordTable + FieldOffsets[4], record.Transition);
        WriteUInt32(bytes, recordTable + FieldOffsets[5], record.BackgroundName);
        WriteUInt32(bytes, recordTable + FieldOffsets[6], record.BackgroundEffect);
        bytes[recordTable + FieldOffsets[15]] = record.TeenMode ? (byte)1 : (byte)0;

        int cursor = recordTable + recordObjectLength;
        WriteString(bytes, recordTable + FieldOffsets[3], record.Sound, ref cursor);
        WriteString(bytes, recordTable + FieldOffsets[7], record.PopupFileName, ref cursor);
        WriteString(bytes, recordTable + FieldOffsets[8], record.CompiledScript, ref cursor);
        WriteString(bytes, recordTable + FieldOffsets[9], record.TextJp, ref cursor);
        WriteString(bytes, recordTable + FieldOffsets[10], record.TextTh, ref cursor);
        WriteString(bytes, recordTable + FieldOffsets[11], record.TextTw, ref cursor);
        WriteString(bytes, recordTable + FieldOffsets[12], record.TextCn, ref cursor);
        WriteString(bytes, recordTable + FieldOffsets[13], record.TextEn, ref cursor);
        WriteString(bytes, recordTable + FieldOffsets[14], record.VoiceJp, ref cursor);

        return bytes[..cursor];
    }

    private static void WriteString(
        byte[] bytes,
        int fieldPosition,
        string value,
        ref int cursor)
    {
        cursor = (cursor + 3) & ~3;
        byte[] encoded = Encoding.UTF8.GetBytes(value);
        WriteUInt32(bytes, fieldPosition, cursor - fieldPosition);
        WriteUInt32(bytes, cursor, encoded.Length);
        encoded.CopyTo(bytes, cursor + 4);
        bytes[cursor + 4 + encoded.Length] = 0;
        cursor += 5 + encoded.Length;
    }

    private static void WriteUInt16(byte[] bytes, int offset, int value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), (ushort)value);

    private static void WriteInt32(byte[] bytes, int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4), value);

    private static void WriteUInt32(byte[] bytes, int offset, int value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), (uint)value);

    private static void WriteUInt32(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);

    private static void WriteInt64(byte[] bytes, int offset, long value) =>
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset, 8), value);
}

internal sealed record SyntheticAasRecord(
    long GroupId = 123,
    long SelectionGroup = 456,
    long BgmId = 999,
    string Sound = "sound/test",
    uint Transition = 7,
    uint BackgroundName = 8,
    uint BackgroundEffect = 9,
    string PopupFileName = "popup/test",
    string CompiledScript = "1;character;00;dialogue",
    string TextJp = "dialogue",
    string TextTh = "thai",
    string TextTw = "traditional",
    string TextCn = "simplified",
    string TextEn = "english",
    string VoiceJp = "voice/test",
    bool TeenMode = true);
