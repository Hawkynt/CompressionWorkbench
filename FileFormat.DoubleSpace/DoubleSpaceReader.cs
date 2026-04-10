#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.DoubleSpace;

/// <summary>
/// Reads Microsoft DoubleSpace/DriveSpace Compressed Volume Files (CVF).
/// Parses MDBPB header, MDFAT entries, FAT directory structure, and
/// decompresses sectors using DS LZ77 compression.
/// </summary>
public sealed class DoubleSpaceReader : IDisposable {
  private readonly byte[] _data;
  private readonly List<DoubleSpaceEntry> _entries = [];

  /// <summary>Signature found in the MDBPB: "MSDSP6.0" or "MSDSP6.2".</summary>
  public string Signature { get; private set; } = "";

  /// <summary>True if DriveSpace (6.2), false if DoubleSpace (6.0).</summary>
  public bool IsDriveSpace => Signature == "MSDSP6.2";

  public IReadOnlyList<DoubleSpaceEntry> Entries => _entries;

  // MDBPB fields
  private int _bytesPerSector;
  private int _sectorsPerCluster;
  private int _reservedSectors;
  private int _fatCount;
  private int _rootEntryCount;
  private int _totalSectors;
  private int _fatSize;
  private int _mdfatStartSector;
  private int _dataStartSector;
  private int _rootDirSectors;
  private int _firstDataSector;

  // MDFAT: maps logical sector -> physical sector + flags
  private int[]? _mdfat;

