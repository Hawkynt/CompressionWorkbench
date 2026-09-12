#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.Layout;

namespace FileFormat.Vhd;

/// <summary>
/// Compacts a dynamic VHD image by scanning the BAT for blocks whose data is
/// all-zero, marking them as unallocated (BAT entry = 0xFFFFFFFF), and then
/// rebuilding the physical file to remove the unused blocks.
/// </summary>
public static class VhdCompactor {

  /// <summary>
  /// Result of a VHD compaction: original and new file sizes, plus the number of
  /// blocks that were freed.
  /// </summary>
  public sealed record CompactResult(long OriginalSize, long NewSize, int BlocksFreed, bool WasReduced);

  /// <summary>
  /// Compacts a dynamic VHD by identifying all-zero allocated blocks, marking them
  /// as sparse in the BAT, and rebuilding the file to eliminate the freed physical
  /// blocks. Fixed VHDs are converted to dynamic to enable sparse blocks, then compacted.
  /// </summary>
  /// <param name="image">Readable/writable/seekable stream containing the VHD file.</param>
  /// <returns>Compaction result with before/after sizes and blocks freed.</returns>
  public static CompactResult Compact(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var originalSize = image.Length;

    if (originalSize < 512)
      throw new InvalidDataException("VHD: file too small.");

    // Stream the image through a sector cache so multi-TB VHDs do not need to
    // live in RAM. We only fetch the footer, dynamic header, BAT and the bytes
    // we actually need to inspect.
    using var cache = new SectorCache(image);

    // Determine VHD type from footer.
    var footerOff = originalSize - 512;
    var footer = cache.Read(footerOff, 512);
    if (!footer.AsSpan(0, 8).SequenceEqual("conectix"u8)) {
      var head = cache.Read(0, 512);
      if (head.AsSpan(0, 8).SequenceEqual("conectix"u8)) {
        footerOff = 0;
        footer = head;
      } else {
        throw new InvalidDataException("VHD: invalid footer magic.");
      }
    }

    var diskType = BinaryPrimitives.ReadUInt32BigEndian(footer.AsSpan(60));
    var virtualSize = (long)BinaryPrimitives.ReadUInt64BigEndian(footer.AsSpan(48));

    if (diskType == 2) {
      // Fixed VHD: convert to dynamic, then compact. Stream the raw payload via the cache.
      return CompactFromFixed(image, cache, originalSize, virtualSize);
    }

    if (diskType is not (3 or 4))
      throw new InvalidDataException($"VHD: unsupported disk type {diskType}.");

    // Dynamic VHD.
    var dataOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(footer.AsSpan(16));
    if (dataOffset < 0 || dataOffset + 1024 > originalSize)
      throw new InvalidDataException("VHD: dynamic disk header offset out of range.");

    var dynHdr = cache.Read(dataOffset, 1024);
    if (!dynHdr.AsSpan(0, 8).SequenceEqual("cxsparse"u8))
      throw new InvalidDataException("VHD: invalid dynamic disk header magic.");

    var batOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(dynHdr.AsSpan(16));
    var maxBatEntries = (int)BinaryPrimitives.ReadUInt32BigEndian(dynHdr.AsSpan(28));
    var blockSize = (int)BinaryPrimitives.ReadUInt32BigEndian(dynHdr.AsSpan(32));

    var sectorsPerBlock = blockSize / 512;
    var bitmapSectors = (sectorsPerBlock + 512 * 8 - 1) / (512 * 8);
    var bitmapBytes = bitmapSectors * 512;

    // Read BAT through the cache.
    var bat = new uint[maxBatEntries];
    var batBytes = new byte[Math.Min(maxBatEntries * 4L, 64 * 1024)];
    var batRemaining = (long)maxBatEntries * 4;
    var batSrc = batOffset;
    var batEntryIdx = 0;
    while (batRemaining > 0) {
      var take = (int)Math.Min(batRemaining, batBytes.Length);
      cache.Read(batSrc, batBytes.AsSpan(0, take));
      for (var i = 0; i + 4 <= take; i += 4)
        bat[batEntryIdx++] = BinaryPrimitives.ReadUInt32BigEndian(batBytes.AsSpan(i, 4));
      batRemaining -= take;
      batSrc += take;
    }

    // Scan for all-zero blocks — read each allocated block via the cache rather than
    // materialising the entire image. Reuse a single buffer.
    var scanBuf = new byte[blockSize];
    var blocksFreed = 0;
    for (var b = 0; b < maxBatEntries; b++) {
      if (bat[b] == 0xFFFFFFFF) continue; // already sparse

      var physOffset = (long)bat[b] * 512 + bitmapBytes;
      if (physOffset + blockSize > originalSize) continue;

      cache.Read(physOffset, scanBuf);
      if (IsAllZero(scanBuf)) {
        bat[b] = 0xFFFFFFFF; // mark as unallocated
        blocksFreed++;
      }
    }

    if (blocksFreed == 0)
      return new CompactResult(originalSize, originalSize, 0, false);

    // Rebuild the dynamic VHD with only allocated blocks. The assembled virtual
    // disk must fit in memory because VhdWriter.SetDiskData / BuildDynamic take
    // a byte[]; matches the prior behaviour and is unavoidable without a
    // streaming writer.
    var virtualDisk = AssembleVirtualDisk(cache, bat, maxBatEntries, blockSize, bitmapBytes, virtualSize, originalSize);
    var writer = new VhdWriter();
    writer.SetDiskData(virtualDisk);
    var rebuilt = writer.BuildDynamic(blockSize);

    // Invalidate every cached chunk before we mutate the stream out from under it.
    cache.InvalidateAll();
    image.Position = 0;
    image.Write(rebuilt);
    image.SetLength(rebuilt.Length);

    return new CompactResult(originalSize, rebuilt.Length, blocksFreed, true);
  }

