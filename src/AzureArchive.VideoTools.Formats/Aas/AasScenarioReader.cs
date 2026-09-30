using System.Security.Cryptography;
using System.Text;
using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Formats.Aas;

public sealed class AasScenarioReader : IPlaybackArchiveReader
{
    public const string CurrentSchemaName = "GenericScenarioExcelTable/v1";

    private const int KnownRootFieldCount = 1;
    private const int KnownRecordFieldCount = 16;

    private readonly AasScenarioReaderOptions _options;

    public AasScenarioReader(AasScenarioReaderOptions? options = null)
    {
        _options = options ?? new AasScenarioReaderOptions();
        ValidateOptions(_options);
    }

    public Result<PlaybackArchiveSnapshot> Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Result<PlaybackArchiveSnapshot>.Fail("AAS path is empty.");
        }

        try
        {
            string fullPath = Path.GetFullPath(path);
            if (!string.Equals(
                    Path.GetExtension(fullPath),
                    ".aas",
                    StringComparison.OrdinalIgnoreCase))
            {
                return Result<PlaybackArchiveSnapshot>.Fail(
                    "Playback archive path must use the .aas extension.");
            }

            if (!File.Exists(fullPath))
            {
                return Result<PlaybackArchiveSnapshot>.Fail(
                    $"AAS file does not exist: {fullPath}");
            }

            Result<StableFileSnapshot> fileResult = ReadStable(fullPath);
            if (!fileResult.Success || fileResult.Value == null)
            {
                return Result<PlaybackArchiveSnapshot>.Fail(fileResult.Error);
            }

            StableFileSnapshot file = fileResult.Value;
            var buffer = new CheckedFlatBuffer(file.Bytes, _options.MaxStringBytes);
            CheckedFlatBuffer.Table root = buffer.ReadRootTable();
            buffer.RejectPresentFieldsAfter(root, KnownRootFieldCount, "AAS root table");
            IReadOnlyList<CheckedFlatBuffer.Table> tables = buffer.ReadTableVector(
                root,
                fieldIndex: 0,
                _options.MaxRecords,
                "AAS DataList");

            var records = new PlaybackRecordSnapshot[tables.Count];
            for (int index = 0; index < tables.Count; index++)
            {
                records[index] = ParseRecord(buffer, tables[index], index);
            }

            var source = new PlaybackArchiveSourceSnapshot(
                fullPath,
                ComputePathKey(fullPath),
                Convert.ToHexString(SHA256.HashData(file.Bytes)),
                file.Bytes.LongLength,
                file.LastWriteTimeUtc);
            return Result<PlaybackArchiveSnapshot>.Ok(new PlaybackArchiveSnapshot(
                source,
                CurrentSchemaName,
                Array.AsReadOnly(records)));
        }
        catch (InvalidDataException ex)
        {
            return Result<PlaybackArchiveSnapshot>.Fail(
                $"AAS FlatBuffer is invalid or unsupported: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Result<PlaybackArchiveSnapshot>.Fail(
                $"AAS file cannot be accessed: {ex.Message}");
        }
        catch (IOException ex)
        {
            return Result<PlaybackArchiveSnapshot>.Fail(
                $"AAS file could not be read: {ex.Message}");
        }
        catch (Exception ex)
        {
            return Result<PlaybackArchiveSnapshot>.Fail(
                $"Unexpected AAS read failure ({ex.GetType().Name}): {ex.Message}");
        }
    }

    private static PlaybackRecordSnapshot ParseRecord(
        CheckedFlatBuffer buffer,
        CheckedFlatBuffer.Table table,
        int index)
    {
        string context = $"AAS record {index}";
        buffer.RejectPresentFieldsAfter(table, KnownRecordFieldCount, context);

        long groupId = buffer.ReadInt64(table, 0, $"{context} GroupId");
        long selectionGroup = buffer.ReadInt64(table, 1, $"{context} SelectionGroup");
        long bgmId = buffer.ReadInt64(table, 2, $"{context} BGMId");
        string sound = buffer.ReadString(table, 3, $"{context} Sound");
        uint transition = buffer.ReadUInt32(table, 4, $"{context} Transition");
        uint backgroundName = buffer.ReadUInt32(table, 5, $"{context} BGName");
        uint backgroundEffect = buffer.ReadUInt32(table, 6, $"{context} BGEffect");
        string popup = buffer.ReadString(table, 7, $"{context} PopupFileName");
        string compiledScript = buffer.ReadString(table, 8, $"{context} ScriptKr");
        string textJp = buffer.ReadString(table, 9, $"{context} TextJp");
        string textTh = buffer.ReadString(table, 10, $"{context} TextTh");
        string textTw = buffer.ReadString(table, 11, $"{context} TextTw");
        string textCn = buffer.ReadString(table, 12, $"{context} TextCn");
        string textEn = buffer.ReadString(table, 13, $"{context} TextEn");
        string voiceJp = buffer.ReadString(table, 14, $"{context} VoiceJp");
        bool teenMode = buffer.ReadBoolean(table, 15, $"{context} TeenMode");

        string fingerprint = ComputeFingerprint(
            groupId,
            selectionGroup,
            bgmId,
            sound,
            transition,
            backgroundName,
            backgroundEffect,
            popup,
            compiledScript,
            textJp,
            textTh,
            textTw,
            textCn,
            textEn,
            voiceJp,
            teenMode);

        return new PlaybackRecordSnapshot(
            index,
            groupId,
            selectionGroup,
            bgmId,
            sound,
            transition,
            backgroundName,
            backgroundEffect,
            popup,
            compiledScript,
            textJp,
            textTh,
            textTw,
            textCn,
            textEn,
            voiceJp,
            teenMode,
            fingerprint);
    }

    private Result<StableFileSnapshot> ReadStable(string fullPath)
    {
        for (int attempt = 1; attempt <= _options.StableReadAttempts; attempt++)
        {
            var before = new FileInfo(fullPath);
            before.Refresh();
            if (before.Length <= 0)
            {
                return Result<StableFileSnapshot>.Fail("AAS file is empty.");
            }

            if (before.Length > _options.MaxFileSizeBytes)
            {
                return Result<StableFileSnapshot>.Fail(
                    $"AAS file is {before.Length} bytes, exceeding the {_options.MaxFileSizeBytes}-byte read limit.");
            }

            byte[] bytes;
            using (var stream = new FileStream(
                       fullPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            {
                using var buffer = new MemoryStream((int)before.Length);
                stream.CopyTo(buffer);
                bytes = buffer.ToArray();
            }

            var after = new FileInfo(fullPath);
            after.Refresh();
            if (before.Length == after.Length
                && before.LastWriteTimeUtc == after.LastWriteTimeUtc
                && bytes.LongLength == after.Length)
            {
                return Result<StableFileSnapshot>.Ok(new StableFileSnapshot(
                    bytes,
                    new DateTimeOffset(after.LastWriteTimeUtc, TimeSpan.Zero)));
            }
        }

        return Result<StableFileSnapshot>.Fail(
            "AAS file changed while it was being read. Wait for compilation to finish and retry.");
    }

    private static string ComputeFingerprint(
        long groupId,
        long selectionGroup,
        long bgmId,
        string sound,
        uint transition,
        uint backgroundName,
        uint backgroundEffect,
        string popup,
        string compiledScript,
        string textJp,
        string textTh,
        string textTw,
        string textCn,
        string textEn,
        string voiceJp,
        bool teenMode)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(groupId);
            writer.Write(selectionGroup);
            writer.Write(bgmId);
            writer.Write(sound);
            writer.Write(transition);
            writer.Write(backgroundName);
            writer.Write(backgroundEffect);
            writer.Write(popup);
            writer.Write(compiledScript);
            writer.Write(textJp);
            writer.Write(textTh);
            writer.Write(textTw);
            writer.Write(textCn);
            writer.Write(textEn);
            writer.Write(voiceJp);
            writer.Write(teenMode);
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static string ComputePathKey(string fullPath)
    {
        string normalized = Path.GetFullPath(fullPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (OperatingSystem.IsWindows())
        {
            normalized = normalized.ToUpperInvariant();
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    private static void ValidateOptions(AasScenarioReaderOptions options)
    {
        if (options.MaxFileSizeBytes <= 0 || options.MaxFileSizeBytes > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Maximum AAS file size must be between 1 and Int32.MaxValue bytes.");
        }

        if (options.MaxRecords <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Maximum AAS record count must be positive.");
        }

        if (options.MaxStringBytes <= 0 || options.MaxStringBytes >= int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Maximum AAS string size must be between 1 and Int32.MaxValue - 1 bytes.");
        }

        if (options.StableReadAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Stable AAS read attempts must be positive.");
        }
    }

    private sealed record StableFileSnapshot(
        byte[] Bytes,
        DateTimeOffset LastWriteTimeUtc);
}
