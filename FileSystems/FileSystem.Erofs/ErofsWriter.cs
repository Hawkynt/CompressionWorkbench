#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Erofs;

/// <summary>
/// Builds a valid, uncompressed EROFS image from a set of files and their (possibly
/// nested) paths, matching the on-disk encoding produced by <c>mkfs.erofs</c> for plain
/// data and accepted by <c>fsck.erofs</c>:
/// <list type="bullet">
///   <item>extended (64-byte) inodes (<c>erofs_inode_extended</c>) so size, uid/gid,
///   mtime and a full 32-bit link count are all expressible;</item>
///   <item>the FLAT_INLINE datalayout: any residual tail (object size modulo block
///   size) is stored inline immediately after the inode header, and only whole blocks
///   spill into the data region. Objects smaller than one block carry their entire body
///   inline with the block-address field set to the <c>0xFFFFFFFF</c> "no full block"
///   sentinel — exactly as <c>mkfs.erofs</c> encodes small files;</item>
///   <item>node ids (<c>nid</c>) as 32-byte granules measured from
///   <c>meta_blkaddr * blockSize</c>; inodes are packed with their inline tails and the
///   next inode is re-aligned to a 32-byte boundary.</item>
/// </list>
/// Directories are emitted as EROFS directory chunks: a contiguous array of 12-byte
/// <c>erofs_dirent</c> headers followed by the packed entry names, with the conventional
/// "." and ".." entries first. Directory bodies follow the same FLAT_INLINE rule.
/// </summary>
public sealed class ErofsWriter {
  private const int BlockSizeBits = 12;
  private const int BlockSize = 1 << BlockSizeBits;        // 4096
  private const int InodeGranule = 32;                     // nid unit
  private const int ExtendedInodeSize = 64;                // erofs_inode_extended
  private const int DirentSize = 12;                       // erofs_dirent
  private const uint NoFullBlock = 0xFFFFFFFFu;            // raw_blkaddr sentinel for inline-only

  private const ushort ModeDirectory = 0x41ED;            // S_IFDIR | 0755
  private const ushort ModeRegular = 0x81A4;              // S_IFREG | 0644

  private const byte FileTypeRegular = 1;
  private const byte FileTypeDirectory = 2;

  // i_format: bit0 = inode version (1 => extended/64-byte), bits1..3 = datalayout.
  // FLAT_PLAIN == 0 => (0 << 1) | 1 = 0x01; FLAT_INLINE == 2 => (2 << 1) | 1 = 0x05.
  private const ushort FormatExtendedFlatPlain = (0 << 1) | 1;
  private const ushort FormatExtendedFlatInline = (2 << 1) | 1;

  private const uint Uid = 0;
  private const uint Gid = 0;

  private abstract class Node {
    public string Name = "";
    public uint Nid;                 // 32-byte granule from meta base
    public Node? Parent;
    public byte[] Body = [];         // directory dirent block bytes, or file content
    public ushort Mode;
    public uint Nlink = 1;
    // Datalayout selection (matches mkfs.erofs):
    //   - non-zero partial tail (size % blockSize != 0) => FLAT_INLINE: the tail is
    //     stored inline after the inode header, full blocks (if any) live at FullBlockAddress.
    //   - exact block multiple (incl. empty) => FLAT_PLAIN: every block lives at
    //     FullBlockAddress, nothing inline.
    public bool UseInline => this.Body.Length % BlockSize != 0;
    public uint FullBlockAddress = NoFullBlock;
    public int FullBlockCount;
  }

  private sealed class DirectoryNode : Node {
    public readonly SortedDictionary<string, Node> Children = new(StringComparer.Ordinal);
  }

  private sealed class FileNode : Node;

  private readonly DirectoryNode _root = new() { Name = "", Mode = ModeDirectory };

