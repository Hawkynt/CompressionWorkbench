#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileSystem.DriveSpace3;

/// <summary>
/// Reads DriveSpace 3 (Microsoft Plus! Pack for Windows 95, 1995) CVF images.
/// The on-disk layout is the DOS 6.x DBLSPACE/DRVSPACE MDBPB + MDFAT + BitFAT
/// + DATA chain — only the OEM name (<c>MS_DSP3</c>), CvfSignature (<c>DVR3</c>),
/// and per-cluster compression algorithm (MS LZH instead of DS LZ77) change.
/// <para>
/// Compressed runs (MDFAT flag = 2) are decoded through <see cref="MsLzhBlockCodec"/>;
/// stored runs (flag = 1) are returned verbatim. The inner FAT16 chain is
/// walked starting from each entry's first cluster, with the MDFAT indirection
/// resolving every cluster to its physical run in the DATA region. Clusters
/// without a valid MDFAT mapping fall back to the inner-data mirror, mirroring
/// the strategy used by <c>FileSystem.DoubleSpace.DoubleSpaceReader</c>.
/// </para>
/// </summary>
public sealed class DriveSpace3Reader : IDisposable {
  private readonly byte[] _data;
  private readonly List<DriveSpace3Entry> _entries = [];

  /// <summary>OEM name in the MDBPB. Always <c>MS_DSP3</c> for valid images.</summary>
  public string Signature { get; private set; } = "";

  /// <summary>Raw CvfSignature at offset 36 (<c>DVR3</c>).</summary>
  public string CvfSignature { get; private set; } = "";

  /// <summary>True once <see cref="Parse"/> has accepted the header.</summary>
  public bool ValidHeader { get; private set; }

  /// <summary>
  /// Gets the entries.
  /// </summary>
public IReadOnlyList<DriveSpace3Entry> Entries => this._entries;

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
  private int _firstDataSector;
  private byte _compressionLevel;

  private uint[]? _mdfat;

  /// <summary>Magic signature bytes (<c>MS_DSP3</c>) at offset 3.</summary>
  public static readonly byte[] Signatures = "MS_DSP3"u8.ToArray();
  /// <summary>Offset of the OEM magic signature in the MDBPB.</summary>
  public const int SignatureOffset = 3;

