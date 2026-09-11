#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Qcow2;

/// <summary>
/// Writes canonical self-contained QCOW2 v2 images with 64 KiB clusters and
/// 16-bit eager refcounts. Sparse mode leaves all-zero guest clusters
/// unallocated; dense mode physically allocates every guest cluster without
/// changing guest bytes.
/// </summary>
public sealed class Qcow2Writer {
  private const int ClusterBits = 16;
  private const int ClusterSize = 1 << ClusterBits;
  private const int L2EntriesPerCluster = ClusterSize / sizeof(ulong);
  private const int RefcountEntriesPerBlock = ClusterSize / sizeof(ushort);
  private const int RefcountTableEntriesPerCluster = ClusterSize / sizeof(ulong);
  private byte[]? _diskData;

  /// <summary>Sets the raw guest disk image to wrap.</summary>
  public void SetDiskImage(byte[] data) {
    ArgumentNullException.ThrowIfNull(data);
    _diskData = data;
  }

  /// <summary>Writes a canonical sparse QCOW2 image.</summary>
  public void WriteTo(Stream output) => WriteTo(output, sparse: true);

  /// <summary>
  /// Writes a canonical QCOW2 image.
  /// </summary>
  /// <param name="output">Destination stream.</param>
  /// <param name="sparse">When true, all-zero guest clusters have zero L2 entries.
  /// When false, every guest cluster receives physical storage.</param>
  public void WriteTo(Stream output, bool sparse) {
    ArgumentNullException.ThrowIfNull(output);
    if (!output.CanWrite)
      throw new ArgumentException("QCOW2 output must be writable.", nameof(output));
    var data = _diskData ?? throw new InvalidOperationException("No disk image set.");

    if (output.CanSeek) {
      output.Position = 0;
      output.SetLength(0);
    }

    var virtualSize = (long)data.Length;
    var guestClusterCount = checked((int)((virtualSize + ClusterSize - 1) / ClusterSize));
    var l1Size = (guestClusterCount + L2EntriesPerCluster - 1) / L2EntriesPerCluster;
    if (l1Size > L2EntriesPerCluster)
      throw new NotSupportedException("QCOW2 byte-array writer requires more than one L1 table cluster.");

    var physicalDataIndex = new int[guestClusterCount];
    Array.Fill(physicalDataIndex, -1);
    var allocatedGuestClusters = 0;
    for (var cluster = 0; cluster < guestClusterCount; ++cluster) {
      if (sparse && IsGuestClusterZero(data, cluster))
        continue;
      physicalDataIndex[cluster] = allocatedGuestClusters++;
    }

    const int headerClusters = 1;
    const int l1Clusters = 1;
    const int refcountTableClusters = 1;
    if (l1Size > RefcountTableEntriesPerCluster)
      throw new NotSupportedException("QCOW2 writer L1 geometry exceeds the supported table layout.");

    var structuralWithoutRefcountBlocks =
      headerClusters + l1Clusters + l1Size + refcountTableClusters;
    var refcountBlockClusters = 1;
    while (true) {
      var total = checked(structuralWithoutRefcountBlocks + refcountBlockClusters + allocatedGuestClusters);
      var needed = Math.Max(1, (total + RefcountEntriesPerBlock - 1) / RefcountEntriesPerBlock);
      if (needed == refcountBlockClusters)
        break;
      refcountBlockClusters = needed;
    }
    if (refcountBlockClusters > RefcountTableEntriesPerCluster)
      throw new NotSupportedException("QCOW2 writer requires more than one refcount table cluster.");

    var l1TableOffset = (long)ClusterSize;
    var l2TablesStart = 2L * ClusterSize;
    var refcountTableOffset = checked(l2TablesStart + (long)l1Size * ClusterSize);
    var refcountBlocksStart = checked(refcountTableOffset + ClusterSize);
    var dataStart = checked(refcountBlocksStart + (long)refcountBlockClusters * ClusterSize);
    var totalPhysicalClusters = checked(structuralWithoutRefcountBlocks + refcountBlockClusters + allocatedGuestClusters);

    WriteHeader(output, virtualSize, l1Size, l1TableOffset, refcountTableOffset);
    WriteL1(output, l1Size, l2TablesStart);
    WriteL2Tables(output, data, guestClusterCount, l1Size, physicalDataIndex, dataStart);
    WriteRefcountTable(output, refcountBlockClusters, refcountBlocksStart);
    WriteRefcountBlocks(output, refcountBlockClusters, totalPhysicalClusters);
    WriteGuestData(output, data, guestClusterCount, physicalDataIndex);
  }

