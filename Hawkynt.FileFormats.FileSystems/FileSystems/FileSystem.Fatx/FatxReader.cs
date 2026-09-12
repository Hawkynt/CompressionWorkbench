#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;
using System.Text;

namespace FileSystem.Fatx;

/// <summary>
/// Reader for Microsoft Xbox / Xbox 360 FATX volumes.
///
/// On-disk layout (little-endian):
///   +0x000  "FATX"             4   magic
///   +0x004  volume_id          4
///   +0x008  sectors_per_cluster 4
///   +0x00C  root_dir_cluster   4
///   +0x010  unused / name      0x1000 - 0x10
///
/// FAT immediately follows the superblock at offset 0x1000 (4 KiB). FAT
/// entries are either 16 or 32 bits depending on cluster count; if the cluster
/// count &lt; 0xFFF4 the table is FAT16, otherwise FAT32. EOC sentinels
/// 0xFFF8/0xFFFFFFF8.
///
/// Directory record (0x40 bytes):
///   +0x00  name_length        u8   (0xFF = unused, 0xE5 = deleted)
///   +0x01  attributes         u8
///   +0x02  name               42 bytes (padded 0xFF)
///   +0x2C  first_cluster      u32
///   +0x30  size               u32
///   +0x34..0x3F  timestamps
///
/// Spec sources: https://www.eecg.utoronto.ca/~lie/papers/usenix2002.pdf (Xbox
/// security paper) and FreeXboxBios FATX documentation; also reverse-engineered
/// by xboxhdm/fatx-linux/fatxlinux projects.
/// </summary>
public sealed class FatxReader : IDisposable {

  /// <summary>
  /// Random-access view over the image. Copying it into a byte[] capped the
  /// reader at the array limit, which FATX's 32-bit cluster numbers do not.
  /// </summary>
  private readonly ImageAccessor _data;
  private readonly List<FatxEntry> _entries = [];

  /// <summary>
  /// Gets the entries.
  /// </summary>
  public IReadOnlyList<FatxEntry> Entries => this._entries;

  /// <summary>
  /// Gets or sets the sectors per cluster.
  /// </summary>
  public uint SectorsPerCluster { get; private set; }
  /// <summary>
  /// Gets or sets the root dir cluster.
  /// </summary>
  public uint RootDirCluster { get; private set; }
  /// <summary>
  /// Gets or sets the fat type.
  /// </summary>
  public int FatType { get; private set; }
  /// <summary>
  /// Gets the cluster size.
  /// </summary>
  public int ClusterSize => (int)this.SectorsPerCluster * SectorSize;

  internal const int SectorSize = 512;
  internal const int SuperblockSize = 0x1000;
  internal const int DirRecordSize = 0x40;
  private const uint MagicFatx = 0x58544146; // 'F','A','T','X' little-endian

