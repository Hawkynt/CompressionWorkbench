#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Qcow2;

/// <summary>
/// Writes QCOW2 v2 disk images in WORM mode. Takes a single raw disk image
/// and wraps it in a QCOW2 container with uncompressed clusters. The L1/L2
/// two-level address translation table maps clusters 1:1 to file offsets.
/// Roundtrips through <see cref="Qcow2Reader.ExtractDisk"/>.
///
/// Simplifications (all valid per QCOW2 spec):
/// <list type="bullet">
///   <item>No compression — raw clusters only.</item>
///   <item>No refcount table — refcount_table_offset and refcount_table_clusters are 0. Our reader doesn't check refcounts; qemu-img check would warn but still mount.</item>
///   <item>No snapshots, no backing files, no encryption.</item>
///   <item>Cluster size is 65536 bytes (cluster_bits = 16) — QEMU's default.</item>
/// </list>
/// </summary>
public sealed class Qcow2Writer {
  private static readonly byte[] Magic = [0x51, 0x46, 0x49, 0xFB]; // "QFI\xFB"
  private const int ClusterBits = 16;
  private const int ClusterSize = 1 << ClusterBits; // 65536
  private const int L2EntriesPerCluster = ClusterSize / 8; // 8192
  private const int HeaderSize = 72;

  private byte[]? _diskData;

  /// <summary>Sets the raw disk image to wrap.</summary>
  public void SetDiskImage(byte[] data) {
    ArgumentNullException.ThrowIfNull(data);
    _diskData = data;
  }

  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);
    var data = _diskData ?? throw new InvalidOperationException("No disk image set.");

    var virtualSize = (long)data.Length;
    var numClusters = (int)((virtualSize + ClusterSize - 1) / ClusterSize);
    var numL2Tables = (numClusters + L2EntriesPerCluster - 1) / L2EntriesPerCluster;
    var l1Size = numL2Tables;

    // Layout: cluster 0 = header, cluster 1 = L1 table, clusters 2..2+numL2-1 = L2 tables,
    //         clusters 2+numL2..end = data clusters.
    var l1TableOffset = (long)ClusterSize;     // cluster 1
    var l2TablesStart = (long)2 * ClusterSize; // cluster 2
    var dataStart = l2TablesStart + (long)numL2Tables * ClusterSize;

    // ---- Write header (cluster 0) ----
    var hdr = new byte[ClusterSize];
    Magic.CopyTo(hdr, 0);
    BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(4), 2);                          // version
    BinaryPrimitives.WriteUInt64BigEndian(hdr.AsSpan(8), 0);                          // backing_file_offset
    BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(16), 0);                         // backing_file_size
    BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(20), (uint)ClusterBits);         // cluster_bits
    BinaryPrimitives.WriteUInt64BigEndian(hdr.AsSpan(24), (ulong)virtualSize);        // disk_size
    BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(32), 0);                         // crypt_method
    BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(36), (uint)l1Size);              // l1_size
    BinaryPrimitives.WriteUInt64BigEndian(hdr.AsSpan(40), (ulong)l1TableOffset);      // l1_table_offset
    BinaryPrimitives.WriteUInt64BigEndian(hdr.AsSpan(48), 0);                         // refcount_table_offset
    BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(56), 0);                         // refcount_table_clusters
    BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(60), 0);                         // nb_snapshots
    BinaryPrimitives.WriteUInt64BigEndian(hdr.AsSpan(64), 0);                         // snapshots_offset
    output.Write(hdr);

    // ---- Write L1 table (cluster 1) ----
    var l1 = new byte[ClusterSize];
    for (var i = 0; i < numL2Tables; i++) {
      var l2Offset = l2TablesStart + (long)i * ClusterSize;
      BinaryPrimitives.WriteUInt64BigEndian(l1.AsSpan(i * 8), (ulong)l2Offset);
    }
    output.Write(l1);

    // ---- Write L2 tables ----
    var clusterIdx = 0;
    for (var l2i = 0; l2i < numL2Tables; l2i++) {
      var l2 = new byte[ClusterSize];
      for (var j = 0; j < L2EntriesPerCluster && clusterIdx < numClusters; j++, clusterIdx++) {
        var hostOffset = dataStart + (long)clusterIdx * ClusterSize;
        BinaryPrimitives.WriteUInt64BigEndian(l2.AsSpan(j * 8), (ulong)hostOffset);
      }
      output.Write(l2);
    }

    // ---- Write data clusters ----
    output.Write(data);
    // Pad the last cluster to ClusterSize.
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
