#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.BitLocker;

/// <summary>
/// The plaintext of a BitLocker volume as a block device, assembled from
/// <see cref="BitLockerLayout"/>'s segments: zero segments read as zeros and swallow writes (as
/// dm-zero does), crypt segments decrypt on read and encrypt on write. Blocks are the volume's
/// sector size; nothing outside the crypt segments is ever written.
/// </summary>
public sealed class BitLockerBlockDevice : IRandomAccessBlockDevice {
  private const int ChunkBytes = 1 << 20;

  private readonly Stream _stream;
  private readonly BitLockerSectorCipher _cipher;
  private readonly IReadOnlyList<BitLockerSegment> _segments;
  private readonly int _unitsPerSector;
  private readonly bool _leaveOpen;
  private readonly object _gate = new();
  private byte[] _buffer = [];
  private bool _disposed;

  public BitLockerBlockDevice(Stream stream, BitLockerVolume volume, ReadOnlySpan<byte> fvek, bool writable, bool leaveOpen = true) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(volume);
    if (!stream.CanRead || !stream.CanSeek)
      throw new ArgumentException("A BitLocker block device requires a readable, seekable stream.", nameof(stream));
    if (writable && !stream.CanWrite)
      throw new NotSupportedException("A writable BitLocker block device requires a writable stream.");
    CheckActivatable(volume);
    this._segments = BitLockerLayout.Segments(volume, stream.Length);
    var sectorSize = volume.SectorSize;
    this._unitsPerSector = sectorSize / BitLockerLayout.Unit;
    foreach (var s in this._segments)
      if (s.Offset % this._unitsPerSector != 0 || s.Length % this._unitsPerSector != 0 || s.IvOffset % this._unitsPerSector != 0)
        throw new InvalidDataException($"BitLocker: a segment is not aligned to the {sectorSize}-byte sector size.");
    this._stream = stream;
    this._cipher = new BitLockerSectorCipher(volume.CipherMode, fvek, sectorSize);
    this._leaveOpen = leaveOpen;
    this.CanWrite = writable;
    var units = this._segments[^1].End;
    this.Geometry = new BlockDeviceGeometry(sectorSize, units / this._unitsPerSector);
  }

  /// <summary>
  /// cryptsetup activates only normal volumes in the normal state; encrypt-on-write volumes and
  /// volumes mid-conversion are refused with its reasons.
  /// </summary>
  public static void CheckActivatable(BitLockerVolume volume) {
    ArgumentNullException.ThrowIfNull(volume);
    if (!volume.StateIsNormal)
      throw new NotSupportedException($"BitLocker: the volume is in an unsupported state (current 0x{volume.CurrentState:X4}, next 0x{volume.NextState:X4}) and cannot be activated.");
    if (volume.EncryptionType != BitLockerEncryptionType.Normal)
      throw new NotSupportedException($"BitLocker: volumes of type '{(volume.EncryptionType == BitLockerEncryptionType.EncryptOnWrite ? "encrypt-on-write" : "unknown")}' cannot be activated.");
  }

  public BlockDeviceGeometry Geometry { get; }
  public bool CanWrite { get; }

  /// <summary>The segments this view is built from (512-byte units).</summary>
  public IReadOnlyList<BitLockerSegment> Segments => this._segments;

  public int ReadBlocks(long firstBlock, Span<byte> destination) {
    this.Validate(firstBlock, destination.Length);
    lock (this._gate) {
      foreach (var (segment, unit, done, count) in this.Pieces(firstBlock * this._unitsPerSector, destination.Length)) {
        var target = destination.Slice(done, count);
        if (segment.IsZero) {
          target.Clear();
          continue;
        }
        var source = segment.IvOffset + (unit - segment.Offset);
        this._stream.Position = source * BitLockerLayout.Unit;
        this._stream.ReadExactly(target);
        this._cipher.Decrypt(target, source / this._unitsPerSector);
      }
    }
    return destination.Length / this.Geometry.LogicalBlockSize;
  }

  public void WriteBlocks(long firstBlock, ReadOnlySpan<byte> source) {
    if (!this.CanWrite) throw new NotSupportedException("The BitLocker block device was opened read-only.");
    this.Validate(firstBlock, source.Length);
    lock (this._gate) {
      foreach (var (segment, unit, done, count) in this.Pieces(firstBlock * this._unitsPerSector, source.Length)) {
        if (segment.IsZero) continue;   // dm-zero discards writes; the metadata stays untouched
        var target = segment.IvOffset + (unit - segment.Offset);
        for (var at = 0; at < count; at += ChunkBytes) {
          var n = Math.Min(ChunkBytes, count - at);
          if (this._buffer.Length < n) this._buffer = new byte[n];
          var buffer = this._buffer.AsSpan(0, n);
          source.Slice(done + at, n).CopyTo(buffer);
          var position = target + at / BitLockerLayout.Unit;
          this._cipher.Encrypt(buffer, position / this._unitsPerSector);
          this._stream.Position = position * BitLockerLayout.Unit;
          this._stream.Write(buffer);
          buffer.Clear();
        }
      }
    }
  }

  /// <summary>The segment pieces of the byte range starting at 512-byte unit <paramref name="unit"/>.</summary>
  private List<(BitLockerSegment Segment, long Unit, int Done, int Count)> Pieces(long unit, int length) {
    var pieces = new List<(BitLockerSegment, long, int, int)>(2);
    var done = 0;
    foreach (var segment in this._segments) {
      if (done == length) break;
      if (unit >= segment.End) continue;
      var count = (int)Math.Min(length - done, (segment.End - unit) * BitLockerLayout.Unit);
      pieces.Add((segment, unit, done, count));
      done += count;
      unit += count / BitLockerLayout.Unit;
    }
    return pieces;
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
    Array.Clear(this._buffer);
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
