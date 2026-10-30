using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace FileFormat.Dxbc;

/// <summary>One part of a DXBC container: a four-character code and its payload.</summary>
public sealed record DxbcChunk(string FourCc, byte[] Data);

/// <summary>
/// Reads and writes DirectX shader bytecode containers (<c>DXBC</c>), the format <c>fxc</c> and the
/// D3D compiler emit for shader models 4 and 5 (and that <c>dxc</c> reuses for DXIL).
/// </summary>
/// <remarks>
/// <para>Layout (little-endian): <c>"DXBC"</c>, a 16-byte checksum, version (1), total size, chunk
/// count, then one 32-bit offset per chunk; each chunk is a four-character code, a 32-bit size and
/// that many bytes. The writer places chunks back to back after the offset table, as fxc does.</para>
/// <para>The checksum covers every byte from offset 20 to the end. It is the MD5 compression
/// function with a container-specific final block: the bit count in the first word, the message
/// tail after it, and <c>(byteCount &lt;&lt; 1) | 1</c> in the last word. Converted from
/// <c>ComputeHashRetail</c> in DirectXShaderCompiler's <c>lib/DxilHash/DxilHash.cpp</c> (Microsoft,
/// University of Illinois/NCSA licence); the round constants and shifts are MD5's (RFC 1321).
/// fxc refuses to disassemble a container whose checksum does not match.</para>
/// </remarks>
public static class DxbcContainer {

  /// <summary>The container magic.</summary>
  public static ReadOnlySpan<byte> Magic => "DXBC"u8;

  /// <summary>Bytes before the chunk-offset table.</summary>
  public const int HeaderSize = 32;

  private const int ChecksumOffset = 4;
  private const int HashedFrom = 20;

  /// <summary>Parses a container and verifies its checksum.</summary>
  /// <exception cref="InvalidDataException">Not a DXBC container, a chunk outside it, or a checksum mismatch.</exception>
  public static IReadOnlyList<DxbcChunk> Read(ReadOnlySpan<byte> data, bool verifyChecksum = true) {
    if (data.Length < HeaderSize || !data.StartsWith(Magic))
      throw new InvalidDataException("Not a DXBC container (missing DXBC magic).");
    var version = BinaryPrimitives.ReadUInt32LittleEndian(data[20..]);
    if (version != 1)
      throw new InvalidDataException($"Unknown DXBC container version {version}.");
    var total = BinaryPrimitives.ReadUInt32LittleEndian(data[24..]);
    var count = BinaryPrimitives.ReadUInt32LittleEndian(data[28..]);
    if (total < HeaderSize || total > data.Length)
      throw new InvalidDataException($"DXBC declares {total} bytes but the file has {data.Length}.");
    if (count > (total - HeaderSize) / 4)
      throw new InvalidDataException($"DXBC declares {count} chunks, more than its offset table can hold.");
    var image = data[..(int)total];
    if (verifyChecksum && !image.Slice(ChecksumOffset, 16).SequenceEqual(Checksum(image[HashedFrom..])))
      throw new InvalidDataException("DXBC checksum mismatch.");

    var chunks = new List<DxbcChunk>((int)count);
    for (var i = 0; i < count; ++i) {
      var offset = BinaryPrimitives.ReadUInt32LittleEndian(image[(HeaderSize + i * 4)..]);
      if (offset < HeaderSize + count * 4 || (ulong)offset + 8 > total)
        throw new InvalidDataException($"DXBC chunk {i} lies outside the container.");
      var size = BinaryPrimitives.ReadUInt32LittleEndian(image[(int)(offset + 4)..]);
      if ((ulong)offset + 8 + size > total)
        throw new InvalidDataException($"DXBC chunk {i} runs past the end of the container.");
      chunks.Add(new DxbcChunk(Encoding.Latin1.GetString(image.Slice((int)offset, 4)), image.Slice((int)offset + 8, (int)size).ToArray()));
    }
    return chunks;
  }