  private static void WriteHeader(
      Stream output,
      long virtualSize,
      int l1Size,
      long l1TableOffset,
      long refcountTableOffset) {
    var header = new byte[ClusterSize];
    Qcow2Structures.Magic.CopyTo(header);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 2);
    BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(8), 0);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), 0);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20), ClusterBits);
    BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(24), (ulong)virtualSize);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(32), 0);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(36), (uint)l1Size);
    BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(40), (ulong)l1TableOffset);
    BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(48), (ulong)refcountTableOffset);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(56), 1);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(60), 0);
    BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(64), 0);
    output.Write(header);
  }

  private static void WriteL1(Stream output, int l1Size, long l2TablesStart) {
    var l1 = new byte[ClusterSize];
    for (var i = 0; i < l1Size; ++i) {
      var l2Offset = checked(l2TablesStart + (long)i * ClusterSize);
      BinaryPrimitives.WriteUInt64BigEndian(
        l1.AsSpan(i * sizeof(ulong)),
        (ulong)l2Offset | Qcow2Structures.CopiedFlag);
    }
    output.Write(l1);
  }

  private static void WriteL2Tables(
      Stream output,
      byte[] data,
      int guestClusterCount,
      int l1Size,
      int[] physicalDataIndex,
      long dataStart) {
    var guestCluster = 0;
    for (var table = 0; table < l1Size; ++table) {
      var l2 = new byte[ClusterSize];
      for (var entry = 0; entry < L2EntriesPerCluster && guestCluster < guestClusterCount;
           ++entry, ++guestCluster) {
        var physicalIndex = physicalDataIndex[guestCluster];
        if (physicalIndex < 0)
          continue;
        var hostOffset = checked(dataStart + (long)physicalIndex * ClusterSize);
        BinaryPrimitives.WriteUInt64BigEndian(
          l2.AsSpan(entry * sizeof(ulong)),
          (ulong)hostOffset | Qcow2Structures.CopiedFlag);
      }
      output.Write(l2);
    }
  }

  private static void WriteRefcountTable(Stream output, int blockCount, long blocksStart) {
    var table = new byte[ClusterSize];
    for (var block = 0; block < blockCount; ++block) {
      var offset = checked(blocksStart + (long)block * ClusterSize);
      BinaryPrimitives.WriteUInt64BigEndian(table.AsSpan(block * sizeof(ulong)), (ulong)offset);
    }
    output.Write(table);
  }

  private static void WriteRefcountBlocks(Stream output, int blockCount, int totalPhysicalClusters) {
    for (var block = 0; block < blockCount; ++block) {
      var refcounts = new byte[ClusterSize];
      var globalStart = block * RefcountEntriesPerBlock;
      var count = Math.Clamp(totalPhysicalClusters - globalStart, 0, RefcountEntriesPerBlock);
      for (var entry = 0; entry < count; ++entry)
        BinaryPrimitives.WriteUInt16BigEndian(refcounts.AsSpan(entry * sizeof(ushort)), 1);
      output.Write(refcounts);
    }
  }

  private static void WriteGuestData(Stream output, byte[] data, int guestClusterCount, int[] physicalDataIndex) {
    for (var cluster = 0; cluster < guestClusterCount; ++cluster) {
      if (physicalDataIndex[cluster] < 0)
        continue;

      var offset = cluster * ClusterSize;
      var length = Math.Min(ClusterSize, data.Length - offset);
      output.Write(data.AsSpan(offset, length));
      if (length < ClusterSize)
        WriteZeroPadding(output, ClusterSize - length);
    }
  }

  private static bool IsGuestClusterZero(byte[] data, int clusterIndex) {
    var offset = clusterIndex * ClusterSize;
    var length = Math.Min(ClusterSize, data.Length - offset);
    foreach (var value in data.AsSpan(offset, Math.Max(0, length)))
      if (value != 0)
        return false;
    return true;
  }

  private static void WriteZeroPadding(Stream output, int length) {
    Span<byte> zeros = stackalloc byte[Math.Min(length, 4096)];
    var remaining = length;
    while (remaining > 0) {
      var chunk = Math.Min(remaining, zeros.Length);
      output.Write(zeros[..chunk]);
      remaining -= chunk;
    }
  }
}
