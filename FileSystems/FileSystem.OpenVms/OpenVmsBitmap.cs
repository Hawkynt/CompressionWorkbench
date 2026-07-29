#pragma warning disable CS1591
namespace FileSystem.OpenVms;

/// <summary>
/// In-memory view of BITMAP.SYS — the Files-11 ODS-2 storage allocation
/// bitmap. One bit per LBN: bit value 0 means free, 1 means allocated.
/// (The on-disk OpenVMS convention is the opposite — 1 = free — but our
/// reader/writer/in-place-modifier agree on the same polarity, and the
/// header-extension marker means real OpenVMS will never see this volume.)
/// <para>
/// The bitmap lives at <see cref="OpenVmsLayout.BitmapStartLbn"/> spanning
/// <see cref="OpenVmsLayout.BitmapBlockCount"/> LBNs, giving 65 536 bits
/// of coverage for the default 8 192-LBN volume (with headroom).
/// </para>
/// <para>
/// LBNs occupied by metadata (boot block, home block, BITMAP.SYS itself,
/// INDEXF.SYS, root directory) are pre-marked allocated by
/// <see cref="MarkMetadataAllocated"/> at volume-creation time. The
/// in-place modifier's allocator (<see cref="AllocateRun"/>) scans past
/// the metadata reservation to find a contiguous run of free LBNs.
/// </para>
/// </summary>
public sealed class OpenVmsBitmap {

  /// <summary>The bitmap's backing bytes. <see cref="BitsTotal"/> bits exposed.</summary>
  public byte[] Bytes { get; }

  /// <summary>Total bits available in the map.</summary>
  public int BitsTotal => this.Bytes.Length * 8;

  /// <summary>
  /// LBNs each bit stands for. ODS-2 tracks the volume in clusters (HM2$W_CLUSTER),
  /// not blocks, so BITMAP.SYS stays a fixed 16 blocks however large the volume
  /// grows -- which is what lets the rest of the layout keep its fixed LBNs.
  /// </summary>
  public int ClusterBlocks { get; init; } = 1;

  /// <summary>Highest LBN the map can express.</summary>
  public long LbnCapacity => (long)this.BitsTotal * this.ClusterBlocks;

  /// <summary>Smallest power-of-two cluster size that lets one bitmap cover <paramref name="volumeBlocks" />.</summary>
  public static int ClusterFor(long volumeBlocks) {
    var bits = (long)OpenVmsLayout.BitmapBlockCount * OpenVmsLayout.BlockSize * 8;
    var cluster = 1;
    while ((long)cluster * bits < volumeBlocks) cluster <<= 1;
    return cluster;
  }

  /// <summary>Creates a fresh empty bitmap sized to the default volume.</summary>
  public OpenVmsBitmap()
    : this(new byte[OpenVmsLayout.BitmapBlockCount * OpenVmsLayout.BlockSize]) { }

  /// <summary>Blocks in the volume this map describes.</summary>
  public long VolumeBlocks { get; init; } = OpenVmsLayout.VolumeBlocks;

  /// <summary>Wraps an existing bitmap buffer (typically read from disk).</summary>
  public OpenVmsBitmap(byte[] bytes) {
    ArgumentNullException.ThrowIfNull(bytes);
    this.Bytes = bytes;
  }

  /// <summary>True when bit <paramref name="lbn"/> is set (allocated).</summary>
  public bool IsAllocated(int lbn) {
    var bit = lbn / this.ClusterBlocks;
    if (lbn < 0 || bit >= this.BitsTotal) return false;
    return (this.Bytes[bit >> 3] & (1 << (bit & 7))) != 0;
  }

  /// <summary>Sets bit <paramref name="lbn"/> (mark allocated).</summary>
  public void MarkAllocated(int lbn) {
    var bit = lbn / this.ClusterBlocks;
    if (lbn < 0 || bit >= this.BitsTotal) return;
    this.Bytes[bit >> 3] |= (byte)(1 << (bit & 7));
  }

  /// <summary>Clears bit <paramref name="lbn"/> (mark free).</summary>
  public void MarkFree(int lbn) {
    var bit = lbn / this.ClusterBlocks;
    if (lbn < 0 || bit >= this.BitsTotal) return;
    this.Bytes[bit >> 3] &= (byte)~(1 << (bit & 7));
  }

  /// <summary>Pre-marks every metadata LBN allocated. Call once when emitting a fresh volume.</summary>
  public void MarkMetadataAllocated() {
    // Boot + home block.
    this.MarkAllocated(OpenVmsLayout.BootBlockLbn);
    this.MarkAllocated(OpenVmsLayout.HomeBlockLbn);

    // BITMAP.SYS.
    for (var i = 0; i < OpenVmsLayout.BitmapBlockCount; i++)
      this.MarkAllocated(OpenVmsLayout.BitmapStartLbn + i);

    // INDEXF.SYS.
    for (var i = 0; i < OpenVmsLayout.IndexFileBlockCount; i++)
      this.MarkAllocated(OpenVmsLayout.IndexFileStartLbn + i);

    // 000000.DIR root directory.
    this.MarkAllocated(OpenVmsLayout.RootDirectoryLbn);

    // Reserve everything past the volume as "allocated" so the allocator never
    // wanders past its end.
    for (var lbn = this.VolumeBlocks; lbn < this.LbnCapacity; lbn += this.ClusterBlocks)
      this.MarkAllocated((int)lbn);
  }

  /// <summary>
  /// Finds and marks a contiguous run of <paramref name="blocks"/> free LBNs,
  /// starting the search at <see cref="OpenVmsLayout.DataAreaStartLbn"/>.
  /// Returns the run's starting LBN or -1 if the volume can't satisfy the request.
  /// </summary>
  public int AllocateRun(int blocks) {
    if (blocks <= 0) return -1;
    var maxLbn = (int)Math.Min(this.VolumeBlocks, this.LbnCapacity);
    // Runs start on a cluster boundary: a bit covers a whole cluster, so two
    // files sharing one would each free the other's blocks.
    var first = (OpenVmsLayout.DataAreaStartLbn + this.ClusterBlocks - 1)
                / this.ClusterBlocks * this.ClusterBlocks;
    for (var start = first; start <= maxLbn - blocks; start += this.ClusterBlocks) {
      var fit = true;
      // One probe per cluster: every run starts on a cluster boundary, so a
      // cluster is either wholly inside this run or wholly outside it. The
      // cursor stays on a boundary too -- were it to skip ahead to the block
      // after a collision, a later run would start mid-cluster and share its
      // bit with the run before it, letting the allocator hand the same
      // blocks out twice.
      for (var i = 0; i < blocks; i += this.ClusterBlocks) {
        if (this.IsAllocated(start + i)) { fit = false; break; }
      }
      if (!fit) continue;
      for (var i = 0; i < blocks; i += this.ClusterBlocks)
        this.MarkAllocated(start + i);
      return start;
    }
    return -1;
  }

  /// <summary>Frees a contiguous run of <paramref name="blocks"/> LBNs starting at <paramref name="startLbn"/>.</summary>
  public void FreeRun(int startLbn, int blocks) {
    for (var i = 0; i < blocks; i++)
      this.MarkFree(startLbn + i);
  }
}
