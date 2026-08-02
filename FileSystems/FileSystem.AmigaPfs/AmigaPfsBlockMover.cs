#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.AmigaPfs;

/// <summary>
/// Moves a file's blocks inside an AmigaPFS volume and repoints its directory
/// entry.
/// </summary>
/// <remarks>
/// <para>A file here is one contiguous run of blocks whose start is the anode
/// number in its directory entry, so relocating a file is the copy plus one
/// four-byte write. Nothing else records the position.</para>
///
/// <para>The entry is found by the anode it still names rather than by the
/// file's name, so two entries sharing a leaf name in different directories
/// cannot send the wrong one somewhere.</para>
/// </remarks>
public sealed class AmigaPfsBlockMover : IFilesystemBlockMover {

  /// <summary>Dirblock ids PFS3 uses.</summary>
  private const ushort DirBlockId = 0xC4;
  private const ushort DirBlockIdAlternate = 0xCC;

  /// <summary>Offset of the first entry inside a dirblock.</summary>
  private const int FirstEntryOffset = 20;

  /// <summary>Offset of the next-dirblock pointer inside a dirblock.</summary>
  private const int NextChainOffset = 12;

  /// <summary>Offset of the anode number inside a directory entry.</summary>
  private const int EntryAnodeOffset = 2;

  private int _blockSize;
  private readonly List<long> _directoryBlocks = [];
  private long _firstDataByte;

  /// <summary>Reads the geometry and walks the dirblock chain.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    image.Position = 0;
    using var reader = new AmigaPfsReader(image);
    this._blockSize = reader.BlockSize;

    var boot = new byte[Math.Min(this._blockSize, 64)];
    image.Position = 0;
    image.ReadExactly(boot);
    var signature = Encoding.ASCII.GetString(boot, 0, 4);
    if (signature is not ("PFS\x02" or "PFS\x03" or "PFSa"))
      throw new InvalidDataException($"AmigaPFS: '{signature}' is not a boot signature.");

    var rootBlockNumber = BinaryPrimitives.ReadUInt32BigEndian(boot.AsSpan(8));
    if (rootBlockNumber == 0) rootBlockNumber = 80;

    var rootBlock = new byte[this._blockSize];
    var rootOffset = (long)rootBlockNumber * this._blockSize;
    if (rootOffset + this._blockSize > image.Length)
      throw new InvalidDataException("AmigaPFS: the root block sits past the end of the image.");
    image.Position = rootOffset;
    image.ReadExactly(rootBlock);

    this._directoryBlocks.Clear();
    var block = BinaryPrimitives.ReadUInt32BigEndian(rootBlock.AsSpan(60));
    var seen = new HashSet<long>();
    var dirBlock = new byte[this._blockSize];
    while (block != 0 && seen.Add(block)) {
      var at = (long)block * this._blockSize;
      if (at + this._blockSize > image.Length) break;
      image.Position = at;
      image.ReadExactly(dirBlock);
      var id = BinaryPrimitives.ReadUInt16BigEndian(dirBlock);
      if (id != DirBlockId && id != DirBlockIdAlternate) break;
      this._directoryBlocks.Add(at);
      block = BinaryPrimitives.ReadUInt32BigEndian(dirBlock.AsSpan(NextChainOffset));
    }

    var first = long.MaxValue;
    foreach (var entry in reader.Entries) {
      if (entry.IsDirectory) continue;
      var (offset, length) = reader.Locate(entry);
      if (length > 0) first = Math.Min(first, offset);
    }
    this._firstDataByte = first == long.MaxValue ? rootOffset + this._blockSize : first;
  }

  /// <summary>Block size in bytes, as the volume was laid out with.</summary>
  public int BlockSize => this._blockSize;

  /// <summary>First byte a file may occupy: past the boot, root and dirblocks.</summary>
  public long FirstDataByte => this._firstDataByte;

  /// <inheritdoc />
  /// <summary>
  /// A run may be held outside the volume while the rest of the layout moves,
  /// which is what lets a full volume be rearranged at all.
  /// </summary>
  public bool SupportsHeldRuns => true;

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
    if (this._blockSize == 0) this.Init(image);

    if (newOffset % this._blockSize != 0)
      throw new NotSupportedException(
        $"AmigaPFS: {newOffset} is not on a {this._blockSize}-byte block boundary, which is all " +
        "an anode number can name.");

    var oldAnode = (uint)(oldOffset / this._blockSize);
    var newAnode = (uint)(newOffset / this._blockSize);
    if (oldAnode == newAnode) return;

    var dirBlock = new byte[this._blockSize];
    foreach (var at in this._directoryBlocks) {
      image.Position = at;
      image.ReadExactly(dirBlock);

      var entryOffset = FirstEntryOffset;
      while (entryOffset < this._blockSize) {
        var entryLength = dirBlock[entryOffset];
        if (entryLength == 0) break;
        if (entryOffset + entryLength > this._blockSize) break;
        if (entryLength < 17) { entryOffset += entryLength; continue; }

        if (BinaryPrimitives.ReadUInt32BigEndian(dirBlock.AsSpan(entryOffset + EntryAnodeOffset)) == oldAnode) {
          Span<byte> field = stackalloc byte[4];
          BinaryPrimitives.WriteUInt32BigEndian(field, newAnode);
          image.Position = at + entryOffset + EntryAnodeOffset;
          image.Write(field);
          image.Flush();
          return;
        }

        entryOffset += entryLength;
      }
    }

    throw new InvalidOperationException(
      $"AmigaPFS: no directory entry names anode {oldAnode}, so '{fileName}' cannot be repointed.");
  }
}
