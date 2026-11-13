#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.FileSystems.Cvf;

namespace FileSystem.DoubleSpace;

/// <summary>
/// Reads a real MS-DOS 6.x DoubleSpace / DriveSpace compressed volume file (OEM name
/// <c>MSDBL6.0</c> or <c>MSDBL6.2</c>) — the general reader behind <see cref="GenuineCvfReader"/>,
/// used for real volumes whose geometry or directory tree the latter does not cover
/// (subdirectories, long file names, other cluster sizes).
/// <para>
/// The MDBPB at sector 0 points (word at 0x27) to the embedded inner FAT12 volume, whose boot
/// sector carries the same <c>MSDBL</c> name; the MDFAT (sector word at 0x24 plus one, index base
/// word at 0x2D) maps each inner cluster to its physical run. A stored run is read verbatim, a
/// compressed one is decoded by <see cref="CvfLzCodec"/> from its method header.
/// </para>
/// </summary>
public sealed class DoubleSpaceReader : IDisposable {
  private readonly byte[] _data;
  private readonly List<DoubleSpaceEntry> _entries = [];

  private int _bytesPerSector;
  private int _sectorsPerCluster;
  private int _reservedSectors;
  private int _fatCount;
  private int _rootEntryCount;
  private int _totalSectors;
  private int _fatSize;
  private int _rootDirSectors;
  private int _firstDataSector;  // inner volume's first data sector
  private int _innerBaseSector;  // base sector of the embedded inner FAT volume
  private int _mdfatBaseOffset;  // byte offset of MDFAT[0]

  /// <summary>OEM name in the MDBPB: <c>MSDBL6.0</c> or <c>MSDBL6.2</c>.</summary>
  public string Signature { get; private set; } = "";

  /// <summary>
  /// Gets the entries.
  /// </summary>
  public IReadOnlyList<DoubleSpaceEntry> Entries => this._entries;

  /// <summary>
  /// Initializes a new instance of <see cref="DoubleSpaceReader"/>.
  /// </summary>
  /// <exception cref="InvalidDataException">Not a real DoubleSpace/DriveSpace volume.</exception>
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

    this.Signature = Encoding.ASCII.GetString(this._data, 3, 8);
    if (this.Signature is not ("MSDBL6.0" or "MSDBL6.2"))
      throw new InvalidDataException($"DoubleSpace: '{this.Signature}' is not a DoubleSpace/DriveSpace OEM name (MSDBL6.0/MSDBL6.2).");

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

    // The inner volume's boot sector sits at the base recorded at 0x27 and carries the same name.
    var candidate = BinaryPrimitives.ReadUInt16LittleEndian(this._data.AsSpan(0x27));
    var candOff = candidate * this._bytesPerSector;
    if (candidate == 0 || candidate >= this._totalSectors || candOff + 11 > this._data.Length
        || Encoding.ASCII.GetString(this._data, candOff + 3, 5) != "MSDBL")
      throw new InvalidDataException("DoubleSpace: no inner volume at the base sector the MDBPB records.");
    this._innerBaseSector = candidate;

    var mdfatStartSec = BinaryPrimitives.ReadUInt16LittleEndian(this._data.AsSpan(0x24)) + 1;
    var idxOff = BinaryPrimitives.ReadUInt16LittleEndian(this._data.AsSpan(0x2D));
    this._mdfatBaseOffset = mdfatStartSec * this._bytesPerSector + idxOff * 4;
    if (this._mdfatBaseOffset >= this._data.Length)
      throw new InvalidDataException("DoubleSpace: MDFAT lies past the end of the image.");

    this._rootDirSectors = (this._rootEntryCount * 32 + this._bytesPerSector - 1) / this._bytesPerSector;
    this._firstDataSector = this._innerBaseSector + this._reservedSectors + this._fatCount * this._fatSize + this._rootDirSectors;

    var rootOffset = (this._innerBaseSector + this._reservedSectors + this._fatCount * this._fatSize) * this._bytesPerSector;
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
      var name = shortName;
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
  /// Extracts file data: walks the inner FAT chain from the file's first cluster and resolves each
  /// cluster through the MDFAT to its stored or compressed run.
  /// </summary>
  public byte[] Extract(DoubleSpaceEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory || entry.Size == 0) return [];

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
      if (cluster is 0 or >= 0xFFF8) break;
    }

    return ms.ToArray();
  }

  // Real inner volumes are FAT12; end-of-chain is normalised to the 0xFFF8.. range.
  private int ReadInnerFatEntry(int cluster) {
    var o = (this._innerBaseSector + this._reservedSectors) * this._bytesPerSector + cluster * 3 / 2;
    if (o + 2 > this._data.Length) return 0xFFFF;
    var v = this._data[o] | (this._data[o + 1] << 8);
    var e = (cluster & 1) == 0 ? v & 0xFFF : v >> 4;
    return e >= 0xFF8 ? 0xFFFF : e;
  }

  // MDFAT entry bits: 0..20 physical sector - 1, 22..25 on-disk sectors - 1, 26..29 cluster
  // sectors - 1, 30 stored, 31 used.
  private byte[] ReadCluster(int cluster) {
    var clusterBytes = this._bytesPerSector * this._sectorsPerCluster;
    var entryOff = this._mdfatBaseOffset + cluster * 4;
    if (entryOff + 4 > this._data.Length) return new byte[clusterBytes];
    var entry = BinaryPrimitives.ReadUInt32LittleEndian(this._data.AsSpan(entryOff));
    if ((entry & 0x8000_0000u) == 0) return new byte[clusterBytes];

    var physOffset = (int)((entry & 0x1FFFFFu) + 1) * this._bytesPerSector;
    if ((entry & 0x4000_0000u) != 0)
      return physOffset + clusterBytes <= this._data.Length
        ? this._data.AsSpan(physOffset, clusterBytes).ToArray()
        : throw new InvalidDataException($"DoubleSpace: stored cluster {cluster} lies past the end of the image.");

    var inLen = (int)(((entry >> 22) & 0xF) + 1) * this._bytesPerSector;
    var outLen = Math.Min(clusterBytes, (int)(((entry >> 26) & 0xF) + 1) * this._bytesPerSector);
    if (physOffset + inLen > this._data.Length)
      throw new InvalidDataException($"DoubleSpace: compressed cluster {cluster} lies past the end of the image.");
    if (this._data[physOffset] == (byte)'S' && this._data[physOffset + 1] == (byte)'Q')
      throw new NotSupportedException($"DoubleSpace: cluster {cluster} holds an SQ-0-0 payload, which only DriveSpace 3 volumes carry.");
    var buffer = new byte[clusterBytes];
    CvfLzCodec.Decompress(this._data.AsSpan(physOffset, inLen).ToArray(), inLen, outLen).CopyTo(buffer, 0);
    return buffer;
  }

  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
  public void Dispose() { }
}
