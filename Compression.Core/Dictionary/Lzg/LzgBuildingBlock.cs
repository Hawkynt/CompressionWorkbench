using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Lzg;

/// <summary>
/// LZG — Marcus Geelnard's liblzg LZ77 codec: an escape-byte (0xFF) token stream
/// over a small 2 KiB sliding window, tuned for a simple, fast, dependency-free
/// decoder rather than maximal ratio.
/// </summary>
/// <remarks>
/// <para>
/// Modeled on the publicly documented liblzg LZG1 method (see references below).
/// Every byte in the payload is either a literal, or the escape byte
/// (<c>0xFF</c>) starting a two-byte-or-longer token:
/// </para>
/// <list type="bullet">
///   <item><description><c>0xFF 0x00</c> — an escaped literal <c>0xFF</c>.</description></item>
///   <item><description><c>0xFF len offHi offLo</c> — a back-reference: <c>len</c>
///     is <c>length - 2</c> (so length 3..257, never 0, which would collide with
///     the escaped-literal form), and <c>offHi:offLo</c> is a big-endian 16-bit
///     distance (1..65535).</description></item>
/// </list>
/// <para>
/// Matches are found with a hash-chain over 3-byte hashes within a 2 KiB window
/// (liblzg's LZG1 window size), minimum match length 3, maximum match length 257
/// (what a single <c>len</c> byte can express).
/// </para>
/// <para>
/// This is a clean-room implementation written from the format description, not a
/// port of Geelnard's reference C source; only this building block's own
/// round-trip is guaranteed. Unlike the liblzg container (16-byte header with
/// Adler-32 checksum and a raw-copy fallback method), the uncompressed length here
/// is carried by the standard 4-byte little-endian building-block header.
/// </para>
/// <para>References:</para>
/// <list type="bullet">
///   <item><description>liblzg — https://github.com/mbitsnbites/liblzg</description></item>
///   <item><description>liblzg project site — https://liblzg.bitsnbites.eu/</description></item>
/// </list>
/// </remarks>
public sealed class LzgBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Lzg";
  /// <inheritdoc/>
  public string DisplayName => "LZG";
  /// <inheritdoc/>
  public string Description => "Geelnard's liblzg LZ77 codec: escape-byte tokens over a 2 KiB window, tuned for a small dependency-free decoder";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  private const byte Escape = 0xFF;
  private const int WindowSize = 2048;
  private const int MinMatch = 3;
  private const int MaxMatch = 257;
  private const int HashBits = 12;
  private const int HashSize = 1 << HashBits;
  private const int MaxChainSteps = 32;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    using var ms = new MemoryStream();
    Span<byte> header = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
    ms.Write(header);

    if (data.Length == 0)
      return ms.ToArray();

    var src = data.ToArray();
    var hashHead = new int[HashSize];
    Array.Fill(hashHead, -1);
    var chain = new int[src.Length];

    var pos = 0;
    while (pos < src.Length) {
      var (bestLen, bestOff) = pos + MinMatch <= src.Length
        ? FindMatch(src, pos, hashHead, chain)
        : (0, 0);

      if (pos + 2 < src.Length)
        InsertHash(src, pos, hashHead, chain);

      if (bestLen >= MinMatch) {
        ms.WriteByte(Escape);
        ms.WriteByte((byte)(bestLen - 2));
        ms.WriteByte((byte)(bestOff >> 8));
        ms.WriteByte((byte)bestOff);

        for (var i = 1; i < bestLen && pos + i + 2 < src.Length; ++i)
          InsertHash(src, pos + i, hashHead, chain);

        pos += bestLen;
      } else {
        if (src[pos] == Escape) {
          ms.WriteByte(Escape);
          ms.WriteByte(0x00);
        } else {
          ms.WriteByte(src[pos]);
        }
        ++pos;
      }
    }

    return ms.ToArray();
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    if (originalSize == 0)
      return [];

    var payload = data[4..];
    var dst = new byte[originalSize];
    var pos = 0;
    var i = 0;

    while (pos < originalSize) {
      if (i >= payload.Length)
        throw new InvalidDataException("LZG: unexpected end of stream.");

      if (payload[i] == Escape) {
        ++i;
        if (i >= payload.Length)
          throw new InvalidDataException("LZG: truncated escape sequence.");

        if (payload[i] == 0x00) {
          dst[pos++] = Escape;
          ++i;
        } else {
          if (i + 2 >= payload.Length)
            throw new InvalidDataException("LZG: truncated match token.");

          var len = payload[i] + 2;
          var off = (payload[i + 1] << 8) | payload[i + 2];
          i += 3;

          if (off <= 0 || off > pos)
            throw new InvalidDataException($"LZG: invalid offset {off} at position {pos}.");

          for (var k = 0; k < len && pos < originalSize; ++k, ++pos)
            dst[pos] = dst[pos - off];
        }
      } else {
        dst[pos++] = payload[i++];
      }
    }

    return dst;
  }

  private static (int Length, int Offset) FindMatch(byte[] src, int pos, int[] hashHead, int[] chain) {
    var h = Hash3(src, pos);
    var candidate = hashHead[h];
    var minPos = Math.Max(0, pos - WindowSize);
    var maxLen = Math.Min(MaxMatch, src.Length - pos);
    var bestLen = 0;
    var bestOff = 0;
    var steps = MaxChainSteps;

    while (candidate >= minPos && steps-- > 0) {
      if (candidate < pos) {
        var len = 0;
        while (len < maxLen && src[candidate + len] == src[pos + len])
          ++len;

        if (len >= MinMatch && len > bestLen) {
          var dist = pos - candidate;
          if (dist <= 65535) {
            bestLen = len;
            bestOff = dist;
            if (bestLen == maxLen)
              break;
          }
        }
      }

      var prev = chain[candidate];
      if (prev >= candidate)
        break;
      candidate = prev;
    }

    return (bestLen, bestOff);
  }

  private static void InsertHash(byte[] src, int pos, int[] hashHead, int[] chain) {
    var h = Hash3(src, pos);
    chain[pos] = hashHead[h];
    hashHead[h] = pos;
  }

  private static int Hash3(byte[] data, int pos) =>
    ((data[pos] << 10) ^ (data[pos + 1] << 5) ^ data[pos + 2]) & (HashSize - 1);
}
