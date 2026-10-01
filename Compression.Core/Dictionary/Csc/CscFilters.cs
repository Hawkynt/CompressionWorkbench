namespace Compression.Core.Dictionary.Csc;

/// <summary>
/// The reversible preprocessors libcsc applies per block: Eugene Shelwien's E8/E9 call-address
/// transform, a fixed English word substitution into the byte range 0x82-0xFB, and the channel-wise
/// delta used for data tables.
/// </summary>
internal static class CscFilters {
  private const byte FirstWordSymbol = 0x82;
  private const byte EscapeSymbol = 254;

  /// <summary>
  /// The substitution dictionary (index 0 unused); word <c>n</c> becomes symbol <c>0x81 + n</c>.
  /// The list is part of the format.
  /// </summary>
  private static readonly string[] _words = [
    "",
    "ac", "ad", "ai", "al", "am",
    "an", "ar", "as", "at", "ea",
    "ec", "ed", "ee", "el", "en",
    "er", "es", "et", "id", "ie",
    "ig", "il", "in", "io", "is",
    "it", "of", "ol", "on", "oo",
    "or", "os", "ou", "ow", "ul",
    "un", "ur", "us", "ba", "be",
    "ca", "ce", "co", "ch", "de",
    "di", "ge", "gh", "ha", "he",
    "hi", "ho", "ra", "re", "ri",
    "ro", "rs", "la", "le", "li",
    "lo", "ld", "ll", "ly", "se",
    "si", "so", "sh", "ss", "st",
    "ma", "me", "mi", "ne", "nc",
    "nd", "ng", "nt", "pa", "pe",
    "ta", "te", "ti", "to", "th",
    "tr", "wa", "ve",
    "all", "and", "but", "dow",
    "for", "had", "hav", "her",
    "him", "his", "man", "mor",
    "not", "now", "one", "out",
    "she", "the", "was", "wer",
    "whi", "whe", "wit", "you",
    "any", "are",
    "that", "said", "with", "have",
    "this", "from", "were", "tion",
  ];

  private static readonly WordTrie _trie = new();

  /// <summary>One past the last word symbol.</summary>
  private static byte EndSymbol => (byte)(FirstWordSymbol + _words.Length - 1);

  /// <summary>Applies the E8/E9 transform in place (encoder side).</summary>
  public static void ForwardE89(Span<byte> data) => new E89State().Run(data, forward: true);

  /// <summary>Reverts the E8/E9 transform in place (decoder side).</summary>
  public static void InverseE89(Span<byte> data) => new E89State().Run(data, forward: false);

  /// <summary>
  /// Replaces dictionary words in place when that shrinks the block below 82 %; the freed tail is
  /// filled with spaces. Blocks under 16 KiB are left alone.
  /// </summary>
  /// <returns>Whether the block was transformed.</returns>
  public static bool ForwardDictionary(Span<byte> data) {
    var size = data.Length;
    if (size < 16384)
      return false;

    var output = new byte[size];
    var count = 0;
    var i = 0;
    for (; i < size - 5;) {
      // libcsc gives up once the output gets within 16 bytes of the input size; such a block
      // would miss the 82 % target anyway.
      if (count > size - 16)
        return false;

      var b = data[i];
      if (b is >= (byte)'a' and <= (byte)'z') {
        var (symbol, length) = _trie.LongestMatch(data[i..]);
        if (symbol != 0) {
          output[count++] = symbol;
          i += length;
          continue;
        }

        output[count++] = b;
        ++i;
        continue;
      }

      if (b >= FirstWordSymbol)
        output[count++] = EscapeSymbol;
      output[count++] = b;
      ++i;
    }

    for (; i < size; ++i) {
      if (data[i] >= FirstWordSymbol)
        output[count++] = EscapeSymbol;
      output[count++] = data[i];
    }

    if (count > size * 0.82)
      return false;

    output.AsSpan(count).Fill((byte)' ');
    output.CopyTo(data);
    return true;
  }

  /// <summary>Expands dictionary symbols in place; the result keeps the block's length.</summary>
  /// <exception cref="InvalidDataException">The block runs out before it is fully expanded.</exception>
  public static void InverseDictionary(Span<byte> data) {
    var size = data.Length;
    var output = new byte[size];
    var end = EndSymbol;
    int i = 0, count = 0;
    while (count < size) {
      if (i >= size)
        throw new InvalidDataException("CSC text block expands past its own end.");

      var b = data[i];
      if (b >= FirstWordSymbol && b < end) {
        var word = _words[b - FirstWordSymbol + 1];
        for (var j = 0; j < word.Length && count < size; ++j)
          output[count++] = (byte)word[j];
      } else if (b == EscapeSymbol && i + 1 < size && data[i + 1] >= FirstWordSymbol)
        output[count++] = data[++i];
      else
        output[count++] = b;
      ++i;
    }

    output.CopyTo(data);
  }

  /// <summary>Splits the block into <paramref name="channels"/> interleaved channels and stores successive differences (blocks under 512 bytes are left alone).</summary>
  public static void ForwardDelta(Span<byte> data, int channels) {
    if (data.Length < 512)
      return;

    var copy = data.ToArray();
    var count = 0;
    byte previous = 0;
    for (var i = 0; i < channels; ++i)
      for (var j = i; j < copy.Length; j += channels) {
        data[count++] = (byte)(copy[j] - previous);
        previous = copy[j];
      }
  }

