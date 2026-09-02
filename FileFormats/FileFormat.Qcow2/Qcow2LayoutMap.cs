#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileFormat.Qcow2;

/// <summary>
/// Walks a QCOW2 image and emits the byte-level layout: header, L1 table,
/// L2 tables, refcount table, refcount blocks, and data clusters.
/// </summary>
public static class Qcow2LayoutMap {

  private static readonly byte[] Magic = [0x51, 0x46, 0x49, 0xFB];

  /// <summary>
  /// Enumerates the value.
  /// </summary>
public static IEnumerable<DefragBlockInfo> Enumerate(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    stream.Position = 0;

    if (stream.Length < 72)
      yield break;

    var buf = new byte[stream.Length];
    stream.Position = 0;
    stream.ReadExactly(buf);

    if (!buf.AsSpan(0, 4).SequenceEqual(Magic))
      yield break;

    var version = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(4));
    if (version is not (2 or 3))
      yield break;

    var clusterBits = (int)BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(20));
    if (clusterBits < 9 || clusterBits > 21)
      yield break;

    var clusterSize = 1 << clusterBits;
    var l2Entries = clusterSize / 8;
    var virtualSize = (long)BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(24));
    var l1Size = (int)BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(36));
    var l1TableOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(40));
    var refcountTableOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(48));
    var refcountTableClusters = (int)BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(56));

    // Header cluster
    yield return new DefragBlockInfo(0, clusterSize, DefragBlockKind.MetadataReserved,
      FileName: $"QCOW2 Header (v{version})");

    // L1 table
    if (l1Size > 0 && l1TableOffset > 0) {
      var l1ByteSize = (long)l1Size * 8;
      var l1AlignedSize = ((l1ByteSize + clusterSize - 1) / clusterSize) * clusterSize;
      yield return new DefragBlockInfo(l1TableOffset, l1AlignedSize, DefragBlockKind.MetadataReserved,
        FileName: $"L1 Table ({l1Size} entries)");

      // Walk L1 -> L2 tables -> data clusters
      for (var l1Idx = 0; l1Idx < l1Size; l1Idx++) {
        var l1EntryOff = (int)(l1TableOffset + l1Idx * 8L);
        if (l1EntryOff + 8 > buf.Length) break;

        var l1Entry = BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(l1EntryOff));
        var l2TableOffset = (long)(l1Entry & 0x00FFFFFFFFFFFE00UL);
        if (l2TableOffset == 0) continue;

        yield return new DefragBlockInfo(l2TableOffset, clusterSize, DefragBlockKind.MetadataReserved,
          FileName: $"L2 Table {l1Idx}");

        // Data clusters referenced by this L2 table
        var totalClusters = (int)((virtualSize + clusterSize - 1) / clusterSize);
        for (var l2Idx = 0; l2Idx < l2Entries; l2Idx++) {
          var clusterIdx = l1Idx * l2Entries + l2Idx;
          if (clusterIdx >= totalClusters) break;

          var l2EntryOff = (int)(l2TableOffset + l2Idx * 8L);
          if (l2EntryOff + 8 > buf.Length) break;

          var l2Entry = BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(l2EntryOff));
          if (l2Entry == 0) continue;

          var isCompressed = (l2Entry & (1UL << 62)) != 0;
          if (isCompressed) {
            // Compressed cluster: size is encoded in the descriptor
            var compSizeBits = clusterBits - 8;
            var descriptor = l2Entry & 0x3FFFFFFFFFFFFFFFUL;
            var compSizeMask = (1UL << compSizeBits) - 1UL;
            var compSize = (long)((descriptor & compSizeMask) + 1);
            var hostSectors = descriptor >> compSizeBits;
            var hostOffset = (long)hostSectors * 512;

            yield return new DefragBlockInfo(hostOffset, compSize, DefragBlockKind.Used,
              FileName: $"Cluster {clusterIdx} (compressed)",
              Classification: DefragBlockClass.Cold);
          } else {
            var hostOffset = (long)(l2Entry & 0x00FFFFFFFFFFFE00UL);
            if (hostOffset > 0) {
              yield return new DefragBlockInfo(hostOffset, clusterSize, DefragBlockKind.Used,
                FileName: $"Cluster {clusterIdx}",
                Classification: DefragBlockClass.Normal);
            }
          }
        }
      }
    }

    // Refcount table
    if (refcountTableOffset > 0 && refcountTableClusters > 0) {
      var rtSize = (long)refcountTableClusters * clusterSize;
      yield return new DefragBlockInfo(refcountTableOffset, rtSize, DefragBlockKind.MetadataReserved,
        FileName: "Refcount Table");

      // Walk refcount table entries to find refcount blocks
      var rtEntries = (int)(rtSize / 8);
      for (var i = 0; i < rtEntries; i++) {
        var entryOff = (int)(refcountTableOffset + i * 8L);
        if (entryOff + 8 > buf.Length) break;

        var rbOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(entryOff));
        if (rbOffset == 0) continue;

        yield return new DefragBlockInfo(rbOffset, clusterSize, DefragBlockKind.MetadataReserved,
          FileName: $"Refcount Block {i}");
      }
    }
  }
}
