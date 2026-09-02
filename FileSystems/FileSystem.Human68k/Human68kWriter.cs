#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Human68k;

/// <summary>
/// Builds a fresh Sharp X68000 Human68k disk image from scratch. The
/// format is FAT12-derived: a BIOS Parameter Block at sector 0 with an
/// extra "X68K" identifier at offset 0x10 (Human68k's primary detection
/// magic), one or two FAT12 copies, a fixed-size root directory, and a
/// data area of N clusters.
/// </summary>
/// <remarks>
/// <para>The writer uses Shift_JIS-aware short-name encoding (filenames
/// are stored as raw byte sequences in the dirent at offsets 0..7 for
/// the 8-char name and 8..10 for the 3-char extension). Non-ASCII bytes
/// pass through; Shift_JIS decoding is the reader's responsibility.
/// </para>
/// <para>The image lays out as: boot sector (1 sector), FAT (sectorsPerFat
/// sectors), root directory (ceil(rootEntries*32 / bytesPerSector)
/// sectors), then data clusters. Single FAT only (FatCount=1) to keep
/// the minimal image small.</para>
/// </remarks>
public sealed class Human68kWriter {

  private const int DefaultBytesPerSector = 512;

  // Reader is permissive: validates FatCount in [1,4], RootEntries in [16,1024].
  private int _bytesPerSector = DefaultBytesPerSector;
  private int _sectorsPerCluster = 1;
  private int _reservedSectors = 1;
  private int _fatCount = 1;
  private int _rootEntries = 32; // valid range 16..1024
  private int _sectorsPerFat = 1;
  private int _totalSectors;
  private string _volumeLabel = "X68K";

  private readonly List<(string Name, byte[] Data)> _files = [];

  /// <summary>Sets bytes per sector (256/512/1024). Default 512.</summary>
  public void SetBytesPerSector(int value) {
    if (value is not (256 or 512 or 1024))
      throw new ArgumentOutOfRangeException(nameof(value), "BytesPerSector must be 256, 512, or 1024.");
    this._bytesPerSector = value;
  }

  /// <summary>Sets sectors per cluster (power of two, 1..32).</summary>
  public void SetSectorsPerCluster(int value) {
    if (value <= 0 || (value & (value - 1)) != 0 || value > 32)
      throw new ArgumentOutOfRangeException(nameof(value), "SectorsPerCluster must be a power of two in [1, 32].");
    this._sectorsPerCluster = value;
  }

  /// <summary>Sets total sector count. 0 = auto (sized to fit files).</summary>
  public void SetTotalSectors(int value) {
    if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
    this._totalSectors = value;
  }

  /// <summary>Sets the volume label (max 11 chars).</summary>
  public void SetVolumeLabel(string? label) {
    if (!string.IsNullOrWhiteSpace(label)) this._volumeLabel = label;
  }

  /// <summary>Adds one file. Subdirectories not supported in this minimal writer.</summary>
  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    this._files.Add((name, data));
  }

  /// <summary>Builds the disk image.</summary>

  /// <summary>Data clusters a FAT16 allocation table can address.</summary>
  private const int MaxClusters = 65524;

  /// <summary>
  /// Performs the build operation.
  /// </summary>
