#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Vhdx;

/// <summary>
/// Seekable read/write view of the logical disk stored in a standalone VHDX.
/// Payload BAT entries are translated through the MS-VHDX chunk interleaving;
/// sector-bitmap BAT entries are skipped because standalone fixed/dynamic VHDX
/// files do not allocate sector bitmap blocks.
/// </summary>
public sealed class VhdxStream : Stream {
  private const ulong StateZero = 2;
  private const ulong StateFullyPresent = 6;
  private const long OneMib = 0x100000;

  private readonly Stream _backing;
  private readonly long _dataLength;
  private readonly bool _leaveOpen;
  private readonly int _blockSize;
  private readonly long _batOffset;
  private readonly int _chunkRatio;
  private readonly ulong[] _payloadBatEntries;
  private long _position;

  private VhdxStream(
      Stream backing,
      long dataLength,
      int blockSize,
      long batOffset,
      int chunkRatio,
      ulong[] payloadBatEntries,
      bool hasAmbiguousPayloadBlocks,
      bool leaveOpen) {
    this._backing = backing;
    this._dataLength = dataLength;
    this._blockSize = blockSize;
    this._batOffset = batOffset;
    this._chunkRatio = chunkRatio;
    this._payloadBatEntries = payloadBatEntries;
    this.HasAmbiguousPayloadBlocks = hasAmbiguousPayloadBlocks;
    this._leaveOpen = leaveOpen;
  }

  /// <summary>
  /// True when at least one payload BAT entry has a state other than ZERO or
  /// FULLY_PRESENT. Those states do not provide a single canonical byte value
  /// for a standalone rebuild, so maintenance must fail closed instead of
  /// converting them into explicit zero blocks.
  /// </summary>
  public bool HasAmbiguousPayloadBlocks { get; }

  public override bool CanRead => true;
  public override bool CanSeek => true;
  public override bool CanWrite => this._backing.CanWrite;
  public override long Length => this._dataLength;

  public override long Position {
    get => this._position;
    set {
      if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
      this._position = value;
    }
  }

  public override int Read(byte[] buffer, int offset, int count) {
    ArgumentNullException.ThrowIfNull(buffer);
    if (this._position >= this._dataLength || count <= 0) return 0;
    var remaining = (int)Math.Min(count, this._dataLength - this._position);
    var totalRead = 0;

    while (remaining > 0) {
      var blockIndex = checked((int)(this._position / this._blockSize));
      var blockOffset = checked((int)(this._position % this._blockSize));
      var take = Math.Min(remaining, this._blockSize - blockOffset);

      if (blockIndex >= this._payloadBatEntries.Length) {
        Array.Clear(buffer, offset, take);
      } else {
        var entry = this._payloadBatEntries[blockIndex];
        var state = entry & 0x07;
        if (state == StateFullyPresent) {
          var fileOffsetMib = entry >> 20;
          var physicalOffset = checked((long)fileOffsetMib * OneMib + blockOffset);
          if (physicalOffset < 0 || physicalOffset + take > this._backing.Length) {
            Array.Clear(buffer, offset, take);
          } else {
            this._backing.Position = physicalOffset;
            var read = 0;
            while (read < take) {
              var n = this._backing.Read(buffer, offset + read, take - read);
              if (n <= 0) break;
              read += n;
            }
            if (read < take) Array.Clear(buffer, offset + read, take - read);
          }
        } else {
          // ZERO is explicitly zero. For ambiguous standalone states we keep
          // the existing read behavior for compatibility, but callers can see
          // HasAmbiguousPayloadBlocks and maintenance refuses to canonicalize.
          Array.Clear(buffer, offset, take);
        }
      }

      offset += take;
      remaining -= take;
      this._position += take;
      totalRead += take;
    }

    return totalRead;
  }

  public override int Read(Span<byte> buffer) {
    if (buffer.Length == 0 || this._position >= this._dataLength) return 0;
    var temp = new byte[Math.Min(buffer.Length, 64 * 1024)];
    var total = 0;
    while (total < buffer.Length) {
      var take = Math.Min(temp.Length, buffer.Length - total);
      var n = this.Read(temp, 0, take);
      if (n <= 0) break;
      temp.AsSpan(0, n).CopyTo(buffer[total..]);
      total += n;
    }
    return total;
  }