  private static CompactResult CompactFromFixed(Stream image, SectorCache cache, long originalSize, long virtualSize) {
    // Extract the raw disk data (everything before the trailing footer) via the cache.
    var rawLen = (int)(originalSize - 512);
    var rawDisk = rawLen > 0 ? cache.Read(0, rawLen) : [];

    // Build as dynamic (which already does sparse detection).
    var writer = new VhdWriter();
    writer.SetDiskData(rawDisk);
    var dynamic = writer.BuildDynamic();

    // Count how many blocks are now sparse.
    var maxBat = (rawDisk.Length + 0x00200000 - 1) / 0x00200000;
    var freed = 0;
    for (var b = 0; b < maxBat; b++) {
      var srcOff = (long)b * 0x00200000;
      var srcLen = (int)Math.Min(0x00200000, rawDisk.Length - srcOff);
      if (srcLen <= 0 || IsAllZero(rawDisk.AsSpan((int)srcOff, srcLen)))
        freed++;
    }

    if (dynamic.Length >= originalSize)
      return new CompactResult(originalSize, originalSize, 0, false);

    cache.InvalidateAll();
    image.Position = 0;
    image.Write(dynamic);
    image.SetLength(dynamic.Length);

    return new CompactResult(originalSize, dynamic.Length, freed, true);
  }

  private static byte[] AssembleVirtualDisk(SectorCache cache, uint[] bat, int maxBatEntries,
      int blockSize, int bitmapBytes, long virtualSize, long streamLength) {
    var result = new byte[virtualSize];
    for (var b = 0; b < maxBatEntries; b++) {
      if (bat[b] == 0xFFFFFFFF) continue;
      var physOffset = (long)bat[b] * 512 + bitmapBytes;
      var virtOffset = (long)b * blockSize;
      var copyLen = (int)Math.Min(blockSize, virtualSize - virtOffset);
      if (copyLen <= 0 || physOffset + copyLen > streamLength) continue;
      cache.Read(physOffset, result.AsSpan((int)virtOffset, copyLen));
    }
    return result;
  }

  private static bool IsAllZero(ReadOnlySpan<byte> data) {
    foreach (var b in data)
      if (b != 0) return false;
    return true;
  }
}
