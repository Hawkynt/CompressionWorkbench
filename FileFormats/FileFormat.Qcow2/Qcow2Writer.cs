#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Qcow2;

/// <summary>
/// Writes QCOW2 v2 disk images in WORM mode. Takes a single raw disk image and
/// wraps it in a QCOW2 container with uncompressed clusters.
/// <para>
/// Layout: header (cluster&#160;0) → L1 table (cluster&#160;1) → L2 tables → refcount table
/// → refcount block → data clusters. Each cluster has a refcount of&#160;1. This is the
/// arrangement <c>qemu-img create</c> produces, and <c>qemu-img check</c> now passes
/// without the previous "Image contains errors or warnings" output that came from
/// the zeroed refcount table.
/// </para>
/// </summary>
public sealed class Qcow2Writer {
  private static readonly byte[] Magic = [0x51, 0x46, 0x49, 0xFB]; // "QFI\xFB"
  private const int ClusterBits = 16;
  private const int ClusterSize = 1 << ClusterBits;               // 65536
  private const int L2EntriesPerCluster = ClusterSize / 8;        // 8192
  private const int RefcountEntriesPerCluster = ClusterSize / 2;  // 32768 (16-bit refcounts at order 4)

  private byte[]? _diskData;

  public void SetDiskImage(byte[] data) {
    ArgumentNullException.ThrowIfNull(data);
    _diskData = data;
  }

  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);
    var data = _diskData ?? throw new InvalidOperationException("No disk image set.");

    var virtualSize = (long)data.Length;
    var numDataClusters = (int)((virtualSize + ClusterSize - 1) / ClusterSize);
    var numL2Tables = (numDataClusters + L2EntriesPerCluster - 1) / L2EntriesPerCluster;

    // Reserve layout slots: header(1) + L1(1) + L2(numL2Tables) + refcount_table(1) + refcount_block(1) + data(numDataClusters).
    const int refcountTableClusters = 1;
    const int refcountBlockClusters = 1;
    var l1TableOffset = (long)ClusterSize;
    var l2TablesStart = 2L * ClusterSize;
    var refcountTableOffset = l2TablesStart + (long)numL2Tables * ClusterSize;
    var refcountBlockOffset = refcountTableOffset + (long)refcountTableClusters * ClusterSize;
    var dataStart = refcountBlockOffset + (long)refcountBlockClusters * ClusterSize;
    var totalClusters = (int)(dataStart / ClusterSize) + numDataClusters;

    // --- Header ---
    var hdr = new byte[ClusterSize];
    Magic.CopyTo(hdr, 0);
    BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(4), 2);                               // version
    BinaryPrimitives.WriteUInt64BigEndian(hdr.AsSpan(8), 0);                               // backing_file_offset
    BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(16), 0);                              // backing_file_size
    BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(20), (uint)ClusterBits);              // cluster_bits
    BinaryPrimitives.WriteUInt64BigEndian(hdr.AsSpan(24), (ulong)virtualSize);             // disk_size
    BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(32), 0);                              // crypt_method
    BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(36), (uint)numL2Tables);              // l1_size
    BinaryPrimitives.WriteUInt64BigEndian(hdr.AsSpan(40), (ulong)l1TableOffset);           // l1_table_offset
    BinaryPrimitives.WriteUInt64BigEndian(hdr.AsSpan(48), (ulong)refcountTableOffset);     // refcount_table_offset
    BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(56), refcountTableClusters);          // refcount_table_clusters
    BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(60), 0);                              // nb_snapshots
    BinaryPrimitives.WriteUInt64BigEndian(hdr.AsSpan(64), 0);                              // snapshots_offset
    output.Write(hdr);

    // --- L1 table ---
    var l1 = new byte[ClusterSize];
    for (var i = 0; i < numL2Tables; ++i) {
      var l2Offset = l2TablesStart + (long)i * ClusterSize;
      BinaryPrimitives.WriteUInt64BigEndian(l1.AsSpan(i * 8), (ulong)l2Offset);
    }
    output.Write(l1);

    // --- L2 tables ---
    var clusterIdx = 0;
    for (var t = 0; t < numL2Tables; ++t) {
      var l2 = new byte[ClusterSize];
      for (var j = 0; j < L2EntriesPerCluster && clusterIdx < numDataClusters; ++j, ++clusterIdx) {
        var hostOffset = dataStart + (long)clusterIdx * ClusterSize;
        BinaryPrimitives.WriteUInt64BigEndian(l2.AsSpan(j * 8), (ulong)hostOffset);
      }
      output.Write(l2);
    }

    // --- Refcount table (one entry → one refcount block) ---
    var rt = new byte[ClusterSize];
    BinaryPrimitives.WriteUInt64BigEndian(rt.AsSpan(0), (ulong)refcountBlockOffset);
    output.Write(rt);

    // --- Refcount block: one 16-bit entry per cluster, all with refcount 1. ---
    var rb = new byte[ClusterSize];
    if (totalClusters > RefcountEntriesPerCluster)
      throw new InvalidOperationException(
        $"qcow2 writer: image of {totalClusters} clusters exceeds single-refcount-block capacity ({RefcountEntriesPerCluster}).");
    for (var c = 0; c < totalClusters; ++c)
      BinaryPrimitives.WriteUInt16BigEndian(rb.AsSpan(c * 2), 1);
    output.Write(rb);

    // --- Data clusters ---
    output.Write(data);
    var tail = data.Length % ClusterSize;
    if (tail != 0) {
      var padLen = ClusterSize - tail;
      Span<byte> pad = stackalloc byte[Math.Min(padLen, 4096)];
      pad.Clear();
      var remaining = padLen;
      while (remaining > 0) {
        var chunk = Math.Min(remaining, pad.Length);
        output.Write(pad[..chunk]);
        remaining -= chunk;
      }
    }
  }
}
