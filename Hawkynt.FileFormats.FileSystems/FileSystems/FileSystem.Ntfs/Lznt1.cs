#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Ntfs;

/// <summary>
/// LZNT1, the codec of NTFS compressed files ([MS-XCA] section 2.5): a sequence of chunks of at
/// most 4096 uncompressed bytes, each behind a 16-bit header (bit 15 compressed, bits 12-14 the
/// signature 3, bits 0-11 the chunk's stored size - 3). A compressed chunk is flag bytes, each
/// governing eight literals or 16-bit back-references whose displacement field grows with the
/// position in the chunk: at least 4 bits, as many as the position needs, the rest is length.
/// </summary>
public static class Lznt1 {
  private const int ChunkSize = 4096;
  private const int MinMatch = 3;
  private const ushort CompressedFlag = 0x8000;
  private const ushort Signature = 0x3000;
  private const int HashBits = 12;
  private const int MaxChain = 64;

  /// <summary>Decompresses LZNT1 data into exactly <paramref name="uncompressedSize"/> bytes (zero-padded when the data ends early).</summary>
  public static byte[] Decompress(ReadOnlySpan<byte> input, int uncompressedSize) {
    var output = new byte[uncompressedSize];
    var outPos = 0;
    var pos = 0;

    while (pos + 2 <= input.Length && outPos < uncompressedSize) {
      var header = BinaryPrimitives.ReadUInt16LittleEndian(input[pos..]);
      pos += 2;
      if (header == 0)
        break;

      var stored = (header & 0x0FFF) + 1;
      if (pos + stored > input.Length)
        throw new InvalidDataException("LZNT1 chunk runs past the end of the data.");

      var chunk = input.Slice(pos, stored);
      pos += stored;

      if ((header & CompressedFlag) == 0) {
        var copy = Math.Min(chunk.Length, uncompressedSize - outPos);
        chunk[..copy].CopyTo(output.AsSpan(outPos));
        outPos += copy;
        continue;
      }

      outPos = DecompressChunk(chunk, output, outPos);
    }

    return output;
  }

  private static int DecompressChunk(ReadOnlySpan<byte> chunk, byte[] output, int chunkStart) {
    var outPos = chunkStart;
    var limit = Math.Min(output.Length, chunkStart + ChunkSize);
    var pos = 0;
    while (pos < chunk.Length && outPos < limit) {
      var flags = chunk[pos++];
      for (var bit = 0; bit < 8 && pos < chunk.Length && outPos < limit; ++bit, flags >>= 1) {
        if ((flags & 1) == 0) {
          output[outPos++] = chunk[pos++];
          continue;
        }

        if (pos + 2 > chunk.Length)
          throw new InvalidDataException("LZNT1 back-reference cut off at the end of a chunk.");

        var token = BinaryPrimitives.ReadUInt16LittleEndian(chunk[pos..]);
        pos += 2;
        var lengthBits = 16 - DisplacementBits(outPos - chunkStart);
        var length = (token & ((1 << lengthBits) - 1)) + MinMatch;
        var displacement = (token >> lengthBits) + 1;
        if (displacement > outPos - chunkStart)
          throw new InvalidDataException("LZNT1 back-reference points before the start of its chunk.");

        var end = Math.Min(limit, outPos + length);
        for (; outPos < end; ++outPos)
          output[outPos] = output[outPos - displacement];
      }
    }

    return outPos;
  }

  /// <summary>Compresses data as LZNT1: each 4096-byte chunk compressed, or stored when that is not smaller.</summary>
  public static byte[] Compress(ReadOnlySpan<byte> input) {
    using var ms = new MemoryStream(input.Length + input.Length / 16 + 16);
    Span<byte> header = stackalloc byte[2];
    var packed = new byte[ChunkSize + ChunkSize / 8 + 16];
    var head = new int[1 << HashBits];
    var prev = new int[ChunkSize];

    for (var offset = 0; offset < input.Length; offset += ChunkSize) {
      var chunk = input.Slice(offset, Math.Min(ChunkSize, input.Length - offset));
      var size = CompressChunk(chunk, packed, head, prev);
      if (size < chunk.Length) {
        BinaryPrimitives.WriteUInt16LittleEndian(header, (ushort)(CompressedFlag | Signature | (size - 1)));
        ms.Write(header);
        ms.Write(packed, 0, size);
      } else {
        BinaryPrimitives.WriteUInt16LittleEndian(header, (ushort)(Signature | (chunk.Length - 1)));
        ms.Write(header);
        ms.Write(chunk);
      }
    }

    return ms.ToArray();
  }

  /// <summary>Compresses one chunk into <paramref name="packed"/>; returns its size (which may exceed the chunk).</summary>
  private static int CompressChunk(ReadOnlySpan<byte> chunk, byte[] packed, int[] head, int[] prev) {
    Array.Fill(head, -1);
    var outPos = 0;
    var pos = 0;
    var inserted = 0;

    while (pos < chunk.Length) {
      var flagPos = outPos++;
      var flags = 0;
      for (var bit = 0; bit < 8 && pos < chunk.Length; ++bit) {
        var lengthBits = 16 - DisplacementBits(pos);
        var maxLength = Math.Min((1 << lengthBits) - 1 + MinMatch, chunk.Length - pos);
        var maxDisplacement = 1 << (16 - lengthBits);

        for (; inserted < pos; ++inserted)
          Insert(chunk, inserted, head, prev);

        var (bestLength, bestDisplacement) = FindMatch(chunk, pos, maxLength, maxDisplacement, head, prev);
        if (bestLength >= MinMatch) {
          flags |= 1 << bit;
          var token = ((bestDisplacement - 1) << lengthBits) | (bestLength - MinMatch);
          BinaryPrimitives.WriteUInt16LittleEndian(packed.AsSpan(outPos), (ushort)token);
          outPos += 2;
          pos += bestLength;
        } else
          packed[outPos++] = chunk[pos++];
      }

      packed[flagPos] = (byte)flags;
    }

    return outPos;
  }

  private static (int Length, int Displacement) FindMatch(ReadOnlySpan<byte> chunk, int pos, int maxLength, int maxDisplacement, int[] head, int[] prev) {
    if (maxLength < MinMatch)
      return (0, 0);

    var bestLength = 0;
    var bestDisplacement = 0;
    var candidate = head[Hash(chunk, pos)];
    for (var steps = 0; candidate >= 0 && steps < MaxChain; ++steps, candidate = prev[candidate]) {
      var displacement = pos - candidate;
      if (displacement > maxDisplacement)
        break;

      var length = 0;
      while (length < maxLength && chunk[candidate + length] == chunk[pos + length])
        ++length;

      if (length > bestLength) {
        bestLength = length;
        bestDisplacement = displacement;
        if (length == maxLength)
          break;
      }
    }

    return (bestLength, bestDisplacement);
  }

  private static void Insert(ReadOnlySpan<byte> chunk, int pos, int[] head, int[] prev) {
    if (pos + MinMatch > chunk.Length)
      return;

    var h = Hash(chunk, pos);
    prev[pos] = head[h];
    head[h] = pos;
  }

  private static int Hash(ReadOnlySpan<byte> chunk, int pos)
    => (int)(((uint)(chunk[pos] << 16 | chunk[pos + 1] << 8 | chunk[pos + 2]) * 2654435761u) >> (32 - HashBits));

  /// <summary>Bits of a back-reference that hold the displacement at <paramref name="position"/> bytes into the chunk.</summary>
  private static int DisplacementBits(int position) {
    var bits = 4;
    while (1 << bits < position)
      ++bits;

    return bits;
  }
}
