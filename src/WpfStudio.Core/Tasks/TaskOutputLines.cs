using System.Text;

namespace WpfStudio.Core.Tasks;

/// <summary>
/// Turns a pseudoconsole's output back into plain lines of text, for reading problems out of it. Colours and other
/// escape sequences are dropped; a cursor move to another row ends the line, as a repaint does, and cursor-forward
/// steps are the spaces they skip. A carriage return on its own starts the line again, as a progress counter does.
/// One instance reads one stream, from one thread.
/// </summary>
public sealed class TaskOutputLines
{
    private const int MaximumLineLength = 16_384;
    private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
    private readonly StringBuilder _line = new();
    private readonly StringBuilder _sequence = new();
    private readonly Action<string> _completed;
    private State _state;
    private bool _returned;
    private char[] _chars = new char[4096];

    private enum State { Text, Escape, Csi, Osc, OscEscape, Charset }

    /// <param name="completed">Receives each finished line, without its line break.</param>
    public TaskOutputLines(Action<string> completed) => _completed = completed;

    public void Write(ReadOnlySpan<byte> bytes)
    {
        var needed = _decoder.GetCharCount(bytes, flush: false);
        if (needed > _chars.Length) _chars = new char[Math.Max(needed, _chars.Length * 2)];
        var count = _decoder.GetChars(bytes, _chars, flush: false);
        foreach (var c in _chars.AsSpan(0, count)) Read(c);
    }

    /// <summary>Hands over the last line when the stream ends without a line break.</summary>
    public void Flush() { if (_line.Length > 0) Complete(); }

    private void Read(char c)
    {
        switch (_state)
        {
            case State.Text: Text(c); break;
            case State.Escape:
                _state = c switch { '[' => State.Csi, ']' => State.Osc, '(' or ')' or '*' or '+' => State.Charset, _ => State.Text };
                _sequence.Clear();
                break;
            case State.Charset: _state = State.Text; break;
            case State.Osc:
                if (c == '\a') _state = State.Text;
                else if (c == '\u001b') _state = State.OscEscape;
                break;
            case State.OscEscape: _state = c == '\\' ? State.Text : State.Osc; break;
            case State.Csi:
                if (c is >= '@' and <= '~') { Control(c, _sequence.ToString()); _state = State.Text; }
                else if (_sequence.Length < 32) _sequence.Append(c);
                break;
        }
    }

    private void Text(char c)
    {
        switch (c)
        {
            case '\u001b': _state = State.Escape; return;
            case '\n': Complete(); return;
            case '\r': _returned = true; return;
            case '\b': if (_line.Length > 0) _line.Length--; return;
            case '\t': Append(' '); return;
        }
        if (char.IsControl(c)) return;
        Append(c);
    }

    private void Append(char c)
    {
        // A carriage return not followed by a line feed means the line is written again from its start.
        if (_returned) { _returned = false; _line.Clear(); }
        if (_line.Length < MaximumLineLength) _line.Append(c);
    }

    private void Control(char final, string parameters)
    {
        switch (final)
        {
            // Cursor position and cursor up or down: the following text belongs to another row.
            case 'H' or 'f' or 'A' or 'B' or 'E' or 'F' or 'd':
                if (_line.Length > 0) Complete();
                else _returned = false;
                break;
            case 'C':
                var count = int.TryParse(parameters, out var steps) && steps > 0 ? Math.Min(steps, 512) : 1;
                for (var i = 0; i < count; i++) Append(' ');
                break;
            case 'G' when parameters is "" or "1":
                _returned = true;
                break;
        }
    }

    private void Complete()
    {
        _returned = false;
        var line = _line.ToString().TrimEnd();
        _line.Clear();
        _completed(line);
    }
}
