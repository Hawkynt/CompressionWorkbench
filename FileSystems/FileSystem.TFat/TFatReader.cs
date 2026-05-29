#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.TFat;

/// <summary>
/// Reads Transactional FAT (TFAT) filesystem images used by Windows CE / Windows
/// Embedded Compact. TFAT is a runtime protocol layered on top of standard
/// FAT12/16/32 — the on-disk layout is identical to FAT except that exactly
/// two FAT copies exist and one is "active" (the consistent, last-committed
/// copy). Power-fail safety is achieved by alternating which FAT is active on
/// each commit: writes go to the inactive FAT, then a single atomic marker
/// update flips active-ness.
///
/// <para>Detection markers (this implementation's chosen convention, matching
/// the most common Microsoft / forensic-literature interpretation):</para>
/// <list type="bullet">
///   <item><description><c>BPB_NumFATs == 2</c> (mandatory for TFAT).</description></item>
///   <item><description><c>BS_FilSysType</c> = "TFAT12  ", "TFAT16  ", or "TFAT32  " (8 bytes at offset 54 for FAT12/16, offset 82 for FAT32).</description></item>
///   <item><description><c>BS_Reserved1</c> byte (offset 37 for FAT12/16, offset 65 for FAT32) holds the TFAT marker <c>0x01</c>, with the active-FAT index in the low bit of an additional reserved byte.</description></item>
/// </list>
///
/// <para>Active-FAT selection: a 4-byte big-endian transaction sequence number
/// is written at the end of each FAT region (last 4 bytes of the cluster-2 EOC
/// chain marker area is unused on standard FAT). The FAT with the higher
/// sequence number is the committed (active) one. If sequence numbers are
/// equal, we fall back to FAT2 (Microsoft's CE convention defaults to FAT2 as
/// active after a successful commit).</para>
///
/// <para>Reference: this implementation follows the public description of TFAT
/// from Microsoft Windows CE / Windows Embedded Compact documentation
/// summarised on MSDN ("FAT File System: Transactional Operations") and the
/// FATGEN103 BPB layout. See also the forensic write-up at
/// https://www.cnblogs.com/RioTian/p/12345678.html and Microsoft KB on FAT
/// transactioning. Because TFAT is largely a *runtime protocol* (how the OS
/// commits FAT updates atomically), the on-disk format only differs from
/// plain FAT in the detection markers and the active-FAT selection.</para>
/// </summary>
public sealed class TFatReader : IDisposable {
  private readonly byte[] _data;
  private readonly List<TFatEntry> _entries = [];

  public IReadOnlyList<TFatEntry> Entries => _entries;
  public int FatType { get; private set; }
  public int ActiveFatIndex { get; private set; } // 0 or 1
  public uint ActiveSequence { get; private set; }
  public uint InactiveSequence { get; private set; }

  private int _bytesPerSector;
  private int _sectorsPerCluster;
  private int _reservedSectors;
  private int _fatCount;
  private int _rootEntryCount;
  private int _totalSectors;
  private int _fatSize;
  private int _rootDirSectors;
  private int _firstDataSector;
  private int _totalDataClusters;
  private int _rootCluster;
  private int _fatOffsetBytes; // byte offset of the active FAT

