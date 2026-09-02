#pragma warning disable CS1591
using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Layout;
using Compression.Registry;

namespace FileSystem.HfsPlus;

/// <summary>
/// In-place HFS+ block mover. Moves allocation-block-aligned extents and patches
/// the catalog file record's fork-data extents + allocation bitmap so the file
/// remains reachable at its new location.
/// <para>
/// Streaming: the mover never loads the whole image. <see cref="Init(Stream)"/>
/// reads only the 512-byte volume header at offset 1024; all metadata updates
/// are targeted reads/writes via <see cref="SectorCache"/> + <see cref="Stream.Flush"/>
/// barriers so a crash mid-operation leaves the image fsck-recoverable. Multi-TB
/// HFS+ images (catalog files alone can be tens of MB on large volumes) need only
/// ~256 MB of cache RAM regardless of image size.
/// </para>
/// </summary>
public sealed class HfsPlusBlockMover : IFilesystemBlockMover, IFilesystemMetadataMover {
  private const int VolumeHeaderOffset = 1024;
  private const int VolumeHeaderSize = 512;
  private const ushort HfsPlusSignature = 0x482B;
  private const ushort HfsxSignature = 0x4858;

  // Cached volume header fields populated by Init().
  private ushort _signature;
  private int _blockSize;
  private uint _catalogStartBlock;
  private uint _catalogBlockCount;
  private uint _totalBlocks;
  private long _imageLength;

    /// <summary>
  /// Gets the first data byte.
  /// </summary>
public long FirstDataByte => 0;

  /// <summary>Block size in bytes (allocation unit).</summary>
  public int BlockSize => _blockSize;

  /// <summary>
  /// Streaming init — reads only the 512-byte volume header at offset 1024 plus
  /// trails of catalog metadata as needed during patches.
  /// </summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (image.Length < VolumeHeaderOffset + VolumeHeaderSize)
      throw new InvalidDataException("HFS+ image too small to contain a volume header.");

    Span<byte> vh = stackalloc byte[VolumeHeaderSize];
    image.Position = VolumeHeaderOffset;
    image.ReadExactly(vh);

    _signature = BinaryPrimitives.ReadUInt16BigEndian(vh);
    if (_signature != HfsPlusSignature && _signature != HfsxSignature)
      throw new InvalidDataException("HFS+ volume header signature missing.");

    _blockSize = (int)BinaryPrimitives.ReadUInt32BigEndian(vh[40..]);
    if (_blockSize == 0)
      throw new InvalidDataException("HFS+ volume header has zero block size.");

    // catalogFile.extents[0] lives at VH+288 (startBlock) and VH+292 (blockCount).
    _totalBlocks = BinaryPrimitives.ReadUInt32BigEndian(vh[44..]);
    _catalogStartBlock = BinaryPrimitives.ReadUInt32BigEndian(vh[288..]);
    _catalogBlockCount = BinaryPrimitives.ReadUInt32BigEndian(vh[292..]);
    _imageLength = image.Length;
  }

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
  /// <remarks>
  /// Power-fail-safe in-place metadata update via three targeted-write steps:
  /// allocation bitmap claim → catalog record patch → bitmap free → alternate
  /// VH mirror. After each step the stream is flushed so the OS commits that
  /// step before starting the next. The image is never loaded whole into
  /// memory — multi-TB HFS+ images require only a few sector reads/writes
  /// per move.
  /// </remarks>
  /// <summary>
  /// Each call repoints the extent descriptor naming the run it is given and
  /// leaves the fork's other descriptors alone, so an owner in several runs is
  /// simply several calls.
  /// </summary>
  public bool RepointsRunsIndependently => true;

  /// <summary>
  /// A run may be held outside the volume while the rest of the layout moves,
  /// which is what lets a full volume be rearranged at all.
  /// </summary>
  public bool SupportsHeldRuns => true;

  /// <inheritdoc />
    /// <summary>
  /// Performs the update allocation after move operation.
  /// </summary>
