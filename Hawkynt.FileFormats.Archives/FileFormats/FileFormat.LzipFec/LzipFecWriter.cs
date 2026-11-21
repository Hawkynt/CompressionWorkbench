using System.Buffers.Binary;
using System.Security.Cryptography;
using Crc32 = Compression.Core.Checksums.Crc32;

namespace FileFormat.LzipFec;

/// <summary>
/// Writes lziprecover fec files as the lziprecover manual specifies them ("Fec files", "Fec file
/// format"): the protected file in N data blocks of the FEC block size (the last one zero-padded
/// for the arithmetic), FEC block i = Σ_j A[i][j]·D_j with the Hilbert-type matrix
/// A[i][j] = 1/(i ⊕ j ⊕ r0), r0 = half the field size, over GF(2^8) (polynomial 0x11D, byte by
/// byte) or GF(2^16) (polynomial 0x1100B, little-endian 16-bit words). The file is a chksum packet
/// with the CRC32s of the data blocks (the last one over its real bytes only), the fec packets in
/// order, and a second chksum packet with CRC32-Cs.
/// </summary>
public static class LzipFecWriter {
  /// <summary>FEC blocks written when none are asked for (lziprecover's default).</summary>
  public const int DefaultFecBlocks = 9;

  private const int Unit = 512;

  /// <summary>
  /// Creates a fec file for <paramref name="data"/>.
  /// </summary>
  /// <param name="data">The file to protect (at least one byte).</param>
  /// <param name="fecBlocks">FEC blocks to create: as many lost data blocks can be repaired.</param>
  /// <param name="blockSize">FEC block size, a multiple of 512; 0 picks the smallest that the field allows.</param>
  /// <param name="gf16">GF(2^16) instead of GF(2^8); <see langword="null"/> uses GF(2^8) when it suffices.</param>
  public static byte[] Create(ReadOnlySpan<byte> data, int fecBlocks = DefaultFecBlocks, long blockSize = 0, bool? gf16 = null) {
    if (data.IsEmpty)
      throw new ArgumentException("fec: an empty file has nothing to protect.", nameof(data));
    if (fecBlocks is < 1 or > 2048)
      throw new ArgumentOutOfRangeException(nameof(fecBlocks), "fec: 1 to 2048 FEC blocks.");
    if (blockSize < 0 || blockSize % Unit != 0)
      throw new ArgumentOutOfRangeException(nameof(blockSize), "fec: the block size is a multiple of 512.");

    var wide = gf16 ?? fecBlocks > 128;
    var limit = wide ? 32768 : 128;
    if (blockSize == 0) {
      blockSize = Math.Max(Unit, (data.Length + limit - 1L) / limit);
      blockSize = (blockSize + Unit - 1) / Unit * Unit;
    }
    blockSize = Representable(blockSize);
    var dataBlocks = (data.Length + blockSize - 1) / blockSize;
    if (gf16 == null && dataBlocks > 128)
      (wide, limit) = (true, 32768);
    if (dataBlocks > limit || fecBlocks > limit)
      throw new ArgumentException($"fec: {dataBlocks} data and {fecBlocks} FEC blocks do not fit GF(2^{(wide ? 16 : 8)}).");
    if (blockSize > int.MaxValue / 2)
      throw new NotSupportedException("fec: blocks over 1 GiB are not written.");
    var fbs = (int)blockSize;

    var fec = new byte[fecBlocks][];
    for (var i = 0; i < fecBlocks; ++i) fec[i] = new byte[fbs];
    var field = new Field(wide ? 16 : 8, wide ? 0x1100B : 0x11D);
    var r0 = field.Size / 2;
    var block = new byte[fbs];
    var crc32 = new uint[dataBlocks];
    var crc32c = new uint[dataBlocks];
    for (var j = 0; j < dataBlocks; ++j) {
      var start = (int)(j * blockSize);
      var real = data.Slice(start, Math.Min(fbs, data.Length - start));
      crc32[j] = Crc32.Compute(real);
      crc32c[j] = Crc32.Compute(real, Crc32.Castagnoli);
      block.AsSpan().Clear();
      real.CopyTo(block);
      for (var i = 0; i < fecBlocks; ++i)
        field.MultiplyAdd(field.Inverse(i ^ j ^ r0), block, fec[i]);
    }

    var md5 = MD5.HashData(data);
    using var output = new MemoryStream();
    var coded = Encode(blockSize);
    WriteChksum(output, coded, data.Length, md5, wide, crc32c: false, crc32);
    Span<byte> header = stackalloc byte[12];
    Span<byte> tail = stackalloc byte[4];
    for (var i = 0; i < fecBlocks; ++i) {
      header[0] = 0xB3;
      "FEC"u8.CopyTo(header[1..]);
      BinaryPrimitives.WriteUInt16LittleEndian(header[4..], (ushort)i);
      BinaryPrimitives.WriteUInt16LittleEndian(header[6..], coded);
      BinaryPrimitives.WriteUInt32LittleEndian(header[8..], Crc32.Compute(header[..8]));
      output.Write(header);
      output.Write(fec[i]);
      BinaryPrimitives.WriteUInt32LittleEndian(tail, Crc32.Compute(fec[i]));
      output.Write(tail);
    }
    WriteChksum(output, coded, data.Length, md5, wide, crc32c: true, crc32c);
    return output.ToArray();
  }

