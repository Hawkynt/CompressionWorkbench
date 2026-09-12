#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;
using System.Text;

namespace FileSystem.Udf;

/// <summary>
/// Reads the directory tree of a UDF volume image and extracts the files it holds.
/// Type-1 ECMA-167 partition references are resolved through the Logical Volume
/// Descriptor's ordered partition-map table instead of assuming partition reference 0.
/// </summary>
public sealed class UdfReader : IDisposable {
  private const int SectorSize = 2048;
  private const uint ExtentLengthMask = 0x3FFFFFFF;
  private const int ExtentTypeShift = 30;

  private readonly ImageAccessor _img;
  private readonly long _len;
  private readonly List<UdfEntry> _entries = [];
  private readonly Dictionary<ushort, PartitionDescriptorInfo> _partitions = [];
  private readonly List<PartitionMapInfo> _partitionMaps = [];
  private readonly HashSet<(ushort PartitionReference, uint Block)> _visitedDirectories = [];

  private int _blockSize = SectorSize;

  /// <summary>Gets the entries.</summary>
  public IReadOnlyList<UdfEntry> Entries => _entries;

  /// <summary>Total size of the backing image in bytes.</summary>
  public long Length => _len;

  /// <summary>Decoded logical block size.</summary>
  internal int LogicalBlockSize => _blockSize;

