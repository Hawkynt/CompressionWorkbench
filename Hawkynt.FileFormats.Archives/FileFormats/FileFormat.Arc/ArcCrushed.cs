namespace FileFormat.Arc;

/// <summary>
/// PAK method 10, "Crushed" (NoGate Consulting's PAK 1.x/2.x): LZW over an 8192-entry table that,
/// once full, reuses its least-recently-used entries, read least-significant bit first.
/// </summary>
/// <remarks>
/// <para>
/// PAK first packs runs with RLE90, as ARC's method 8 does, then crushes the result. This class undoes
/// the LZW layer only; <see cref="ArcRle"/> undoes the runs.
/// </para>
/// <para>
/// Code 256 ends the stream; entries 257 and up are strings. Codes come in one of two modes. While
/// fewer than 375 of the last 500 codes were strings, each code is a flag bit followed by either an
/// 8-bit literal (flag 0) or a string code of the current width (flag 1, value + 256). Otherwise every
/// code has the current width, and literals are stored with their bits inverted (value XOR 0xFF).
/// The width starts at 1 bit and grows by one when the number of strings defined reaches the
/// mode's threshold: 2^width with the flag bit, 2^width − 256 without. Switching mode recomputes
/// the threshold for the current width.
/// </para>
/// <para>
/// Each code marks itself and every prefix it extends as recently used (count 4). A code adds
/// the previous code extended by its own first byte, as in LZW. A new entry starts with count 2.
/// Once all 8192 entries exist, the new string overwrites an existing one. A sweep starts where the
/// previous sweep stopped and walks the string entries, wrapping from 8191 back to 257. It lowers
/// each count it passes (a byte, so 0 wraps to 255) and stops at the first count that reaches
/// zero. The entry overwritten is the one with the lowest count the sweep saw, the earliest on a
/// tie.
/// </para>
/// <para>Converted from XADMaster's XADARCCrushHandle and its LZW table (LZW.c), LGPL-2.1-or-later.</para>
/// </remarks>
internal static class ArcCrushed {
  private const int MaxSymbols = 8192;
  private const int EndOfStream = 256;
  private const int FirstString = 257;
  private const int Window = 500;
  private const int StringModeThreshold = 375;

  /// <summary>
  /// Decodes the LZW layer of a method-10 member up to its end code. The result is still
  /// RLE90-packed (see <see cref="ArcRle"/>), as PAK runs that pass before crushing.
  /// </summary>
  /// <param name="input">The member's stored bytes.</param>
  /// <param name="maxOutput">The most bytes to accept before the data is refused as malformed.</param>
  /// <exception cref="InvalidDataException">The data ends without the end code, holds an impossible code, or exceeds <paramref name="maxOutput"/>.</exception>
  public static byte[] Decode(byte[] input, int maxOutput = int.MaxValue) {
    var output = new MemoryStream();
    var bits = new LsbBits(input);
    var parent = new int[MaxSymbols];
    var chr = new byte[MaxSymbols];
    var usage = new byte[MaxSymbols];
    var recent = new bool[Window];
    var phrase = new byte[MaxSymbols];
    for (var i = 0; i < 256; ++i) {
      parent[i] = -1;
      chr[i] = (byte)i;
    }

    var count = FirstString;
    var previous = -1;
    var width = 1;
    var bump = 2;
    var literalFlag = true;
    var recentStrings = 0;
    var ring = 0;
    var sweep = FirstString;

    while (true) {
      int symbol;
      if (literalFlag)
        symbol = bits.Read(1) != 0 ? bits.Read(width) + 256 : bits.Read(8);
      else {
        symbol = bits.Read(width);
        if (symbol < 0x100) symbol ^= 0xFF;
      }

      if (symbol == EndOfStream) break;
      if (symbol >= MaxSymbols) throw new InvalidDataException("PAK Crushed code is out of range.");

      // Mark the string and every prefix of it as recently used.
      for (var mark = symbol; mark >= 0; mark = parent[mark]) usage[mark] = 4;

      if (recent[ring]) --recentStrings;
      recent[ring] = symbol >= 0x100;
      if (recent[ring]) ++recentStrings;
      ring = (ring + 1) % Window;

      var manyLiterals = recentStrings < StringModeThreshold;
      if (manyLiterals != literalFlag) {
        literalFlag = manyLiterals;
        bump = 1 << width;
        if (!literalFlag) bump -= 0x100;
      }

      if (count < MaxSymbols) {
        if (previous >= 0) {
          int first;
          if (symbol < count) first = FirstByte(parent, chr, symbol);
          else if (symbol == count) first = FirstByte(parent, chr, previous);
          else throw new InvalidDataException("PAK Crushed code refers to an undefined string.");
          parent[count] = previous;
          chr[count] = (byte)first;
          ++count;
        } else if (symbol >= count)
          throw new InvalidDataException("PAK Crushed stream starts with an undefined string.");

        previous = symbol;
        usage[count - 1] = 2;
      } else {
        if (symbol >= count) throw new InvalidDataException("PAK Crushed code refers to an undefined string.");
        int victim = 0, lowest = int.MaxValue, index = sweep;
        do {
          if (++index == MaxSymbols) index = FirstString;
          if (usage[index] < lowest) {
            victim = index;
            lowest = usage[index];
          }

          usage[index] = (byte)(usage[index] - 1);
          if (usage[index] == 0) break;
        } while (index != sweep);

        sweep = index;
        parent[victim] = previous;
        chr[victim] = FirstByte(parent, chr, symbol);
        previous = symbol;
        usage[victim] = 2;
      }

      // The string just decoded, last byte first.
      var length = 0;
      for (var s = previous; s >= 0; s = parent[s]) {
        if (length == phrase.Length) throw new InvalidDataException("PAK Crushed string is cyclic.");
        phrase[length++] = chr[s];
      }

      if (output.Length + length > maxOutput) throw new InvalidDataException("PAK Crushed data expands past its limit.");
      while (length > 0) output.WriteByte(phrase[--length]);

      if (count - FirstString >= bump) {
        ++width;
        bump = 1 << width;
        if (!literalFlag) bump -= 0x100;
      }
    }

    return output.ToArray();
  }

  private static byte FirstByte(int[] parent, byte[] chr, int symbol) {
    for (var guard = 0; parent[symbol] >= 0; ++guard) {
      if (guard == MaxSymbols) throw new InvalidDataException("PAK Crushed string is cyclic.");
      symbol = parent[symbol];
    }

    return chr[symbol];
  }

  /// <summary>Least-significant-bit-first reader that tolerates a few bytes of overrun, then throws.</summary>
  private sealed class LsbBits(byte[] data) {
    private const int Slack = 4;
    private long _position;

    public int Read(int count) {
      var value = 0;
      for (var i = 0; i < count; ++i, ++this._position) {
        var index = this._position >> 3;
        int b;
        if (index < data.Length) b = data[index];
        else if (index < data.Length + Slack) b = 0;
        else throw new InvalidDataException("PAK Crushed data ended early.");
        value |= ((b >> (int)(this._position & 7)) & 1) << i;
      }

      return value;
    }
  }
}
