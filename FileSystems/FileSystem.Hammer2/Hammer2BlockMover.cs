#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.Hammer2;

/// <summary>
/// Moves a file's blocks inside a HAMMER2 volume, repoints the blockref that
/// named each, and takes the chain of checks above it again.
/// </summary>
/// <remarks>
/// <para>The check beside a blockref covers the bytes it points at, and a move
/// does not change those bytes — so the data's own check survives untouched.
/// What does not survive is every check above it: the blockref sits inside its
/// parent block, whose check sits in the blockref naming the parent, up to the
/// volume header and its sector CRCs.</para>
///
/// <para>So each move is one field and a walk outwards, and the volume headers
/// are stamped again at the end of the pass.</para>
/// </remarks>
public sealed class Hammer2BlockMover : IFilesystemBlockMover {

  private Hammer2Layout.Layout? _layout;
  private long _imageLength;

  /// <summary>Where each data block is now, and everything a move of it touches.</summary>
  private readonly Dictionary<long, Hammer2Layout.DataBlock> _blockAt = [];

  /// <summary>Reads the blockref tree once and notes the chain above each block.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    this._layout = Hammer2Layout.Read(image);
    if (this._layout == null)
      throw new InvalidDataException("HAMMER2: the volume is not one this reads.");

    this._imageLength = image.Length;
    this._blockAt.Clear();
    foreach (var block in this._layout.DataBlocks)
      this._blockAt[block.Offset] = block;
  }

  /// <summary>
  /// The largest block any file uses. A blockref names its block with a radix,
  /// so a destination has to be aligned to the block it holds.
  /// </summary>
  public int BlockSize {
    get {
      if (this._layout == null || this._layout.DataBlocks.Count == 0) return 65536;
      return this._layout.DataBlocks.Max(b => b.Length);
    }
  }

  /// <summary>First byte a file's block may occupy: past the volume headers.</summary>
  public long FirstDataByte =>
    (long)(this._layout?.VolumeHeaders.Count ?? 1) * Hammer2Layout.VolumeBytes;

  /// <summary>
  /// Each call repoints the blockref naming the block it is given, so a file in
  /// several blocks is simply several calls.
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

    Span<byte> field = stackalloc byte[8];
    var moved = 0L;
    while (moved < length) {
      if (!this._blockAt.Remove(oldOffset + moved, out var block))
        throw new InvalidOperationException(
          $"HAMMER2: no blockref names {oldOffset + moved}, so '{fileName}' cannot be repointed.");

      var destination = newOffset + moved;
      if (destination % block.Length != 0)
        throw new NotSupportedException(
          $"HAMMER2: {destination} is not aligned to the {block.Length}-byte block it would hold, " +
          "which is what a blockref's radix fixes.");

      BinaryPrimitives.WriteInt64LittleEndian(field,
        Hammer2Layout.EncodeDataOff(destination, block.Radix));
      image.Position = block.DataOffsetField;
      image.Write(field);

      // The bytes did not change, so the check beside the blockref still holds;
      // every check above it is over a block that just did.
      foreach (var link in block.Chain)
        RewriteCheck(image, link);

      this._blockAt[destination] = block with { Offset = destination };
      moved += block.Length;
    }

    image.Flush();
  }

  /// <summary>
  /// Stamps the volume headers again, which carry CRCs over their own sectors.
  /// </summary>
  /// <remarks>
  /// Called once a layout pass has finished. The headers sit above every check
  /// the pass rewrote, so leaving them behind hands back a volume whose own
  /// account of itself says it is damaged.
  /// </remarks>
  public void SettleVolumeHeaders(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (this._layout == null) this.Init(image);

    foreach (var at in this._layout!.VolumeHeaders) {
      if (at + Hammer2Layout.VolumeBytes > this._imageLength) continue;

      var header = new byte[Hammer2Layout.VolumeBytes];
      image.Position = at;
      image.ReadExactly(header);

      // The second sector's CRC first: the first sector's range covers the field
      // the second one lives in, and stops just before its own.
      var sector1 = Hammer2Crc.Iscsi32(header.AsSpan(512, 512));
      BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x1E0 + 6 * 4), sector1);
      var sector0 = Hammer2Crc.Iscsi32(header.AsSpan(0, 512 - 4));
      BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x1E0 + 7 * 4), sector0);

      var whole = Hammer2Crc.Iscsi32(header.AsSpan(0, Hammer2Layout.VolumeBytes - 4));
      BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(Hammer2Layout.VolumeBytes - 4), whole);

      image.Position = at;
      image.Write(header, 0, Hammer2Layout.VolumeBytes);
    }

    image.Flush();
  }

  /// <summary>Takes a block's check again and writes it where it belongs.</summary>
  private static void RewriteCheck(Stream image, Hammer2Layout.CheckLink link) {
    if (link.BlockOffset < 0 || link.BlockLength <= 0) return;
    if (link.BlockOffset + link.BlockLength > image.Length) return;

    var block = new byte[link.BlockLength];
    image.Position = link.BlockOffset;
    image.ReadExactly(block);

    Span<byte> check = stackalloc byte[8];
    BinaryPrimitives.WriteUInt64LittleEndian(check,
      Hammer2Crc.XxHash64(block, Hammer2Crc.Hammer2Seed));
    image.Position = link.CheckFieldOffset;
    image.Write(check);
  }
}
