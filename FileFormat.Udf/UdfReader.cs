#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Udf;

public sealed class UdfReader : IDisposable {
  private const int SectorSize = 2048;
  private readonly byte[] _data;
  private readonly List<UdfEntry> _entries = [];

  private int _partitionStart; // in sectors
  private int _blockSize = SectorSize;

  public IReadOnlyList<UdfEntry> Entries => _entries;

  public UdfReader(Stream stream, bool leaveOpen = false) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < 257 * SectorSize)
      throw new InvalidDataException("UDF: image too small.");

    // Validate Volume Recognition Sequence — look for NSR02 or NSR03
    bool foundNsr = false;
    for (int sector = 16; sector < 20 && sector * SectorSize + 5 < _data.Length; sector++) {
      var off = sector * SectorSize;
      var id = Encoding.ASCII.GetString(_data, off + 1, 5);
      if (id is "NSR02" or "NSR03") { foundNsr = true; break; }
    }
    if (!foundNsr)
      throw new InvalidDataException("UDF: no NSR02/NSR03 descriptor found.");

    // Read AVDP at sector 256
    var avdpOff = 256 * SectorSize;
    var avdpTagId = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(avdpOff));
    if (avdpTagId != 2)
      throw new InvalidDataException("UDF: invalid AVDP tag.");

    var mainVdsLoc = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(avdpOff + 20));
    var mainVdsLen = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(avdpOff + 16));

    // Scan VDS for Partition Descriptor (5) and Logical Volume Descriptor (6)
    int partStart = 0, partLen = 0;
    int fsdLbn = 0;
    int fsdPartRef = 0;

    var vdsSectors = (int)(mainVdsLen / SectorSize);
    for (int i = 0; i < vdsSectors && i < 64; i++) {
      var off = (int)(mainVdsLoc + i) * SectorSize;
      if (off + 512 > _data.Length) break;
      var tagId = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(off));

      if (tagId == 5) { // Partition Descriptor
        partStart = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(off + 188));
        partLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(off + 192));
      } else if (tagId == 6) { // Logical Volume Descriptor
        _blockSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(off + 212));
        if (_blockSize == 0) _blockSize = SectorSize;
        // FSD location: long_ad at offset 248
        fsdLbn = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(off + 252));
        fsdPartRef = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(off + 256));
      } else if (tagId == 8) { // Terminating Descriptor
        break;
      }
    }

    _partitionStart = partStart;

    // Read File Set Descriptor
    var fsdOffset = PartitionOffset(fsdLbn);
    if (fsdOffset + 512 > _data.Length) return;
    var fsdTag = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(fsdOffset));
    if (fsdTag != 256) return;

    // Root ICB: long_ad at offset 400
    var rootIcbLen = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(fsdOffset + 400));
    var rootIcbLbn = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(fsdOffset + 404));

    ReadDirectory((int)rootIcbLbn, (int)rootIcbLen, "");
  }

  private int PartitionOffset(int lbn) => (_partitionStart + lbn) * SectorSize;

  private void ReadDirectory(int icbLbn, int icbLen, string basePath) {
    var feOffset = PartitionOffset(icbLbn);
    if (feOffset + 200 > _data.Length) return;

    var feTag = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(feOffset));
    if (feTag is not (261 or 266)) return;

    // Parse File Entry or Extended File Entry
    int lEa, lAd, adStart;
    long infoLength;
    byte fileType;
    int icbFlags;

    if (feTag == 261) {
      // File Entry
      icbFlags = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(feOffset + 34));
      fileType = _data[feOffset + 27];
      infoLength = (long)BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(feOffset + 56));
      lEa = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(feOffset + 168));
      lAd = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(feOffset + 172));
      adStart = feOffset + 176 + lEa;
    } else {
      // Extended File Entry
      icbFlags = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(feOffset + 34));
      fileType = _data[feOffset + 27];
      infoLength = (long)BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(feOffset + 56));
      lEa = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(feOffset + 208));
      lAd = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(feOffset + 212));
      adStart = feOffset + 216 + lEa;
    }

    if (fileType != 4) return; // not a directory

    // Read allocation descriptors to get directory data
    var dirData = ReadAllocData(adStart, lAd, icbFlags & 0x07, infoLength);
    if (dirData == null) return;

    // Parse File Identifier Descriptors
    var pos = 0;
    while (pos + 38 < dirData.Length) {
      var fidTag = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(pos));
      if (fidTag != 257) break;

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
      var childIcbLbn = (int)BinaryPrimitives.ReadUInt32LittleEndian(dirData.AsSpan(pos + 24));

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

  private long GetFileSize(int icbLbn) {
    var off = PartitionOffset(icbLbn);
    if (off + 64 > _data.Length) return 0;
    var tag = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(off));
    if (tag is not (261 or 266)) return 0;
    return (long)BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(off + 56));
  }

  private long GetFileDataOffset(int icbLbn) {
    var off = PartitionOffset(icbLbn);
    if (off + 200 > _data.Length) return 0;
    var tag = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(off));
    if (tag is not (261 or 266)) return 0;

    int lEa, adStart;
    var icbFlags = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(off + 34));
    var adType = icbFlags & 0x07;

    if (tag == 261) {
      lEa = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(off + 168));
      adStart = off + 176 + lEa;
    } else {
      lEa = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(off + 208));
      adStart = off + 216 + lEa;
    }

    if (adType == 3) {
      // Embedded data — data is inline after the FE header
      return adStart;
    }

    // Short alloc descriptor: 8 bytes (length uint32 + position uint32)
    if (adType == 0 && adStart + 8 <= _data.Length) {
      var lbn = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(adStart + 4));
      return PartitionOffset(lbn);
    }
    // Long alloc descriptor: 16 bytes
    if (adType == 1 && adStart + 16 <= _data.Length) {
      var lbn = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(adStart + 4));
      return PartitionOffset(lbn);
    }

    return 0;
  }

  private byte[]? ReadAllocData(int adStart, int lAd, int adType, long infoLength) {
    if (adType == 3) {
      // Embedded (inline) data
      if (adStart + lAd <= _data.Length)
        return _data.AsSpan(adStart, lAd).ToArray();
      return null;
    }

    // Read from allocation descriptors
    using var ms = new MemoryStream();
    var pos = adStart;
    var end = adStart + lAd;

    while (pos < end && ms.Length < infoLength) {
      if (adType == 0) {
        // Short allocation descriptor: 8 bytes
        if (pos + 8 > _data.Length) break;
        var extLen = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(pos));
        var extPos = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(pos + 4));
        var len = (int)(extLen & 0x3FFFFFFF);
        var off = PartitionOffset(extPos);
        if (off + len <= _data.Length)
          ms.Write(_data, off, len);
        pos += 8;
      } else if (adType == 1) {
        // Long allocation descriptor: 16 bytes
        if (pos + 16 > _data.Length) break;
        var extLen = (int)(BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(pos)) & 0x3FFFFFFF);
        var extLbn = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(pos + 4));
        var off = PartitionOffset(extLbn);
        if (off + extLen <= _data.Length)
          ms.Write(_data, off, extLen);
        pos += 16;
      } else {
        break;
      }
    }

    return ms.ToArray();
  }

  public byte[] Extract(UdfEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    if (entry.DataOffset + entry.Size > _data.Length) return [];
    return _data.AsSpan((int)entry.DataOffset, (int)entry.Size).ToArray();
  }

  public void Dispose() { }
}
