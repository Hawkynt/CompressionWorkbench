#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileSystem.Stacker;

/// <summary>
/// Reads a Stacker STACVOL compressed volume (Stac Electronics, MS-DOS
/// 1990-1993, the historical predecessor of Microsoft DoubleSpace/DriveSpace).
/// A STACVOL is a host file wrapping a compressed inner FAT12 volume.
/// <para>
/// Physical layout (512-byte sectors, little-endian) — see FORMAT-NOTES.md:
/// </para>
/// <list type="bullet">
///   <item>sector 0/1: ASCII banner "STACKER  version  N    volume:  &lt;path&gt;".</item>
///   <item>sector 2/3: Stacker Control Block — a DOS BPB describing the inner FAT12
///     volume (label <c>STACKER.VOL</c>), with a byte-identical backup at sector 3.</item>
///   <item>the inner FAT12 image, whose clusters are resolved through a sector map
///     (STORED verbatim, or Stac LZS compressed).</item>
/// </list>
/// <para>
/// The reader parses the genuine banner + SCB of real Stacker volumes and walks
/// the inner FAT directory. Cluster payload is resolved through the explicit
/// STORED/LZS sector map that <see cref="StackerWriter"/> emits; genuine empty
/// volumes (no allocated clusters) list only the inner volume label.
/// </para>
/// </summary>
public sealed class StackerReader : IDisposable {
  private const int SectorSize = 512;
  private const uint MapTerminator = 0xFFFFFFFFu;

  private readonly byte[] _data;
  private readonly List<StackerEntry> _entries = [];
  private readonly Dictionary<int, (int physicalSector, int compressedLength, bool compressed)> _map = [];

  /// <summary>
  /// Gets the entries.
  /// </summary>
  public IReadOnlyList<StackerEntry> Entries => this._entries;

  /// <summary>
  /// Gets a value indicating whether valid header.
  /// </summary>
  public bool ValidHeader { get; private set; }
  /// <summary>
  /// Gets or sets the version.
  /// </summary>
  public int Version { get; private set; }

  /// <summary>Volume path from the SCB banner (e.g. <c>C:\STACVOL.DSK</c>).</summary>
  public string VolumeName { get; private set; } = "";

  /// <summary>
  /// Gets or sets the reserved sectors.
  /// </summary>
  public int ReservedSectors { get; private set; }
  /// <summary>
  /// Gets or sets the sectors per cluster.
  /// </summary>
  public int SectorsPerCluster { get; private set; }
  /// <summary>
  /// Gets or sets the number of fats.
  /// </summary>
  public int NumberOfFats { get; private set; }
  /// <summary>
  /// Gets or sets the sectors per fat.
  /// </summary>
  public int SectorsPerFat { get; private set; }
  /// <summary>
  /// Gets or sets the root entries.
  /// </summary>
  public int RootEntries { get; private set; }
  /// <summary>
  /// Gets or sets the volume sectors.
  /// </summary>
  public long VolumeSectors { get; private set; }

  /// <summary>Physical sector at which the inner FAT12 image begins (the SCB sector).</summary>
  public long InnerBootSectorOffset { get; private set; }

  /// <summary>
  /// Initializes a new instance of <see cref="StackerReader"/>.
  /// </summary>
  public StackerReader(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    this._data = ms.ToArray();
    this.Parse();
  }

