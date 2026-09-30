using System.Text;

namespace Rukari.Lib.Captions;

/// <summary>
/// The names inside a font file. A caption layer that wants a font the game does not ship has to load one from a
/// file, and the platform's font APIs take a family NAME rather than a path, so the name has to come out of the
/// file itself. Only the name table is read: a CJK font is twenty-odd megabytes, and reading all of it to learn
/// what it is called would cost more than the caption layer does in a minute.
/// </summary>
public sealed record TtfNames(string Family, string FamilyEnglish, string FullName, string PostScriptName)
{
    /// <summary>The name a font API is most likely to answer to, which is the localised one when there is one.</summary>
    public string Preferred => string.IsNullOrWhiteSpace(Family) ? FamilyEnglish : Family;
}

/// <summary>Reads the <c>name</c> table of a TrueType or OpenType file.</summary>
public static class TtfNameReader
{
    /// <summary>Reads the names out of a font file, touching only its header and its name table.</summary>
    public static bool TryReadFile(string path, out TtfNames names, out string error)
    {
        names = new TtfNames("", "", "", "");
        error = string.Empty;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
            return TryRead(reader, stream.Length, out names, out error);
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    /// <summary>Reads the names out of a font already in memory.</summary>
    public static bool TryRead(byte[] data, out TtfNames names, out string error)
    {
        names = new TtfNames("", "", "", "");
        error = string.Empty;
        if (data is null || data.Length < 12)
        {
            error = "The data is too short to be a font.";
            return false;
        }
        try
        {
            using var stream = new MemoryStream(data, writable: false);
            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
            return TryRead(reader, data.Length, out names, out error);
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    private static bool TryRead(BinaryReader reader, long length, out TtfNames names, out string error)
    {
        names = new TtfNames("", "", "", "");
        error = string.Empty;
        if (length < 12)
        {
            error = "The file is too short to be a font.";
            return false;
        }
        // Every field in a font header and its tables is big-endian, while a BinaryReader reads little-endian, so
        // the numbers are assembled here rather than taken from the reader.
        uint version = ReadUInt32(reader);
        // 0x00010000 is TrueType, 'OTTO' is CFF-flavoured OpenType, 'true'/'typ1' are older Apple flavours.
        bool recognizable = version is 0x00010000 or 0x4F54544F or 0x74727565 or 0x74797031;
        if (!recognizable)
        {
            error = $"The file does not start with a font signature (0x{version:X8}).";
            return false;
        }
        int tableCount = ReadUInt16(reader);
        ReadUInt16(reader);
        ReadUInt16(reader);
        ReadUInt16(reader);
        long nameOffset = 0;
        for (int i = 0; i < tableCount; i++)
        {
            if (reader.BaseStream.Position + 16 > length) break;
            string tag = Encoding.ASCII.GetString(reader.ReadBytes(4));
            ReadUInt32(reader);
            uint offset = ReadUInt32(reader);
            ReadUInt32(reader);
            if (tag == "name") nameOffset = offset;
        }
        if (nameOffset <= 0 || nameOffset + 6 > length)
        {
            error = "The font has no readable name table.";
            return false;
        }

        reader.BaseStream.Position = nameOffset;
        int format = ReadUInt16(reader);
        int count = ReadUInt16(reader);
        int stringOffset = ReadUInt16(reader);
        if (format > 1)
        {
            error = $"The name table uses format {format}, which is not supported.";
            return false;
        }
        string family = "", familyEnglish = "", full = "", postScript = "";
        // The records are walked by their own offset: reading a string moves the stream into the string area, so a
        // loop that simply carried on from there read rubbish for every record after the first one.
        long records = nameOffset + 6;
        for (int i = 0; i < count; i++)
        {
            long recordAt = records + i * 12L;
            if (recordAt + 12 > length) break;
            reader.BaseStream.Position = recordAt;
            int platform = ReadUInt16(reader);
            ReadUInt16(reader);
            int language = ReadUInt16(reader);
            int nameId = ReadUInt16(reader);
            int textLength = ReadUInt16(reader);
            int textOffset = ReadUInt16(reader);
            if (nameId is not (1 or 4 or 6)) continue;
            long at = nameOffset + stringOffset + textOffset;
            if (at < 0 || at + textLength > length || textLength > 1024) continue;
            reader.BaseStream.Position = at;
            string text = Decode(reader.ReadBytes(textLength), platform);
            if (text.Length == 0) continue;
            bool english = platform == 1 || language is 0x0409 or 0;
            switch (nameId)
            {
                case 1 when family.Length == 0 || english:
                    // A localised family name is what the platform's font API answers to on this machine; the
                    // English one is kept as the name to try when the localised spelling is not recognised.
                    if (family.Length == 0 || (english && familyEnglish.Length == 0)) family = text;
                    if (english) familyEnglish = text;
                    break;
                case 4 when full.Length == 0 || english:
                    full = text;
                    break;
                case 6 when postScript.Length == 0 || english:
                    postScript = text;
                    break;
            }
        }
        if (family.Length == 0 && familyEnglish.Length == 0 && full.Length == 0)
        {
            error = "The font's name table carries no family name.";
            return false;
        }
        if (familyEnglish.Length == 0) familyEnglish = family;
        names = new TtfNames(family, familyEnglish, full, postScript);
        return true;
    }

    private static int ReadUInt16(BinaryReader reader)
    {
        byte[] bytes = reader.ReadBytes(2);
        if (bytes.Length < 2) return 0;
        return (bytes[0] << 8) | bytes[1];
    }

    private static uint ReadUInt32(BinaryReader reader)
    {
        byte[] bytes = reader.ReadBytes(4);
        if (bytes.Length < 4) return 0;
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    /// <summary>
    /// Windows name records are UTF-16 big endian whatever the encoding id says; the older Mac platform is the one
    /// that really uses single-byte text.
    /// </summary>
    private static string Decode(byte[] bytes, int platform)
    {
        if (platform == 3) return Encoding.BigEndianUnicode.GetString(bytes).Trim('\0', ' ');
        var text = new StringBuilder(bytes.Length);
        foreach (byte value in bytes)
        {
            if (value == 0) break;
            text.Append((char)value);
        }
        return text.ToString().Trim();
    }
}
