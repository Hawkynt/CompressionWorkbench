#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Pc98;

/// <summary>
/// Builds a fresh NEC PC-98 DOS disk image from scratch. PC-98 disks
/// prepend a 512-byte vendor Initial Program Loader (IPL) block to a
/// regular FAT12 layout; the IPL carries the <c>NECIPL</c> ASCII
/// signature at offset 0 and the FAT BPB at offset 0x80.
/// </summary>
/// <remarks>
/// <para>Image layout (sector_size = 512 B by default):</para>
/// <list type="bullet">
/// <item>Sector 0 (offset 0..511): IPL block — "NECIPL" at offset 0, FAT BPB at offset 0x80.</item>
/// <item>Sectors 1..ReservedSectors: additional reserved sectors (we always use 1).</item>
/// <item>Next sectors_per_fat: FAT12 (single copy).</item>
/// <item>Next root_dir_sectors: root directory (32-entry default).</item>
/// <item>Remaining: data clusters.</item>
/// </list>
/// <para>The reader is permissive: FatCount in [1, 4], RootEntries in [16, 1024],
/// bytesPerSector in [256, 4096]. This writer uses FatCount=1, RootEntries=32
/// to keep the minimal image small.</para>
/// </remarks>
public sealed class Pc98Writer {

  private const int DefaultBytesPerSector = 512;

  private int _bytesPerSector = DefaultBytesPerSector;
  private int _sectorsPerCluster = 1;
  private int _reservedSectors = 1;
  private int _fatCount = 1;
  private int _rootEntries = 32;
  private int _sectorsPerFat = 1;
  private int _totalSectors;
  private string _volumeLabel = "PC98DISK";
  private string _mediaType = "HDM";

  private readonly List<(string Name, byte[] Data)> _files = [];

  /// <summary>Sets bytes per sector (256/512/1024). Default 512.</summary>
  public void SetBytesPerSector(int value) {
    if (value is not (256 or 512 or 1024))
      throw new ArgumentOutOfRangeException(nameof(value), "BytesPerSector must be 256, 512, or 1024.");
    this._bytesPerSector = value;
  }

  /// <summary>Sets sectors per cluster (power of two 1..32).</summary>
  public void SetSectorsPerCluster(int value) {
    if (value <= 0 || (value & (value - 1)) != 0 || value > 32)
      throw new ArgumentOutOfRangeException(nameof(value));
    this._sectorsPerCluster = value;
  }

  /// <summary>Sets total sector count. 0 = auto.</summary>
  public void SetTotalSectors(int value) {
    if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
    this._totalSectors = value;
  }

  /// <summary>Sets the 11-character volume label.</summary>
  public void SetVolumeLabel(string? label) {
    if (!string.IsNullOrWhiteSpace(label)) this._volumeLabel = label;
  }

  /// <summary>Sets the media-type label written into the BPB OEM field (HDM/FDI/D88).</summary>
  public void SetMediaType(string? value) {
    if (!string.IsNullOrWhiteSpace(value)) this._mediaType = value;
  }

  /// <summary>Adds one file. Subdirectory writes are deferred.</summary>
  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    this._files.Add((name, data));
  }

  /// <summary>Builds the disk image.</summary>

  /// <summary>Data clusters a FAT12 allocation table can address.</summary>
  private const int MaxFat12Clusters = 4084;

  /// <summary>
  /// Performs the build operation.
  /// </summary>