  /// <summary>
  /// Initializes a new instance of <see cref="FatxReader"/>.
  /// </summary>
  public FatxReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek) stream.Position = 0;
    this._data = new ImageAccessor(stream, leaveOpen: true);
    this.Parse();
  }

  private void Parse() {
    if (this._data.Length < SuperblockSize)
      throw new InvalidDataException("FATX: image smaller than superblock (4 KiB).");

    var magic = this._data.ReadUInt32(0);
    if (magic != MagicFatx)
      throw new InvalidDataException($"FATX: bad magic 0x{magic:X8} (expected 'FATX' 0x{MagicFatx:X8}).");

    this.SectorsPerCluster = this._data.ReadUInt32(0x08);
    this.RootDirCluster = this._data.ReadUInt32(0x0C);
    if (this.SectorsPerCluster == 0 || (this.SectorsPerCluster & (this.SectorsPerCluster - 1)) != 0)
      throw new InvalidDataException($"FATX: invalid sectors_per_cluster {this.SectorsPerCluster} (must be power of two).");

    // Decide FAT width from total cluster count. The FAT region starts at
    // 0x1000; data region starts after the FAT(s). For simplicity we use
    // the heuristic from the spec: if the volume can hold < 0xFFF4 clusters
    // the FAT is 16 bits, otherwise 32. We approximate cluster_count from
    // the image size to choose the width.
    var dataBytes = (long)this._data.Length - SuperblockSize;
    var clusterBytes = this.ClusterSize;
    var clusterCount = dataBytes / clusterBytes;
    this.FatType = clusterCount < 0xFFF4 ? 16 : 32;

    this.WalkDirectory(this.RootDirCluster, path: "");
  }

  private long FatRegionStart => SuperblockSize;

  internal long DataRegionStart {
    get {
      // FAT length in bytes is rounded up to 4 KiB pages.
      var dataBytes = (long)this._data.Length - SuperblockSize;
      var clusterBytes = this.ClusterSize;
      var clusterCount = Math.Max(1, dataBytes / clusterBytes);
      var entryBytes = this.FatType == 16 ? 2 : 4;
      var fatRaw = (clusterCount + 2) * entryBytes;
      var fatRounded = (fatRaw + 0xFFF) & ~0xFFFL;
      return SuperblockSize + fatRounded;
    }
  }

  internal long ClusterOffset(uint cluster) =>
    this.DataRegionStart + (long)(cluster - 1) * this.ClusterSize;

  internal uint GetNextCluster(uint cluster) {
    if (cluster < 1) return 0;
    var width = this.FatType == 16 ? 2 : 4;
    var pos = this.FatRegionStart + (long)cluster * width;
    if (pos + width > this._data.Length) return EndOfChain();
    return this.FatType == 16
      ? this._data.ReadUInt16(pos)
      : this._data.ReadUInt32(pos);
  }

  internal uint EndOfChain() => this.FatType == 16 ? 0xFFF8u : 0xFFFFFFF8u;

  internal bool IsEoc(uint c) => this.FatType == 16 ? c >= 0xFFF8 : c >= 0xFFFFFFF8;

  private void WalkDirectory(uint startCluster, string path) {
    var cluster = startCluster;
    var seenClusters = new HashSet<uint>();
    while (cluster >= 1 && !this.IsEoc(cluster) && seenClusters.Add(cluster)) {
      var offset = this.ClusterOffset(cluster);
      if (offset < 0 || offset + this.ClusterSize > this._data.Length) break;
      var endOfDir = false;
      for (var off = 0; off < this.ClusterSize; off += DirRecordSize) {
        var rec = this._data.Read(offset + off, DirRecordSize).AsSpan();
        var nameLen = rec[0];
        if (nameLen == 0xFF || nameLen == 0x00) { endOfDir = true; break; }
        if (nameLen == 0xE5) continue; // deleted
        if (nameLen > 42) continue; // malformed — skip

        var attrs = rec[1];
        var rawName = rec.Slice(2, nameLen).ToArray();
        var name = Encoding.ASCII.GetString(rawName);
        var firstCluster = BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(0x2C));
        var size = BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(0x30));
        var isDir = (attrs & 0x10) != 0;
        var full = string.IsNullOrEmpty(path) ? name : $"{path}/{name}";

        this._entries.Add(new FatxEntry {
          Name = full,
          Size = isDir ? 0 : size,
          FirstCluster = firstCluster,
          IsDirectory = isDir,
        });
        if (isDir && firstCluster >= 1 && !this.IsEoc(firstCluster))
          this.WalkDirectory(firstCluster, full);
      }
      if (endOfDir) break;
      cluster = this.GetNextCluster(cluster);
    }
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public byte[] Extract(FatxEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    using var ms = new MemoryStream();
    var remaining = entry.Size;
    var cluster = entry.FirstCluster;
    var seen = new HashSet<uint>();
    while (remaining > 0 && cluster >= 1 && !this.IsEoc(cluster) && seen.Add(cluster)) {
      var offset = this.ClusterOffset(cluster);
      if (offset < 0 || offset + this.ClusterSize > this._data.Length) break;
      var take = (int)Math.Min(remaining, this.ClusterSize);
      this._data.CopyTo(offset, ms, take);
      remaining -= take;
      cluster = this.GetNextCluster(cluster);
    }
    return ms.ToArray();
  }

  /// <summary>Internal accessor for the image used by the bounded chain stream.</summary>
  internal ImageAccessor Image => this._data;

  /// <summary>
  /// Copies an entry's bytes into <paramref name="destination" /> one cluster at
  /// a time, so an entry larger than a byte[] can hold is extracted like any other.
  /// </summary>
  public void ExtractTo(FatxEntry entry, Stream destination) {
    ArgumentNullException.ThrowIfNull(entry);
    ArgumentNullException.ThrowIfNull(destination);
    if (entry.IsDirectory) return;

    var remaining = entry.Size;
    var cluster = entry.FirstCluster;
    var seen = new HashSet<uint>();
    while (remaining > 0 && cluster >= 1 && !this.IsEoc(cluster) && seen.Add(cluster)) {
      var offset = this.ClusterOffset(cluster);
      if (offset < 0 || offset + this.ClusterSize > this._data.Length) break;
      var take = Math.Min(remaining, this.ClusterSize);
      this._data.CopyTo(offset, destination, take);
      remaining -= take;
      cluster = this.GetNextCluster(cluster);
    }
  }

  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
  public void Dispose() { }
}
