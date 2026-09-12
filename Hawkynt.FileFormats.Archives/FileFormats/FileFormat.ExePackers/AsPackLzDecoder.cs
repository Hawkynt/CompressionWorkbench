#pragma warning disable CS1591
namespace FileFormat.ExePackers;

/// <summary>
/// Decoder for the LZ77 + canonical-Huffman stream ASPack 2.x stores in each of
/// its packed regions. The format is an LZX relative — literals and matches share
/// one main alphabet, distances are split into a position slot plus extra bits,
/// the three most recent distances are addressable as repeat codes, and every
/// block starts with its own Huffman code lengths delta-coded against the previous
/// block's — but the alphabet sizes, the length encoding and the code-length
/// delta rule are ASPack's own, so an LZX decoder cannot read it.
/// </summary>
/// <remarks>
/// <para>
/// The stream layout was reconstructed from the behaviour of the ASPack 2.12
/// unpacking stub — the routine the stub calls once per packed region with
/// (source, destination, original size, scratch buffer) — because no published
/// description of the container exists. Nothing here is derived from another
/// implementation's source.
/// </para>
/// <para>Stream grammar:</para>
/// <list type="bullet">
///   <item><description>Bits are consumed most-significant-first from a
///     big-endian byte stream; a single read never spans more than 24 bits.</description></item>
///   <item><description>A block header carries: one bit selecting whether the
///     previous block's code lengths are kept (1) or reset to zero (0); 19
///     four-bit lengths for the pre-tree; then 757 code lengths for the main
///     (721), length (28) and aligned (8) alphabets, coded with the pre-tree.
///     Pre-tree symbols 0–15 mean <c>(previous + symbol) mod 16</c>, 16 repeats
///     the last emitted length <c>read(2)+3</c> times, 17 emits <c>read(3)+3</c>
///     zeros and 18 emits <c>read(7)+11</c> zeros.</description></item>
///   <item><description>Main symbols below 256 are literals; 720 restarts the
///     block header; 256–719 are matches, splitting into a position slot
///     (<c>(symbol-256)/8</c>, 58 slots) and a length footer
///     (<c>(symbol-256)%8</c>). Footer <i>f</i> below 7 means a match length of
///     <c>f+2</c>; footer 7 draws a slot from the length alphabet for a length of
///     <c>9 + base + read(extra)</c>.</description></item>
///   <item><description>The distance code is the slot's base plus its extra bits.
///     When the aligned alphabet is non-trivial (not all eight lengths equal 3)
///     and the slot carries at least three extra bits, the low three bits come
///     from the aligned alphabet instead: <c>base + aligned + verbatim*8</c>.
///     Distance codes 0/1/2 address the three most recent distances (code 0
///     without reordering, 1 and 2 swapping themselves to the front); larger
///     codes mean a literal distance of <c>code-2</c> and push the recency
///     list.</description></item>
/// </list>
/// <para>
/// A canonical code is built over a 24-bit code space: a code of length
/// <c>L</c> claims <c>2^(24-L)</c> of it, the space must be filled exactly, and
/// symbols are ordered by (length, symbol). Codes of eight bits or fewer resolve
/// through a 256-entry table indexed by the top eight bits of the code.
/// </para>
/// </remarks>
internal sealed class AsPackLzDecoder {

  private const int MainSymbols = 0x2D1;
  private const int LengthSymbols = 0x1C;
  private const int AlignedSymbols = 8;
  private const int PreTreeSymbols = 0x13;
  private const int CodeLengthCount = MainSymbols + LengthSymbols + AlignedSymbols;
  private const int NewBlockSymbol = 0x2D0;
  private const int MaxCodeLength = 15;
  private const int CodeBits = 24;
  private const uint CodeSpace = 1u << CodeBits;

  /// <summary>Extra bytes a match may write past the requested output length.</summary>
  internal const int OverrunMargin = 0x10E;

  /// <summary>Bytes the bit reader may consume past the end of the image before the stream is rejected.</summary>
  private const int LookAhead = 8;

  private static readonly int[] LengthBases = [
    0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 14, 16, 20, 24, 28,
    32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224,
  ];

  private static readonly int[] LengthExtraBits = [
    0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2,
    3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5,
  ];

  private static readonly int[] PositionExtraBits = [
    0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6,
    7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13, 14, 14,
    15, 15, 16, 16, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17,
    17, 17, 18, 18, 18, 18, 18, 18, 18, 18,
  ];

  private static readonly uint[] PositionBases = BuildPositionBases();

  private static uint[] BuildPositionBases() {
    var bases = new uint[PositionExtraBits.Length];
    var accumulator = 0u;
    for (var i = 0; i < bases.Length; ++i) {
      bases[i] = accumulator;
      accumulator += 1u << PositionExtraBits[i];
    }

    return bases;
  }

  private readonly byte[] _source;
  private int _cursor;
  private uint _window;
  private int _consumed = 32;

