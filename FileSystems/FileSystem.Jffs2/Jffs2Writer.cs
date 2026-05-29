#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;

namespace FileSystem.Jffs2;

/// <summary>
/// Builds a JFFS2 (Journaling Flash File System v2) image from scratch.
/// Produces a valid log-structured image with cleanmarkers, inode nodes,
/// and dirent nodes. Data is stored uncompressed (compr=0x00 NONE).
/// Default erase block size: 128 KiB (common NOR flash).
/// </summary>
internal sealed class Jffs2Writer {
  private readonly List<(string Name, byte[] Data)> _files = [];
  private readonly int _eraseBlockSize;

  /// <summary>Default erase block size for NOR flash: 128 KiB.</summary>
  internal const int DefaultEraseBlockSize = 128 * 1024;

  /// <summary>JFFS2 magic number (LE).</summary>
  private const ushort Magic = 0x1985;

  /// <summary>Node type identifiers.</summary>
  private const ushort NodeTypeDirent = 0xE001;
  private const ushort NodeTypeInode = 0xE002;
  private const ushort NodeTypeCleanmarker = 0x2003;

  /// <summary>JFFS2 inode node fixed header size (before data).</summary>
  private const int InodeNodeHeaderSize = 68;

  /// <summary>JFFS2 dirent node fixed header size (before name).</summary>
  private const int DirentNodeHeaderSize = 40;

  /// <summary>Common node header size (magic + nodetype + totlen + hdr_crc).</summary>
  private const int CommonHeaderSize = 12;

  /// <summary>DT_REG — regular file.</summary>
  private const byte DtReg = 8;

  /// <summary>S_IFREG | 0644</summary>
  private const uint ModeRegular = 0x81A4;

  /// <summary>S_IFDIR | 0755</summary>
  private const uint ModeDirectory = 0x41ED;

  public Jffs2Writer(int eraseBlockSize = DefaultEraseBlockSize) {
    if (eraseBlockSize < 4096 || (eraseBlockSize & (eraseBlockSize - 1)) != 0)
      throw new ArgumentException("Erase block size must be a power of two >= 4096.", nameof(eraseBlockSize));
    this._eraseBlockSize = eraseBlockSize;
  }

