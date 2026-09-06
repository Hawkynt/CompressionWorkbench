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
  private const uint ExtentLengthMask = 0x3FFFFFFF;
  private const int ExtentTypeShift = 30;

  // Structures are read on demand: copying a multi-gigabyte volume in capped the
  // reader at what a byte[] can address, which UDF's 32-bit block numbers do not.
  private readonly ImageAccessor _img;
  private readonly long _len;
  private readonly List<UdfEntry> _entries = [];

  private long _partitionStart; // in sectors
  private int _blockSize = SectorSize;

  /// <summary>Gets the entries.</summary>
  public IReadOnlyList<UdfEntry> Entries => _entries;

  /// <summary>Total size of the backing image in bytes.</summary>
  public long Length => this._len;

  /// <summary>Decoded logical block size.</summary>
  internal int LogicalBlockSize => this._blockSize;

  /// <summary>Initializes a new UDF reader.</summary>
  public UdfReader(Stream stream, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek)
      stream.Position = 0;
    _img = new ImageAccessor(stream, leaveOpen: true);
    _len = _img.Length;
    Parse();
  }

  private byte U8(long off) => off >= 0 && off < _len ? _img.Read(off, 1)[0] : (byte)0;

  private ushort U16(long off)
    => off >= 0 && off + 2 <= _len
      ? BinaryPrimitives.ReadUInt16LittleEndian(_img.Read(off, 2))
      : (ushort)0;

  private uint U32(long off)
    => off >= 0 && off + 4 <= _len
      ? BinaryPrimitives.ReadUInt32LittleEndian(_img.Read(off, 4))
      : 0u;

  private ulong U64(long off)
    => off >= 0 && off + 8 <= _len
      ? BinaryPrimitives.ReadUInt64LittleEndian(_img.Read(off, 8))
      : 0ul;

  private void Parse() {
    if (_len < 257L * SectorSize)
      throw new InvalidDataException("UDF: image too small.");

    var foundNsr = false;
    for (var sector = 16L; sector < 20 && sector * SectorSize + 6 < _len; ++sector) {
      var off = sector * SectorSize;
      var id = Encoding.ASCII.GetString(_img.Read(off + 1, 5));
      if (id is "NSR02" or "NSR03") {
        foundNsr = true;
        break;
      }
    }
    if (!foundNsr)
      throw new InvalidDataException("UDF: no NSR02/NSR03 descriptor found.");

    var avdpOff = 256L * SectorSize;
    if (U16(avdpOff) != 2)
      throw new InvalidDataException("UDF: invalid AVDP tag.");

    var mainVdsLoc = U32(avdpOff + 20);
    var mainVdsLen = U32(avdpOff + 16);
    long partStart = 0;
    long fsdLbn = 0;

    var vdsSectors = (int)(mainVdsLen / SectorSize);
    for (var i = 0; i < vdsSectors && i < 64; ++i) {
      var off = ((long)mainVdsLoc + i) * SectorSize;
      if (off + 512 > _len)
        break;

      var tagId = U16(off);
      if (tagId == 5) {
        partStart = U32(off + 188);
      } else if (tagId == 6) {
        _blockSize = checked((int)U32(off + 212));
        if (_blockSize == 0)
          _blockSize = SectorSize;
        fsdLbn = U32(off + 252);
      } else if (tagId == 8) {
        break;
      }
    }

    _partitionStart = partStart;

    var fsdOffset = PartitionOffset(fsdLbn);
    if (fsdOffset + 512 > _len)
      return;
    if (U16(fsdOffset) != 256)
      return;

    var rootIcbLen = U32(fsdOffset + 400);
    var rootIcbLbn = U32(fsdOffset + 404);
    ReadDirectory(rootIcbLbn, checked((int)rootIcbLen), "");
  }

  private long PartitionOffset(long lbn)
    => checked(_partitionStart * SectorSize + lbn * (long)_blockSize);

  private void ReadDirectory(long icbLbn, int icbLen, string basePath) {
    var feOffset = PartitionOffset(icbLbn);
    if (feOffset + 200 > _len)
      return;

    var feTag = U16(feOffset);
    if (feTag is not (261 or 266))
      return;

    int lEa, lAd;
    long adStart;
    var icbFlags = U16(feOffset + 34);
    var fileType = U8(feOffset + 27);
    var infoLengthRaw = U64(feOffset + 56);
    if (infoLengthRaw > long.MaxValue)
      throw new InvalidDataException("UDF: directory information length exceeds supported signed range.");
    var infoLength = (long)infoLengthRaw;

    if (feTag == 261) {
      lEa = checked((int)U32(feOffset + 168));
      lAd = checked((int)U32(feOffset + 172));
      adStart = checked(feOffset + 176L + lEa);
    } else {
      lEa = checked((int)U32(feOffset + 208));
      lAd = checked((int)U32(feOffset + 212));
      adStart = checked(feOffset + 216L + lEa);
    }

    if (fileType != 4)
      return;

    var dirData = ReadAllocData(adStart, lAd, icbFlags & 0x07, infoLength);
    if (dirData is null)
      return;

    var pos = 0;
    while (pos + 38 < dirData.Length) {
      var fidTag = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(pos));
      if (fidTag != 257) {
        var nextBlock = ((pos / _blockSize) + 1) * _blockSize;
        if (nextBlock <= pos)
          break;
        pos = nextBlock;
        continue;
      }

      var lIu = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(pos + 36));
      var fidIdLen = dirData[pos + 19];
      var fidLen = (38 + lIu + fidIdLen + 3) & ~3;
      if (fidLen <= 0 || pos > dirData.Length - fidLen)
        break;

      var fidFlags = dirData[pos + 18];
      var isParent = (fidFlags & 0x08) != 0;
      var isDeleted = (fidFlags & 0x04) != 0;
      var isDir = (fidFlags & 0x02) != 0;
      var childIcbLen = BinaryPrimitives.ReadUInt32LittleEndian(dirData.AsSpan(pos + 20));
      var childIcbLbn = BinaryPrimitives.ReadUInt32LittleEndian(dirData.AsSpan(pos + 24));

      if (!isParent && !isDeleted && fidIdLen > 0) {
        var nameStart = pos + 38 + lIu;
        if (nameStart > dirData.Length - fidIdLen)
          break;

        string name;
        if (fidIdLen > 1 && dirData[nameStart] == 8)
          name = Encoding.UTF8.GetString(dirData, nameStart + 1, fidIdLen - 1);
        else if (fidIdLen > 1 && dirData[nameStart] == 16)
          name = Encoding.BigEndianUnicode.GetString(dirData, nameStart + 1, fidIdLen - 1);
        else
          name = Encoding.ASCII.GetString(dirData, nameStart, fidIdLen);
        name = name.TrimEnd('\0');

        var fullPath = string.IsNullOrEmpty(basePath) ? name : $"{basePath}/{name}";
        if (isDir) {
          _entries.Add(new UdfEntry { Name = fullPath, IsDirectory = true });
          ReadDirectory(childIcbLbn, checked((int)childIcbLen), fullPath);
        } else {
          var childSize = GetFileSize(childIcbLbn);
          var layout = GetFileDataLayout(childIcbLbn, childSize);
          var contiguousOffset = layout.Segments is [{ ZeroFill: false } only] ? only.PhysicalOffset : 0;
          _entries.Add(new UdfEntry {
            Name = fullPath,
            Size = childSize,
            DataOffset = contiguousOffset,
            DataLength = childSize,
            DataSegments = layout.Segments,
            MountLimitation = layout.Limitation,
          });
        }
      }

      pos += fidLen;
    }
  }

  private long GetFileSize(long icbLbn) {
    var off = PartitionOffset(icbLbn);
    if (off + 64 > _len)
      return 0;
    var tag = U16(off);
    if (tag is not (261 or 266))
      return 0;
    var size = U64(off + 56);
    if (size > long.MaxValue)
      throw new InvalidDataException("UDF: file information length exceeds supported signed range.");
    return (long)size;
  }

  private UdfFileDataLayout GetFileDataLayout(long icbLbn, long informationLength) {
    if (informationLength < 0)
      return new([], "UDF file has a negative logical length.");
    if (informationLength == 0)
      return new([], null);

    var off = PartitionOffset(icbLbn);
    if (off < 0 || off > _len - 216)
      return new([], "UDF File Entry lies outside the image.");

    var tag = U16(off);
    if (tag is not (261 or 266))
      return new([], $"UDF file ICB has unsupported descriptor tag {tag}.");

    int lEa, lAd;
    long adStart;
    var adType = U16(off + 34) & 0x07;
    if (tag == 261) {
      lEa = checked((int)U32(off + 168));
      lAd = checked((int)U32(off + 172));
      adStart = checked(off + 176L + lEa);
    } else {
      lEa = checked((int)U32(off + 208));
      lAd = checked((int)U32(off + 212));
      adStart = checked(off + 216L + lEa);
    }

    long adEnd;
    try {
      adEnd = checked(adStart + lAd);
    } catch (OverflowException) {
      return new([], "UDF allocation descriptor range overflows the image address space.");
    }
    if (adStart < 0 || adStart > _len || adEnd > _len)
      return new([], "UDF allocation descriptor range lies outside the image.");

    if (adType == 3) {
      if (lAd < informationLength || adStart > _len - informationLength)
        return new([], "UDF embedded file body is shorter than its information length.");
      return new([new(0, informationLength, adStart, ZeroFill: false)], null);
    }

    if (adType is not (0 or 1))
      return new([], $"UDF allocation descriptor type {adType} is not yet supported for mounted reads.");

    var stride = adType == 0 ? 8 : 16;
    var segments = new List<UdfDataSegment>();
    long logicalOffset = 0;
    var pos = adStart;

    while (pos + stride <= adEnd && logicalOffset < informationLength) {
      var rawLength = U32(pos);
      var extentType = (int)(rawLength >> ExtentTypeShift);
      var extentLength = (long)(rawLength & ExtentLengthMask);
      var extentLbn = U32(pos + 4);
      if (extentLength == 0) {
        pos += stride;
        continue;
      }

      if (extentType == 3)
        return new(segments, "UDF continuation allocation descriptors are not yet supported for mounted reads.");

      if (adType == 1) {
        var partitionReference = U16(pos + 8);
        if (partitionReference != 0)
          return new(segments, $"UDF long allocation descriptor references partition map {partitionReference}; only the decoded primary partition is supported.");
      }

      var logicalLength = Math.Min(extentLength, informationLength - logicalOffset);
      if (extentType == 0) {
        long physicalOffset;
        try {
          physicalOffset = PartitionOffset(extentLbn);
        } catch (OverflowException) {
          return new(segments, "UDF file extent address overflows the image address space.");
        }
        if (physicalOffset < 0 || physicalOffset > _len - logicalLength)
          return new(segments, "UDF recorded file extent lies outside the backing image.");
        segments.Add(new(logicalOffset, logicalLength, physicalOffset, ZeroFill: false));
      } else {
        // ECMA-167 allocation descriptor types 1 and 2 are unrecorded ranges.
        // Their logical bytes read as zeroes and consume no source-data bytes.
        segments.Add(new(logicalOffset, logicalLength, 0, ZeroFill: true));
      }

      logicalOffset += logicalLength;
      pos += stride;
    }

    if (logicalOffset != informationLength)
      return new(segments, $"UDF allocation descriptors cover {logicalOffset} of {informationLength} logical file bytes.");

    return new(segments, null);
  }

  private byte[]? ReadAllocData(long adStart, int lAd, int adType, long infoLength) {
    if (infoLength < 0 || infoLength > int.MaxValue)
      return null;

    if (adType == 3) {
      if (lAd < infoLength || adStart < 0 || adStart > _len - infoLength)
        return null;
      return _img.Read(adStart, checked((int)infoLength));
    }

    if (adType is not (0 or 1))
      return null;

    using var ms = new MemoryStream(checked((int)infoLength));
    var pos = adStart;
    long end;
    try {
      end = checked(adStart + lAd);
    } catch (OverflowException) {
      return null;
    }
    if (adStart < 0 || end > _len)
      return null;

    var stride = adType == 0 ? 8 : 16;
    var zeroBuffer = new byte[8192];
    while (pos + stride <= end && ms.Length < infoLength) {
      var rawLength = U32(pos);
      var extentType = (int)(rawLength >> ExtentTypeShift);
      var extentLength = (long)(rawLength & ExtentLengthMask);
      var extentLbn = U32(pos + 4);
      if (extentType == 3)
        return null;
      if (adType == 1 && U16(pos + 8) != 0)
        return null;

      var take = Math.Min(extentLength, infoLength - ms.Length);
      if (take > 0) {
        if (extentType == 0) {
          var physical = PartitionOffset(extentLbn);
          if (physical < 0 || physical > _len - take)
            return null;
          _img.CopyTo(physical, ms, take);
        } else {
          var remaining = take;
          while (remaining > 0) {
            var chunk = checked((int)Math.Min(zeroBuffer.Length, remaining));
            ms.Write(zeroBuffer, 0, chunk);
            remaining -= chunk;
          }
        }
      }
      pos += stride;
    }

    return ms.Length == infoLength ? ms.ToArray() : null;
  }

  /// <summary>Decodes the supplied entry into one byte array.</summary>
  public byte[] Extract(UdfEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory || entry.Size <= 0)
      return [];
    if (entry.Size > Array.MaxLength)
      throw new IOException($"UDF: '{entry.Name}' is {entry.Size:N0} bytes, past the array limit; use ExtractTo.");

    using var output = new MemoryStream(checked((int)entry.Size));
    ExtractTo(entry, output);
    return output.ToArray();
  }

  /// <summary>Writes <paramref name="entry" />'s logical bytes into <paramref name="destination" />.</summary>
  public long ExtractTo(UdfEntry entry, Stream destination) {
    ArgumentNullException.ThrowIfNull(entry);
    ArgumentNullException.ThrowIfNull(destination);
    if (entry.IsDirectory || entry.Size <= 0)
      return 0;
    if (entry.MountLimitation is { } limitation)
      throw new NotSupportedException($"UDF: '{entry.Name}' cannot be decoded safely: {limitation}");

    var zeroBuffer = new byte[64 * 1024];
    long written = 0;
    foreach (var segment in entry.DataSegments) {
      if (segment.LogicalOffset != written)
        throw new InvalidDataException($"UDF: '{entry.Name}' has a discontinuous logical segment map.");

      if (segment.ZeroFill) {
        var remaining = segment.Length;
        while (remaining > 0) {
          var chunk = checked((int)Math.Min(zeroBuffer.Length, remaining));
          destination.Write(zeroBuffer, 0, chunk);
          remaining -= chunk;
        }
      } else {
        if (segment.PhysicalOffset < 0 || segment.PhysicalOffset > _len - segment.Length)
          throw new InvalidDataException($"UDF: '{entry.Name}' has an extent outside the backing image.");
        _img.CopyTo(segment.PhysicalOffset, destination, segment.Length);
      }
      written += segment.Length;
    }

    if (written != entry.Size)
      throw new InvalidDataException($"UDF: '{entry.Name}' segment map produced {written} of {entry.Size} bytes.");
    return written;
  }

  /// <summary>Releases resources held by this instance.</summary>
  public void Dispose() => this._img.Dispose();

  private sealed record UdfFileDataLayout(
    IReadOnlyList<UdfDataSegment> Segments,
    string? Limitation
  );
}
