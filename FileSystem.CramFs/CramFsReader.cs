using System.Buffers.Binary;
using System.Text;
using Compression.Core.Deflate;

namespace FileSystem.CramFs;

/// <summary>
/// Reads a CramFS (Compressed ROM Filesystem) image.
/// CramFS is a Linux read-only compressed filesystem where file data is stored
/// as independently-compressed 4 KB zlib blocks.
/// </summary>
public sealed class CramFsReader : IDisposable {
  private readonly byte[] _image;
  private readonly bool _bigEndian;
  private readonly List<CramFsEntry> _entries = [];

  /// <summary>
  /// Initialises a <see cref="CramFsReader"/> by loading the entire image into memory.
  /// </summary>
  /// <param name="stream">Stream positioned at the start of the CramFS image.</param>
  /// <exception cref="InvalidDataException">
  /// Thrown when the image does not start with a recognised CramFS magic number.
  /// </exception>
  public CramFsReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);

    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    this._image = ms.ToArray();

    if (this._image.Length < CramFsConstants.SuperblockSize)
      throw new InvalidDataException("Image is smaller than the CramFS superblock.");

    var rawMagic = BinaryPrimitives.ReadUInt32LittleEndian(this._image);
    if (rawMagic == CramFsConstants.MagicLE)
      this._bigEndian = false;
    else if (rawMagic == CramFsConstants.MagicBE)
      this._bigEndian = true;
    else
      throw new InvalidDataException(
        $"Not a CramFS image: magic 0x{rawMagic:X8} does not match 0x{CramFsConstants.MagicLE:X8}.");

    this.ParseSuperblockAndWalk();
  }

  /// <summary>Flat list of all entries (files, directories, symlinks) found in the image.</summary>
  public IReadOnlyList<CramFsEntry> Entries => this._entries;

  // ── Public API ───────────────────────────────────────────────────────────────

  /// <summary>
  /// Extracts (decompresses) the data for a file or symlink entry.
  /// </summary>
  /// <param name="entry">The entry to extract. Must be a regular file or symlink.</param>
  /// <returns>The uncompressed content as a byte array.</returns>
  /// <exception cref="ArgumentException">
  /// Thrown when <paramref name="entry"/> is a directory.
  /// </exception>
  public byte[] Extract(CramFsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory)
      throw new ArgumentException("Cannot extract a directory entry.", nameof(entry));
    if (entry.Size == 0)
      return [];

    return this.DecompressFile(entry);
  }

  // ── Superblock + tree walking ────────────────────────────────────────────────

  private void ParseSuperblockAndWalk() {
    // The root inode is embedded in the superblock at offset 60.
    var rootInode = this.ReadInode(CramFsConstants.RootInodeOffset);
    var rootEntry = new CramFsEntry {
      Name      = "",
      FullPath  = "/",
      Size      = rootInode.size,
      Mode      = rootInode.mode,
      Uid       = rootInode.uid,
      Gid       = rootInode.gid,
      DataOffset = rootInode.offset * 4,
    };

    this._entries.Add(rootEntry);
    this.WalkDirectory(rootInode, "/");
  }

  /// <summary>
  /// Recursively walks the directory pointed to by <paramref name="dirInode"/>,
  /// adding all children to <see cref="_entries"/> and recursing into subdirs.
  /// </summary>
  private void WalkDirectory((ushort mode, ushort uid, int size, byte gid, int namelen, int offset) dirInode, string parentPath) {
    var dirDataStart = dirInode.offset * 4;
    if (dirDataStart == 0 || dirInode.size == 0)
      return;

    var pos = dirDataStart;
    var end = dirDataStart + dirInode.size;

    while (pos < end && pos + CramFsConstants.InodeSize <= this._image.Length) {
      var inode = this.ReadInode(pos);
      pos += CramFsConstants.InodeSize;

      // namelen field is stored as (byte_count / 4), so actual byte count = inode.namelen * 4
      var nameBytes = inode.namelen * 4;
      if (nameBytes == 0 || pos + nameBytes > this._image.Length)
        break;

      // Name is null-padded to a multiple of 4 bytes
      var name = ReadNullTerminatedName(this._image, pos, nameBytes);
      pos += nameBytes;

      var fullPath = parentPath == "/"
        ? "/" + name
        : parentPath + "/" + name;

      var entry = new CramFsEntry {
        Name       = name,
        FullPath   = fullPath,
        Size       = inode.size,
        Mode       = inode.mode,
        Uid        = inode.uid,
        Gid        = inode.gid,
        DataOffset = inode.offset * 4,
      };

      this._entries.Add(entry);

      if (entry.IsDirectory)
        this.WalkDirectory(inode, fullPath);
    }
  }

  // ── File decompression ───────────────────────────────────────────────────────

  private byte[] DecompressFile(CramFsEntry entry) {
    var size   = entry.Size;
    var blocks = (size + CramFsConstants.PageSize - 1) / CramFsConstants.PageSize;

    // Block pointer table: `blocks` uint32 values starting at entry.DataOffset.
    // Each value is the END byte offset of the corresponding compressed block.
    var ptrTableStart = entry.DataOffset;
    var ptrTableSize  = blocks * 4;

    if (ptrTableStart + ptrTableSize > this._image.Length)
      throw new InvalidDataException(
        $"Block pointer table for '{entry.FullPath}' extends past end of image.");

    var result = new byte[size];
    var outPos = 0;

    for (var i = 0; i < blocks; i++) {
      var endOffset = (int)this.ReadU32(ptrTableStart + i * 4);

      // Start of this block's compressed data:
      //   block 0 → right after the pointer table
      //   block i → endOffset of block i-1
      var startOffset = i == 0
        ? ptrTableStart + ptrTableSize
        : (int)this.ReadU32(ptrTableStart + (i - 1) * 4);

      var compressedLen = endOffset - startOffset;
      if (compressedLen < 0 || startOffset + compressedLen > this._image.Length)
        throw new InvalidDataException(
          $"Block {i} of '{entry.FullPath}' has invalid bounds (start={startOffset}, end={endOffset}).");

      var expectedOut = Math.Min(CramFsConstants.PageSize, size - outPos);

      // Blocks are zlib-wrapped deflate: strip the 2-byte header and 4-byte Adler-32 trailer.
      var compressed = this._image.AsSpan(startOffset, compressedLen);
      var decompressed = DecompressZlibBlock(compressed, entry.FullPath, i);

      var copy = Math.Min(decompressed.Length, expectedOut);
      decompressed.AsSpan(0, copy).CopyTo(result.AsSpan(outPos));
      outPos += copy;
    }

    return result;
  }

  private static byte[] DecompressZlibBlock(ReadOnlySpan<byte> data, string path, int blockIndex) {
    // Minimum valid zlib block: 2-byte header + at least 1 deflate byte + 4-byte trailer
    if (data.Length < 7)
      throw new InvalidDataException(
        $"Block {blockIndex} of '{path}' is too short to be a zlib block ({data.Length} bytes).");

    // Validate and strip 2-byte zlib header
    int cmf = data[0];
    int flg = data[1];
    if ((cmf * 256 + flg) % 31 != 0)
      throw new InvalidDataException(
        $"Block {blockIndex} of '{path}' has an invalid zlib header checksum.");
    if ((cmf & 0x0F) != 8)
      throw new InvalidDataException(
        $"Block {blockIndex} of '{path}' uses unsupported zlib compression method {cmf & 0x0F}.");

    // Raw deflate payload sits between the 2-byte header and the 4-byte Adler-32 trailer
    var deflateData = data[2..^4];
    return DeflateDecompressor.Decompress(deflateData);
  }

  // ── Binary helpers ───────────────────────────────────────────────────────────

  /// <summary>
  /// Reads a cramfs inode (12 bytes) at the given byte offset.
  /// Layout (all fields little-endian on LE images):
  ///   word 0: mode[15:0], uid[31:16]
  ///   word 1: size[23:0] (bits 0-23), gid[31:24]
  ///   word 2: namelen[5:0] (bits 0-5), offset[31:6] (bits 6-31)
  /// </summary>
  private (ushort mode, ushort uid, int size, byte gid, int namelen, int offset) ReadInode(int byteOffset) {
    if (byteOffset + CramFsConstants.InodeSize > this._image.Length)
      throw new InvalidDataException(
        $"Inode at offset {byteOffset} extends past end of image.");

    var w0 = this.ReadU32(byteOffset);
    var w1 = this.ReadU32(byteOffset + 4);
    var w2 = this.ReadU32(byteOffset + 8);

    var mode = (ushort)(w0 & 0xFFFF);
    var uid  = (ushort)(w0 >> 16);
    var    size = (int)(w1 & 0x00FFFFFF);
    var   gid  = (byte)(w1 >> 24);
    var    namelen = (int)(w2 & 0x3F);
    var    offset  = (int)(w2 >> 6);

    return (mode, uid, size, gid, namelen, offset);
  }

  private uint ReadU32(int offset) {
    var span = this._image.AsSpan(offset, 4);
    return this._bigEndian
      ? BinaryPrimitives.ReadUInt32BigEndian(span)
      : BinaryPrimitives.ReadUInt32LittleEndian(span);
  }

  private static string ReadNullTerminatedName(byte[] image, int offset, int maxBytes) {
    var end = offset;
    while (end < offset + maxBytes && end < image.Length && image[end] != 0)
      end++;
    return Encoding.UTF8.GetString(image, offset, end - offset);
  }

  // ── IDisposable ──────────────────────────────────────────────────────────────

  /// <inheritdoc />
  public void Dispose() {
    // Nothing to release — the image byte array is managed by GC.
    GC.SuppressFinalize(this);
  }
}
