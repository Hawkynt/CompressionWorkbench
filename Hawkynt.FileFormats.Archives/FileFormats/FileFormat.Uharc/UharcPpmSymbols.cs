namespace FileFormat.Uharc;

/// <summary>
/// What a PPM encoder codes (docs/UHARC-ON-DISK.md, "PPM", Text transform, and "Writing"): the
/// model symbols in order, which in a text stream are not one per output byte. The leading run of
/// text files becomes a text stream of length <see cref="TextLength"/>: each byte is its
/// complement, except that runs of spaces, common English letter groups and line ends become one
/// string symbol, a capitalised word starts with the capital-letter prefix, and a byte from 0x80
/// whose value the transform reserves goes through an escape pair. Everything after it, or the
/// whole stream when it does not start with text, is coded as binary: one complemented byte per
/// symbol.
/// </summary>
internal sealed class UharcPpmSymbols {

  private const byte LiteralClass = 0xC8;
  private const byte EscapeClass = 0x64;
  private const byte CapitalClass = 0x63;
  private const int EscapeTableStart = 166;
  private const int EscapeKinds = 10;
  private const int EscapeValues = 11;
  private const byte FirstEscapeKind = 0xA6;

  /// <summary>Strings by first output byte, longest first: (value coding it, its bytes).</summary>
  private static readonly (byte Value, byte[] Text)[][] StringsByFirst = BuildStrings();

  /// <summary>Per output byte from 0x80 that needs an escape: the kind and value that produce it.</summary>
  private static readonly (byte Kind, byte Value)[] Escapes = BuildEscapes();

  private readonly int[] _textStarts;

  private UharcPpmSymbols(byte[] symbols, long textLength, int[] textStarts) {
    this.Symbols = symbols;
    this.TextLength = textLength;
    this._textStarts = textStarts;
  }

  /// <summary>The model symbols, in coding order.</summary>
  public byte[] Symbols { get; }

  /// <summary>Output bytes the word transform covers (the stream's text length); 0 for a binary stream.</summary>
  public long TextLength { get; }

  /// <summary>Number of symbols of the text stream.</summary>
  public int TextSymbols => this._textStarts.Length - 1;

  /// <summary>A binary stream: one complemented byte per symbol.</summary>
  public static UharcPpmSymbols Binary(byte[] data) => new(Complement(data, 0, []), 0, [0]);

  /// <summary>
  /// A text stream over the leading files that look like text (empty files pass), when there are
  /// any, then binary; otherwise <see cref="Binary"/>. Strings never cross a file end.
  /// </summary>
  public static UharcPpmSymbols Plan(byte[] data, IReadOnlyList<long> fileEnds) {
    long textLength = 0;
    long start = 0;
    foreach (var end in fileEnds) {
      if (end > start && !IsText(data.AsSpan((int)start, (int)(end - start))))
        break;
      start = end;
      textLength = end;
    }

    if (textLength == 0)
      return Binary(data);

    var symbols = new List<byte>((int)textLength);
    var starts = new List<int>((int)textLength + 1);
    start = 0;
    foreach (var end in fileEnds) {
      if (end > textLength)
        break;
      Transform(data, (int)start, (int)end, symbols, starts);
      start = end;
    }

    starts.Add((int)textLength);
    return new(Complement(data, (int)textLength, symbols), textLength, [.. starts]);
  }

  /// <summary>Whether a file reads as text: no control bytes but tab, line ends, form feed and end-of-file, and few bytes from 0x80.</summary>
  internal static bool IsText(ReadOnlySpan<byte> file) {
    var high = 0;
    foreach (var b in file) {
      if (b < 0x20 && b is not (0x09 or 0x0A or 0x0C or 0x0D or 0x1A))
        return false;
      if (b >= 0x80)
        ++high;
    }

    return high * 16 <= file.Length;
  }

  /// <summary>The index of the first symbol that starts at or after output position <paramref name="position"/>.</summary>
  public int IndexAt(long position) {
    if (position >= this.TextLength)
      return (int)Math.Min(this.TextSymbols + (position - this.TextLength), this.Symbols.Length);

    var found = Array.BinarySearch(this._textStarts, (int)position);
    if (found >= 0) {
      // Prefix symbols produce nothing and share the position of the symbol they modify.
      while (found > 0 && this._textStarts[found - 1] == position)
        --found;
      return found;
    }

    return ~found;
  }

  /// <summary>
  /// The index a copy must stop before: the first symbol at or after <paramref name="limit"/> (the
  /// next reset point), or one symbol short of the end of the text — the window is rewritten
  /// there, so the last text symbol is left to a literal, which consumes any byte a copy
  /// excluded before the rewrite moves it.
  /// </summary>
  public int CopyEnd(long limit, long textEnd) => limit < textEnd ? this.IndexAt(limit) : this.IndexAt(textEnd) - 1;

