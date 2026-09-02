#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;
using Compression.Core.DiskImage;

namespace FileSystem.Jffs2;

/// <summary>
/// Builds a JFFS2 (Journaling Flash File System v2) image from scratch.
/// Produces a valid log-structured image with cleanmarkers, inode nodes,
/// and dirent nodes. Data is stored uncompressed (compr=0x00 NONE).
/// Default erase block size: 128 KiB (common NOR flash).
/// Files whose name contains path separators ('/' or '\') are placed inside a
/// real directory tree: each intermediate path segment becomes its own
/// directory inode plus a dirent in its parent, so nested paths round-trip
/// through the reader instead of being flattened into the root.
/// </summary>
public sealed class Jffs2Writer {
  private readonly List<(string Name, FilePayload Payload)> _files = [];
  private readonly int _eraseBlockSize;

  /// <summary>Default erase block size for NOR flash: 128 KiB.</summary>
  internal const int DefaultEraseBlockSize = 128 * 1024;

  /// <summary>JFFS2 magic number (LE).</summary>
  private const ushort Magic = 0x1985;

  /// <summary>Node type identifiers.</summary>
  private const ushort NodeTypeDirent = 0xE001;
  private const ushort NodeTypeInode = 0xE002;
  private const ushort NodeTypeCleanmarker = 0x2003;

  /// <summary>Fills the tail of an erase block so no node straddles the boundary.</summary>
  private const ushort NodeTypePadding = 0x2004;

  /// <summary>
  /// Largest data payload one inode node carries. JFFS2 writes a file as a run of
  /// page-sized fragments, each an inode node with its own <c>offset</c> into the
  /// file; a single node holding a whole multi-gigabyte file could neither fit an
  /// erase block nor be expressed by the 32-bit length fields.
  /// </summary>
  private const int DataFragmentSize = 4096;

  /// <summary>JFFS2 inode node fixed header size (before data).</summary>
  private const int InodeNodeHeaderSize = 68;

  /// <summary>JFFS2 dirent node fixed header size (before name).</summary>
  private const int DirentNodeHeaderSize = 40;

  /// <summary>Common node header size (magic + nodetype + totlen + hdr_crc).</summary>
  private const int CommonHeaderSize = 12;

  /// <summary>DT_REG — regular file.</summary>
  private const byte DtReg = 8;

  /// <summary>DT_DIR — directory.</summary>
  private const byte DtDir = 4;

  /// <summary>Root directory inode number (JFFS2 convention).</summary>
  private const uint RootInode = 1;

  /// <summary>S_IFREG | 0644</summary>
  private const uint ModeRegular = 0x81A4;

  /// <summary>S_IFDIR | 0755</summary>
  private const uint ModeDirectory = 0x41ED;

    /// <summary>
  /// Initializes a new instance of <see cref="Jffs2Writer"/>.
  /// </summary>
public Jffs2Writer(int eraseBlockSize = DefaultEraseBlockSize) {
    if (eraseBlockSize < 4096 || (eraseBlockSize & (eraseBlockSize - 1)) != 0)
      throw new ArgumentException("Erase block size must be a power of two >= 4096.", nameof(eraseBlockSize));
    this._eraseBlockSize = eraseBlockSize;
  }