  /// <summary>Builds a container from <paramref name="chunks"/>, in order, with a valid checksum.</summary>
  public static byte[] Write(IReadOnlyList<DxbcChunk> chunks) {
    ArgumentNullException.ThrowIfNull(chunks);
    var tableEnd = HeaderSize + 4L * chunks.Count;
    var total = tableEnd + chunks.Sum(c => 8L + c.Data.Length);
    if (total > int.MaxValue)
      throw new ArgumentException("DXBC container would exceed 2 GiB.", nameof(chunks));
    var image = new byte[total];
    Magic.CopyTo(image);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(20), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(24), (uint)total);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(28), (uint)chunks.Count);
    var pos = (int)tableEnd;
    for (var i = 0; i < chunks.Count; ++i) {
      var c = chunks[i];
      var code = Encoding.Latin1.GetBytes(c.FourCc);
      if (code.Length != 4 || c.FourCc.Any(ch => ch > 0xFF))
        throw new ArgumentException($"DXBC chunk code '{c.FourCc}' is not four characters.", nameof(chunks));
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(HeaderSize + i * 4), (uint)pos);
      code.CopyTo(image, pos);
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(pos + 4), (uint)c.Data.Length);
      c.Data.CopyTo(image, pos + 8);
      pos += 8 + c.Data.Length;
    }
    Checksum(image.AsSpan(HashedFrom)).CopyTo(image, ChecksumOffset);
    return image;
  }

  /// <summary>The DXBC checksum of <paramref name="data"/> (the container from byte 20 on).</summary>
  public static byte[] Checksum(ReadOnlySpan<byte> data) {
    var byteCount = (uint)data.Length;
    var leftOver = byteCount & 0x3F;
    var twoRows = leftOver >= 56;
    var padAmount = twoRows ? 120 - leftOver : 56 - leftOver;
    var blocks = (int)((byteCount + padAmount + 8) >> 6);
    uint[] state = [0x67452301, 0xEFCDAB89, 0x98BADCFE, 0x10325476];
    Span<byte> block = stackalloc byte[64];
    Span<uint> x = stackalloc uint[16];
    for (var i = 0; i < blocks; ++i) {
      var offset = i * 64;
      block.Clear();
      if (twoRows && i == blocks - 2) {
        var remainder = data[offset..];
        remainder.CopyTo(block);
        block[remainder.Length] = 0x80;
      } else if (i == blocks - 1) {
        // last block: bit count, then (single-row case) the message tail and the 0x80 pad, then the marker
        BinaryPrimitives.WriteUInt32LittleEndian(block, byteCount << 3);
        if (!twoRows) {
          var remainder = data[offset..];
          remainder.CopyTo(block[4..]);
          block[4 + remainder.Length] = 0x80;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(block[60..], 1 | (byteCount << 1));
      } else
        data.Slice(offset, 64).CopyTo(block);
      for (var k = 0; k < 16; ++k)
        x[k] = BinaryPrimitives.ReadUInt32LittleEndian(block[(k * 4)..]);
      Md5Compress(state, x);
    }
    var result = new byte[16];
    for (var k = 0; k < 4; ++k)
      BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(k * 4), state[k]);
    return result;
  }

  private static readonly int[] Shifts = [7, 12, 17, 22, 5, 9, 14, 20, 4, 11, 16, 23, 6, 10, 15, 21];

  private static readonly uint[] Sines = [
    0xd76aa478, 0xe8c7b756, 0x242070db, 0xc1bdceee, 0xf57c0faf, 0x4787c62a, 0xa8304613, 0xfd469501,
    0x698098d8, 0x8b44f7af, 0xffff5bb1, 0x895cd7be, 0x6b901122, 0xfd987193, 0xa679438e, 0x49b40821,
    0xf61e2562, 0xc040b340, 0x265e5a51, 0xe9b6c7aa, 0xd62f105d, 0x02441453, 0xd8a1e681, 0xe7d3fbc8,
    0x21e1cde6, 0xc33707d6, 0xf4d50d87, 0x455a14ed, 0xa9e3e905, 0xfcefa3f8, 0x676f02d9, 0x8d2a4c8a,
    0xfffa3942, 0x8771f681, 0x6d9d6122, 0xfde5380c, 0xa4beea44, 0x4bdecfa9, 0xf6bb4b60, 0xbebfbc70,
    0x289b7ec6, 0xeaa127fa, 0xd4ef3085, 0x04881d05, 0xd9d4d039, 0xe6db99e5, 0x1fa27cf8, 0xc4ac5665,
    0xf4292244, 0x432aff97, 0xab9423a7, 0xfc93a039, 0x655b59c3, 0x8f0ccc92, 0xffeff47d, 0x85845dd1,
    0x6fa87e4f, 0xfe2ce6e0, 0xa3014314, 0x4e0811a1, 0xf7537e82, 0xbd3af235, 0x2ad7d2bb, 0xeb86d391,
  ];

  /// <summary>The MD5 block function (RFC 1321) over one 16-word block.</summary>
  private static void Md5Compress(uint[] state, ReadOnlySpan<uint> x) {
    uint a = state[0], b = state[1], c = state[2], d = state[3];
    for (var i = 0; i < 64; ++i) {
      uint f;
      int g;
      switch (i >> 4) {
        case 0: f = (b & c) | (~b & d); g = i; break;
        case 1: f = (b & d) | (c & ~d); g = (5 * i + 1) & 15; break;
        case 2: f = b ^ c ^ d; g = (3 * i + 5) & 15; break;
        default: f = c ^ (b | ~d); g = (7 * i) & 15; break;
      }
      var rotated = BitOperations.RotateLeft(a + f + x[g] + Sines[i], Shifts[((i >> 4) << 2) | (i & 3)]);
      a = d; d = c; c = b;
      b += rotated;
    }
    state[0] += a; state[1] += b; state[2] += c; state[3] += d;
  }
}