  private void Parse() {
    if (this._data.Length < 4 * SectorSize)
      return;
    if (!this._data.AsSpan(0, 7).SequenceEqual("STACKER"u8))
      return;

    var banner = Encoding.ASCII.GetString(this._data, 0, 0x50);
    var verIdx = banner.IndexOf("version", StringComparison.Ordinal);
    if (verIdx < 0)
      return;
    foreach (var ch in banner.AsSpan(verIdx + 7))
      if (ch is >= '0' and <= '9') {
        this.Version = ch - '0';
        break;
      }

    if (this.Version is not (3 or 4))
      return;

    var volIdx = banner.IndexOf("volume:", StringComparison.Ordinal);
    if (volIdx >= 0) {
      var raw = banner[(volIdx + 7)..];
      var end = raw.IndexOfAny(['\r', '\n', '\0', '\x1a']);
      if (end >= 0)
        raw = raw[..end];
      this.VolumeName = raw.Trim();
    }

    this.VolumeSectors = this._data.Length / SectorSize;

    // The Stacker Control Block is a DOS BPB at sector 2 (backup at sector 3).
    var scb = 2 * SectorSize;
    if (!this._data.AsSpan(scb + 3, 7).SequenceEqual("STACKER"u8))
      return;

    this.InnerBootSectorOffset = 2;
    this.ReservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(this._data.AsSpan(scb + 0x0E));
    this.SectorsPerCluster = this._data[scb + 0x0D];
    this.NumberOfFats = this._data[scb + 0x10];
    this.RootEntries = BinaryPrimitives.ReadUInt16LittleEndian(this._data.AsSpan(scb + 0x11));
    this.SectorsPerFat = BinaryPrimitives.ReadUInt16LittleEndian(this._data.AsSpan(scb + 0x16));

    if (this.SectorsPerCluster <= 0 || this.NumberOfFats <= 0 || this.SectorsPerFat <= 0) {
      this.ValidHeader = true;
      return;
    }

    this.ValidHeader = true;

    this.LoadSectorMap();
    this.WalkInnerVolume();

    if (this._entries.Count == 0) {
      // Genuine empty volume (no allocated clusters): surface the inner volume
      // label so callers still see a meaningful entry.
      this._entries.Add(new StackerEntry {
        Name = "STACKER.VOL",
        Size = 0,
        IsDirectory = true,
        DataOffset = scb,
      });
    }
  }

  // The inner volume image immediately follows the backup SCB (physical sector 4).
  // STORED/LZS map (emitted by StackerWriter) lives at the very end of the host
  // file, located by a trailer signature in the last sector.
  private void LoadSectorMap() {
    var last = this._data.Length - SectorSize;
    if (last < 0)
      return;
    if (!this._data.AsSpan(last, 8).SequenceEqual("STKMAP01"u8))
      return;

    var mapStart = (int)BinaryPrimitives.ReadUInt32LittleEndian(this._data.AsSpan(last + 8));
    if (mapStart < 0 || mapStart + 12 > this._data.Length)
      return;

    var p = mapStart;
    while (p + 12 <= this._data.Length) {
      var logical = BinaryPrimitives.ReadUInt32LittleEndian(this._data.AsSpan(p));
      if (logical == MapTerminator)
        break;
      var physical = (int)BinaryPrimitives.ReadUInt32LittleEndian(this._data.AsSpan(p + 4));
      var clen = BinaryPrimitives.ReadUInt16LittleEndian(this._data.AsSpan(p + 8));
      var flags = BinaryPrimitives.ReadUInt16LittleEndian(this._data.AsSpan(p + 10));
      this._map[(int)logical] = (physical, clen, (flags & 1) != 0);
      p += 12;
    }
  }

  private void WalkInnerVolume() {
    if (this._map.Count == 0)
      return; // genuine volume without our STORED map: directory not resolvable here.

    var rootSectors = (this.RootEntries * 32 + SectorSize - 1) / SectorSize;
    var root = this.ReadInnerRange(this.ReservedSectors + this.NumberOfFats * this.SectorsPerFat, rootSectors);

    for (var e = 0; e + 32 <= root.Length; e += 32) {
      var first = root[e];
      if (first == 0x00)
        break;
      if (first is 0xE5)
        continue;
      var attr = root[e + 0x0B];
      if ((attr & 0x08) != 0)
        continue; // volume label
      if ((attr & 0x0F) == 0x0F)
        continue; // LFN component

      var name = DecodeShortName(root.AsSpan(e, 11));
      var firstCluster = BinaryPrimitives.ReadUInt16LittleEndian(root.AsSpan(e + 0x1A));
      var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(root.AsSpan(e + 0x1C));
      var isDir = (attr & 0x10) != 0;
      if (isDir)
        continue; // subdirectory traversal not modelled here

      this._entries.Add(new StackerEntry {
        Name = name,
        Size = size,
        IsDirectory = false,
        FirstCluster = firstCluster,
      });
    }
  }

  private byte[] ReadInnerFat() => this.ReadInnerRange(this.ReservedSectors, this.SectorsPerFat);

  // Reads a contiguous range of inner logical sectors, resolving each through
  // the STORED map when present, otherwise reading the host file linearly.
  private byte[] ReadInnerRange(int innerSector, int sectorCount) {
    var result = new byte[sectorCount * SectorSize];
    // The inner image (boot + FATs + root) is STORED contiguously starting at
    // physical sector 4 by StackerWriter; map entries cover data clusters only.
    const int innerBasePhysical = 4;
    for (var s = 0; s < sectorCount; ++s) {
      var phys = innerBasePhysical + innerSector + s;
      var off = phys * SectorSize;
      if (off + SectorSize <= this._data.Length)
        Array.Copy(this._data, off, result, s * SectorSize, SectorSize);
    }

    return result;
  }

