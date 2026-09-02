#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Stacker;

/// <summary>
/// Emits a Stacker STACVOL that <see cref="StackerReader"/> round-trips
/// byte-exact. The container reproduces the genuine banner + Stacker Control
/// Block (BPB) and a real inner FAT12 image; file payload is laid out as
/// STORED or Stac-LZS clusters tracked by the explicit STKMAP01 sector map
/// documented in FORMAT-NOTES.md. Incompressible data is stored verbatim.
/// </summary>
public sealed class StackerWriter {
  private const int SectorSize = 512;
  private const int InnerBasePhysical = 4;
  private const uint MapTerminator = 0xFFFFFFFFu;

  private readonly List<(string name, byte[] data)> _files = [];

    /// <summary>
  /// Gets or sets the sectors per cluster.
  /// </summary>
public int SectorsPerCluster { get; init; } = 4;
    /// <summary>
  /// Gets or sets the version.
  /// </summary>
public int Version { get; init; } = 3;
    /// <summary>
  /// Gets or sets the volume path.
  /// </summary>
public string VolumePath { get; init; } = "C:\\STACVOL.DSK";

  /// <summary>When true, clusters are LZS-compressed if that shrinks them; else STORED.</summary>
  public bool Compress { get; init; } = true;

    /// <summary>
  /// Performs the add file operation.
  /// </summary>
public void AddFile(string name, byte[] data) {
    ArgumentException.ThrowIfNullOrEmpty(name);
    ArgumentNullException.ThrowIfNull(data);
    this._files.Add((name.ToUpperInvariant(), data));
  }

    /// <summary>
  /// Performs the build operation.
  /// </summary>
public byte[] Build() {
    var clusterBytes = this.SectorsPerCluster * SectorSize;
    const int reserved = 1;
    const int numFats = 2;
    const int rootEntries = 512;
    var rootSectors = (rootEntries * 32 + SectorSize - 1) / SectorSize;

    // Allocate inner clusters and FAT chains.
    var fileFirstCluster = new int[this._files.Count];
    var clusterToData = new List<byte[]>(); // index 0 => cluster 2
    var nextCluster = 2;

    var fileClusters = new List<List<int>>();
    for (var f = 0; f < this._files.Count; ++f) {
      var data = this._files[f].data;
      var clusters = new List<int>();
      var offset = 0;
      if (data.Length == 0) {
        fileFirstCluster[f] = 0;
        fileClusters.Add(clusters);
        continue;
      }

      while (offset < data.Length) {
        var chunk = new byte[clusterBytes];
        var take = Math.Min(clusterBytes, data.Length - offset);
        Array.Copy(data, offset, chunk, 0, take);
        clusterToData.Add(chunk);
        clusters.Add(nextCluster++);
        offset += clusterBytes;
      }

      fileFirstCluster[f] = clusters[0];
      fileClusters.Add(clusters);
    }

    var totalClusters = nextCluster - 2;
    var maxCluster = nextCluster; // exclusive

    // Sectors-per-FAT large enough for the cluster count (FAT12).
    var fatEntries = maxCluster + 1;
    var fatBytes = (fatEntries * 3 + 1) / 2;
    var sectorsPerFat = Math.Max(1, (fatBytes + SectorSize - 1) / SectorSize);

    // Build the FAT (two identical copies).
    var fat = new byte[sectorsPerFat * SectorSize];
    SetFat12(fat, 0, 0xFF8);
    SetFat12(fat, 1, 0xFFF);
    foreach (var clusters in fileClusters)
      for (var i = 0; i < clusters.Count; ++i)
        SetFat12(fat, clusters[i], i + 1 < clusters.Count ? clusters[i + 1] : 0xFFF);

    // Build the root directory.
    var root = new byte[rootSectors * SectorSize];
    WriteVolumeLabel(root, 0);
    var dirIdx = 1;
    for (var f = 0; f < this._files.Count; ++f) {
      WriteDirEntry(root, dirIdx++, this._files[f].name, fileFirstCluster[f], this._files[f].data.Length);
    }

    var innerImageSectors = reserved + numFats * sectorsPerFat + rootSectors;
    var innerTotalSectors = innerImageSectors + totalClusters * this.SectorsPerCluster;

    // Assemble the physical image.
    var ms = new MemoryStream();

    // Sectors 0/1: banner.
    var banner = MakeBanner(this.Version, this.VolumePath);
    ms.Write(banner);
    ms.Write(banner);

    // Sectors 2/3: SCB BPB + backup.
    var scb = MakeBpb(this.SectorsPerCluster, reserved, numFats, rootEntries, innerTotalSectors, sectorsPerFat);
    ms.Write(scb);
    ms.Write(scb);

    // Inner image at physical sector 4: boot(reserved) + FAT1 + FAT2 + root.
    var boot = new byte[reserved * SectorSize]; // empty reserved region
    ms.Write(boot);
    ms.Write(fat);
    ms.Write(fat);
    ms.Write(root);

    // Data clusters (STORED or LZS), recording the map.
    var map = new List<(int logical, int physical, int clen, bool compressed)>();
    for (var c = 0; c < clusterToData.Count; ++c) {
      var logical = c + 2;
      var raw = clusterToData[c];
      var physical = (int)(ms.Position / SectorSize);

      byte[] payload;
      bool compressed;
      if (this.Compress) {
        var lzs = StacLzs.Compress(raw);
        if (lzs.Length < raw.Length && lzs.Length <= 0xFFFF) {
          payload = lzs;
          compressed = true;
        } else {
          payload = raw;
          compressed = false;
        }
      } else {
        payload = raw;
        compressed = false;
      }

      var clen = compressed ? payload.Length : 0;
      ms.Write(payload);
      PadToSector(ms);
      map.Add((logical, physical, clen, compressed));
    }

    // Map table.
    var mapStart = (int)ms.Position;
    var mapBuf = new byte[(map.Count + 1) * 12];
    var mp = 0;
    foreach (var (logical, physical, clen, compressed) in map) {
      BinaryPrimitives.WriteUInt32LittleEndian(mapBuf.AsSpan(mp), (uint)logical);
      BinaryPrimitives.WriteUInt32LittleEndian(mapBuf.AsSpan(mp + 4), (uint)physical);
      BinaryPrimitives.WriteUInt16LittleEndian(mapBuf.AsSpan(mp + 8), (ushort)clen);
      BinaryPrimitives.WriteUInt16LittleEndian(mapBuf.AsSpan(mp + 10), (ushort)(compressed ? 1 : 0));
      mp += 12;
    }

    BinaryPrimitives.WriteUInt32LittleEndian(mapBuf.AsSpan(mp), MapTerminator);
    ms.Write(mapBuf);
    PadToSector(ms);

    // Trailer sector: "STKMAP01" + u32 mapStart.
    var trailer = new byte[SectorSize];
    "STKMAP01"u8.CopyTo(trailer);
    BinaryPrimitives.WriteUInt32LittleEndian(trailer.AsSpan(8), (uint)mapStart);
    ms.Write(trailer);

    return ms.ToArray();
  }