  public DoubleSpaceReader(Stream stream, bool leaveOpen = false) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < 512)
      throw new InvalidDataException("DoubleSpace: image too small.");

    // Read MDBPB signature at offset 3 (8 bytes)
    Signature = Encoding.ASCII.GetString(_data, 3, 8);
    if (Signature is not ("MSDSP6.0" or "MSDSP6.2"))
      throw new InvalidDataException($"DoubleSpace: invalid signature '{Signature}'.");

    _bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(11));
    if (_bytesPerSector is 0 or > 4096) _bytesPerSector = 512;
    _sectorsPerCluster = _data[13];
    if (_sectorsPerCluster == 0) _sectorsPerCluster = 1;
    _reservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(14));
    _fatCount = _data[16];
    if (_fatCount == 0) _fatCount = 2;
    _rootEntryCount = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(17));

    _totalSectors = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(19));
    if (_totalSectors == 0)
      _totalSectors = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(32));

    _fatSize = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(22));
    if (_fatSize == 0)
      _fatSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(36));

    // CVF-specific fields stored after standard BPB
    // MDFAT start sector at offset 44, data start sector at offset 48
    if (_data.Length >= 52) {
      _mdfatStartSector = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(44));
      _dataStartSector = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(48));
    }

    // Sanity: if MDFAT/data start look invalid, compute from standard BPB
    _rootDirSectors = (_rootEntryCount * 32 + _bytesPerSector - 1) / _bytesPerSector;
    _firstDataSector = _reservedSectors + _fatCount * _fatSize + _rootDirSectors;

    if (_mdfatStartSector <= 0 || _mdfatStartSector >= _totalSectors)
      _mdfatStartSector = _reservedSectors + _fatCount * _fatSize + _rootDirSectors;
    if (_dataStartSector <= 0 || _dataStartSector >= _totalSectors)
      _dataStartSector = _mdfatStartSector;

    // Read MDFAT
    ReadMdfat();

    // Read root directory (FAT12/16-style fixed root)
    var rootOffset = (_reservedSectors + _fatCount * _fatSize) * _bytesPerSector;
    if (rootOffset + _rootDirSectors * _bytesPerSector <= _data.Length)
      ReadDirectory(rootOffset, _rootEntryCount, "");
  }

  private void ReadMdfat() {
    var mdfatOffset = _mdfatStartSector * _bytesPerSector;
    // Each MDFAT entry is 4 bytes
    var logicalSectors = (_dataStartSector > _mdfatStartSector)
      ? (_dataStartSector - _mdfatStartSector) * _bytesPerSector / 4
      : _totalSectors;

    if (logicalSectors <= 0 || logicalSectors > 1_000_000) logicalSectors = 0;

    _mdfat = new int[logicalSectors];
    for (var i = 0; i < logicalSectors; i++) {
      var off = mdfatOffset + i * 4;
      if (off + 4 > _data.Length) break;
      _mdfat[i] = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(off));
    }
  }

  private void ReadDirectory(int offset, int maxEntries, string path) {
    for (var i = 0; i < maxEntries; i++) {
      var off = offset + i * 32;
      if (off + 32 > _data.Length) break;

      var firstByte = _data[off];
      if (firstByte == 0x00) break;
      if (firstByte == 0xE5) continue;

      var attr = _data[off + 11];
      if ((attr & 0x08) != 0) continue; // volume label
      if ((attr & 0x3F) == 0x0F) continue; // LFN entry (skip)

      var name = GetShortName(off);
      if (name is "." or "..") continue;

      var isDir = (attr & 0x10) != 0;
      var fileSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(off + 28));
      var startCluster = (int)BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(off + 26));

      var fullPath = string.IsNullOrEmpty(path) ? name : $"{path}/{name}";

      // Map cluster to sector for our MDFAT lookup
      var startSector = (_firstDataSector + (startCluster - 2) * _sectorsPerCluster);
      var sectorCount = isDir ? 0 : (fileSize + _bytesPerSector - 1) / _bytesPerSector;

      _entries.Add(new DoubleSpaceEntry {
        Name = fullPath,
        Size = isDir ? 0 : fileSize,
        IsDirectory = isDir,
        StartSector = startSector,
        SectorCount = sectorCount,
      });

      if (isDir && startCluster >= 2) {
        var dirOffset = (_firstDataSector + (startCluster - 2) * _sectorsPerCluster) * _bytesPerSector;
        if (dirOffset + 32 <= _data.Length)
          ReadDirectory(dirOffset, _bytesPerSector * _sectorsPerCluster / 32, fullPath);
      }
    }
  }

  private string GetShortName(int offset) {
    var name = Encoding.ASCII.GetString(_data, offset, 8).TrimEnd();
    var ext = Encoding.ASCII.GetString(_data, offset + 8, 3).TrimEnd();
    return string.IsNullOrEmpty(ext) ? name : $"{name}.{ext}";
  }

  /// <summary>
  /// Extracts file data, decompressing sectors via MDFAT mapping.
  /// Each sector may be stored compressed or uncompressed per MDFAT flags.
  /// </summary>
  public byte[] Extract(DoubleSpaceEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory || entry.Size == 0) return [];

    using var ms = new MemoryStream();
    for (var s = 0; s < entry.SectorCount; s++) {
      var logicalSector = entry.StartSector + s;
      var sectorData = ReadSector(logicalSector);
      ms.Write(sectorData);
    }

    var result = ms.ToArray();
    if (result.Length > entry.Size)
      return result.AsSpan(0, (int)entry.Size).ToArray();
    return result;
  }

  private byte[] ReadSector(int logicalSector) {
    // MDFAT is indexed from 0, mapping logical data sectors (starting at _firstDataSector)
    var mdfatIndex = logicalSector - _firstDataSector;
    if (_mdfat != null && mdfatIndex >= 0 && mdfatIndex < _mdfat.Length) {
      var mdfatEntry = _mdfat[mdfatIndex];
      var physSector = mdfatEntry & 0x1FFFFF; // bits 0-20
      var compSectorCount = (mdfatEntry >> 21) & 0xF; // bits 21-24
      var flags = (mdfatEntry >> 25) & 0x7; // bits 25-27

      if ((flags == 1 || flags == 2) && physSector > 0) {
        // Both compressed and uncompressed sectors are stored as DsCompression blocks
        // (the 2-byte block header distinguishes compressed vs stored internally)
        var physOffset = physSector * _bytesPerSector;
        var blockSize = compSectorCount * _bytesPerSector;
        if (blockSize == 0) blockSize = _bytesPerSector;
        if (physOffset + blockSize <= _data.Length) {
          var block = _data.AsSpan(physOffset, blockSize);
          return DsCompression.Decompress(block);
        }
      }
    }

    // Fallback: read directly at logical sector offset
    var directOffset = logicalSector * _bytesPerSector;
    if (directOffset + _bytesPerSector <= _data.Length)
      return _data.AsSpan(directOffset, _bytesPerSector).ToArray();

    return new byte[_bytesPerSector];
  }

  public void Dispose() { }
}
