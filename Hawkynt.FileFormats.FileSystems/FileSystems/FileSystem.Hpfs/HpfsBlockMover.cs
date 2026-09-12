#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.Hpfs;

/// <summary>
/// Moves a file's sectors inside an HPFS volume, repoints its fnode, and moves
/// the allocation with it.
/// </summary>
/// <remarks>
/// <para>A file this reader can resolve is one contiguous run recorded in the
/// first entry of its fnode's direct allocation list — a starting LBA and a
/// length — so relocating it is the copy, one four-byte write, and the bitmap
/// bits that say which sectors are taken.</para>
///
/// <para>The fnode is found by the LBA it still names rather than by the file's
/// name, so two files with the same leaf name in different directories cannot
/// send the wrong one somewhere.</para>
/// </remarks>
public sealed class HpfsBlockMover : IFilesystemBlockMover {

  private const int LbaSize = HpfsReader.LbaSize;

  /// <summary>Offset of the direct allocation list inside an fnode.</summary>
  private const int FnodeAllocEntryOffset = HpfsLayout.FnAlloc;

  /// <summary>LBA the writer puts the allocation bitmap at when the superblock names none.</summary>
  private const uint DefaultBitmapLba = 24;

  /// <summary>Sectors one bitmap sector accounts for.</summary>
  private const int BitsPerBitmapSector = LbaSize * 8;

  private uint _bitmapLba;
  private bool _initialised;

  /// <summary>Reads where the volume keeps its allocation bitmap.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    var superblock = new byte[LbaSize];
    var at = (long)HpfsReader.SuperblockLba * LbaSize;
    if (at + LbaSize > image.Length)
      throw new InvalidDataException("HPFS: the image is too short to hold a superblock.");
    image.Position = at;
    image.ReadExactly(superblock);

    var bitmapLba = BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(24));
    this._bitmapLba = bitmapLba == 0 ? DefaultBitmapLba : bitmapLba;

    // Which fnode owns which run is settled here, before anything moves, and
    // keyed by where the run started. Asking the live volume which fnode still
    // names an LBA finds whatever has since been laid down there and repoints
    // that file instead — two files of one length then come back holding each
    // other's bytes, at the right length, with nothing raised.
    this._fnodeOf.Clear();
    image.Position = 0;
    using (var reader = new HpfsReader(image)) {
      foreach (var entry in reader.Entries) {
        if (entry.IsDirectory || entry.IsBtreeFile) continue;
        if (entry.FnodeLba == 0) continue;
        this._fnodeOf[entry.DataLba] = entry.FnodeLba;
      }
    }

    this._initialised = true;
  }

  /// <summary>A sector. An allocation entry names a starting LBA.</summary>
  public int BlockSize => LbaSize;

  /// <summary>
  /// First byte a file may occupy. Everything below LBA 32 is the fixed head of
  /// the volume — boot sector, superblock, spare block, root fnode and dirent
  /// block, and the two bitmaps — none of which is located by anything that
  /// could be repointed.
  /// </summary>
  public long FirstDataByte => 32L * LbaSize;

  /// <inheritdoc />
  /// <summary>
  /// A run may be held outside the volume while the rest of the layout moves,
  /// which is what lets a full volume be rearranged at all.
  /// </summary>
  public bool SupportsHeldRuns => true;

  /// <summary>
  /// Performs the move extent operation.
  /// </summary>
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    if (length <= 0 || srcOffset == dstOffset) return;

    // Overlap-safe: a run shifted forward by less than its own length
    // overwrites its own tail, and copying that front to back reads bytes
    // the copy has already replaced.
    Compression.Core.DiskImage.ExtentCopy.Move(image, srcOffset, dstOffset, length);
    if (zeroSource)
      Compression.Core.DiskImage.ExtentCopy.Zero(image, srcOffset, length);
  }

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(fileName);
    if (!this._initialised) this.Init(image);

    if (newOffset % LbaSize != 0)
      throw new NotSupportedException(
        $"HPFS: {newOffset} is not on a sector boundary, which is all an allocation entry can name.");

    var oldLba = (uint)(oldOffset / LbaSize);
    var newLba = (uint)(newOffset / LbaSize);
    if (oldLba == newLba) return;

    var fnodeLba = this._fnodeOf.GetValueOrDefault(oldLba);
    if (fnodeLba == 0)
      throw new InvalidOperationException(
        $"HPFS: no fnode names LBA {oldLba}, so '{fileName}' cannot be repointed.");

    var fnodeOffset = (long)fnodeLba * LbaSize;
    Span<byte> field = stackalloc byte[4];

    image.Position = fnodeOffset + FnodeAllocEntryOffset + 4;
    image.ReadExactly(field);
    var runLbas = BinaryPrimitives.ReadUInt32LittleEndian(field);
    if (runLbas == 0) runLbas = (uint)((length + LbaSize - 1) / LbaSize);

    BinaryPrimitives.WriteUInt32LittleEndian(field, newLba);
    image.Position = fnodeOffset + FnodeAllocEntryOffset + 8;
    image.Write(field);

    // The bitmap says which sectors are taken; leaving it behind would let the
    // next file added to the volume be allocated straight on top of this one.
    this.SetBits(image, oldLba, runLbas, free: true);
    this.SetBits(image, newLba, runLbas, free: false);
    image.Flush();
  }

  /// <summary>The fnode whose allocation entry started at each LBA, as read before any move.</summary>
  private readonly Dictionary<uint, uint> _fnodeOf = [];

  /// <summary>Kept for callers that want the pre-move owner of an LBA.</summary>
  private uint FindFnodeNaming(Stream image, uint lba) {
    if (!this._initialised) this.Init(image);
    return this._fnodeOf.GetValueOrDefault(lba);
  }

  /// <summary>
  /// Flips <paramref name="count" /> allocation bits starting at
  /// <paramref name="startLba" />. A set bit means free.
  /// </summary>
  /// <remarks>
  /// The writer lays down a single bitmap band, which accounts for the first
  /// 4096 sectors. A sector past that has no bit to flip; the volume's own
  /// allocator will not hand it out either, so leaving it alone keeps the two
  /// agreeing rather than writing over a neighbouring band's bits.
  /// </remarks>
  private void SetBits(Stream image, uint startLba, uint count, bool free) {
    var bitmapOffset = (long)this._bitmapLba * LbaSize;
    if (bitmapOffset + LbaSize > image.Length) return;

    for (var i = 0u; i < count; ++i) {
      var lba = startLba + i;
      if (lba >= BitsPerBitmapSector) break;

      var at = bitmapOffset + lba / 8;
      image.Position = at;
      var current = image.ReadByte();
      if (current < 0) break;

      var mask = 1 << (int)(lba % 8);
      var updated = free ? current | mask : current & ~mask;
      if (updated == current) continue;

      image.Position = at;
      image.WriteByte((byte)updated);
    }
  }
}
