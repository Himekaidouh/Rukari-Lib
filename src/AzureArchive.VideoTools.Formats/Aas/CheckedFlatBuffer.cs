using System.Buffers.Binary;
using System.Text;

namespace AzureArchive.VideoTools.Formats.Aas;

internal sealed class CheckedFlatBuffer
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly byte[] _bytes;
    private readonly int _maxStringBytes;

    public CheckedFlatBuffer(byte[] bytes, int maxStringBytes)
    {
        _bytes = bytes;
        _maxStringBytes = maxStringBytes;
    }

    public Table ReadRootTable()
    {
        uint relative = ReadUInt32(0, "root offset");
        int position = AddOffset(0, relative, "root table");
        return ReadTable(position, "root table");
    }

    public IReadOnlyList<Table> ReadTableVector(
        Table owner,
        int fieldIndex,
        int maxElements,
        string context)
    {
        int fieldPosition = GetRequiredFieldPosition(
            owner,
            fieldIndex,
            sizeof(uint),
            context);
        int vectorPosition = ReadOffsetTarget(fieldPosition, $"{context} vector");
        uint rawLength = ReadUInt32(vectorPosition, $"{context} length");
        if (rawLength > maxElements || rawLength > int.MaxValue)
        {
            throw new InvalidDataException(
                $"{context} has {rawLength} elements, exceeding the {maxElements}-element limit.");
        }

        int length = (int)rawLength;
        int dataPosition = CheckedAdd(vectorPosition, sizeof(uint), $"{context} data");
        long rawDataLength = (long)length * sizeof(uint);
        if (rawDataLength > int.MaxValue)
        {
            throw new InvalidDataException($"{context} byte length exceeds Int32.MaxValue.");
        }

        EnsureRange(dataPosition, (int)rawDataLength, $"{context} data");

        var tables = new Table[length];
        for (int index = 0; index < length; index++)
        {
            int slot = CheckedAdd(
                dataPosition,
                checked(index * sizeof(uint)),
                $"{context}[{index}] slot");
            int tablePosition = ReadOffsetTarget(slot, $"{context}[{index}] table");
            tables[index] = ReadTable(tablePosition, $"{context}[{index}] table");
        }

        return Array.AsReadOnly(tables);
    }

    public long ReadInt64(Table table, int fieldIndex, string context)
    {
        int? position = GetOptionalFieldPosition(
            table,
            fieldIndex,
            sizeof(long),
            context);
        return position.HasValue
            ? BinaryPrimitives.ReadInt64LittleEndian(_bytes.AsSpan(position.Value, sizeof(long)))
            : 0L;
    }

    public uint ReadUInt32(Table table, int fieldIndex, string context)
    {
        int? position = GetOptionalFieldPosition(
            table,
            fieldIndex,
            sizeof(uint),
            context);
        return position.HasValue
            ? ReadUInt32(position.Value, context)
            : 0U;
    }

    public bool ReadBoolean(Table table, int fieldIndex, string context)
    {
        int? position = GetOptionalFieldPosition(
            table,
            fieldIndex,
            sizeof(byte),
            context);
        if (!position.HasValue)
        {
            return false;
        }

        byte value = _bytes[position.Value];
        if (value > 1)
        {
            throw new InvalidDataException(
                $"{context} contains invalid FlatBuffer boolean value {value}.");
        }

        return value == 1;
    }

    public string ReadString(Table table, int fieldIndex, string context)
    {
        int? fieldPosition = GetOptionalFieldPosition(
            table,
            fieldIndex,
            sizeof(uint),
            context);
        if (!fieldPosition.HasValue)
        {
            return string.Empty;
        }

        int stringPosition = ReadOffsetTarget(fieldPosition.Value, context);
        uint rawLength = ReadUInt32(stringPosition, $"{context} length");
        if (rawLength > _maxStringBytes || rawLength > int.MaxValue)
        {
            throw new InvalidDataException(
                $"{context} is {rawLength} bytes, exceeding the {_maxStringBytes}-byte string limit.");
        }

        int length = (int)rawLength;
        int dataPosition = CheckedAdd(stringPosition, sizeof(uint), $"{context} data");
        if (length == int.MaxValue)
        {
            throw new InvalidDataException($"{context} length cannot include a terminator.");
        }

        EnsureRange(dataPosition, length + 1, context);
        if (_bytes[dataPosition + length] != 0)
        {
            throw new InvalidDataException(
                $"{context} has no FlatBuffer null terminator.");
        }

        try
        {
            return StrictUtf8.GetString(_bytes, dataPosition, length);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException($"{context} is not valid UTF-8.", ex);
        }
    }

    public void RejectPresentFieldsAfter(
        Table table,
        int knownFieldCount,
        string context)
    {
        int availableFields = (table.VtableLength - 4) / sizeof(ushort);
        for (int index = knownFieldCount; index < availableFields; index++)
        {
            int entryPosition = table.VtablePosition + 4 + index * sizeof(ushort);
            ushort fieldOffset = BinaryPrimitives.ReadUInt16LittleEndian(
                _bytes.AsSpan(entryPosition, sizeof(ushort)));
            if (fieldOffset != 0)
            {
                throw new InvalidDataException(
                    $"{context} contains unsupported field index {index}.");
            }
        }
    }

    private Table ReadTable(int position, string context)
    {
        EnsureRange(position, sizeof(int), context);
        int vtableDistance = BinaryPrimitives.ReadInt32LittleEndian(
            _bytes.AsSpan(position, sizeof(int)));
        if (vtableDistance == 0)
        {
            throw new InvalidDataException($"{context} has a zero vtable distance.");
        }

        long rawVtablePosition = (long)position - vtableDistance;
        if (rawVtablePosition < 0 || rawVtablePosition > int.MaxValue)
        {
            throw new InvalidDataException($"{context} vtable is outside the file.");
        }

        int vtablePosition = (int)rawVtablePosition;
        EnsureRange(vtablePosition, 4, $"{context} vtable");
        ushort vtableLength = BinaryPrimitives.ReadUInt16LittleEndian(
            _bytes.AsSpan(vtablePosition, sizeof(ushort)));
        ushort objectLength = BinaryPrimitives.ReadUInt16LittleEndian(
            _bytes.AsSpan(vtablePosition + sizeof(ushort), sizeof(ushort)));
        if (vtableLength < 4 || (vtableLength & 1) != 0)
        {
            throw new InvalidDataException(
                $"{context} has invalid vtable length {vtableLength}.");
        }

        if (objectLength < sizeof(int))
        {
            throw new InvalidDataException(
                $"{context} has invalid object length {objectLength}.");
        }

        EnsureRange(vtablePosition, vtableLength, $"{context} vtable");
        EnsureRange(position, objectLength, context);
        return new Table(position, vtablePosition, vtableLength, objectLength);
    }

    private int GetRequiredFieldPosition(
        Table table,
        int fieldIndex,
        int width,
        string context) =>
        GetOptionalFieldPosition(table, fieldIndex, width, context)
        ?? throw new InvalidDataException($"{context} is missing.");

    private int? GetOptionalFieldPosition(
        Table table,
        int fieldIndex,
        int width,
        string context)
    {
        if (fieldIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fieldIndex));
        }

        int entryOffset = checked(4 + fieldIndex * sizeof(ushort));
        if (entryOffset + sizeof(ushort) > table.VtableLength)
        {
            return null;
        }

        int entryPosition = table.VtablePosition + entryOffset;
        ushort fieldOffset = BinaryPrimitives.ReadUInt16LittleEndian(
            _bytes.AsSpan(entryPosition, sizeof(ushort)));
        if (fieldOffset == 0)
        {
            return null;
        }

        if (fieldOffset < sizeof(int)
            || fieldOffset + width > table.ObjectLength)
        {
            throw new InvalidDataException(
                $"{context} has invalid object offset {fieldOffset}.");
        }

        int position = CheckedAdd(table.Position, fieldOffset, context);
        EnsureRange(position, width, context);
        return position;
    }

    private int ReadOffsetTarget(int position, string context)
    {
        uint relative = ReadUInt32(position, $"{context} offset");
        if (relative < sizeof(uint))
        {
            throw new InvalidDataException(
                $"{context} has invalid relative offset {relative}.");
        }

        return AddOffset(position, relative, context);
    }

    private uint ReadUInt32(int position, string context)
    {
        EnsureRange(position, sizeof(uint), context);
        return BinaryPrimitives.ReadUInt32LittleEndian(
            _bytes.AsSpan(position, sizeof(uint)));
    }

    private int AddOffset(int position, uint relative, string context)
    {
        long target = (long)position + relative;
        if (target < 0 || target > int.MaxValue || target >= _bytes.Length)
        {
            throw new InvalidDataException($"{context} points outside the file.");
        }

        return (int)target;
    }

    private static int CheckedAdd(int left, int right, string context)
    {
        try
        {
            return checked(left + right);
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException($"{context} offset overflowed.", ex);
        }
    }

    private void EnsureRange(int position, int length, string context)
    {
        if (position < 0
            || length < 0
            || (long)position + length > _bytes.Length)
        {
            throw new InvalidDataException($"{context} is outside the file.");
        }
    }

    public readonly record struct Table(
        int Position,
        int VtablePosition,
        ushort VtableLength,
        ushort ObjectLength);
}
