#pragma warning disable CS1591

using System.Buffers.Binary;
using System.Numerics;

namespace FileFormat.Lizard;

/// <summary>
/// Lizard (formerly LZ5) compression stream.
/// Frame format: magic (06 22 4D 18) + FLG + BD + ContentSize + HC + blocks + end mark.
/// Block internals use LZ4-compatible token format.
/// </summary>
public static class LizardStream {

  private static readonly byte[] Magic = [0x06, 0x22, 0x4D, 0x18];
  private const byte Flg = 0x68;  // version=01, B.Indep=1, B.Checksum=0, C.Size=1, C.Checksum=0
  private const byte Bd = 0x40;   // block max size bits 6-4 = 4 (4MB)
  private const int BlockSize = 4 * 1024 * 1024;

  /// <summary>Compresses input into the Lizard frame format.</summary>
  public static void Compress(Stream input, Stream output) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    var data = ms.ToArray();

    // Frame magic
    output.Write(Magic);

    // FLG + BD + 8-byte content size
    output.WriteByte(Flg);
    output.WriteByte(Bd);
    Span<byte> contentSizeBuf = stackalloc byte[8];
    BinaryPrimitives.WriteUInt64LittleEndian(contentSizeBuf, (ulong)data.Length);
    output.Write(contentSizeBuf);

    // Header checksum: XXHash32 over FLG + BD + ContentSize bytes, seed=0
    Span<byte> hcInput = stackalloc byte[10];
    hcInput[0] = Flg;
    hcInput[1] = Bd;
    contentSizeBuf.CopyTo(hcInput[2..]);
    var hcHash = XxHash32(hcInput);
    output.WriteByte((byte)((hcHash >> 8) & 0xFF));

    // Data blocks (up to 4MB each)
    var offset = 0;
    Span<byte> blockSizeBuf = stackalloc byte[4];
    while (offset < data.Length) {
      var chunkLen = Math.Min(BlockSize, data.Length - offset);
      var chunk = data.AsSpan(offset, chunkLen);
      var compressed = CompressBlock(chunk);

      // If compressed is larger than original, store uncompressed (bit 31 set)
      if (compressed.Length >= chunkLen) {
        BinaryPrimitives.WriteUInt32LittleEndian(blockSizeBuf, (uint)chunkLen | 0x80000000u);
        output.Write(blockSizeBuf);
        output.Write(chunk);
      } else {
        BinaryPrimitives.WriteUInt32LittleEndian(blockSizeBuf, (uint)compressed.Length);
        output.Write(blockSizeBuf);
        output.Write(compressed);
      }
      offset += chunkLen;
    }

