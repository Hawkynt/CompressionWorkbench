using System.Buffers.Binary;

namespace FileFormat.AndroidSparse;

/// <summary>
/// Expands Android sparse images to their raw form and packs raw images back
/// into the sparse container. The layout is a 28-byte file header followed by
/// <c>total_chunks</c> records; each record is a 12-byte chunk header
/// (<c>chunk_type</c> u16, <c>reserved</c> u16, <c>chunk_sz</c> u32 in blocks,
/// <c>total_sz</c> u32 in bytes) optionally followed by RAW literal bytes or a
/// 4-byte FILL pattern. DONT_CARE regions expand to zero; CRC32 records carry a
/// 4-byte running checksum but no output blocks.
/// </summary>
internal static class AndroidSparseCodec {

  /// <summary>Returns true when <paramref name="data"/> starts with the sparse magic.</summary>
  public static bool HasMagic(ReadOnlySpan<byte> data)
    => data.Length >= 4 &&
       BinaryPrimitives.ReadUInt32LittleEndian(data) == AndroidSparseConstants.Magic;

  /// <summary>Parses the 28-byte file header. Throws on a bad magic/short buffer.</summary>
  public static AndroidSparseHeader ParseHeader(ReadOnlySpan<byte> data) {
    if (data.Length < AndroidSparseConstants.FileHeaderSize)
      throw new InvalidDataException("Sparse image truncated: header shorter than 28 bytes.");
    var magic = BinaryPrimitives.ReadUInt32LittleEndian(data);
    if (magic != AndroidSparseConstants.Magic)
      throw new InvalidDataException("Not an Android sparse image (bad magic).");
    return new AndroidSparseHeader(
      Magic: magic,
      MajorVersion: BinaryPrimitives.ReadUInt16LittleEndian(data[4..]),
      MinorVersion: BinaryPrimitives.ReadUInt16LittleEndian(data[6..]),
      FileHeaderSize: BinaryPrimitives.ReadUInt16LittleEndian(data[8..]),
      ChunkHeaderSize: BinaryPrimitives.ReadUInt16LittleEndian(data[10..]),
      BlockSize: BinaryPrimitives.ReadUInt32LittleEndian(data[12..]),
      TotalBlocks: BinaryPrimitives.ReadUInt32LittleEndian(data[16..]),
      TotalChunks: BinaryPrimitives.ReadUInt32LittleEndian(data[20..]),
      ImageChecksum: BinaryPrimitives.ReadUInt32LittleEndian(data[24..]));
  }

