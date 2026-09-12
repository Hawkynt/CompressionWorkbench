#pragma warning disable CS1591
namespace Codec.TrackerXmIt;

/// <summary>
/// Decodes Impulse Tracker compressed sample data (IT214 / IT215) into raw signed PCM.
/// </summary>
/// <remarks>
/// <para>
/// IT compresses samples in independent blocks of at most 0x8000 <em>decompressed bytes</em>
/// (0x8000 samples for 8-bit, 0x4000 samples for 16-bit). Each block is a length-prefixed
/// bitstream (u16 little-endian byte count) of variable-width packed values: the bit width
/// starts at 9 (8-bit samples) or 17 (16-bit samples) and shrinks/grows in response to escape
/// codes embedded in the stream. Decoded values are differenced; IT215 ("v2.15" compression)
/// applies the delta accumulation <em>twice</em> (a delta-of-delta pass) where IT214 applies it
/// once.
/// </para>
/// <para>
/// This is a direct port of the schismtracker reference decoder
/// (<c>fmt/it.c</c> : <c>it_decompress8</c> / <c>it_decompress16</c>), which is itself the
/// canonical reading of Impulse Tracker's own <c>ITSEX</c>/<c>ITDEC</c> routines and matches
/// OpenMPT's <c>ITCompression.cpp</c>. Width-change escape handling follows the three-range
/// scheme described there (top-bit escape for width 1-6, offset escape for width 7-8/9-16, and
/// the explicit width+1 escape for the maximum width).
/// </para>
/// </remarks>
public static class ItSampleDecompressor {

  /// <summary>Decodes an 8-bit IT-compressed sample to signed bytes.</summary>
  /// <param name="compressed">The full compressed bitstream (all blocks back-to-back).</param>
  /// <param name="lengthSamples">Number of decoded samples expected.</param>
  /// <param name="it215">True for IT215 (double delta), false for IT214 (single delta).</param>
  /// <returns>One signed byte per sample.</returns>
  public static sbyte[] Decompress8(ReadOnlySpan<byte> compressed, int lengthSamples, bool it215) {
    var output = new sbyte[lengthSamples];
    var reader = new BitReader(compressed);
    var pos = 0;

    while (pos < lengthSamples) {
      // Block length prefix.
      var blockBytes = reader.ReadBlockLength();
      reader.BeginBlock(blockBytes);

      var blockSamples = Math.Min(0x8000, lengthSamples - pos);
      var width = 9;
      sbyte d1 = 0, d2 = 0;

      var i = 0;
      while (i < blockSamples) {
        if (width is < 1 or > 9)
          throw new InvalidDataException("IT8 decompress: bit width out of range.");

        var value = (int)reader.ReadBits(width);

        // Width-change escape handling for 8-bit. Escapes do NOT emit a sample.
        if (width < 7) {
          // Method 1: top bit set, the rest (3 bits + 1) selects the new width.
          if (value == (1 << (width - 1))) {
            var newWidth = (int)reader.ReadBits(3) + 1;
            width = newWidth < width ? newWidth : newWidth + 1;
            continue;
          }
        } else if (width < 9) {
          // Method 2: a band near the top selects a new width.
          var border = (0xFF >> (9 - width)) - 4;
          if (value > border && value <= border + 8) {
            var newWidth = value - border;
            width = newWidth < width ? newWidth : newWidth + 1;
            continue;
          }
        } else {
          // width == 9, Method 3: high bit (bit 8) escapes; low 8 bits + 1 = new width.
          if ((value & 0x100) != 0) {
            width = (value & 0xFF) + 1;
            continue;
          }
        }

        // Sign-extend the sample of the current width into a full signed byte.
        sbyte v;
        if (width < 8) {
          var shift = 8 - width;
          v = (sbyte)(unchecked((sbyte)(value << shift)) >> shift);
        } else {
          v = (sbyte)value;
        }

        d1 += v;
        d2 += d1;
        output[pos + i] = it215 ? d2 : d1;
        ++i;
      }

      pos += blockSamples;
      reader.EndBlock();
    }

    return output;
  }