  private readonly HuffmanCode _main = new(MainSymbols);
  private readonly HuffmanCode _lengths = new(LengthSymbols);
  private readonly HuffmanCode _aligned = new(AlignedSymbols);
  private readonly HuffmanCode _preTree = new(PreTreeSymbols);
  private readonly byte[] _previousCodeLengths = new byte[CodeLengthCount];
  private readonly byte[] _codeLengths = new byte[CodeLengthCount];
  private bool _alignedOffsets;
  private readonly uint[] _recentDistances = new uint[3];

  private AsPackLzDecoder(byte[] source, int offset) {
    this._source = source;
    this._cursor = offset;
  }

  /// <summary>
  /// Decodes one ASPack region. <paramref name="outputLength"/> is the original
  /// size recorded in the stub's region table; the stream carries no end marker,
  /// so that length is what terminates the decode.
  /// </summary>
  /// <returns>
  /// A buffer of <paramref name="outputLength"/> bytes plus whatever the final
  /// match overran, and the number of bytes actually produced.
  /// </returns>
  public static (byte[] Buffer, int Produced) Decompress(byte[] source, int offset, int outputLength) {
    if (source is null) throw new ArgumentNullException(nameof(source));
    if (offset < 0 || offset > source.Length)
      throw new InvalidDataException("ASPack: region source offset lies outside the image.");
    if (outputLength < 0)
      throw new InvalidDataException("ASPack: negative region length.");

    var decoder = new AsPackLzDecoder(source, offset);
    return decoder.Run(outputLength);
  }

  private (byte[] Buffer, int Produced) Run(int outputLength) {
    var output = new byte[outputLength + OverrunMargin];
    var produced = 0;
    if (!this.ReadBlockHeader())
      throw new InvalidDataException("ASPack: malformed block header.");

    while (produced < outputLength) {
      var symbol = this.Decode(this._main);
      if (symbol < 0x100) {
        output[produced++] = (byte)symbol;
        continue;
      }

      if (symbol >= NewBlockSymbol) {
        if (!this.ReadBlockHeader())
          throw new InvalidDataException("ASPack: malformed block header.");
        continue;
      }

      var match = symbol - 0x100;
      var slot = match >> 3;
      var footer = match & 7;
      var matchLength = footer + 2;
      if (footer == 7) {
        var lengthSlot = this.Decode(this._lengths);
        matchLength = 9 + LengthBases[lengthSlot] + this.ReadBits(LengthExtraBits[lengthSlot]);
      }

      var extraBits = PositionExtraBits[slot];
      uint distanceCode;
      if (this._alignedOffsets && extraBits >= 3) {
        var verbatim = (uint)this.ReadBits(extraBits - 3);
        var alignedBits = (uint)this.Decode(this._aligned);
        distanceCode = PositionBases[slot] + alignedBits + verbatim * 8;
      } else
        distanceCode = PositionBases[slot] + (uint)this.ReadBits(extraBits);

      uint recent;
      if (distanceCode < 3) {
        recent = this._recentDistances[distanceCode];
        if (distanceCode != 0) {
          this._recentDistances[distanceCode] = this._recentDistances[0];
          this._recentDistances[0] = recent;
        }
      } else {
        this._recentDistances[2] = this._recentDistances[1];
        this._recentDistances[1] = this._recentDistances[0];
        recent = distanceCode - 3;
        this._recentDistances[0] = recent;
      }

      var distance = (long)recent + 1;
      if (distance > produced)
        throw new InvalidDataException($"ASPack: match distance {distance} exceeds the {produced} bytes produced so far.");
      if (produced + matchLength > output.Length)
        throw new InvalidDataException("ASPack: match runs past the region's declared size.");

      var from = produced - (int)distance;
      for (var i = 0; i < matchLength; ++i)
        output[produced + i] = output[from + i];
      produced += matchLength;
    }

    return (output, produced);
  }

  private bool ReadBlockHeader() {
    if (this.ReadBits(1) == 0)
      Array.Clear(this._previousCodeLengths);

    Span<byte> preTreeLengths = stackalloc byte[PreTreeSymbols];
    for (var i = 0; i < PreTreeSymbols; ++i)
      preTreeLengths[i] = (byte)this.ReadBits(4);
    if (!this._preTree.Build(preTreeLengths))
      return false;

    var lengths = this._codeLengths;
    Array.Clear(lengths);
    var index = 0;
    while (index < CodeLengthCount) {
      var symbol = this.Decode(this._preTree);
      switch (symbol) {
        case < 16:
          lengths[index] = (byte)((this._previousCodeLengths[index] + symbol) & 15);
          ++index;
          break;
        case 16: {
          var run = this.ReadBits(2) + 3;
          for (; run > 0 && index < CodeLengthCount; --run, ++index)
            lengths[index] = index > 0 ? lengths[index - 1] : (byte)0;
          break;
        }
        case 17: {
          var run = this.ReadBits(3) + 3;
          for (; run > 0 && index < CodeLengthCount; --run, ++index)
            lengths[index] = 0;
          break;
        }
        default: {
          var run = this.ReadBits(7) + 11;
          for (; run > 0 && index < CodeLengthCount; --run, ++index)
            lengths[index] = 0;
          break;
        }
      }
    }

    if (!this._main.Build(lengths.AsSpan(0, MainSymbols)))
      return false;
    if (!this._lengths.Build(lengths.AsSpan(MainSymbols, LengthSymbols)))
      return false;

    var alignedLengths = lengths.AsSpan(MainSymbols + LengthSymbols, AlignedSymbols);
    if (!this._aligned.Build(alignedLengths))
      return false;

    // A uniform 3-bit aligned code carries no information, and the stub takes
    // that as "this block does not use aligned offsets" rather than reading the
    // low three distance bits from the aligned alphabet.
    this._alignedOffsets = false;
    foreach (var length in alignedLengths)
      if (length != 3) {
        this._alignedOffsets = true;
        break;
      }

    lengths.CopyTo(this._previousCodeLengths, 0);
    return true;
  }

