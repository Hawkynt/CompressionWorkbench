#pragma warning disable CS1591

namespace Codec.Dts;

/// <summary>
/// A single DCA variable-length code table: parallel arrays of right-aligned <see cref="Codes"/>
/// and their bit <see cref="Lengths"/>, ported verbatim from FFmpeg's <c>dcahuff.h</c>
/// (<c>*_codes</c> / <c>*_bits</c>). Decoding reads bits MSB-first and matches the accumulated
/// prefix against each entry — FFmpeg builds an <c>init_vlc</c> look-up table from the same data,
/// so this exhaustive matcher is functionally identical for the (small) DCA alphabets while being
/// trivially verifiable against the source tables.
/// </summary>
public sealed class DtsVlc {

  public ushort[] Codes { get; }
  public byte[] Lengths { get; }
  public int MaxBits { get; }

  public DtsVlc(ushort[] codes, byte[] lengths) {
    ArgumentNullException.ThrowIfNull(codes);
    ArgumentNullException.ThrowIfNull(lengths);
    if (codes.Length != lengths.Length)
      throw new ArgumentException("DCA VLC code/length arrays must have equal length.");
    this.Codes = codes;
    this.Lengths = lengths;
    var max = 0;
    foreach (var b in lengths)
      if (b > max)
        max = b;
    this.MaxBits = max;
  }

  /// <summary>
  /// Decodes one symbol: peeks <see cref="MaxBits"/> bits, then matches every entry's
  /// right-aligned code against the matching-length prefix. Returns the symbol index, or -1 when
  /// no code matches (corrupt stream). On success the reader is advanced by the matched length.
  /// </summary>
  public int Decode(DtsBitReader reader) {
    var look = reader.PeekBits(this.MaxBits);
    for (var i = 0; i < this.Codes.Length; ++i) {
      var len = this.Lengths[i];
      if (len == 0)
        continue;
      var prefix = look >> (this.MaxBits - len);
      if (prefix == this.Codes[i]) {
        reader.SkipBits(len);
        return i;
      }
    }
    return -1;
  }
}

/// <summary>
/// A DCA "BitAlloc" code-book group as defined in FFmpeg's <c>dcadec.c</c>: a set of selectable
/// <see cref="Vlc"/> tables sharing a fixed <see cref="Offset"/> that is added to the decoded
/// symbol index (e.g. the scale-factor books bias by -64, the bit-allocation index books by +1).
/// </summary>
public sealed class DtsBitAllocBook {
  public int Offset { get; }
  public DtsVlc?[] Vlc { get; }

  public DtsBitAllocBook(int offset, DtsVlc?[] vlc) {
    this.Offset = offset;
    this.Vlc = vlc;
  }

  /// <summary>Decodes a symbol from selector <paramref name="sel"/>'s table and applies the book offset.</summary>
  public int Get(DtsBitReader reader, int sel) {
    var table = this.Vlc[sel] ?? throw new InvalidDataException("DCA VLC selector has no table.");
    var idx = table.Decode(reader);
    if (idx < 0)
      throw new InvalidDataException("DCA VLC look-up failed.");
    return idx + this.Offset;
  }
}