    // End mark
    BinaryPrimitives.WriteUInt32LittleEndian(blockSizeBuf, 0);
    output.Write(blockSizeBuf);
  }

  /// <summary>Decompresses a Lizard frame stream.</summary>
  public static void Decompress(Stream input, Stream output) {
    // Read and verify magic
    Span<byte> magicBuf = stackalloc byte[4];
    input.ReadExactly(magicBuf);
    if (magicBuf[0] != Magic[0] || magicBuf[1] != Magic[1] ||
        magicBuf[2] != Magic[2] || magicBuf[3] != Magic[3])
      throw new InvalidDataException("Not a Lizard stream: invalid magic.");

    // FLG
    var flg = (byte)input.ReadByte();
    var hasCSize = (flg & 0x08) != 0;

    // BD
    _ = (byte)input.ReadByte();

    // Optional content size
    if (hasCSize) {
      Span<byte> cSizeBuf = stackalloc byte[8];
      input.ReadExactly(cSizeBuf);
      // content size is informational; not strictly needed for decompression
    }

    // HC (header checksum) — read and skip
    input.ReadByte();

    // Read blocks until end mark
    Span<byte> blockSizeBuf = stackalloc byte[4];
    while (true) {
      input.ReadExactly(blockSizeBuf);
      var rawSize = BinaryPrimitives.ReadUInt32LittleEndian(blockSizeBuf);
      if (rawSize == 0) break; // end mark

      var isUncompressed = (rawSize & 0x80000000u) != 0;
      var byteCount = (int)(rawSize & 0x7FFFFFFFu);

      var blockData = new byte[byteCount];
      input.ReadExactly(blockData);

      if (isUncompressed) {
        output.Write(blockData);
      } else {
        DecompressBlock(blockData, output);
      }
    }
  }

  // ── LZ4-compatible block compression ────────────────────────────────────────

  private static byte[] CompressBlock(ReadOnlySpan<byte> src) {
    // Hash chain matching: min match 4, max offset 65535
    var n = src.Length;
    if (n == 0) return [];

    // Hash table: maps 4-byte hash → last position seen
    const int HashBits = 16;
    const int HashSize = 1 << HashBits;
    var hashTable = new int[HashSize];
    Array.Fill(hashTable, -1);

    using var outBuf = new MemoryStream();

    var ip = 0;         // current input position
    var anchor = 0;     // start of current literal run

    // Need at least 4 bytes for a match
    while (ip < n - 4) {
      var hash = Hash4(src, ip);
      var matchPos = hashTable[hash];
      hashTable[hash] = ip;

      // Try to find a match
      var bestLen = 0;
      var bestOffset = 0;

      if (matchPos >= 0 && matchPos < ip && ip - matchPos <= 65535) {
        var candidate = matchPos;
        while (candidate >= 0 && candidate < ip && ip - candidate <= 65535) {
          if (src[candidate] == src[ip] && src[candidate + 1] == src[ip + 1] &&
              src[candidate + 2] == src[ip + 2] && src[candidate + 3] == src[ip + 3]) {
            // Extend match
            var ml = 4;
            var maxMl = Math.Min(n - ip, ip - candidate); // can't overlap in source beyond n
            // Actually max match length is limited to n - ip
            var maxMatch = n - ip - 1; // leave at least 1 byte as end-of-block safety
            if (maxMatch < 4) break;
            while (ml < maxMatch && src[candidate + ml] == src[ip + ml])
              ml++;
            if (ml > bestLen) {
              bestLen = ml;
              bestOffset = ip - candidate;
            }
          }
          // We don't maintain a full chain here, so stop after first candidate.
          break;
        }
      }

      if (bestLen < 4) {
        ip++;
        continue;
      }

      // Emit sequence: literals [anchor..ip) + match
      EmitSequence(outBuf, src, anchor, ip, bestOffset, bestLen);
      ip += bestLen;
      anchor = ip;

      // Update hash table for skipped positions
      if (ip < n - 4) {
        hashTable[Hash4(src, ip)] = ip;
      }
    }

    // Emit final literals
    EmitFinalLiterals(outBuf, src, anchor);
    return outBuf.ToArray();
  }

  private static void EmitSequence(MemoryStream output, ReadOnlySpan<byte> src,
      int literalStart, int matchStart, int matchOffset, int matchLen) {
    var litLen = matchStart - literalStart;
    var mlCode = matchLen - 4; // LZ4: match length stored as (actual - 4)

    // Token byte: high nibble = literal length (capped at 15), low nibble = match length code (capped at 15)
    var litNibble = Math.Min(litLen, 15);
    var mlNibble = Math.Min(mlCode, 15);
    output.WriteByte((byte)((litNibble << 4) | mlNibble));

    // Extra literal length bytes
    if (litLen >= 15) {
      var remaining = litLen - 15;
      while (remaining >= 255) { output.WriteByte(255); remaining -= 255; }
      output.WriteByte((byte)remaining);
    }

    // Literal bytes
    for (var i = 0; i < litLen; i++)
      output.WriteByte(src[literalStart + i]);

    // Match offset: 2 bytes LE
    output.WriteByte((byte)(matchOffset & 0xFF));
    output.WriteByte((byte)(matchOffset >> 8));

    // Extra match length bytes
    if (mlCode >= 15) {
      var remaining = mlCode - 15;
      while (remaining >= 255) { output.WriteByte(255); remaining -= 255; }
      output.WriteByte((byte)remaining);
    }
  }

  private static void EmitFinalLiterals(MemoryStream output, ReadOnlySpan<byte> src, int literalStart) {
    var litLen = src.Length - literalStart;
    if (litLen == 0) return;

    var litNibble = Math.Min(litLen, 15);
    output.WriteByte((byte)(litNibble << 4)); // match nibble = 0 (no match at end)

    if (litLen >= 15) {
      var remaining = litLen - 15;
      while (remaining >= 255) { output.WriteByte(255); remaining -= 255; }
      output.WriteByte((byte)remaining);
    }

    for (var i = 0; i < litLen; i++)
      output.WriteByte(src[literalStart + i]);
    // No match offset/length for final literals
  }

  private static void DecompressBlock(byte[] src, Stream output) {
    var result = new List<byte>(src.Length * 3);
    var ip = 0;

    while (ip < src.Length) {
      var token = src[ip++];
      var litLen = (token >> 4) & 0xF;
      var mlCode = token & 0xF;

      // Expand literal length
      if (litLen == 15) {
        int extra;
        do {
          extra = src[ip++];
          litLen += extra;
        } while (extra == 255);
      }

      // Copy literals
      for (var i = 0; i < litLen; i++)
        result.Add(src[ip++]);

      // Check if there's a match (end of block = no more data after literals)
      if (ip >= src.Length) break;

      // Match offset: 2 bytes LE
      var matchOffset = src[ip] | (src[ip + 1] << 8);
      ip += 2;

      // Expand match length
      var matchLen = mlCode + 4;
      if (mlCode == 15) {
        int extra;
        do {
          extra = src[ip++];
          matchLen += extra;
        } while (extra == 255);
      }

      // Copy match (may overlap — use byte-by-byte to handle overlapping copies correctly)
      var matchStart = result.Count - matchOffset;
      for (var i = 0; i < matchLen; i++)
        result.Add(result[matchStart + i]);
    }

    output.Write(result.ToArray());
  }

  private static int Hash4(ReadOnlySpan<byte> data, int pos) {
    var v = BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
    return (int)(((v * 2654435761u) >> 16) & 0xFFFF);
  }

  // ── XXHash32 ─────────────────────────────────────────────────────────────────

  private static uint XxHash32(ReadOnlySpan<byte> data, uint seed = 0) {
    const uint Prime1 = 2654435761u, Prime2 = 2246822519u, Prime3 = 3266489917u,
                Prime4 = 668265263u, Prime5 = 374761393u;
    uint h;
    int i = 0;
    if (data.Length >= 16) {
      uint v1 = seed + Prime1 + Prime2, v2 = seed + Prime2, v3 = seed, v4 = seed - Prime1;
      int limit = data.Length - 16;
      while (i <= limit) {
        v1 = BitOperations.RotateLeft(v1 + BinaryPrimitives.ReadUInt32LittleEndian(data[i..]) * Prime2, 13) * Prime1; i += 4;
        v2 = BitOperations.RotateLeft(v2 + BinaryPrimitives.ReadUInt32LittleEndian(data[i..]) * Prime2, 13) * Prime1; i += 4;
        v3 = BitOperations.RotateLeft(v3 + BinaryPrimitives.ReadUInt32LittleEndian(data[i..]) * Prime2, 13) * Prime1; i += 4;
        v4 = BitOperations.RotateLeft(v4 + BinaryPrimitives.ReadUInt32LittleEndian(data[i..]) * Prime2, 13) * Prime1; i += 4;
      }
      h = BitOperations.RotateLeft(v1, 1) + BitOperations.RotateLeft(v2, 7) +
          BitOperations.RotateLeft(v3, 12) + BitOperations.RotateLeft(v4, 18);
    } else {
      h = seed + Prime5;
    }
    h += (uint)data.Length;
    while (i <= data.Length - 4) { h = BitOperations.RotateLeft(h + BinaryPrimitives.ReadUInt32LittleEndian(data[i..]) * Prime3, 17) * Prime4; i += 4; }
    while (i < data.Length) { h = BitOperations.RotateLeft(h + data[i] * Prime5, 11) * Prime1; i++; }
    h ^= h >> 15; h *= Prime2; h ^= h >> 13; h *= Prime3; h ^= h >> 16;
    return h;
  }
}
