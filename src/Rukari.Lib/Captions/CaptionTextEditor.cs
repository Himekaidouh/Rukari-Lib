namespace Rukari.Lib.Captions;

/// <summary>
/// The editing state of one caption line: where the caret is, what is selected, and what a keystroke does to the
/// text. It is a plain object with no engine types so the rules can be pinned by tests rather than by typing into
/// the game and watching, which is also why a text field can afford to be simple: the page forwards keys and draws
/// whatever this reports.
/// </summary>
public sealed class CaptionTextEditor
{
    private string _text = string.Empty;
    private int _caret;
    private bool _selectAll;

    /// <summary>The text being edited.</summary>
    public string Text => _text;

    /// <summary>Where the caret sits, from 0 (before the first character) to the text's length.</summary>
    public int Caret => Math.Clamp(_caret, 0, _text.Length);

    /// <summary>
    /// True when everything is selected. A field this small does not need a range: "select all" is the one
    /// selection the user actually makes, and it is what Ctrl+A means before typing a replacement.
    /// </summary>
    public bool SelectAll => _selectAll;

    /// <summary>Replaces the whole text and puts the caret at its end.</summary>
    public void Set(string? text)
    {
        _text = Clean(text);
        _caret = _text.Length;
        _selectAll = false;
    }

    /// <summary>Moves the caret, dropping any selection.</summary>
    public void MoveCaret(int delta)
    {
        _selectAll = false;
        _caret = Math.Clamp(Caret + delta, 0, _text.Length);
    }

    /// <summary>Puts the caret before the first character.</summary>
    public void Home()
    {
        _selectAll = false;
        _caret = 0;
    }

    /// <summary>Puts the caret after the last character.</summary>
    public void End()
    {
        _selectAll = false;
        _caret = _text.Length;
    }

    /// <summary>Selects everything, so the next character typed replaces the line.</summary>
    public void SelectAllText()
    {
        if (_text.Length == 0) return;
        _selectAll = true;
        _caret = _text.Length;
    }

    /// <summary>
    /// Types text in at the caret, replacing a selection. A newline is folded to a space: a caption is one line, and
    /// a pasted paragraph must not become an unrenderable one.
    /// </summary>
    public void Insert(string? value)
    {
        string clean = Clean(value).Replace('\n', ' ').Replace('\r', ' ');
        if (clean.Length == 0) return;
        if (_selectAll)
        {
            _text = "";
            _caret = 0;
            _selectAll = false;
        }
        int at = Caret;
        _text = _text[..at] + clean + _text[at..];
        _caret = at + clean.Length;
    }

    /// <summary>Deletes the character before the caret, a whole selected line, or nothing.</summary>
    public void Backspace()
    {
        if (_selectAll) { Clear(); return; }
        int at = Caret;
        if (at == 0) return;
        // A surrogate pair is one character and must not be cut in half.
        int remove = at >= 2 && char.IsLowSurrogate(_text[at - 1]) && char.IsHighSurrogate(_text[at - 2]) ? 2 : 1;
        _text = _text[..(at - remove)] + _text[at..];
        _caret = at - remove;
    }

    /// <summary>Deletes the character after the caret, a whole selected line, or nothing.</summary>
    public void Delete()
    {
        if (_selectAll) { Clear(); return; }
        int at = Caret;
        if (at >= _text.Length) return;
        int remove = at + 1 < _text.Length && char.IsHighSurrogate(_text[at]) && char.IsLowSurrogate(_text[at + 1]) ? 2 : 1;
        _text = _text[..at] + _text[(at + remove)..];
    }

    /// <summary>Empties the line and puts the caret at the start.</summary>
    public void Clear()
    {
        _text = "";
        _caret = 0;
        _selectAll = false;
    }

    /// <summary>
    /// What the field should draw: the line with the caret in it, and the characters the input method is still
    /// composing shown as a preview after the caret. A composing run is not part of the text yet — the input method
    /// may still replace it — so it is drawn but never stored.
    /// </summary>
    public string Display(string? composition = null, char caretGlyph = '｜')
    {
        string composing = Clean(composition);
        int at = Caret;
        string head = _text[..at];
        string tail = _text[at..];
        string selected = _selectAll && _text.Length != 0
            ? "【" + _text + "】"
            : head + caretGlyph + composing + tail;
        return selected;
    }

    /// <summary>Every control character out; a caption is drawn, not executed.</summary>
    private static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var kept = new System.Text.StringBuilder(value.Length);
        foreach (char character in value)
        {
            if (character == '\t') kept.Append(' ');
            else if (!char.IsControl(character) || character == '\n' || character == '\r') kept.Append(character);
        }
        return kept.ToString();
    }
}