  private static void PadToSector(MemoryStream ms) {
    var rem = (int)(ms.Position % SectorSize);
    if (rem != 0)
      ms.Write(new byte[SectorSize - rem]);
  }

  private static byte[] MakeBanner(int version, string path) {
    var sector = new byte[SectorSize];
    var text = $"STACKER  version  {version}    volume:  {path}";
    var bytes = Encoding.ASCII.GetBytes(text);
    Array.Copy(bytes, sector, Math.Min(bytes.Length, 0x4D));
    for (var i = bytes.Length; i < 0x4D; ++i)
      sector[i] = (byte)' ';
    sector[0x4D] = 0x0D;
    sector[0x4E] = 0x0A;
    sector[0x4F] = 0x1A;
    return sector;
  }

  private static byte[] MakeBpb(int secPerClus, int reserved, int numFats, int rootEntries, int totalSectors, int sectorsPerFat) {
    var s = new byte[SectorSize];
    s[0] = 0xEB;
    s[1] = 0xFE;
    s[2] = 0x90;
    "STACKER "u8.CopyTo(s.AsSpan(3));
    BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(0x0B), SectorSize);
    s[0x0D] = (byte)secPerClus;
    BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(0x0E), (ushort)reserved);
    s[0x10] = (byte)numFats;
    BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(0x11), (ushort)rootEntries);
    BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(0x13), (ushort)Math.Min(totalSectors, 0xFFFF));
    s[0x15] = 0xF8;
    BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(0x16), (ushort)sectorsPerFat);
    BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(0x18), 63);
    BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(0x1A), 15);
    s[0x26] = 0x29;
    BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(0x27), 0x20620613);
    "STACKER.VOL"u8.CopyTo(s.AsSpan(0x2B));
    return s;
  }

  private static void WriteVolumeLabel(byte[] root, int index) {
    var e = index * 32;
    var label = Encoding.ASCII.GetBytes("STACVOL_DSK");
    Array.Copy(label, 0, root, e, Math.Min(11, label.Length));
    root[e + 0x0B] = 0x08; // volume-label attribute
  }

  private static void WriteDirEntry(byte[] root, int index, string name, int firstCluster, int size) {
    var e = index * 32;
    var (stem, ext) = SplitName(name);
    for (var i = 0; i < 11; ++i)
      root[e + i] = (byte)' ';
    for (var i = 0; i < stem.Length && i < 8; ++i)
      root[e + i] = (byte)stem[i];
    for (var i = 0; i < ext.Length && i < 3; ++i)
      root[e + 8 + i] = (byte)ext[i];
    root[e + 0x0B] = 0x20; // archive
    BinaryPrimitives.WriteUInt16LittleEndian(root.AsSpan(e + 0x1A), (ushort)firstCluster);
    BinaryPrimitives.WriteUInt32LittleEndian(root.AsSpan(e + 0x1C), (uint)size);
  }

  private static (string stem, string ext) SplitName(string name) {
    var dot = name.LastIndexOf('.');
    if (dot < 0)
      return (name, "");
    return (name[..dot], name[(dot + 1)..]);
  }

  private static void SetFat12(byte[] fat, int n, int value) {
    var o = n * 3 / 2;
    if (o + 1 >= fat.Length)
      return;
    if ((n & 1) == 0) {
      fat[o] = (byte)(value & 0xFF);
      fat[o + 1] = (byte)((fat[o + 1] & 0xF0) | ((value >> 8) & 0x0F));
    } else {
      fat[o] = (byte)((fat[o] & 0x0F) | ((value << 4) & 0xF0));
      fat[o + 1] = (byte)((value >> 4) & 0xFF);
    }
  }
}
