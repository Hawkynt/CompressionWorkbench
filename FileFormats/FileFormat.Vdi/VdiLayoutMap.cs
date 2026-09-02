#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileFormat.Vdi;

/// <summary>
/// Walks a VDI image and emits the byte-level layout: pre-header, header,
/// block allocation map, and data blocks (allocated vs unallocated).
/// </summary>
public static class VdiLayoutMap {

  private const uint VdiSignature = 0xBEDA107F;

    /// <summary>
  /// Enumerates the value.
  /// </summary>
public static IEnumerable<DefragBlockInfo> Enumerate(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    stream.Position = 0;

    if (stream.Length < 512)
      yield break;

    var buf = new byte[stream.Length];
    stream.Position = 0;
    stream.ReadExactly(buf);

    // Verify signature at offset 64
    if (buf.Length < 392)
      yield break;

    var sig = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(64));
    if (sig != VdiSignature)
      yield break;

    var imageType = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(76));
    var offsetBlocks = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(340));
    var offsetData = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(344));
    var blockSize = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(376));
    var blockCount = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(384));
    var allocatedCount = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(388));

    if (blockSize == 0)
      yield break;

    // Pre-header + signature + header (0 to offsetBlocks)
    yield return new DefragBlockInfo(0, offsetBlocks, DefragBlockKind.MetadataReserved,
      FileName: $"VDI Header (type={imageType})");

    // Block allocation map
    var mapByteSize = (long)blockCount * 4;
    yield return new DefragBlockInfo(offsetBlocks, mapByteSize, DefragBlockKind.MetadataReserved,
      FileName: $"Block Map ({blockCount} entries, {allocatedCount} allocated)");

    // Padding between map end and data start
    var mapEnd = offsetBlocks + mapByteSize;
    if (mapEnd < offsetData) {
      yield return new DefragBlockInfo(mapEnd, offsetData - mapEnd, DefragBlockKind.Free,
        FileName: "Alignment padding");
    }

    // Data blocks
    for (uint i = 0; i < blockCount; i++) {
      var mapEntryOff = (int)(offsetBlocks + i * 4);
      if (mapEntryOff + 4 > buf.Length) break;

      var blockMapEntry = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(mapEntryOff));
      if (blockMapEntry == 0xFFFFFFFF)
        continue; // unallocated

      var physicalOffset = (long)offsetData + (long)blockMapEntry * blockSize;
      yield return new DefragBlockInfo(physicalOffset, blockSize, DefragBlockKind.Used,
        FileName: $"Block {i} (physical {blockMapEntry})",
        Classification: DefragBlockClass.Normal);
    }
  }
}
