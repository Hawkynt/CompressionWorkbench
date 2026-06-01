#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.Jfs1;

/// <summary>
/// Walks a JFS1 image written by <see cref="Jfs1Writer"/> and yields its
/// on-disk extents for purge + defrag.
/// </summary>
internal static class Jfs1ExtentMap {
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
    var sb = Jfs1Superblock.TryParse(bytes);
    if (!sb.Valid) return result;
    var bsize = (int)sb.BlockSize;
    if (bsize <= 0) bsize = Jfs1Writer.DefaultBlockSize;
    // Block 0 = superblock; block 1.. = inode table; then data.
    result.Add(new DefragBlockInfo(0, bsize, DefragBlockKind.MetadataReserved, "superblock"));
    try {
      using var rs = new MemoryStream(bytes);
      var reader = new Jfs1Reader(rs);
      result.Add(new DefragBlockInfo(bsize, bsize, DefragBlockKind.MetadataReserved, "inode_table"));
      // Root dir block follows inode table.
      var rootDirOffset = 2L * bsize;
      if (rootDirOffset < bytes.Length)
        result.Add(new DefragBlockInfo(rootDirOffset, bsize, DefragBlockKind.MetadataReserved, "root_dir"));
      foreach (var e in reader.Entries) {
        if (e.FirstBlock == 0) continue;
        var blocks = e.IsDirectory ? 1 : (int)((e.Size + bsize - 1) / bsize);
        var len = (long)blocks * bsize;
        result.Add(new DefragBlockInfo((long)e.FirstBlock * bsize, len,
          e.IsDirectory ? DefragBlockKind.MetadataReserved : DefragBlockKind.Used,
          e.IsDirectory ? null : e.Name));
      }
    } catch { /* tolerate */ }
    return result;
  }
}