  /// <summary>
  /// Registers a file at the given archive path. Path segments are split on '/' and the
  /// intermediate directories are created on demand so nested layouts round-trip with
  /// their full directory chain intact.
  /// </summary>
  public void AddFile(string path, byte[] content) {
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(content);

    var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (segments.Length == 0)
      throw new ArgumentException("Path resolves to no name segments.", nameof(path));

    var dir = this._root;
    for (var i = 0; i < segments.Length - 1; ++i) {
      var segment = segments[i];
      if (!dir.Children.TryGetValue(segment, out var existing)) {
        var created = new DirectoryNode { Name = segment, Parent = dir, Mode = ModeDirectory };
        dir.Children.Add(segment, created);
        dir = created;
      } else if (existing is DirectoryNode childDir) {
        dir = childDir;
      } else {
        throw new InvalidOperationException(
          $"Path segment '{segment}' is already a file but is used as a directory.");
      }
    }

    var fileName = segments[^1];
    if (dir.Children.ContainsKey(fileName))
      throw new InvalidOperationException($"Duplicate entry '{path}'.");
    dir.Children.Add(fileName, new FileNode { Name = fileName, Parent = dir, Mode = ModeRegular, Body = content });
  }

  /// <summary>Produces the complete EROFS image as a byte array.</summary>
  public byte[] Build() {
    using var ms = new MemoryStream();
    this.WriteTo(ms);
    return ms.ToArray();
  }

