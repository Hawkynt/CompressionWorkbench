#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.UnixV6;

/// <summary>
/// A file (<see cref="Data"/> set) or directory (<see cref="Children"/> set) to write into a V6 volume.
/// </summary>
/// <param name="Name">Entry name, at most 14 Latin-1 bytes; ignored for the root.</param>
/// <param name="Data">File contents, at most 16 MiB - 1 (the 24-bit <c>i_size</c>).</param>
/// <param name="Children">Directory contents.</param>
public sealed record UnixV6WriteNode(string Name, byte[]? Data = null, IReadOnlyList<UnixV6WriteNode>? Children = null) {
  /// <summary>Permission bits (<c>0777</c> and the set-id/sticky bits); defaults to 0644 / 0755.</summary>
  public int? Permissions { get; init; }

  /// <summary>Access and modification time, seconds since 1970 (a PDP-11 long).</summary>
  public uint Time { get; init; }

  public bool IsDirectory => this.Children != null;
}

/// <summary>
/// Writes Sixth Edition UNIX volumes the V6 kernel mounts and V6 <c>icheck</c>/<c>dcheck</c> pass:
/// boot block 0, the superblock in block 1, the i-list from block 2, then the data blocks.
/// <para>
/// The root is inode 1 and each directory's entries take consecutive numbers; every directory starts with
/// <c>.</c> and <c>..</c> and has two links plus one per subdirectory. Data is laid out in
/// inode order, each large file's indirect blocks ahead of the data they map. A file of up to
/// eight blocks is small (direct addresses); a longer one is <c>ILARG</c> with
/// <c>i_addr[0..6]</c> single-indirect and <c>i_addr[7]</c> double-indirect, the mapping of the
/// kernel's <c>bmap</c>.
/// </para>
/// <para>
/// The free blocks are chained as the kernel's <c>alloc</c> consumes them: <c>s_free[0]</c> of
/// the last link is 0, and each full set of 100 is stored in the block named by the next
/// set's <c>s_free[0]</c> (count word, then the 100 numbers). Blocks are pushed from the top
/// of the volume down, so allocation proceeds upwards. <c>s_ninode</c> is left 0, which makes
/// the kernel's <c>ialloc</c> scan the i-list for free inodes.
/// </para>
/// <para>References: V6 <c>/usr/sys/filsys.h</c>, <c>inode.h</c>, <c>ken/alloc.c</c>,
/// <c>ken/subr.c</c> (TUHS); Lions, <i>A Commentary on the Sixth Edition UNIX Operating System</i>.</para>
/// </summary>
public static class UnixV6Writer {
  private const int BlockSize = UnixV6Reader.BlockSize;
  private const int InodesPerBlock = BlockSize / UnixV6Reader.InodeSize;
  private const int PointersPerBlock = BlockSize / 2;
  private const int FreeListLength = 100;
  private const int MaxFileSize = 0xFFFFFF;
  private const int MaxVolumeBlocks = 0xFFFF;
  private const int IAlloc = 0x8000;
  private const int IFDir = 0x4000;
  private const int ILarg = 0x1000;

  /// <summary>Free blocks added on top of the used ones when no volume size is given.</summary>
  public const int DefaultSpareBlocks = 64;

  /// <summary>Free inodes added on top of the used ones (rounded up to whole i-list blocks).</summary>
  public const int DefaultSpareInodes = 16;

  /// <summary>Blocks on an RK05 cartridge, the V6 distribution's own medium.</summary>
  public const int Rk05Blocks = 4872;

  /// <summary>Writes <paramref name="root"/> as a complete volume.</summary>
  /// <param name="output">Receives the image, <c>s_fsize</c> blocks long.</param>
  /// <param name="root">The root directory.</param>
  /// <param name="volumeBlocks">Volume size; 0 picks the used blocks plus <see cref="DefaultSpareBlocks"/>.</param>
  /// <param name="time">Superblock time, seconds since 1970.</param>
  public static void Write(Stream output, UnixV6WriteNode root, int volumeBlocks = 0, uint time = 0) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(root);
    if (!root.IsDirectory) throw new ArgumentException("The root must be a directory.", nameof(root));
    ArgumentOutOfRangeException.ThrowIfNegative(volumeBlocks);

    // Number the inodes: root 1, then each directory's entries together, subdirectories expanded in order.
    var nodes = new List<(UnixV6WriteNode Node, int Parent)> { (root, 1) };
    Number(root, 1, nodes, 0);
    var inodeCount = nodes.Count + DefaultSpareInodes;
    var isize = (inodeCount + InodesPerBlock - 1) / InodesPerBlock;
    if (isize * InodesPerBlock > 0xFFFF) throw new InvalidOperationException("UNIX V6: more files than 16-bit inode numbers address.");

