#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.ExFat;

/// <summary>
/// Reads exFAT filesystem images. Parses VBR, FAT, and directory entry sets
/// (File 0x85 + Stream Extension 0xC0 + File Name 0xC1). Supports subdirectories.
/// </summary>
public sealed class ExFatReader : IDisposable {
  private readonly byte[] _data;
  private readonly List<ExFatEntry> _entries = [];

  public IReadOnlyList<ExFatEntry> Entries => _entries;

  private int _bytesPerSector;
  private int _sectorsPerCluster;
  private int _clusterSize;
  private uint _fatOffset;       // in bytes
  private uint _fatLengthBytes;
  private uint _clusterHeapOffset; // in bytes
  private uint _clusterCount;
  private uint _rootDirCluster;

  public ExFatReader(Stream stream, bool leaveOpen = false) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < 512)
      throw new InvalidDataException("exFAT: image too small.");

    // Validate "EXFAT   " signature at offset 3
    var sig = Encoding.ASCII.GetString(_data, 3, 8);
    if (sig != "EXFAT   ")
      throw new InvalidDataException("exFAT: invalid signature.");

    // Boot signature at 510-511
    if (_data[510] != 0x55 || _data[511] != 0xAA)
      throw new InvalidDataException("exFAT: missing boot signature.");

    // Parse VBR fields
    var bytesPerSectorShift = _data[108];
    var sectorsPerClusterShift = _data[109];
    _bytesPerSector = 1 << bytesPerSectorShift;
    _sectorsPerCluster = 1 << sectorsPerClusterShift;
    _clusterSize = _bytesPerSector * _sectorsPerCluster;

    var fatOffsetSectors = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(80));
    var fatLengthSectors = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(84));
    var clusterHeapOffsetSectors = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(88));
    _clusterCount = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(92));
    _rootDirCluster = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(96));

    _fatOffset = fatOffsetSectors * (uint)_bytesPerSector;
    _fatLengthBytes = fatLengthSectors * (uint)_bytesPerSector;
    _clusterHeapOffset = clusterHeapOffsetSectors * (uint)_bytesPerSector;

    // Read root directory
    ReadDirectory(_rootDirCluster, "");
  }

  private void ReadDirectory(uint cluster, string path) {
    var dirData = ReadClusterChain(cluster);
    var entryCount = dirData.Length / 32;

    for (var i = 0; i < entryCount; i++) {
      var off = i * 32;
      var entryType = dirData[off];

      // End of directory
      if (entryType == 0x00) break;

      // File directory entry (0x85)
      if (entryType == 0x85) {
        var secondaryCount = dirData[off + 1];
        var attributes = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(off + 4));
        var isDir = (attributes & 0x10) != 0;

        // Read timestamps from the file entry
        var modTime = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(off + 12));
        var modDate = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(off + 14));
        DateTime? lastMod = null;
        if (modDate != 0) {
          try {
            lastMod = new DateTime(
              1980 + (modDate >> 9), (modDate >> 5) & 0xF, modDate & 0x1F,
              modTime >> 11, (modTime >> 5) & 0x3F, (modTime & 0x1F) * 2);
          } catch { /* ignore invalid dates */ }
        }

        // Next entry must be Stream Extension (0xC0)
        if (i + 1 >= entryCount) break;
        var streamOff = (i + 1) * 32;
        if (dirData[streamOff] != 0xC0) { i += secondaryCount; continue; }

        var nameLength = dirData[streamOff + 3];
        var validDataLength = BinaryPrimitives.ReadInt64LittleEndian(dirData.AsSpan(streamOff + 8));
        var firstCluster = BinaryPrimitives.ReadUInt32LittleEndian(dirData.AsSpan(streamOff + 20));
        var dataLength = BinaryPrimitives.ReadInt64LittleEndian(dirData.AsSpan(streamOff + 24));

        // Read file name from subsequent 0xC1 entries
        var nameBuilder = new StringBuilder();
        var nameEntriesNeeded = (nameLength + 14) / 15; // 15 chars per name entry
        for (var n = 0; n < nameEntriesNeeded && i + 2 + n < entryCount; n++) {
          var nameOff = (i + 2 + n) * 32;
          if (dirData[nameOff] != 0xC1) break;
          var charsToRead = Math.Min(15, nameLength - n * 15);
          for (var c = 0; c < charsToRead; c++) {
            var charOff = nameOff + 2 + c * 2;
            if (charOff + 2 > dirData.Length) break;
            var ch = (char)BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(charOff));
            if (ch == 0) break;
            nameBuilder.Append(ch);
          }
        }

        var name = nameBuilder.ToString();
        var fullPath = string.IsNullOrEmpty(path) ? name : $"{path}/{name}";

        _entries.Add(new ExFatEntry {
          Name = fullPath,
          Size = isDir ? 0 : dataLength,
          IsDirectory = isDir,
          LastModified = lastMod,
          FirstCluster = firstCluster,
        });

        if (isDir && firstCluster >= 2)
          ReadDirectory(firstCluster, fullPath);

        // Skip past secondary entries
        i += secondaryCount;
      }
    }
  }

  private byte[] ReadClusterChain(uint startCluster) {
    using var ms = new MemoryStream();
    var cluster = startCluster;
    var seen = new HashSet<uint>();

    while (cluster >= 2 && cluster <= _clusterCount + 1 && seen.Add(cluster)) {
      var offset = _clusterHeapOffset + (long)(cluster - 2) * _clusterSize;
      if (offset + _clusterSize > _data.Length) break;
      ms.Write(_data, (int)offset, _clusterSize);
      cluster = GetNextCluster(cluster);
    }

    return ms.ToArray();
  }

  private uint GetNextCluster(uint cluster) {
    var pos = _fatOffset + cluster * 4;
    if (pos + 4 > _data.Length) return 0xFFFFFFF8;
    var val = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan((int)pos));
    return val;
  }

  private static bool IsEndOfChain(uint cluster) => cluster >= 0xFFFFFFF8;

  public byte[] Extract(ExFatEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    if (entry.FirstCluster < 2) return [];

    var data = ReadClusterChain(entry.FirstCluster);
    if (data.Length > entry.Size)
      return data.AsSpan(0, (int)entry.Size).ToArray();
    return data;
  }

  public void Dispose() { }
}
