using Compression.Core.Dictionary.MatchFinders;

namespace Compression.Core.Dictionary.Rar;

/// <summary>
/// Encoder for the RAR 2.9 compression format (RAR 3.x/4.x archives, unpack version 29), the
/// counterpart of <see cref="Rar3Decoder"/>.
/// </summary>
/// <remarks>
/// Emits one set of Huffman tables per file, literals, new matches (symbols 271-298) and
/// matches on the four repeat distances (259-262), then the end-of-file marker. Successive
/// calls continue a solid stream: the repeat distances carry over, and each file announces
/// fresh tables for the next one. Filters and PPMd are not used.
/// </remarks>
public sealed class Rar3Encoder {
  private const int MainCodeSize = 299;
  private const int OffsetCodeSize = 60;
  private const int LowOffsetCodeSize = 17;
  private const int LengthCodeSize = 28;
  private const int PrecodeSize = 20;
  private const int MaxNewLength = 258;
  private const int MaxRepeatLength = 257;

  private static readonly int[] LengthBases = [0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 14, 16, 20, 24, 28, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224];
  private static readonly int[] LengthBits = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5];
  private static readonly int[] OffsetBases = [
    0, 1, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48, 64, 96, 128, 192, 256, 384, 512, 768, 1024, 1536, 2048, 3072,
    4096, 6144, 8192, 12288, 16384, 24576, 32768, 49152, 65536, 98304, 131072, 196608, 262144, 327680, 393216, 458752,
    524288, 589824, 655360, 720896, 786432, 851968, 917504, 983040, 1048576, 1310720, 1572864, 1835008, 2097152, 2359296,
    2621440, 2883584, 3145728, 3407872, 3670016, 3932160];
  private static readonly int[] OffsetBits = [
    0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13, 14, 14,
    15, 15, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18];

  private readonly int _windowSize;
  private readonly int[] _oldOffsets = new int[4];

  private readonly record struct Token(int Symbol, int Length, int Offset);

  /// <summary>Creates an encoder for a dictionary of 2^<paramref name="windowBits"/> bytes.</summary>
  /// <param name="windowBits">Dictionary size as a power of two, 16-22.</param>
  public Rar3Encoder(int windowBits = 22) => this._windowSize = 1 << Math.Clamp(windowBits, 16, 22);

  /// <summary>Compresses one file; call again for the next file of a solid archive.</summary>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    if (data.Length == 0) return [];

    var tokens = this.Parse(data);
    var main = new int[MainCodeSize];
    var offsets = new int[OffsetCodeSize];
    var lows = new int[LowOffsetCodeSize];
    var lengths = new int[LengthCodeSize];
    foreach (var t in tokens) {
      ++main[t.Symbol];
      if (t.Symbol >= 271) {
        var (slot, _) = OffsetSlot(t.Offset);
        ++offsets[slot];
        if (slot > 9) ++lows[(t.Offset - 1 - OffsetBases[slot]) & 0xF];
      } else if (t.Symbol is >= 259 and <= 262)
        ++lengths[LengthSlot(t.Length - 2)];
    }
    ++main[256];
    foreach (var f in (int[][])[main, offsets, lows, lengths]) AtLeastTwo(f);

    var mainCode = new Rar3HuffmanEncoder();
    var offsetCode = new Rar3HuffmanEncoder();
    var lowCode = new Rar3HuffmanEncoder();
    var lengthCode = new Rar3HuffmanEncoder();
    mainCode.Build(main, MainCodeSize);
    offsetCode.Build(offsets, OffsetCodeSize);
    lowCode.Build(lows, LowOffsetCodeSize);
    lengthCode.Build(lengths, LengthCodeSize);

    var w = new Rar3BitWriter();
    WriteTables(w, [.. mainCode.CodeLengths, .. offsetCode.CodeLengths, .. lowCode.CodeLengths, .. lengthCode.CodeLengths]);

    foreach (var t in tokens) {
      mainCode.EncodeSymbol(w, t.Symbol);
      if (t.Symbol < 256) continue;
      if (t.Symbol <= 262) {
        var slot = LengthSlot(t.Length - 2);
        lengthCode.EncodeSymbol(w, slot);
        w.WriteBits((uint)(t.Length - 2 - LengthBases[slot]), LengthBits[slot]);
        continue;
      }
      var lengthSlot = t.Symbol - 271;
      w.WriteBits((uint)(t.Length - Bonus(t.Offset) - 3 - LengthBases[lengthSlot]), LengthBits[lengthSlot]);
      var (offsetSlot, bits) = OffsetSlot(t.Offset);
      offsetCode.EncodeSymbol(w, offsetSlot);
      var extra = (uint)(t.Offset - 1 - OffsetBases[offsetSlot]);
      if (bits == 0) continue;
      if (offsetSlot > 9) {
        if (bits > 4) w.WriteBits(extra >> 4, bits - 4);
        lowCode.EncodeSymbol(w, (int)(extra & 0xF));
      } else
        w.WriteBits(extra, bits);
    }

    // End of file; the next file brings its own tables.
    mainCode.EncodeSymbol(w, 256);
    w.WriteBit(0);
    w.WriteBit(1);
    return w.ToArray();
  }

  private List<Token> Parse(ReadOnlySpan<byte> data) {
    var tokens = new List<Token>(data.Length / 2);
    var finder = new HashChainMatchFinder(this._windowSize);
    var maxDistance = this._windowSize - 1;
    for (var pos = 0; pos < data.Length;) {
      var rep = this.BestRepeat(data, pos);
      var match = finder.FindMatch(data, pos, maxDistance, MaxNewLength, 3);
      var newLength = match.Length - Bonus(match.Distance);
      if (rep.Length >= 2 && rep.Length + 1 >= newLength) {
        tokens.Add(new(259 + rep.Index, rep.Length, this._oldOffsets[rep.Index]));
        var offset = this._oldOffsets[rep.Index];
        for (var i = rep.Index; i > 0; --i) this._oldOffsets[i] = this._oldOffsets[i - 1];
        this._oldOffsets[0] = offset;
        pos = this.Skip(finder, data, pos, rep.Length);
      } else if (match.Length > 0 && newLength >= 3) {
        var length = Math.Min(match.Length, MaxNewLength + Bonus(match.Distance));
        var coded = length - Bonus(match.Distance);
        tokens.Add(new(271 + LengthSlot(coded - 3), length, match.Distance));
        this._oldOffsets[3] = this._oldOffsets[2];
        this._oldOffsets[2] = this._oldOffsets[1];
        this._oldOffsets[1] = this._oldOffsets[0];
        this._oldOffsets[0] = match.Distance;
        pos = this.Skip(finder, data, pos, length);
      } else {
        tokens.Add(new(data[pos], 1, 0));
        ++pos;
      }
    }
    return tokens;
  }

  private int Skip(HashChainMatchFinder finder, ReadOnlySpan<byte> data, int pos, int length) {
    for (var i = 1; i < length; ++i) finder.InsertPosition(data, pos + i);
    return pos + length;
  }

  private (int Index, int Length) BestRepeat(ReadOnlySpan<byte> data, int pos) {
    var best = (Index: -1, Length: 0);
    for (var k = 0; k < 4; ++k) {
      var offset = this._oldOffsets[k];
      if (offset <= 0 || offset > pos) continue;
      var length = 0;
      var limit = Math.Min(MaxRepeatLength, data.Length - pos);
      while (length < limit && data[pos + length] == data[pos + length - offset]) ++length;
      if (length > best.Length) best = (k, length);
    }
    return best;
  }

  /// <summary>Keeps every code a proper prefix code: a table with fewer than two symbols gets dummies.</summary>
  private static void AtLeastTwo(int[] frequencies) {
    var used = frequencies.Count(f => f > 0);
    for (var i = 0; used < 2 && i < frequencies.Length; ++i)
      if (frequencies[i] == 0) {
        frequencies[i] = 1;
        ++used;
      }
  }

  /// <summary>Lengths the decoder adds for far matches.</summary>
  private static int Bonus(int offset) => (offset >= 0x2000 ? 1 : 0) + (offset >= 0x40000 ? 1 : 0);

  private static int LengthSlot(int value) {
    var slot = LengthBases.Length - 1;
    while (LengthBases[slot] > value) --slot;
    return slot;
  }

  private static (int Slot, int Bits) OffsetSlot(int offset) {
    var slot = OffsetBases.Length - 1;
    while (OffsetBases[slot] > offset - 1) --slot;
    return (slot, OffsetBits[slot]);
  }

  /// <summary>Tables as the decoder reads them: LZ flag, fresh lengths, precode, then run-length coded lengths.</summary>
  private static void WriteTables(Rar3BitWriter w, int[] lengths) {
    var symbols = new List<(int Symbol, int Extra, int ExtraBits)>();
    for (var i = 0; i < lengths.Length;) {
      if (lengths[i] == 0) {
        var run = 1;
        while (i + run < lengths.Length && lengths[i + run] == 0 && run < 138) ++run;
        if (run >= 11) {
          symbols.Add((19, run - 11, 7));
          i += run;
          continue;
        }
        if (run >= 3) {
          symbols.Add((18, run - 3, 3));
          i += run;
          continue;
        }
      } else if (i > 0 && lengths[i] == lengths[i - 1]) {
        var run = 0;
        while (i + run < lengths.Length && lengths[i + run] == lengths[i - 1] && run < 138) ++run;
        if (run >= 11) {
          symbols.Add((17, run - 11, 7));
          i += run;
          continue;
        }
        if (run >= 3) {
          symbols.Add((16, Math.Min(run, 10) - 3, 3));
          i += Math.Min(run, 10);
          continue;
        }
      }
      symbols.Add((lengths[i], 0, 0));
      ++i;
    }

    var frequencies = new int[PrecodeSize];
    foreach (var s in symbols) ++frequencies[s.Symbol];
    AtLeastTwo(frequencies);
    var precode = new Rar3HuffmanEncoder();
    precode.Build(frequencies, PrecodeSize);

    w.WriteBit(0);   // LZ block
    w.WriteBit(0);   // lengths are not deltas of the previous tables
    foreach (var l in precode.CodeLengths) {
      w.WriteBits((uint)l, 4);
      if (l == 15) w.WriteBits(0, 4);
    }
    foreach (var s in symbols) {
      precode.EncodeSymbol(w, s.Symbol);
      if (s.ExtraBits > 0) w.WriteBits((uint)s.Extra, s.ExtraBits);
    }
  }
}