  /// <summary>Reverts <see cref="ForwardDelta"/>.</summary>
  public static void InverseDelta(Span<byte> data, int channels) {
    if (data.Length < 512)
      return;

    var copy = data.ToArray();
    var count = 0;
    byte previous = 0;
    for (var i = 0; i < channels; ++i)
      for (var j = i; j < data.Length; j += channels) {
        previous = (byte)(copy[count++] + previous);
        data[j] = previous;
      }
  }

  /// <summary>Lower-case word trie built from <see cref="_words"/>.</summary>
  private sealed class WordTrie {
    private readonly List<(int[] Next, byte Symbol)> _nodes = [(new int[26], 0)];

    public WordTrie() {
      var symbol = FirstWordSymbol;
      for (var w = 1; w < _words.Length; ++w) {
        var node = 0;
        foreach (var ch in _words[w]) {
          var index = ch - 'a';
          if (this._nodes[node].Next[index] == 0) {
            this._nodes[node].Next[index] = this._nodes.Count;
            this._nodes.Add((new int[26], 0));
          }

          node = this._nodes[node].Next[index];
        }

        this._nodes[node] = this._nodes[node] with { Symbol = symbol++ };
      }
    }

    /// <summary>The symbol and length of the longest dictionary word at the start of <paramref name="text"/>.</summary>
    public (byte Symbol, int Length) LongestMatch(ReadOnlySpan<byte> text) {
      byte symbol = 0;
      int length = 0, node = 0;
      for (var j = 0; j < text.Length; ++j) {
        var index = text[j] - 'a';
        if ((uint)index > 25 || this._nodes[node].Next[index] == 0)
          break;

        node = this._nodes[node].Next[index];
        if (this._nodes[node].Symbol != 0) {
          symbol = this._nodes[node].Symbol;
          length = j + 1;
        }
      }

      return (symbol, length);
    }
  }

  /// <summary>
  /// State machine of Shelwien's E8/E9 filter: an 8-byte delay line (x1:x0) in which a 0xE8/0xE9
  /// opcode followed by a near 32-bit offset gets that offset made position-absolute (forward) or
  /// relative again (inverse), with the offset bytes rotated for better modelling.
  /// </summary>
  private struct E89State {
    private uint _x0;
    private uint _x1;
    private uint _i;
    private uint _k;
    private byte _cacheMask;

    public void Run(Span<byte> data, bool forward) {
      this.Init();
      var j = 0;
      foreach (var b in data) {
        var c = forward ? this.Forward(b) : this.Inverse(b);
        if (c >= 0)
          data[j++] = (byte)c;
      }

      int d;
      while ((d = this.Flush()) >= 0)
        data[j++] = (byte)d;
    }

    private void Init() {
      this._cacheMask = 0xFF;
      this._x0 = this._x1 = 0;
      this._i = 0;
      this._k = 5;
    }

    private int CacheByte(int c) {
      var d = (this._cacheMask & 0x80) != 0 ? -1 : (byte)this._x1;
      this._x1 = (this._x1 >> 8) | (this._x0 << 24);
      this._x0 = (this._x0 >> 8) | ((uint)c << 24);
      this._cacheMask <<= 1;
      ++this._i;
      return d;
    }

    private static uint SwapForward(uint x) {
      x <<= 7;
      return (x >> 24) | ((uint)(byte)(x >> 16) << 8) | ((uint)(byte)(x >> 8) << 16) | ((uint)(byte)x << (24 - 7));
    }

    private static uint SwapInverse(uint x) {
      x = ((uint)(byte)(x >> 24) << 7) | ((uint)(byte)(x >> 16) << 8) | ((uint)(byte)(x >> 8) << 16) | (x << 24);
      return x >> 7;
    }

    private int Forward(int c) {
      if (this._i >= this._k && (this._x1 & 0xFE000000) == 0xE8000000) {
        this._k = this._i + 4;
        var x = this._x0 - 0xFF000000;
        if (x < 0x02000000) {
          x = (x + this._i) & 0x01FFFFFF;
          this._x0 = SwapForward(x) + 0xFF000000;
        }
      }

      return this.CacheByte(c);
    }

    private int Inverse(int c) {
      if (this._i >= this._k && (this._x1 & 0xFE000000) == 0xE8000000) {
        this._k = this._i + 4;
        var x = this._x0 - 0xFF000000;
        if (x < 0x02000000) {
          x = (SwapInverse(x) - this._i) & 0x01FFFFFF;
          this._x0 = x + 0xFF000000;
        }
      }

      return this.CacheByte(c);
    }

    private int Flush() {
      if (this._cacheMask == 0xFF) {
        this.Init();
        return -1;
      }

      while ((this._cacheMask & 0x80) != 0) {
        this.CacheByte(0);
        ++this._cacheMask;
      }

      var d = this.CacheByte(0);
      ++this._cacheMask;
      return d;
    }
  }
}