public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    if (_blockSize == 0)
      Init(image);

    if (_catalogStartBlock == 0 || _catalogBlockCount == 0) return;

    var oldBlock = (uint)(oldOffset / _blockSize);
    var newBlock = (uint)(newOffset / _blockSize);
    var blockCount = (int)((length + _blockSize - 1) / _blockSize);
    // The bitmap lives wherever the allocation fork says it does. Assuming
    // block 1 was right only for volumes this repository had laid out itself,
    // and became wrong the moment the allocation file was relocated.
    var bitmapBase = AllocationFileOffset(image);

    using var cache = new SectorCache(image);

    // Step 1: Claim new blocks in allocation bitmap.
    for (var i = 0; i < blockCount; i++)
      SetBitmapBitStream(image, bitmapBase, newBlock + (uint)i);
    cache.Invalidate(bitmapBase, blockCount * 2 + 1); // a few bytes around bitmap
    image.Flush();

    // Step 2: Walk catalog B-tree leaf chain via cache + patch matching file
    // record's fork data with a single 4-byte targeted write per match.
    PatchCatalogStartBlockStream(image, cache, fileName, oldBlock, newBlock);
    image.Flush();

    // The old blocks are deliberately not released here. A fork's runs are
    // moved one at a time, and one run's old home is routinely where another
    // run has just landed — clearing it marks live blocks free, and the next
    // file added to the volume is written straight over them. The caller
    // settles the bitmap once every run has moved, from where they all ended
    // up, which is the only point at which the answer is knowable.

    // Step 4: Mirror alternate volume header (last 1024 bytes contain the
    // alternate VH at offset image.Length-1024; field layout matches primary).
    if (_imageLength >= 1024) {
      Span<byte> vh = stackalloc byte[VolumeHeaderSize];
      image.Position = VolumeHeaderOffset;
      image.ReadExactly(vh);
      image.Position = _imageLength - 1024;
      image.Write(vh);
      image.Flush();
    }
  }

  /// <summary>
  /// Writes the allocation bitmap from the runs the volume actually holds.
  /// </summary>
  /// <remarks>
  /// Called once a layout pass has finished. Releasing a run's old blocks as it
  /// moves cannot be right while other runs are still moving: an old home is
  /// routinely another run's new one, and clearing it hands live space out
  /// twice. From the finished layout the answer is simply what is covered.
  /// </remarks>
  public void SettleAllocationBitmap(Stream image, IEnumerable<(long Offset, long Length)> live) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(live);
    if (this._blockSize == 0) this.Init(image);

    var bitmapBase = AllocationFileOffset(image);
    var totalBlocks = this._totalBlocks > 0
      ? (int)Math.Min(this._totalBlocks, this._imageLength / this._blockSize)
      : (int)(this._imageLength / this._blockSize);
    var claimed = new bool[totalBlocks];

    foreach (var (offset, length) in live) {
      if (length <= 0) continue;
      var first = offset / this._blockSize;
      var last = (offset + length + this._blockSize - 1) / this._blockSize;
      for (var block = first; block < last && block < totalBlocks; ++block)
        if (block >= 0) claimed[block] = true;
    }

    var free = 0;
    for (var block = 0; block < totalBlocks; ++block) {
      if (claimed[block]) SetBitmapBitStream(image, bitmapBase, (uint)block);
      else { ClearBitmapBitStream(image, bitmapBase, (uint)block); ++free; }
    }

    // The header carries the free count as a number of its own, and fsck reads
    // it rather than counting the bitmap. Leaving it behind is how a volume
    // that is otherwise sound reads as corrupt.
    Span<byte> freeBlocks = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(freeBlocks, (uint)free);
    image.Position = VolumeHeaderOffset + 48;
    image.Write(freeBlocks);

    MirrorAlternateVolumeHeader(image);
    image.Flush();
  }

  // ── IFilesystemMetadataMover ──────────────────────────────────────────

  /// <summary>Where each relocatable fork's descriptor sits in the volume header.</summary>
  private static readonly Dictionary<string, int> ForkOffsets =
    new(StringComparer.OrdinalIgnoreCase) {
      ["HFS+ allocation file"] = 112,
      ["HFS+ catalog file"] = 272,
    };

  /// <summary>
  /// The allocation file and the catalog file. Both are ordinary forks whose
  /// extents the volume header records, which is the whole of what says where
  /// they are — so writing a new start block moves them. The boot region and the
  /// volume header itself are pinned: the header is at a fixed offset by
  /// definition, and it is what everything else is found through.
  /// </summary>
  public IReadOnlySet<string> RelocatableMetadata { get; } =
    ForkOffsets.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

  /// <inheritdoc />
    /// <summary>
  /// Performs the update metadata after move operation.
  /// </summary>