  /// <summary>Initializes a new UDF reader.</summary>
  public UdfReader(Stream stream, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek)
      stream.Position = 0;
    _img = new ImageAccessor(stream, leaveOpen);
    _len = _img.Length;
    Parse();
  }

  private byte U8(long off) => off >= 0 && off < _len ? _img.ReadByte(off) : (byte)0;

  private ushort U16(long off)
    => off >= 0 && off + 2 <= _len
      ? _img.ReadUInt16(off)
      : (ushort)0;

  private uint U32(long off)
    => off >= 0 && off + 4 <= _len
      ? _img.ReadUInt32(off)
      : 0u;

  private ulong U64(long off)
    => off >= 0 && off + 8 <= _len
      ? _img.ReadUInt64(off)
      : 0ul;

  private static readonly int[] CandidateBlockSizes = [2048, 512, 1024, 4096, 8192, 16384, 32768];

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

  private long FindAnchor() {
    foreach (var blockSize in CandidateBlockSizes) {
      var totalBlocks = _len / blockSize;
      if (totalBlocks <= 256)
        continue;

      foreach (var block in new[] { 256L, totalBlocks - 1, totalBlocks - 257 }) {
        if (block < 256 || block > uint.MaxValue)
          continue;
        var offset = block * blockSize;
        if (!IsTagAt(offset, 2, (uint)block))
          continue;
        if (!SequenceDeclaresBlockSize(U32(offset + 20), U32(offset + 16), blockSize))
          continue;

        _blockSize = blockSize;
        return offset;
      }
    }

    throw new InvalidDataException("UDF: no Anchor Volume Descriptor Pointer found.");
  }

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

    var avdpOff = FindAnchor();
    var mainVdsLoc = U32(avdpOff + 20);
    var mainVdsLen = U32(avdpOff + 16);
    long lvdOffset = -1;

    var vdsSectors = checked((int)Math.Min(mainVdsLen / (uint)_blockSize, 64));
    for (var i = 0; i < vdsSectors; ++i) {
      var off = ((long)mainVdsLoc + i) * _blockSize;
      if (off + 512 > _len)
        throw new InvalidDataException("UDF: volume descriptor sequence runs outside the image.");

      var tagId = U16(off);
      if (tagId == 5) {
        var partitionNumber = U16(off + 22);
        var start = U32(off + 188);
        var length = U32(off + 192);
        if (!_partitions.TryAdd(partitionNumber, new(start, length)))
          throw new InvalidDataException($"UDF: duplicate Partition Descriptor number {partitionNumber}.");
      } else if (tagId == 6) {
        var declared = checked((int)U32(off + 212));
        if (declared != _blockSize)
          throw new InvalidDataException(
            $"UDF: Logical Volume Descriptor block size {declared} disagrees with anchor geometry {_blockSize}.");
        lvdOffset = off;
      } else if (tagId == 8) {
        break;
      }
    }

    if (lvdOffset < 0)
      throw new InvalidDataException("UDF: no Logical Volume Descriptor found.");
    if (_partitions.Count == 0)
      throw new InvalidDataException("UDF: no Partition Descriptor found.");

    ParsePartitionMaps(lvdOffset);

    // UDF maps LogicalVolumeContentsUse to a long_ad naming the File Set Descriptor.
    var fsdLbn = U32(lvdOffset + 252);
    var fsdPartitionReference = U16(lvdOffset + 256);
    var fsdOffset = PartitionOffset(fsdPartitionReference, fsdLbn);
    if (fsdOffset < 0 || fsdOffset + 512 > _len)
      throw new InvalidDataException("UDF: File Set Descriptor lies outside the image.");
    if (U16(fsdOffset) != 256)
      throw new InvalidDataException("UDF: Logical Volume Contents Use does not reference a File Set Descriptor.");

    var rootIcbLen = U32(fsdOffset + 400);
    var rootIcbLbn = U32(fsdOffset + 404);
    var rootPartitionReference = U16(fsdOffset + 408);
    ReadDirectory(rootPartitionReference, rootIcbLbn, checked((int)rootIcbLen), "");
  }

  private void ParsePartitionMaps(long lvdOffset) {
    var mapTableLength = U32(lvdOffset + 264);
    var mapCount = U32(lvdOffset + 268);
    if (mapCount == 0)
      throw new InvalidDataException("UDF: Logical Volume Descriptor has no partition maps.");
    if (mapTableLength > _blockSize - 440)
      throw new InvalidDataException("UDF: partition map table exceeds its Logical Volume Descriptor block.");

    var start = lvdOffset + 440;
    var end = checked(start + mapTableLength);
    var pos = start;
    for (var reference = 0u; reference < mapCount; ++reference) {
      if (pos + 2 > end)
        throw new InvalidDataException("UDF: partition map table ends before NumberOfPartitionMaps entries were decoded.");
      var type = U8(pos);
      var length = U8(pos + 1);
      if (length < 2 || pos + length > end)
        throw new InvalidDataException($"UDF: partition map {reference} has invalid length {length}.");

      if (type == 1) {
        if (length != 6)
          throw new InvalidDataException($"UDF: Type 1 partition map {reference} has length {length}, expected 6.");
        var volumeSequenceNumber = U16(pos + 2);
        var partitionNumber = U16(pos + 4);
        var limitation = volumeSequenceNumber == 1
          ? null
          : $"Type 1 partition map {reference} addresses volume sequence {volumeSequenceNumber}; multi-volume UDF sets are not supported.";
        if (!_partitions.ContainsKey(partitionNumber))
          limitation = $"Type 1 partition map {reference} names missing Partition Descriptor {partitionNumber}.";
        _partitionMaps.Add(new(partitionNumber, limitation));
      } else if (type == 2) {
        _partitionMaps.Add(new(null,
          $"UDF Type 2 partition map {reference} requires virtual/sparable/metadata remapping that is not implemented."));
      } else {
        _partitionMaps.Add(new(null, $"UDF partition map {reference} has unsupported type {type}."));
      }

      pos += length;
    }

    if (pos > end)
      throw new InvalidDataException("UDF: partition map table is truncated.");
  }

  private long PartitionOffset(ushort partitionReference, long lbn) {
    if (partitionReference >= _partitionMaps.Count)
      throw new InvalidDataException($"UDF: partition reference {partitionReference} is outside the Logical Volume Descriptor map table.");
    if (lbn < 0)
      throw new InvalidDataException("UDF: negative logical block number.");

    var map = _partitionMaps[partitionReference];
    if (map.Limitation is { } limitation)
      throw new NotSupportedException(limitation);
    if (map.PartitionNumber is not { } number || !_partitions.TryGetValue(number, out var partition))
      throw new InvalidDataException($"UDF: partition reference {partitionReference} cannot be resolved.");
    if ((ulong)lbn >= partition.BlockCount)
      throw new InvalidDataException(
        $"UDF: logical block {lbn} lies outside partition reference {partitionReference} ({partition.BlockCount} blocks). ");

    return checked(((long)partition.StartBlock + lbn) * _blockSize);
  }

  private bool TryPartitionRange(
      ushort partitionReference,
      uint block,
      long length,
      out long physicalOffset,
      out string? limitation) {
    physicalOffset = 0;
    limitation = null;
    try {
      if (partitionReference >= _partitionMaps.Count) {
        limitation = $"UDF partition reference {partitionReference} is outside the Logical Volume Descriptor map table.";
        return false;
      }
      var map = _partitionMaps[partitionReference];
      if (map.Limitation is { } mapLimitation) {
        limitation = mapLimitation;
        return false;
      }
      if (map.PartitionNumber is not { } number || !_partitions.TryGetValue(number, out var partition)) {
        limitation = $"UDF partition reference {partitionReference} cannot be resolved.";
        return false;
      }
      var partitionBytes = checked((long)partition.BlockCount * _blockSize);
      var relative = checked((long)block * _blockSize);
      if (length < 0 || relative < 0 || relative > partitionBytes || length > partitionBytes - relative) {
        limitation = $"UDF extent [{block}, +{length} bytes] lies outside partition reference {partitionReference}.";
        return false;
      }
      physicalOffset = checked((long)partition.StartBlock * _blockSize + relative);
      if (physicalOffset < 0 || physicalOffset > _len || length > _len - physicalOffset) {
        limitation = "UDF recorded extent lies outside the backing image.";
        return false;
      }
      return true;
    } catch (OverflowException) {
      limitation = "UDF extent address overflows the image address space.";
      return false;
    }
  }

  private void ReadDirectory(ushort partitionReference, uint icbLbn, int icbLen, string basePath) {
    if (!_visitedDirectories.Add((partitionReference, icbLbn)))
      throw new InvalidDataException($"UDF: directory '{basePath}' forms a cycle or reuses an already visited ICB.");

    var feOffset = PartitionOffset(partitionReference, icbLbn);
    if (feOffset + 216 > _len)
      throw new InvalidDataException($"UDF: directory '{basePath}' File Entry lies outside the image.");

    var feTag = U16(feOffset);
    if (feTag is not (261 or 266))
      throw new InvalidDataException($"UDF: directory '{basePath}' ICB has unsupported descriptor tag {feTag}.");

    int lEa, lAd;
    long adStart;
    var icbFlags = U16(feOffset + 34);
    var fileType = U8(feOffset + 27);
    var infoLengthRaw = U64(feOffset + 56);
    if (infoLengthRaw > int.MaxValue)
      throw new NotSupportedException("UDF: directories larger than the managed directory-buffer limit are not yet mountable.");
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
      throw new InvalidDataException($"UDF: directory '{basePath}' ICB has file type {fileType}, expected directory type 4.");

    var dirData = ReadAllocData(partitionReference, adStart, lAd, icbFlags & 0x07, infoLength)
      ?? throw new InvalidDataException($"UDF: directory '{basePath}' allocation descriptors cannot be decoded safely.");

    var pos = 0;
    while (pos + 38 <= dirData.Length) {
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
        throw new InvalidDataException($"UDF: directory '{basePath}' contains a truncated File Identifier Descriptor.");

      var fidFlags = dirData[pos + 18];
      var isParent = (fidFlags & 0x08) != 0;
      var isDeleted = (fidFlags & 0x04) != 0;
      var isDir = (fidFlags & 0x02) != 0;
      var childIcbLen = BinaryPrimitives.ReadUInt32LittleEndian(dirData.AsSpan(pos + 20));
      var childIcbLbn = BinaryPrimitives.ReadUInt32LittleEndian(dirData.AsSpan(pos + 24));
      var childPartitionReference = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(pos + 28));

      if (!isParent && !isDeleted && fidIdLen > 0) {
        var nameStart = pos + 38 + lIu;
        if (nameStart > dirData.Length - fidIdLen)
          throw new InvalidDataException($"UDF: directory '{basePath}' contains a truncated file identifier name.");

        var name = OstaCompressedUnicode.Decode(dirData.AsSpan(nameStart, fidIdLen));
        var fullPath = string.IsNullOrEmpty(basePath) ? name : $"{basePath}/{name}";
        if (isDir) {
          _entries.Add(new UdfEntry { Name = fullPath, IsDirectory = true });
          ReadDirectory(childPartitionReference, childIcbLbn, checked((int)childIcbLen), fullPath);
        } else {
          var childSize = GetFileSize(childPartitionReference, childIcbLbn);
          var layout = GetFileDataLayout(childPartitionReference, childIcbLbn, childSize);
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

  private long GetFileSize(ushort partitionReference, uint icbLbn) {
    var off = PartitionOffset(partitionReference, icbLbn);
    if (off + 64 > _len)
      throw new InvalidDataException("UDF: file ICB lies outside the image.");
    var tag = U16(off);
    if (tag is not (261 or 266))
      throw new InvalidDataException($"UDF: file ICB has unsupported descriptor tag {tag}.");
    var size = U64(off + 56);
    if (size > long.MaxValue)
      throw new InvalidDataException("UDF: file information length exceeds supported signed range.");
    return (long)size;
  }

  private readonly record struct AllocationDescriptor(int ExtentType, long Length, uint Block, ushort PartitionReference);

  private IEnumerable<AllocationDescriptor> EnumerateAllocationDescriptors(
      ushort containingPartitionReference,
      long adStart,
      int lAd,
      int adType) {
    if (adType is not (0 or 1))
      yield break;

    var stride = adType == 0 ? 8 : 16;
    var visited = new HashSet<(ushort PartitionReference, uint Block)>();
    var pos = adStart;
    var end = checked(adStart + lAd);

    while (true) {
      if (pos < 0 || end > _len || end < pos)
        yield break;

      (ushort PartitionReference, uint Block)? continuation = null;
      while (pos + stride <= end) {
        var raw = U32(pos);
        var extentType = (int)(raw >> ExtentTypeShift);
        var length = (long)(raw & ExtentLengthMask);
        var block = U32(pos + 4);
        var partitionReference = adType == 1 ? U16(pos + 8) : containingPartitionReference;
        pos += stride;

        if (length == 0)
          continue;

        if (extentType == 3) {
          continuation = (partitionReference, block);
          break;
        }

        yield return new(extentType, length, block, partitionReference);
      }

      if (continuation is not { } next)
        yield break;
      if (!visited.Add(next))
        throw new InvalidDataException("UDF: allocation-descriptor continuation chain contains a cycle.");

      var nextOffset = PartitionOffset(next.PartitionReference, next.Block);
      if (nextOffset < 0 || nextOffset + 24 > _len)
        throw new InvalidDataException("UDF: Allocation Extent Descriptor lies outside the image.");
      if (U16(nextOffset) != 258)
        throw new InvalidDataException("UDF: allocation-descriptor continuation does not reference an Allocation Extent Descriptor.");

      var nextLength = U32(nextOffset + 20);
      pos = nextOffset + 24;
      end = checked(pos + nextLength);
      if (end > _len)
        throw new InvalidDataException("UDF: Allocation Extent Descriptor payload lies outside the image.");
    }
  }

  private UdfFileDataLayout GetFileDataLayout(
      ushort containingPartitionReference,
      uint icbLbn,
      long informationLength) {
    if (informationLength < 0)
      return new([], "UDF file has a negative logical length.");
    if (informationLength == 0)
      return new([], null);

    long off;
    try {
      off = PartitionOffset(containingPartitionReference, icbLbn);
    } catch (Exception e) when (e is InvalidDataException or NotSupportedException or OverflowException) {
      return new([], e.Message);
    }
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
    try {
      foreach (var ad in EnumerateAllocationDescriptors(containingPartitionReference, adStart, lAd, adType)) {
        if (logicalOffset >= informationLength)
          break;

        var logicalLength = Math.Min(ad.Length, informationLength - logicalOffset);
        if (ad.ExtentType == 0) {
          if (!TryPartitionRange(ad.PartitionReference, ad.Block, logicalLength, out var physicalOffset, out var limitation))
            return new(segments, limitation);
          segments.Add(new(logicalOffset, logicalLength, physicalOffset, ZeroFill: false));
        } else if (ad.ExtentType is 1 or 2) {
          segments.Add(new(logicalOffset, logicalLength, 0, ZeroFill: true));
        } else {
          return new(segments, $"UDF allocation descriptor has unsupported extent type {ad.ExtentType}.");
        }

        logicalOffset += logicalLength;
      }
    } catch (Exception e) when (e is InvalidDataException or NotSupportedException or OverflowException) {
      return new(segments, e.Message);
    }

    if (logicalOffset != informationLength)
      return new(segments, $"UDF allocation descriptors cover {logicalOffset} of {informationLength} logical file bytes.");

    return new(segments, null);
  }

  private byte[]? ReadAllocData(
      ushort containingPartitionReference,
      long adStart,
      int lAd,
      int adType,
      long infoLength) {
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
    foreach (var ad in EnumerateAllocationDescriptors(containingPartitionReference, adStart, lAd, adType)) {
      if (ms.Length >= infoLength)
        break;

      var take = Math.Min(ad.Length, infoLength - ms.Length);
      if (take <= 0)
        continue;

      if (ad.ExtentType == 0) {
        if (!TryPartitionRange(ad.PartitionReference, ad.Block, take, out var physical, out _))
          return null;
        _img.CopyTo(physical, ms, take);
      } else if (ad.ExtentType is 1 or 2) {
        var remaining = take;
        while (remaining > 0) {
          var chunk = checked((int)Math.Min(zeroBuffer.Length, remaining));
          ms.Write(zeroBuffer, 0, chunk);
          remaining -= chunk;
        }
      } else {
        return null;
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
  public void Dispose() => _img.Dispose();

  private readonly record struct PartitionDescriptorInfo(uint StartBlock, uint BlockCount);
  private readonly record struct PartitionMapInfo(ushort? PartitionNumber, string? Limitation);

  private sealed record UdfFileDataLayout(
    IReadOnlyList<UdfDataSegment> Segments,
    string? Limitation
  );
}
