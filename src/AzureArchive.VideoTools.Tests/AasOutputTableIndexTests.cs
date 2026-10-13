using System.Buffers.Binary;
using AzureArchive.VideoTools.Formats.Aas;

namespace AzureArchive.VideoTools.Tests;

internal static class AasOutputTableIndexTests
{
    public static void ActualVectorOrderResolvesEmptyRecordsIndependentlyOfEmissionOrder()
    {
        byte[] bytes = Archive(firstTable: 64, secondTable: 40);
        var read = AasOutputTableIndex.Read(bytes);
        AssertEx.True(read.Success, read.Error);
        IReadOnlyDictionary<int, int> index = AssertEx.NotNull(read.Value);
        AssertEx.Equal(0, index[bytes.Length - 64]);
        AssertEx.Equal(1, index[bytes.Length - 40]);
        using var files = new TemporaryAapDirectory();
        var parsed = new AasScenarioReader().Read(files.WriteBytes("empty-vector.aas", bytes));
        AssertEx.True(parsed.Success, parsed.Error);
        AssertEx.True(AssertEx.NotNull(parsed.Value).Records.All(record => record.TextJp == string.Empty
            && record.CompiledScript == string.Empty));
    }

    public static void DuplicateTableAddressesAndInvalidVectorsCannotProvideEvidence()
    {
        AssertEx.False(AasOutputTableIndex.Read(Archive(firstTable: 64, secondTable: 64)).Success);
        byte[] outside = Archive(firstTable: 64, secondTable: 40);
        BinaryPrimitives.WriteUInt32LittleEndian(outside.AsSpan(24, 4), uint.MaxValue);
        AssertEx.False(AasOutputTableIndex.Read(outside).Success);
        AssertEx.False(AasOutputTableIndex.Read(Archive(firstTable: 64, secondTable: 40), maximumRecords: 1).Success);
    }

    private static byte[] Archive(int firstTable, int secondTable)
    {
        byte[] bytes = new byte[80];
        U32(bytes, 0, 12); U16(bytes, 4, 6); U16(bytes, 6, 8); U16(bytes, 8, 4);
        I32(bytes, 12, 8); U32(bytes, 16, 4); U32(bytes, 20, 2);
        U32(bytes, 24, firstTable - 24); U32(bytes, 28, secondTable - 28);
        U16(bytes, 32, 4); U16(bytes, 34, 4);
        I32(bytes, 40, 8); I32(bytes, 64, 32);
        return bytes;
    }

    private static void U16(byte[] bytes, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), value);
    private static void U32(byte[] bytes, int offset, int value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), (uint)value);
    private static void I32(byte[] bytes, int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4), value);
}
