#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.LittleFs;

/// <summary>
/// Moves a file's blocks inside a littlefs volume and threads the skip-list
/// through their new homes.
/// </summary>
/// <remarks>
/// <para>Nothing outside a file names its blocks except the head. The rest are
/// named from inside the file itself: block <c>i</c> opens with pointers back
/// to <c>i-1</c>, <c>i-2</c>, <c>i-4</c> and so on. So a move is not a field
/// somewhere else — it is the pointer arrays of the blocks after it, which is
/// why they are written from the finished order rather than patched one at a
/// time.</para>
///
/// <para>The head is named by a tag in a metadata pair, and a metadata pair is
/// a log of commits with a checksum over each. Writing the new head means
/// taking that commit's checksum again.</para>
/// </remarks>
public sealed class LittleFsBlockMover : IFilesystemBlockMover {

  private LittleFsLayout.Layout? _layout;

  /// <summary>Every block that moved, and where it moved to.</summary>
  private readonly Dictionary<uint, uint> _moved = [];

  /// <summary>Reads the volume once and notes what each file is made of.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    this._layout = LittleFsLayout.Read(image);
    if (this._layout == null)
      throw new InvalidDataException("littlefs: the volume is not one this reads.");

    this._moved.Clear();
  }

  /// <summary>A block, which is what the skip-list counts in.</summary>
  public int BlockSize => (int)(this._layout?.BlockSize ?? 0);

  /// <summary>
  /// First byte a file's block may occupy: past the metadata pair at the front
  /// that everything is found through.
  /// </summary>
  public long FirstDataByte => 2L * (this._layout?.BlockSize ?? 0);

  /// <summary>
  /// Each call notes where one block has got to; the pointers are threaded once
  /// the whole file has landed.
  /// </summary>
  public bool RepointsRunsIndependently => true;

  /// <summary>
  /// A block may be held outside the volume while the rest of the layout moves,
  /// which is what lets a full volume be rearranged at all.
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
    if (this._layout == null) this.Init(image);
    if (oldOffset == newOffset) return;

    var blockSize = this._layout!.BlockSize;
    if (newOffset % blockSize != 0)
      throw new NotSupportedException(
        $"littlefs: {newOffset} is not on a {blockSize}-byte block boundary, which is what a " +
        "pointer counts in.");

    var moved = 0L;
    while (moved < length) {
      this._moved[(uint)((oldOffset + moved) / blockSize)] = (uint)((newOffset + moved) / blockSize);
      moved += blockSize;
    }
  }

  /// <summary>
  /// Threads every file's skip-list through where its blocks ended up, and
  /// writes each new head into the commit that names it.
  /// </summary>
  /// <remarks>
  /// Called once a layout pass has finished. A block's pointers name the blocks
  /// before it in the same file, so they can only be written when every one of
  /// them has a final home — patching them as the pass went would thread the
  /// list through addresses that were about to change.
  /// </remarks>
  public void SettleChains(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (this._layout == null || this._moved.Count == 0) return;

    var blockSize = this._layout.BlockSize;
    foreach (var file in this._layout.Files) {
      var final = file.Blocks.Select(b => this._moved.TryGetValue(b, out var to) ? to : b).ToList();

      // Each block after the first opens with pointers to the ones it skips
      // back to; all of them are known now and none of them were before.
      for (var i = 1; i < final.Count; ++i) {
        var pointers = LittleFsLayout.PointerCount(i);
        var at = (long)final[i] * blockSize;
        if (at < 0 || at + pointers * 4 > image.Length) continue;

        var bytes = new byte[pointers * 4];
        for (var p = 0; p < pointers; ++p) {
          var target = i - (1 << p);
          if (target < 0) break;
          BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(p * 4), final[target]);
        }

        image.Position = at;
        image.Write(bytes, 0, bytes.Length);
      }

      // The head is the last block, and it is the one thing named from outside.
      var head = final[^1];
      if (head == file.Blocks[^1]) continue;

      var metadata = new byte[blockSize];
      image.Position = file.MetadataBlock;
      image.ReadExactly(metadata);

      var field = (int)(file.HeadField - file.MetadataBlock);
      if (field < 0 || field + 4 > metadata.Length) continue;

      BinaryPrimitives.WriteUInt32LittleEndian(metadata.AsSpan(field), head);
      LittleFsLayout.RestampCommit(metadata);
      image.Position = file.MetadataBlock;
      image.Write(metadata, 0, (int)blockSize);
    }

    image.Flush();
  }
}
