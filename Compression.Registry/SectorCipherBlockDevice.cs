#pragma warning disable CS1591
namespace Compression.Registry;

/// <summary>
/// A sector cipher as disk encryption uses one: every sector is transformed on its own, keyed by
/// its sector number, so any sector can be read or rewritten without touching its neighbours.
/// </summary>
public interface ISectorCipher : IDisposable {
  /// <summary>Decrypts whole sectors in place; <paramref name="firstSector"/> numbers the first one.</summary>
  void Decrypt(Span<byte> data, long firstSector);

  /// <summary>Encrypts whole sectors in place; <paramref name="firstSector"/> numbers the first one.</summary>
  void Encrypt(Span<byte> data, long firstSector);
}

/// <summary>
/// The plaintext view of an encrypted data area: <see cref="Geometry"/> blocks of
/// <c>sectorSize</c> bytes starting at <c>dataOffset</c> in the backing stream, decrypted as they
/// are read and encrypted as they are written. Nothing outside the data area (headers, key
/// material) is ever written.
/// </summary>
public sealed class SectorCipherBlockDevice : IRandomAccessBlockDevice {
  private readonly Stream _stream;
  private readonly long _dataOffset;
  private readonly ISectorCipher _cipher;
  private readonly bool _leaveOpen;
  private readonly object _gate = new();
  private byte[] _scratch = [];
  private bool _disposed;

  /// <param name="stream">Backing ciphertext.</param>
  /// <param name="dataOffset">Byte offset of the first encrypted sector.</param>
  /// <param name="sectorSize">Encryption sector size; also the logical block size.</param>
  /// <param name="sectorCount">Number of sectors in the data area.</param>
  /// <param name="cipher">The sector transform; owned by the device and disposed with it.</param>
  /// <param name="writable">Whether <see cref="WriteBlocks"/> is allowed.</param>
  /// <param name="leaveOpen">Leave <paramref name="stream"/> open on dispose.</param>
  public SectorCipherBlockDevice(Stream stream, long dataOffset, int sectorSize, long sectorCount, ISectorCipher cipher, bool writable, bool leaveOpen = true) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(cipher);
    if (!stream.CanRead || !stream.CanSeek)
      throw new ArgumentException("An encrypted block device requires a readable, seekable stream.", nameof(stream));
    if (sectorSize < 512 || (sectorSize & (sectorSize - 1)) != 0)
      throw new ArgumentOutOfRangeException(nameof(sectorSize), "The sector size must be a power of two of at least 512.");
    ArgumentOutOfRangeException.ThrowIfNegative(dataOffset);
    ArgumentOutOfRangeException.ThrowIfNegative(sectorCount);
    if (writable && !stream.CanWrite)
      throw new NotSupportedException("A writable encrypted block device requires a writable stream.");
    if (dataOffset + sectorCount * sectorSize > stream.Length)
      throw new InvalidDataException("The encrypted data area runs past the end of the device.");
    this._stream = stream;
    this._dataOffset = dataOffset;
    this._cipher = cipher;
    this._leaveOpen = leaveOpen;
    this.CanWrite = writable;
    this.Geometry = new BlockDeviceGeometry(sectorSize, sectorCount);
  }

  public BlockDeviceGeometry Geometry { get; }
  public bool CanWrite { get; }

  public int ReadBlocks(long firstBlock, Span<byte> destination) {
    this.Validate(firstBlock, destination.Length);
    if (destination.Length == 0) return 0;
    lock (this._gate) {
      this._stream.Position = this._dataOffset + firstBlock * this.Geometry.LogicalBlockSize;
      this._stream.ReadExactly(destination);
      this._cipher.Decrypt(destination, firstBlock); // cipher instances are not thread-safe
    }
    return destination.Length / this.Geometry.LogicalBlockSize;
  }

  public void WriteBlocks(long firstBlock, ReadOnlySpan<byte> source) {
    if (!this.CanWrite) throw new NotSupportedException("The encrypted block device was opened read-only.");
    this.Validate(firstBlock, source.Length);
    if (source.Length == 0) return;
    lock (this._gate) {
      if (this._scratch.Length < source.Length) this._scratch = new byte[source.Length];
      var buffer = this._scratch.AsSpan(0, source.Length);
      source.CopyTo(buffer);
      this._cipher.Encrypt(buffer, firstBlock);
      this._stream.Position = this._dataOffset + firstBlock * this.Geometry.LogicalBlockSize;
      this._stream.Write(buffer);
      buffer.Clear();
    }
  }

  public void Trim(long firstBlock, long blockCount)
    => throw new NotSupportedException("Discarding sectors would reveal which parts of an encrypted volume are in use.");

  public void Flush() {
    ObjectDisposedException.ThrowIf(this._disposed, this);
    lock (this._gate) this._stream.Flush();
  }

  public void Dispose() {
    if (this._disposed) return;
    if (this.CanWrite) this._stream.Flush();
    this._disposed = true;
    this._cipher.Dispose();
    Array.Clear(this._scratch);
    if (!this._leaveOpen) this._stream.Dispose();
  }

  private void Validate(long firstBlock, int byteCount) {
    ObjectDisposedException.ThrowIf(this._disposed, this);
    if (byteCount % this.Geometry.LogicalBlockSize != 0)
      throw new ArgumentException("Block I/O buffers must contain a whole number of logical blocks.");
    var count = byteCount / this.Geometry.LogicalBlockSize;
    if (firstBlock < 0 || firstBlock > this.Geometry.BlockCount || count > this.Geometry.BlockCount - firstBlock)
      throw new ArgumentOutOfRangeException(nameof(firstBlock), "Block range extends beyond the device.");
  }
}