  /// <summary>
  /// Expands a sparse image into its raw contents. Tolerant: stops cleanly if a
  /// chunk runs past the buffer instead of throwing, so partial images still
  /// yield the bytes decoded so far.
  /// </summary>
  public static byte[] Expand(ReadOnlySpan<byte> data) {
    var header = ParseHeader(data);
    var blockSize = header.BlockSize;
    var headerLen = header.FileHeaderSize == 0 ? AndroidSparseConstants.FileHeaderSize : header.FileHeaderSize;
    var chunkHeaderLen = header.ChunkHeaderSize == 0 ? AndroidSparseConstants.ChunkHeaderSize : header.ChunkHeaderSize;

    using var output = new MemoryStream(checked((int)Math.Min(header.ExpandedLength, int.MaxValue)));
    var pos = headerLen;
    for (var c = 0; c < header.TotalChunks; ++c) {
      if (pos + chunkHeaderLen > data.Length)
        break;
      var chunkType = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
      var chunkSizeBlocks = BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 4)..]);
      var totalSize = BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 8)..]);
      var payloadPos = pos + chunkHeaderLen;
      var outBytes = (long)chunkSizeBlocks * blockSize;

      switch (chunkType) {
        case AndroidSparseConstants.ChunkTypeRaw: {
          if (payloadPos + outBytes > data.Length)
            outBytes = data.Length - payloadPos;
          if (outBytes > 0)
            output.Write(data.Slice(payloadPos, (int)outBytes));
          break;
        }
        case AndroidSparseConstants.ChunkTypeFill: {
          if (payloadPos + 4 > data.Length)
            break;
          var pattern = data.Slice(payloadPos, 4);
          WriteFill(output, pattern, outBytes);
          break;
        }
        case AndroidSparseConstants.ChunkTypeDontCare: {
          WriteZeros(output, outBytes);
          break;
        }
        case AndroidSparseConstants.ChunkTypeCrc32:
          // No output blocks; the 4-byte running CRC is metadata only.
          break;
        default:
          // Unknown chunk: skip its declared payload and continue.
          break;
      }

      // Advance by the chunk's declared total size, guarding against corruption.
      var advance = totalSize >= chunkHeaderLen ? totalSize : (uint)chunkHeaderLen;
      pos += (int)advance;
      if (pos <= payloadPos - chunkHeaderLen)
        break; // no forward progress — bail rather than loop forever
    }

    return output.ToArray();
  }

  /// <summary>
  /// Packs a raw image into a sparse image. Consecutive all-zero blocks become
  /// a single DONT_CARE chunk; every other run becomes a RAW chunk. A trailing
  /// partial block is zero-padded to a full block so the geometry stays exact.
  /// </summary>
  public static byte[] Build(ReadOnlySpan<byte> raw, uint blockSize) {
    if (blockSize == 0 || blockSize % 4 != 0)
      throw new ArgumentException("Block size must be a non-zero multiple of 4.", nameof(blockSize));

    var totalBlocks = (uint)((raw.Length + blockSize - 1) / blockSize);

    // Build chunk descriptors first so we can fill total_chunks in the header.
    var chunks = new List<(bool IsRaw, uint Blocks, int RawOffset)>();
    var block = 0u;
    var offset = 0;
    while (block < totalBlocks) {
      var isZero = IsZeroBlock(raw, offset, blockSize);
      var runStart = offset;
      var runBlocks = 0u;
      while (block < totalBlocks && IsZeroBlock(raw, offset, blockSize) == isZero) {
        ++block;
        ++runBlocks;
        offset += (int)blockSize;
      }
      chunks.Add((!isZero, runBlocks, runStart));
    }

    using var ms = new MemoryStream();
    Span<byte> hdr = stackalloc byte[AndroidSparseConstants.FileHeaderSize];
    BinaryPrimitives.WriteUInt32LittleEndian(hdr, AndroidSparseConstants.Magic);
    BinaryPrimitives.WriteUInt16LittleEndian(hdr[4..], AndroidSparseConstants.MajorVersion);
    BinaryPrimitives.WriteUInt16LittleEndian(hdr[6..], AndroidSparseConstants.MinorVersion);
    BinaryPrimitives.WriteUInt16LittleEndian(hdr[8..], AndroidSparseConstants.FileHeaderSize);
    BinaryPrimitives.WriteUInt16LittleEndian(hdr[10..], AndroidSparseConstants.ChunkHeaderSize);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[12..], blockSize);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[16..], totalBlocks);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[20..], (uint)chunks.Count);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[24..], 0); // image checksum unused
    ms.Write(hdr);

    Span<byte> chunkHdr = stackalloc byte[AndroidSparseConstants.ChunkHeaderSize];
    foreach (var (isRaw, blocks, rawOffset) in chunks) {
      var payloadBytes = (long)blocks * blockSize;
      var type = isRaw ? AndroidSparseConstants.ChunkTypeRaw : AndroidSparseConstants.ChunkTypeDontCare;
      var totalSize = AndroidSparseConstants.ChunkHeaderSize + (isRaw ? payloadBytes : 0);
      BinaryPrimitives.WriteUInt16LittleEndian(chunkHdr, type);
      BinaryPrimitives.WriteUInt16LittleEndian(chunkHdr[2..], 0);
      BinaryPrimitives.WriteUInt32LittleEndian(chunkHdr[4..], blocks);
      BinaryPrimitives.WriteUInt32LittleEndian(chunkHdr[8..], (uint)totalSize);
      ms.Write(chunkHdr);

      if (!isRaw)
        continue;

      // Emit exactly blocks*blockSize bytes, zero-padding a trailing short block.
      var available = Math.Max(0, raw.Length - rawOffset);
      var copy = (int)Math.Min(payloadBytes, available);
      if (copy > 0)
        ms.Write(raw.Slice(rawOffset, copy));
      var pad = (int)(payloadBytes - copy);
      if (pad > 0)
        WriteZeros(ms, pad);
    }

    return ms.ToArray();
  }

  private static bool IsZeroBlock(ReadOnlySpan<byte> raw, int offset, uint blockSize) {
    var end = (int)Math.Min((long)offset + blockSize, raw.Length);
    for (var i = offset; i < end; ++i)
      if (raw[i] != 0)
        return false;
    return true;
  }

  private static void WriteFill(Stream output, ReadOnlySpan<byte> pattern, long totalBytes) {
    Span<byte> buffer = stackalloc byte[4096];
    for (var i = 0; i < buffer.Length; ++i)
      buffer[i] = pattern[i % 4];
    var remaining = totalBytes;
    while (remaining > 0) {
      var n = (int)Math.Min(remaining, buffer.Length);
      output.Write(buffer[..n]);
      remaining -= n;
    }
  }

  private static void WriteZeros(Stream output, long count) {
    Span<byte> zeros = stackalloc byte[4096];
    var remaining = count;
    while (remaining > 0) {
      var n = (int)Math.Min(remaining, zeros.Length);
      output.Write(zeros[..n]);
      remaining -= n;
    }
  }
}