    // Contents and block maps, in inode order.
    var contents = new byte[nodes.Count][];
    var entries = new List<int>[nodes.Count];
    for (var j = 1; j < nodes.Count; ++j) (entries[nodes[j].Parent - 1] ??= []).Add(j);
    for (var i = 0; i < nodes.Count; ++i) contents[i] = nodes[i].Node.IsDirectory ? Directory(nodes, i, entries[i]) : nodes[i].Node.Data ?? [];

    var next = isize + 2;
    var maps = new List<(int Block, byte[] Data)>[nodes.Count];
    var addrs = new int[nodes.Count][];
    for (var i = 0; i < nodes.Count; ++i)
      (addrs[i], maps[i]) = Lay(contents[i], ref next);
    var used = next;
    var fsize = volumeBlocks == 0 ? used + DefaultSpareBlocks : volumeBlocks;
    if (fsize < used) throw new ArgumentOutOfRangeException(nameof(volumeBlocks), $"UNIX V6: the contents need {used} blocks.");
    if (fsize > MaxVolumeBlocks) throw new InvalidOperationException($"UNIX V6: {fsize} blocks exceed the 16-bit s_fsize.");

    var image = new byte[(long)fsize * BlockSize];

    // i-list
    for (var i = 0; i < nodes.Count; ++i) {
      var node = nodes[i].Node;
      var at = 2 * BlockSize + i * UnixV6Reader.InodeSize;
      var inode = image.AsSpan(at, UnixV6Reader.InodeSize);
      var size = contents[i].Length;
      var large = (size + BlockSize - 1) / BlockSize > 8;
      var mode = IAlloc | (node.IsDirectory ? IFDir : 0) | (large ? ILarg : 0) | (node.Permissions ?? (node.IsDirectory ? 0x1ED : 0x1A4)) & 0xFFF;
      BinaryPrimitives.WriteUInt16LittleEndian(inode, (ushort)mode);
      inode[2] = (byte)(node.IsDirectory ? 2 + node.Children!.Count(c => c.IsDirectory) : 1);
      inode[5] = (byte)(size >> 16);
      BinaryPrimitives.WriteUInt16LittleEndian(inode[6..], (ushort)size);
      for (var a = 0; a < 8; ++a) BinaryPrimitives.WriteUInt16LittleEndian(inode[(8 + a * 2)..], (ushort)addrs[i][a]);
      Long(inode[24..], node.Time);
      Long(inode[28..], node.Time);
      foreach (var (block, data) in maps[i]) data.CopyTo(image.AsSpan(block * BlockSize));
    }

    // Superblock and free chain: s_free[0] = 0 ends the chain, blocks pushed top-down.
    var sb = image.AsSpan(BlockSize, BlockSize);
    BinaryPrimitives.WriteUInt16LittleEndian(sb, (ushort)isize);
    BinaryPrimitives.WriteUInt16LittleEndian(sb[2..], (ushort)fsize);
    var free = new int[FreeListLength];
    var nfree = 0;
    free[nfree++] = 0;
    for (var b = fsize - 1; b >= used; --b) {
      if (nfree == FreeListLength) {
        var link = image.AsSpan(b * BlockSize, BlockSize);
        BinaryPrimitives.WriteUInt16LittleEndian(link, (ushort)nfree);
        for (var k = 0; k < FreeListLength; ++k) BinaryPrimitives.WriteUInt16LittleEndian(link[(2 + k * 2)..], (ushort)free[k]);
        nfree = 0;
      }
      free[nfree++] = b;
    }
    BinaryPrimitives.WriteUInt16LittleEndian(sb[4..], (ushort)nfree);
    for (var k = 0; k < nfree; ++k) BinaryPrimitives.WriteUInt16LittleEndian(sb[(6 + k * 2)..], (ushort)free[k]);
    Long(sb[412..], time);