  /// <summary>The output position at which symbol <paramref name="index"/> starts.</summary>
  public long StartOf(int index)
    => index < this.TextSymbols ? this._textStarts[index] : this.TextLength + (index - this.TextSymbols);

  private static byte[] Complement(byte[] data, int from, List<byte> prefix) {
    var symbols = new byte[prefix.Count + data.Length - from];
    prefix.CopyTo(symbols);
    for (int i = from, k = prefix.Count; i < data.Length; ++i, ++k)
      symbols[k] = (byte)~data[i];
    return symbols;
  }

  /// <summary>Turns the text bytes <c>[start, end)</c> into model symbols, recording where each starts.</summary>
  private static void Transform(byte[] data, int start, int end, List<byte> symbols, List<int> starts) {
    var p = start;
    while (p < end) {
      var b = data[p];
      if (b is >= (byte)'A' and <= (byte)'Z' && p + 1 < end && data[p + 1] is >= (byte)'a' and <= (byte)'z') {
        // A capitalised word: the prefix, then the word as if it were lower case.
        Add(symbols, starts, 0xFD, p);
        var (value, length) = Match(data, p, end, (byte)(b + 0x20));
        Add(symbols, starts, value, p);
        p += length;
        continue;
      }

      var (v, n) = Match(data, p, end, b);
      // A line end is a string (which the model's context then takes for a space) only inside
      // running text, where a lower-case word follows; elsewhere its bytes stay as they are.
      if (n > 0 && b is (byte)'\r' or (byte)'\n' && !(p + n < end && data[p + n] is >= (byte)'a' and <= (byte)'z'))
        n = 0;
      if (n > 0) {
        Add(symbols, starts, v, p);
        p += n;
        continue;
      }

      if (b < 0x80 || UharcPpmTables.TransformClass[b] >= LiteralClass)
        Add(symbols, starts, b, p);
      else {
        var (kind, escaped) = Escapes[b - 0x80];
        Add(symbols, starts, kind, p);
        Add(symbols, starts, escaped, p);
      }

      ++p;
    }
  }

  private static void Add(List<byte> symbols, List<int> starts, byte value, int position) {
    symbols.Add((byte)~value);
    starts.Add(position);
  }

  /// <summary>
  /// The longest string symbol matching the bytes at <paramref name="p"/> (its first byte taken as
  /// <paramref name="first"/>) within the file; for a capitalised word a single lower-case letter
  /// when no string starts the word. Length 0 when nothing matches.
  /// </summary>
  private static (byte Value, int Length) Match(byte[] data, int p, int end, byte first) {
    foreach (var (value, text) in StringsByFirst[first]) {
      if (p + text.Length > end)
        continue;
      var k = 1;
      while (k < text.Length && data[p + k] == text[k])
        ++k;
      if (k == text.Length)
        return (value, text.Length);
    }

    return first != data[p] ? (first, 1) : ((byte)0, 0);
  }

  private static (byte, byte[])[][] BuildStrings() {
    var byFirst = new List<(byte, byte[])>[256];
    for (var k = 0; k < 256; ++k)
      byFirst[k] = [];
    for (var value = 0x80; value < 0x100; ++value) {
      var kind = UharcPpmTables.TransformClass[value];
      if (kind >= CapitalClass)
        continue;
      var offset = UharcPpmTables.TransformStringOffset[kind];
      var text = new byte[UharcPpmTables.TransformStringLength[kind]];
      for (var i = 0; i < text.Length; ++i)
        text[i] = (byte)~UharcPpmTables.TransformStrings[offset + i];
      byFirst[text[0]].Add(((byte)value, text));
    }

    return [.. byFirst.Select(list => list.OrderByDescending(s => s.Item2.Length).ToArray())];
  }

  private static (byte, byte)[] BuildEscapes() {
    var escapes = new (byte, byte)[0x80];
    for (var n = 0; n < EscapeKinds * EscapeValues; ++n) {
      var b = UharcPpmTables.TransformEscape[EscapeTableStart + n];
      if (b >= 0x80)
        escapes[b - 0x80] = ((byte)(FirstEscapeKind + n / EscapeValues), (byte)(FirstEscapeKind + n % EscapeValues));
    }

    for (var b = 0x80; b < 0x100; ++b)
      if (UharcPpmTables.TransformClass[b] < LiteralClass && escapes[b - 0x80].Item1 == 0)
        throw new InvalidOperationException($"UHARC: no escape produces 0x{b:X2}.");
    return escapes;
  }
}
