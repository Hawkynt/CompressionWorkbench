#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Qcow2;

/// <summary>
/// Writes QCOW2 v2 disk images in WORM mode. Takes a single raw disk image and
/// wraps it in a QCOW2 container with uncompressed clusters.
/// <para>
/// Layout: header (cluster&#160;0) → L1 table (cluster&#160;1) → L2 tables → refcount table
/// → refcount block → allocated data clusters. Guest clusters that contain only
/// zeroes are left unallocated (zero L2 entries), so sparse raw disks stay sparse.
/// Each physically allocated cluster has a refcount of&#160;1, and every L1/L2 entry
/// that points at such a single-refcount cluster carries the
/// <c>QCOW_OFLAG_COPIED</c> flag (bit&#160;63). This matches the allocation semantics
/// in the QEMU QCOW2 specification and is accepted by <c>qemu-img check</c>.
/// </para>
/// </summary>
public sealed class Qcow2Writer {
  private static readonly byte[] Magic = [0x51, 0x46, 0x49, 0xFB]; // "QFI\xFB"
  private const int ClusterBits = 16;
  private const int ClusterSize = 1 << ClusterBits;               // 65536
  private const int L2EntriesPerCluster = ClusterSize / 8;        // 8192
  private const int RefcountEntriesPerCluster = ClusterSize / 2;  // 32768 (16-bit refcounts at order 4)

  private const ulong CopiedFlag = 1UL << 63;

  private byte[]? _diskData;

  /// <summary>
  /// Sets the disk image.
  /// </summary>
  public void SetDiskImage(byte[] data) {
    ArgumentNullException.ThrowIfNull(data);
    _diskData = data;
  }

  /// <summary>
  /// Writes the to to the supplied output.
  /// </summary>
  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);
    var data = _diskData ?? throw new InvalidOperationException("No disk image set.");

    var virtualSize = (long)data.Length;
    var numGuestClusters = (int)((virtualSize + ClusterSize - 1) / ClusterSize);
    var numL2Tables = (numGuestClusters + L2EntriesPerCluster - 1) / L2EntriesPerCluster;
    var allocatedGuestClusters = CountAllocatedGuestClusters(data, numGuestClusters);

    const int refcountTableClusters = 1;
    const int refcountBlockClusters = 1;
    var l1TableOffset = (long)ClusterSize;
    var l2TablesStart = 2L * ClusterSize;
    var refcountTableOffset = l2TablesStart + (long)numL2Tables * ClusterSize;
    var refcountBlockOffset = refcountTableOffset + (long)refcountTableClusters * ClusterSize;
    var dataStart = refcountBlockOffset + (long)refcountBlockClusters * ClusterSize;
    var structuralClusters = (int)(dataStart / ClusterSize);
    var totalPhysicalClusters = structuralClusters + allocatedGuestClusters;

    if (totalPhysicalClusters > RefcountEntriesPerCluster)
      throw new InvalidOperationException(
        $"qcow2 writer: image of {totalPhysicalClusters} allocated clusters exceeds single-refcount-block capacity ({RefcountEntriesPerCluster}).");

    // --- Header ---
    var hdr = new byte[ClusterSize];
    Magic.CopyTo(hdr, 0);
    BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(4), 2);
    BinaryPrimitives.WriteUInt64BigEndian(hdr.AsSpan(8), 0);
    BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(16), 0);
    BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(20), (uint)ClusterBits);
    BinaryPrimitives.WriteUInt64BigEndian(hdr.AsSpan(24), (ulong)virtualSize);
    BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(32), 0);
    BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(36), (uint)numL2Tables);
    BinaryPrimitives.WriteUInt64BigEndian(hdr.AsSpan(40), (ulong)l1TableOffset);
    BinaryPrimitives.WriteUInt64BigEndian(hdr.AsSpan(48), (ulong)refcountTableOffset);
    BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(56), refcountTableClusters);
    BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(60), 0);
    BinaryPrimitives.WriteUInt64BigEndian(hdr.AsSpan(64), 0);
    output.Write(hdr);

    // --- L1 table ---
    var l1 = new byte[ClusterSize];
    for (var i = 0; i < numL2Tables; ++i) {
      var l2Offset = l2TablesStart + (long)i * ClusterSize;
      BinaryPrimitives.WriteUInt64BigEndian(l1.AsSpan(i * 8), (ulong)l2Offset | CopiedFlag);
    }
    output.Write(l1);

    // --- L2 tables ---
    // All L2 tables are allocated structurally. A zero L2 entry means the guest
    // cluster is unallocated and therefore reads as zeroes. Non-zero guest
    // clusters are packed consecutively in the physical data area.
    var guestClusterIndex = 0;
    var physicalDataIndex = 0;
    for (var table = 0; table < numL2Tables; ++table) {
      var l2 = new byte[ClusterSize];
      for (var entry = 0; entry < L2EntriesPerCluster && guestClusterIndex < numGuestClusters;
           ++entry, ++guestClusterIndex) {
        if (IsGuestClusterZero(data, guestClusterIndex))
          continue;

        var hostOffset = dataStart + (long)physicalDataIndex * ClusterSize;
        BinaryPrimitives.WriteUInt64BigEndian(l2.AsSpan(entry * 8), (ulong)hostOffset | CopiedFlag);
        ++physicalDataIndex;
      }
      output.Write(l2);
    }

    // --- Refcount table (one entry → one refcount block) ---
    var rt = new byte[ClusterSize];
    BinaryPrimitives.WriteUInt64BigEndian(rt.AsSpan(0), (ulong)refcountBlockOffset);
    output.Write(rt);

    // --- Refcount block ---
    // The physical file is dense from cluster 0 through the final allocated
    // data cluster, so each physical cluster in that range has refcount 1.
    var rb = new byte[ClusterSize];
    for (var cluster = 0; cluster < totalPhysicalClusters; ++cluster)
      BinaryPrimitives.WriteUInt16BigEndian(rb.AsSpan(cluster * 2), 1);
    output.Write(rb);

    // --- Allocated data clusters ---
    for (var cluster = 0; cluster < numGuestClusters; ++cluster) {
      if (IsGuestClusterZero(data, cluster))
        continue;

      var offset = cluster * ClusterSize;
      var length = Math.Min(ClusterSize, data.Length - offset);
      output.Write(data.AsSpan(offset, length));
      if (length < ClusterSize)
        WriteZeroPadding(output, ClusterSize - length);
    }
  }

  private static int CountAllocatedGuestClusters(byte[] data, int guestClusterCount) {
    var count = 0;
    for (var cluster = 0; cluster < guestClusterCount; ++cluster)
      if (!IsGuestClusterZero(data, cluster))
        ++count;
    return count;
  }

  private static bool IsGuestClusterZero(byte[] data, int clusterIndex) {
    var offset = clusterIndex * ClusterSize;
    var length = Math.Min(ClusterSize, data.Length - offset);
    if (length <= 0) return true;
    foreach (var value in data.AsSpan(offset, length))
      if (value != 0)
        return false;
    return true;
  }

  private static void WriteZeroPadding(Stream output, int length) {
    Span<byte> pad = stackalloc byte[Math.Min(length, 4096)];
    var remaining = length;
    while (remaining > 0) {
      var chunk = Math.Min(remaining, pad.Length);
      output.Write(pad[..chunk]);
      remaining -= chunk;
    }
  }
}
