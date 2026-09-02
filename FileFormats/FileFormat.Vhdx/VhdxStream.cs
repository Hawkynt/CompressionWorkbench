#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Vhdx;

/// <summary>
/// Provides seekable read/write access to the virtual disk content of a VHDX
/// (both fixed and dynamic). For a fixed VHDX (all BAT entries FULLY_PRESENT),
/// the data blocks are mapped through the BAT. For a dynamic VHDX, blocks
/// with state PAYLOAD_BLOCK_NOT_PRESENT return zeros on read and are allocated
/// at EOF on write.
/// </summary>
public sealed class VhdxStream : Stream {
  private const ulong StateNotPresent = 0;
  private const ulong StateFullyPresent = 6;
  private const long OneMib = 0x100000;

  private readonly Stream _backing;
  private readonly long _dataLength;
  private readonly bool _leaveOpen;
  private readonly int _blockSize;
  private readonly long _batOffset;
  private readonly ulong[] _batEntries;
  private long _position;

  private VhdxStream(Stream backing, long dataLength, int blockSize,
      long batOffset, ulong[] batEntries, bool leaveOpen) {
    _backing = backing;
    _dataLength = dataLength;
    _leaveOpen = leaveOpen;
    _blockSize = blockSize;
    _batOffset = batOffset;
    _batEntries = batEntries;
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
public override long Length => _dataLength;

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
    if (_position >= _dataLength) return 0;
    var remaining = (int)Math.Min(count, _dataLength - _position);
    var totalRead = 0;

    while (remaining > 0) {
      var blockIdx = (int)(_position / _blockSize);
      var blockOff = (int)(_position % _blockSize);
      var toRead = Math.Min(remaining, _blockSize - blockOff);

      if (blockIdx >= _batEntries.Length) {
        Array.Clear(buffer, offset, toRead);
      } else {
        var entry = _batEntries[blockIdx];
        var state = entry & 0x07;

        if (state == StateFullyPresent) {
          var fileOffsetMib = entry >> 20;
          var physOffset = (long)fileOffsetMib * OneMib + blockOff;
          _backing.Position = physOffset;
          var n = _backing.Read(buffer, offset, toRead);
          if (n < toRead) Array.Clear(buffer, offset + n, toRead - n);
        } else {
          // NOT_PRESENT or any other state — return zeros
          Array.Clear(buffer, offset, toRead);
        }
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
    if (_position + count > _dataLength)
      throw new InvalidOperationException(
        $"Write would exceed virtual disk size ({_dataLength} bytes). " +
        $"Position={_position}, Count={count}.");

    var remaining = count;
    while (remaining > 0) {
      var blockIdx = (int)(_position / _blockSize);
      var blockOff = (int)(_position % _blockSize);
      var toWrite = Math.Min(remaining, _blockSize - blockOff);

      if (blockIdx >= _batEntries.Length)
        throw new InvalidOperationException($"Block index {blockIdx} out of BAT range.");

      var entry = _batEntries[blockIdx];
      var state = entry & 0x07;

      if (state != StateFullyPresent) {
        // Allocate new block at EOF
        AllocateBlock(blockIdx);
        entry = _batEntries[blockIdx];
      }

      var fileOffsetMib = entry >> 20;
      var physOffset = (long)fileOffsetMib * OneMib + blockOff;
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
      SeekOrigin.End => _dataLength + offset,
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
    if (value != _dataLength)
      throw new NotSupportedException(
        $"Cannot change the length of a VHDX virtual disk stream " +
        $"(current={_dataLength}, requested={value}).");
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

  // ── Dynamic allocation ────────────────────────────────────────────────

  /// <summary>
  /// Allocates a new block at EOF (aligned to 1 MiB) for the given block index.
  /// Zeros the block region, sets BAT entry to FULLY_PRESENT, and writes the
  /// BAT entry to the backing stream.
  /// </summary>
  private void AllocateBlock(int blockIdx) {
    // Align EOF up to 1 MiB boundary
    var eof = _backing.Length;
    var aligned = ((eof + OneMib - 1) / OneMib) * OneMib;

    // Extend the backing stream to hold the new block
    _backing.SetLength(aligned + _blockSize);

    // Zero-fill the new block region
    _backing.Position = aligned;
    var zeros = new byte[Math.Min(_blockSize, 65536)];
    var toZero = _blockSize;
    while (toZero > 0) {
      var chunk = Math.Min(toZero, zeros.Length);
      _backing.Write(zeros, 0, chunk);
      toZero -= chunk;
    }

    // Update BAT entry: FULLY_PRESENT + file offset in MiB units
    var fileOffsetMib = (ulong)aligned / (ulong)OneMib;
    var newEntry = (fileOffsetMib << 20) | StateFullyPresent;
    _batEntries[blockIdx] = newEntry;

    // Write BAT entry to backing stream
    _backing.Position = _batOffset + blockIdx * 8L;
    Span<byte> batBuf = stackalloc byte[8];
    BinaryPrimitives.WriteUInt64LittleEndian(batBuf, newEntry);
    _backing.Write(batBuf);
  }

  // ── Static factory ────────────────────────────────────────────────

  /// <summary>
  /// Tries to open a <see cref="VhdxStream"/> for a VHDX image (fixed or dynamic).
  /// Returns <c>null</c> if the stream is not a valid VHDX (too small, bad signature,
  /// has parent locator, etc.). The caller owns the returned stream and must dispose it.
  /// </summary>
  public static VhdxStream? TryOpen(Stream stream) {
    try {
      if (stream.Length < 0x110000) return null; // need at least header + metadata + BAT

      stream.Position = 0;
      Span<byte> magic = stackalloc byte[8];
      stream.ReadExactly(magic);
      if (!"vhdxfile"u8.SequenceEqual(magic))
        return null;

      // Read region table 1 at 0x30000 to find BAT and Metadata regions
      stream.Position = 0x30000;
      Span<byte> regionHdr = stackalloc byte[16];
      stream.ReadExactly(regionHdr);
      if (regionHdr[0] != (byte)'r' || regionHdr[1] != (byte)'e' ||
          regionHdr[2] != (byte)'g' || regionHdr[3] != (byte)'i')
        return null;

      var entryCount = BinaryPrimitives.ReadUInt32LittleEndian(regionHdr[8..]);

      long batOffset = 0, batLength = 0, metadataOffset = 0;
      var batGuid = new Guid("2DC27766-F623-4200-9D64-115E9BFD4A08");
      var metaGuid = new Guid("8B7CA206-4790-4B9A-B8FE-575F050F886E");

      var entryBuf = new byte[32];
      for (uint i = 0; i < entryCount && i < 2048; i++) {
        stream.Position = 0x30000 + 16 + i * 32;
        stream.ReadExactly(entryBuf);
        var guid = new Guid(entryBuf.AsSpan(0, 16));
        var offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(entryBuf.AsSpan(16));
        var length = BinaryPrimitives.ReadUInt32LittleEndian(entryBuf.AsSpan(24));

        if (guid == batGuid) { batOffset = offset; batLength = length; }
        else if (guid == metaGuid) { metadataOffset = offset; }
      }

      if (batOffset == 0 || metadataOffset == 0 || batLength == 0)
        return null;

      // Read metadata to get virtual disk size and block size
      stream.Position = metadataOffset;
      Span<byte> metaHdr = stackalloc byte[12];
      stream.ReadExactly(metaHdr);
      if (!"metadata"u8.SequenceEqual(metaHdr[..8]))
        return null;

      var metaEntryCount = BinaryPrimitives.ReadUInt16LittleEndian(metaHdr[10..]);

      var fileParamsGuid = new Guid("CAA16737-FA36-4D43-B3B6-33F0AA44E76B");
      var vdiskSizeGuid = new Guid("2FA54224-CD1B-4876-B211-5DBED83BF4B8");

      uint blockSize = 0;
      ulong virtualDiskSize = 0;

      var meBuf = new byte[32];
      var valBuf = new byte[8];
      for (int i = 0; i < metaEntryCount; i++) {
        stream.Position = metadataOffset + 32 + i * 32;
        stream.ReadExactly(meBuf);
        var itemGuid = new Guid(meBuf.AsSpan(0, 16));
        var itemOffset = BinaryPrimitives.ReadUInt32LittleEndian(meBuf.AsSpan(16));

        if (itemGuid == fileParamsGuid) {
          stream.Position = metadataOffset + itemOffset;
          stream.ReadExactly(valBuf);
          blockSize = BinaryPrimitives.ReadUInt32LittleEndian(valBuf);
          var flags = BinaryPrimitives.ReadUInt32LittleEndian(valBuf.AsSpan(4));
          // bit 1 = HasParent -> differencing image, not supported
          if ((flags & 0x02) != 0) return null;
        } else if (itemGuid == vdiskSizeGuid) {
          stream.Position = metadataOffset + itemOffset;
          stream.ReadExactly(valBuf);
          virtualDiskSize = BinaryPrimitives.ReadUInt64LittleEndian(valBuf);
        }
      }

      if (blockSize == 0 || virtualDiskSize == 0)
        return null;

      // Read all BAT entries
      var blockCount = (long)((virtualDiskSize + blockSize - 1) / blockSize);
      var maxBatEntries = batLength / 8;
      if (blockCount > (long)maxBatEntries) blockCount = (long)maxBatEntries;

      var batEntries = new ulong[blockCount];
      var batByteBuf = new byte[blockCount * 8];
      stream.Position = batOffset;
      stream.ReadExactly(batByteBuf);
      for (long i = 0; i < blockCount; i++)
        batEntries[i] = BinaryPrimitives.ReadUInt64LittleEndian(batByteBuf.AsSpan((int)(i * 8)));

      stream.Position = 0;
      return new VhdxStream(stream, (long)virtualDiskSize, (int)blockSize,
        batOffset, batEntries, leaveOpen: true);
    } catch {
      stream.Position = 0;
      return null;
    }
  }
}
