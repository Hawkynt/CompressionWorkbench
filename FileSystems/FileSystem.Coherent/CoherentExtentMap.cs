#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.Coherent;

/// <summary>
/// Describes where a Coherent volume keeps its bytes: the superblock, the inode
/// table, each file's data blocks, and the indirect blocks that name them.
/// </summary>
/// <remarks>
/// <para>A file's blocks are named one at a time — ten of them in the inode
/// itself, the rest through one, two or three levels of indirect block, each
/// pointer three bytes in the byte order a PDP-11 wrote. So a block can be
/// moved and the pointer that named it rewritten.</para>
///
/// <para>Nothing described this volume before, which is why wiping one zeroed
/// live bytes: a map that claims nothing reads as a volume that is entirely
/// free.</para>
/// </remarks>
public static class CoherentExtentMap {

  public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    var layout = CoherentLayout.Read(image);
    if (layout == null) yield break;

    yield return new DefragBlockInfo(0, layout.InodeTableOffset,
      DefragBlockKind.MetadataReserved, "Coherent superblock");
    yield return new DefragBlockInfo(layout.InodeTableOffset,
      layout.FirstDataOffset - layout.InodeTableOffset,
      DefragBlockKind.MetadataReserved, "Coherent inode table");

    // Blocks of one file that sit next to each other are one run. Describing
    // them one at a time makes a file look like a dozen owners of the same
    // name, and a layout planned from that has two of them landing on the same
    // bytes.
    var ordered = layout.Pointers.OrderBy(p => p.Block).ToList();
    var index = 0;
    while (index < ordered.Count) {
      var first = ordered[index];
      var last = index;
      while (last + 1 < ordered.Count
             && ordered[last + 1].Block == ordered[last].Block + 1
             && ordered[last + 1].IsIndirect == first.IsIndirect
             && string.Equals(ordered[last + 1].Owner, first.Owner, StringComparison.Ordinal))
        ++last;

      var at = (long)first.Block * layout.BlockSize;
      var length = (long)(last - index + 1) * layout.BlockSize;
      index = last + 1;
      if (at < 0 || at + length > image.Length) continue;

      yield return new DefragBlockInfo(at, length,
        first.IsIndirect ? DefragBlockKind.MetadataReserved : DefragBlockKind.Used,
        first.IsIndirect ? "Coherent indirect block" : first.Owner);
    }
  }
}
