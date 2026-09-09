#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Layout;

namespace FileFormat.Vdi;

/// <summary>
/// Reads VirtualBox Disk Image (VDI) files. Allocated map entries address data
/// blocks; VDI_DISCARDED is guaranteed zero, while VDI_UNALLOCATED is tracked
/// separately so canonical maintenance can refuse to manufacture semantics for
/// an undefined never-allocated block.
/// </summary>
public sealed class VdiReader : IDisposable {
  public const uint VdiSignature = 0xBEDA107F;

  private readonly SectorCache _cache;
  private readonly long _streamLength;
  private uint[] _blockMap = [];

  public long VirtualSize { get; private set; }
  public uint BlockSize { get; private set; }
  public uint BlockCount { get; private set; }
  public uint AllocatedBlockCount { get; private set; }
  public uint OffsetBlocks { get; private set; }
  public uint OffsetData { get; private set; }
  public uint ImageType { get; private set; }

  /// <summary>
  /// True when the map contains VDI_UNALLOCATED rather than VDI_DISCARDED.
  /// Those entries are not guaranteed to represent zero bytes by the format,
  /// so a canonical rebuild must not silently turn them into discarded zeros.
  /// </summary>
  public bool HasUndefinedBlocks { get; private set; }

  public VdiReader(Stream stream, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(stream);
    this._streamLength = stream.Length;
    this._cache = new SectorCache(stream);
    this.Parse();
  }

  private void Parse() {
    if (this._streamLength < 512)
      throw new InvalidDataException("VDI: file too small.");

    var header = this._cache.Read(0, 512);
    var signature = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(64));
    if (signature != VdiSignature)
      throw new InvalidDataException($"VDI: invalid signature 0x{signature:X8}, expected 0x{VdiSignature:X8}.");

    this.ImageType = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(76));
    this.OffsetBlocks = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(340));
    this.OffsetData = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(344));
    var rawVirtualSize = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(368));
    if (rawVirtualSize > long.MaxValue)
      throw new InvalidDataException("VDI: virtual disk size exceeds the supported signed range.");
    this.VirtualSize = (long)rawVirtualSize;
    this.BlockSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(376));
    this.BlockCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(384));
    this.AllocatedBlockCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(388));

    if (this.BlockSize == 0)
      throw new InvalidDataException("VDI: block size is zero.");
    if (this.BlockCount > int.MaxValue)
      throw new NotSupportedException("VDI: block map exceeds the current managed-array limit.");

    var mapByteLength = checked((long)this.BlockCount * 4);
    if ((long)this.OffsetBlocks + mapByteLength > this._streamLength)
      throw new InvalidDataException("VDI: block allocation map extends beyond file.");

    this._blockMap = new uint[checked((int)this.BlockCount)];
    if (this.BlockCount == 0) return;

    var bytes = new byte[checked((int)mapByteLength)];
    this._cache.Read(this.OffsetBlocks, bytes);
    for (var i = 0; i < this._blockMap.Length; ++i) {
      var entry = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4, 4));
      this._blockMap[i] = entry;
      if (entry == VdiWriter.UnallocatedBlock)
        this.HasUndefinedBlocks = true;
    }
  }

  public byte[] ExtractDisk() {
    if (this.VirtualSize == 0) return [];
    if (this.VirtualSize > int.MaxValue)
      throw new NotSupportedException("VDI buffered extraction currently supports guest disks up to 2 GiB.");

    var result = new byte[checked((int)this.VirtualSize)];
    for (var i = 0; i < this._blockMap.Length; ++i) {
      var mapEntry = this._blockMap[i];
      if (mapEntry >= VdiWriter.DiscardedBlock)
        continue;

      var sourceOffset = checked((long)this.OffsetData + (long)mapEntry * this.BlockSize);
      var destinationOffset = checked((long)i * this.BlockSize);
      if (destinationOffset >= this.VirtualSize) break;
      var copyLength = checked((int)Math.Min((long)this.BlockSize, this.VirtualSize - destinationOffset));
      if (sourceOffset < 0 || sourceOffset + copyLength > this._streamLength)
        throw new InvalidDataException($"VDI: allocated block {i} extends beyond the container.");

      this._cache.Read(sourceOffset, result.AsSpan(checked((int)destinationOffset), copyLength));
    }

    return result;
  }

  public void Dispose() => this._cache.Dispose();
}
