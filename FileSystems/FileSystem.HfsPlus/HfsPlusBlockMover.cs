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
public sealed class HfsPlusBlockMover : IFilesystemBlockMover {
  private const int VolumeHeaderOffset = 1024;
  private const int VolumeHeaderSize = 512;
  private const ushort HfsPlusSignature = 0x482B;
  private const ushort HfsxSignature = 0x4858;

  // Cached volume header fields populated by Init().
  private ushort _signature;
  private int _blockSize;
  private uint _catalogStartBlock;
  private uint _catalogBlockCount;
  private long _imageLength;

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
    _catalogStartBlock = BinaryPrimitives.ReadUInt32BigEndian(vh[288..]);
    _catalogBlockCount = BinaryPrimitives.ReadUInt32BigEndian(vh[292..]);
    _imageLength = image.Length;
  }

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    if (length <= 0 || srcOffset == dstOffset) return;
    var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(length, 64 * 1024));
    try {
      var remaining = length;
      var src = srcOffset;
      var dst = dstOffset;
      while (remaining > 0) {
        var chunk = (int)Math.Min(remaining, buffer.Length);
        image.Position = src;
        image.ReadExactly(buffer, 0, chunk);
        image.Position = dst;
        image.Write(buffer, 0, chunk);
        src += chunk;
        dst += chunk;
        remaining -= chunk;
      }
      // Crash barrier: data must land on disk before metadata references it.
      image.Flush();
      if (zeroSource) {
        Array.Clear(buffer, 0, buffer.Length);
        remaining = length;
        src = srcOffset;
        while (remaining > 0) {
          var chunk = (int)Math.Min(remaining, buffer.Length);
          image.Position = src;
          image.Write(buffer, 0, chunk);
          src += chunk;
          remaining -= chunk;
        }
        // Crash barrier: data must land on disk before metadata references it.
        image.Flush();
      }
    } finally {
      ArrayPool<byte>.Shared.Return(buffer);
    }
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
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    if (_blockSize == 0)
      Init(image);

    if (_catalogStartBlock == 0 || _catalogBlockCount == 0) return;

    var oldBlock = (uint)(oldOffset / _blockSize);
    var newBlock = (uint)(newOffset / _blockSize);
    var blockCount = (int)((length + _blockSize - 1) / _blockSize);
    var bitmapBase = (long)_blockSize; // allocation bitmap at block 1

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

    // Step 3: Release old blocks in allocation bitmap.
    for (var i = 0; i < blockCount; i++)
      ClearBitmapBitStream(image, bitmapBase, oldBlock + (uint)i);
    cache.Invalidate(bitmapBase, blockCount * 2 + 1);
    image.Flush();

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
        var startBlock = BinaryPrimitives.ReadUInt32BigEndian(nd.AsSpan(dataOffset + dataForkOffset + 16));
        if (startBlock == oldBlock &&
            (name.Equals(fileName, StringComparison.OrdinalIgnoreCase) ||
             fileName.Equals("*", StringComparison.Ordinal))) {
          // Targeted 4-byte write at the absolute offset of the startBlock field.
          var fieldOff = nodeOffset + recOffset + 2 + keyLength;
          if ((fieldOff & 1) != 0) fieldOff++;
          fieldOff += dataForkOffset + 16;
          BinaryPrimitives.WriteUInt32BigEndian(patched, newBlock);
          image.Position = fieldOff;
          image.Write(patched);
          cache.Invalidate(fieldOff, 4);
        }
      }
      currentNode = BinaryPrimitives.ReadUInt32BigEndian(nd.AsSpan(0));
    }
  }
}
