#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.Zfs;

/// <summary>
/// Moves a file's blocks inside a ZFS pool, repoints the block pointer that
/// named each, and takes every check above them again.
/// </summary>
/// <remarks>
/// <para>A block pointer holds the address in 512-byte sectors and a Fletcher-4
/// over the bytes it points at. A move does not change those bytes, so that
/// check survives; every check above it does not, because a pointer lives
/// inside a block whose own check lives in the pointer above.</para>
///
/// <para>So the addresses are written as the pass goes, and the checks are
/// taken again from the bottom up once it is over — a pointer is recorded
/// before everything it reaches, so walking the record backwards settles every
/// child before its parent.</para>
/// </remarks>
public sealed class ZfsBlockMover : IFilesystemBlockMover {

  private ZfsLayout.Layout? _layout;

  /// <summary>Where the pointer naming each data block sits, keyed by the block.</summary>
  private readonly Dictionary<long, long> _pointerOf = [];

  /// <summary>Reads the pool once and notes which pointer names each block.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    this._layout = ZfsLayout.Read(image);
    if (this._layout == null)
      throw new InvalidDataException("ZFS: the pool is not one this reads.");

    this._pointerOf.Clear();
    foreach (var block in this._layout.DataBlocks)
      this._pointerOf[block.Offset] = block.PointerOffset;
  }

  /// <summary>A sector, which is the unit a block pointer counts addresses in.</summary>
  public int BlockSize => (int)ZfsConstants.SectorSize;

  /// <summary>First byte a block may occupy: past the two labels at the front.</summary>
  public long FirstDataByte => 2L * ZfsConstants.LabelSize;

  /// <summary>
  /// Each call repoints the pointer naming the block it is given, so a file in
  /// several blocks is simply several calls.
  /// </summary>
  public bool RepointsRunsIndependently => true;

  /// <summary>
  /// A block may be held outside the pool while the rest of the layout moves,
  /// which is what lets a full pool be rearranged at all.
  /// </summary>
  public bool SupportsHeldRuns => true;

  /// <inheritdoc />
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
    if (this._layout == null) this.Init(image);
    if (oldOffset == newOffset) return;

    if (newOffset % ZfsConstants.SectorSize != 0)
      throw new NotSupportedException(
        $"ZFS: {newOffset} is not on a {ZfsConstants.SectorSize}-byte sector boundary, which is " +
        "what a block pointer counts in.");

    if (!this._pointerOf.Remove(oldOffset, out var pointerAt))
      throw new InvalidOperationException(
        $"ZFS: no block pointer names {oldOffset}, so '{fileName}' cannot be repointed.");

    // The address shares its word with a flag the pool sets for gang blocks;
    // only the address is ours to change.
    Span<byte> word = stackalloc byte[8];
    image.Position = pointerAt + 8;
    image.ReadExactly(word);
    var flags = BinaryPrimitives.ReadUInt64LittleEndian(word) & 0x8000000000000000UL;
    BinaryPrimitives.WriteUInt64LittleEndian(word,
      flags | ((ulong)(newOffset / ZfsConstants.SectorSize) & 0x7FFFFFFFFFFFFFFFUL));
    image.Position = pointerAt + 8;
    image.Write(word);

    this._pointerOf[newOffset] = pointerAt;
    image.Flush();
  }

  /// <summary>
  /// Takes every check in the pool again, from the bottom up.
  /// </summary>
  /// <remarks>
  /// Called once a layout pass has finished. A pointer's check covers the block
  /// it names, and that block holds the pointers below it — so a parent can
  /// only be settled once its children are. The walk records a pointer before
  /// everything it reaches, which makes the record read backwards exactly that
  /// order.
  /// </remarks>
  public void SettleChecksums(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    // Read the pool as it now stands: the addresses have already changed.
    var settled = ZfsLayout.Read(image);
    if (settled == null)
      throw new InvalidDataException("ZFS: the pool no longer reads after the pass.");

    for (var i = settled.Pointers.Count - 1; i >= 0; --i) {
      var pointer = settled.Pointers[i];
      if (pointer.BlockOffset < 0 || pointer.BlockLength <= 0) continue;
      if (pointer.BlockOffset + pointer.BlockLength > image.Length) continue;

      var block = new byte[pointer.BlockLength];
      image.Position = pointer.BlockOffset;
      image.ReadExactly(block);

      var check = Fletcher4.Compute(block);
      var bytes = new byte[32];
      check.WriteLe(bytes);
      image.Position = pointer.PointerOffset + 0x60;
      image.Write(bytes, 0, 32);
    }

    MirrorUberblocks(image);
    image.Flush();
  }

  /// <summary>
  /// Copies the first label's uberblock array over the others.
  /// </summary>
  /// <remarks>
  /// A pool keeps four labels and the reader takes the first, but leaving the
  /// rest naming a check that no longer holds would make the pool read one way
  /// from the front and another from the back.
  /// </remarks>
  private static void MirrorUberblocks(Stream image) {
    const int at = ZfsConstants.UberblockArrayOffset;
    const int length = ZfsConstants.LabelSize - at;
    if (image.Length < 2L * ZfsConstants.LabelSize) return;

    var array = new byte[length];
    image.Position = at;
    image.ReadExactly(array);

    var labels = new List<long> { ZfsConstants.LabelSize };
    if (image.Length >= 4L * ZfsConstants.LabelSize) {
      labels.Add(image.Length - 2L * ZfsConstants.LabelSize);
      labels.Add(image.Length - ZfsConstants.LabelSize);
    }

    foreach (var label in labels) {
      if (label + at + length > image.Length) continue;
      image.Position = label + at;
      image.Write(array, 0, length);
    }
  }
}
