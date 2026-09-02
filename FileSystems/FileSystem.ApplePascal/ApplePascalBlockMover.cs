#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.ApplePascal;

/// <summary>
/// Moves a file's blocks inside an Apple Pascal volume and repoints its
/// directory entry.
/// </summary>
/// <remarks>
/// <para>A p-System file is one contiguous run, and its directory entry says
/// where the run begins and where it ends. Both are block numbers, so
/// relocating a file is the copy plus those two sixteen-bit fields.</para>
///
/// <para>The entry is found by the block it still names rather than by the
/// file's name, so two entries sharing a name cannot send the wrong one
/// somewhere.</para>
///
/// <para>The directory is kept in block order afterwards, which is what the
/// p-System itself expects: it looks for free space by walking the entries and
/// measuring the gaps between them, and an out-of-order directory makes that
/// reading nonsense.</para>
/// </remarks>
public sealed class ApplePascalBlockMover : IFilesystemBlockMover {

  /// <summary>Offset of the first-block field inside a directory entry.</summary>
  private const int EntryStartBlockOffset = 0;

  /// <summary>Offset of the past-the-end block field inside a directory entry.</summary>
  private const int EntryEndBlockOffset = 2;

  private int _entryCount;

  /// <summary>Reads how many entries the directory holds.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    var header = new byte[ApplePascalReader.EntrySize];
    if (image.Length < ApplePascalReader.DirectoryOffset + header.Length)
      throw new InvalidDataException("Apple Pascal: the image is too short to hold a directory.");

    image.Position = ApplePascalReader.DirectoryOffset;
    image.ReadExactly(header);
    this._entryCount = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(16));
    if (this._entryCount > ApplePascalReader.MaxEntries)
      throw new InvalidDataException(
        $"Apple Pascal: the directory claims {this._entryCount} entries, past the " +
        $"{ApplePascalReader.MaxEntries} it can hold.");
  }

  /// <summary>A block. A directory entry names a block, not a byte.</summary>
  public int BlockSize => ApplePascalReader.BlockSize;

  /// <summary>First byte a file may occupy: past the boot blocks and the directory.</summary>
  public long FirstDataByte => 6L * ApplePascalReader.BlockSize;

  /// <summary>
  /// Each call repoints the entry it is given and nothing else, so an owner in
  /// several runs — which this format cannot produce — would be several calls.
  /// </summary>
  public bool RepointsRunsIndependently => true;

  /// <summary>
  /// A run may be held outside the volume while the rest of the layout moves,
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
    if (this._entryCount == 0) this.Init(image);

    var blockSize = ApplePascalReader.BlockSize;
    if (newOffset % blockSize != 0)
      throw new NotSupportedException(
        $"Apple Pascal: {newOffset} is not on a {blockSize}-byte block boundary, which is all a " +
        "directory entry can name.");

    var oldBlock = oldOffset / blockSize;
    var newBlock = newOffset / blockSize;
    if (oldBlock == newBlock) return;
    if (newBlock > ushort.MaxValue)
      throw new NotSupportedException(
        $"Apple Pascal: block {newBlock} is past the 65535 a sixteen-bit block number holds.");

    var entry = new byte[4];
    for (var i = 0; i < this._entryCount; ++i) {
      var at = ApplePascalReader.DirectoryOffset + (long)(i + 1) * ApplePascalReader.EntrySize;
      if (at + entry.Length > image.Length) break;

      image.Position = at;
      image.ReadExactly(entry);
      var start = BinaryPrimitives.ReadUInt16LittleEndian(entry);
      if (start != oldBlock) continue;

      var end = BinaryPrimitives.ReadUInt16LittleEndian(entry.AsSpan(EntryEndBlockOffset));
      var blocks = end - start;
      if (blocks <= 0) continue;
      if (newBlock + blocks > ushort.MaxValue)
        throw new NotSupportedException(
          $"Apple Pascal: '{fileName}' would end past the 65535 a block number holds.");

      BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(EntryStartBlockOffset), (ushort)newBlock);
      BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(EntryEndBlockOffset), (ushort)(newBlock + blocks));
      image.Position = at;
      image.Write(entry);
      image.Flush();
      return;
    }

    throw new InvalidOperationException(
      $"Apple Pascal: no directory entry starts at block {oldBlock}, so '{fileName}' cannot be " +
      "repointed.");
  }

  /// <summary>
  /// Puts the directory back in block order. The p-System finds free space by
  /// walking the entries and measuring the gaps between them, so an
  /// out-of-order directory reads as a volume with no room and overlapping
  /// files.
  /// </summary>
  public void SortDirectory(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (this._entryCount == 0) this.Init(image);
    if (this._entryCount <= 1) return;

    var size = ApplePascalReader.EntrySize;
    var first = ApplePascalReader.DirectoryOffset + (long)size;
    var table = new byte[this._entryCount * size];
    if (first + table.Length > image.Length) return;

    image.Position = first;
    image.ReadExactly(table);

    var order = Enumerable.Range(0, this._entryCount)
      .OrderBy(i => BinaryPrimitives.ReadUInt16LittleEndian(table.AsSpan(i * size)))
      .ToArray();

    var sorted = new byte[table.Length];
    for (var i = 0; i < order.Length; ++i)
      Array.Copy(table, order[i] * size, sorted, i * size, size);

    if (sorted.AsSpan().SequenceEqual(table)) return;
    image.Position = first;
    image.Write(sorted);
    image.Flush();
  }
}
