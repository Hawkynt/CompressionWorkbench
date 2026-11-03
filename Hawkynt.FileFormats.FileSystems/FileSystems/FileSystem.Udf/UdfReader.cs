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
  public UdfReader(Stream stream, bool leaveOpen = false) : this(stream, leaveOpen, partitionMapsOnly: false) { }

  private UdfReader(Stream stream, bool leaveOpen, bool partitionMapsOnly) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek)
      stream.Position = 0;
    _img = new ImageAccessor(stream, leaveOpen);
    _len = _img.Length;
    _partitionMapsOnly = partitionMapsOnly;
    Parse();
  }

  private readonly bool _partitionMapsOnly;

  /// <summary>
  /// Whether the volume addresses blocks through a virtual, sparable or metadata partition. The
  /// in-place editors, the extent map and every maintenance operation assume plain Type 1
  /// partitions, so callers refuse to change such a volume. Unreadable volumes report false and
  /// fail on their own.
  /// </summary>
  public static bool UsesRemapping(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var position = image.CanSeek ? image.Position : 0;
    try {
      using var reader = new UdfReader(image, leaveOpen: true, partitionMapsOnly: true);
      return reader.UsesRemappedPartitions;
    } catch (Exception e) when (e is InvalidDataException or NotSupportedException or OverflowException or EndOfStreamException) {
      return false;
    } finally {
      if (image.CanSeek)
        image.Position = position;
    }
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
    if (_partitionMapsOnly)
      return;

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

  /// <summary>Whether any partition map remaps blocks (virtual, sparable or metadata): such volumes are read-only here.</summary>
  public bool UsesRemappedPartitions => _partitionMaps.Any(m => m.Kind != PartitionKind.Physical);

  /// <summary>
  /// Decodes the Logical Volume Descriptor's partition maps (ECMA-167 3/10.7, UDF 2.60 2.2.8–2.2.10).
  /// Type 1 maps address a Partition Descriptor directly. Type 2 maps are identified by their
  /// entity identifier: "*UDF Virtual Partition" (blocks go through the VAT, write-once media),
  /// "*UDF Sparable Partition" (defective packets relocated by a sparing table, rewritable media)
  /// and "*UDF Metadata Partition" (blocks live inside the metadata file, UDF 2.50+). Their
  /// tables are loaded once all maps are known, since they live in the physical partitions.
  /// </summary>
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
        _partitionMaps.Add(new PartitionMapInfo(PartitionKind.Physical, partitionNumber, limitation));
      } else if (type == 2 && length == 64) {
        _partitionMaps.Add(ParseType2Map(pos, reference));
      } else {
        _partitionMaps.Add(new PartitionMapInfo(PartitionKind.Physical, null, $"UDF partition map {reference} has unsupported type {type}."));
      }

      pos += length;
    }

    if (pos > end)
      throw new InvalidDataException("UDF: partition map table is truncated.");

    // Remapping tables are read through the physical maps, so they come last.
    foreach (var map in _partitionMaps)
      if (map.Limitation == null)
        LoadRemapping(map);
  }

  private PartitionMapInfo ParseType2Map(long pos, uint reference) {
    var identifier = Encoding.ASCII.GetString(_img.Read(pos + 5, 23)).TrimEnd('\0');
    var volumeSequenceNumber = U16(pos + 36);
    var partitionNumber = U16(pos + 38);
    string? limitation = null;
    if (volumeSequenceNumber != 1)
      limitation = $"Type 2 partition map {reference} addresses volume sequence {volumeSequenceNumber}; multi-volume UDF sets are not supported.";
    else if (!_partitions.ContainsKey(partitionNumber))
      limitation = $"Type 2 partition map {reference} names missing Partition Descriptor {partitionNumber}.";

    switch (identifier) {
      case "*UDF Virtual Partition":
        return new PartitionMapInfo(PartitionKind.Virtual, partitionNumber, limitation);
      case "*UDF Sparable Partition": {
        var packetLength = U16(pos + 40);
        var tableCount = U8(pos + 42);
        var tableSize = U32(pos + 44);
        var locations = new uint[Math.Min((int)tableCount, 4)];
        for (var i = 0; i < locations.Length; ++i)
          locations[i] = U32(pos + 48 + i * 4);
        if (packetLength == 0 || (packetLength & (packetLength - 1)) != 0)
          limitation ??= $"Sparable partition map {reference} has packet length {packetLength}, not a power of two.";
        return new PartitionMapInfo(PartitionKind.Sparable, partitionNumber, limitation) {
          PacketLength = packetLength, SparingTableLocations = locations, SparingTableSize = tableSize,
        };
      }
      case "*UDF Metadata Partition":
        return new PartitionMapInfo(PartitionKind.Metadata, partitionNumber, limitation) {
          MetadataFileLocation = U32(pos + 40), MetadataMirrorLocation = U32(pos + 44),
        };
      default:
        return new PartitionMapInfo(PartitionKind.Physical, null, $"UDF Type 2 partition map {reference} has unknown identifier '{identifier}'.");
    }
  }

  /// <summary>The reference of the map that records <paramref name="partitionNumber"/> physically (Type 1 or sparable).</summary>
  private ushort PhysicalReference(ushort partitionNumber) {
    for (var i = 0; i < _partitionMaps.Count; ++i) {
      var map = _partitionMaps[i];
      if (map.Kind is PartitionKind.Physical or PartitionKind.Sparable && map.PartitionNumber == partitionNumber && map.Limitation == null)
        return (ushort)i;
    }
    // A virtual or metadata map may stand alone over its partition: address it directly.
    return ushort.MaxValue;
  }

  private void LoadRemapping(PartitionMapInfo map) {
    try {
      switch (map.Kind) {
        case PartitionKind.Sparable:
          LoadSparingTable(map);
          break;
        case PartitionKind.Virtual:
          map.Vat = LoadVat(map.PartitionNumber!.Value);
          break;
        case PartitionKind.Metadata:
          map.MetadataRuns = LoadMetadataFile(map);
          break;
      }
    } catch (Exception e) when (e is InvalidDataException or NotSupportedException or OverflowException) {
      map.Limitation = e.Message;
    }
  }

  /// <summary>
  /// Reads the sparing table (UDF 2.60 2.2.12): a tag, "*UDF Sparing Table", then map entries of
  /// (original packet LBN within the partition, mapped absolute sector). Free (0xFFFFFFF0) and
  /// defective (0xFFFFFFF1) entries map nothing. The first copy that reads cleanly is used.
  /// </summary>
  private void LoadSparingTable(PartitionMapInfo map) {
    foreach (var location in map.SparingTableLocations) {
      var offset = (long)location * _blockSize;
      if (offset < 0 || offset + 56 > _len)
        continue;
      var id = Encoding.ASCII.GetString(_img.Read(offset + 17, 18));
      if (id != "*UDF Sparing Table")
        continue;
      var entries = U16(offset + 48);
      if (56L + entries * 8L > Math.Max(map.SparingTableSize, 56) || offset + 56 + entries * 8L > _len)
        continue;
      var table = new Dictionary<uint, uint>();
      for (var i = 0; i < entries; ++i) {
        var original = U32(offset + 56 + i * 8);
        var mapped = U32(offset + 60 + i * 8);
        if (original < 0xFFFFFFF0)
          table[original] = mapped;
      }
      map.Sparing = table;
      return;
    }
    throw new InvalidDataException("UDF: no readable sparing table for the sparable partition.");
  }

  /// <summary>
  /// Finds and reads the Virtual Allocation Table (UDF 2.60 2.2.11): its File Entry is the last
  /// one recorded, so the last blocks of the image are tried. A UDF 2.00+ VAT is a file of type
  /// 248 with a header; a 1.50 VAT ends with "*UDF Virtual Alloc Tbl" and the previous VAT ICB.
  /// </summary>
  private uint[] LoadVat(ushort partitionNumber) {
    var physical = PhysicalReference(partitionNumber);
    var partition = _partitions[partitionNumber];
    var lastBlock = _len / _blockSize - 1;
    for (var absolute = lastBlock; absolute >= Math.Max(partition.StartBlock, lastBlock - 7); --absolute) {
      var offset = absolute * _blockSize;
      var tag = U16(offset);
      if (tag is not (261 or 266) || !IsTagAt(offset, tag, (uint)(absolute - partition.StartBlock)))
        continue;
      var fileType = U8(offset + 27);
      var size = U64(offset + 56);
      if (size > 64L * 1024 * 1024)
        continue;
      var data = ReadFileEntryData(physical, offset, (long)size);
      if (data == null)
        continue;
      if (fileType == 248 && data.Length >= 152) {
        int headerLength = BinaryPrimitives.ReadUInt16LittleEndian(data);
        if (headerLength < 152 || headerLength > data.Length)
          continue;
        return ToEntries(data.AsSpan(headerLength));
      }
      if (fileType == 0 && data.Length >= 36
          && Encoding.ASCII.GetString(data, data.Length - 35, 22) == "*UDF Virtual Alloc Tbl")
        return ToEntries(data.AsSpan(0, data.Length - 36));
    }
    throw new InvalidDataException("UDF: the Virtual Allocation Table was not found at the end of the image.");

    static uint[] ToEntries(ReadOnlySpan<byte> bytes) {
      var entries = new uint[bytes.Length / 4];
      for (var i = 0; i < entries.Length; ++i)
        entries[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(i * 4)..]);
      return entries;
    }
  }

  /// <summary>
  /// Reads the metadata file's extents (UDF 2.60 2.2.13): the metadata partition's blocks are the
  /// bytes of that file, which lies in the physical partition. The mirror is used when the main
  /// copy cannot be read.
  /// </summary>
  private List<(long Logical, long Length, long Physical)> LoadMetadataFile(PartitionMapInfo map) {
    var physical = PhysicalReference(map.PartitionNumber!.Value);
    if (physical == ushort.MaxValue)
      throw new InvalidDataException("UDF: the metadata partition has no physical partition map beneath it.");
    Exception? last = null;
    foreach (var location in new[] { map.MetadataFileLocation, map.MetadataMirrorLocation }) {
      try {
        var feOffset = PartitionOffset(physical, location);
        var tag = U16(feOffset);
        if (tag is not (261 or 266) || U8(feOffset + 27) is not (250 or 251))
          throw new InvalidDataException("UDF: the metadata file location does not hold a metadata file.");
        var size = (long)U64(feOffset + 56);
        var layout = GetFileDataLayout(physical, location, size);
        if (layout.Limitation is { } limitation)
          throw new InvalidDataException(limitation);
        var runs = new List<(long, long, long)>();
        foreach (var segment in layout.Segments)
          if (!segment.ZeroFill)
            runs.Add((segment.LogicalOffset, segment.Length, segment.PhysicalOffset));
        return runs;
      } catch (Exception e) when (e is InvalidDataException or NotSupportedException or OverflowException) {
        last = e;
      }
    }
    throw new InvalidDataException("UDF: neither the metadata file nor its mirror can be read: " + last?.Message);
  }

  /// <summary>The contents of the File Entry at absolute byte <paramref name="feOffset"/>, or null when they cannot be read safely.</summary>
  private byte[]? ReadFileEntryData(ushort containingPartitionReference, long feOffset, long size) {
    var tag = U16(feOffset);
    int lEa, lAd;
    long adStart;
    if (tag == 261) {
      lEa = checked((int)U32(feOffset + 168));
      lAd = checked((int)U32(feOffset + 172));
      adStart = checked(feOffset + 176L + lEa);
    } else {
      lEa = checked((int)U32(feOffset + 208));
      lAd = checked((int)U32(feOffset + 212));
      adStart = checked(feOffset + 216L + lEa);
    }
    return ReadAllocData(containingPartitionReference, adStart, lAd, U16(feOffset + 34) & 0x07, size);
  }

  /// <summary>Translates one logical block of a partition reference to its absolute byte offset.</summary>
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

    switch (map.Kind) {
      case PartitionKind.Virtual: {
        var vat = map.Vat ?? throw new InvalidDataException("UDF: the virtual partition has no allocation table.");
        if (lbn >= vat.Length || vat[lbn] == 0xFFFFFFFF)
          throw new InvalidDataException($"UDF: virtual block {lbn} is not allocated in the VAT.");
        var physical = PhysicalReference(number);
        return physical == ushort.MaxValue
          ? checked(((long)partition.StartBlock + vat[lbn]) * _blockSize)
          : PartitionOffset(physical, vat[lbn]);
      }
      case PartitionKind.Metadata: {
        var position = checked(lbn * _blockSize);
        foreach (var (logical, length, physicalOffset) in map.MetadataRuns!)
          if (position >= logical && position < logical + length)
            return physicalOffset + (position - logical);
        throw new InvalidDataException($"UDF: metadata block {lbn} lies outside the metadata file.");
      }
    }

    if ((ulong)lbn >= partition.BlockCount)
      throw new InvalidDataException(
        $"UDF: logical block {lbn} lies outside partition reference {partitionReference} ({partition.BlockCount} blocks). ");
    if (map.Kind == PartitionKind.Sparable && map.Sparing is { Count: > 0 } sparing) {
      var packet = (uint)(lbn & ~(map.PacketLength - 1L));
      if (sparing.TryGetValue(packet, out var mapped))
        return checked(((long)mapped + (lbn - packet)) * _blockSize);
    }
    return checked(((long)partition.StartBlock + lbn) * _blockSize);
  }

  /// <summary>
  /// Maps a byte range of a partition to absolute image ranges. A plain partition gives one
  /// range; remapped ones are walked block by block and contiguous blocks merged.
  /// </summary>
  private bool TryPartitionRange(
      ushort partitionReference,
      uint block,
      long length,
      out List<(long Offset, long Length)> runs,
      out string? limitation) {
    runs = [];
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
      if (length < 0) {
        limitation = $"UDF extent [{block}, {length} bytes] has a negative length.";
        return false;
      }

      if (map.Kind == PartitionKind.Physical) {
        var partitionBytes = checked((long)partition.BlockCount * _blockSize);
        var relative = checked((long)block * _blockSize);
        if (relative > partitionBytes || length > partitionBytes - relative) {
          limitation = $"UDF extent [{block}, +{length} bytes] lies outside partition reference {partitionReference}.";
          return false;
        }
        var physicalOffset = checked((long)partition.StartBlock * _blockSize + relative);
        if (physicalOffset > _len || length > _len - physicalOffset) {
          limitation = "UDF recorded extent lies outside the backing image.";
          return false;
        }
        runs.Add((physicalOffset, length));
        return true;
      }

      for (long done = 0, lbn = block; done < length; ++lbn) {
        var take = Math.Min(_blockSize, length - done);
        var offset = PartitionOffset(partitionReference, lbn);
        if (offset < 0 || offset > _len - take) {
          limitation = "UDF recorded extent lies outside the backing image.";
          return false;
        }
        if (runs.Count > 0 && runs[^1].Offset + runs[^1].Length == offset)
          runs[^1] = (runs[^1].Offset, runs[^1].Length + take);
        else
          runs.Add((offset, take));
        done += take;
      }
      return true;
    } catch (Exception e) when (e is OverflowException or InvalidDataException or NotSupportedException) {
      limitation = e is OverflowException ? "UDF extent address overflows the image address space." : e.Message;
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
          if (!TryPartitionRange(ad.PartitionReference, ad.Block, logicalLength, out var runs, out var limitation))
            return new(segments, limitation);
          var runLogical = logicalOffset;
          foreach (var (physicalOffset, runLength) in runs) {
            segments.Add(new(runLogical, runLength, physicalOffset, ZeroFill: false));
            runLogical += runLength;
          }
        } else if (ad.ExtentType is 1 or 2) {
          segments.Add(new(logicalOffset, logicalLength, 0, ZeroFill: true));
        } else {
          return new(segments, $"UDF allocation descriptor has unsupported extent type {ad.ExtentType}.");
        }

        logicalOffset += logicalLength;
      }
    } catch (Exception e) when (e is InvalidDataException or NotSupportedException or OverflowException) {
      // Say both what went wrong and how much of the file it costs: the cause alone
      // does not tell a caller whether anything is still addressable.
      return new(segments,
        $"{e.Message} UDF allocation descriptors cover {logicalOffset} of {informationLength} logical file bytes.");
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
        if (!TryPartitionRange(ad.PartitionReference, ad.Block, take, out var runs, out _))
          return null;
        foreach (var (physical, runLength) in runs)
          _img.CopyTo(physical, ms, runLength);
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

  private enum PartitionKind { Physical, Virtual, Sparable, Metadata }

  /// <summary>One partition map and, for the remapping kinds, the table it maps through.</summary>
  private sealed class PartitionMapInfo(PartitionKind kind, ushort? partitionNumber, string? limitation) {
    public PartitionKind Kind { get; } = kind;
    public ushort? PartitionNumber { get; } = partitionNumber;
    public string? Limitation { get; set; } = limitation;
    public int PacketLength { get; init; }
    public uint[] SparingTableLocations { get; init; } = [];
    public uint SparingTableSize { get; init; }
    public uint MetadataFileLocation { get; init; }
    public uint MetadataMirrorLocation { get; init; }
    public Dictionary<uint, uint>? Sparing { get; set; }
    public uint[]? Vat { get; set; }
    public List<(long Logical, long Length, long Physical)>? MetadataRuns { get; set; }
  }

  private sealed record UdfFileDataLayout(
    IReadOnlyList<UdfDataSegment> Segments,
    string? Limitation
  );
}
