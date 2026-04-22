#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.DoubleSpace;

/// <summary>
/// Reads Microsoft DoubleSpace / DriveSpace Compressed Volume Files (CVF).
/// <para>
/// The MDBPB (offset 0) starts with a standard FAT BPB (first 36 bytes) and
/// is followed by CVF-specific fields at offset 36 (CvfSignature, CvfVersion,
/// MdfatStart/Len, BitFatStart/Len, DataStart/Len). The reader follows the
/// MDFAT indirection when available and falls back to the inline inner data
/// region otherwise.
/// </para>
/// </summary>
public sealed class DoubleSpaceReader : IDisposable {
  private readonly byte[] _data;
  private readonly List<DoubleSpaceEntry> _entries = [];

  /// <summary>OEM name in the MDBPB: <c>MSDSP6.0</c>, <c>MSDSP6.2</c>, or <c>DRVSPACE</c>.</summary>
  public string Signature { get; private set; } = "";

  /// <summary>Raw CvfSignature at offset 36 (<c>DBLS</c> or <c>DVRS</c>).</summary>
  public string CvfSignature { get; private set; } = "";

  /// <summary>True if DriveSpace (any), false if DoubleSpace 6.0.</summary>
  public bool IsDriveSpace => this.Signature != "MSDSP6.0";

  public IReadOnlyList<DoubleSpaceEntry> Entries => this._entries;

  // MDBPB fields
  private int _bytesPerSector;
  private int _sectorsPerCluster;
  private int _reservedSectors;
  private int _fatCount;
  private int _rootEntryCount;
  private int _totalSectors;
  private int _fatSize;
  private int _mdfatStartSector;
  private int _mdfatLenSectors;
  private int _bitFatStartSector;
  private int _bitFatLenSectors;
  private int _dataStartSector;
  private int _dataLenSectors;
  private int _rootDirSectors;
  private int _firstDataSector; // inner FAT volume's first data sector

  // MDFAT: cluster index -> packed entry.
  // Packed entry format:
  //   bits 0..20   physical sector offset within DATA region
  //   bits 21..27  run length in sectors
  //   bits 28..31  flags: 1 = stored, 2 = compressed, 0 = free
  private uint[]? _mdfat;

  public DoubleSpaceReader(Stream stream, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(stream);
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    this._data = ms.ToArray();
    this.Parse();
  }

