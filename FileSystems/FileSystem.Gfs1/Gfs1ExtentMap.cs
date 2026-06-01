#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.Gfs1;

/// <summary>
/// Walks a Sistina GFS1 image written by <see cref="Gfs1Writer"/> and yields
/// its on-disk extents.
/// </summary>
internal static class Gfs1ExtentMap {
  internal static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    using var ms = new MemoryStream();
    image.CopyTo(ms);
    var bytes = ms.ToArray();
    return EnumerateBytes(bytes);
  }

  private static List<DefragBlockInfo> EnumerateBytes(byte[] bytes) {
    var result = new List<DefragBlockInfo>();
    var sb = Gfs1Superblock.TryParse(bytes);
    if (!sb.Valid) return result;
    // 0..65536 = boot/reserved; 65536..65536+BlockSize = superblock.
    result.Add(new DefragBlockInfo(0, Gfs1Writer.SuperblockOffset, DefragBlockKind.MetadataReserved, "boot"));
    result.Add(new DefragBlockInfo(Gfs1Writer.SuperblockOffset, Gfs1Writer.BlockSize, DefragBlockKind.MetadataReserved, "superblock"));
    var inodeStart = Gfs1Writer.SuperblockOffset + Gfs1Writer.BlockSize;
    try {
      using var rs = new MemoryStream(bytes);
      var reader = new Gfs1Reader(rs);
      // Surface root dir + inode table block.
      result.Add(new DefragBlockInfo(inodeStart, Gfs1Writer.BlockSize, DefragBlockKind.MetadataReserved, "inode_table"));
      foreach (var e in reader.Entries) {
        if (e.FirstBlock == 0) continue;
        var blocks = e.IsDirectory ? 1
          : (int)((e.Size + Gfs1Writer.BlockSize - 1) / Gfs1Writer.BlockSize);
        var len = (long)blocks * Gfs1Writer.BlockSize;
        result.Add(new DefragBlockInfo((long)e.FirstBlock * Gfs1Writer.BlockSize, len,
          e.IsDirectory ? DefragBlockKind.MetadataReserved : DefragBlockKind.Used,
          e.IsDirectory ? null : e.Name));
      }
      // Root dir (inode 2) is not yielded by reader.Entries — surface it too.
      // Its first block sits at inodeStart + BlockSize.
      var rootDirStart = inodeStart + Gfs1Writer.BlockSize;
      if (rootDirStart < bytes.Length)
        result.Add(new DefragBlockInfo(rootDirStart, Gfs1Writer.BlockSize,
          DefragBlockKind.MetadataReserved, "root_dir"));
    } catch { /* tolerate */ }
    return result;
  }
}
