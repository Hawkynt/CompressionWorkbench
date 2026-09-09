#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.Layout;

namespace FileFormat.Vhd;

/// <summary>
/// Reader for standalone Microsoft VHD images (fixed and dynamic).
/// Dynamic data blocks are resolved through both the BAT and the per-sector
/// bitmap; a cleared bitmap bit is sparse in a dynamic VHD and therefore reads
/// as zero rather than exposing stale bytes from the allocated block body.
/// Differencing images are rejected until their parent chain can be resolved.
/// </summary>
public sealed class VhdReader : IDisposable {
  private static readonly byte[] Magic = "conectix"u8.ToArray();
  private static readonly byte[] DynMagic = "cxsparse"u8.ToArray();

  private readonly SectorCache _cache;
  private readonly long _streamLength;
  private readonly List<VhdEntry> _entries = [];

  private long _fixedDataOffset;
  private long _fixedDataLength;

  private bool _isDynamic;
  private uint[] _bat = [];
  private int _blockSize;
  private int _sectorsPerBlock;
  private int _bitmapSectors;
  private long _virtualSize;

  public IReadOnlyList<VhdEntry> Entries => this._entries;

  public VhdReader(Stream stream, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(stream);
    this._streamLength = stream.Length;
    this._cache = new SectorCache(stream);
    this.Parse();
  }

  private void Parse() {
    if (this._streamLength < 512)
      throw new InvalidDataException("VHD: file too small.");

    var footerOffset = this._streamLength - 512;
    var footer = this._cache.Read(footerOffset, 512);
    if (!footer.AsSpan(0, 8).SequenceEqual(Magic)) {
      var head = this._cache.Read(0, 512);
      if (!head.AsSpan(0, 8).SequenceEqual(Magic))
        throw new InvalidDataException("VHD: invalid footer magic.");
      footerOffset = 0;
      footer = head;
    }

    var diskType = BinaryPrimitives.ReadUInt32BigEndian(footer.AsSpan(60));
    var rawVirtualSize = BinaryPrimitives.ReadUInt64BigEndian(footer.AsSpan(48));
    if (rawVirtualSize > long.MaxValue)
      throw new InvalidDataException("VHD: virtual disk size exceeds the supported signed range.");
    this._virtualSize = (long)rawVirtualSize;
    var rawDataOffset = BinaryPrimitives.ReadUInt64BigEndian(footer.AsSpan(16));

    if (diskType == 2) {
      var availableData = this._streamLength - 512;
      if (availableData < this._virtualSize)
        throw new InvalidDataException(
          $"VHD: fixed disk is truncated; footer declares {this._virtualSize} guest bytes but only {availableData} are present.");
      this._isDynamic = false;
      this._fixedDataOffset = 0;
      this._fixedDataLength = this._virtualSize;
      this._entries.Add(new VhdEntry { Name = "disk.img", Size = this._virtualSize });
      return;
    }

    if (diskType == 4)
      throw new NotSupportedException("VHD: differencing disks require parent-chain resolution, which is not supported.");
    if (diskType != 3)
      throw new InvalidDataException($"VHD: unsupported disk type {diskType}.");
    if (rawDataOffset > long.MaxValue)
      throw new InvalidDataException("VHD: dynamic-header offset exceeds the supported range.");

    this._isDynamic = true;
    this.ParseDynamicHeader((long)rawDataOffset);
    this._entries.Add(new VhdEntry { Name = "disk.img", Size = this._virtualSize });
  }

