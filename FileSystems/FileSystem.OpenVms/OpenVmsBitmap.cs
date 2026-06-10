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

  /// <summary>Total bits = LBNs trackable.</summary>
  public int BitsTotal => this.Bytes.Length * 8;

  /// <summary>Creates a fresh empty bitmap sized to the default volume.</summary>
  public OpenVmsBitmap()
    : this(new byte[OpenVmsLayout.BitmapBlockCount * OpenVmsLayout.BlockSize]) { }

  /// <summary>Wraps an existing bitmap buffer (typically read from disk).</summary>
  public OpenVmsBitmap(byte[] bytes) {
    ArgumentNullException.ThrowIfNull(bytes);
    this.Bytes = bytes;
  }

  /// <summary>True when bit <paramref name="lbn"/> is set (allocated).</summary>
  public bool IsAllocated(int lbn) {
    if (lbn < 0 || lbn >= this.BitsTotal) return false;
    return (this.Bytes[lbn >> 3] & (1 << (lbn & 7))) != 0;
  }

  /// <summary>Sets bit <paramref name="lbn"/> (mark allocated).</summary>
  public void MarkAllocated(int lbn) {
    if (lbn < 0 || lbn >= this.BitsTotal) return;
    this.Bytes[lbn >> 3] |= (byte)(1 << (lbn & 7));
  }

  /// <summary>Clears bit <paramref name="lbn"/> (mark free).</summary>
  public void MarkFree(int lbn) {
    if (lbn < 0 || lbn >= this.BitsTotal) return;
    this.Bytes[lbn >> 3] &= (byte)~(1 << (lbn & 7));
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

    // Reserve LBNs past VolumeBlocks as "allocated" so the allocator never wanders past the volume.
    for (var lbn = OpenVmsLayout.VolumeBlocks; lbn < this.BitsTotal; lbn++)
      this.MarkAllocated(lbn);
  }

  /// <summary>
  /// Finds and marks a contiguous run of <paramref name="blocks"/> free LBNs,
  /// starting the search at <see cref="OpenVmsLayout.DataAreaStartLbn"/>.
  /// Returns the run's starting LBN or -1 if the volume can't satisfy the request.
  /// </summary>
  public int AllocateRun(int blocks) {
    if (blocks <= 0) return -1;
    var maxLbn = Math.Min(OpenVmsLayout.VolumeBlocks, this.BitsTotal);
    for (var start = OpenVmsLayout.DataAreaStartLbn; start <= maxLbn - blocks; start++) {
      var fit = true;
      for (var i = 0; i < blocks; i++) {
        if (this.IsAllocated(start + i)) { fit = false; start += i; break; }
      }
      if (!fit) continue;
      for (var i = 0; i < blocks; i++)
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