  public override void Write(byte[] buffer, int offset, int count) {
    ArgumentNullException.ThrowIfNull(buffer);
    if (!this.CanWrite) throw new NotSupportedException("Backing stream is not writable.");
    if (this._position + count > this._dataLength)
      throw new InvalidOperationException(
        $"Write would exceed virtual disk size ({this._dataLength} bytes). Position={this._position}, Count={count}.");

    var remaining = count;
    while (remaining > 0) {
      var blockIndex = checked((int)(this._position / this._blockSize));
      var blockOffset = checked((int)(this._position % this._blockSize));
      var take = Math.Min(remaining, this._blockSize - blockOffset);
      if (blockIndex >= this._payloadBatEntries.Length)
        throw new InvalidOperationException($"Block index {blockIndex} is outside the VHDX BAT.");

      var entry = this._payloadBatEntries[blockIndex];
      if ((entry & 0x07) != StateFullyPresent) {
        this.AllocateBlock(blockIndex);
        entry = this._payloadBatEntries[blockIndex];
      }

      var fileOffsetMib = entry >> 20;
      var physicalOffset = checked((long)fileOffsetMib * OneMib + blockOffset);
      this._backing.Position = physicalOffset;
      this._backing.Write(buffer, offset, take);

      offset += take;
      remaining -= take;
      this._position += take;
    }
  }

  public override void Write(ReadOnlySpan<byte> buffer) {
    if (buffer.Length == 0) return;
    var temp = buffer.ToArray();
    this.Write(temp, 0, temp.Length);
  }

  public override long Seek(long offset, SeekOrigin origin) {
    var next = origin switch {
      SeekOrigin.Begin => offset,
      SeekOrigin.Current => this._position + offset,
      SeekOrigin.End => this._dataLength + offset,
      _ => throw new ArgumentOutOfRangeException(nameof(origin)),
    };
    if (next < 0) throw new IOException("Seek before beginning of stream.");
    this._position = next;
    return next;
  }

  public override void SetLength(long value) {
    if (value != this._dataLength)
      throw new NotSupportedException(
        $"Cannot change a VHDX guest disk length (current={this._dataLength}, requested={value}).");
  }

  public override void Flush() => this._backing.Flush();

  protected override void Dispose(bool disposing) {
    if (disposing && !this._leaveOpen) this._backing.Dispose();
    base.Dispose(disposing);
  }

  private void AllocateBlock(int blockIndex) {
    var aligned = checked((this._backing.Length + OneMib - 1) / OneMib * OneMib);
    this._backing.SetLength(checked(aligned + this._blockSize));

    this._backing.Position = aligned;
    var zeros = new byte[Math.Min(this._blockSize, 64 * 1024)];
    var remaining = this._blockSize;
    while (remaining > 0) {
      var take = Math.Min(remaining, zeros.Length);
      this._backing.Write(zeros, 0, take);
      remaining -= take;
    }

    var entry = checked(((ulong)(aligned / OneMib) << 20) | StateFullyPresent);
    this._payloadBatEntries[blockIndex] = entry;
    var rawBatIndex = VhdxWriter.PayloadBatIndex(blockIndex, this._chunkRatio);
    this._backing.Position = checked(this._batOffset + rawBatIndex * 8L);
    Span<byte> bytes = stackalloc byte[8];
    BinaryPrimitives.WriteUInt64LittleEndian(bytes, entry);
    this._backing.Write(bytes);
  }

  /// <summary>
  /// Opens a standalone VHDX guest disk. Differencing images are deliberately
  /// rejected because resolving parent chains is outside this stream's contract.
  /// </summary>
  public static VhdxStream? TryOpen(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    try {
      if (!stream.CanSeek || stream.Length < 0x110000) return null;
      stream.Position = 0;
      Span<byte> magic = stackalloc byte[8];
      stream.ReadExactly(magic);
      if (!magic.SequenceEqual("vhdxfile"u8)) return null;

      if (!TryReadRegions(stream, out var batOffset, out var batLength, out var metadataOffset))
        return null;
      if (!TryReadMetadata(stream, metadataOffset, out var blockSize, out var virtualDiskSize,
            out var logicalSectorSize, out var hasParent))
        return null;
      if (hasParent || blockSize <= 0 || virtualDiskSize <= 0 || logicalSectorSize <= 0) return null;

      var chunkBytes = checked((1L << 23) * logicalSectorSize);
      if (chunkBytes % blockSize != 0) return null;
      var chunkRatioLong = chunkBytes / blockSize;
      if (chunkRatioLong <= 0 || chunkRatioLong > int.MaxValue) return null;
      var chunkRatio = (int)chunkRatioLong;

      var payloadBlockCountLong = (virtualDiskSize + blockSize - 1) / blockSize;
      if (payloadBlockCountLong <= 0 || payloadBlockCountLong > int.MaxValue) return null;
      var payloadBlockCount = (int)payloadBlockCountLong;
      var rawBatEntries = payloadBlockCount + (payloadBlockCount - 1) / chunkRatio;
      if ((long)rawBatEntries * 8 > batLength) return null;

      var payloadEntries = new ulong[payloadBlockCount];
      var hasAmbiguousPayloadBlocks = false;
      Span<byte> entryBytes = stackalloc byte[8];
      for (var blockIndex = 0; blockIndex < payloadBlockCount; ++blockIndex) {
        var rawIndex = VhdxWriter.PayloadBatIndex(blockIndex, chunkRatio);
        var offset = checked(batOffset + rawIndex * 8L);
        if (offset < 0 || offset + 8 > stream.Length) return null;
        stream.Position = offset;
        stream.ReadExactly(entryBytes);
        var entry = BinaryPrimitives.ReadUInt64LittleEndian(entryBytes);
        payloadEntries[blockIndex] = entry;
        var state = entry & 0x07;
        if (state is not (StateZero or StateFullyPresent))
          hasAmbiguousPayloadBlocks = true;
      }

      stream.Position = 0;
      return new VhdxStream(stream, virtualDiskSize, blockSize, batOffset, chunkRatio,
        payloadEntries, hasAmbiguousPayloadBlocks, leaveOpen: true);
    } catch {
      if (stream.CanSeek) stream.Position = 0;
      return null;
    }
  }