  private void Parse() {
    if (this._data.Length < 512)
      throw new InvalidDataException("DoubleSpace: image too small.");

    // OEM name (8 bytes at offset 3).
    this.Signature = Encoding.ASCII.GetString(this._data, 3, 8);
    if (this.Signature is not ("MSDSP6.0" or "MSDSP6.2" or "DRVSPACE"))
      throw new InvalidDataException($"DoubleSpace: invalid OEM signature '{this.Signature}'.");

    this._bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(this._data.AsSpan(11));
    if (this._bytesPerSector is 0 or > 4096) this._bytesPerSector = 512;
    this._sectorsPerCluster = this._data[13];
    if (this._sectorsPerCluster == 0) this._sectorsPerCluster = 1;
    this._reservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(this._data.AsSpan(14));
    if (this._reservedSectors == 0) this._reservedSectors = 1;
    this._fatCount = this._data[16];
    if (this._fatCount == 0) this._fatCount = 2;
    this._rootEntryCount = BinaryPrimitives.ReadUInt16LittleEndian(this._data.AsSpan(17));

    this._totalSectors = BinaryPrimitives.ReadUInt16LittleEndian(this._data.AsSpan(19));
    if (this._totalSectors == 0)
      this._totalSectors = (int)BinaryPrimitives.ReadUInt32LittleEndian(this._data.AsSpan(32));

    this._fatSize = BinaryPrimitives.ReadUInt16LittleEndian(this._data.AsSpan(22));

    // CVF-specific fields at offset 36 onwards.
    this.CvfSignature = Encoding.ASCII.GetString(this._data, 36, 4);
    this._mdfatStartSector = (int)BinaryPrimitives.ReadUInt32LittleEndian(this._data.AsSpan(44));
    this._mdfatLenSectors = (int)BinaryPrimitives.ReadUInt32LittleEndian(this._data.AsSpan(48));
    this._bitFatStartSector = (int)BinaryPrimitives.ReadUInt32LittleEndian(this._data.AsSpan(52));
    this._bitFatLenSectors = (int)BinaryPrimitives.ReadUInt32LittleEndian(this._data.AsSpan(56));
    this._dataStartSector = (int)BinaryPrimitives.ReadUInt32LittleEndian(this._data.AsSpan(60));
    this._dataLenSectors = (int)BinaryPrimitives.ReadUInt32LittleEndian(this._data.AsSpan(64));

    this._rootDirSectors = (this._rootEntryCount * 32 + this._bytesPerSector - 1) / this._bytesPerSector;
    this._firstDataSector = this._reservedSectors + this._fatCount * this._fatSize + this._rootDirSectors;

    // Read MDFAT if present.
    if (this._mdfatStartSector > 0
        && this._mdfatLenSectors > 0
        && this._mdfatStartSector < this._totalSectors) {
      var entryCount = this._mdfatLenSectors * this._bytesPerSector / 4;
      this._mdfat = new uint[entryCount];
      var baseOffset = this._mdfatStartSector * this._bytesPerSector;
      for (var i = 0; i < entryCount; i++) {
        var off = baseOffset + i * 4;
        if (off + 4 > this._data.Length) break;
        this._mdfat[i] = BinaryPrimitives.ReadUInt32LittleEndian(this._data.AsSpan(off));
      }
    }

    // Parse the inner FAT12/16 root directory.
    var rootOffset = (this._reservedSectors + this._fatCount * this._fatSize) * this._bytesPerSector;
    if (rootOffset + this._rootDirSectors * this._bytesPerSector <= this._data.Length)
      this.ReadDirectory(rootOffset, this._rootEntryCount, "");
  }

  private void ReadDirectory(int offset, int maxEntries, string path) {
    var pendingLfn = new List<string>();

    for (var i = 0; i < maxEntries; i++) {
      var off = offset + i * 32;
      if (off + 32 > this._data.Length) break;

      var firstByte = this._data[off];
      if (firstByte == 0x00) break;
      if (firstByte == 0xE5) { pendingLfn.Clear(); continue; }

      var attr = this._data[off + 11];

      // VFAT LFN entry (attribute 0x0F) — accumulate.
      if ((attr & 0x3F) == 0x0F) {
        var seq = firstByte & 0x3F;
        var chars = new char[13];
        int[] slots = [1, 3, 5, 7, 9, 14, 16, 18, 20, 22, 24, 28, 30];
        for (var k = 0; k < 13; k++) {
          var ch = (ushort)(this._data[off + slots[k]] | (this._data[off + slots[k] + 1] << 8));
          chars[k] = (char)ch;
        }
        // LFN entries come in reverse order (last-first written first on disk).
        // We collect by sequence; reassembly happens on the next 8.3 entry.
        while (pendingLfn.Count < seq) pendingLfn.Add("");
        pendingLfn[seq - 1] = new string(chars);
        continue;
      }

      if ((attr & 0x08) != 0) { pendingLfn.Clear(); continue; } // volume label

      var shortName = GetShortName(this._data, off);
      if (shortName is "." or "..") { pendingLfn.Clear(); continue; }

      // Reassemble LFN if present.
      string name = shortName;
      if (pendingLfn.Count > 0) {
        var combined = string.Concat(pendingLfn);
        // Strip NUL and 0xFFFF padding.
        var endIdx = combined.IndexOfAny(['\0', '\uFFFF']);
        if (endIdx >= 0) combined = combined[..endIdx];
        if (combined.Length > 0) name = combined;
        pendingLfn.Clear();
      }

      var isDir = (attr & 0x10) != 0;
      var fileSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(this._data.AsSpan(off + 28));
      var startCluster = BinaryPrimitives.ReadUInt16LittleEndian(this._data.AsSpan(off + 26));

      var fullPath = string.IsNullOrEmpty(path) ? name : $"{path}/{name}";

      this._entries.Add(new DoubleSpaceEntry {
        Name = fullPath,
        Size = isDir ? 0 : fileSize,
        IsDirectory = isDir,
        StartCluster = startCluster,
        SectorCount = isDir ? 0 : (fileSize + this._bytesPerSector - 1) / this._bytesPerSector,
      });

      if (isDir && startCluster >= 2) {
        var dirOffset = (this._firstDataSector + (startCluster - 2) * this._sectorsPerCluster) * this._bytesPerSector;
        var dirSize = this._bytesPerSector * this._sectorsPerCluster / 32;
        if (dirOffset + 32 <= this._data.Length)
          this.ReadDirectory(dirOffset, dirSize, fullPath);
      }
    }
  }