  private void ParseDynamicHeader(long headerOffset) {
    if (headerOffset < 0 || headerOffset + 1024 > this._streamLength)
      throw new InvalidDataException("VHD: dynamic disk header offset out of range.");

    var header = this._cache.Read(headerOffset, 1024);
    if (!header.AsSpan(0, 8).SequenceEqual(DynMagic))
      throw new InvalidDataException("VHD: invalid dynamic disk header magic (expected 'cxsparse').");

    var rawBatOffset = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(16));
    if (rawBatOffset > long.MaxValue)
      throw new InvalidDataException("VHD: BAT offset exceeds the supported range.");
    var batOffset = (long)rawBatOffset;
    var maxBatEntries = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(28));
    var rawBlockSize = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(32));
    if (rawBlockSize > int.MaxValue)
      throw new InvalidDataException("VHD: block size exceeds the supported range.");
    this._blockSize = (int)rawBlockSize;

    if (this._blockSize <= 0 || (this._blockSize & (this._blockSize - 1)) != 0 || this._blockSize % 512 != 0)
      throw new InvalidDataException($"VHD: invalid block size {this._blockSize}.");
    if (maxBatEntries > int.MaxValue)
      throw new NotSupportedException("VHD: BAT exceeds the current managed-array limit.");

    this._sectorsPerBlock = this._blockSize / 512;
    this._bitmapSectors = (this._sectorsPerBlock + 4095) / 4096;

    var batByteLength = checked((long)maxBatEntries * 4);
    if (batOffset < 0 || batOffset + batByteLength > this._streamLength)
      throw new InvalidDataException("VHD: BAT extends beyond file.");

    this._bat = new uint[checked((int)maxBatEntries)];
    const int chunkSize = 64 * 1024;
    var chunk = new byte[chunkSize];
    var remaining = batByteLength;
    var sourceOffset = batOffset;
    var entryIndex = 0;
    while (remaining > 0) {
      var take = (int)Math.Min(remaining, chunk.Length);
      this._cache.Read(sourceOffset, chunk.AsSpan(0, take));
      for (var i = 0; i + 4 <= take; i += 4)
        this._bat[entryIndex++] = BinaryPrimitives.ReadUInt32BigEndian(chunk.AsSpan(i, 4));
      remaining -= take;
      sourceOffset += take;
    }
  }

  public byte[] Extract(VhdEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Size > int.MaxValue)
      throw new NotSupportedException("VHD buffered extraction currently supports guest disks up to 2 GiB.");

    if (!this._isDynamic) {
      var length = checked((int)this._fixedDataLength);
      if (length <= 0) return [];
      var result = new byte[checked((int)entry.Size)];
      this._cache.Read(this._fixedDataOffset, result.AsSpan(0, length));
      return result;
    }

    var disk = new byte[checked((int)this._virtualSize)];
    var bitmapByteLength = checked(this._bitmapSectors * 512);
    var bitmap = new byte[bitmapByteLength];

    for (var blockIndex = 0; blockIndex < this._bat.Length; ++blockIndex) {
      var batEntry = this._bat[blockIndex];
      if (batEntry == 0xFFFF_FFFFu)
        continue;

      var bitmapOffset = checked((long)batEntry * 512);
      var dataOffset = checked(bitmapOffset + bitmapByteLength);
      var virtualOffset = checked((long)blockIndex * this._blockSize);
      if (virtualOffset >= this._virtualSize) break;
      var blockLength = checked((int)Math.Min(this._blockSize, this._virtualSize - virtualOffset));

      if (bitmapOffset < 0 || bitmapOffset + bitmapByteLength > this._streamLength)
        throw new InvalidDataException($"VHD: block {blockIndex} sector bitmap extends beyond the file.");
      this._cache.Read(bitmapOffset, bitmap);

      var sectorCount = (blockLength + 511) / 512;
      for (var sector = 0; sector < sectorCount; ++sector) {
        // VHD stores the first sector in the MSB of the first bitmap byte.
        var mask = 1 << (7 - (sector & 7));
        if ((bitmap[sector >> 3] & mask) == 0)
          continue;

        var destination = checked(virtualOffset + sector * 512L);
        var length = checked((int)Math.Min(512, this._virtualSize - destination));
        var source = checked(dataOffset + sector * 512L);
        if (source < 0 || source + length > this._streamLength)
          throw new InvalidDataException($"VHD: block {blockIndex} sector {sector} extends beyond the file.");
        this._cache.Read(source, disk.AsSpan(checked((int)destination), length));
      }
    }

    return disk;
  }

  public void Dispose() => this._cache.Dispose();
}