  /// <summary>The block size rounded up to one the 16-bit fbs field stores exactly (11-bit mantissa × 2^(exponent+9)).</summary>
  public static long Representable(long size) {
    var units = (size + Unit - 1) / Unit;
    var shift = 0;
    while ((units + (1L << shift) - 1) >> shift > 2047) ++shift;
    return ((units + (1L << shift) - 1) >> shift << shift) * Unit;
  }

  /// <summary>The fbs field for a representable block size: the largest mantissa, the smallest exponent.</summary>
  public static ushort Encode(long size) {
    var mantissa = size / Unit;
    var exponent = 0;
    while (mantissa > 2047) {
      if ((mantissa & 1) != 0) throw new ArgumentException($"fec: {size} bytes is not a representable block size.");
      mantissa >>= 1;
      ++exponent;
    }
    if (exponent > 31) throw new ArgumentException($"fec: {size} bytes is too large a block size.");
    return (ushort)(exponent << 11 | (int)mantissa);
  }

  private static void WriteChksum(Stream output, ushort coded, long size, byte[] md5, bool gf16, bool crc32c, uint[] crcs) {
    var header = new byte[36];
    header[0] = 0xB3; header[1] = 0xA5; header[2] = 0xB6; header[3] = 0xAF;
    header[5] = (byte)((crc32c ? 1 : 0) | (gf16 ? 2 : 0));
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), coded);
    BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), (ulong)size);
    md5.CopyTo(header, 16);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(32), Crc32.Compute(header.AsSpan(0, 32)));
    output.Write(header);
    var array = new byte[crcs.Length * 4 + 4];
    for (var i = 0; i < crcs.Length; ++i) BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(i * 4), crcs[i]);
    BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(crcs.Length * 4), Crc32.Compute(array.AsSpan(0, crcs.Length * 4)));
    output.Write(array);
  }

  /// <summary>GF(2^8) or GF(2^16) by logarithm tables.</summary>
  private sealed class Field {
    public readonly int Size;
    private readonly int _bits;
    private readonly ushort[] _exp, _log;

    public Field(int bits, int polynomial) {
      this._bits = bits;
      this.Size = 1 << bits;
      this._exp = new ushort[2 * this.Size];
      this._log = new ushort[this.Size];
      var x = 1;
      for (var i = 0; i < this.Size - 1; ++i) {
        this._exp[i] = (ushort)x;
        this._log[x] = (ushort)i;
        x <<= 1;
        if ((x & this.Size) != 0) x ^= polynomial;
      }
      for (var i = this.Size - 1; i < 2 * this.Size; ++i) this._exp[i] = this._exp[i - (this.Size - 1)];
    }

    public int Inverse(int a) => this._exp[this.Size - 1 - this._log[a]];

    /// <summary>accumulator ^= c · block, element by element.</summary>
    public void MultiplyAdd(int c, ReadOnlySpan<byte> block, Span<byte> accumulator) {
      var logC = this._log[c];
      if (this._bits == 8) {
        Span<byte> table = stackalloc byte[256];
        for (var v = 1; v < 256; ++v) table[v] = (byte)this._exp[logC + this._log[v]];
        for (var k = 0; k < block.Length; ++k) accumulator[k] ^= table[block[k]];
        return;
      }
      for (var k = 0; k < block.Length; k += 2) {
        var w = block[k] | block[k + 1] << 8;
        if (w == 0) continue;
        var p = this._exp[logC + this._log[w]];
        accumulator[k] ^= (byte)p;
        accumulator[k + 1] ^= (byte)(p >> 8);
      }
    }
  }
}
