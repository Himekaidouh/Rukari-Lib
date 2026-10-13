using System.Collections.ObjectModel;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Formats.Aas;

/// <summary>Resolves observed backwards builder offsets through the archive's actual DataList vector.</summary>
public static class AasOutputTableIndex
{
    public static Result<IReadOnlyDictionary<int, int>> Read(byte[] bytes, int maximumRecords = 100000)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (maximumRecords <= 0)
            return Result<IReadOnlyDictionary<int, int>>.Fail("Output table record limit must be positive.");
        try
        {
            var buffer = new CheckedFlatBuffer(bytes, new AasScenarioReaderOptions().MaxStringBytes);
            CheckedFlatBuffer.Table root = buffer.ReadRootTable();
            buffer.RejectPresentFieldsAfter(root, 1, "AAS root table");
            IReadOnlyList<CheckedFlatBuffer.Table> tables = buffer.ReadTableVector(root, 0, maximumRecords, "AAS DataList");
            var indices = new Dictionary<int, int>();
            for (int index = 0; index < tables.Count; index++)
            {
                int backwardsOffset = bytes.Length - tables[index].Position;
                if (!indices.TryAdd(backwardsOffset, index))
                    return Result<IReadOnlyDictionary<int, int>>.Fail("Output DataList repeats a table address.");
            }
            return Result<IReadOnlyDictionary<int, int>>.Ok(new ReadOnlyDictionary<int, int>(indices));
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or OverflowException)
        {
            return Result<IReadOnlyDictionary<int, int>>.Fail("Output table vector is invalid: " + ex.Message);
        }
    }
}
