#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Vhd;

/// <summary>
/// Provides seekable read/write access to the virtual disk content of a VHD
/// (both Fixed and Dynamic). For a fixed VHD the raw disk data occupies bytes
/// [0 .. fileLength-512) and the trailing 512-byte footer is hidden. For a
/// dynamic VHD the BAT (Block Allocation Table) maps virtual 2 MB blocks to
/// physical file offsets; unallocated blocks read as zeros and are allocated
/// at EOF on write.
/// </summary>
public sealed class VhdStream : Stream {
  private static readonly byte[] FooterMagic = "conectix"u8.ToArray();
  private static readonly byte[] DynMagic = "cxsparse"u8.ToArray();

  private readonly Stream _backing;
  private readonly bool _leaveOpen;
  private readonly long _virtualSize;
  private long _position;

  // Fixed VHD: data starts at 0, length = fileLength - 512
  private readonly bool _isDynamic;

  // Dynamic VHD fields
  private readonly uint[] _bat;
  private readonly int _blockSize;
  private readonly int _bitmapBytes;
  private readonly long _batFileOffset;

  /// <summary>
  /// Creates a <see cref="VhdStream"/> over an existing VHD file stream.
  /// Auto-detects fixed vs dynamic from the footer's disk_type field.
  /// </summary>
  /// <param name="backing">The underlying VHD file stream. Must be readable and seekable.</param>
  /// <param name="leaveOpen">If <c>true</c>, the backing stream is not disposed when
  /// this stream is disposed.</param>
  public VhdStream(Stream backing, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(backing);
    if (!backing.CanRead) throw new ArgumentException("Backing stream must be readable.", nameof(backing));
    if (!backing.CanSeek) throw new ArgumentException("Backing stream must be seekable.", nameof(backing));
    if (backing.Length < 512)
      throw new InvalidDataException("VHD: file too small (must be at least 512 bytes for footer).");

    _backing = backing;
    _leaveOpen = leaveOpen;
    _bat = [];

    // Read footer — try EOF first (fixed VHD), then offset 0 (dynamic VHD copy)
    var footerBuf = new byte[512];
    backing.Position = backing.Length - 512;
    backing.ReadExactly(footerBuf);

    if (!footerBuf.AsSpan(0, 8).SequenceEqual(FooterMagic)) {
      // Try dynamic VHD: footer copy at offset 0
      backing.Position = 0;
      backing.ReadExactly(footerBuf);
      if (!footerBuf.AsSpan(0, 8).SequenceEqual(FooterMagic))
        throw new InvalidDataException("VHD: invalid footer magic.");
    }

    var diskType = BinaryPrimitives.ReadUInt32BigEndian(footerBuf.AsSpan(60));
    _virtualSize = (long)BinaryPrimitives.ReadUInt64BigEndian(footerBuf.AsSpan(48));
    var dataOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(footerBuf.AsSpan(16));

    if (diskType == 2) {
      // Fixed VHD
      _isDynamic = false;
      _virtualSize = backing.Length - 512;
    } else if (diskType is 3 or 4) {
      // Dynamic (3) or Differencing (4)
      _isDynamic = true;

      if (dataOffset < 0 || dataOffset + 1024 > backing.Length)
        throw new InvalidDataException("VHD: dynamic disk header offset out of range.");

      var hdrBuf = new byte[1024];
      backing.Position = dataOffset;
      backing.ReadExactly(hdrBuf);

      if (!hdrBuf.AsSpan(0, 8).SequenceEqual(DynMagic))
        throw new InvalidDataException("VHD: invalid dynamic disk header magic.");

      _batFileOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(hdrBuf.AsSpan(16));
      var maxBatEntries = (int)BinaryPrimitives.ReadUInt32BigEndian(hdrBuf.AsSpan(28));
      _blockSize = (int)BinaryPrimitives.ReadUInt32BigEndian(hdrBuf.AsSpan(32));

      if (_blockSize <= 0 || (_blockSize & (_blockSize - 1)) != 0)
        throw new InvalidDataException($"VHD: invalid block size {_blockSize}.");

      var sectorsPerBlock = _blockSize / 512;
      var bitmapSectors = (sectorsPerBlock + 512 * 8 - 1) / (512 * 8);
      _bitmapBytes = bitmapSectors * 512;

      // Read BAT
      _bat = new uint[maxBatEntries];
      var batBuf = new byte[maxBatEntries * 4];
      backing.Position = _batFileOffset;
      backing.ReadExactly(batBuf);
      for (var i = 0; i < maxBatEntries; i++)
        _bat[i] = BinaryPrimitives.ReadUInt32BigEndian(batBuf.AsSpan(i * 4));
    } else {
      throw new InvalidDataException($"VHD: unsupported disk type {diskType}.");
    }
  }

  /// <summary>
  /// Gets a value indicating whether can read.
  /// </summary>
  public override bool CanRead => true;
  /// <summary>
  /// Gets a value indicating whether can seek.
  /// </summary>
  public override bool CanSeek => true;
  /// <summary>
  /// Gets a value indicating whether can write.
  /// </summary>
  public override bool CanWrite => _backing.CanWrite;
  /// <summary>
  /// Gets the length.
  /// </summary>
  public override long Length => _virtualSize;

  /// <summary>
  /// Gets or sets the position.
  /// </summary>
  public override long Position {
    get => _position;
    set {
      if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
      _position = value;
    }
  }

