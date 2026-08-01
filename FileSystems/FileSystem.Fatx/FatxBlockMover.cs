#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.Fatx;

/// <summary>
/// Moves a file's clusters inside a FATX volume and relinks its chain.
/// </summary>
/// <remarks>
/// <para>FATX is the Xbox's take on FAT: a 4 KB superblock, then the allocation
/// table, then a data region whose clusters are numbered from one rather than
/// two. Its directory records are 64 bytes and carry the name inline, which is
/// why a FAT walker cannot read them — but the chain in the table works the same
/// way, so relocating a file is a chain rewrite and one write into the record.</para>
///
/// <para>Only a file that occupies one run is relocated; a fragmented file needs
/// its whole chain restated, and the caller rebuilds for that instead.</para>
/// </remarks>
public sealed class FatxBlockMover : IFilesystemBlockMover {

  /// <summary>Offset of the first-cluster field inside a directory record.</summary>
  private const int RecordFirstClusterOffset = 0x2C;

  private int _clusterSize;
  private long _dataStart;
  private long _fatStart;
  private uint _rootCluster;
  private int _fatType = 16;

  /// <summary>Reads the volume's geometry from its superblock.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    Span<byte> superblock = stackalloc byte[0x14];
    image.Position = 0;
    image.ReadExactly(superblock);

    var sectorsPerCluster = BinaryPrimitives.ReadUInt32LittleEndian(superblock[0x08..]);
    if (sectorsPerCluster == 0) sectorsPerCluster = 1;
    this._clusterSize = (int)sectorsPerCluster * FatxReader.SectorSize;
    this._rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(superblock[0x0C..]);
    if (this._rootCluster == 0) this._rootCluster = 1;

    this._fatStart = FatxReader.SuperblockSize;
    var dataBytes = image.Length - FatxReader.SuperblockSize;
    var clusterCount = dataBytes / Math.Max(1, this._clusterSize);
    this._fatType = clusterCount < 0xFFF4 ? 16 : 32;

    var width = this._fatType == 16 ? 2 : 4;
    var fatBytes = (clusterCount + 1) * width;
    var rounded = (fatBytes + 4095) / 4096 * 4096;
    this._dataStart = FatxReader.SuperblockSize + rounded;
  }

  /// <summary>Allocation unit in bytes.</summary>
  public int ClusterSize => this._clusterSize;

  /// <summary>First byte a file may occupy.</summary>
  public long FirstDataByte => this._dataStart;

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    if (length <= 0 || srcOffset == dstOffset) return;

    // Overlap-safe: a run shifted forward by less than its own length
    // overwrites its own tail, and copying that front to back reads bytes
    // the copy has already replaced.
    Compression.Core.DiskImage.ExtentCopy.Move(image, srcOffset, dstOffset, length);
    if (zeroSource)
      Compression.Core.DiskImage.ExtentCopy.Zero(image, srcOffset, length);
  }

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(fileName);
    if (this._clusterSize == 0) this.Init(image);

    var oldFirst = this.ClusterOf(oldOffset);
    var newFirst = this.ClusterOf(newOffset);
    var count = (int)((length + this._clusterSize - 1) / this._clusterSize);
    if (count <= 0 || oldFirst == newFirst) return;

    // The new chain first: nothing reaches it until a record names it.
    for (var i = 0; i < count; ++i)
      this.WriteFatEntry(image, (uint)(newFirst + i),
        i + 1 < count ? (uint)(newFirst + i + 1) : this.EndOfChain());
    image.Flush();

    if (!this.PatchRecord(image, this._rootCluster, oldFirst, newFirst, []))
      throw new InvalidOperationException(
        $"FATX: no directory record starts at cluster {oldFirst}, so '{fileName}' cannot be repointed.");
    image.Flush();

    for (var i = 0; i < count; ++i)
      this.WriteFatEntry(image, (uint)(oldFirst + i), 0);
    image.Flush();
  }

  /// <summary>The cluster that holds a byte offset. FATX numbers from one.</summary>
  private uint ClusterOf(long offset) => (uint)((offset - this._dataStart) / this._clusterSize) + 1;

  /// <summary>End-of-chain marker for this volume's table width.</summary>
  private uint EndOfChain() => this._fatType == 16 ? 0xFFF8u : 0xFFFFFFF8u;

  /// <summary>Whether a chain entry marks the end.</summary>
  private bool IsEndOfChain(uint cluster)
    => this._fatType == 16 ? cluster >= 0xFFF8 : cluster >= 0xFFFFFFF8;

  /// <summary>Reads one entry from the allocation table.</summary>
  private uint ReadFatEntry(Stream image, uint cluster) {
    var width = this._fatType == 16 ? 2 : 4;
    var at = this._fatStart + (long)cluster * width;
    if (at + width > image.Length) return this.EndOfChain();

    Span<byte> value = stackalloc byte[4];
    image.Position = at;
    image.ReadExactly(value[..width]);
    return width == 2
      ? BinaryPrimitives.ReadUInt16LittleEndian(value)
      : BinaryPrimitives.ReadUInt32LittleEndian(value);
  }

  /// <summary>Writes one entry in the allocation table.</summary>
  private void WriteFatEntry(Stream image, uint cluster, uint value) {
    var width = this._fatType == 16 ? 2 : 4;
    var at = this._fatStart + (long)cluster * width;
    if (at + width > image.Length) return;

    Span<byte> encoded = stackalloc byte[4];
    if (width == 2) BinaryPrimitives.WriteUInt16LittleEndian(encoded, (ushort)value);
    else BinaryPrimitives.WriteUInt32LittleEndian(encoded, value);
    image.Position = at;
    image.Write(encoded[..width]);
  }

  /// <summary>
  /// Walks the directory tree for the record that still names
  /// <paramref name="oldFirst" /> and points it at <paramref name="newFirst" />.
  /// Matching on the cluster rather than the name keeps a duplicate name from
  /// repointing the wrong file.
  /// </summary>
  private bool PatchRecord(Stream image, uint directoryCluster, uint oldFirst, uint newFirst,
      HashSet<uint> visited) {
    var cluster = directoryCluster;
    var record = new byte[FatxReader.DirRecordSize];
    var subdirectories = new List<uint>();

    while (cluster >= 1 && !this.IsEndOfChain(cluster) && visited.Add(cluster)) {
      var clusterOffset = this._dataStart + (long)(cluster - 1) * this._clusterSize;
      if (clusterOffset < 0 || clusterOffset + this._clusterSize > image.Length) break;

      for (var at = 0; at < this._clusterSize; at += FatxReader.DirRecordSize) {
        image.Position = clusterOffset + at;
        image.ReadExactly(record);

        var nameLength = record[0];
        if (nameLength is 0xFF or 0x00) break;      // end of directory
        if (nameLength == 0xE5) continue;           // deleted

        var first = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(RecordFirstClusterOffset));
        if ((record[1] & 0x10) != 0) { subdirectories.Add(first); continue; }
        if (first != oldFirst) continue;

        Span<byte> field = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(field, newFirst);
        image.Position = clusterOffset + at + RecordFirstClusterOffset;
        image.Write(field);
        return true;
      }

      cluster = this.ReadFatEntry(image, cluster);
    }

    foreach (var child in subdirectories)
      if (child >= 1 && !this.IsEndOfChain(child) && this.PatchRecord(image, child, oldFirst, newFirst, visited))
        return true;

    return false;
  }
}
