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

  /// <summary>
  /// Logical block sizes ECMA-167 volumes are recorded with, most common first.
  /// The Anchor Volume Descriptor Pointer is the only descriptor at a fixed
  /// address (logical block 256), and that address is counted in logical blocks
  /// — so until the anchor is found the block size is unknown and has to be
  /// probed, exactly as udftools does.
  /// </summary>
  private static readonly int[] CandidateBlockSizes = [2048, 512, 1024, 4096, 8192, 16384, 32768];

  /// <summary>
  /// True when a descriptor tag sits at <paramref name="offset" /> with the
  /// given identifier and records <paramref name="expectedLocation" /> as its
  /// own address. The ECMA-167 §7.2 TagChecksum is verified too, so a run of
  /// file data that happens to start with the anchor's tag identifier cannot be
  /// mistaken for a descriptor.
  /// </summary>
  private bool IsTagAt(long offset, ushort identifier, uint expectedLocation) {
    if (offset < 0 || offset + 16 > _len)
      return false;

    var tag = _img.Read(offset, 16);
    if (BinaryPrimitives.ReadUInt16LittleEndian(tag) != identifier)
      return false;
    if (BinaryPrimitives.ReadUInt32LittleEndian(tag.AsSpan(12)) != expectedLocation)
      return false;

    byte sum = 0;
    for (var i = 0; i < 16; ++i)
      if (i != 4)
        sum = (byte)(sum + tag[i]);

    return sum == tag[4];
  }

  /// <summary>
  /// Locates the Anchor Volume Descriptor Pointer and, with it, the volume's
  /// logical block size. ECMA-167 §3/8.4 puts an anchor at logical block 256 and
  /// at the last block of the volume (and optionally 256 blocks before it); each
  /// is tried for every plausible block size.
  /// </summary>
  private long FindAnchor() {
    foreach (var blockSize in CandidateBlockSizes) {
      var totalBlocks = _len / blockSize;
      if (totalBlocks <= 256)
        continue;

      foreach (var block in new[] { 256L, totalBlocks - 1, totalBlocks - 257 }) {
        if (block < 256)
          continue;
        var offset = block * blockSize;
        if (!this.IsTagAt(offset, 2, (uint)block))
          continue;
        // The sequence the candidate names has to describe a volume of the same
        // block size, so a run of file data that survives the tag checks cannot
        // carry the read off to the wrong addresses in silence.
        if (!this.SequenceDeclaresBlockSize(U32(offset + 20), U32(offset + 16), blockSize))
          continue;

        this._blockSize = blockSize;
        return offset;
      }
    }

    throw new InvalidDataException("UDF: no Anchor Volume Descriptor Pointer found.");
  }

  /// <summary>
  /// True when the volume descriptor sequence at <paramref name="location" />
  /// holds a Logical Volume Descriptor declaring <paramref name="blockSize" />.
  /// </summary>
  private bool SequenceDeclaresBlockSize(uint location, uint byteLength, int blockSize) {
    var descriptors = Math.Min(byteLength / (uint)blockSize, 64);
    for (var i = 0u; i < descriptors; ++i) {
      var offset = ((long)location + i) * blockSize;
      if (offset < 0 || offset + 512 > _len)
        return false;

      var tag = U16(offset);
      if (tag == 6)
        return U32(offset + 212) == (uint)blockSize;
      if (tag == 8)
        return false;
    }

    return false;
  }

  private void Parse() {
    if (_len < 257L * 512)
      throw new InvalidDataException("UDF: image too small.");

    // ECMA-167 §2/9.1: the Volume Recognition Sequence starts at byte 32768 and
    // occupies consecutive logical sectors, whose size is 2048 or the block size
    // when that is larger. Scanning at the 2048 stride covers both.
    var foundNsr = false;
    for (var sector = 16L; sector < 24 && sector * SectorSize + 6 < _len; ++sector) {
      var off = sector * SectorSize;
      var id = Encoding.ASCII.GetString(_img.Read(off + 1, 5));
      if (id is "NSR02" or "NSR03") {
        foundNsr = true;
        break;
      }
    }
    if (!foundNsr)
      throw new InvalidDataException("UDF: no NSR02/NSR03 descriptor found.");

    var avdpOff = this.FindAnchor();

    var mainVdsLoc = U32(avdpOff + 20);
    var mainVdsLen = U32(avdpOff + 16);
    long partStart = 0;
    long fsdLbn = 0;

    var vdsSectors = (int)(mainVdsLen / (uint)_blockSize);
    for (var i = 0; i < vdsSectors && i < 64; ++i) {
      var off = ((long)mainVdsLoc + i) * _blockSize;
      if (off + 512 > _len)
        break;

      var tagId = U16(off);
      if (tagId == 5) {
        partStart = U32(off + 188);
      } else if (tagId == 6) {
        var declared = checked((int)U32(off + 212));
        if (declared > 0)
          _blockSize = declared;
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

  // The Partition Starting Location (ECMA-167 §3/10.5.9) is counted in logical
  // blocks, not in 2048-byte sectors: scaling it by a fixed 2048 addressed the
  // wrong place on every volume whose block size is not 2048.
  private long PartitionOffset(long lbn)
    => checked((_partitionStart + lbn) * (long)_blockSize);

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

        var name = OstaCompressedUnicode.Decode(dirData.AsSpan(nameStart, fidIdLen));

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

  /// <summary>One decoded allocation descriptor.</summary>
  private readonly record struct AllocationDescriptor(int ExtentType, long Length, uint Block, ushort Partition);

  /// <summary>
  /// Walks an allocation descriptor list, following the continuations that
  /// ECMA-167 §4/14.14.1.1 records as extent type 3. Once a File Entry's own
  /// descriptor area is full the rest of the list lives in an Allocation Extent
  /// Descriptor (tag 258) in a block of its own, and a reader that stops at the
  /// type-3 entry sees only as much of the object as fitted in the entry — for
  /// a directory that means most of its children vanish.
  /// </summary>
  private IEnumerable<AllocationDescriptor> EnumerateAllocationDescriptors(long adStart, int lAd, int adType) {
    if (adType is not (0 or 1))
      yield break;

    var stride = adType == 0 ? 8 : 16;
    var visited = new HashSet<long>();
    var pos = adStart;
    var end = adStart + lAd;

    while (true) {
      if (pos < 0 || end > _len || end < pos)
        yield break;

      long? continuation = null;
      while (pos + stride <= end) {
        var raw = U32(pos);
        var extentType = (int)(raw >> ExtentTypeShift);
        var length = (long)(raw & ExtentLengthMask);
        var block = U32(pos + 4);
        var partition = adType == 1 ? U16(pos + 8) : (ushort)0;
        pos += stride;

        if (length == 0)
          continue;

        if (extentType == 3) {
          // The continuation replaces the rest of this list; anything after it
          // in the current block is not part of the object.
          continuation = block;
          break;
        }

        yield return new(extentType, length, block, partition);
      }

      if (continuation is not { } nextBlock)
        yield break;

      long nextOffset;
      try {
        nextOffset = PartitionOffset(nextBlock);
      } catch (OverflowException) {
        yield break;
      }

      // A continuation pointing back at a block already walked would loop for
      // ever; refusing to revisit one bounds the walk.
      if (!visited.Add(nextOffset))
        yield break;
      if (nextOffset < 0 || nextOffset + 24 > _len)
        yield break;
      // ECMA-167 §4/14.5: the continuation block opens with an Allocation
      // Extent Descriptor whose header says how many bytes of descriptors follow.
      if (U16(nextOffset) != 258)
        yield break;

      var nextLength = U32(nextOffset + 20);
      pos = nextOffset + 24;
      end = pos + nextLength;
    }
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

    var segments = new List<UdfDataSegment>();
    long logicalOffset = 0;

    foreach (var ad in this.EnumerateAllocationDescriptors(adStart, lAd, adType)) {
      if (logicalOffset >= informationLength)
        break;

      if (ad.Partition != 0)
        return new(segments, $"UDF long allocation descriptor references partition map {ad.Partition}; only the decoded primary partition is supported.");

      var logicalLength = Math.Min(ad.Length, informationLength - logicalOffset);
      if (ad.ExtentType == 0) {
        long physicalOffset;
        try {
          physicalOffset = PartitionOffset(ad.Block);
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
    if (adStart < 0 || adStart + lAd > _len)
      return null;

    var zeroBuffer = new byte[8192];
    foreach (var ad in this.EnumerateAllocationDescriptors(adStart, lAd, adType)) {
      if (ms.Length >= infoLength)
        break;
      if (ad.Partition != 0)
        return null;

      var take = Math.Min(ad.Length, infoLength - ms.Length);
      if (take <= 0)
        continue;

      if (ad.ExtentType == 0) {
        var physical = PartitionOffset(ad.Block);
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