  private static bool TryReadRegions(Stream stream, out long batOffset, out long batLength, out long metadataOffset) {
    batOffset = 0;
    batLength = 0;
    metadataOffset = 0;
    stream.Position = 0x30000;
    Span<byte> header = stackalloc byte[16];
    stream.ReadExactly(header);
    if (!header[..4].SequenceEqual("regi"u8)) return false;
    var count = BinaryPrimitives.ReadUInt32LittleEndian(header[8..12]);
    if (count > 2047) return false;

    var batGuid = new Guid("2DC27766-F623-4200-9D64-115E9BFD4A08");
    var metadataGuid = new Guid("8B7CA206-4790-4B9A-B8FE-575F050F886E");
    Span<byte> entry = stackalloc byte[32];
    for (var i = 0u; i < count; ++i) {
      stream.Position = 0x30000 + 16 + i * 32L;
      stream.ReadExactly(entry);
      var guid = new Guid(entry[..16]);
      var offset = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(entry[16..24]));
      var length = BinaryPrimitives.ReadUInt32LittleEndian(entry[24..28]);
      if (guid == batGuid) {
        batOffset = offset;
        batLength = length;
      } else if (guid == metadataGuid) {
        metadataOffset = offset;
      }
    }
    return batOffset > 0 && batLength > 0 && metadataOffset > 0;
  }

  private static bool TryReadMetadata(
      Stream stream,
      long metadataOffset,
      out int blockSize,
      out long virtualDiskSize,
      out int logicalSectorSize,
      out bool hasParent) {
    blockSize = 0;
    virtualDiskSize = 0;
    logicalSectorSize = 0;
    hasParent = false;

    stream.Position = metadataOffset;
    Span<byte> header = stackalloc byte[12];
    stream.ReadExactly(header);
    if (!header[..8].SequenceEqual("metadata"u8)) return false;
    var count = BinaryPrimitives.ReadUInt16LittleEndian(header[10..12]);

    var fileParametersGuid = new Guid("CAA16737-FA36-4D43-B3B6-33F0AA44E76B");
    var virtualDiskSizeGuid = new Guid("2FA54224-CD1B-4876-B211-5DBED83BF4B8");
    var logicalSectorGuid = new Guid("8141BF1D-A96F-4709-BA47-F233A8FAAB5F");
    Span<byte> entry = stackalloc byte[32];
    Span<byte> value = stackalloc byte[8];

    for (var i = 0; i < count; ++i) {
      stream.Position = metadataOffset + 32 + i * 32L;
      stream.ReadExactly(entry);
      var guid = new Guid(entry[..16]);
      var relativeOffset = BinaryPrimitives.ReadUInt32LittleEndian(entry[16..20]);
      var length = BinaryPrimitives.ReadUInt32LittleEndian(entry[20..24]);
      if (relativeOffset == 0 || length == 0) continue;
      var valueOffset = checked(metadataOffset + relativeOffset);
      if (valueOffset < 0 || valueOffset + length > stream.Length) return false;

      if (guid == fileParametersGuid && length >= 8) {
        stream.Position = valueOffset;
        stream.ReadExactly(value);
        blockSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(value[..4]));
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(value[4..8]);
        hasParent = (flags & 0x02) != 0;
      } else if (guid == virtualDiskSizeGuid && length >= 8) {
        stream.Position = valueOffset;
        stream.ReadExactly(value);
        virtualDiskSize = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(value));
      } else if (guid == logicalSectorGuid && length >= 4) {
        stream.Position = valueOffset;
        stream.ReadExactly(value[..4]);
        logicalSectorSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(value[..4]));
      }
    }

    return blockSize > 0 && virtualDiskSize > 0 && logicalSectorSize > 0;
  }
}
