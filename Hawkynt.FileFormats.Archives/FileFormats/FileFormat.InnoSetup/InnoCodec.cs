using System.Buffers.Binary;
using Compression.Core.Checksums;
using Compression.Core.Deflate;
using Compression.Core.Dictionary.Lzma;
using FileFormat.Bzip2;

namespace FileFormat.InnoSetup;

/// <summary>
/// The decoding pieces of an Inno Setup installer: setup-0 header blocks, setup-1 chunk
/// streams, the x86 call/jump filters and the per-file checksums. Converted from innoextract
/// (src/stream/block.cpp, chunk.cpp, lzma.cpp, exefilter.hpp; zlib licence).
/// </summary>
internal static class InnoCodec {

  /// <summary>
  /// Reads one setup-0 block at the stream's position: a CRC'd header, then the payload split
  /// into 4096-byte CRC'd sub-blocks, compressed as a whole. Leaves the stream after the block.
  /// </summary>
  public static byte[] ReadHeaderBlock(Stream stream, InnoVersion version) {
    Span<byte> h = stackalloc byte[9];
    stream.ReadExactly(h[..4]);
    var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(h);
    long storedSize;
    InnoCompression compression;
    if (version.AtLeast(6, 7, 0)) {
      // TCompressedBlockHeader: StoredSize Int64, Compressed Boolean (issrc Compression.Base.pas).
      Span<byte> wide = stackalloc byte[9];
      stream.ReadExactly(wide);
      if (Crc32.Compute(wide) != expectedCrc)
        throw new InvalidDataException("Inno Setup block header CRC mismatch.");
      storedSize = BinaryPrimitives.ReadInt64LittleEndian(wide);
      compression = wide[8] != 0 ? InnoCompression.Lzma1 : InnoCompression.Stored;
    } else if (version.AtLeast(4, 0, 9)) {
      stream.ReadExactly(h[4..9]);
      if (Crc32.Compute(h[4..9]) != expectedCrc)
        throw new InvalidDataException("Inno Setup block header CRC mismatch.");
      storedSize = BinaryPrimitives.ReadUInt32LittleEndian(h[4..]);
      compression = h[8] != 0 ? (version.AtLeast(4, 1, 6) ? InnoCompression.Lzma1 : InnoCompression.Zlib) : InnoCompression.Stored;
    } else {
      Span<byte> old = stackalloc byte[8];
      stream.ReadExactly(old);
      if (Crc32.Compute(old) != expectedCrc)
        throw new InvalidDataException("Inno Setup block header CRC mismatch.");
      var compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(old);
      var uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(old[4..]);
      (storedSize, compression) = compressedSize == uint.MaxValue ? (uncompressedSize, InnoCompression.Stored) : (compressedSize, InnoCompression.Zlib);
      storedSize += (storedSize + 4095) / 4096 * 4;
    }
    if (storedSize > stream.Length - stream.Position)
      throw new InvalidDataException("Inno Setup block runs past the end of the file.");

    var raw = new byte[storedSize];
    stream.ReadExactly(raw);
    var payload = new MemoryStream(raw.Length);
    for (var p = 0; p < raw.Length;) {
      if (raw.Length - p < 5)
        throw new InvalidDataException("Inno Setup block ends inside a sub-block header.");
      var crc = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(p));
      var len = Math.Min(4096, raw.Length - p - 4);
      var chunk = raw.AsSpan(p + 4, len);
      if (Crc32.Compute(chunk) != crc)
        throw new InvalidDataException("Inno Setup sub-block CRC mismatch.");
      payload.Write(chunk);
      p += 4 + len;
    }
    payload.Position = 0;
    return compression switch {
      InnoCompression.Stored => payload.ToArray(),
      InnoCompression.Zlib => Decompress(InnoCompression.Zlib, payload, -1),
      _ => Decompress(InnoCompression.Lzma1, payload, -1),
    };
  }

  /// <summary>
  /// Decompresses an Inno stream. <paramref name="length"/> is the number of bytes wanted, or -1
  /// to decode to the end of the stream.
  /// </summary>
  public static byte[] Decompress(InnoCompression compression, Stream input, long length) {
    var output = new MemoryStream(length > 0 && length < int.MaxValue ? (int)length : 0);
    switch (compression) {
      case InnoCompression.Stored:
        CopyUpTo(input, output, length);
        break;
      case InnoCompression.Zlib: {
        Span<byte> zhdr = stackalloc byte[2];
        input.ReadExactly(zhdr);
        if ((zhdr[0] & 0x0F) != 8 || ((zhdr[0] << 8) | zhdr[1]) % 31 != 0)
          throw new InvalidDataException("Inno Setup zlib stream has no zlib header.");
        var inflater = new DeflateDecompressor(input);
        CopyUpTo(new DecompressorReader(inflater), output, length);
        break;
      }
      case InnoCompression.BZip2: {
        using var bz = new Bzip2Stream(input, Compression.Core.Streams.CompressionStreamMode.Decompress, leaveOpen: true);
        CopyUpTo(bz, output, length);
        break;
      }
      case InnoCompression.Lzma1: {
        var props = new byte[5];
        input.ReadExactly(props);
        if (props[0] > 9 * 5 * 5)
          throw new InvalidDataException("Invalid Inno Setup LZMA properties.");
        new LzmaDecoder(input, props, length).Decode(output);
        break;
      }
      case InnoCompression.Lzma2: {
        var prop = input.ReadByte();
        if (prop is < 0 or > 40)
          throw new InvalidDataException("Invalid Inno Setup LZMA2 properties.");
        var dictionary = prop == 40 ? int.MaxValue : (int)Math.Min(int.MaxValue, (2L | ((uint)prop & 1)) << (prop / 2 + 11));
        var decoded = new Lzma2Decoder(input, dictionary).Decode();
        output.Write(decoded, 0, length < 0 ? decoded.Length : (int)Math.Min(length, decoded.Length));
        break;
      }
      default:
        throw new NotSupportedException($"Inno Setup compression {compression} is not supported.");
    }
    if (length >= 0 && output.Length < length)
      throw new InvalidDataException("Inno Setup data stream ended early.");
    return output.ToArray();
  }

  private static void CopyUpTo(Stream input, Stream output, long length) {
    var buffer = new byte[81920];
    var remaining = length < 0 ? long.MaxValue : length;
    while (remaining > 0) {
      var n = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
      if (n <= 0)
        break;
      output.Write(buffer, 0, n);
      remaining -= n;
    }
  }

  /// <summary>Adapts a <see cref="DeflateDecompressor"/> to a read-only stream.</summary>
  private sealed class DecompressorReader(DeflateDecompressor inflater) : Stream {
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => inflater.Decompress(buffer.AsSpan(offset, count));
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }

  // ── x86 call/jump filters (exefilter.hpp) ───────────────────────────

  /// <summary>Undoes the compiler's call-instruction optimisation on a whole file, in place.</summary>
  public static void Unfilter(InnoExeFilter filter, Span<byte> data) {
    switch (filter) {
      case InnoExeFilter.Filter4108: Unfilter4108(data); break;
      case InnoExeFilter.Filter5200: Unfilter5200(data, flipHighByte: false); break;
      case InnoExeFilter.Filter5309: Unfilter5200(data, flipHighByte: true); break;
    }
  }

  private static void Unfilter4108(Span<byte> data) {
    uint addr = 0;
    var left = 0;
    uint addrOffset = 5;
    for (var i = 0; i < data.Length; ++i, ++addrOffset) {
      var b = data[i];
      if (left == 0) {
        if (b is 0xE8 or 0xE9) {
          addr = ~addrOffset + 1;
          left = 4;
        }
      } else {
        addr += b;
        data[i] = (byte)addr;
        addr >>= 8;
        --left;
      }
    }
  }

  private static void Unfilter5200(Span<byte> data, bool flipHighByte) {
    const int blockSize = 0x10000;
    var i = 0;
    while (i < data.Length) {
      var b = data[i++];
      if (b is not (0xE8 or 0xE9))
        continue;
      // i is now the number of bytes consumed, the "offset" after reading the opcode.
      if (blockSize - ((i - 1) % blockSize) < 5)
        continue; // instructions that span 64 KiB blocks are left alone
      if (i + 4 > data.Length)
        break;
      var a = data.Slice(i, 4);
      i += 4;
      if (a[3] is 0x00 or 0xFF) {
        var addr = (uint)i & 0xFFFFFF;
        var rel = (uint)(a[0] | (a[1] << 8) | (a[2] << 16)) - addr;
        a[0] = (byte)rel;
        a[1] = (byte)(rel >> 8);
        a[2] = (byte)(rel >> 16);
        if (flipHighByte && (rel & 0x800000) != 0)
          a[3] = (byte)~a[3];
      }
    }
  }

  // ── checksums ───────────────────────────────────────────────────────

  public static bool Verify(InnoChecksumKind kind, byte[] expected, ReadOnlySpan<byte> data) => kind switch {
    InnoChecksumKind.Adler32 => BinaryPrimitives.ReadUInt32LittleEndian(expected) == Adler32.Compute(data),
    InnoChecksumKind.Crc32 => BinaryPrimitives.ReadUInt32LittleEndian(expected) == Crc32.Compute(data),
    InnoChecksumKind.Md5 => Md5.Compute(data).AsSpan().SequenceEqual(expected),
    InnoChecksumKind.Sha1 => Sha1.Compute(data).AsSpan().SequenceEqual(expected),
    InnoChecksumKind.Sha256 => Sha256.Compute(data).AsSpan().SequenceEqual(expected),
    _ => false,
  };
}
