#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;
using Compression.Registry;

namespace FileSystem.Jfs;

/// <summary>
/// Reads a JFS volume's block allocation map and reports which blocks are in
/// use. JFS keeps one dmap page per 8192 blocks; the page's persistent bitmap
/// holds one bit per block, set when the block is allocated. What the bitmap
/// leaves clear is exactly the free space — including the blocks a removed file
/// used to occupy, which still hold its bytes.
/// </summary>
/// <remarks>
/// The map is contiguous from <see cref="FirstDmapBlock" />, and each page
/// states which range it covers in its own header, so the walk validates that
/// against where it expected the page to be rather than assuming the layout.
/// </remarks>
public static class JfsExtentMap {

  private const int BlockSize = JfsWriter.BlockSize;
  private const int FirstDmapBlock = 20;
  private const int BlocksPerDmap = 8192;
  private const int PmapOffset = 3072;
  private const int LeavesPerDmap = 256;

  public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var result = new List<DefragBlockInfo>();
    try {
      if (image.CanSeek) image.Position = 0;
      using var accessor = new ImageAccessor(image);
      var volumeBlocks = accessor.Length / BlockSize;
      if (volumeBlocks <= FirstDmapBlock) return [];

      long runStart = -1;
      var pages = (volumeBlocks + BlocksPerDmap - 1) / BlocksPerDmap;
      for (var page = 0L; page < pages; ++page) {
        var pageBlock = FirstDmapBlock + page;
        var pageOffset = pageBlock * BlockSize;
        if (pageOffset + BlockSize > accessor.Length) break;

        var dmap = accessor.Read(pageOffset, BlockSize);
        var nblocks = BinaryPrimitives.ReadInt32LittleEndian(dmap.AsSpan(0, 4));
        var start = BinaryPrimitives.ReadInt64LittleEndian(dmap.AsSpan(8, 8));
        // A page that does not describe the range it should is not a dmap, and
        // guessing past it would put the wipe onto live data.
        if (start != page * BlocksPerDmap || nblocks <= 0 || nblocks > BlocksPerDmap) break;

        for (var leaf = 0; leaf < LeavesPerDmap; ++leaf) {
          var word = BinaryPrimitives.ReadUInt32LittleEndian(dmap.AsSpan(PmapOffset + leaf * 4, 4));
          for (var bit = 0; bit < 32; ++bit) {
            var block = start + leaf * 32 + bit;
            if (block >= volumeBlocks) break;
            var allocated = (word & (0x80000000u >> bit)) != 0;
            if (allocated) {
              if (runStart < 0) runStart = block;
              continue;
            }
            if (runStart >= 0) {
              result.Add(new DefragBlockInfo(runStart * BlockSize, (block - runStart) * BlockSize,
                DefragBlockKind.MetadataReserved));
              runStart = -1;
            }
          }
        }
      }
      if (runStart >= 0)
        result.Add(new DefragBlockInfo(runStart * BlockSize, (volumeBlocks - runStart) * BlockSize,
          DefragBlockKind.MetadataReserved));
    } catch {
      // A volume whose map we cannot read claims nothing, and a wipe of it would
      // zero live data — so report no extents at all.
      return [];
    }
    return result;
  }
}
