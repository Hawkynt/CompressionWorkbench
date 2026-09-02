#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.Pc98;

/// <summary>
/// Moves a file's clusters inside a PC-98 volume and relinks its chain.
/// </summary>
/// <remarks>
/// <para>A PC-98 volume opens with an IPL block and keeps its BPB in that
/// block's second half, so everything the FAT walker expects at the front of
/// the volume sits one sector and 128 bytes further in. Past that it is an
/// ordinary FAT layout: reserved sectors, the allocation tables, a fixed root
/// directory, then data, with a 32-byte directory entry carrying a first
/// cluster.</para>
///
/// <para>Only a file that occupies one run is relocated. A fragmented file
/// needs its whole chain restated, which the caller is told to rebuild for
/// instead.</para>
/// </remarks>
public sealed class Pc98BlockMover : IFilesystemBlockMover {

  private int _bytesPerSector = 512;
  private int _sectorsPerCluster = 1;
  private int _reservedSectors;
  private int _fatCount;
  private int _rootEntries;
  private int _sectorsPerFat;
  private int _clusterSize;
  private long _fatBase;
  private long _rootBase;
  private long _dataBase;
  private int _fatType = 12;

  /// <summary>Reads the volume's geometry from its boot sector.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    Span<byte> boot = stackalloc byte[512];
    image.Position = 0;
    image.ReadExactly(boot);

    // The BPB lives at 0x80, in the IPL block's second half.
    var bpb = boot[Pc98Reader.BpbOffset..];
    this._bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(bpb[0x0B..]);
    if (this._bytesPerSector is < 256 or > 4096) this._bytesPerSector = 512;
    this._sectorsPerCluster = bpb[0x0D] == 0 ? 1 : bpb[0x0D];
    this._reservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(bpb[0x0E..]);
    this._fatCount = bpb[0x10];
    this._rootEntries = BinaryPrimitives.ReadUInt16LittleEndian(bpb[0x11..]);
    this._sectorsPerFat = BinaryPrimitives.ReadUInt16LittleEndian(bpb[0x16..]);
    if (this._fatCount is < 1 or > 4)
      throw new InvalidDataException("PC-98: the boot sector does not describe a FAT layout.");

    this._clusterSize = this._sectorsPerCluster * this._bytesPerSector;

    // Every structure sits one sector further in than a plain FAT volume's,
    // because the IPL block comes first.
    var iplBytes = (long)this._bytesPerSector;
    this._fatBase = iplBytes + (long)this._reservedSectors * this._bytesPerSector;
    this._rootBase = this._fatBase + (long)this._fatCount * this._sectorsPerFat * this._bytesPerSector;
    var rootSectors = (this._rootEntries * 32 + this._bytesPerSector - 1) / this._bytesPerSector;
    this._dataBase = this._rootBase + (long)rootSectors * this._bytesPerSector;

    var dataSectors = image.Length / this._bytesPerSector - this._dataBase / this._bytesPerSector;
    var clusters = dataSectors / this._sectorsPerCluster;
    this._fatType = clusters < 4085 ? 12 : 16;
  }

  /// <summary>Allocation unit in bytes.</summary>
  public int ClusterSize => this._clusterSize;

  /// <summary>First byte a file may occupy.</summary>
  public long FirstDataByte => this._dataBase;

  /// <inheritdoc />
    /// <summary>
  /// Performs the move extent operation.
  /// </summary>
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
    /// <summary>
  /// Performs the update allocation after move operation.
  /// </summary>
public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(fileName);
    if (this._clusterSize == 0) this.Init(image);

    var oldFirst = this.ClusterOf(oldOffset);
    var newFirst = this.ClusterOf(newOffset);
    var count = (int)((length + this._clusterSize - 1) / this._clusterSize);
    if (count <= 0 || oldFirst == newFirst) return;

    // The new chain first: nothing reads it until the directory entry names it,
    // so a failure here costs only the clusters it claimed.
    for (var i = 0; i < count; ++i)
      this.WriteFatEntry(image, newFirst + i, i + 1 < count ? newFirst + i + 1 : EndOfChain());
    image.Flush();

    if (!this.PatchDirectoryEntry(image, oldFirst, newFirst))
      throw new InvalidOperationException(
        $"PC-98: no directory entry starts at cluster {oldFirst}, so '{fileName}' cannot be repointed.");
    image.Flush();

    // Only now are the old clusters unreferenced.
    for (var i = 0; i < count; ++i)
      this.WriteFatEntry(image, oldFirst + i, 0);
    image.Flush();
  }

  /// <summary>The cluster number that holds a byte offset.</summary>
  private int ClusterOf(long offset) => (int)((offset - this._dataBase) / this._clusterSize) + 2;

  /// <summary>End-of-chain marker for this volume's table width.</summary>
  private int EndOfChain() => this._fatType == 12 ? 0x0FFF : 0xFFFF;

  /// <summary>
  /// Writes one entry in every copy of the table. A 12-bit entry shares a byte
  /// with its neighbour, so it is read back before being written.
  /// </summary>
  private void WriteFatEntry(Stream image, int cluster, int value) {
    Span<byte> word = stackalloc byte[2];
    Span<byte> pair = stackalloc byte[2];
    for (var copy = 0; copy < this._fatCount; ++copy) {
      var tableBase = this._fatBase + (long)copy * this._sectorsPerFat * this._bytesPerSector;

      if (this._fatType == 16) {
        BinaryPrimitives.WriteUInt16LittleEndian(word, (ushort)value);
        image.Position = tableBase + (long)cluster * 2;
        image.Write(word);
        continue;
      }

      var at = tableBase + cluster * 3L / 2;
      image.Position = at;
      image.ReadExactly(pair);
      if ((cluster & 1) == 0) {
        pair[0] = (byte)(value & 0xFF);
        pair[1] = (byte)((pair[1] & 0xF0) | ((value >> 8) & 0x0F));
      } else {
        pair[0] = (byte)((pair[0] & 0x0F) | ((value & 0x0F) << 4));
        pair[1] = (byte)((value >> 4) & 0xFF);
      }
      image.Position = at;
      image.Write(pair);
    }
  }

  /// <summary>
  /// Points the entry that currently starts at <paramref name="oldFirst" /> at
  /// <paramref name="newFirst" />. Matching on the cluster rather than the name
  /// keeps a duplicate name from repointing the wrong file.
  /// </summary>
  private bool PatchDirectoryEntry(Stream image, int oldFirst, int newFirst) {
    var record = new byte[32];
    for (var i = 0; i < this._rootEntries; ++i) {
      var at = this._rootBase + (long)i * 32;
      if (at + 32 > image.Length) return false;

      image.Position = at;
      image.ReadExactly(record);
      if (record[0] == 0x00) return false;      // end of directory
      if (record[0] == 0xE5) continue;          // deleted
      if (BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(0x1A)) != oldFirst) continue;

      Span<byte> field = stackalloc byte[2];
      BinaryPrimitives.WriteUInt16LittleEndian(field, (ushort)newFirst);
      image.Position = at + 0x1A;
      image.Write(field);
      return true;
    }
    return false;
  }
}