  /// <summary>
  /// Reads the value from the supplied input.
  /// </summary>
  public override int Read(byte[] buffer, int offset, int count) {
    if (_position >= _virtualSize) return 0;
    var remaining = (int)Math.Min(count, _virtualSize - _position);

    if (!_isDynamic) {
      // Fixed: direct pass-through
      _backing.Position = _position;
      var read = _backing.Read(buffer, offset, remaining);
      _position += read;
      return read;
    }

    // Dynamic: translate through BAT
    var totalRead = 0;
    while (remaining > 0) {
      var blockIdx = (int)(_position / _blockSize);
      var blockOff = (int)(_position % _blockSize);
      var toRead = Math.Min(remaining, _blockSize - blockOff);

      if (blockIdx >= _bat.Length || _bat[blockIdx] == 0xFFFFFFFF) {
        // Unallocated — return zeros
        Array.Clear(buffer, offset, toRead);
      } else {
        var physOffset = (long)_bat[blockIdx] * 512 + _bitmapBytes + blockOff;
        _backing.Position = physOffset;
        var n = _backing.Read(buffer, offset, toRead);
        if (n < toRead) Array.Clear(buffer, offset + n, toRead - n);
      }

      offset += toRead;
      remaining -= toRead;
      _position += toRead;
      totalRead += toRead;
    }

    return totalRead;
  }

  /// <summary>
  /// Writes the value to the supplied output.
  /// </summary>
  public override void Write(byte[] buffer, int offset, int count) {
    if (!CanWrite) throw new NotSupportedException("Backing stream is not writable.");
    if (_position + count > _virtualSize)
      throw new InvalidOperationException(
        $"Write would exceed virtual disk size ({_virtualSize} bytes). " +
        $"Position={_position}, Count={count}.");

    if (!_isDynamic) {
      // Fixed: direct pass-through
      _backing.Position = _position;
      _backing.Write(buffer, offset, count);
      _position += count;
      return;
    }

    // Dynamic: translate through BAT, allocating blocks as needed
    var remaining = count;
    while (remaining > 0) {
      var blockIdx = (int)(_position / _blockSize);
      var blockOff = (int)(_position % _blockSize);
      var toWrite = Math.Min(remaining, _blockSize - blockOff);

      if (blockIdx >= _bat.Length)
        throw new InvalidOperationException($"Block index {blockIdx} out of BAT range.");

      if (_bat[blockIdx] == 0xFFFFFFFF) {
        // Unallocated — allocate new block at EOF
        AllocateBlock(blockIdx);
      }

      var physOffset = (long)_bat[blockIdx] * 512 + _bitmapBytes + blockOff;
      _backing.Position = physOffset;
      _backing.Write(buffer, offset, toWrite);

      offset += toWrite;
      remaining -= toWrite;
      _position += toWrite;
    }
  }

  /// <summary>
  /// Performs the seek operation.
  /// </summary>
  public override long Seek(long offset, SeekOrigin origin) {
    var newPos = origin switch {
      SeekOrigin.Begin => offset,
      SeekOrigin.Current => _position + offset,
      SeekOrigin.End => _virtualSize + offset,
      _ => throw new ArgumentOutOfRangeException(nameof(origin))
    };
    if (newPos < 0) throw new IOException("Seek before beginning of stream.");
    _position = newPos;
    return _position;
  }

  /// <summary>
  /// Sets the length.
  /// </summary>
  public override void SetLength(long value) {
    if (value != _virtualSize)
      throw new NotSupportedException(
        $"Cannot change the length of a VHD virtual disk stream " +
        $"(current={_virtualSize}, requested={value}).");
  }

  /// <summary>
  /// Performs the flush operation.
  /// </summary>
  public override void Flush() => _backing.Flush();

  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
  protected override void Dispose(bool disposing) {
    if (disposing && !_leaveOpen)
      _backing.Dispose();
    base.Dispose(disposing);
  }

  // ── Dynamic VHD block allocation ─────────────────────────────────────

  /// <summary>
  /// Allocates a new block at EOF for the given block index. Writes sector
  /// bitmap (all 0xFF = all sectors present), zeros the data region, updates
  /// the in-memory BAT and writes the BAT entry + footer to the backing stream.
  /// </summary>
  private void AllocateBlock(int blockIdx) {
    // The footer currently sits at the very end. We overwrite it with the new
    // block and write a new footer after the block.
    var eof = _backing.Length;

    // The last 512 bytes are the footer — new block starts there
    var newBlockStart = eof - 512;
    var newBlockSector = (uint)(newBlockStart / 512);

    // Write sector bitmap (all 1s = all sectors present)
    var bitmap = new byte[_bitmapBytes];
    Array.Fill(bitmap, (byte)0xFF);
    _backing.Position = newBlockStart;
    _backing.Write(bitmap);

    // Write zeroed data block
    var zeros = new byte[_blockSize];
    _backing.Write(zeros);

    // Write new footer at the new EOF
    var footerBuf = new byte[512];
    // Read the old footer from the beginning (dynamic VHD always has a copy at offset 0)
    _backing.Position = 0;
    _backing.ReadExactly(footerBuf);
    _backing.Position = newBlockStart + _bitmapBytes + _blockSize;
    _backing.Write(footerBuf);

    // Update BAT entry
    _bat[blockIdx] = newBlockSector;
    _backing.Position = _batFileOffset + blockIdx * 4L;
    Span<byte> batEntry = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(batEntry, newBlockSector);
    _backing.Write(batEntry);
  }
}
