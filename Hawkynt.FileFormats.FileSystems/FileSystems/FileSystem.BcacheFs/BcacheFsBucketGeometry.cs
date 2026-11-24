#pragma warning disable CS1591

namespace FileSystem.BcacheFs;

/// <summary>
/// The bucket size, block size and time units the writing code works in. This package's writer
/// lays a device out in 64 KiB buckets, one b-tree node per bucket, with 512-byte blocks and times
/// in 100 ns units from the Unix epoch; a volume <c>bcachefs format</c> made has the bucket size it
/// chose (128 KiB by default, or what <c>--bucket</c> asked for), the device's block size (4 KiB on
/// an image file) and times in nanoseconds from the instant it was formatted. An in-place edit of
/// it writes its nodes, allocation keys, data extents, journal entry and inode times in those. An
/// edit sets them for its own flow of control; everything else sees the writer's.
/// </summary>
internal static class BcacheFsBucketGeometry {
  private sealed record Units(int Bucket, int Block, int Node, Int128 TimeBase, uint TimePrecision, int MetadataReplicas);

  private static readonly AsyncLocal<Units?> Current = new();

  /// <summary>Sectors per bucket in effect.</summary>
  internal static int Sectors => Current.Value?.Bucket ?? BcacheFsFormat.WriterBucketSectors;

  /// <summary>Sectors per b-tree node in effect: the bucket size unless the volume says otherwise.</summary>
  internal static int NodeSectors => Current.Value?.Node ?? Sectors;

  /// <summary>Copies of every b-tree node, and so pointer entries in every pointer to one.</summary>
  internal static int MetadataReplicas => Current.Value?.MetadataReplicas ?? 1;

  /// <summary>Sectors per block in effect: every node, extent and journal entry is a whole number of them.</summary>
  internal static int BlockSectors => Current.Value?.Block ?? 1;

  /// <summary>Nanoseconds since the Unix epoch that a stored time of zero means.</summary>
  internal static Int128 TimeBase => Current.Value?.TimeBase ?? 0;

  /// <summary>Nanoseconds per stored time unit.</summary>
  internal static uint TimePrecision => Current.Value?.TimePrecision ?? BcacheFsFormat.TimePrecisionNanoseconds;

  /// <summary>
  /// Works in buckets of <paramref name="sectors" />, blocks of <paramref name="blockSectors" /> and
  /// times of <paramref name="timePrecision" /> ns from <paramref name="timeBase" /> until the result is disposed.
  /// </summary>
  internal static IDisposable Use(int sectors, int blockSectors = 1, Int128? timeBase = null, uint? timePrecision = null,
      int? nodeSectors = null, int metadataReplicas = 1) {
    if (metadataReplicas is < 1 or > 4)
      throw new NotSupportedException($"bcachefs: {metadataReplicas} metadata replicas is outside the 1 to 4 a volume can ask for.");
    if (sectors < 8 || (sectors & (sectors - 1)) != 0)
      throw new NotSupportedException($"bcachefs: a bucket of {sectors} sectors is not a power of two of at least one 4 KiB block.");
    if (blockSectors < 1 || (blockSectors & (blockSectors - 1)) != 0 || blockSectors > sectors)
      throw new NotSupportedException($"bcachefs: a block of {blockSectors} sectors is not a power of two within a {sectors}-sector bucket.");
    var node = nodeSectors ?? sectors;
    if (node < blockSectors || (node & (node - 1)) != 0 || node > sectors)
      throw new NotSupportedException($"bcachefs: a b-tree node of {node} sectors is not a power of two between a block and a {sectors}-sector bucket.");
    var previous = Current.Value;
    Current.Value = new Units(sectors, blockSectors, node, timeBase ?? 0,
      Math.Max(1u, timePrecision ?? BcacheFsFormat.TimePrecisionNanoseconds), metadataReplicas);
    return new Restore(previous);
  }

  /// <summary><paramref name="bytes" /> rounded up to whole blocks, in sectors.</summary>
  internal static int RoundUpSectors(long bytes) {
    var block = (long)BlockSectors * BcacheFsFormat.SectorSize;
    return checked((int)((bytes + block - 1) / block * BlockSectors));
  }

  private sealed class Restore(Units? previous) : IDisposable {
    private bool _done;
    public void Dispose() {
      if (this._done) return;
      Current.Value = previous;
      this._done = true;
    }
  }
}
