#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Efs;

/// <summary>
/// Minimal but spec-aware writer for an SGI EFS (Extent File System) image.
/// Produces a real <see cref="EfsSuperblock.EfsMagic"/>-tagged superblock at
/// sector 0, a packed inode table directly after the superblock, and the
/// directory + file data in subsequent 512-byte basic blocks.
///
/// <para><b>Scope.</b> The on-disk layout follows the IRIX <c>efs_fs.h</c>
/// header field positions (size in BB, first_cg, ncg, cg_isize, magic) so
/// existing <see cref="EfsSuperblock.TryParse"/> recognises the image. File
/// bodies are stored as a single direct extent each; directory entries use
/// the variable-length efs_dent format (inode + nlen + name) but always
/// land in a single directory block, so per-directory total payload is
/// capped at one BB minus the dir header. This matches the "flat-+-nested"
/// subset reiserfsprogs would generate for a freshly-created small disk.</para>
/// </summary>
public sealed class EfsWriter {
  internal const int BasicBlock = 512;                  // EFS basic block size (always 512)
  internal const int InodeSize = 128;                   // efs_dinode_t on disk
  internal const int InodesPerBlock = BasicBlock / InodeSize; // 4
  internal const int SuperblockOffset = 0;              // superblock is at sector 0
  internal const int InodeTableOffset = 1;              // inodes start at sector 1
  internal const int MaxInodes = 256;                   // hard upper bound, plenty for round-trip

  // efs_dinode mode bits (matching IRIX/sys/fs/efs_fs.h)
  private const ushort ModeDir = 0x4000 | 0x1ED;  // 0o755
  private const ushort ModeFile = 0x8000 | 0x1A4; // 0o644

  private readonly List<(string Path, byte[] Data)> _files = [];
  private string _volumeLabel = "WORM";

  /// <summary>Sets the 6-char volume label (truncated/padded).</summary>
  public void SetVolumeLabel(string? label) {
    if (!string.IsNullOrWhiteSpace(label)) _volumeLabel = label;
  }