  /// <summary>Decodes a 16-bit IT-compressed sample to signed 16-bit samples.</summary>
  /// <param name="compressed">The full compressed bitstream (all blocks back-to-back).</param>
  /// <param name="lengthSamples">Number of decoded samples expected.</param>
  /// <param name="it215">True for IT215 (double delta), false for IT214 (single delta).</param>
  /// <returns>One signed 16-bit value per sample.</returns>
  public static short[] Decompress16(ReadOnlySpan<byte> compressed, int lengthSamples, bool it215) {
    var output = new short[lengthSamples];
    var reader = new BitReader(compressed);
    var pos = 0;

    while (pos < lengthSamples) {
      var blockBytes = reader.ReadBlockLength();
      reader.BeginBlock(blockBytes);

      var blockSamples = Math.Min(0x4000, lengthSamples - pos);
      var width = 17;
      short d1 = 0, d2 = 0;

      var i = 0;
      while (i < blockSamples) {
        if (width is < 1 or > 17)
          throw new InvalidDataException("IT16 decompress: bit width out of range.");

        var value = (int)reader.ReadBits(width);

        if (width < 7) {
          if (value == (1 << (width - 1))) {
            var newWidth = (int)reader.ReadBits(4) + 1;
            width = newWidth < width ? newWidth : newWidth + 1;
            continue;
          }
        } else if (width < 17) {
          var border = (0xFFFF >> (17 - width)) - 8;
          if (value > border && value <= border + 16) {
            var newWidth = value - border;
            width = newWidth < width ? newWidth : newWidth + 1;
            continue;
          }
        } else {
          // width == 17, Method 3: high bit (bit 16) escapes; low 16 bits + 1 = new width.
          if ((value & 0x10000) != 0) {
            width = (value & 0xFFFF) + 1;
            continue;
          }
        }

        short v;
        if (width < 16) {
          var shift = 16 - width;
          v = (short)(unchecked((short)(value << shift)) >> shift);
        } else {
          v = (short)value;
        }

        d1 += v;
        d2 += d1;
        output[pos + i] = it215 ? d2 : d1;
        ++i;
      }

      pos += blockSamples;
      reader.EndBlock();
    }

    return output;
  }

  /// <summary>
  /// Little-endian, LSB-first bit reader over the IT compressed stream. The stream is a
  /// sequence of blocks; each block is preceded by a u16 little-endian byte count and bits are
  /// drawn LSB-first within each successive byte. Reads past the end of a block (which IT's own
  /// encoder can legitimately request near the tail) yield zero bits.
  /// </summary>
  private ref struct BitReader {

    private readonly ReadOnlySpan<byte> _data;
    private int _bytePos;          // absolute position into _data for block-length reads
    private int _blockEnd;         // absolute byte index just past the current block
    private int _bitBufferPos;     // absolute byte index of the next byte to load
    private uint _bitBuffer;
    private int _bitsAvailable;

    public BitReader(ReadOnlySpan<byte> data) {
      this._data = data;
      this._bytePos = 0;
      this._blockEnd = 0;
      this._bitBufferPos = 0;
      this._bitBuffer = 0;
      this._bitsAvailable = 0;
    }

    /// <summary>Reads the u16 little-endian block length prefix.</summary>
    public ushort ReadBlockLength() {
      var lo = this._bytePos < this._data.Length ? this._data[this._bytePos] : (byte)0;
      var hi = this._bytePos + 1 < this._data.Length ? this._data[this._bytePos + 1] : (byte)0;
      this._bytePos += 2;
      return (ushort)(lo | (hi << 8));
    }

    /// <summary>Starts a block of <paramref name="byteCount"/> packed bytes after the prefix.</summary>
    public void BeginBlock(int byteCount) {
      this._bitBufferPos = this._bytePos;
      this._blockEnd = Math.Min(this._data.Length, this._bytePos + byteCount);
      this._bitBuffer = 0;
      this._bitsAvailable = 0;
    }

    /// <summary>Advances past the current block to the next length prefix.</summary>
    public void EndBlock() => this._bytePos = this._blockEnd;

    /// <summary>Reads <paramref name="count"/> bits LSB-first; zero-fills past the block end.</summary>
    public uint ReadBits(int count) {
      uint result = 0;
      var got = 0;
      while (got < count) {
        if (this._bitsAvailable == 0) {
          this._bitBuffer = this._bitBufferPos < this._blockEnd ? this._data[this._bitBufferPos] : 0u;
          ++this._bitBufferPos;
          this._bitsAvailable = 8;
        }
        var take = Math.Min(count - got, this._bitsAvailable);
        var mask = (1u << take) - 1u;
        result |= (this._bitBuffer & mask) << got;
        this._bitBuffer >>= take;
        this._bitsAvailable -= take;
        got += take;
      }
      return result;
    }
  }
}