  /// <summary>Writes the complete EROFS image to <paramref name="output"/>.</summary>
  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);

    // 1. Collect directories (first) and files. Directory nlink = 2 + child-dir count
    //    ("." + ".." + one per subdirectory).
    var directories = new List<DirectoryNode>();
    var files = new List<FileNode>();
    Collect(this._root, directories, files);

    foreach (var dir in directories) {
      dir.Body = EncodeDirectory(dir);
      var subdirs = 0;
      foreach (var child in dir.Children.Values)
        if (child is DirectoryNode) subdirs++;
      dir.Nlink = (uint)(2 + subdirs);
    }

    var allNodes = new List<Node>(directories.Count + files.Count);
    allNodes.AddRange(directories);
    allNodes.AddRange(files);

    // 2. The meta region starts at block 1 (block 0 holds boot sector + superblock).
    //    Inodes (with inline tails) are packed sequentially from there; each next inode
    //    re-aligns to a 32-byte granule so its nid is an integer. We assign nids first
    //    (pass A) so directory dirents can reference final nids, then place whole-block
    //    data after the meta region (pass B).
    const uint metaBlkAddr = 1;
    var metaBase = (long)metaBlkAddr * BlockSize;

    // Determine each node's full-block count up front:
    //   FLAT_INLINE  => floor(size / block) full blocks, the partial tail is inline.
    //   FLAT_PLAIN   => ceil(size / block) full blocks (i.e. all of it), none inline.
    foreach (var node in allNodes)
      node.FullBlockCount = node.UseInline
        ? node.Body.Length / BlockSize
        : CeilDiv(node.Body.Length, BlockSize);

    // Pass A: assign nids by walking the packed meta layout.
    var cursor = metaBase;
    foreach (var node in allNodes) {
      cursor = PlaceInode(cursor, metaBase, InlineTail(node));
      var off = cursor - metaBase;
      node.Nid = (uint)(off / InodeGranule);
      cursor += ExtendedInodeSize + InlineTail(node);
    }

    // Re-encode directory bodies now that all child nids are final (the encoded length
    // is independent of nid values, so the layout above stays valid).
    foreach (var dir in directories)
      dir.Body = EncodeDirectory(dir);

    // The meta region ends after the last inode+tail; whole-block data begins at the
    // next block boundary.
    var metaEnd = cursor;
    var dataStartBlock = (uint)CeilDiv(metaEnd, BlockSize);
    var nextDataBlock = dataStartBlock;

    // Pass B: assign whole-block addresses.
    foreach (var node in allNodes) {
      if (node.FullBlockCount > 0) {
        node.FullBlockAddress = nextDataBlock;
        nextDataBlock += (uint)node.FullBlockCount;
      } else {
        node.FullBlockAddress = NoFullBlock;
      }
    }

    var totalBlocks = Math.Max(nextDataBlock, dataStartBlock);
    var image = new byte[(long)totalBlocks * BlockSize];

    // 3. Superblock at offset 1024.
    WriteSuperblock(image, rootNid: this._root.Nid, inodeCount: allNodes.Count,
      totalBlocks: totalBlocks, metaBlkAddr: metaBlkAddr);

    // 4. Inodes + inline tails. Placement must reproduce pass A exactly so that each
    //    inode lands at the byte its nid encodes.
    cursor = metaBase;
    foreach (var node in allNodes) {
      cursor = PlaceInode(cursor, metaBase, InlineTail(node));
      WriteInode(image, cursor, node);
      var tail = InlineTail(node);
      if (tail > 0) {
        var fullBytes = node.FullBlockCount * BlockSize;
        Buffer.BlockCopy(node.Body, fullBytes, image, (int)(cursor + ExtendedInodeSize), tail);
      }
      cursor += ExtendedInodeSize + tail;
    }

    // 5. Whole-block data.
    foreach (var node in allNodes) {
      if (node.FullBlockCount == 0) continue;
      var fullBytes = node.FullBlockCount * BlockSize;
      Buffer.BlockCopy(node.Body, 0, image, (int)((long)node.FullBlockAddress * BlockSize), fullBytes);
    }

    output.Write(image, 0, image.Length);
  }

  private static void Collect(DirectoryNode dir, List<DirectoryNode> directories, List<FileNode> files) {
    directories.Add(dir);
    foreach (var child in dir.Children.Values) {
      switch (child) {
        case DirectoryNode sub: Collect(sub, directories, files); break;
        case FileNode file: files.Add(file); break;
      }
    }
  }

  // erofs_dirent: nid (u64) @0, nameoff (u16) @8, file_type (u8) @10, reserved @11.
  // Names follow the header array; "." and ".." come first.
  private static byte[] EncodeDirectory(DirectoryNode dir) {
    var entries = new List<(string Name, uint Nid, byte Type)> {
      (".", dir.Nid, FileTypeDirectory),
      ("..", (dir.Parent ?? dir).Nid, FileTypeDirectory),
    };
    foreach (var child in dir.Children.Values)
      entries.Add((child.Name, child.Nid, child is DirectoryNode ? FileTypeDirectory : FileTypeRegular));

    var headerBytes = entries.Count * DirentSize;
    var encodedNames = new List<byte[]>(entries.Count);
    var bodyLength = headerBytes;
    foreach (var (name, _, _) in entries) {
      var encoded = Encoding.UTF8.GetBytes(name);
      encodedNames.Add(encoded);
      bodyLength += encoded.Length;
    }

    // A single directory block holds at most BlockSize bytes of dirents+names; spanning
    // multiple blocks would require per-block header restarts. Typical archive contents
    // fit comfortably; reject the rare overflow explicitly rather than emit a broken dir.
    if (bodyLength > BlockSize)
      throw new InvalidOperationException(
        $"Directory '{dir.Name}' is too large for a single {BlockSize}-byte directory block.");

    var body = new byte[bodyLength];
    var nameCursor = headerBytes;
    for (var i = 0; i < entries.Count; ++i) {
      var (_, entryNid, type) = entries[i];
      var headerOffset = i * DirentSize;
      BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(headerOffset), entryNid);
      BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(headerOffset + 8), (ushort)nameCursor);
      body[headerOffset + 10] = type;
      encodedNames[i].CopyTo(body.AsSpan(nameCursor));
      nameCursor += encodedNames[i].Length;
    }
    return body;
  }

  private static void WriteSuperblock(byte[] image, uint rootNid, int inodeCount, uint totalBlocks, uint metaBlkAddr) {
    var sb = image.AsSpan(1024);
    BinaryPrimitives.WriteUInt32LittleEndian(sb, ErofsReader.Magic);            // magic @0
    // checksum @4 left 0 — sb_csum feature bit is NOT advertised, so fsck skips it.
    // feature_compat @8 left 0 — no optional compat features.
    sb[12] = BlockSizeBits;                                                     // blkszbits @12
    sb[13] = 0;                                                                 // sb_extslots @13
    BinaryPrimitives.WriteUInt16LittleEndian(sb[14..], (ushort)rootNid);        // root_nid @14
    BinaryPrimitives.WriteUInt64LittleEndian(sb[16..], (ulong)inodeCount);      // inos @16
    BinaryPrimitives.WriteUInt64LittleEndian(sb[24..], 0);                      // build_time @24
    BinaryPrimitives.WriteUInt32LittleEndian(sb[32..], 0);                      // build_time_nsec @32
    BinaryPrimitives.WriteUInt32LittleEndian(sb[36..], totalBlocks);            // blocks @36
    BinaryPrimitives.WriteUInt32LittleEndian(sb[40..], metaBlkAddr);            // meta_blkaddr @40
    BinaryPrimitives.WriteUInt32LittleEndian(sb[44..], 0);                      // xattr_blkaddr @44
    // uuid @48[16] — a fixed, deterministic non-zero UUID.
    for (var i = 0; i < 16; ++i) sb[48 + i] = (byte)(0x10 + i);
    // volume_name @64[16] left zero.
    // feature_incompat @80 left 0.
  }

  private static void WriteInode(byte[] image, long offset, Node node) {
    var inode = image.AsSpan((int)offset);
    var format = node.UseInline ? FormatExtendedFlatInline : FormatExtendedFlatPlain;
    BinaryPrimitives.WriteUInt16LittleEndian(inode, format);                    // i_format @0
    BinaryPrimitives.WriteUInt16LittleEndian(inode[2..], 0);                    // i_xattr_icount @2
    BinaryPrimitives.WriteUInt16LittleEndian(inode[4..], node.Mode);            // i_mode @4
    BinaryPrimitives.WriteUInt16LittleEndian(inode[6..], 0);                    // i_reserved @6
    BinaryPrimitives.WriteInt64LittleEndian(inode[8..], node.Body.Length);      // i_size @8
    BinaryPrimitives.WriteUInt32LittleEndian(inode[16..], node.FullBlockAddress); // i_u (raw_blkaddr) @16
    BinaryPrimitives.WriteUInt32LittleEndian(inode[20..], node.Nid);            // i_ino @20 (informational)
    BinaryPrimitives.WriteUInt32LittleEndian(inode[24..], Uid);                 // i_uid @24
    BinaryPrimitives.WriteUInt32LittleEndian(inode[28..], Gid);                 // i_gid @28
    BinaryPrimitives.WriteInt64LittleEndian(inode[32..], 0);                    // i_mtime @32
    BinaryPrimitives.WriteUInt32LittleEndian(inode[40..], 0);                   // i_mtime_nsec @40
    BinaryPrimitives.WriteUInt32LittleEndian(inode[44..], node.Nlink);          // i_nlink @44
    // i_reserved2 @48[16] left zero.
  }

  // Determines the meta-region byte at which an inode (header + inline tail) is placed.
  // The candidate is the next 32-byte granule; but FLAT_INLINE requires the inline tail
  // to live entirely within one block — the inode header plus tail must not straddle a
  // block boundary (fsck.erofs rejects "inline data cross block boundary"). When it
  // would, the inode is pushed to the start of the next block (still 32-byte aligned).
  private static long PlaceInode(long cursor, long metaBase, int inlineTail) {
    var at = Align(cursor, InodeGranule);
    var span = ExtendedInodeSize + inlineTail;
    var rel = at - metaBase;
    var offsetInBlock = rel % BlockSize;
    if (offsetInBlock + span > BlockSize) {
      // Advance to the next block boundary relative to the meta base.
      var nextBlock = (rel / BlockSize + 1) * BlockSize;
      at = metaBase + nextBlock;
    }
    return at;
  }

  // Bytes stored inline after the inode header: the partial tail for FLAT_INLINE, else 0.
  private static int InlineTail(Node node) => node.UseInline ? node.Body.Length % BlockSize : 0;

  private static long Align(long value, int alignment) => (value + alignment - 1) / alignment * alignment;
  private static int CeilDiv(long value, int divisor) => (int)((value + divisor - 1) / divisor);
}
