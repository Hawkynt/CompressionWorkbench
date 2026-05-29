#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.Layout;

namespace FileFormat.Vhd;

/// <summary>
/// Reader for Microsoft VHD images (fixed, dynamic and differencing).
/// Streams reads via <see cref="SectorCache"/> so opening a multi-TB image
/// does not load the whole file into RAM — only the footer, dynamic header,
/// BAT and (during <see cref="Extract"/>) the requested block bytes are
/// fetched on demand.
/// </summary>
public sealed class VhdReader : IDisposable {
  private static readonly byte[] Magic = "conectix"u8.ToArray();
  private static readonly byte[] DynMagic = "cxsparse"u8.ToArray();

  private readonly SectorCache _cache;
  private readonly long _streamLength;
  private readonly List<VhdEntry> _entries = [];

  // Fixed disk fields
  private long _fixedDataOffset;
  private long _fixedDataLength;

  // Dynamic disk fields
  private bool _isDynamic;
  private uint[] _bat = [];
  private int _blockSize;
  private int _sectorsPerBlock;
  private int _bitmapSectors;
  private long _virtualSize;

  public IReadOnlyList<VhdEntry> Entries => _entries;

  public VhdReader(Stream stream, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(stream);
    _streamLength = stream.Length;
    _cache = new SectorCache(stream);
    Parse();
  }

  private void Parse() {
    if (_streamLength < 512)
      throw new InvalidDataException("VHD: file too small.");

    // Footer at end of file (fixed), or copy at offset 0 (dynamic/differencing).
    var footerOff = _streamLength - 512;
    var footer = _cache.Read(footerOff, 512);
    if (!footer.AsSpan(0, 8).SequenceEqual(Magic)) {
      // Try the offset-0 copy used by dynamic/differencing disks.
      var head = _cache.Read(0, 512);
      if (head.AsSpan(0, 8).SequenceEqual(Magic)) {
        footerOff = 0;
        footer = head;
      } else {
        throw new InvalidDataException("VHD: invalid footer magic.");
      }
    }

    var diskType = BinaryPrimitives.ReadUInt32BigEndian(footer.AsSpan(60));
    _virtualSize = (long)BinaryPrimitives.ReadUInt64BigEndian(footer.AsSpan(48));
    var dataOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(footer.AsSpan(16));

    if (diskType == 2) {
      // Fixed VHD: raw data is everything before the trailing footer.
      _isDynamic = false;
      _fixedDataOffset = 0;
      _fixedDataLength = _streamLength - 512;

      _entries.Add(new VhdEntry {
        Name = "disk.img",
        Size = _fixedDataLength,
      });
    } else if (diskType is 3 or 4) {
      // Dynamic (3) or Differencing (4).
      _isDynamic = true;
      ParseDynamicHeader(dataOffset);

      _entries.Add(new VhdEntry {
        Name = "disk.img",
        Size = _virtualSize,
      });
    } else {
      throw new InvalidDataException($"VHD: unsupported disk type {diskType}.");
    }
  }

  private void ParseDynamicHeader(long headerOffset) {
    if (headerOffset < 0 || headerOffset + 1024 > _streamLength)
      throw new InvalidDataException("VHD: dynamic disk header offset out of range.");

    var hdr = _cache.Read(headerOffset, 1024);
    if (!hdr.AsSpan(0, 8).SequenceEqual(DynMagic))
      throw new InvalidDataException("VHD: invalid dynamic disk header magic (expected 'cxsparse').");

    var batOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(hdr.AsSpan(16));
    var maxBatEntries = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(28));
    _blockSize = (int)BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(32));

    if (_blockSize <= 0 || (_blockSize & (_blockSize - 1)) != 0)
      throw new InvalidDataException($"VHD: invalid block size {_blockSize} (must be a power of 2).");

    _sectorsPerBlock = _blockSize / 512;
    // Each block on disk is preceded by a sector bitmap: one bit per sector, rounded up to full sectors.
    _bitmapSectors = (_sectorsPerBlock + 512 * 8 - 1) / (512 * 8);

    // Read the BAT — this can be large (1 entry per block) so stream it through the cache
    // rather than materialising the raw bytes.
    var batByteLen = (long)maxBatEntries * 4;
    if (batOffset < 0 || batOffset + batByteLen > _streamLength)
      throw new InvalidDataException("VHD: BAT extends beyond file.");

    _bat = new uint[maxBatEntries];
    // Read in chunks to limit transient allocation for huge BATs.
    const int batChunkBytes = 64 * 1024;
    var buf = new byte[batChunkBytes];
    var remaining = batByteLen;
    var srcOff = batOffset;
    var entryIdx = 0;
    while (remaining > 0) {
      var take = (int)Math.Min(remaining, batChunkBytes);
      _cache.Read(srcOff, buf.AsSpan(0, take));
      for (var i = 0; i + 4 <= take; i += 4)
        _bat[entryIdx++] = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(i, 4));
      remaining -= take;
      srcOff += take;
    }
  }

  public byte[] Extract(VhdEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);

    if (!_isDynamic) {
      var len = (int)Math.Min(entry.Size, _streamLength - _fixedDataOffset);
      if (len <= 0) return [];
      var buf = new byte[len];
      _cache.Read(_fixedDataOffset, buf);
      return buf;
    }

    // Dynamic: assemble virtual disk from BAT, fetching each allocated block via the cache.
    var result = new byte[_virtualSize];
    for (var blockIdx = 0; blockIdx < _bat.Length; blockIdx++) {
      var batEntry = _bat[blockIdx];
      if (batEntry == 0xFFFFFFFF)
        continue; // sparse — already zeroed

      // Physical offset = BAT entry * 512 (sector address) + bitmap sectors
      var physicalOffset = (long)batEntry * 512 + _bitmapSectors * 512L;
      var virtualOffset = (long)blockIdx * _blockSize;
      var copyLen = (int)Math.Min(_blockSize, _virtualSize - virtualOffset);

      if (copyLen <= 0)
        break;

      if (physicalOffset + copyLen > _streamLength)
        continue; // truncated — leave as zeros

      _cache.Read(physicalOffset, result.AsSpan((int)virtualOffset, copyLen));
    }

    return result;
  }

  public void Dispose() => _cache.Dispose();
}
