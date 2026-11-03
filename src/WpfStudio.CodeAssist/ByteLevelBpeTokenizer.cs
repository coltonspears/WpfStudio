using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WpfStudio.CodeAssist;

/// <summary>
/// Reads the byte-level BPE tokenizer.json the line models are trained with, and gives the same tokens as the Hugging Face
/// tokenizers library that wrote it. Only the settings those models use are accepted, so another format fails on load
/// instead of quietly producing different tokens.
/// </summary>
internal sealed partial class ByteLevelBpeTokenizer
{
    private static readonly (char[] ToChar, Dictionary<char, byte> ToByte) ByteAlphabet = BuildByteAlphabet();
    private readonly Dictionary<string, int> _vocabulary;
    private readonly string[] _tokens;
    private readonly Dictionary<(string Left, string Right), int> _merges;
    private readonly Dictionary<string, int> _special = new(StringComparer.Ordinal);
    private readonly Regex? _specialSplit;

    [GeneratedRegex(@"'s|'t|'re|'ve|'m|'ll|'d| ?\p{L}+| ?\p{N}+| ?[^\s\p{L}\p{N}]+|\s+(?!\S)|\s+", RegexOptions.None, 1000)]
    private static partial Regex Pieces();

    private ByteLevelBpeTokenizer(Dictionary<string, int> vocabulary, Dictionary<(string, string), int> merges, IEnumerable<string> special)
    {
        _vocabulary = vocabulary; _merges = merges;
        _tokens = new string[vocabulary.Count == 0 ? 0 : vocabulary.Values.Max() + 1];
        foreach (var (token, id) in vocabulary) _tokens[id] = token;
        foreach (string token in special) _special[token] = vocabulary[token];
        if (_special.Count > 0) _specialSplit = new Regex("(" + string.Join('|', _special.Keys.Select(Regex.Escape)) + ")", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    public static ByteLevelBpeTokenizer Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        var model = root.GetProperty("model");
        var pre = root.GetProperty("pre_tokenizer");
        if (model.GetProperty("type").GetString() != "BPE" || pre.GetProperty("type").GetString() != "ByteLevel" || !pre.GetProperty("use_regex").GetBoolean() || pre.GetProperty("add_prefix_space").GetBoolean()
            || root.GetProperty("normalizer").ValueKind != JsonValueKind.Null || model.GetProperty("continuing_subword_prefix").ValueKind != JsonValueKind.Null || model.GetProperty("end_of_word_suffix").ValueKind != JsonValueKind.Null)
            throw new InvalidDataException("The tokenizer is not the byte-level BPE this build reads.");
        var vocabulary = model.GetProperty("vocab").EnumerateObject().ToDictionary(entry => entry.Name, entry => entry.Value.GetInt32(), StringComparer.Ordinal);
        var merges = new Dictionary<(string, string), int>();
        foreach (var merge in model.GetProperty("merges").EnumerateArray())
            merges[(merge[0].GetString()!, merge[1].GetString()!)] = merges.Count;
        var special = root.GetProperty("added_tokens").EnumerateArray().Where(token => token.GetProperty("special").GetBoolean()).Select(token => token.GetProperty("content").GetString()!);
        return new ByteLevelBpeTokenizer(vocabulary, merges, special);
    }

    public int VocabularySize => _tokens.Length;

    public List<int> Encode(string text)
    {
        var ids = new List<int>(text.Length / 2);
        foreach (string segment in _specialSplit?.Split(text) ?? [text])
        {
            if (_special.TryGetValue(segment, out int special)) { ids.Add(special); continue; }
            foreach (Match piece in Pieces().Matches(segment)) AppendPiece(piece.Value, ids);
        }
        return ids;
    }

    /// <summary>The text of these tokens. A token ending partway through a character shows as U+FFFD until the next token completes it.</summary>
    public string Decode(IEnumerable<int> ids)
    {
        var bytes = new List<byte>();
        foreach (int id in ids)
            foreach (char symbol in _tokens[id])
            {
                if (ByteAlphabet.ToByte.TryGetValue(symbol, out byte value)) bytes.Add(value);
                else bytes.AddRange(Encoding.UTF8.GetBytes(symbol.ToString()));
            }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    // Starts from single bytes and keeps joining the adjacent pair learned earliest, until no learned pair remains.
    private void AppendPiece(string piece, List<int> ids)
    {
        var word = new List<string>();
        foreach (byte value in Encoding.UTF8.GetBytes(piece)) word.Add(ByteAlphabet.ToChar[value].ToString());
        while (word.Count > 1)
        {
            (string Left, string Right) best = default;
            int bestRank = int.MaxValue;
            for (int i = 0; i + 1 < word.Count; i++)
                if (_merges.TryGetValue((word[i], word[i + 1]), out int rank) && rank < bestRank) { bestRank = rank; best = (word[i], word[i + 1]); }
            if (bestRank == int.MaxValue) break;
            for (int i = 0; i + 1 < word.Count; i++)
            {
                if (word[i] != best.Left || word[i + 1] != best.Right) continue;
                word[i] = best.Left + best.Right;
                word.RemoveAt(i + 1);
            }
        }
        foreach (string symbol in word)
            if (_vocabulary.TryGetValue(symbol, out int id)) ids.Add(id);
    }

    // GPT-2's table: printable bytes stand for themselves, the rest borrow characters above 255 so that a token is always visible text.
    private static (char[], Dictionary<char, byte>) BuildByteAlphabet()
    {
        var toChar = new char[256];
        var toByte = new Dictionary<char, byte>(256);
        int next = 256;
        for (int value = 0; value < 256; value++)
        {
            bool printable = value is (>= '!' and <= '~') or (>= 0xA1 and <= 0xAC) or (>= 0xAE and <= 0xFF);
            char symbol = printable ? (char)value : (char)next++;
            toChar[value] = symbol;
            toByte[symbol] = (byte)value;
        }
        return (toChar, toByte);
    }
}