  /// <summary>Queues a file for inclusion in the next <see cref="Build"/> call.</summary>
  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    this._files.Add((name, data));
  }

  /// <summary>
  /// Builds a complete JFFS2 image. Layout:
  /// 1. Cleanmarker at offset 0
  /// 2. Root directory inode node (inode 1, mode=dir)
  /// 3. For each file: inode node (data in body) + dirent node (parent=1)
  /// 4. Remainder filled with 0xFF
  /// Image is padded to a multiple of the erase block size.
  /// </summary>
  public byte[] Build() {
    var nodes = new List<byte[]>();
    uint nextInode = 2; // inode 1 = root dir
    var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // 1. Cleanmarker
    nodes.Add(BuildCleanmarker());

    // 2. Root directory inode (inode 1)
    nodes.Add(BuildInodeNode(
      inode: 1,
      version: 1,
      mode: ModeDirectory,
      size: 0,
      data: [],
      mtime: now
    ));

    // 3. Per-file: inode node + dirent node
    foreach (var (name, data) in this._files) {
      var fileInode = nextInode++;

      // Inode node with file data
      nodes.Add(BuildInodeNode(
        inode: fileInode,
        version: 1,
        mode: ModeRegular,
        size: (uint)data.Length,
        data: data,
        mtime: now
      ));

      // Dirent node linking name to inode under root (parent=1)
      nodes.Add(BuildDirentNode(
        parentInode: 1,
        inode: fileInode,
        name: name,
        type: DtReg,
        version: 1,
        mtime: now
      ));
    }

    // Calculate total size: sum of aligned node sizes, padded to erase block boundary
    var totalNodeBytes = 0;
    foreach (var node in nodes)
      totalNodeBytes += Align4(node.Length);

    var imageSize = ((totalNodeBytes + this._eraseBlockSize - 1) / this._eraseBlockSize) * this._eraseBlockSize;
    if (imageSize < this._eraseBlockSize)
      imageSize = this._eraseBlockSize;

    // Fill with 0xFF (erased flash state)
    var image = new byte[imageSize];
    Array.Fill(image, (byte)0xFF);

    // Write nodes sequentially
    var offset = 0;
    foreach (var node in nodes) {
      node.CopyTo(image, offset);
      offset += Align4(node.Length);
    }

    return image;
  }

  /// <summary>Writes the image to a stream.</summary>
  public void WriteTo(Stream output) {
    var data = this.Build();
    output.Write(data, 0, data.Length);
  }

  /// <summary>
  /// Builds a cleanmarker node (12 bytes). Layout:
  /// 0: magic (u16), 2: nodetype (u16), 4: totlen (u32), 8: hdr_crc (u32)
  /// </summary>
  private static byte[] BuildCleanmarker() {
    var node = new byte[CommonHeaderSize];
    BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(0, 2), Magic);
    BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(2, 2), NodeTypeCleanmarker);
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(4, 4), CommonHeaderSize);
    // hdr_crc covers bytes 0..7
    var hdrCrc = Crc32.Compute(node.AsSpan(0, 8));
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(8, 4), hdrCrc);
    return node;
  }

  /// <summary>
  /// Builds an inode node. Layout (68 bytes header + data):
  ///  0: magic(u16) 2: nodetype(u16) 4: totlen(u32) 8: hdr_crc(u32)
  /// 12: ino(u32) 16: version(u32) 20: mode(u32)
  /// 24: uid(u16) 26: gid(u16) 28: isize(u32)
  /// 32: atime(u32) 36: mtime(u32) 40: ctime(u32)
  /// 44: offset(u32) 48: csize(u32) 52: dsize(u32)
  /// 56: compr(u8) 57: usercompr(u8) 58: flags(u16)
  /// 60: data_crc(u32) 64: node_crc(u32)
  /// 68: data[csize]
  /// </summary>
  private static byte[] BuildInodeNode(uint inode, uint version, uint mode, uint size, byte[] data, uint mtime) {
    var totLen = (uint)(InodeNodeHeaderSize + data.Length);
    var node = new byte[totLen];

    // Common header
    BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(0, 2), Magic);
    BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(2, 2), NodeTypeInode);
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(4, 4), totLen);
    // hdr_crc filled after node_crc

    // Inode-specific fields
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(12, 4), inode);
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(16, 4), version);
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(20, 4), mode);
    BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(24, 2), 0); // uid
    BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(26, 2), 0); // gid
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(28, 4), size); // isize (total file size)
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(32, 4), mtime); // atime
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(36, 4), mtime); // mtime
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(40, 4), mtime); // ctime
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(44, 4), 0); // offset (start of data in file)
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(48, 4), (uint)data.Length); // csize (compressed size = dsize for uncompressed)
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(52, 4), (uint)data.Length); // dsize (decompressed size)
    node[56] = 0x00; // compr = JFFS2_COMPR_NONE
    node[57] = 0x00; // usercompr
    BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(58, 2), 0); // flags

    // Data
    if (data.Length > 0)
      data.CopyTo(node, InodeNodeHeaderSize);

    // data_crc — CRC of the data payload
    var dataCrc = data.Length > 0 ? Crc32.Compute(data) : Crc32.Compute(ReadOnlySpan<byte>.Empty);
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(60, 4), dataCrc);

    // node_crc — CRC of bytes 0..59 (header without data_crc and node_crc fields)
    // Actually: node_crc covers bytes 12..59 (the inode-specific header, excluding common header's hdr_crc)
    // Per JFFS2 spec: node_crc covers bytes 0..63 with node_crc zeroed
    // Let's follow the kernel: node_crc = crc32(0, node, sizeof(*ri) - 8) where -8 skips data_crc+node_crc
    // Actually the kernel does: ri->node_crc = 0; ri->data_crc = dataCrc; crc32(0, ri, sizeof(*ri)-8)
    // sizeof(jffs2_raw_inode) = 68, so node_crc = crc32 of bytes 0..59
    var nodeCrc = Crc32.Compute(node.AsSpan(0, InodeNodeHeaderSize - 8));
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(64, 4), nodeCrc);

    // hdr_crc — CRC of common header bytes 0..7
    var hdrCrc = Crc32.Compute(node.AsSpan(0, 8));
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(8, 4), hdrCrc);

    return node;
  }

  /// <summary>
  /// Builds a dirent node. Layout (40 bytes header + name):
  ///  0: magic(u16) 2: nodetype(u16) 4: totlen(u32) 8: hdr_crc(u32)
  /// 12: pino(u32) 16: version(u32) 20: ino(u32) 24: mctime(u32)
  /// 28: nsize(u8) 29: type(u8) 30: unused[2]
  /// 32: node_crc(u32) 36: name_crc(u32)
  /// 40: name[nsize]
  /// </summary>
  private static byte[] BuildDirentNode(uint parentInode, uint inode, string name, byte type, uint version, uint mtime) {
    var nameBytes = Encoding.UTF8.GetBytes(name);
    var totLen = (uint)(DirentNodeHeaderSize + nameBytes.Length);
    var node = new byte[totLen];

    // Common header
    BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(0, 2), Magic);
    BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(2, 2), NodeTypeDirent);
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(4, 4), totLen);

    // Dirent-specific fields
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(12, 4), parentInode);
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(16, 4), version);
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(20, 4), inode);
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(24, 4), mtime);
    node[28] = (byte)nameBytes.Length;
    node[29] = type;
    // bytes 30-31 = unused (0)

    // Name
    nameBytes.CopyTo(node, DirentNodeHeaderSize);

    // name_crc — CRC of the name bytes
    var nameCrc = Crc32.Compute(nameBytes);
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(36, 4), nameCrc);

    // node_crc — CRC of bytes 0..31 (the 32 bytes before node_crc/name_crc)
    // Per kernel: rd->node_crc = 0; crc32(0, rd, sizeof(*rd)-8) where sizeof=40, so bytes 0..31
    var nodeCrc = Crc32.Compute(node.AsSpan(0, DirentNodeHeaderSize - 8));
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(32, 4), nodeCrc);

    // hdr_crc — CRC of common header bytes 0..7
    var hdrCrc = Crc32.Compute(node.AsSpan(0, 8));
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(8, 4), hdrCrc);

    return node;
  }

  /// <summary>Aligns a value up to the next 4-byte boundary.</summary>
  private static int Align4(int value) => (value + 3) & ~3;
}