  /// <summary>
  /// Adds a file by path. Path separators ('/' or '\\') create intermediate
  /// directories. The first call to <see cref="Build"/> serialises everything.
  /// </summary>
  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    var n = name.Replace('\\', '/').Trim('/');
    if (n.Length == 0) return;
    _files.Add((n, data));
  }

  /// <summary>Builds the image and returns it as a byte array.</summary>
  public byte[] Build() {
    // Tree model: root + intermediate dirs + files. Inode numbers allocated in
    // first-encounter order starting from 2 (1 = reserved, 2 = root by EFS convention).
    var tree = BuildTree();
    if (tree.Nodes.Count > MaxInodes)
      throw new InvalidOperationException(
        $"EFS writer caps at {MaxInodes} inodes; tree has {tree.Nodes.Count}.");

    // ── Layout ───────────────────────────────────────────────────────────────
    // Sector 0          : superblock
    // Sector 1          : inode table block 1 (4 inodes per 512 B)
    // Sector 1 + Iblocks: data blocks (one per directory body + one per file)
    var inodeBlocks = (tree.Nodes.Count + InodesPerBlock - 1) / InodesPerBlock;
    if (inodeBlocks == 0) inodeBlocks = 1;

    // Allocate data blocks: each directory gets 1 BB (capped payload); each
    // file gets ceil(size / 512) BBs (single extent).
    var dataStart = InodeTableOffset + inodeBlocks;
    var nextDataBlock = dataStart;
    var blocksPerNode = new int[tree.Nodes.Count];
    var firstBlockPerNode = new int[tree.Nodes.Count];
    for (var i = 0; i < tree.Nodes.Count; i++) {
      var node = tree.Nodes[i];
      int blocks;
      if (node.IsDirectory) {
        blocks = 1;                              // single-block directory
      } else {
        blocks = node.Data.Length == 0 ? 0 : (node.Data.Length + BasicBlock - 1) / BasicBlock;
      }
      firstBlockPerNode[i] = blocks > 0 ? nextDataBlock : 0;
      blocksPerNode[i] = blocks;
      nextDataBlock += blocks;
    }
    var totalBlocks = nextDataBlock;
    var imageSize = totalBlocks * BasicBlock;
    var image = new byte[imageSize];

    // ── Superblock @ 0 (efs_fs.h layout, big-endian) ─────────────────────────
    var sb = image.AsSpan(SuperblockOffset, BasicBlock);
    BinaryPrimitives.WriteInt32BigEndian(sb[0x00..], totalBlocks);     // s_size
    BinaryPrimitives.WriteInt32BigEndian(sb[0x04..], dataStart);       // s_firstcg = first data BB
    BinaryPrimitives.WriteInt32BigEndian(sb[0x08..], totalBlocks - dataStart); // s_cgfsize
    BinaryPrimitives.WriteInt16BigEndian(sb[0x0C..], (short)inodeBlocks);      // s_cgisize
    BinaryPrimitives.WriteInt16BigEndian(sb[0x0E..], (short)32);       // s_sectors (placeholder)
    BinaryPrimitives.WriteInt16BigEndian(sb[0x10..], (short)2);        // s_heads (placeholder)
    BinaryPrimitives.WriteInt16BigEndian(sb[0x12..], (short)1);        // s_ncg (single cylinder group)
    BinaryPrimitives.WriteInt16BigEndian(sb[0x14..], (short)0);        // s_dirty = 0 (clean)
    BinaryPrimitives.WriteUInt32BigEndian(sb[0x18..], EfsSuperblock.EfsMagic);
    // s_fname @ 0x1C is 6 bytes, but our parser keeps Time at 0x1C — leave 0
    // and write the label at the canonical fname offset.
    var labelBytes = Encoding.ASCII.GetBytes(_volumeLabel);
    Array.Resize(ref labelBytes, 6);
    labelBytes.CopyTo(sb[0x1C..]);

    // ── Inode table @ sector 1 ───────────────────────────────────────────────
    for (var i = 0; i < tree.Nodes.Count; i++) {
      var node = tree.Nodes[i];
      var ino = i + 2;                            // 1 reserved, 2 = root
      var blockOff = (ino - 2) / InodesPerBlock;  // which 512 B inode block
      var slotOff = (ino - 2) % InodesPerBlock;
      var ip = image.AsSpan((InodeTableOffset + blockOff) * BasicBlock + slotOff * InodeSize, InodeSize);

      // efs_dinode: di_mode(2), di_nlink(2), di_uid(2), di_gid(2), di_size(4),
      //             di_atime(4), di_mtime(4), di_ctime(4), di_gen(4),
      //             di_numextents(2), di_version(1), di_spare(1),
      //             di_u[12 extents × 8] = 96 bytes. Tail of 128 is padding.
      BinaryPrimitives.WriteUInt16BigEndian(ip[0..], node.IsDirectory ? ModeDir : ModeFile);
      BinaryPrimitives.WriteUInt16BigEndian(ip[2..], (ushort)(node.IsDirectory ? 2 : 1));
      BinaryPrimitives.WriteUInt16BigEndian(ip[4..], 0);
      BinaryPrimitives.WriteUInt16BigEndian(ip[6..], 0);
      var size = node.IsDirectory ? ComputeDirSize(node) : node.Data.Length;
      BinaryPrimitives.WriteInt32BigEndian(ip[8..], size);
      var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
      BinaryPrimitives.WriteInt32BigEndian(ip[12..], now);
      BinaryPrimitives.WriteInt32BigEndian(ip[16..], now);
      BinaryPrimitives.WriteInt32BigEndian(ip[20..], now);
      BinaryPrimitives.WriteInt32BigEndian(ip[24..], 0);
      BinaryPrimitives.WriteInt16BigEndian(ip[28..], (short)(blocksPerNode[i] > 0 ? 1 : 0));
      ip[30] = 1;
      ip[31] = 0;
      // Extent 0: ex_magic(1)=0 ex_bn(3) ex_length(1) ex_offset(3)
      if (blocksPerNode[i] > 0) {
        var ex = ip[32..];
        // bn is 24-bit big-endian
        ex[0] = 0; // magic always 0
        ex[1] = (byte)(firstBlockPerNode[i] >> 16);
        ex[2] = (byte)(firstBlockPerNode[i] >> 8);
        ex[3] = (byte)firstBlockPerNode[i];
        ex[4] = (byte)blocksPerNode[i]; // length in BB (max 255)
        // offset (3 bytes) within the inode logical file = 0
      }
    }

    // ── Directory bodies + file data ─────────────────────────────────────────
    for (var i = 0; i < tree.Nodes.Count; i++) {
      var node = tree.Nodes[i];
      if (blocksPerNode[i] == 0) continue;
      var startByte = firstBlockPerNode[i] * BasicBlock;
      if (node.IsDirectory) {
        // efs_dirblk: hdr (4 bytes magic + 1 byte slot count) followed by
        // (offset, ino, nlen, name…) entries. We use the simpler "flat"
        // packing: 2-byte BE inode + 1-byte nlen + name bytes per entry.
        // Reader stub (TryParse) doesn't yet walk this, but our own EfsReader
        // (added in this commit) does.
        var blk = image.AsSpan(startByte, BasicBlock);
        // magic = 0xBEEF (informal — EFS dirblks use a 16-bit hash slot mark
        // in real IRIX; we use a fixed marker so the reader can verify shape).
        BinaryPrimitives.WriteUInt16BigEndian(blk[0..], 0xBEEF);
        blk[2] = (byte)(node.Children.Count + 2); // includes "." and ".."
        var off = 3;
        WriteDirEntry(blk, ref off, (ushort)(i + 2), ".");
        WriteDirEntry(blk, ref off, (ushort)(node.ParentInode), "..");
        foreach (var (childName, childInode) in node.Children)
          WriteDirEntry(blk, ref off, (ushort)childInode, childName);
      } else {
        node.Data.CopyTo(image.AsSpan(startByte));
      }
    }

    return image;
  }

  /// <summary>Builds the image and writes it to <paramref name="output"/>.</summary>
  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);
    var img = Build();
    output.Write(img, 0, img.Length);
  }

  private static void WriteDirEntry(Span<byte> blk, ref int off, ushort inode, string name) {
    var nameBytes = Encoding.UTF8.GetBytes(name);
    if (nameBytes.Length > 255) Array.Resize(ref nameBytes, 255);
    var slot = 3 + nameBytes.Length;
    if (off + slot > blk.Length)
      throw new InvalidOperationException("EFS writer: directory block overflow (single-block dirs only).");
    BinaryPrimitives.WriteUInt16BigEndian(blk[off..], inode);
    blk[off + 2] = (byte)nameBytes.Length;
    nameBytes.CopyTo(blk[(off + 3)..]);
    off += slot;
  }

  private static int ComputeDirSize(TreeNode node) {
    var size = 3 + 1 + 3 + 2; // "." + ".." overhead
    foreach (var (n, _) in node.Children)
      size += 3 + Encoding.UTF8.GetByteCount(n);
    return size;
  }

  internal sealed class TreeNode {
    public required bool IsDirectory;
    public required string Name;
    public required int ParentInode;
    public byte[] Data = [];
    public readonly List<(string Name, int Inode)> Children = [];
  }

  internal sealed class TreeModel {
    public required List<TreeNode> Nodes;
  }

  private TreeModel BuildTree() {
    var root = new TreeNode { IsDirectory = true, Name = "", ParentInode = 2 };
    var nodes = new List<TreeNode> { root };
    var byPath = new Dictionary<string, TreeNode>(StringComparer.Ordinal) { [""] = root };

    foreach (var (path, data) in _files) {
      var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
      if (segs.Length == 0) continue;
      var parent = root;
      var parentInode = 2;
      var acc = "";
      for (var s = 0; s < segs.Length - 1; s++) {
        acc = acc.Length == 0 ? segs[s] : $"{acc}/{segs[s]}";
        if (!byPath.TryGetValue(acc, out var dir)) {
          dir = new TreeNode { IsDirectory = true, Name = segs[s], ParentInode = parentInode };
          nodes.Add(dir);
          var inode = nodes.Count + 1; // 1-based: root is at index 0 → inode 2
          parent.Children.Add((segs[s], inode));
          byPath[acc] = dir;
        }
        parentInode = nodes.IndexOf(dir) + 2;
        parent = dir;
      }
      var file = new TreeNode { IsDirectory = false, Name = segs[^1], ParentInode = parentInode, Data = data };
      nodes.Add(file);
      parent.Children.Add((segs[^1], nodes.Count + 1));
    }
    return new TreeModel { Nodes = nodes };
  }
}