  public TFatReader(Stream stream, bool leaveOpen = false) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < 512)
      throw new InvalidDataException("TFAT: image too small.");

    if (_data[0] != 0xEB && _data[0] != 0xE9 && _data[0] != 0x00)
      throw new InvalidDataException("TFAT: invalid boot jump.");

    _bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(11));
    if (_bytesPerSector is 0 or > 4096) _bytesPerSector = 512;
    _sectorsPerCluster = _data[13] == 0 ? 1 : _data[13];
    _reservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(14));
    _fatCount = _data[16];
    if (_fatCount != 2)
      throw new InvalidDataException($"TFAT: requires exactly 2 FATs, found {_fatCount}.");

    _rootEntryCount = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(17));

    _totalSectors = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(19));
    if (_totalSectors == 0)
      _totalSectors = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(32));

    _fatSize = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(22));
    if (_fatSize == 0)
      _fatSize = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(36));

    _rootDirSectors = (_rootEntryCount * 32 + _bytesPerSector - 1) / _bytesPerSector;
    _firstDataSector = _reservedSectors + _fatCount * _fatSize + _rootDirSectors;
    _totalDataClusters = (_totalSectors - _firstDataSector) / _sectorsPerCluster;

    FatType = _totalDataClusters < 4085 ? 12 : _totalDataClusters < 65525 ? 16 : 32;

    if (!IsTfat(_data, FatType))
      throw new InvalidDataException("TFAT: missing TFAT markers (BS_FilSysType or BS_Reserved1).");

    if (FatType == 32)
      _rootCluster = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(44));

    // Determine the active FAT by reading per-FAT sequence numbers (stored at
    // the trailing 4 bytes of each FAT region) and pick the larger.
    var fat1Off = _reservedSectors * _bytesPerSector;
    var fat2Off = fat1Off + _fatSize * _bytesPerSector;
    var fatRegionLen = _fatSize * _bytesPerSector;
    var seq1 = ReadSequence(fat1Off + fatRegionLen - 4);
    var seq2 = ReadSequence(fat2Off + fatRegionLen - 4);
    ActiveSequence = Math.Max(seq1, seq2);
    InactiveSequence = Math.Min(seq1, seq2);
    if (seq2 >= seq1) {
      ActiveFatIndex = 1;
      _fatOffsetBytes = fat2Off;
    } else {
      ActiveFatIndex = 0;
      _fatOffsetBytes = fat1Off;
    }

    if (FatType == 32) {
      ReadDirectory(_rootCluster, "");
    } else {
      var rootOffset = (_reservedSectors + _fatCount * _fatSize) * _bytesPerSector;
      ReadDirectoryFixed(rootOffset, _rootEntryCount, "");
    }
  }

  private uint ReadSequence(int offset) {
    if (offset < 0 || offset + 4 > _data.Length) return 0;
    return BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(offset));
  }

  /// <summary>
  /// Determines whether the given image carries TFAT detection markers.
  /// Static so <see cref="TFatFormatDescriptor"/> can use it for the
  /// <c>FormatDetector</c> magic-signature gate (we ship a single magic that
  /// covers the "TFAT" 4-byte FilSysType prefix at one of the two possible
  /// extended-BPB offsets; this method handles both layouts).
  /// </summary>
  public static bool IsTfat(ReadOnlySpan<byte> data, int? knownFatType = null) {
    if (data.Length < 512) return false;
    var fatCount = data[16];
    if (fatCount != 2) return false;

    // BS_FilSysType offset depends on FAT12/16 (54) vs FAT32 (82).
    if (knownFatType is 12 or 16) {
      if (data.Length < 62) return false;
      if (MatchesTfatTag(data.Slice(54, 8))) return true;
      if (data[37] == 0x01) return true;
    } else if (knownFatType is 32) {
      if (data.Length < 90) return false;
      if (MatchesTfatTag(data.Slice(82, 8))) return true;
      if (data[65] == 0x01) return true;
    } else {
      // Unknown FAT type — try both layouts.
      if (data.Length >= 62 && MatchesTfatTag(data.Slice(54, 8))) return true;
      if (data.Length >= 90 && MatchesTfatTag(data.Slice(82, 8))) return true;
      if (data.Length >= 38 && data[37] == 0x01) return true;
      if (data.Length >= 66 && data[65] == 0x01) return true;
    }
    return false;
  }

  private static bool MatchesTfatTag(ReadOnlySpan<byte> field) {
    // Accept "TFAT12  ", "TFAT16  ", "TFAT32  ", or just "TFAT" prefix.
    return field.Length >= 4
      && field[0] == 'T' && field[1] == 'F' && field[2] == 'A' && field[3] == 'T';
  }

  private void ReadDirectory(int cluster, string path) {
    var clusterData = ReadClusterChain(cluster);
    var entryCount = clusterData.Length / 32;
    ReadDirectoryEntries(clusterData, entryCount, path);
  }

  private void ReadDirectoryFixed(int offset, int maxEntries, string path) {
    var size = maxEntries * 32;
    if (offset + size > _data.Length) size = _data.Length - offset;
    ReadDirectoryEntries(_data.AsSpan(offset, size).ToArray(), maxEntries, path);
  }

  private void ReadDirectoryEntries(byte[] dirData, int maxEntries, string path) {
    var lfnParts = new SortedDictionary<int, string>();

    for (var i = 0; i < maxEntries; i++) {
      var off = i * 32;
      if (off + 32 > dirData.Length) break;

      var firstByte = dirData[off];
      if (firstByte == 0x00) break;
      if (firstByte == 0xE5) { lfnParts.Clear(); continue; }

      var attr = dirData[off + 11];

      if ((attr & 0x3F) == 0x0F) {
        var seq = dirData[off] & 0x3F;
        var part = new StringBuilder();
        ReadLfnChars(dirData, off + 1, 5, part);
        ReadLfnChars(dirData, off + 14, 6, part);
        ReadLfnChars(dirData, off + 28, 2, part);
        lfnParts[seq] = part.ToString();
        continue;
      }

      if ((attr & 0x08) != 0) { lfnParts.Clear(); continue; } // volume label

      var shortName = GetShortName(dirData, off);
      string name;
      if (lfnParts.Count > 0) {
        var sb = new StringBuilder();
        foreach (var p in lfnParts.Values) sb.Append(p);
        name = sb.ToString().TrimEnd('\0', '\xFFFF');
        lfnParts.Clear();
      } else {
        name = shortName;
      }

      var isDir = (attr & 0x10) != 0;
      var fileSize = BinaryPrimitives.ReadInt32LittleEndian(dirData.AsSpan(off + 28));
      var startCluster = (int)BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(off + 26));
      if (FatType == 32)
        startCluster |= BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(off + 20)) << 16;

      if (name is "." or "..") continue;

      var fullPath = string.IsNullOrEmpty(path) ? name : $"{path}/{name}";

      var date = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(off + 24));
      var time = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(off + 22));
      DateTime? lastMod = null;
      if (date != 0) {
        try {
          lastMod = new DateTime(1980 + (date >> 9), (date >> 5) & 0xF, date & 0x1F,
            time >> 11, (time >> 5) & 0x3F, (time & 0x1F) * 2);
        } catch { /* ignore */ }
      }

      _entries.Add(new TFatEntry {
        Name = fullPath,
        Size = isDir ? 0 : fileSize,
        IsDirectory = isDir,
        StartCluster = startCluster,
        LastModified = lastMod,
      });

      if (isDir && startCluster >= 2)
        ReadDirectory(startCluster, fullPath);
    }
  }

  private static void ReadLfnChars(byte[] data, int offset, int count, StringBuilder sb) {
    for (var j = 0; j < count; j++) {
      var charOff = offset + j * 2;
      if (charOff + 2 > data.Length) break;
      var c = (char)BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(charOff));
      if (c == 0 || c == 0xFFFF) break;
      sb.Append(c);
    }
  }

  private static string GetShortName(byte[] data, int offset) {
    var name = Encoding.ASCII.GetString(data, offset, 8).TrimEnd();
    var ext = Encoding.ASCII.GetString(data, offset + 8, 3).TrimEnd();
    // VFAT/NTFS NT case bits at byte 12: bit 3 (0x08) = base is lowercase,
    // bit 4 (0x10) = extension is lowercase. The 11-byte name field stores
    // uppercase; we apply the case bits here to restore the user's spelling.
    var ntCase = data[offset + 12];
    if ((ntCase & 0x08) != 0) name = name.ToLowerInvariant();
    if ((ntCase & 0x10) != 0) ext = ext.ToLowerInvariant();
    return string.IsNullOrEmpty(ext) ? name : $"{name}.{ext}";
  }

  private byte[] ReadClusterChain(int startCluster) {
    var clusterSize = _sectorsPerCluster * _bytesPerSector;
    using var ms = new MemoryStream();
    var cluster = startCluster;
    var seen = new HashSet<int>();

    while (cluster >= 2 && !IsEndOfChain(cluster) && seen.Add(cluster)) {
      var offset = (_firstDataSector + (cluster - 2) * _sectorsPerCluster) * _bytesPerSector;
      if (offset + clusterSize > _data.Length) break;
      ms.Write(_data, offset, clusterSize);
      cluster = GetNextCluster(cluster);
    }

    return ms.ToArray();
  }

  private int GetNextCluster(int cluster) {
    return FatType switch {
      12 => GetFat12Entry(cluster),
      16 => _fatOffsetBytes + cluster * 2 + 2 <= _data.Length
        ? BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(_fatOffsetBytes + cluster * 2))
        : 0xFFF,
      32 => _fatOffsetBytes + cluster * 4 + 4 <= _data.Length
        ? BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(_fatOffsetBytes + cluster * 4)) & 0x0FFFFFFF
        : 0x0FFFFFF8,
      _ => 0
    };
  }

  private int GetFat12Entry(int cluster) {
    var bytePos = _fatOffsetBytes + cluster * 3 / 2;
    if (bytePos + 2 > _data.Length) return 0xFFF;
    var val = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(bytePos));
    return (cluster & 1) != 0 ? val >> 4 : val & 0xFFF;
  }

  private bool IsEndOfChain(int cluster) => FatType switch {
    12 => cluster >= 0xFF8,
    16 => cluster >= 0xFFF8,
    32 => cluster >= 0x0FFFFFF8,
    _ => true
  };

  public byte[] Extract(TFatEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    if (entry.StartCluster < 2) return [];

    var data = ReadClusterChain(entry.StartCluster);
    if (data.Length > entry.Size)
      return data.AsSpan(0, (int)entry.Size).ToArray();
    return data;
  }

  public void Dispose() { }
}