public byte[] Build() {
    if (this._files.Count > this._rootEntries)
      throw new InvalidOperationException(
        $"PC-98: more files ({this._files.Count}) than root entries ({this._rootEntries}).");

    var bps = this._bytesPerSector;
    var bytesPerCluster = bps * this._sectorsPerCluster;
    var rootDirSectors = (this._rootEntries * 32 + bps - 1) / bps;
    var clustersNeeded = 0;
    foreach (var (_, data) in this._files)
      clustersNeeded += Math.Max(1, (data.Length + bytesPerCluster - 1) / bytesPerCluster);

    var fatBytesNeeded = ((2 + clustersNeeded) * 3 + 1) / 2;
    var sectorsForFat = Math.Max(1, (fatBytesNeeded + bps - 1) / bps);
    this._sectorsPerFat = sectorsForFat;

    // Layout includes a leading IPL sector. The reader adds bytesPerSector to
    // every offset to skip past the IPL block.
    const int iplSectors = 1;
    var fatSectors = this._fatCount * sectorsForFat;
    var metadataSectors = iplSectors + this._reservedSectors + fatSectors + rootDirSectors;
    var dataSectors = clustersNeeded * this._sectorsPerCluster;
    var minTotal = metadataSectors + dataSectors;
    var total = this._totalSectors > 0 ? Math.Max(this._totalSectors, minTotal) : Math.Max(minTotal, 16);

    // The medium is a FAT12 floppy: past 4084 clusters the on-disk format
    // cannot express the chain at all, and multiplying the sector count out
    // reported that as an arithmetic overflow rather than as a full disk.
    if (clustersNeeded > MaxFat12Clusters)
      throw new InvalidOperationException(
        $"PC-98: the payload needs {clustersNeeded:N0} clusters, past the {MaxFat12Clusters:N0} " +
        $"a FAT12 medium can address ({(long)MaxFat12Clusters * bytesPerCluster:N0} bytes).");

    var image = new byte[(long)total * bps];

    // IPL block at offset 0.
    Encoding.ASCII.GetBytes("NECIPL").CopyTo(image.AsSpan(0, 6));

    // FAT BPB at offset 0x80 inside the IPL sector.
    var bpb = 0x80;
    image[bpb + 0] = 0xEB; // jump
    image[bpb + 1] = 0x00;
    image[bpb + 2] = 0x90;
    // OEM at offset 0x83 (BPB+3) — 8 bytes; use the media-type label.
    WriteFixedAscii(image.AsSpan(bpb + 3, 8), this._mediaType);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(bpb + 0x0B, 2), (ushort)bps);
    image[bpb + 0x0D] = (byte)this._sectorsPerCluster;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(bpb + 0x0E, 2), (ushort)this._reservedSectors);
    image[bpb + 0x10] = (byte)this._fatCount;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(bpb + 0x11, 2), (ushort)this._rootEntries);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(bpb + 0x13, 2), (ushort)Math.Min(0xFFFF, total));
    image[bpb + 0x15] = 0xFE; // media descriptor
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(bpb + 0x16, 2), (ushort)sectorsForFat);

    // FAT starts at sector iplSectors + reservedSectors = sector 2 with defaults.
    var fatStartSector = iplSectors + this._reservedSectors;
    var fatStart = fatStartSector * bps;
    image[fatStart]     = 0xF8;
    image[fatStart + 1] = 0xFF;
    image[fatStart + 2] = 0xFF;

    // Root directory.
    var rootDirStartSector = fatStartSector + fatSectors;
    var rootDirOffset = rootDirStartSector * bps;

    // Data starts after the root directory.
    var dataStartSector = rootDirStartSector + rootDirSectors;

    var nextCluster = 2;
    var dirIdx = 0;
    foreach (var (rawName, data) in this._files) {
      var (nameField, extField) = SplitShortName(rawName);
      var clusterCount = Math.Max(1, (data.Length + bytesPerCluster - 1) / bytesPerCluster);
      var firstCluster = nextCluster;
      for (var k = 0; k < clusterCount; k++) {
        var thisCluster = firstCluster + k;
        var clusterStart = (dataStartSector + (thisCluster - 2) * this._sectorsPerCluster) * bps;
        var bytesIntoFile = k * bytesPerCluster;
        var toCopy = Math.Min(data.Length - bytesIntoFile, bytesPerCluster);
        if (toCopy > 0)
          Array.Copy(data, bytesIntoFile, image, clusterStart, toCopy);
        var nextEntry = k == clusterCount - 1 ? 0xFFF : thisCluster + 1;
        WriteFat12Entry(image, fatStart, thisCluster, nextEntry);
      }
      nextCluster += clusterCount;

      var dirOff = rootDirOffset + dirIdx * 32;
      Array.Copy(nameField, 0, image, dirOff, 8);
      Array.Copy(extField, 0, image, dirOff + 8, 3);
      image[dirOff + 0x0B] = 0x20;
      BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(dirOff + 0x1A, 2), (ushort)firstCluster);
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(dirOff + 0x1C, 4), (uint)data.Length);
      dirIdx++;
    }

    return image;
  }

  private static void WriteFat12Entry(byte[] image, int fatStart, int cluster, int value) {
    var off = fatStart + cluster * 3 / 2;
    if (off + 1 >= image.Length) return;
    if ((cluster & 1) == 0) {
      image[off] = (byte)(value & 0xFF);
      image[off + 1] = (byte)((image[off + 1] & 0xF0) | ((value >> 8) & 0x0F));
    } else {
      image[off] = (byte)((image[off] & 0x0F) | ((value & 0x0F) << 4));
      image[off + 1] = (byte)((value >> 4) & 0xFF);
    }
  }

  private static (byte[] Name, byte[] Ext) SplitShortName(string raw) {
    var safe = raw.Replace('\\', '/');
    var slash = safe.LastIndexOf('/');
    if (slash >= 0) safe = safe[(slash + 1)..];
    safe = safe.ToUpperInvariant();
    var dot = safe.LastIndexOf('.');
    var rawName = dot > 0 ? safe[..dot] : safe;
    var rawExt = dot > 0 ? safe[(dot + 1)..] : "";

    var nameBytes = new byte[8];
    Array.Fill(nameBytes, (byte)0x20);
    var extBytes = new byte[3];
    Array.Fill(extBytes, (byte)0x20);
    var nb = Encoding.ASCII.GetBytes(rawName);
    var eb = Encoding.ASCII.GetBytes(rawExt);
    Array.Copy(nb, 0, nameBytes, 0, Math.Min(nb.Length, 8));
    Array.Copy(eb, 0, extBytes, 0, Math.Min(eb.Length, 3));
    return (nameBytes, extBytes);
  }

  private static void WriteFixedAscii(Span<byte> dst, string value) {
    dst.Fill(0x20);
    var n = Math.Min(value.Length, dst.Length);
    for (var i = 0; i < n; i++) {
      var c = value[i];
      dst[i] = c < 0x80 ? (byte)c : (byte)'?';
    }
  }
}