  /// <summary>Queues a file for inclusion in the next <see cref="Build"/> call.</summary>
  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    this._files.Add((name, FilePayload.FromBytes(data)));
  }

  /// <summary>
  /// Queues a file whose bytes are pulled from <paramref name="openStream" /> as the
  /// image is written, so the content never has to fit in memory.
  /// </summary>
  public void AddStreamingFile(string name, long size, Func<Stream> openStream) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(openStream);
    if (size > uint.MaxValue)
      throw new IOException($"JFFS2: '{name}' is {size:N0} bytes; the inode isize field is 32-bit.");
    this._files.Add((name, FilePayload.FromStream(size, openStream)));
  }

  /// <summary>
  /// Builds a complete JFFS2 image. Layout:
  /// 1. Cleanmarker at offset 0
  /// 2. Root directory inode node (inode 1, mode=dir)
  /// 3. For each path component a directory inode + dirent (parent=enclosing dir),
  ///    created once and shared; for each file an inode node (data in body) +
  ///    dirent node (parent=enclosing dir).
  /// 4. Remainder filled with 0xFF
  /// Image is padded to a multiple of the erase block size.
  /// </summary>
  public byte[] Build() {
    using var buffer = new MemoryStream();
    this.WriteTo(buffer);
    return buffer.ToArray();
  }

  /// <summary>Writes the image to a stream, node by node — nothing is buffered whole.</summary>
  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);
    var emitter = new NodeEmitter(output, this._eraseBlockSize);
    uint nextInode = 2; // inode 1 = root dir
    var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // 1. Cleanmarker
    emitter.Emit(BuildCleanmarker());

    // 2. Root directory inode (inode 1)
    emitter.Emit(BuildInodeNode(RootInode, 1, ModeDirectory, 0, [], now));

    // Maps a normalized directory path (e.g. "docs/api") to its inode number.
    // The empty path maps to the root inode.
    var directoryInodes = new Dictionary<string, uint>(StringComparer.Ordinal) {
      [string.Empty] = RootInode,
    };

    // 3. Per-file: ensure parent directory tree exists, then emit the file.
    foreach (var (rawName, payload) in this._files) {
      var segments = SplitPath(rawName);
      if (segments.Length == 0)
        continue;

      // Walk/create the chain of parent directories.
      var parentInode = RootInode;
      var pathSoFar = string.Empty;
      for (var i = 0; i < segments.Length - 1; ++i) {
        pathSoFar = pathSoFar.Length == 0 ? segments[i] : pathSoFar + "/" + segments[i];
        if (!directoryInodes.TryGetValue(pathSoFar, out var dirInode)) {
          dirInode = nextInode++;
          directoryInodes[pathSoFar] = dirInode;
          emitter.Emit(BuildInodeNode(dirInode, 1, ModeDirectory, 0, [], now));
          emitter.Emit(BuildDirentNode(parentInode, dirInode, segments[i], DtDir, 1, now));
        }

        parentInode = dirInode;
      }

      // File data: one inode node per fragment, all at version 1 so the reader
      // treats them as a single write contributing successive ranges.
      var leafName = segments[^1];
      var fileInode = nextInode++;
      var size = (uint)payload.Size;

      if (size == 0) {
        emitter.Emit(BuildInodeNode(fileInode, 1, ModeRegular, 0, [], now));
      } else {
        var fragment = new byte[DataFragmentSize];
        using var source = payload.Open();
        uint written = 0;
        while (written < size) {
          var want = (int)Math.Min(DataFragmentSize, size - written);
          var got = 0;
          while (got < want) {
            var n = source.Read(fragment, got, want - got);
            if (n <= 0) break;
            got += n;
          }
          if (got <= 0)
            throw new IOException($"JFFS2: '{rawName}' ended after {written:N0} of {size:N0} bytes.");
          emitter.Emit(BuildDataNode(fileInode, ModeRegular, size, written, fragment.AsSpan(0, got), now));
          written += (uint)got;
        }
      }

      emitter.Emit(BuildDirentNode(parentInode, fileInode, leafName, DtReg, 1, now));
    }

    emitter.Finish();
  }

  /// <summary>
  /// Appends nodes to a stream, keeping each one inside a single erase block —
  /// a node that straddles the boundary is unrecoverable once that block is
  /// erased, so the tail is filled with a padding node instead.
  /// </summary>
  private sealed class NodeEmitter(Stream output, int eraseBlockSize) {

    private long _position;

    public void Emit(byte[] node) {
      var aligned = Align4(node.Length);
      var room = eraseBlockSize - (int)(this._position % eraseBlockSize);
      if (aligned > room) {
        this.Pad(room);
        room = eraseBlockSize;
      }
      if (aligned > room)
        throw new IOException(
          $"JFFS2: a {node.Length}-byte node does not fit a {eraseBlockSize}-byte erase block.");

      output.Write(node, 0, node.Length);
      for (var i = node.Length; i < aligned; ++i) output.WriteByte(0xFF);
      this._position += aligned;
    }

    /// <summary>Rounds the image up to a whole erase block, in the erased 0xFF state.</summary>
    public void Finish() {
      var tail = (int)(this._position % eraseBlockSize);
      // An image is always a whole number of erase blocks, and never zero of them.
      var fill = this._position == 0 ? eraseBlockSize : tail == 0 ? 0 : eraseBlockSize - tail;
      this.FillErased(fill);
      this._position += fill;
      output.Flush();
    }

    private void Pad(int room) {
      if (room <= 0) return;
      // Under twelve bytes there is no room for a padding node's header, so the
      // remainder simply stays in the erased state.
      if (room >= CommonHeaderSize) {
        var pad = new byte[CommonHeaderSize];
        BinaryPrimitives.WriteUInt16LittleEndian(pad.AsSpan(0, 2), Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(pad.AsSpan(2, 2), NodeTypePadding);
        BinaryPrimitives.WriteUInt32LittleEndian(pad.AsSpan(4, 4), (uint)room);
        BinaryPrimitives.WriteUInt32LittleEndian(pad.AsSpan(8, 4), Crc32.Compute(pad.AsSpan(0, 8)));
        output.Write(pad, 0, pad.Length);
        this.FillErased(room - CommonHeaderSize);
      } else {
        this.FillErased(room);
      }
      this._position += room;
    }

    private void FillErased(long count) {
      if (count <= 0) return;
      var chunk = new byte[(int)Math.Min(count, 64 * 1024)];
      Array.Fill(chunk, (byte)0xFF);
      var remaining = count;
      while (remaining > 0) {
        var n = (int)Math.Min(chunk.Length, remaining);
        output.Write(chunk, 0, n);
        remaining -= n;
      }
    }
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
  private static byte[] BuildInodeNode(uint inode, uint version, uint mode, uint size, byte[] data, uint mtime)
    => BuildDataNode(inode, mode, size, 0, data, mtime, version);

  /// <summary>
  /// Builds one data-carrying inode node: <paramref name="fileOffset" /> is where
  /// <paramref name="data" /> starts within the file and <paramref name="size" /> is
  /// the file's total length, which every fragment repeats.
  /// </summary>
  private static byte[] BuildDataNode(uint inode, uint mode, uint size, uint fileOffset,
    ReadOnlySpan<byte> data, uint mtime, uint version = 1) {
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
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(44, 4), fileOffset); // offset (start of data in file)
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(48, 4), (uint)data.Length); // csize (compressed size = dsize for uncompressed)
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(52, 4), (uint)data.Length); // dsize (decompressed size)
    node[56] = 0x00; // compr = JFFS2_COMPR_NONE
    node[57] = 0x00; // usercompr
    BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(58, 2), 0); // flags

    // Data
    if (data.Length > 0)
      data.CopyTo(node.AsSpan(InodeNodeHeaderSize));

    // data_crc — CRC of the data payload
    var dataCrc = Crc32.Compute(data);
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

  /// <summary>
  /// Splits an entry name into its path components on '/' and '\' separators,
  /// dropping empty segments (leading/trailing/duplicate separators).
  /// </summary>
  private static string[] SplitPath(string name)
    => name.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
}