  private static string GetShortName(byte[] data, int offset) {
    var name = Encoding.ASCII.GetString(data, offset, 8).TrimEnd();
    var ext = Encoding.ASCII.GetString(data, offset + 8, 3).TrimEnd();
    return string.IsNullOrEmpty(ext) ? name : $"{name}.{ext}";
  }

  /// <summary>
  /// Extracts file data. Traverses the inner FAT chain starting from the
  /// file's first cluster, resolves each cluster through the MDFAT (when
  /// available) to its compressed run in the DATA region, and decompresses.
  /// Falls back to the inner data region for clusters with no MDFAT mapping.
  /// </summary>
  public byte[] Extract(DoubleSpaceEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory || entry.Size == 0) return [];

    var clusterBytes = this._bytesPerSector * this._sectorsPerCluster;
    using var ms = new MemoryStream();

    var cluster = entry.StartCluster;
    var remaining = entry.Size;
    var safety = 1_000_000;
    while (cluster >= 2 && remaining > 0 && safety-- > 0) {
      var clusterData = this.ReadCluster(cluster);
      var take = (int)Math.Min(remaining, clusterData.Length);
      ms.Write(clusterData, 0, take);
      remaining -= take;

      cluster = this.ReadInnerFatEntry(cluster);
      // EoC markers for FAT16 = 0xFFF8..0xFFFF; also treat 0 as end.
      if (cluster is 0 or >= 0xFFF8 and <= 0xFFFF) break;
    }

    return ms.ToArray();
  }

  private int ReadInnerFatEntry(int cluster) {
    // FAT16 (we always produce FAT16) — 2 bytes per entry.
    var fatOffset = this._reservedSectors * this._bytesPerSector;
    var entryOffset = fatOffset + cluster * 2;
    if (entryOffset + 2 > this._data.Length) return 0xFFFF;
    return BinaryPrimitives.ReadUInt16LittleEndian(this._data.AsSpan(entryOffset));
  }

  private byte[] ReadCluster(int cluster) {
    var clusterBytes = this._bytesPerSector * this._sectorsPerCluster;

    // Try MDFAT indirection first.
    if (this._mdfat != null && cluster < this._mdfat.Length) {
      var entry = this._mdfat[cluster];
      var physSector = (int)(entry & 0x1FFFFFu);
      var runSectors = (int)((entry >> 21) & 0x7Fu);
      var flags = (int)((entry >> 28) & 0xFu);
      if (flags is 1 or 2 && runSectors > 0) {
        var absoluteSector = this._dataStartSector + physSector;
        var physOffset = absoluteSector * this._bytesPerSector;
        var blockSize = runSectors * this._bytesPerSector;
        if (physOffset + blockSize <= this._data.Length) {
          var block = this._data.AsSpan(physOffset, blockSize);
          try {
            return DsCompression.Decompress(block);
          } catch (InvalidDataException) {
            // Fall through to inner-volume read below.
          }
        }
      }
    }

    // Fallback — read from the inner FAT data region directly.
    var innerOffset = (this._firstDataSector + (cluster - 2) * this._sectorsPerCluster) * this._bytesPerSector;
    if (innerOffset + clusterBytes <= this._data.Length)
      return this._data.AsSpan(innerOffset, clusterBytes).ToArray();

    return new byte[clusterBytes];
  }

  public void Dispose() { }
}