public void UpdateMetadataAfterMove(Stream image, string metadataName,
      long oldOffset, long newOffset, long length,
      IReadOnlyList<(long Offset, long Length)>? liveRanges = null) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(metadataName);
    if (!ForkOffsets.TryGetValue(metadataName, out var forkOffset))
      throw new NotSupportedException(
        $"HFS+: '{metadataName}' is not a fork this volume can be repointed at.");

    if (_blockSize == 0) Init(image);

    var oldBlock = (uint)(oldOffset / _blockSize);
    var newBlock = (uint)(newOffset / _blockSize);
    var blockCount = (int)((length + _blockSize - 1) / _blockSize);
    if (blockCount <= 0 || oldBlock == newBlock) return;

    // The fork's first extent is the whole of it here: the planner only offers
    // a structure that arrives as a single run.
    Span<byte> field = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(field, newBlock);
    image.Position = VolumeHeaderOffset + forkOffset + 16;
    image.Write(field);
    image.Flush();

    if (string.Equals(metadataName, "HFS+ catalog file", StringComparison.OrdinalIgnoreCase))
      _catalogStartBlock = newBlock;

    // Now that the header points at the new home, the bitmap is read and
    // written through it — which matters when the bitmap is what moved.
    var bitmapBase = AllocationFileOffset(image);
    for (var i = 0; i < blockCount; i++)
      SetBitmapBitStream(image, bitmapBase, newBlock + (uint)i);
    image.Flush();

    for (var i = 0; i < blockCount; i++) {
      var releasing = (long)(oldBlock + (uint)i) * _blockSize;
      if (IsLive(releasing, _blockSize, liveRanges)) continue;
      ClearBitmapBitStream(image, bitmapBase, oldBlock + (uint)i);
    }
    image.Flush();

    MirrorAlternateVolumeHeader(image);
  }

  /// <summary>Byte offset of the allocation file, as the volume header records it.</summary>
  private long AllocationFileOffset(Stream image) {
    Span<byte> field = stackalloc byte[4];
    image.Position = VolumeHeaderOffset + 112 + 16;
    image.ReadExactly(field);
    var startBlock = BinaryPrimitives.ReadUInt32BigEndian(field);
    return (long)startBlock * _blockSize;
  }

  /// <summary>
  /// Copies the volume header over the alternate one HFS+ keeps in the
  /// second-to-last sector. A driver reads whichever it finds intact, so
  /// leaving the copy naming the old positions would make the volume read two
  /// different ways.
  /// </summary>
  private void MirrorAlternateVolumeHeader(Stream image) {
    if (image.Length < 1024) return;
    Span<byte> vh = stackalloc byte[VolumeHeaderSize];
    image.Position = VolumeHeaderOffset;
    image.ReadExactly(vh);
    image.Position = image.Length - 1024;
    image.Write(vh);
    image.Flush();
  }

  /// <summary>Whether any live range covers part of this block.</summary>
  private static bool IsLive(long offset, long length,
      IReadOnlyList<(long Offset, long Length)>? liveRanges) {
    if (liveRanges == null) return false;
    foreach (var (start, len) in liveRanges)
      if (offset < start + len && start < offset + length)
        return true;
    return false;
  }

  // ── Stream-based bitmap RMW ────────────────────────────────────────────

  private static void SetBitmapBitStream(Stream image, long bitmapBase, uint block) {
    var pos = bitmapBase + block / 8;
    if (pos >= image.Length) return;
    Span<byte> b = stackalloc byte[1];
    image.Position = pos;
    image.ReadExactly(b);
    b[0] |= (byte)(1 << (int)(7 - block % 8));
    image.Position = pos;
    image.Write(b);
  }

  private static void ClearBitmapBitStream(Stream image, long bitmapBase, uint block) {
    var pos = bitmapBase + block / 8;
    if (pos >= image.Length) return;
    Span<byte> b = stackalloc byte[1];
    image.Position = pos;
    image.ReadExactly(b);
    b[0] &= (byte)~(1 << (int)(7 - block % 8));
    image.Position = pos;
    image.Write(b);
  }

  // ── Catalog walk via SectorCache ──────────────────────────────────────

  /// <summary>
  /// Walks the catalog B-tree leaf chain (linked via fLink at node offset 0)
  /// node-by-node through <see cref="SectorCache"/>. For each leaf record
  /// matching the target file name and old startBlock, writes 4 bytes at the
  /// fork-data startBlock field — no rest of the node needs to be rewritten.
  /// </summary>
  private void PatchCatalogStartBlockStream(Stream image, SectorCache cache,
      string fileName, uint oldBlock, uint newBlock) {
    var catalogOff = (long)_catalogStartBlock * _blockSize;
    if (catalogOff + 32 > _imageLength) return;

    // Read the BTreeNodeDescriptor + BTHeaderRec to find first leaf + node size.
    var hdr = cache.Read(catalogOff, 32);
    var hdrKind = (sbyte)hdr[8];
    if (hdrKind != 1) return; // not a header node

    // BTHeaderRec starts at +14: firstLeafNode at offset 10, nodeSize at offset 18.
    if (catalogOff + 14 + 30 > _imageLength) return;
    var btHeader = cache.Read(catalogOff + 14, 30);
    var firstLeaf = BinaryPrimitives.ReadUInt32BigEndian(btHeader.AsSpan(10));
    var nodeSize = BinaryPrimitives.ReadUInt16BigEndian(btHeader.AsSpan(18));
    if (nodeSize == 0) return;

    var currentNode = firstLeaf;
    var visited = new HashSet<uint>();
    // Hoist the patch buffer outside the loop to avoid CA2014 (stackalloc in loop
    // could blow the stack on very long catalog leaf chains).
    Span<byte> patched = stackalloc byte[4];
    while (currentNode != 0 && visited.Add(currentNode)) {
      var nodeOffset = catalogOff + (long)currentNode * nodeSize;
      if (nodeOffset + nodeSize > _imageLength) break;

      // Read this leaf node into a managed buffer via the cache.
      var nd = cache.Read(nodeOffset, nodeSize);
      var ndKind = (sbyte)nd[8];
      if (ndKind != -1) break;

      var numRecords = BinaryPrimitives.ReadUInt16BigEndian(nd.AsSpan(10));
      for (var i = 0; i < numRecords; i++) {
        var offsetPos = nodeSize - 2 * (i + 1);
        if (offsetPos < 12) break;
        var recOffset = BinaryPrimitives.ReadUInt16BigEndian(nd.AsSpan(offsetPos));
        if (recOffset + 8 > nodeSize) continue;

        var keyLength = BinaryPrimitives.ReadUInt16BigEndian(nd.AsSpan(recOffset));
        if (keyLength < 6) continue;
        var nameLength = BinaryPrimitives.ReadUInt16BigEndian(nd.AsSpan(recOffset + 6));
        var nameByteLen = nameLength * 2;
        var name = "";
        if (nameLength > 0 && recOffset + 8 + nameByteLen <= nodeSize)
          name = Encoding.BigEndianUnicode.GetString(nd, recOffset + 8, nameByteLen);

        var dataOffset = recOffset + 2 + keyLength;
        if ((dataOffset & 1) != 0) dataOffset++;
        if (dataOffset + 248 > nodeSize) continue;
        var recordType = BinaryPrimitives.ReadInt16BigEndian(nd.AsSpan(dataOffset));
        if (recordType != 2) continue; // file records only

        const int dataForkOffset = 88;
        const int extentRecordOffset = 16;      // past logicalSize, clumpSize, totalBlocks
        const int extentCount = 8;              // descriptors an HFS+ fork holds
        const int extentSize = 8;               // startBlock + blockCount, each four bytes

        if (!name.Equals(fileName, StringComparison.OrdinalIgnoreCase)
            && !fileName.Equals("*", StringComparison.Ordinal)) continue;

        // A fork is eight extent descriptors, not one. Looking only at the
        // first meant a file in more than one piece had whichever piece moved
        // written over its first — the file kept its length and lost its
        // contents, which is why this mover was written and then not used.
        for (var extent = 0; extent < extentCount; ++extent) {
          var descriptor = dataOffset + dataForkOffset + extentRecordOffset + extent * extentSize;
          if (descriptor + extentSize > nodeSize) break;

          var startBlock = BinaryPrimitives.ReadUInt32BigEndian(nd.AsSpan(descriptor));
          var extentBlocks = BinaryPrimitives.ReadUInt32BigEndian(nd.AsSpan(descriptor + 4));
          if (extentBlocks == 0) break;                       // unused descriptor: the fork ends
          if (startBlock != oldBlock) continue;

          // Targeted 4-byte write at the absolute offset of this startBlock.
          var fieldOff = nodeOffset + recOffset + 2 + keyLength;
          if ((fieldOff & 1) != 0) fieldOff++;
          fieldOff += dataForkOffset + extentRecordOffset + extent * extentSize;
          BinaryPrimitives.WriteUInt32BigEndian(patched, newBlock);
          image.Position = fieldOff;
          image.Write(patched);
          cache.Invalidate(fieldOff, 4);
          break;
        }
      }
      currentNode = BinaryPrimitives.ReadUInt32BigEndian(nd.AsSpan(0));
    }
  }
}
