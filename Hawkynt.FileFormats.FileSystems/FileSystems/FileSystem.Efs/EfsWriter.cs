#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;
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
  /// <summary>
  /// The superblock is at block 1. Block 0 is the SGI volume header, which a
  /// driver reads first and which — being all zeroes here — it correctly takes
  /// as "no partition table, look at the next block".
  /// </summary>
  internal const int SuperblockBlock = 1;
  internal const int SuperblockOffset = SuperblockBlock * BasicBlock;
  internal const int InodeTableOffset = 2;              // the cylinder group starts here
  internal const int MaxInodes = 256;                   // hard upper bound, plenty for round-trip

  // efs_dinode mode bits (matching IRIX/sys/fs/efs_fs.h)
  private const ushort ModeDir = 0x4000 | 0x1ED;  // 0o755
  private const ushort ModeFile = 0x8000 | 0x1A4; // 0o644

  private readonly List<(string Path, FilePayload Payload)> _files = [];
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
    _files.Add((n, FilePayload.FromBytes(data)));
  }

  /// <summary>
  /// Adds a file whose bytes are produced on demand. <paramref name="size" /> must
  /// match what <paramref name="openStream" /> yields; the layout is settled from
  /// it before a byte is read, so a payload past what a byte[] can hold is placed
  /// like any other.
  /// </summary>
  public void AddStreamingFile(string name, long size, Func<Stream> openStream) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(openStream);
    var n = name.Replace('\\', '/').Trim('/');
    if (n.Length == 0) return;
    _files.Add((n, FilePayload.FromStream(size, openStream)));
  }

  /// <summary>Builds the image and returns it as a byte array.</summary>
  /// <summary>Materialises the whole volume.</summary>
  public byte[] Build() {
    var image = this.BuildCore(out var payloads);
    return payloads.Materialise(image);
  }

  /// <summary>
  /// Writes the volume into <paramref name="output" />: the blocks the filesystem
  /// populates, then each file's bytes at the offset it was allocated. Only a
  /// non-seekable target has to materialise the volume, so a seekable one is
  /// bounded by the disk rather than by what a byte[] can address.
  /// </summary>
  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);
    if (!output.CanSeek) {
      var full = this.Build();
      output.Write(full, 0, full.Length);
      return;
    }

    var basePosition = output.Position;
    var image = this.BuildCore(out var payloads);
    image.WriteTo(output);
    payloads.FlushTo(output, basePosition);
    output.Position = basePosition + image.TotalBytes;
    output.Flush();
  }

  private SparseBlockImage BuildCore(out DeferredPayloads payloads) {
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
    // Inode numbers start at 2 — 0 and 1 are reserved and still occupy their
    // slots, because a driver finds inode n at block table + n/4.
    var inodeBlocks = (tree.Nodes.Count + 2 + InodesPerBlock - 1) / InodesPerBlock;
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
        blocks = node.Payload.Size == 0 ? 0 : (int)((node.Payload.Size + BasicBlock - 1) / BasicBlock);
      }
      firstBlockPerNode[i] = blocks > 0 ? nextDataBlock : 0;
      blocksPerNode[i] = blocks;
      nextDataBlock += blocks;
    }
    var totalBlocks = nextDataBlock;
    var imageSize = totalBlocks * BasicBlock;
    // Only the blocks the filesystem populates are held: file payloads are
    // placed by seek afterwards, so a volume past what a byte[] can address
    // costs its metadata rather than its size.
    var image = new SparseBlockImage(BasicBlock, (long)totalBlocks * BasicBlock);
    payloads = new DeferredPayloads();

    // ── Superblock @ 0 (efs_fs.h layout, big-endian) ─────────────────────────
    var sb = image.At(SuperblockOffset, BasicBlock);
    BinaryPrimitives.WriteInt32BigEndian(sb[0x00..], totalBlocks);     // s_size
    // The cylinder group starts at the inode table, and a driver locates every
    // inode from there. Pointing this at the first data block instead put the
    // whole inode table out of reach.
    BinaryPrimitives.WriteInt32BigEndian(sb[0x04..], InodeTableOffset);         // fs_firstcg
    BinaryPrimitives.WriteInt32BigEndian(sb[0x08..], totalBlocks - InodeTableOffset); // fs_cgfsize
    BinaryPrimitives.WriteInt16BigEndian(sb[0x0C..], (short)inodeBlocks);      // s_cgisize
    BinaryPrimitives.WriteInt16BigEndian(sb[0x0E..], (short)32);       // s_sectors (placeholder)
    BinaryPrimitives.WriteInt16BigEndian(sb[0x10..], (short)2);        // s_heads (placeholder)
    BinaryPrimitives.WriteInt16BigEndian(sb[0x12..], (short)1);        // s_ncg (single cylinder group)
    BinaryPrimitives.WriteInt16BigEndian(sb[0x14..], (short)0);        // s_dirty = 0 (clean)
    BinaryPrimitives.WriteUInt32BigEndian(sb[0x18..], 0);              // fs_time
    BinaryPrimitives.WriteUInt32BigEndian(sb[0x1C..], EfsSuperblock.EfsMagic);
    var labelBytes = Encoding.ASCII.GetBytes(_volumeLabel);
    Array.Resize(ref labelBytes, 6);
    labelBytes.CopyTo(sb[0x20..]);                                     // fs_fname

    // ── Inode table @ sector 1 ───────────────────────────────────────────────
    for (var i = 0; i < tree.Nodes.Count; i++) {
      var node = tree.Nodes[i];
      var ino = i + 2;                            // 0 and 1 reserved, 2 = root
      var blockOff = ino / InodesPerBlock;        // which 512 B inode block
      var slotOff = ino % InodesPerBlock;
      var ip = image.At(((long)InodeTableOffset + blockOff) * BasicBlock + slotOff * InodeSize, InodeSize);

      // efs_dinode: di_mode(2), di_nlink(2), di_uid(2), di_gid(2), di_size(4),
      //             di_atime(4), di_mtime(4), di_ctime(4), di_gen(4),
      //             di_numextents(2), di_version(1), di_spare(1),
      //             di_u[12 extents × 8] = 96 bytes. Tail of 128 is padding.
      BinaryPrimitives.WriteUInt16BigEndian(ip[0..], node.IsDirectory ? ModeDir : ModeFile);
      BinaryPrimitives.WriteUInt16BigEndian(ip[2..], (ushort)(node.IsDirectory ? 2 : 1));
      BinaryPrimitives.WriteUInt16BigEndian(ip[4..], 0);
      BinaryPrimitives.WriteUInt16BigEndian(ip[6..], 0);
      // A directory's size counts whole directory blocks. EFS stores its
      // entries in fixed 512-byte blocks and a driver refuses a size that is
      // not a multiple of one — "directory size not a multiple of
      // EFS_DIRBSIZE" — however well the entries inside are formed.
      var size = node.IsDirectory
        ? blocksPerNode[i] * BasicBlock
        : (int)Math.Min(node.Payload.Size, int.MaxValue);
      BinaryPrimitives.WriteInt32BigEndian(ip[8..], size);
      var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
      BinaryPrimitives.WriteInt32BigEndian(ip[12..], now);
      BinaryPrimitives.WriteInt32BigEndian(ip[16..], now);
      BinaryPrimitives.WriteInt32BigEndian(ip[20..], now);
      BinaryPrimitives.WriteInt32BigEndian(ip[24..], 0);
      ip[30] = 1;
      ip[31] = 0;

      // An extent is ex_magic(1) ex_bn(3) ex_length(1) ex_offset(3), and the
      // length is one byte — so a file of more than 255 blocks needs more than
      // one extent. Writing a single extent truncated the count to its low
      // byte, which gave a file of the right size whose bytes past the first
      // 255 blocks were whatever happened to follow.
      var extents = 0;
      var remaining = blocksPerNode[i];
      var block = firstBlockPerNode[i];
      var fileOffset = 0;
      while (remaining > 0 && extents < DirectExtents) {
        var run = Math.Min(remaining, MaxExtentBlocks);
        var ex = ip[(32 + extents * 8)..];
        ex[0] = 0;                          // magic is always zero
        ex[1] = (byte)(block >> 16);
        ex[2] = (byte)(block >> 8);
        ex[3] = (byte)block;
        ex[4] = (byte)run;
        ex[5] = (byte)(fileOffset >> 16);
        ex[6] = (byte)(fileOffset >> 8);
        ex[7] = (byte)fileOffset;           // where in the file, in blocks
        block += run;
        fileOffset += run;
        remaining -= run;
        ++extents;
      }

      if (remaining > 0)
        throw new InvalidOperationException(
          $"EFS writer: '{node.Name}' needs more than the {DirectExtents} extents an inode holds.");

      BinaryPrimitives.WriteInt16BigEndian(ip[28..], (short)extents);
    }

    // ── Directory bodies + file data ─────────────────────────────────────────
    for (var i = 0; i < tree.Nodes.Count; i++) {
      var node = tree.Nodes[i];
      if (blocksPerNode[i] == 0) continue;
      // Both operands are block indices in an int, and a volume of a few
      // gigabytes has more blocks than their product fits: the byte offset
      // came out negative and the payload was written before the file.
      var startByte = (long)firstBlockPerNode[i] * BasicBlock;
      if (node.IsDirectory) {
        // efs_dirblk: hdr (4 bytes magic + 1 byte slot count) followed by
        // (offset, ino, nlen, name…) entries. We use the simpler "flat"
        // packing: 2-byte BE inode + 1-byte nlen + name bytes per entry.
        // Reader stub (TryParse) doesn't yet walk this, but our own EfsReader
        // (added in this commit) does.
        var blk = image.At(startByte, BasicBlock);
        var entries = new List<(uint Inode, string Name)> {
          ((uint)(i + 2), "."),
          ((uint)node.ParentInode, ".."),
        };
        foreach (var (childName, childInode) in node.Children)
          entries.Add(((uint)childInode, childName));
        WriteDirectoryBlock(blk, entries);
      } else {
        payloads.Add(startByte, node.Payload);
      }
    }

    return image;
  }

  /// <summary>
  /// Writes one EFS directory block.
  /// </summary>
  /// <remarks>
  /// <para>The shape is not a packed list, which is what this wrote before and
  /// what nothing else reads. A block opens with the magic, the offset of the
  /// lowest byte in use and a slot count; then a byte per slot giving where
  /// that entry is; and the entries themselves packed against the far end of
  /// the block, growing downwards. Every offset is stored halved, so each one
  /// has to land on an even byte.</para>
  ///
  /// <para>An entry is a four-byte inode, a name length and the name, padded to
  /// an even length.</para>
  /// </remarks>
  private static void WriteDirectoryBlock(Span<byte> blk, List<(uint Inode, string Name)> entries) {
    BinaryPrimitives.WriteUInt16BigEndian(blk, EfsDirBlockMagic);

    var cursor = blk.Length;
    var slotAt = DirBlockHeaderSize;
    foreach (var (inode, name) in entries) {
      var nameBytes = Encoding.ASCII.GetBytes(name);
      if (nameBytes.Length > 255) Array.Resize(ref nameBytes, 255);

      var size = (DirEntryOverhead + nameBytes.Length + 1) & ~1;
      cursor -= size;
      if (cursor < slotAt + 1)
        throw new InvalidOperationException(
          "EFS writer: directory block overflow (single-block directories only).");

      BinaryPrimitives.WriteUInt32BigEndian(blk[cursor..], inode);
      blk[cursor + 4] = (byte)nameBytes.Length;
      nameBytes.CopyTo(blk[(cursor + 5)..]);

      blk[slotAt++] = (byte)(cursor >> 1);
    }

    blk[2] = (byte)(cursor >> 1);          // firstused, halved
    blk[3] = (byte)entries.Count;          // slots
  }

  internal const ushort EfsDirBlockMagic = 0xBEEF;

  /// <summary>Extents an inode holds before it needs an indirect one.</summary>
  private const int DirectExtents = 12;

  /// <summary>An extent's length field is a single byte.</summary>
  private const int MaxExtentBlocks = 255;
  private const int DirBlockHeaderSize = 4;

  /// <summary>An entry's fixed part: the inode and the name length.</summary>
  private const int DirEntryOverhead = 5;

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
    public FilePayload Payload;
    public readonly List<(string Name, int Inode)> Children = [];
  }

  internal sealed class TreeModel {
    public required List<TreeNode> Nodes;
  }

  private TreeModel BuildTree() {
    var root = new TreeNode { IsDirectory = true, Name = "", ParentInode = 2 };
    var nodes = new List<TreeNode> { root };
    var byPath = new Dictionary<string, TreeNode>(StringComparer.Ordinal) { [""] = root };

    foreach (var (path, payload) in _files) {
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
      var file = new TreeNode { IsDirectory = false, Name = segs[^1], ParentInode = parentInode, Payload = payload };
      nodes.Add(file);
      parent.Children.Add((segs[^1], nodes.Count + 1));
    }
    return new TreeModel { Nodes = nodes };
  }
}
