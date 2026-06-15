#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Erofs;

/// <summary>
/// Builds a minimal, valid EROFS image from a set of files and their (possibly nested)
/// paths. The output is a write-once image using the simplest on-disk encoding the
/// <see cref="ErofsReader"/> understands:
/// <list type="bullet">
///   <item>compact (32-byte) inodes packed contiguously from <c>meta_blkaddr</c>, so a
///   node's <c>nid</c> is its inode index;</item>
///   <item>the uncompressed FLAT_PLAIN data layout (layout 0): every directory and file
///   stores its bytes in whole, block-aligned data blocks addressed by the inode's
///   block-address field — no inline tails, no compressed clusters.</item>
/// </list>
/// Directories are emitted as EROFS directory blocks: a contiguous array of 12-byte
/// <c>erofs_dirent</c> headers followed by the packed entry names, with the conventional
/// "." and ".." entries first. Each directory is kept within a single block, which is
/// ample for typical archive contents.
/// </summary>
public sealed class ErofsWriter {
  // Block size: 4096 (blkszbits = 12), matching the reader's overwhelmingly common case.
  private const int BlockSizeBits = 12;
  private const int BlockSize = 1 << BlockSizeBits;
  private const int InodeSize = 32;          // compact inode (erofs_inode_compact)
  private const int DirentSize = 12;         // erofs_dirent
  private const int MetaBlockAddress = 1;    // inodes start in block 1 (block 0 holds the superblock)

  private const ushort ModeDirectory = 0x41ED; // S_IFDIR | 0755
  private const ushort ModeRegular = 0x81A4;    // S_IFREG | 0644

  private const byte FileTypeRegular = 1;
  private const byte FileTypeDirectory = 2;

  // Datalayout 0 = FLAT_PLAIN, stored in the inode format field as (layout << 1).
  private const ushort FormatFlatPlainCompact = 0 << 1;

  private abstract class Node {
    public string Name = "";
    public int Nid;                  // inode index == nid (inodes are 32 bytes each)
    public uint DataBlockAddress;    // first data block of this node
    public Node? Parent;
  }

  private sealed class DirectoryNode : Node {
    public readonly SortedDictionary<string, Node> Children =
      new(StringComparer.Ordinal);
  }

  private sealed class FileNode : Node {
    public byte[] Content = [];
  }

  private readonly DirectoryNode _root = new() { Name = "" };

