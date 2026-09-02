#pragma warning disable CS1591
using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.DoubleSpace;

/// <summary>
/// In-place DoubleSpace/DriveSpace CVF block mover. Moves compressed cluster
/// runs within the DATA region and patches the MDFAT + BitFAT + inner FAT
/// chain so the file remains reachable at its new physical location.
///
/// <para>Unlike a plain FAT block mover that moves raw cluster bytes and
/// patches a FAT chain, DoubleSpace has a two-level indirection: the inner
/// FAT maps files to logical clusters, and the MDFAT maps logical clusters
/// to physical sector offsets within the DATA region. A "move" here
/// relocates the compressed run in the DATA region and patches the MDFAT
/// entry for the corresponding logical cluster to point at the new physical
/// sector offset. The inner FAT chain is unchanged (logical cluster numbers
/// don't move).</para>
/// </summary>
public sealed class DoubleSpaceBlockMover : IFilesystemBlockMover {

  // Cached MDBPB geometry, parsed once per Init() call.
  private int _bytesPerSector;
  private int _sectorsPerCluster;
  private int _reservedSectors;
  private int _fatCount;
  private int _rootEntryCount;
  private int _fatSize;
  private int _mdfatStartSector;
  private int _mdfatLenSectors;
  private int _bitFatStartSector;
  private int _bitFatLenSectors;
  private int _dataStartSector;
  private int _dataLenSectors;

  /// <summary>Byte offset of the DATA region start.</summary>
  public long DataRegionByteStart => (long)_dataStartSector * _bytesPerSector;

  /// <summary>Bytes per sector.</summary>
  public int BytesPerSector => _bytesPerSector;

  /// <summary>
  /// Initialises the mover by parsing MDBPB fields from <paramref name="image"/>.
  /// Must be called before any move operations.
  /// </summary>
  public void Init(byte[] image) {
    _bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(11));
    if (_bytesPerSector is 0 or > 4096) _bytesPerSector = 512;
    _sectorsPerCluster = image[13] == 0 ? 1 : image[13];
    _reservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(14));
    if (_reservedSectors == 0) _reservedSectors = 1;
    _fatCount = image[16] == 0 ? 2 : image[16];
    _rootEntryCount = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(17));
    _fatSize = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(22));

    _mdfatStartSector = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(44));
    _mdfatLenSectors = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(48));
    _bitFatStartSector = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(52));
    _bitFatLenSectors = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(56));
    _dataStartSector = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(60));
    _dataLenSectors = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(64));
  }

  // ── IFilesystemBlockMover ──────────────────────────────────────────────

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
  /// Patches MDFAT and BitFAT after a raw extent move within the DATA region.
  /// Finds the MDFAT entry whose physical sector range matches the old offset,
  /// rewrites it to point at the new physical sector, and updates BitFAT bits
  /// accordingly (clears old sectors, sets new sectors).
  /// <para>The inner FAT chain is NOT touched because logical cluster numbers
  /// do not change during a physical move — only the MDFAT indirection
  /// changes.</para>
  /// </summary>
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    image.Position = 0;
    using var ms = new MemoryStream();
    image.CopyTo(ms);
    var data = ms.ToArray();

    var dataRegionByteStart = (long)_dataStartSector * _bytesPerSector;
    var mdfatByteBase = _mdfatStartSector * _bytesPerSector;
    var mdfatEntryCount = _mdfatLenSectors * _bytesPerSector / 4;

    // Convert old/new byte offsets to physical sector offsets relative to DATA start.
    var oldPhysSector = (int)((oldOffset - dataRegionByteStart) / _bytesPerSector);
    var newPhysSector = (int)((newOffset - dataRegionByteStart) / _bytesPerSector);
    var moveSectors = (int)(length / _bytesPerSector);
    if (moveSectors == 0) moveSectors = 1;

    // Scan MDFAT for entries whose physical sector falls within [oldPhysSector, oldPhysSector+moveSectors).
    for (var i = 0; i < mdfatEntryCount; i++) {
      var entryOffset = mdfatByteBase + i * 4;
      if (entryOffset + 4 > data.Length) break;

      var entry = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(entryOffset));
      var physSector = (int)(entry & 0x1FFFFFu);
      var runSectors = (int)((entry >> 21) & 0x7Fu);
      var flags = (int)((entry >> 28) & 0xFu);

      if (flags == 0 || runSectors == 0) continue;

      // Check if this MDFAT entry's physical sector falls within the moved range.
      if (physSector >= oldPhysSector && physSector < oldPhysSector + moveSectors) {
        // Compute new physical sector for this entry.
        var offset = physSector - oldPhysSector;
        var newSector = newPhysSector + offset;

        // Rewrite MDFAT entry with new physical sector, same run length and flags.
        var newEntry = ((uint)newSector & 0x1FFFFFu)
          | (((uint)runSectors & 0x7Fu) << 21)
          | (((uint)flags & 0xFu) << 28);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(entryOffset), newEntry);

        // Update BitFAT: clear old sectors, set new sectors.
        UpdateBitFat(data, physSector, runSectors, clear: true);
        UpdateBitFat(data, newSector, runSectors, clear: false);
      }
    }

    // Write patched image back.
    image.Position = 0;
    image.Write(data, 0, data.Length);
    // Crash barrier: metadata commit durable before return.
    image.Flush();
  }

  // ── BitFAT helpers ────────────────────────────────────────────────────

  private void UpdateBitFat(byte[] data, int physSectorStart, int runSectors, bool clear) {
    const int BitFatRegionBytes = 8192;
    var bitFatByteBase = _bitFatStartSector * _bytesPerSector;
    var runByteStart = physSectorStart * _bytesPerSector;
    var runByteEnd = runByteStart + runSectors * _bytesPerSector;
    var firstRegion = runByteStart / BitFatRegionBytes;
    var lastRegion = (runByteEnd - 1) / BitFatRegionBytes;

    for (var r = firstRegion; r <= lastRegion; r++) {
      var bitPos = bitFatByteBase + r / 8;
      if (bitPos >= data.Length) break;
      if (clear)
        data[bitPos] &= (byte)~(1 << (r & 7));
      else
        data[bitPos] |= (byte)(1 << (r & 7));
    }
  }
}
