namespace Rukari.Lib.Tools;

/// <summary>
/// One sprite record as published by the atlas export's <c>metadata\&lt;Atlas&gt;.json</c>: the source rectangle in
/// the atlas image, in top-left origin, plus the NGUI nine-slice border. This is the authoritative record — a
/// region copied by hand into a mod goes stale the moment the export is regenerated, a region read from here
/// cannot.
/// </summary>
/// <param name="Name">Sprite name inside the atlas, for example <c>Common_Bg_Raius10px</c>.</param>
/// <param name="X">Left edge of the source rectangle.</param>
/// <param name="Y">Top edge of the source rectangle, measured from the top of the atlas image.</param>
/// <param name="Width">Width of the source rectangle.</param>
/// <param name="Height">Height of the source rectangle.</param>
/// <param name="BorderLeft">Left nine-slice border; 0 means the sprite is drawn whole.</param>
/// <param name="BorderTop">Top nine-slice border; 0 means the sprite is drawn whole.</param>
/// <param name="BorderRight">Right nine-slice border; 0 means the sprite is drawn whole.</param>
/// <param name="BorderBottom">Bottom nine-slice border; 0 means the sprite is drawn whole.</param>
public readonly record struct AtlasSpriteRegion(
    string Name,
    int X,
    int Y,
    int Width,
    int Height,
    int BorderLeft,
    int BorderTop,
    int BorderRight,
    int BorderBottom)
{
    /// <summary>Whether any edge carries a border, meaning the sprite must be drawn nine-sliced rather than scaled.</summary>
    public bool HasBorder => BorderLeft > 0 || BorderTop > 0 || BorderRight > 0 || BorderBottom > 0;
}

/// <summary>
/// Reads the sprite table out of a decoded atlas export's metadata file. The file is produced by an external
/// exporter and only its shape matters here: a top-level object with a <c>sprites</c> array, each element naming
/// itself and carrying <c>x</c>, <c>y</c>, <c>width</c>, <c>height</c> and the four border fields. Unknown members
/// are ignored and field order is irrelevant, so an exporter upgrade that adds columns cannot break loading.
///
/// This is deliberately a small hand-written scanner instead of a JSON library: the schema is fixed, the input is
/// generated rather than hostile, and the mod must not take a dependency the game's managed profile may not carry.
/// </summary>
public static class AtlasSpriteCatalog
{
    private static readonly IReadOnlyDictionary<string, AtlasSpriteRegion> Empty =
        new Dictionary<string, AtlasSpriteRegion>(StringComparer.Ordinal);

    /// <summary>An empty catalog, for callers that want a valid value to fall back to.</summary>
    public static IReadOnlyDictionary<string, AtlasSpriteRegion> None => Empty;

    /// <summary>
    /// Parses a metadata document. Returns false, with a reason in <paramref name="error"/>, when the document
    /// carries no usable sprite table; regions without a name or with a non-positive size are skipped rather than
    /// failing the whole document.
    /// </summary>
    /// <param name="json">Contents of a <c>metadata\&lt;Atlas&gt;.json</c> file.</param>
    /// <param name="regions">Parsed regions, keyed by sprite name; empty on failure.</param>
    /// <param name="error">Human-readable reason when the result is false, otherwise null.</param>
    public static bool TryParse(
        string? json,
        out IReadOnlyDictionary<string, AtlasSpriteRegion> regions,
        out string? error)
    {
        regions = Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "document is empty";
            return false;
        }

        if (!TryFindMember(json!, "sprites", out string? array) || array is null)
        {
            error = "no 'sprites' member";
            return false;
        }

        IReadOnlyList<string> elements = ReadArray(array);
        if (elements.Count == 0)
        {
            error = "'sprites' carries no elements";
            return false;
        }

        var parsed = new Dictionary<string, AtlasSpriteRegion>(StringComparer.Ordinal);
        foreach (string element in elements)
        {
            if (!TryReadString(element, "name", out string name) || string.IsNullOrWhiteSpace(name)) continue;
            if (!TryReadInt(element, "x", out int x) || !TryReadInt(element, "y", out int y)) continue;
            if (!TryReadInt(element, "width", out int width) || !TryReadInt(element, "height", out int height)) continue;
            if (width <= 0 || height <= 0) continue;
            TryReadInt(element, "borderLeft", out int left);
            TryReadInt(element, "borderTop", out int top);
            TryReadInt(element, "borderRight", out int right);
            TryReadInt(element, "borderBottom", out int bottom);
            parsed.TryAdd(name, new AtlasSpriteRegion(name, x, y, width, height, left, top, right, bottom));
        }

        if (parsed.Count == 0)
        {
            error = "no usable sprite records";
            return false;
        }

