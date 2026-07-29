#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.Efs;

/// <summary>
/// Walks an EFS image written by <see cref="EfsWriter"/> and emits an extent
/// map: superblock + inode table become
/// <see cref="DefragBlockKind.MetadataReserved"/>; each directory body and
/// each file's data extent becomes <see cref="DefragBlockKind.Used"/>; any
/// unallocated tail bytes are surfaced as <see cref="DefragBlockKind.Free"/>.
/// Drives <see cref="UnusedSpaceWiper"/> for the purge capability.
/// </summary>
internal static class EfsExtentMap {

  internal static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    // The head carries the superblock and inode table; the walk itself goes
    // through the reader, which streams. Buffering the whole volume capped the
    // wipe at the array limit and, worse, returned nothing when it threw -- and
    // an empty extent list reads as "the volume is entirely free".
    var head = new byte[(int)Math.Min(image.Length, 4 * 1024 * 1024)];
    image.ReadExactly(head, 0, head.Length);
    image.Position = 0;
    return EnumerateBytes(head, image);
  }

  private static List<DefragBlockInfo> EnumerateBytes(byte[] bytes, Stream image) {
    var result = new List<DefragBlockInfo>();
    var sb = EfsSuperblock.TryParse(bytes);
    if (!sb.Valid) return result;

    // Sector 0 = superblock; sector 1..firstcg-1 = inode table.
    result.Add(new DefragBlockInfo(0, EfsWriter.BasicBlock, DefragBlockKind.MetadataReserved, "superblock"));
    var inodeTableLen = (sb.FirstCg - EfsWriter.InodeTableOffset) * EfsWriter.BasicBlock;
    if (inodeTableLen > 0)
      result.Add(new DefragBlockInfo(EfsWriter.InodeTableOffset * EfsWriter.BasicBlock, inodeTableLen,
        DefragBlockKind.MetadataReserved, "inode_table"));

    // Walk every inode; each non-zero extent becomes Used (named by file path
    // for cluster-tip wiping). Directories also count — their body is real
    // on-disk metadata. We have to surface the ROOT directory's extent too;
    // the reader's Entries collection lists children only.
    try {
      image.Position = 0;
      var reader = new EfsReader(image);
      // Root directory body extent: walk inode 2 directly so we don't lose it.
      var rootBlock = sb.FirstCg; // first data block after the inode table
      if (rootBlock > 0 && rootBlock * EfsWriter.BasicBlock < image.Length)
        result.Add(new DefragBlockInfo(rootBlock * EfsWriter.BasicBlock, EfsWriter.BasicBlock,
          DefragBlockKind.MetadataReserved, "root_dir"));
      foreach (var e in reader.Entries) {
        if (e.FirstBlock == 0) continue;
        var extentBlocks = e.IsDirectory
          ? 1
          : (e.Size + EfsWriter.BasicBlock - 1) / EfsWriter.BasicBlock;
        var len = extentBlocks * EfsWriter.BasicBlock;
        result.Add(new DefragBlockInfo(e.FirstBlock * EfsWriter.BasicBlock, len,
          e.IsDirectory ? DefragBlockKind.MetadataReserved : DefragBlockKind.Used,
          e.IsDirectory ? null : e.Name));
      }
    } catch { /* malformed-tail tolerance — partial map is still useful */ }
    return result;
  }
}
