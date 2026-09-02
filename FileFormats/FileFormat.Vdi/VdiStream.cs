#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Vdi;

/// <summary>
/// Provides seekable read/write access to the virtual disk content of a VDI image.
/// Translates virtual block offsets through the block allocation map (BAM).
/// Reads from unallocated blocks (BAM entry = 0xFFFFFFFF) return zeros.
/// Writes to unallocated blocks allocate new data blocks at EOF and update
/// the BAM entry and allocated block count.
/// </summary>
public sealed class VdiStream : Stream {
  private const uint UnallocatedEntry = 0xFFFFFFFF;
  private const uint VdiSignature = 0xBEDA107F;

  private readonly Stream _backing;
  private readonly bool _leaveOpen;
  private readonly long _virtualSize;
  private readonly uint _blockSize;
  private readonly uint _blockCount;
  private readonly uint _offsetBlocks;
  private readonly uint _offsetData;
  private readonly uint[] _blockMap; // virtual block index -> physical block index (or 0xFFFFFFFF)
  private uint _allocatedCount;
  private long _position;

  private VdiStream(Stream backing, long virtualSize, uint blockSize, uint blockCount,
                     uint offsetBlocks, uint offsetData, uint[] blockMap,
                     uint allocatedCount, bool leaveOpen) {
    _backing = backing;
    _virtualSize = virtualSize;
    _blockSize = blockSize;
    _blockCount = blockCount;
    _offsetBlocks = offsetBlocks;
    _offsetData = offsetData;
    _blockMap = blockMap;
    _allocatedCount = allocatedCount;
    _leaveOpen = leaveOpen;
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
    var totalRead = 0;

    while (remaining > 0) {
      var blockIdx = (uint)(_position / _blockSize);
      var blockOff = (int)(_position % _blockSize);
      var toRead = Math.Min(remaining, (int)_blockSize - blockOff);

      if (blockIdx >= _blockCount || _blockMap[blockIdx] == UnallocatedEntry) {
        // Unallocated block — return zeros
        Array.Clear(buffer, offset, toRead);
      } else {
        var physOffset = (long)_offsetData + (long)_blockMap[blockIdx] * _blockSize + blockOff;
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

    var remaining = count;
    while (remaining > 0) {
      var blockIdx = (uint)(_position / _blockSize);
      var blockOff = (int)(_position % _blockSize);
      var toWrite = Math.Min(remaining, (int)_blockSize - blockOff);

      if (blockIdx >= _blockCount)
        throw new InvalidOperationException($"Block index {blockIdx} out of range.");

      if (_blockMap[blockIdx] == UnallocatedEntry) {
        // Allocate new block at EOF
        var newPhysIdx = _allocatedCount;
        _allocatedCount++;
        _blockMap[blockIdx] = newPhysIdx;

        // Update BAM entry in backing stream
        _backing.Position = _offsetBlocks + blockIdx * 4;
        var bamBytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bamBytes, newPhysIdx);
        _backing.Write(bamBytes);

        // Update cBlocksAllocated in header (offset 388)
        _backing.Position = 388;
        BinaryPrimitives.WriteUInt32LittleEndian(bamBytes, _allocatedCount);
        _backing.Write(bamBytes);

        // Ensure backing stream is large enough and zero-fill the block
        var blockStart = (long)_offsetData + (long)newPhysIdx * _blockSize;
        var blockEnd = blockStart + _blockSize;
        if (_backing.Length < blockEnd)
          _backing.SetLength(blockEnd);

        // Zero the new block
        var zeroBuf = new byte[_blockSize];
        _backing.Position = blockStart;
        _backing.Write(zeroBuf, 0, (int)_blockSize);
      }

      var physOffset = (long)_offsetData + (long)_blockMap[blockIdx] * _blockSize + blockOff;
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
        $"Cannot change the length of a VDI virtual disk stream " +
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

  // ── Static factory ────────────────────────────────────────────────

  /// <summary>
  /// Tries to open a <see cref="VdiStream"/> for a VDI image (dynamic or fixed).
  /// Returns <c>null</c> if the stream is not a valid VDI.
  /// </summary>
  public static VdiStream? TryOpen(Stream stream) {
    try {
      if (stream.Length < 512) return null;

      stream.Position = 64;
      Span<byte> sigBuf = stackalloc byte[4];
      stream.ReadExactly(sigBuf);
      var sig = BinaryPrimitives.ReadUInt32LittleEndian(sigBuf);
      if (sig != VdiSignature) return null;

      if (stream.Length < 392) return null;

      // Read header fields
      stream.Position = 340;
      Span<byte> hdrBuf = stackalloc byte[52]; // 340..391
      stream.ReadExactly(hdrBuf);

      var offsetBlocks = BinaryPrimitives.ReadUInt32LittleEndian(hdrBuf);        // 340
      var offsetData = BinaryPrimitives.ReadUInt32LittleEndian(hdrBuf[4..]);     // 344
      // skip cCylinders (348), cHeads (352), cSectors (356), cbSector (360), unused (364)
      var virtualSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(hdrBuf[28..]); // 368
      var blockSize = BinaryPrimitives.ReadUInt32LittleEndian(hdrBuf[36..]);     // 376
      // skip cbBlockExtra (380)
      var blockCount = BinaryPrimitives.ReadUInt32LittleEndian(hdrBuf[44..]);    // 384
      var allocatedCount = BinaryPrimitives.ReadUInt32LittleEndian(hdrBuf[48..]); // 388

      if (blockSize == 0 || virtualSize <= 0) return null;

      // Read block allocation map
      var mapBytes = new byte[blockCount * 4];
      stream.Position = offsetBlocks;
      stream.ReadExactly(mapBytes);

      var blockMap = new uint[blockCount];
      for (uint i = 0; i < blockCount; i++)
        blockMap[i] = BinaryPrimitives.ReadUInt32LittleEndian(mapBytes.AsSpan((int)(i * 4)));

      stream.Position = 0;
      return new VdiStream(stream, virtualSize, blockSize, blockCount,
                            offsetBlocks, offsetData, blockMap, allocatedCount,
                            leaveOpen: true);
    } catch {
      stream.Position = 0;
      return null;
    }
  }
}
