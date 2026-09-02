#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;

namespace FileSystem.Fat;

/// <summary>
/// Read-only <see cref="Stream"/> that walks a FAT cluster chain on demand,
/// pulling one cluster at a time into a small buffer. Memory cost is bounded
/// by the cluster size (max 64 KB on standard FAT geometries) — the whole
/// entry is never materialised at once.
/// </summary>
/// <remarks>
/// <para>
/// Intended to be wrapped in a
/// <see cref="Compression.Registry.Streaming.BoundedEntryStream"/> sized to
/// the FAT directory entry's logical <c>Size</c> field. The bound on top
/// guarantees that cluster-tail slack past the entry's real size never
/// surfaces to the caller — even though the underlying chain reader
/// physically fills the whole final cluster into its buffer.
/// </para>
/// <para>
/// The stream takes an in-memory snapshot of the image (already the
/// <see cref="FatReader"/> contract) and walks the FAT table itself,
/// rather than depending on <see cref="FatReader.Extract"/>'s eager
/// concatenation. That keeps peak memory at <c>O(cluster_size)</c> on top
/// of whatever the caller already paid to instantiate the reader.
/// </para>
/// </remarks>
public sealed class FatChainStream : Stream {

  private readonly ImageAccessor _image;
  private readonly int _fatType;
  private readonly int _bytesPerSector;
  private readonly int _sectorsPerCluster;
  private readonly int _reservedSectors;
  private readonly int _fatCount;
  private readonly int _fatSize;
  private readonly int _firstDataSector;
  private readonly long _logicalSize;
  private readonly int _clusterSize;

  // Buffered current cluster.
  private byte[] _clusterBuffer;
  private int _clusterBufferLen;
  private int _clusterBufferPos;
  private int _currentCluster;
  private long _position;
  private bool _disposed;

  internal FatChainStream(
      ImageAccessor image, int startCluster, long logicalSize,
      int fatType, int bytesPerSector, int sectorsPerCluster,
      int reservedSectors, int fatCount, int fatSize, int firstDataSector) {
    this._image = image;
    this._currentCluster = startCluster;
    this._logicalSize = Math.Max(0, logicalSize);
    this._fatType = fatType;
    this._bytesPerSector = bytesPerSector;
    this._sectorsPerCluster = sectorsPerCluster;
    this._reservedSectors = reservedSectors;
    this._fatCount = fatCount;
    this._fatSize = fatSize;
    this._firstDataSector = firstDataSector;
    this._clusterSize = sectorsPerCluster * bytesPerSector;
    this._clusterBuffer = new byte[this._clusterSize];
    this._clusterBufferLen = 0;
    this._clusterBufferPos = 0;
  }

  /// <summary>
  /// Gets a value indicating whether can read.
  /// </summary>
public override bool CanRead => !this._disposed;
  /// <summary>
  /// Gets a value indicating whether can seek.
  /// </summary>
public override bool CanSeek => false;
  /// <summary>
  /// Gets a value indicating whether can write.
  /// </summary>
public override bool CanWrite => false;
  /// <summary>
  /// Gets the length.
  /// </summary>
public override long Length => this._logicalSize;
  /// <summary>
  /// Gets or sets the position.
  /// </summary>
public override long Position {
    get => this._position;
    set => throw new NotSupportedException("FatChainStream is forward-only.");
  }

  /// <summary>
  /// Reads the value from the supplied input.
  /// </summary>
public override int Read(byte[] buffer, int offset, int count) {
    ArgumentNullException.ThrowIfNull(buffer);
    ObjectDisposedException.ThrowIf(this._disposed, this);
    if (count <= 0) return 0;
    var remaining = this._logicalSize - this._position;
    if (remaining <= 0) return 0;

    var produced = 0;
    var want = (int)Math.Min(count, remaining);
    while (produced < want) {
      if (this._clusterBufferPos >= this._clusterBufferLen) {
        if (!this.LoadNextCluster()) break;
      }
      var avail = this._clusterBufferLen - this._clusterBufferPos;
      var take = Math.Min(want - produced, avail);
      Buffer.BlockCopy(this._clusterBuffer, this._clusterBufferPos, buffer, offset + produced, take);
      this._clusterBufferPos += take;
      this._position += take;
      produced += take;
    }
    return produced;
  }

  private bool LoadNextCluster() {
    if (this._currentCluster < 2 || this.IsEndOfChain(this._currentCluster))
      return false;
    // 64-bit: the sector-to-byte product overflows int past ~2 GB.
    var offset = ((long)this._firstDataSector + (long)(this._currentCluster - 2) * this._sectorsPerCluster) * this._bytesPerSector;
    var len = this._clusterSize;
    if (offset >= this._image.Length) return false;
    if (offset + len > this._image.Length) len = (int)(this._image.Length - offset);
    this._image.Read(offset, this._clusterBuffer.AsSpan(0, len));
    this._clusterBufferLen = len;
    this._clusterBufferPos = 0;
    this._currentCluster = this.GetNextCluster(this._currentCluster);
    return true;
  }

  private int GetNextCluster(int cluster) {
    var fatOffset = (long)this._reservedSectors * this._bytesPerSector;
    switch (this._fatType) {
      case 12: {
        var bytePos = fatOffset + (long)cluster * 3 / 2;
        if (bytePos + 2 > this._image.Length) return 0xFFF;
        var val = this._image.ReadUInt16(bytePos);
        return (cluster & 1) != 0 ? val >> 4 : val & 0xFFF;
      }
      case 16: {
        var pos = fatOffset + (long)cluster * 2;
        return pos + 2 <= this._image.Length ? this._image.ReadUInt16(pos) : 0xFFFF;
      }
      case 32: {
        var pos = fatOffset + (long)cluster * 4;
        return pos + 4 <= this._image.Length
          ? this._image.ReadInt32(pos) & 0x0FFFFFFF
          : 0x0FFFFFF8;
      }
      default: return 0;
    }
  }

  private bool IsEndOfChain(int cluster) => this._fatType switch {
    12 => cluster >= 0xFF8,
    16 => cluster >= 0xFFF8,
    32 => cluster >= 0x0FFFFFF8,
    _ => true,
  };

  /// <summary>
  /// Performs the flush operation.
  /// </summary>
public override void Flush() { }
  /// <summary>
  /// Performs the seek operation.
  /// </summary>
public override long Seek(long offset, SeekOrigin origin)
    => throw new NotSupportedException("FatChainStream is forward-only.");
  /// <summary>
  /// Sets the length.
  /// </summary>
public override void SetLength(long value)
    => throw new NotSupportedException("FatChainStream is read-only.");
  /// <summary>
  /// Writes the value to the supplied output.
  /// </summary>
public override void Write(byte[] buffer, int offset, int count)
    => throw new NotSupportedException("FatChainStream is read-only.");

  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
protected override void Dispose(bool disposing) {
    this._disposed = true;
    base.Dispose(disposing);
  }

  /// <summary>
  /// Opens a forward-only stream walking the FAT cluster chain for
  /// <paramref name="entry"/> against the BPB-derived geometry of
  /// <paramref name="reader"/>.
  /// </summary>
  public static FatChainStream Open(FatReader reader, FatEntry entry) {
    ArgumentNullException.ThrowIfNull(reader);
    ArgumentNullException.ThrowIfNull(entry);
    return reader.OpenChainStream(entry);
  }
}
