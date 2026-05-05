#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Vhd;

public sealed class VhdReader : IDisposable {
  private static readonly byte[] Magic = "conectix"u8.ToArray();
  private static readonly byte[] DynMagic = "cxsparse"u8.ToArray();

  private readonly byte[] _data;
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
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < 512)
      throw new InvalidDataException("VHD: file too small.");

    // Footer at end of file (fixed), or copy at offset 0 (dynamic/differencing)
    var footerOff = _data.Length - 512;
    if (!_data.AsSpan(footerOff, 8).SequenceEqual(Magic)) {
      if (_data.AsSpan(0, 8).SequenceEqual(Magic))
        footerOff = 0;
      else
        throw new InvalidDataException("VHD: invalid footer magic.");
    }

    var diskType = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(footerOff + 60));
    _virtualSize = (long)BinaryPrimitives.ReadUInt64BigEndian(_data.AsSpan(footerOff + 48));
    var dataOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(_data.AsSpan(footerOff + 16));

    if (diskType == 2) {
      // Fixed VHD: raw data is everything before the trailing footer
      _isDynamic = false;
      _fixedDataOffset = 0;
      _fixedDataLength = _data.Length - 512;

      _entries.Add(new VhdEntry {
        Name = "disk.img",
        Size = _fixedDataLength,
      });
    } else if (diskType is 3 or 4) {
      // Dynamic (3) or Differencing (4)
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
    if (headerOffset < 0 || headerOffset + 1024 > _data.Length)
      throw new InvalidDataException("VHD: dynamic disk header offset out of range.");

    var hdr = _data.AsSpan((int)headerOffset);
    if (!hdr[..8].SequenceEqual(DynMagic))
      throw new InvalidDataException("VHD: invalid dynamic disk header magic (expected 'cxsparse').");

    var batOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(hdr[16..]);
    var maxBatEntries = BinaryPrimitives.ReadUInt32BigEndian(hdr[28..]);
    _blockSize = (int)BinaryPrimitives.ReadUInt32BigEndian(hdr[32..]);

    if (_blockSize <= 0 || (_blockSize & (_blockSize - 1)) != 0)
      throw new InvalidDataException($"VHD: invalid block size {_blockSize} (must be a power of 2).");

    _sectorsPerBlock = _blockSize / 512;
    // Each block on disk is preceded by a sector bitmap: one bit per sector, rounded up to full sectors
    _bitmapSectors = (_sectorsPerBlock + 512 * 8 - 1) / (512 * 8);

    // Read the BAT
    var batByteLen = (long)maxBatEntries * 4;
    if (batOffset < 0 || batOffset + batByteLen > _data.Length)
      throw new InvalidDataException("VHD: BAT extends beyond file.");

    _bat = new uint[maxBatEntries];
    for (var i = 0; i < (int)maxBatEntries; i++)
      _bat[i] = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan((int)(batOffset + i * 4L)));
  }

  public byte[] Extract(VhdEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);

    if (!_isDynamic) {
      var len = (int)Math.Min(entry.Size, _data.Length - _fixedDataOffset);
      if (len <= 0) return [];
      return _data.AsSpan((int)_fixedDataOffset, len).ToArray();
    }

    // Dynamic: assemble virtual disk from BAT
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

      if (physicalOffset + copyLen > _data.Length)
        continue; // truncated — leave as zeros

      _data.AsSpan((int)physicalOffset, copyLen).CopyTo(result.AsSpan((int)virtualOffset));
    }

    return result;
  }

  public void Dispose() { }
}