  private void Refill() {
    while (this._consumed >= 8) {
      // The stub reads the stream out of mapped memory, where the bytes past a
      // section's raw data read as zero; the last few bits of a region legitimately
      // come from that padding, so a short read is padded rather than fatal. Running
      // any further than the reader's own look-ahead means the stream is not one of
      // ours and would otherwise spin forever on synthetic zero blocks.
      byte next;
      if (this._cursor < this._source.Length)
        next = this._source[this._cursor];
      else if (this._cursor < this._source.Length + LookAhead)
        next = 0;
      else
        throw new InvalidDataException("ASPack: stream ran past the end of the image before the region was complete.");

      ++this._cursor;
      this._window = (this._window << 8) | next;
      this._consumed -= 8;
    }
  }

  private int ReadBits(int count) {
    this.Refill();
    var value = (this._window >> (8 - this._consumed)) & (CodeSpace - 1);
    value >>= CodeBits - count;
    this._consumed += count;
    return (int)value;
  }

  private int Decode(HuffmanCode code) {
    this.Refill();
    // Codes are at most 15 bits wide, so every limit is a multiple of 2^9 and the
    // low nine bits of the peeked window cannot influence the comparisons.
    var peeked = (this._window >> (8 - this._consumed)) & 0xFFFE00u;
    var symbol = code.Lookup(peeked, out var length);
    this._consumed += length;
    return symbol;
  }

  /// <summary>Canonical Huffman code over a 24-bit code space, at most 15 bits per code.</summary>
  private sealed class HuffmanCode(int symbolCount) {

    private readonly uint[] _limits = new uint[MaxCodeLength + 1];
    private readonly int[] _firstIndex = new int[MaxCodeLength + 1];
    private readonly int[] _symbols = new int[symbolCount];
    private readonly byte[] _shortLengths = new byte[0x100];

    public bool Build(ReadOnlySpan<byte> lengths) {
      Span<int> counts = stackalloc int[MaxCodeLength + 1];
      foreach (var length in lengths) {
        if (length > MaxCodeLength) return false;
        ++counts[length];
      }

      Array.Clear(this._symbols);
      Array.Clear(this._shortLengths);
      this._limits[0] = 0;
      this._firstIndex[0] = 0;
      var accumulated = 0u;
      var filled = 0;
      for (var length = 1; length <= MaxCodeLength; ++length) {
        accumulated += (uint)counts[length] << (CodeBits - length);
        if (accumulated > CodeSpace) return false;
        this._limits[length] = accumulated;
        // Zero-length symbols occupy the leading slots of the symbol table and are
        // never addressed, which is what makes the running index start at counts[0].
        this._firstIndex[length] = this._firstIndex[length - 1] + counts[length - 1];
        if (length > 8) continue;

        var top = (int)(accumulated >> 16);
        for (; filled < top; ++filled)
          this._shortLengths[filled] = (byte)length;
      }

      if (accumulated != CodeSpace) return false;

      Span<int> next = stackalloc int[MaxCodeLength + 1];
      for (var length = 0; length <= MaxCodeLength; ++length)
        next[length] = this._firstIndex[length];
      for (var symbol = 0; symbol < lengths.Length; ++symbol) {
        var length = lengths[symbol];
        if (length != 0)
          this._symbols[next[length]++] = symbol;
      }

      return true;
    }

    public int Lookup(uint code, out int length) {
      if (code < this._limits[8])
        length = this._shortLengths[code >> 16];
      else if (code < this._limits[10])
        length = code < this._limits[9] ? 9 : 10;
      else if (code < this._limits[11])
        length = 11;
      else if (code < this._limits[12])
        length = 12;
      else if (code < this._limits[13])
        length = 13;
      else if (code < this._limits[14])
        length = 14;
      else
        length = 15;

      if (length == 0)
        throw new InvalidDataException("ASPack: Huffman code is not resolvable.");

      var index = (int)((code - this._limits[length - 1]) >> (CodeBits - length)) + this._firstIndex[length];
      if ((uint)index >= (uint)this._symbols.Length)
        throw new InvalidDataException("ASPack: Huffman symbol index is out of range.");

      return this._symbols[index];
    }
  }
}