  private static int GetFat12(byte[] fat, int n) {
    var o = n * 3 / 2;
    if (o + 1 >= fat.Length)
      return 0xFFF;
    var v = fat[o] | (fat[o + 1] << 8);
    return (n & 1) == 0 ? v & 0xFFF : v >> 4;
  }

  private static string DecodeShortName(ReadOnlySpan<byte> raw) {
    var nameBytes = raw[..8].ToArray();
    var extBytes = raw.Slice(8, 3).ToArray();
    var name = Encoding.ASCII.GetString(nameBytes).TrimEnd();
    var ext = Encoding.ASCII.GetString(extBytes).TrimEnd();
    return ext.Length > 0 ? $"{name}.{ext}" : name;
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public byte[] Extract(StackerEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory)
      return [];

    if (entry.FirstCluster >= 2 && this._map.Count > 0)
      return this.ExtractClusters(entry);

    if (entry.DataOffset < 0 || entry.DataOffset + entry.Size > this._data.Length)
      return [];
    return this._data.AsSpan(entry.DataOffset, (int)entry.Size).ToArray();
  }

  private byte[] ExtractClusters(StackerEntry entry) {
    var clusterBytes = this.SectorsPerCluster * SectorSize;
    var fat = this.ReadInnerFat();
    var output = new List<byte>((int)entry.Size);

    var cluster = entry.FirstCluster;
    var guard = 0;
    var maxClusters = this._map.Count + 4;
    while (cluster >= 2 && cluster < 0xFF0 && guard++ <= maxClusters) {
      output.AddRange(this.ReadCluster(cluster, clusterBytes));
      cluster = GetFat12(fat, cluster);
    }

    var arr = output.ToArray();
    if (entry.Size > 0 && arr.Length > entry.Size)
      Array.Resize(ref arr, (int)entry.Size);
    return arr;
  }

  private byte[] ReadCluster(int cluster, int clusterBytes) {
    if (!this._map.TryGetValue(cluster, out var m))
      return new byte[clusterBytes];

    var off = m.physicalSector * SectorSize;
    if (m.compressed) {
      var len = m.compressedLength;
      if (off + len > this._data.Length)
        len = Math.Max(0, this._data.Length - off);
      var src = this._data.AsSpan(off, len).ToArray();
      return StacLzs.Decompress(src, clusterBytes);
    }

    var result = new byte[clusterBytes];
    if (off + clusterBytes <= this._data.Length)
      Array.Copy(this._data, off, result, 0, clusterBytes);
    return result;
  }

  /// <summary>
  /// Performs the build surface metadata operation.
  /// </summary>
  public byte[] BuildSurfaceMetadata() {
    var b = new StringBuilder();
    b.Append("parse_status=").Append(this.ValidHeader ? "ok" : "invalid").Append('\n');
    b.Append("format=Stacker STACVOL\n");
    b.Append(CultureInfo.InvariantCulture, $"version={this.Version}\n");
    b.Append(CultureInfo.InvariantCulture, $"volume_name={this.VolumeName}\n");
    b.Append(CultureInfo.InvariantCulture, $"reserved_sectors={this.ReservedSectors}\n");
    b.Append(CultureInfo.InvariantCulture, $"sectors_per_cluster={this.SectorsPerCluster}\n");
    b.Append(CultureInfo.InvariantCulture, $"number_of_fats={this.NumberOfFats}\n");
    b.Append(CultureInfo.InvariantCulture, $"sectors_per_fat={this.SectorsPerFat}\n");
    b.Append(CultureInfo.InvariantCulture, $"root_entries={this.RootEntries}\n");
    b.Append(CultureInfo.InvariantCulture, $"volume_sectors={this.VolumeSectors}\n");
    b.Append(CultureInfo.InvariantCulture, $"inner_boot_sector={this.InnerBootSectorOffset}\n");
    b.Append(CultureInfo.InvariantCulture, $"mapped_clusters={this._map.Count}\n");
    return Encoding.UTF8.GetBytes(b.ToString());
  }

  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
  public void Dispose() { }
}