  /// <summary>
  /// Parses the MDBPB and inner FAT directory from <paramref name="stream"/>.
  /// Throws <see cref="InvalidDataException"/> if the image is too small or
  /// the OEM signature does not match <c>MS_DSP3</c>.
  /// </summary>
  public DriveSpace3Reader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    this._data = ms.ToArray();
    this.Parse();
  }

  private void Parse() {
    if (this._data.Length < 512)
      throw new InvalidDataException("DriveSpace 3: image too small.");

    var oem = Encoding.ASCII.GetString(this._data, 3, 7);
    if (oem != "MS_DSP3")
      throw new InvalidDataException($"DriveSpace 3: invalid OEM signature '{oem}'.");

    this.Signature = "MS_DSP3";
    this.ValidHeader = true;

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

    this.CvfSignature = Encoding.ASCII.GetString(this._data, 36, 4);
    this._mdfatStartSector = (int)BinaryPrimitives.ReadUInt32LittleEndian(this._data.AsSpan(44));
    this._mdfatLenSectors = (int)BinaryPrimitives.ReadUInt32LittleEndian(this._data.AsSpan(48));
    this._bitFatStartSector = (int)BinaryPrimitives.ReadUInt32LittleEndian(this._data.AsSpan(52));
    this._bitFatLenSectors = (int)BinaryPrimitives.ReadUInt32LittleEndian(this._data.AsSpan(56));
    this._dataStartSector = (int)BinaryPrimitives.ReadUInt32LittleEndian(this._data.AsSpan(60));
    this._dataLenSectors = (int)BinaryPrimitives.ReadUInt32LittleEndian(this._data.AsSpan(64));
    this._compressionLevel = this._data.Length > 76 ? this._data[76] : (byte)0;

    this._rootDirSectors = (this._rootEntryCount * 32 + this._bytesPerSector - 1) / this._bytesPerSector;
    this._firstDataSector = this._reservedSectors + this._fatCount * this._fatSize + this._rootDirSectors;

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

      if ((attr & 0x3F) == 0x0F) {
        var seq = firstByte & 0x3F;
        var chars = new char[13];
        int[] slots = [1, 3, 5, 7, 9, 14, 16, 18, 20, 22, 24, 28, 30];
        for (var k = 0; k < 13; k++) {
          var ch = (ushort)(this._data[off + slots[k]] | (this._data[off + slots[k] + 1] << 8));
          chars[k] = (char)ch;
        }
        while (pendingLfn.Count < seq) pendingLfn.Add("");
        pendingLfn[seq - 1] = new string(chars);
        continue;
      }

      if ((attr & 0x08) != 0) { pendingLfn.Clear(); continue; }

      var shortName = GetShortName(this._data, off);
      if (shortName is "." or "..") { pendingLfn.Clear(); continue; }

      string name = shortName;
      if (pendingLfn.Count > 0) {
        var combined = string.Concat(pendingLfn);
        var endIdx = combined.IndexOfAny(['\0', '￿']);
        if (endIdx >= 0) combined = combined[..endIdx];
        if (combined.Length > 0) name = combined;
        pendingLfn.Clear();
      }

      var isDir = (attr & 0x10) != 0;
      var fileSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(this._data.AsSpan(off + 28));
      var startCluster = BinaryPrimitives.ReadUInt16LittleEndian(this._data.AsSpan(off + 26));

      var fullPath = string.IsNullOrEmpty(path) ? name : $"{path}/{name}";

      this._entries.Add(new DriveSpace3Entry {
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
  /// Extracts the bytes of <paramref name="entry"/> by walking the inner FAT
  /// chain from its first cluster, resolving each cluster through the MDFAT
  /// indirection and decoding the corresponding stored or MS LZH run.
  /// </summary>
  public byte[] Extract(DriveSpace3Entry entry) {
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
      if (cluster is 0 or >= 0xFFF8 and <= 0xFFFF) break;
    }

    return ms.ToArray();
  }

  private int ReadInnerFatEntry(int cluster) {
    var fatOffset = this._reservedSectors * this._bytesPerSector;
    var entryOffset = fatOffset + cluster * 2;
    if (entryOffset + 2 > this._data.Length) return 0xFFFF;
    return BinaryPrimitives.ReadUInt16LittleEndian(this._data.AsSpan(entryOffset));
  }

  private byte[] ReadCluster(int cluster) {
    var clusterBytes = this._bytesPerSector * this._sectorsPerCluster;

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
            return MsLzhBlockCodec.Decompress(block);
          } catch (InvalidDataException) {
            // Fall through to inner-volume read below.
          }
        }
      }
    }

    var innerOffset = (this._firstDataSector + (cluster - 2) * this._sectorsPerCluster) * this._bytesPerSector;
    if (innerOffset + clusterBytes <= this._data.Length)
      return this._data.AsSpan(innerOffset, clusterBytes).ToArray();

    return new byte[clusterBytes];
  }

  /// <summary>
  /// Surfaces high-level metadata about the parsed CVF for tooling/UI
  /// inspection. Returns an INI-style key=value block.
  /// </summary>
  public byte[] BuildSurfaceMetadata() {
    var b = new StringBuilder();
    b.Append("parse_status=").Append(this.ValidHeader ? "ok" : "invalid").Append('\n');
    b.Append("format=DriveSpace 3 CVF\n");
    b.Append("oem_signature=").Append(this.Signature).Append('\n');
    b.Append("cvf_signature=").Append(this.CvfSignature).Append('\n');
    b.Append(CultureInfo.InvariantCulture, $"compression_level={this._compressionLevel}\n");
    b.Append(CultureInfo.InvariantCulture, $"sectors_per_cluster={this._sectorsPerCluster}\n");
    b.Append(CultureInfo.InvariantCulture, $"mdfat_start_sector={this._mdfatStartSector}\n");
    b.Append(CultureInfo.InvariantCulture, $"mdfat_len_sectors={this._mdfatLenSectors}\n");
    b.Append(CultureInfo.InvariantCulture, $"bitfat_start_sector={this._bitFatStartSector}\n");
    b.Append(CultureInfo.InvariantCulture, $"bitfat_len_sectors={this._bitFatLenSectors}\n");
    b.Append(CultureInfo.InvariantCulture, $"data_start_sector={this._dataStartSector}\n");
    b.Append(CultureInfo.InvariantCulture, $"data_len_sectors={this._dataLenSectors}\n");
    return Encoding.UTF8.GetBytes(b.ToString());
  }

  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
public void Dispose() { }
}
