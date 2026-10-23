using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Zfs;

/// <summary>One ZAP entry: a name and its integer array.</summary>
internal sealed record ZfsZapEntry(string Name, int IntegerLength, ulong[] Values) {
  public ulong Value => this.Values.Length > 0 ? this.Values[0] : 0;
}

/// <summary>
/// Reads micro and fat ZAP objects (<c>docs/ZFS-ON-DISK.md</c>, "ZAP"). A fat ZAP is read by
/// walking every leaf block's entry chunks, so the pointer table is never needed.
/// </summary>
internal static class ZfsZap {
  private const int MicroHeaderSize = 64;
  private const int MicroEntrySize = 64;
  private const int MicroNameSize = 50;
  private const int LeafHeaderSize = 48;
  private const int ChunkSize = 24;
  private const int ChunkArrayBytes = 21;
  private const byte ChunkEntry = 252;
  private const byte ChunkArray = 251;
  private const ushort ChainEnd = 0xFFFF;

  /// <summary>Decodes the ZAP whose logical blocks <paramref name="readBlock"/> returns, block 0 first.</summary>
  public static List<ZfsZapEntry> Decode(Func<ulong, byte[]> readBlock, ulong maxBlockId) {
    var first = readBlock(0);
    if (first.Length < 16)
      throw new InvalidDataException("ZFS: ZAP block too small.");
    var kind = BinaryPrimitives.ReadUInt64LittleEndian(first);
    return kind switch {
      ZfsConstants.ZbtMicro => DecodeMicro(first),
      ZfsConstants.ZbtHeader => DecodeFat(first, readBlock, maxBlockId),
      _ => throw new InvalidDataException($"ZFS: object is not a ZAP (block type 0x{kind:X16})."),
    };
  }

  private static List<ZfsZapEntry> DecodeMicro(byte[] block) {
    var entries = new List<ZfsZapEntry>();
    for (var at = MicroHeaderSize; at + MicroEntrySize <= block.Length; at += MicroEntrySize) {
      var nameBytes = block.AsSpan(at + 14, MicroNameSize);
      if (nameBytes[0] == 0) continue;
      var length = nameBytes.IndexOf((byte)0);
      var name = Encoding.UTF8.GetString(length < 0 ? nameBytes : nameBytes[..length]);
      entries.Add(new(name, 8, [BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(at))]));
    }
    return entries;
  }

  private static List<ZfsZapEntry> DecodeFat(byte[] header, Func<ulong, byte[]> readBlock, ulong maxBlockId) {
    if (BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(8)) != ZfsConstants.ZapMagic)
      throw new InvalidDataException("ZFS: fat ZAP header magic mismatch.");
    var entries = new List<ZfsZapEntry>();
    for (ulong id = 1; id <= maxBlockId; ++id) {
      var block = readBlock(id);
      if (block.Length < LeafHeaderSize
          || BinaryPrimitives.ReadUInt64LittleEndian(block) != ZfsConstants.ZbtLeaf
          || BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(24)) != ZfsConstants.ZapLeafMagic)
        continue;
      DecodeLeaf(block, entries);
    }
    return entries;
  }

  private static void DecodeLeaf(byte[] block, List<ZfsZapEntry> entries) {
    var shift = 63 - ulong.LeadingZeroCount((ulong)block.Length);
    var hashEntries = 1 << ((int)shift - 5);
    var chunkBase = LeafHeaderSize + 2 * hashEntries;
    var chunkCount = (block.Length - chunkBase) / ChunkSize;

    for (var i = 0; i < chunkCount; ++i) {
      var at = chunkBase + i * ChunkSize;
      if (block[at] != ChunkEntry) continue;
      var valueIntLength = block[at + 1];
      var nameChunk = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(at + 4));
      var nameInts = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(at + 6));
      var valueChunk = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(at + 8));
      var valueInts = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(at + 10));
      if (valueIntLength is not (1 or 2 or 4 or 8))
        throw new InvalidDataException($"ZFS: ZAP value integer length {valueIntLength}.");

      var nameBytes = ReadArray(block, chunkBase, chunkCount, nameChunk, nameInts);
      var nameLength = nameBytes.AsSpan().IndexOf((byte)0);
      var name = Encoding.UTF8.GetString(nameBytes, 0, nameLength < 0 ? nameBytes.Length : nameLength);

      var valueBytes = ReadArray(block, chunkBase, chunkCount, valueChunk, valueInts * valueIntLength);
      var values = new ulong[valueInts];
      for (var v = 0; v < valueInts; ++v) {
        ulong x = 0;
        // Integers in leaf arrays are stored most significant byte first.
        for (var k = 0; k < valueIntLength; ++k)
          x = (x << 8) | valueBytes[v * valueIntLength + k];
        values[v] = x;
      }
      entries.Add(new(name, valueIntLength, values));
    }
  }

  private static byte[] ReadArray(byte[] block, int chunkBase, int chunkCount, int chunk, int length) {
    var bytes = new byte[length];
    var taken = 0;
    for (var guard = 0; taken < length; ++guard) {
      if (chunk == ChainEnd || chunk >= chunkCount || guard > chunkCount)
        throw new InvalidDataException("ZFS: ZAP array chain broken.");
      var at = chunkBase + chunk * ChunkSize;
      if (block[at] != ChunkArray)
        throw new InvalidDataException("ZFS: ZAP array chain reaches a non-array chunk.");
      var n = Math.Min(ChunkArrayBytes, length - taken);
      block.AsSpan(at + 1, n).CopyTo(bytes.AsSpan(taken));
      taken += n;
      chunk = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(at + 22));
    }
    return bytes;
  }
}
