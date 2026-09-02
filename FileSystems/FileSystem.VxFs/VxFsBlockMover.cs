#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.VxFs;

/// <summary>
/// Moves a file's blocks inside a VxFS volume and rewrites the direct extents
/// that claimed them.
/// </summary>
/// <remarks>
/// <para>A file's blocks are named by up to ten direct extents in its own
/// inode, and the order of those extents is the order of the file's bytes: the
/// first covers the file's first blocks, the next the ones after that. So a
/// move may change where an extent points but never which extent it is, and the
/// array is written back in the order it was read.</para>
///
/// <para>That is done once, after the pass. One run's old home is routinely
/// another's new one, and an inode rewritten halfway through would describe a
/// layout that no longer holds.</para>
/// </remarks>
public sealed class VxFsBlockMover : IFilesystemBlockMover {

  /// <summary>One extent of one file, and where it currently is.</summary>
  private sealed class Slot {
    public required string FileName { get; init; }
    public required long InodeAt { get; init; }
    public required int Index { get; init; }
    public long Offset { get; set; }
    public long Length { get; set; }
  }

  /// <summary>Every file's extents, in the order the file's bytes run.</summary>
  private readonly List<Slot> _slots = [];

  private VxFsVolume? _volume;

  /// <summary>Reads the volume once and notes which extent claims each run.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    this._volume = new VxFsVolume(image);
    if (!this._volume.Valid)
      throw new InvalidDataException($"VxFS: {this._volume.Status}.");

    this._slots.Clear();

    var bs = this._volume.BlockSize;
    foreach (var file in this._volume.Files)
      for (var i = 0; i < file.Extents.Count; ++i)
        this._slots.Add(new Slot {
          FileName = file.Name,
          InodeAt = file.InodeOffset,
          Index = i,
          Offset = file.Extents[i].Block * bs,
          Length = file.Extents[i].Count * bs,
        });
  }

  /// <summary>The volume's own block size; a file owns whole blocks of it.</summary>
  public int BlockSize => this._volume?.BlockSize ?? VxFsLayout.BlockSize;

  /// <summary>First byte a file may occupy: past the whole walk to the files.</summary>
  public long FirstDataByte => this._volume == null ? 0 : VxFsExtentMap.FirstDataByte(this._volume);

  /// <summary>Each call notes one run; the inodes are written once the pass is over.</summary>
  public bool RepointsRunsIndependently => true;

  /// <summary>
  /// A run may be held outside the volume while the rest of the layout moves,
  /// which is what lets a full one be rearranged at all.
  /// </summary>
  public bool SupportsHeldRuns => true;

  /// <inheritdoc />
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
  /// <summary>
  /// Performs the update allocation after move operation.
  /// </summary>
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(fileName);
    if (this._volume == null) this.Init(image);
    if (oldOffset == newOffset) return;

    var bs = this.BlockSize;
    if (newOffset % bs != 0)
      throw new NotSupportedException(
        $"VxFS: an extent names a block number, so it cannot start at byte {newOffset}.");

    if (newOffset < this.FirstDataByte)
      throw new NotSupportedException(
        "VxFS: a file cannot start before the volume's own structures end — the superblock, the " +
        "object location table, the inode lists and the root directory all live there.");

    if (newOffset + length > this._volume!.ImageLength)
      throw new NotSupportedException("VxFS: a file cannot reach past the end of the volume.");

    // A run is found by who owns it, not by where it is. A held run keeps the
    // offset it was lifted from until it is put down again, and something else
    // has very likely taken that offset meanwhile — so an offset alone names
    // two runs at once, and picking the wrong one hands a file another's blocks.
    var slot = this._slots.FirstOrDefault(
                 x => x.Offset == oldOffset && x.Length == length && x.FileName == fileName)
               ?? this._slots.FirstOrDefault(x => x.Offset == oldOffset && x.Length == length)
      ?? throw new InvalidOperationException(
        $"VxFS: no extent of '{fileName}' claims {oldOffset}, so it cannot be repointed.");

    slot.Offset = newOffset;
  }

  /// <summary>Writes every inode's direct extents back, in the order they were read.</summary>
  /// <remarks>
  /// The order is not cosmetic. Extent <c>i</c> covers the file's bytes after
  /// everything extents <c>0..i-1</c> cover, so writing them sorted by block
  /// would keep the file's bytes on disk and hand them back shuffled.
  /// </remarks>
  public void Settle(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (this._volume == null) return;

    var bs = this.BlockSize;
    Span<byte> extents = stackalloc byte[VxFsLayout.DirectExtents * 8];
    foreach (var group in this._slots.GroupBy(x => x.InodeAt)) {
      extents.Clear();

      foreach (var slot in group) {
        if (slot.Index >= VxFsLayout.DirectExtents)
          throw new NotSupportedException(
            $"VxFS: an inode holds {VxFsLayout.DirectExtents} direct extents; extent {slot.Index} has nowhere to go.");

        var at = slot.Index * 8;
        this.Write32(extents[at..], (uint)(slot.Offset / bs));
        this.Write32(extents[(at + 4)..], (uint)(slot.Length / bs));
      }

      image.Position = group.Key + VxFsLayout.Ext4Direct;
      image.Write(extents);
    }

    image.Flush();
  }

  private void Write32(Span<byte> target, uint value) {
    if (this._volume!.IsBigEndian) BinaryPrimitives.WriteUInt32BigEndian(target, value);
    else BinaryPrimitives.WriteUInt32LittleEndian(target, value);
  }
}