    output.Write(image);
  }

  private static void Number(UnixV6WriteNode dir, int self, List<(UnixV6WriteNode Node, int Parent)> nodes, int depth) {
    if (depth > 128) throw new InvalidOperationException("UNIX V6: directories nest too deeply.");
    var seen = new HashSet<string>(StringComparer.Ordinal);
    foreach (var child in dir.Children!) {
      Check(child, seen);
      nodes.Add((child, self));
    }
    var first = nodes.Count - dir.Children!.Count;
    for (var i = 0; i < dir.Children!.Count; ++i)
      if (dir.Children[i].IsDirectory) {
        Number(dir.Children[i], first + i + 1, nodes, depth + 1);
      }
  }

  private static void Check(UnixV6WriteNode node, HashSet<string> seen) {
    ArgumentNullException.ThrowIfNull(node);
    var bytes = Encoding.Latin1.GetByteCount(node.Name);
    if (bytes is 0 or > UnixV6Reader.NameLength || node.Name is "." or ".." || node.Name.Contains('/') || node.Name.Contains('\0')
        || Encoding.Latin1.GetString(Encoding.Latin1.GetBytes(node.Name)) != node.Name)
      throw new ArgumentException($"UNIX V6: '{node.Name}' is not a name of 1 to 14 Latin-1 characters.", nameof(node));
    if (!seen.Add(node.Name)) throw new ArgumentException($"UNIX V6: '{node.Name}' appears twice in one directory.", nameof(node));
    if (node.IsDirectory == (node.Data != null)) throw new ArgumentException($"UNIX V6: '{node.Name}' must be either a file or a directory.", nameof(node));
    if (node.Data is { Length: > MaxFileSize }) throw new ArgumentException($"UNIX V6: '{node.Name}' exceeds the 24-bit file size.", nameof(node));
  }

  private static byte[] Directory(List<(UnixV6WriteNode Node, int Parent)> nodes, int index, List<int>? entries) {
    var children = new List<(int Inode, string Name)> { (index + 1, "."), (nodes[index].Parent, "..") };
    foreach (var j in entries ?? []) children.Add((j + 1, nodes[j].Node.Name));
    var data = new byte[children.Count * UnixV6Reader.EntrySize];
    for (var k = 0; k < children.Count; ++k) {
      BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(k * UnixV6Reader.EntrySize), (ushort)children[k].Inode);
      Encoding.Latin1.GetBytes(children[k].Name, data.AsSpan(k * UnixV6Reader.EntrySize + 2));
    }
    return data;
  }

  /// <summary>Places one inode's data (and indirect blocks) from block <paramref name="next"/> on.</summary>
  private static (int[] Addr, List<(int Block, byte[] Data)> Blocks) Lay(byte[] data, ref int next) {
    var count = (data.Length + BlockSize - 1) / BlockSize;
    var addr = new int[8];
    var blocks = new List<(int, byte[])>();
    if (count <= 8) {
      for (var b = 0; b < count; ++b) {
        addr[b] = next++;
        blocks.Add((addr[b], Slice(data, b)));
      }
      return (addr, blocks);
    }

    // Large: single-indirect blocks for logical blocks 0..1791, then a double-indirect block.
    var singles = Math.Min(7, (count + PointersPerBlock - 1) / PointersPerBlock);
    var indirect = new byte[(count + PointersPerBlock - 1) / PointersPerBlock][];
    var indirectBlock = new int[indirect.Length];
    for (var i = 0; i < singles; ++i) {
      indirectBlock[i] = addr[i] = next++;
      indirect[i] = new byte[BlockSize];
    }
    byte[]? dbl = null;
    if (indirect.Length > 7) {
      addr[7] = next++;
      dbl = new byte[BlockSize];
      for (var i = 7; i < indirect.Length; ++i) {
        indirectBlock[i] = next++;
        indirect[i] = new byte[BlockSize];
        BinaryPrimitives.WriteUInt16LittleEndian(dbl.AsSpan((i - 7) * 2), (ushort)indirectBlock[i]);
      }
    }
    for (var b = 0; b < count; ++b) {
      var at = next++;
      BinaryPrimitives.WriteUInt16LittleEndian(indirect[b / PointersPerBlock].AsSpan(b % PointersPerBlock * 2), (ushort)at);
      blocks.Add((at, Slice(data, b)));
    }
    for (var i = 0; i < indirect.Length; ++i) blocks.Add((indirectBlock[i], indirect[i]));
    if (dbl != null) blocks.Add((addr[7], dbl));
    if (next > MaxVolumeBlocks) throw new InvalidOperationException("UNIX V6: the contents exceed the 16-bit block numbers.");
    return (addr, blocks);
  }

  private static byte[] Slice(byte[] data, int block) => data.AsSpan(block * BlockSize, Math.Min(BlockSize, data.Length - block * BlockSize)).ToArray();

  /// <summary>A PDP-11 long: high word first, each word little-endian.</summary>
  private static void Long(Span<byte> target, uint value) {
    BinaryPrimitives.WriteUInt16LittleEndian(target, (ushort)(value >> 16));
    BinaryPrimitives.WriteUInt16LittleEndian(target[2..], (ushort)value);
  }
}
