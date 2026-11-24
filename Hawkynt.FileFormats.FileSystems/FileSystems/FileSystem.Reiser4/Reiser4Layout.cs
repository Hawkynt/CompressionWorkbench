#pragma warning disable CS1591

namespace FileSystem.Reiser4;

/// <summary>
/// Where a format40 volume's fixed blocks sit for a given block size. The master superblock is
/// always at byte 65536, so its block number is <c>65536 / blockSize</c>; everything else follows it
/// in the same order <c>mkfs.reiser4 -b</c> lays it down at every block size it accepts (512 to
/// 8192): format superblock, first allocator bitmap, journal header and footer, status block,
/// backup block, and — on a fresh volume — the tree root and the first leaf.
/// </summary>
internal readonly record struct Reiser4Layout(int BlockSize) {

  /// <summary>The 4096-byte layout: master 16, format 17, bitmap 18, journal 19/20, status 21, backup 22, root 23, leaf 24.</summary>
  internal static readonly Reiser4Layout Default = new(4096);

  /// <summary>Block sizes <c>mkfs.reiser4 -b</c> accepts.</summary>
  internal static bool IsSupported(int blockSize) => blockSize is 512 or 1024 or 2048 or 4096 or 8192;

  internal ulong Master => (ulong)(Reiser4Volume.MasterOffset / this.BlockSize);
  internal ulong Format => this.Master + 1;
  internal ulong FirstBitmap => this.Master + 2;
  internal ulong JournalHeader => this.Master + 3;
  internal ulong JournalFooter => this.Master + 4;
  internal ulong Status => this.Master + 5;
  internal ulong Backup => this.Master + 6;

  /// <summary>Where a fresh volume's tree root sits; no tree node lies below it.</summary>
  internal ulong Root => this.Master + 7;

  /// <summary>Where a fresh volume's first leaf sits.</summary>
  internal ulong FirstLeaf => this.Master + 8;

  /// <summary>First block a file's data may occupy.</summary>
  internal ulong FirstData => this.Master + 9;

  /// <summary>
  /// Blocks one bitmap block accounts for: its first four bytes hold the Adler-32 of the rest, so
  /// the bit array is four bytes short of the block.
  /// </summary>
  internal ulong BlocksPerBitmap => (ulong)(this.BlockSize - 4) * 8;

  /// <summary>Bitmap <c>i</c> covers blocks <c>[i × BlocksPerBitmap, (i+1) × BlocksPerBitmap)</c>; the first sits after the format superblock, the rest at the stride boundaries.</summary>
  internal ulong BitmapBlock(ulong index) => index == 0 ? this.FirstBitmap : index * this.BlocksPerBitmap;

  internal bool IsBitmapBlock(ulong block) => block == this.FirstBitmap || block != 0 && block % this.BlocksPerBitmap == 0;

  /// <summary>
  /// The further copies of the backup block (<see cref="Backup" />) a volume of
  /// <paramref name="blocks" /> blocks carries, as <c>mkfs.reiser4</c> places them and
  /// <c>fsck.reiser4</c> expects them. Candidate block numbers start at 2 and grow by half
  /// (<c>n × 3 / 2</c>, truncated) while they lie inside the volume; each one names the bitmap region
  /// it falls in, and the copy goes into the second block of that region — no earlier than the
  /// backup block itself, never twice, and not at all in a region one block long.
  /// </summary>
  internal IReadOnlyList<ulong> BackupCopies(ulong blocks) {
    var copies = new List<ulong>();
    var previous = this.Backup;
    for (ulong candidate = 2; candidate < blocks; candidate = candidate * 3 / 2) {
      var start = candidate / this.BlocksPerBitmap * this.BlocksPerBitmap;
      if (Math.Min(start + this.BlocksPerBitmap, blocks) - start <= 1) continue;
      var copy = Math.Max(start + 1, this.Backup);
      if (copy >= blocks) break;
      if (copy <= previous) continue;
      copies.Add(copy);
      previous = copy;
    }
    return copies;
  }
}