  /// <summary>
  /// Registers a file at the given archive path. Path segments are split on '/', and the
  /// intermediate directories are created on demand so nested layouts round-trip with their
  /// full directory chain intact.
  /// </summary>
  public void AddFile(string path, byte[] content) {
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(content);

    var segments = path.Replace('\\', '/')
      .Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (segments.Length == 0)
      throw new ArgumentException("Path resolves to no name segments.", nameof(path));

    var dir = this._root;
    for (var i = 0; i < segments.Length - 1; ++i) {
      var segment = segments[i];
      if (!dir.Children.TryGetValue(segment, out var existing)) {
        var created = new DirectoryNode { Name = segment, Parent = dir };
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
    dir.Children.Add(fileName, new FileNode { Name = fileName, Content = content, Parent = dir });
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

    // 1. Enumerate all nodes (directories first, then files) and assign nids.
    //    Directories come first so the root directory keeps nid 0 (and the
    //    superblock's u16 root_nid stays trivially in range).
    var directories = new List<DirectoryNode>();
    var files = new List<FileNode>();
    Collect(this._root, directories, files);

    var nid = 0;
    foreach (var dir in directories)
      dir.Nid = nid++;
    foreach (var file in files)
      file.Nid = nid++;

    var inodeCount = directories.Count + files.Count;

    // 2. Lay out data blocks after the inode region. Inodes occupy whole blocks
    //    starting at MetaBlockAddress; data blocks follow.
    var inodeBytes = inodeCount * InodeSize;
    var inodeBlocks = CeilDiv(inodeBytes, BlockSize);
    var nextDataBlock = (uint)(MetaBlockAddress + inodeBlocks);

    // Each directory's encoded body lives in exactly one block.
    var directoryBodies = new Dictionary<DirectoryNode, byte[]>(directories.Count);
    foreach (var dir in directories) {
      var body = EncodeDirectory(dir);
      directoryBodies[dir] = body;
      dir.DataBlockAddress = nextDataBlock;
      nextDataBlock += 1; // single block per directory
    }

    // Files occupy ceil(size / block) blocks; empty files reserve none.
    foreach (var file in files) {
      var blocks = CeilDiv(file.Content.Length, BlockSize);
      file.DataBlockAddress = nextDataBlock;
      nextDataBlock += (uint)blocks;
    }

    var totalBlocks = nextDataBlock;
    var image = new byte[(long)totalBlocks * BlockSize];

    // 3. Superblock at offset 1024.
    WriteSuperblock(image, rootNid: this._root.Nid, inodeCount: inodeCount, totalBlocks: totalBlocks);

    // 4. Inodes.
    var metaBase = (long)MetaBlockAddress * BlockSize;
    foreach (var dir in directories)
      WriteInode(image, metaBase + (long)dir.Nid * InodeSize,
        ModeDirectory, directoryBodies[dir].Length, dir.DataBlockAddress);
    foreach (var file in files)
      WriteInode(image, metaBase + (long)file.Nid * InodeSize,
        ModeRegular, file.Content.Length, file.DataBlockAddress);

    // 5. Directory bodies.
    foreach (var dir in directories) {
      var body = directoryBodies[dir];
      Buffer.BlockCopy(body, 0, image, (int)((long)dir.DataBlockAddress * BlockSize), body.Length);
    }

    // 6. File data.
    foreach (var file in files) {
      if (file.Content.Length == 0) continue;
      Buffer.BlockCopy(file.Content, 0, image,
        (int)((long)file.DataBlockAddress * BlockSize), file.Content.Length);
    }

    output.Write(image, 0, image.Length);
  }

  private static void Collect(DirectoryNode dir, List<DirectoryNode> directories, List<FileNode> files) {
    directories.Add(dir);
    foreach (var child in dir.Children.Values) {
      switch (child) {
        case DirectoryNode sub:
          Collect(sub, directories, files);
          break;
        case FileNode file:
          files.Add(file);
          break;
      }
    }
  }

  // Directory block format read by ErofsReader.WalkDirBlock:
  //   [erofs_dirent[count]] [packed names], first name at offset count*DirentSize.
  //   dirent: nid (u64) @0, nameoff (u16) @8, file_type (u8) @10, reserved @11.
  // Names extend from this entry's nameoff to the next entry's nameoff (block end for
  // the last). "." and ".." come first (and are skipped by the reader).
  private static byte[] EncodeDirectory(DirectoryNode dir) {
    var entries = new List<(string Name, int Nid, byte Type)> {
      (".", dir.Nid, FileTypeDirectory),
      ("..", (dir.Parent ?? dir).Nid, FileTypeDirectory),
    };
    foreach (var child in dir.Children.Values)
      entries.Add((child.Name, child.Nid,
        child is DirectoryNode ? FileTypeDirectory : FileTypeRegular));

    var headerBytes = entries.Count * DirentSize;
    var nameBytes = new List<byte[]>(entries.Count);
    var bodyLength = headerBytes;
    foreach (var (name, _, _) in entries) {
      var encoded = Encoding.UTF8.GetBytes(name);
      nameBytes.Add(encoded);
      bodyLength += encoded.Length;
    }

    if (bodyLength > BlockSize)
      throw new InvalidOperationException(
        $"Directory '{dir.Name}' has too many entries to fit in a single {BlockSize}-byte block.");

    var body = new byte[bodyLength];
    var nameCursor = headerBytes;
    for (var i = 0; i < entries.Count; ++i) {
      var (_, entryNid, type) = entries[i];
      var headerOffset = i * DirentSize;
      BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(headerOffset), (ulong)entryNid);
      BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(headerOffset + 8), (ushort)nameCursor);
      body[headerOffset + 10] = type;
      var encoded = nameBytes[i];
      encoded.CopyTo(body.AsSpan(nameCursor));
      nameCursor += encoded.Length;
    }

    return body;
  }

  private static void WriteSuperblock(byte[] image, int rootNid, int inodeCount, uint totalBlocks) {
    var sb = image.AsSpan(1024);
    BinaryPrimitives.WriteUInt32LittleEndian(sb, ErofsReader.Magic);
    sb[12] = BlockSizeBits;                                                  // blkszbits
    BinaryPrimitives.WriteUInt16LittleEndian(sb[16..], (ushort)rootNid);     // root_nid
    BinaryPrimitives.WriteUInt32LittleEndian(sb[20..], (uint)inodeCount);    // inos (informational)
    BinaryPrimitives.WriteUInt32LittleEndian(sb[36..], MetaBlockAddress);    // meta_blkaddr
    // blocks: total filesystem blocks (informational for the reader, but set correctly).
    BinaryPrimitives.WriteUInt32LittleEndian(sb[44..], totalBlocks);
  }

  private static void WriteInode(byte[] image, long offset, ushort mode, int size, uint dataBlockAddress) {
    var inode = image.AsSpan((int)offset);
    BinaryPrimitives.WriteUInt16LittleEndian(inode, FormatFlatPlainCompact);      // i_format: compact, FLAT_PLAIN
    // i_xattr_icount (@2) stays 0 — no extended attributes.
    BinaryPrimitives.WriteUInt16LittleEndian(inode[4..], mode);                   // i_mode
    BinaryPrimitives.WriteUInt32LittleEndian(inode[8..], (uint)size);             // i_size
    BinaryPrimitives.WriteUInt16LittleEndian(inode[12..], 1);                     // i_nlink = 1 (informational)
    BinaryPrimitives.WriteUInt32LittleEndian(inode[16..], dataBlockAddress);      // i_u.raw_blkaddr
  }

  private static int CeilDiv(int value, int divisor) => (value + divisor - 1) / divisor;
}