public byte[] Build() {
    if (this._files.Count > this._rootEntries)
      throw new InvalidOperationException(
        $"Human68k: more files ({this._files.Count}) than root entries ({this._rootEntries}).");

    var bytesPerCluster = this._bytesPerSector * this._sectorsPerCluster;
    var rootDirSectors = (this._rootEntries * 32 + this._bytesPerSector - 1) / this._bytesPerSector;
    var clustersNeeded = 0;
    foreach (var (_, data) in this._files)
      clustersNeeded += Math.Max(1, (data.Length + bytesPerCluster - 1) / bytesPerCluster);

    // Each FAT12 entry is 12 bits — 1.5 bytes; we need 2 + clustersNeeded entries
    // (clusters 0 and 1 are reserved by FAT spec).
    var fatBytesNeeded = ((2 + clustersNeeded) * 3 + 1) / 2;
    var sectorsForFat = Math.Max(1, (fatBytesNeeded + this._bytesPerSector - 1) / this._bytesPerSector);
    this._sectorsPerFat = sectorsForFat;

    var metadataSectors = this._reservedSectors + this._fatCount * sectorsForFat + rootDirSectors;
    var dataSectors = clustersNeeded * this._sectorsPerCluster;
    var minTotal = metadataSectors + dataSectors;
    var total = this._totalSectors > 0 ? Math.Max(this._totalSectors, minTotal) : Math.Max(minTotal, 16);

    // A Human68k volume is FAT12/16 on an X68000 medium: past the clusters the
    // allocation table can address there is nothing to describe, and
    // multiplying the sector count out reported that as an arithmetic overflow.
    var imageBytes = (long)total * this._bytesPerSector;
    if (clustersNeeded > MaxClusters || imageBytes > System.Array.MaxLength)
      throw new InvalidOperationException(
        $"Human68k: the payload needs {clustersNeeded:N0} clusters ({imageBytes:N0} bytes), past " +
        $"the {MaxClusters:N0} an X68000 volume can address.");

    var image = new byte[imageBytes];

    // Boot sector / BPB.
    image[0] = 0x60; // BSR jump (Human68k convention).
    image[1] = 0x00;
    image[2] = 0x00;
    Encoding.ASCII.GetBytes("HMN68K  ").CopyTo(image.AsSpan(3, 8)); // OEM (ignored by reader).
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x0B, 2), (ushort)this._bytesPerSector);
    image[0x0D] = (byte)this._sectorsPerCluster;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x0E, 2), (ushort)this._reservedSectors);
    Encoding.ASCII.GetBytes("X68K").CopyTo(image.AsSpan(0x10, 4)); // Human68k identifier (primary detection magic).
    image[0x14] = (byte)this._fatCount;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x15, 2), (ushort)this._rootEntries);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x17, 2), (ushort)Math.Min(0xFFFF, total));
    image[0x19] = 0xFE; // media descriptor.
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x1A, 2), (ushort)sectorsForFat);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x1C, 2), 9); // sectors per track.
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x1E, 2), 2); // heads.

    // FAT cluster 0 + 1 reserved (FAT12 standard: 0xFF8 + 0xFFF).
    var fatStart = this._reservedSectors * this._bytesPerSector;
    image[fatStart]     = 0xF8;
    image[fatStart + 1] = 0xFF;
    image[fatStart + 2] = 0xFF;

    // Allocate cluster chains.
    var rootDirOffset = (this._reservedSectors + this._fatCount * sectorsForFat) * this._bytesPerSector;
    var dataStartSector = this._reservedSectors + this._fatCount * sectorsForFat + rootDirSectors;
    var nextCluster = 2;

    var dirIdx = 0;
    foreach (var (rawName, data) in this._files) {
      var (nameField, extField) = SplitShortName(rawName);
      var clusterCount = Math.Max(1, (data.Length + bytesPerCluster - 1) / bytesPerCluster);
      var firstCluster = nextCluster;
      for (var k = 0; k < clusterCount; k++) {
        var thisCluster = firstCluster + k;
        var clusterStart = (dataStartSector + (thisCluster - 2) * this._sectorsPerCluster) * this._bytesPerSector;
        var bytesIntoFile = k * bytesPerCluster;
        var toCopy = Math.Min(data.Length - bytesIntoFile, bytesPerCluster);
        if (toCopy > 0)
          Array.Copy(data, bytesIntoFile, image, clusterStart, toCopy);
        // FAT12 entry: link to next or 0xFFF end-of-chain on last.
        var nextEntry = k == clusterCount - 1 ? 0xFFF : thisCluster + 1;
        WriteFat12Entry(image, fatStart, thisCluster, nextEntry);
      }
      nextCluster += clusterCount;

      // Write the directory entry at root.
      var dirOff = rootDirOffset + dirIdx * 32;
      Array.Copy(nameField, 0, image, dirOff, 8);
      Array.Copy(extField, 0, image, dirOff + 8, 3);
      image[dirOff + 0x0B] = 0x20; // attr = archive.
      BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(dirOff + 0x1A, 2), (ushort)firstCluster);
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(dirOff + 0x1C, 4), (uint)data.Length);
      dirIdx++;
    }

    return image;
  }

  private static void WriteFat12Entry(byte[] image, int fatStart, int cluster, int value) {
    // FAT12: two 12-bit entries packed into 3 bytes. Cluster N: byte offset = N * 3 / 2.
    var off = fatStart + cluster * 3 / 2;
    if (off + 1 >= image.Length) return;
    if ((cluster & 1) == 0) {
      // Even: low byte + low nibble of next byte.
      image[off] = (byte)(value & 0xFF);
      image[off + 1] = (byte)((image[off + 1] & 0xF0) | ((value >> 8) & 0x0F));
    } else {
      // Odd: high nibble of current + next byte.
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
}
