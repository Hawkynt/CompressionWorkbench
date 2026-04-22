using System.Buffers.Binary;
using System.Text;
using Compression.Core.Deflate;
using Compression.Core.Dictionary.Lz4;
using Compression.Core.Dictionary.Lzma;
using Compression.Core.Dictionary.Lzo;

namespace FileSystem.SquashFs;

/// <summary>
/// Reads a SquashFS version 4 filesystem image.
/// </summary>
public sealed class SquashFsReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly SquashFsSuperblock _sb;
  private readonly List<SquashFsEntry> _entries = [];
  private FragmentEntry[]? _fragmentTable;
  private uint[]? _idTable;

  // Decoded metadata region caches: maps absolute file offset of the metadata block
  // header to the 8192-byte decompressed payload.
  private readonly Dictionary<long, byte[]> _metaCache = new();

  /// <summary>
  /// Opens a SquashFS image from the given stream.
  /// </summary>
  /// <param name="stream">A seekable stream containing the SquashFS image.</param>
  /// <param name="leaveOpen">When true, the stream is not disposed when this reader is disposed.</param>
  public SquashFsReader(Stream stream, bool leaveOpen = false) {
    if (!stream.CanSeek)
      throw new ArgumentException("Stream must be seekable.", nameof(stream));

    _stream = stream;
    _leaveOpen = leaveOpen;

    _sb = SquashFsSuperblock.Read(stream);
    LoadIdTable();
    LoadFragmentTable();
    WalkDirectory();
  }

  /// <summary>All entries found in the archive, in depth-first order.</summary>
  public IReadOnlyList<SquashFsEntry> Entries => _entries;

  /// <summary>
  /// Extracts the data of a regular file entry.
  /// </summary>
  /// <param name="entry">A file entry obtained from <see cref="Entries"/>.</param>
  /// <returns>The uncompressed file data.</returns>
  public byte[] Extract(SquashFsEntry entry) {
    if (entry.IsDirectory)
      throw new ArgumentException("Cannot extract a directory entry.", nameof(entry));
    if (entry.IsSymlink)
      throw new ArgumentException("Cannot extract a symlink entry.", nameof(entry));

    return ExtractFile(entry);
  }

  // ──────────────────────────────────────────────────────────────────────────
  // Metadata reading
  // ──────────────────────────────────────────────────────────────────────────

  // A cursor into the logical stream of concatenated decompressed metadata blocks.
  private struct MetaCursor {
    public long BlockOffset;  // absolute file offset of the current metadata block header
    public int  IntraOffset;  // byte offset within the decompressed block data
  }

  /// <summary>
  /// Reads <paramref name="count"/> bytes from the metadata region starting at
  /// <paramref name="cursor"/> and advances the cursor.
  /// </summary>
  private byte[] ReadMeta(ref MetaCursor cursor, int count) {
    var result = new byte[count];
    var filled = 0;
    while (filled < count) {
      var block = GetMetaBlock(cursor.BlockOffset);
      var available = block.Length - cursor.IntraOffset;
      if (available <= 0) {
        // Advance to the next block — we need its header size to skip
        cursor.BlockOffset = AdvanceMetaBlock(cursor.BlockOffset);
        cursor.IntraOffset = 0;
        continue;
      }
      var toCopy = Math.Min(count - filled, available);
      block.AsSpan(cursor.IntraOffset, toCopy).CopyTo(result.AsSpan(filled));
      filled += toCopy;
      cursor.IntraOffset += toCopy;
      if (cursor.IntraOffset >= block.Length) {
        cursor.BlockOffset = AdvanceMetaBlock(cursor.BlockOffset);
        cursor.IntraOffset = 0;
      }
    }
    return result;
  }

  private long AdvanceMetaBlock(long headerOffset) {
    // Read the 2-byte header at this offset to find the compressed size
    _stream.Position = headerOffset;
    Span<byte> hdr = stackalloc byte[2];
    ReadExact(hdr);
    var word = BinaryPrimitives.ReadUInt16LittleEndian(hdr);
    var compressedSize = word & SquashFsConstants.MetadataSizeMask;
    return headerOffset + 2 + compressedSize;
  }

  private byte[] GetMetaBlock(long headerOffset) {
    if (_metaCache.TryGetValue(headerOffset, out var cached))
      return cached;

    _stream.Position = headerOffset;
    Span<byte> hdr = stackalloc byte[2];
    ReadExact(hdr);
    var word = BinaryPrimitives.ReadUInt16LittleEndian(hdr);
    var isUncompressed = (word & SquashFsConstants.MetadataUncompressedFlag) != 0;
    var size = word & SquashFsConstants.MetadataSizeMask;

    var compressedData = new byte[size];
    ReadExact(compressedData);

    byte[] decompressed;
    if (isUncompressed) {
      decompressed = compressedData;
    } else {
      decompressed = DecompressBlock(compressedData, SquashFsConstants.MetadataBlockMaxSize);
    }

    _metaCache[headerOffset] = decompressed;
    return decompressed;
  }

  private ushort ReadMetaU16(ref MetaCursor cursor) {
    var b = ReadMeta(ref cursor, 2);
    return BinaryPrimitives.ReadUInt16LittleEndian(b);
  }

  private uint ReadMetaU32(ref MetaCursor cursor) {
    var b = ReadMeta(ref cursor, 4);
    return BinaryPrimitives.ReadUInt32LittleEndian(b);
  }

  private ulong ReadMetaU64(ref MetaCursor cursor) {
    var b = ReadMeta(ref cursor, 8);
    return BinaryPrimitives.ReadUInt64LittleEndian(b);
  }

  // ──────────────────────────────────────────────────────────────────────────
  // ID table
  // ──────────────────────────────────────────────────────────────────────────

  private void LoadIdTable() {
    if (_sb.IdCount == 0) {
      _idTable = [];
      return;
    }

    // Number of metadata blocks needed
    var blockCount = (_sb.IdCount + SquashFsConstants.IdsPerBlock - 1) / SquashFsConstants.IdsPerBlock;
    var pointers = ReadLookupPointers(_sb.IdTableStart, blockCount);

    _idTable = new uint[_sb.IdCount];
    var idx = 0;
    for (var bi = 0; bi < blockCount && idx < _sb.IdCount; ++bi) {
      var blockData = GetMetaBlock((long)pointers[bi]);
      var pos = 0;
      while (pos + 4 <= blockData.Length && idx < _sb.IdCount) {
        _idTable[idx++] = BinaryPrimitives.ReadUInt32LittleEndian(blockData.AsSpan(pos));
        pos += 4;
      }
    }
  }

  private uint LookupId(ushort index) {
    if (_idTable == null || index >= _idTable.Length)
      return 0;
    return _idTable[index];
  }

  // ──────────────────────────────────────────────────────────────────────────
  // Fragment table
  // ──────────────────────────────────────────────────────────────────────────

  private struct FragmentEntry {
    public ulong Start;
    public uint Size;
  }

  private void LoadFragmentTable() {
    if (_sb.FragmentCount == 0 || _sb.FragmentTableStart == SquashFsConstants.InvalidTable) {
      _fragmentTable = [];
      return;
    }

    var blockCount = (int)((_sb.FragmentCount + SquashFsConstants.FragmentEntriesPerBlock - 1)
                     / SquashFsConstants.FragmentEntriesPerBlock);
    var pointers = ReadLookupPointers(_sb.FragmentTableStart, blockCount);

    _fragmentTable = new FragmentEntry[_sb.FragmentCount];
    var idx = 0;
    for (var bi = 0; bi < blockCount && idx < (int)_sb.FragmentCount; ++bi) {
      var blockData = GetMetaBlock((long)pointers[bi]);
      var pos = 0;
      while (pos + SquashFsConstants.FragmentEntrySize <= blockData.Length
             && idx < (int)_sb.FragmentCount) {
        _fragmentTable[idx++] = new FragmentEntry {
          Start = BinaryPrimitives.ReadUInt64LittleEndian(blockData.AsSpan(pos)),
          Size  = BinaryPrimitives.ReadUInt32LittleEndian(blockData.AsSpan(pos + 8)),
          // bytes 12-15: unused
        };
        pos += SquashFsConstants.FragmentEntrySize;
      }
    }
  }

  // ──────────────────────────────────────────────────────────────────────────
  // Lookup table helpers
  // ──────────────────────────────────────────────────────────────────────────

  private ulong[] ReadLookupPointers(ulong tableStart, int count) {
    _stream.Position = (long)tableStart;
    var pointers = new ulong[count];
    var buf = new byte[8];
    for (var i = 0; i < count; ++i) {
      ReadExact(buf);
      pointers[i] = BinaryPrimitives.ReadUInt64LittleEndian(buf);
    }
    return pointers;
  }

  // ──────────────────────────────────────────────────────────────────────────
  // Directory walking
  // ──────────────────────────────────────────────────────────────────────────

  private void WalkDirectory() {
    // Root inode reference: high 32 bits = block offset into inode table,
    // low 16 bits = byte offset within the decompressed block.
    var rootInodeRef = _sb.RootInode;
    var rootBlockOffset = (uint)(rootInodeRef >> 16);
    var rootIntraOffset = (ushort)(rootInodeRef & 0xFFFF);

    var cursor = new MetaCursor {
      BlockOffset = (long)_sb.InodeTableStart + rootBlockOffset,
      IntraOffset = rootIntraOffset,
    };

    var root = ReadInode(ref cursor, "");
    if (root is not null && root.IsDirectory)
      WalkDir(root, root.FullPath);
    // Also add root itself? Typically we don't include "/" but we do add its children.
  }

  private void WalkDir(SquashFsEntry dir, string basePath) {
    // dir stores the absolute metadata offset into the directory table
    var dirBlockOffset = dir.BlocksStart;   // offset in bytes from start of directory table
    var dirIntraOffset = (int)dir.FragmentOffset; // offset within the decompressed block

    var cursor = new MetaCursor {
      BlockOffset = (long)_sb.DirectoryTableStart + dirBlockOffset,
      IntraOffset = dirIntraOffset,
    };

    // file_size field for a directory = total listing bytes + 3 (with no entries = 3)
    // We read exactly file_size - 3 bytes worth of directory listing.
    var listingSize = (int)dir.FileSize - 3;
    if (listingSize <= 0)
      return;

    var bytesRead = 0;
    while (bytesRead < listingSize) {
      // Read directory header (12 bytes)
      if (listingSize - bytesRead < 12)
        break;

      var hdrCount      = ReadMetaU32(ref cursor); bytesRead += 4;
      var hdrStart      = ReadMetaU32(ref cursor); bytesRead += 4;
      var hdrInodeBase  = ReadMetaU32(ref cursor); bytesRead += 4;

      var entryCount = (int)(hdrCount + 1);
      for (var e = 0; e < entryCount; ++e) {
        if (listingSize - bytesRead < 8)
          goto done;

        var entryOffset      = ReadMetaU16(ref cursor); bytesRead += 2;
        var entryInodeOffset = (short)ReadMetaU16(ref cursor); bytesRead += 2;
        var entryType        = ReadMetaU16(ref cursor); bytesRead += 2;
        var entryNameSize    = ReadMetaU16(ref cursor); bytesRead += 2;
        var nameBytes        = ReadMeta(ref cursor, entryNameSize + 1);
        bytesRead += entryNameSize + 1;

        var name = Encoding.UTF8.GetString(nameBytes);
        var fullPath = basePath.Length == 0 ? name : basePath + "/" + name;

        // Locate the inode for this entry
        var inodeCursor = new MetaCursor {
          BlockOffset = (long)_sb.InodeTableStart + hdrStart,
          IntraOffset = entryOffset,
        };

        var entry = ReadInode(ref inodeCursor, fullPath);
        if (entry is null)
          continue;

        _entries.Add(entry);

        if (entry.IsDirectory)
          WalkDir(entry, fullPath);
      }
    }
    done:;
  }

  // ──────────────────────────────────────────────────────────────────────────
  // Inode parsing
  // ──────────────────────────────────────────────────────────────────────────

  private SquashFsEntry? ReadInode(ref MetaCursor cursor, string fullPath) {
    // Common inode header (16 bytes)
    var inodeType  = ReadMetaU16(ref cursor);
    var perms      = ReadMetaU16(ref cursor);
    var uidIdx     = ReadMetaU16(ref cursor);
    var gidIdx     = ReadMetaU16(ref cursor);
    var mtime      = ReadMetaU32(ref cursor);
    var inodeNum   = ReadMetaU32(ref cursor); // unused by us

    var modifiedTime = DateTimeOffset.FromUnixTimeSeconds(mtime).UtcDateTime;
    var uid = LookupId(uidIdx);
    var gid = LookupId(gidIdx);

    var name = fullPath.Contains('/') ? fullPath[(fullPath.LastIndexOf('/') + 1)..] : fullPath;

    switch (inodeType) {
      case SquashFsConstants.InodeBasicDir: {
        var dirBlockStart  = ReadMetaU32(ref cursor);
        var nlink          = ReadMetaU32(ref cursor);
        var fileSize       = ReadMetaU16(ref cursor);
        var offset         = ReadMetaU16(ref cursor);
        var parentInode    = ReadMetaU32(ref cursor);
        return new SquashFsEntry {
          Name         = name,
          FullPath     = fullPath,
          IsDirectory  = true,
          ModifiedTime = modifiedTime,
          Permissions  = perms,
          Uid          = uid,
          Gid          = gid,
          // Repurpose BlocksStart/FragmentOffset for directory location
          BlocksStart    = dirBlockStart,
          FragmentOffset = offset,
          FileSize       = fileSize,
        };
      }

      case SquashFsConstants.InodeExtDir: {
        var nlink          = ReadMetaU32(ref cursor);
        var fileSize       = ReadMetaU32(ref cursor);
        var dirBlockStart  = ReadMetaU32(ref cursor);
        var parentInode    = ReadMetaU32(ref cursor);
        var indexCount     = ReadMetaU16(ref cursor);
        var offset         = ReadMetaU16(ref cursor);
        var xattrIdx       = ReadMetaU32(ref cursor);
        // Skip directory index entries
        for (var i = 0; i < indexCount; ++i) {
          ReadMetaU32(ref cursor); // index offset
          ReadMetaU32(ref cursor); // start block
          var nameSize = ReadMetaU32(ref cursor);
          ReadMeta(ref cursor, (int)(nameSize + 1));
        }
        return new SquashFsEntry {
          Name         = name,
          FullPath     = fullPath,
          IsDirectory  = true,
          ModifiedTime = modifiedTime,
          Permissions  = perms,
          Uid          = uid,
          Gid          = gid,
          BlocksStart    = dirBlockStart,
          FragmentOffset = offset,
          FileSize       = fileSize,
        };
      }

      case SquashFsConstants.InodeBasicFile: {
        var blocksStart  = ReadMetaU32(ref cursor);
        var fragment     = ReadMetaU32(ref cursor);
        var fragOffset   = ReadMetaU32(ref cursor);
        var fileSize     = ReadMetaU32(ref cursor);
        var blockCount   = (int)Math.Ceiling((double)fileSize / _sb.BlockSize);
        if (fragment != SquashFsConstants.NoFragment && fileSize % _sb.BlockSize == 0)
          blockCount = (int)(fileSize / _sb.BlockSize); // exact multiple — fragment holds remainder
        var blockSizes = new uint[blockCount];
        for (var i = 0; i < blockCount; ++i)
          blockSizes[i] = ReadMetaU32(ref cursor);
        return new SquashFsEntry {
          Name           = name,
          FullPath       = fullPath,
          Size           = fileSize,
          ModifiedTime   = modifiedTime,
          Permissions    = perms,
          Uid            = uid,
          Gid            = gid,
          BlocksStart    = blocksStart,
          Fragment       = fragment,
          FragmentOffset = fragOffset,
          FileSize       = fileSize,
          BlockSizes     = blockSizes,
        };
      }

      case SquashFsConstants.InodeExtFile: {
        var blocksStart  = ReadMetaU64(ref cursor);
        var fileSize     = ReadMetaU64(ref cursor);
        var sparse       = ReadMetaU64(ref cursor);
        var nlink        = ReadMetaU32(ref cursor);
        var fragment     = ReadMetaU32(ref cursor);
        var fragOffset   = ReadMetaU32(ref cursor);
        var xattrIdx     = ReadMetaU32(ref cursor);
        var blockCount   = (int)Math.Ceiling((double)fileSize / _sb.BlockSize);
        if (fragment != SquashFsConstants.NoFragment && fileSize % (ulong)_sb.BlockSize == 0)
          blockCount = (int)(fileSize / (ulong)_sb.BlockSize);
        var blockSizes = new uint[blockCount];
        for (var i = 0; i < blockCount; ++i)
          blockSizes[i] = ReadMetaU32(ref cursor);
        return new SquashFsEntry {
          Name           = name,
          FullPath       = fullPath,
          Size           = (long)fileSize,
          ModifiedTime   = modifiedTime,
          Permissions    = perms,
          Uid            = uid,
          Gid            = gid,
          BlocksStart    = (uint)blocksStart,
          Fragment       = fragment,
          FragmentOffset = fragOffset,
          FileSize       = (uint)Math.Min(fileSize, uint.MaxValue),
          BlockSizes     = blockSizes,
        };
      }

      case SquashFsConstants.InodeBasicSymlink:
      case SquashFsConstants.InodeExtSymlink: {
        var nlink      = ReadMetaU32(ref cursor);
        var symlinkSize = ReadMetaU32(ref cursor);
        var targetBytes = ReadMeta(ref cursor, (int)symlinkSize);
        if (inodeType == SquashFsConstants.InodeExtSymlink)
          ReadMetaU32(ref cursor); // xattr_idx
        var target = Encoding.UTF8.GetString(targetBytes);
        return new SquashFsEntry {
          Name           = name,
          FullPath       = fullPath,
          IsSymlink      = true,
          SymlinkTarget  = target,
          ModifiedTime   = modifiedTime,
          Permissions    = perms,
          Uid            = uid,
          Gid            = gid,
        };
      }

      // Device nodes, FIFOs, sockets — return a minimal entry, not extractable
      case SquashFsConstants.InodeBasicBlkDev:
      case SquashFsConstants.InodeBasicChrDev:
        ReadMetaU32(ref cursor); // nlink
        ReadMetaU32(ref cursor); // rdev
        return new SquashFsEntry {
          Name = name, FullPath = fullPath,
          ModifiedTime = modifiedTime, Permissions = perms, Uid = uid, Gid = gid,
        };

      case SquashFsConstants.InodeBasicFifo:
      case SquashFsConstants.InodeBasicSocket:
        ReadMetaU32(ref cursor); // nlink
        return new SquashFsEntry {
          Name = name, FullPath = fullPath,
          ModifiedTime = modifiedTime, Permissions = perms, Uid = uid, Gid = gid,
        };

      case SquashFsConstants.InodeExtBlkDev:
      case SquashFsConstants.InodeExtChrDev:
        ReadMetaU32(ref cursor); // nlink
        ReadMetaU32(ref cursor); // rdev
        ReadMetaU32(ref cursor); // xattr_idx
        return new SquashFsEntry {
          Name = name, FullPath = fullPath,
          ModifiedTime = modifiedTime, Permissions = perms, Uid = uid, Gid = gid,
        };

      case SquashFsConstants.InodeExtFifo:
      case SquashFsConstants.InodeExtSocket:
        ReadMetaU32(ref cursor); // nlink
        ReadMetaU32(ref cursor); // xattr_idx
        return new SquashFsEntry {
          Name = name, FullPath = fullPath,
          ModifiedTime = modifiedTime, Permissions = perms, Uid = uid, Gid = gid,
        };

      default:
        // Unknown inode type — skip
        return null;
    }
  }

  // ──────────────────────────────────────────────────────────────────────────
  // File extraction
  // ──────────────────────────────────────────────────────────────────────────

  private byte[] ExtractFile(SquashFsEntry entry) {
    var fileSize = entry.FileSize;
    var output = new byte[fileSize];
    var outPos = 0;

    // Read full data blocks
    var filePos = (long)entry.BlocksStart;
    for (var i = 0; i < entry.BlockSizes.Length; ++i) {
      var sizeEntry = entry.BlockSizes[i];
      var isUncompressed = (sizeEntry & SquashFsConstants.BlockUncompressedFlag) != 0;
      var compressedSize = (int)(sizeEntry & ~SquashFsConstants.BlockUncompressedFlag);

      if (compressedSize == 0) {
        // Sparse block — zero-fill
        outPos += (int)_sb.BlockSize;
        continue;
      }

      _stream.Position = filePos;
      var compressedData = new byte[compressedSize];
      ReadExact(compressedData);
      filePos += compressedSize;

      byte[] blockData;
      if (isUncompressed || compressedSize == (int)_sb.BlockSize) {
        blockData = compressedData;
      } else {
        var maxOut = (int)_sb.BlockSize;
        blockData = DecompressBlock(compressedData, maxOut);
      }

      var toCopy = Math.Min(blockData.Length, (int)fileSize - outPos);
      blockData.AsSpan(0, toCopy).CopyTo(output.AsSpan(outPos));
      outPos += toCopy;
    }

    // Read fragment (tail data)
    if (entry.Fragment != SquashFsConstants.NoFragment && outPos < (int)fileSize) {
      var frag = GetFragment(entry.Fragment);
      var fragOffset = (int)entry.FragmentOffset;
      var remaining = (int)fileSize - outPos;
      frag.AsSpan(fragOffset, remaining).CopyTo(output.AsSpan(outPos));
      outPos += remaining;
    }

    return output;
  }

  private byte[] GetFragment(uint index) {
    if (_fragmentTable == null || index >= _fragmentTable.Length)
      throw new InvalidDataException($"Fragment index {index} out of range.");

    var frag = _fragmentTable[index];
    var isUncompressed = (frag.Size & SquashFsConstants.BlockUncompressedFlag) != 0;
    var compressedSize = (int)(frag.Size & ~SquashFsConstants.BlockUncompressedFlag);

    _stream.Position = (long)frag.Start;
    var compressedData = new byte[compressedSize];
    ReadExact(compressedData);

    if (isUncompressed)
      return compressedData;

    return DecompressBlock(compressedData, (int)_sb.BlockSize);
  }

  // ──────────────────────────────────────────────────────────────────────────
  // Compression dispatch
  // ──────────────────────────────────────────────────────────────────────────

  private byte[] DecompressBlock(byte[] data, int maxOutput) {
    return _sb.CompressionType switch {
      SquashFsConstants.CompressionGzip => DecompressGzip(data),
      SquashFsConstants.CompressionLz4  => DecompressLz4(data, maxOutput),
      SquashFsConstants.CompressionLzo  => DecompressLzo(data, maxOutput),
      SquashFsConstants.CompressionXz   => DecompressXz(data),
      SquashFsConstants.CompressionZstd => DecompressZstd(data),
      SquashFsConstants.CompressionLzma => DecompressLzma(data),
      _ => throw new NotSupportedException(
             $"Compression type {_sb.CompressionType} is not supported.")
    };
  }

  private static byte[] DecompressGzip(byte[] data) {
    // SquashFS gzip blocks are zlib-wrapped (RFC 1950): 2-byte header + deflate + 4-byte adler32
    ReadOnlySpan<byte> span = data;
    if (span.Length >= 2) {
      var cmf = span[0];
      var flg = span[1];
      // zlib magic: cmf & 0x0F == 8 (deflate), (cmf*256+flg) % 31 == 0
      if ((cmf & 0x0F) == 8 && ((cmf * 256 + flg) % 31 == 0)) {
        // Strip 2-byte zlib header; deflate data ends 4 bytes before the end (adler32 trailer)
        var deflateData = span[2..^4];
        return DeflateDecompressor.Decompress(deflateData);
      }
    }
    // Fallback: treat as raw deflate
    return DeflateDecompressor.Decompress(data);
  }

  private static byte[] DecompressLz4(byte[] data, int maxOutput) {
    // LZ4 block format (no frame header in SquashFS)
    var dest = new byte[maxOutput];
    // LZ4 block decompression: try to decompress into dest
    var outLen = Lz4BlockDecompressor.Decompress(data, dest);
    return dest.AsSpan(0, outLen).ToArray();
  }

  private static byte[] DecompressLzo(byte[] data, int maxOutput) {
    return Lzo1xDecompressor.Decompress(data, maxOutput);
  }

  private static byte[] DecompressXz(byte[] data) {
    using var ms = new MemoryStream(data);
    using var xzStream = new FileFormat.Xz.XzStream(ms, Compression.Core.Streams.CompressionStreamMode.Decompress, leaveOpen: true);
    using var outMs = new MemoryStream();
    xzStream.CopyTo(outMs);
    return outMs.ToArray();
  }

  private static byte[] DecompressZstd(byte[] data) {
    using var ms = new MemoryStream(data);
    using var zstdStream = new FileFormat.Zstd.ZstdStream(ms, Compression.Core.Streams.CompressionStreamMode.Decompress, leaveOpen: true);
    using var outMs = new MemoryStream();
    zstdStream.CopyTo(outMs);
    return outMs.ToArray();
  }

  private static byte[] DecompressLzma(byte[] data) {
    // SquashFS LZMA blocks: 5-byte properties header + 8-byte uncompressed size + compressed payload
    if (data.Length < 13)
      throw new InvalidDataException("LZMA block too short.");
    var props = data.AsSpan(0, 5).ToArray();
    var uncompressedSize = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(5));
    using var ms = new MemoryStream(data, 13, data.Length - 13);
    var decoder = new LzmaDecoder(ms, props, uncompressedSize);
    return decoder.Decode();
  }

  // ──────────────────────────────────────────────────────────────────────────
  // Stream helpers
  // ──────────────────────────────────────────────────────────────────────────

  private void ReadExact(Span<byte> buffer) {
    var totalRead = 0;
    while (totalRead < buffer.Length) {
      var n = _stream.Read(buffer[totalRead..]);
      if (n == 0)
        throw new EndOfStreamException("Unexpected end of SquashFS stream.");
      totalRead += n;
    }
  }

  private void ReadExact(byte[] buffer) => ReadExact(buffer.AsSpan());

  /// <inheritdoc />
  public void Dispose() {
    if (!_leaveOpen)
      _stream.Dispose();
  }
}
