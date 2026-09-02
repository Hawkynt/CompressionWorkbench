#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;
using System.Text;

namespace FileSystem.Udf;

/// <summary>
/// Reads the directory tree of a UDF volume image and extracts the files it holds.
/// </summary>
public sealed class UdfReader : IDisposable {
  private const int SectorSize = 2048;
  // Structures are read on demand: copying a multi-gigabyte volume in capped the
  // reader at what a byte[] can address, which UDF's 32-bit block numbers do not.
  private readonly ImageAccessor _img;
  private readonly long _len;
  private readonly List<UdfEntry> _entries = [];

  private long _partitionStart; // in sectors
  private int _blockSize = SectorSize;

    /// <summary>
  /// Gets the entries.
  /// </summary>
public IReadOnlyList<UdfEntry> Entries => _entries;

  /// <summary>Total size of the backing image in bytes.</summary>
  public long Length => this._len;

    /// <summary>
  /// Initializes a new instance of <see cref="UdfReader"/>.
  /// </summary>
public UdfReader(Stream stream, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek) stream.Position = 0;
    _img = new ImageAccessor(stream, leaveOpen: true);
    _len = _img.Length;
    Parse();
  }

  // ── Field readers over the image ────────────────────────────────────────

  private byte U8(long off) => off >= 0 && off < _len ? _img.Read(off, 1)[0] : (byte)0;

  private ushort U16(long off) =>
    off >= 0 && off + 2 <= _len ? BinaryPrimitives.ReadUInt16LittleEndian(_img.Read(off, 2)) : (ushort)0;

  private uint U32(long off) =>
    off >= 0 && off + 4 <= _len ? BinaryPrimitives.ReadUInt32LittleEndian(_img.Read(off, 4)) : 0u;

  private ulong U64(long off) =>
    off >= 0 && off + 8 <= _len ? BinaryPrimitives.ReadUInt64LittleEndian(_img.Read(off, 8)) : 0ul;

  private void Parse() {
    if (_len < 257L * SectorSize)
      throw new InvalidDataException("UDF: image too small.");

    // Validate Volume Recognition Sequence — look for NSR02 or NSR03
    bool foundNsr = false;
    for (var sector = 16L; sector < 20 && sector * SectorSize + 6 < _len; sector++) {
      var off = sector * SectorSize;
      var id = Encoding.ASCII.GetString(_img.Read(off + 1, 5));
      if (id is "NSR02" or "NSR03") { foundNsr = true; break; }
    }
    if (!foundNsr)
      throw new InvalidDataException("UDF: no NSR02/NSR03 descriptor found.");

    // Read AVDP at sector 256
    var avdpOff = 256L * SectorSize;
    var avdpTagId = U16(avdpOff);
    if (avdpTagId != 2)
      throw new InvalidDataException("UDF: invalid AVDP tag.");

    var mainVdsLoc = U32(avdpOff + 20);
    var mainVdsLen = U32(avdpOff + 16);

    // Scan VDS for Partition Descriptor (5) and Logical Volume Descriptor (6)
    long partStart = 0, partLen = 0;
    long fsdLbn = 0;

    var vdsSectors = (int)(mainVdsLen / SectorSize);
    for (var i = 0; i < vdsSectors && i < 64; i++) {
      var off = ((long)mainVdsLoc + i) * SectorSize;
      if (off + 512 > _len) break;
      var tagId = U16(off);

      if (tagId == 5) { // Partition Descriptor
        partStart = U32(off + 188);
        partLen = U32(off + 192);
      } else if (tagId == 6) { // Logical Volume Descriptor
        _blockSize = (int)U32(off + 212);
        if (_blockSize == 0) _blockSize = SectorSize;
        // FSD location: long_ad at offset 248
        fsdLbn = U32(off + 252);
      } else if (tagId == 8) { // Terminating Descriptor
        break;
      }
    }

    _partitionStart = partStart;

    // Read File Set Descriptor
    var fsdOffset = PartitionOffset(fsdLbn);
    if (fsdOffset + 512 > _len) return;
    var fsdTag = U16(fsdOffset);
    if (fsdTag != 256) return;

    // Root ICB: long_ad at offset 400
    var rootIcbLen = U32(fsdOffset + 400);
    var rootIcbLbn = U32(fsdOffset + 404);

    ReadDirectory(rootIcbLbn, (int)rootIcbLen, "");
  }

  private long PartitionOffset(long lbn) => (_partitionStart + lbn) * SectorSize;

  private void ReadDirectory(long icbLbn, int icbLen, string basePath) {
    var feOffset = PartitionOffset(icbLbn);
    if (feOffset + 200 > _len) return;

    var feTag = U16(feOffset);
    if (feTag is not (261 or 266)) return;

    // Parse File Entry or Extended File Entry
    int lEa, lAd;
    long adStart;
    long infoLength;
    byte fileType;
    int icbFlags;

    icbFlags = U16(feOffset + 34);
    fileType = U8(feOffset + 27);
    infoLength = (long)U64(feOffset + 56);
    if (feTag == 261) {
      // File Entry
      lEa = (int)U32(feOffset + 168);
      lAd = (int)U32(feOffset + 172);
      adStart = feOffset + 176 + lEa;
    } else {
      // Extended File Entry
      lEa = (int)U32(feOffset + 208);
      lAd = (int)U32(feOffset + 212);
      adStart = feOffset + 216 + lEa;
    }

    if (fileType != 4) return; // not a directory

    // Read allocation descriptors to get directory data
    var dirData = ReadAllocData(adStart, lAd, icbFlags & 0x07, infoLength);
    if (dirData == null) return;

    // Parse File Identifier Descriptors. FIDs are block-aligned: none crosses a
    // logical-block boundary (ECMA-167 §14.4), so when a FID won't fit in the
    // tail of a block the writer zero-pads to the next block. A zero tag thus
    // means "rest of this block is padding" — advance to the next block start
    // and keep reading instead of stopping.
    var pos = 0;
    while (pos + 38 < dirData.Length) {
      var fidTag = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(pos));
      if (fidTag != 257) {
        var nextBlock = ((pos / _blockSize) + 1) * _blockSize;
        if (nextBlock <= pos) break;
        pos = nextBlock;
        continue;
      }

      var fidLen = 38;
      var lIu = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(pos + 36));
      var fidIdLen = dirData[pos + 19];
      fidLen += lIu + fidIdLen;
      // Pad to 4-byte boundary
      fidLen = (fidLen + 3) & ~3;

      var fidFlags = dirData[pos + 18];
      var isParent = (fidFlags & 0x08) != 0;
      var isDeleted = (fidFlags & 0x04) != 0;
      var isDir = (fidFlags & 0x02) != 0;

      // ICB at offset 20
      var childIcbLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(dirData.AsSpan(pos + 20));
      var childIcbLbn = (long)BinaryPrimitives.ReadUInt32LittleEndian(dirData.AsSpan(pos + 24));

      if (!isParent && !isDeleted && fidIdLen > 0) {
        var nameStart = pos + 38 + lIu;
        string name;
        // Check for CS0/OSTA encoding (first byte)
        if (fidIdLen > 1 && dirData[nameStart] == 8) {
          name = Encoding.UTF8.GetString(dirData, nameStart + 1, fidIdLen - 1);
        } else if (fidIdLen > 1 && dirData[nameStart] == 16) {
          name = Encoding.BigEndianUnicode.GetString(dirData, nameStart + 1, fidIdLen - 1);
        } else {
          name = Encoding.ASCII.GetString(dirData, nameStart, fidIdLen);
        }
        name = name.TrimEnd('\0');

        var fullPath = string.IsNullOrEmpty(basePath) ? name : $"{basePath}/{name}";

        if (isDir) {
          _entries.Add(new UdfEntry { Name = fullPath, IsDirectory = true });
          ReadDirectory(childIcbLbn, childIcbLen, fullPath);
        } else {
          // Read file entry to get size
          var childSize = GetFileSize(childIcbLbn);
          var childDataOff = GetFileDataOffset(childIcbLbn);
          _entries.Add(new UdfEntry {
            Name = fullPath,
            Size = childSize,
            DataOffset = childDataOff,
            DataLength = childSize,
          });
        }
      }

      pos += fidLen;
    }
  }

  private long GetFileSize(long icbLbn) {
    var off = PartitionOffset(icbLbn);
    if (off + 64 > _len) return 0;
    var tag = U16(off);
    if (tag is not (261 or 266)) return 0;
    return (long)U64(off + 56);
  }

  private long GetFileDataOffset(long icbLbn) {
    var off = PartitionOffset(icbLbn);
    if (off + 200 > _len) return 0;
    var tag = U16(off);
    if (tag is not (261 or 266)) return 0;

    int lEa;
    long adStart;
    var icbFlags = U16(off + 34);
    var adType = icbFlags & 0x07;

    if (tag == 261) {
      lEa = (int)U32(off + 168);
      adStart = off + 176 + lEa;
    } else {
      lEa = (int)U32(off + 208);
      adStart = off + 216 + lEa;
    }

    if (adType == 3) {
      // Embedded data — data is inline after the FE header
      return adStart;
    }

    // Short alloc descriptor: 8 bytes (length uint32 + position uint32)
    if (adType == 0 && adStart + 8 <= _len)
      return PartitionOffset(U32(adStart + 4));
    // Long alloc descriptor: 16 bytes
    if (adType == 1 && adStart + 16 <= _len)
      return PartitionOffset(U32(adStart + 4));

    return 0;
  }

  private byte[]? ReadAllocData(long adStart, int lAd, int adType, long infoLength) {
    if (adType == 3) {
      // Embedded (inline) data
      if (adStart + lAd <= _len) return _img.Read(adStart, lAd);
      return null;
    }

    // Read from allocation descriptors. This assembles directory bodies, which
    // are bounded by the directory's own size, not by the volume's.
    using var ms = new MemoryStream();
    var pos = adStart;
    var end = adStart + lAd;

    while (pos < end && ms.Length < infoLength) {
      if (adType == 0) {
        // Short allocation descriptor: 8 bytes
        if (pos + 8 > _len) break;
        var extLen = U32(pos);
        var extPos = U32(pos + 4);
        var len = (int)(extLen & 0x3FFFFFFF);
        var off = PartitionOffset(extPos);
        if (len > 0 && off + len <= _len)
          ms.Write(_img.Read(off, len));
        pos += 8;
      } else if (adType == 1) {
        // Long allocation descriptor: 16 bytes
        if (pos + 16 > _len) break;
        var extLen = (int)(U32(pos) & 0x3FFFFFFF);
        var extLbn = U32(pos + 4);
        var off = PartitionOffset(extLbn);
        if (extLen > 0 && off + extLen <= _len)
          ms.Write(_img.Read(off, extLen));
        pos += 16;
      } else {
        break;
      }
    }

    return ms.ToArray();
  }

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Extract(UdfEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    if (entry.DataOffset + entry.Size > _len) return [];
    if (entry.Size > Array.MaxLength)
      throw new IOException(
        $"UDF: '{entry.Name}' is {entry.Size:N0} bytes, past the array limit; use ExtractTo.");
    return _img.Read(entry.DataOffset, (int)entry.Size);
  }

  /// <summary>Writes <paramref name="entry" />'s bytes into <paramref name="destination" />.</summary>
  public long ExtractTo(UdfEntry entry, Stream destination) {
    ArgumentNullException.ThrowIfNull(entry);
    ArgumentNullException.ThrowIfNull(destination);
    if (entry.IsDirectory || entry.Size <= 0) return 0;
    var take = Math.Min(entry.Size, _len - entry.DataOffset);
    if (take <= 0) return 0;
    _img.CopyTo(entry.DataOffset, destination, take);
    return take;
  }

    /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
public void Dispose() => this._img.Dispose();
}