        regions = parsed;
        error = null;
        return true;
    }

    private static bool TryReadString(string objectJson, string name, out string value)
    {
        value = string.Empty;
        if (!TryFindMember(objectJson, name, out string? raw) || raw is null || raw.Length < 2 || raw[0] != '"') return false;
        int index = 0;
        if (!ReadString(raw, ref index, out string text)) return false;
        value = text;
        return true;
    }

    private static bool TryReadInt(string objectJson, string name, out int value)
    {
        value = 0;
        if (!TryFindMember(objectJson, name, out string? raw) || raw is null) return false;
        return int.TryParse(raw.Trim(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Finds the first member of an object literal by name and returns its raw value text.</summary>
    private static bool TryFindMember(string objectJson, string name, out string? value)
    {
        value = null;
        int index = SkipWhitespace(objectJson, 0);
        if (index >= objectJson.Length || objectJson[index] != '{') return false;
        index++;
        while (index < objectJson.Length)
        {
            index = SkipWhitespace(objectJson, index);
            if (index >= objectJson.Length || objectJson[index] == '}') return false;
            if (objectJson[index] == ',')
            {
                index++;
                continue;
            }
            if (objectJson[index] != '"') return false;
            if (!ReadString(objectJson, ref index, out string key)) return false;
            index = SkipWhitespace(objectJson, index);
            if (index >= objectJson.Length || objectJson[index] != ':') return false;
            index = SkipWhitespace(objectJson, index + 1);
            if (!ReadValue(objectJson, ref index, out string raw)) return false;
            if (string.Equals(key, name, StringComparison.Ordinal))
            {
                value = raw;
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns the raw text of every element of an array literal; empty when the text is not an array.</summary>
    private static IReadOnlyList<string> ReadArray(string arrayJson)
    {
        var elements = new List<string>();
        int index = SkipWhitespace(arrayJson, 0);
        if (index >= arrayJson.Length || arrayJson[index] != '[') return elements;
        index++;
        while (index < arrayJson.Length)
        {
            index = SkipWhitespace(arrayJson, index);
            if (index >= arrayJson.Length || arrayJson[index] == ']') break;
            if (arrayJson[index] == ',')
            {
                index++;
                continue;
            }
            if (!ReadValue(arrayJson, ref index, out string raw)) break;
            elements.Add(raw);
        }

        return elements;
    }

    /// <summary>
    /// Reads one JSON value starting at <paramref name="index"/>, advancing past it. Strings are returned quoted,
    /// exactly as they appear, so callers can decide whether to unquote them.
    /// </summary>
    private static bool ReadValue(string json, ref int index, out string raw)
    {
        raw = string.Empty;
        if (index >= json.Length) return false;
        int start = index;
        char c = json[index];
        if (c == '"')
        {
            if (!ReadString(json, ref index, out _)) return false;
            raw = json.Substring(start, index - start);
            return true;
        }

        if (c == '{' || c == '[')
        {
            char close = c == '{' ? '}' : ']';
            int depth = 0;
            while (index < json.Length)
            {
                char current = json[index];
                if (current == '"')
                {
                    if (!ReadString(json, ref index, out _)) return false;
                    continue;
                }

                if (current == c) depth++;
                else if (current == close)
                {
                    depth--;
                    index++;
                    if (depth == 0)
                    {
                        raw = json.Substring(start, index - start);
                        return true;
                    }

                    continue;
                }

                index++;
            }

            return false;
        }

        // Number, true, false or null: everything up to the next structural character.
        while (index < json.Length && json[index] != ',' && json[index] != '}' && json[index] != ']') index++;
        raw = json.Substring(start, index - start);
        return raw.Trim().Length > 0;
    }

    /// <summary>Reads a quoted string, advancing past the closing quote and decoding the common escapes.</summary>
    private static bool ReadString(string json, ref int index, out string text)
    {
        text = string.Empty;
        if (index >= json.Length || json[index] != '"') return false;
        index++;
        var builder = new System.Text.StringBuilder();
        while (index < json.Length)
        {
            char c = json[index++];
            if (c == '"')
            {
                text = builder.ToString();
                return true;
            }

            if (c != '\\')
            {
                builder.Append(c);
                continue;
            }

            if (index >= json.Length) return false;
            char escape = json[index++];
            switch (escape)
            {
                case 'n': builder.Append('\n'); break;
                case 'r': builder.Append('\r'); break;
                case 't': builder.Append('\t'); break;
                case 'b': builder.Append('\b'); break;
                case 'f': builder.Append('\f'); break;
                case 'u':
                    if (index + 4 > json.Length) return false;
                    if (!ushort.TryParse(json.Substring(index, 4), System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out ushort code)) return false;
                    builder.Append((char)code);
                    index += 4;
                    break;
                default: builder.Append(escape); break;
            }
        }

        return false;
    }

    private static int SkipWhitespace(string json, int index)
    {
        while (index < json.Length && char.IsWhiteSpace(json[index])) index++;
        return index;
    }
}
