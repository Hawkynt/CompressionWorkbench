#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Vmdk;

/// <summary>
/// Provides seekable read/write access to the virtual disk content of a monolithic
/// sparse VMDK. Translates virtual disk offsets through the grain directory and
/// grain tables. Reads from unallocated grains return zeros. Writes to unallocated
/// grains lazily allocate a fresh grain (and grain table, when needed) at the end
/// of the backing file and update the grain directory + grain table on disk so the
/// new contents are visible on the next read.
/// </summary>
public sealed class VmdkStream : Stream {
  private static readonly byte[] SparseMagic = [0x4B, 0x44, 0x4D, 0x56]; // "KDMV" LE

  private readonly Stream _backing;
  private readonly bool _leaveOpen;
  private readonly long _diskSize;
  private readonly int _grainSizeBytes;
  private readonly int _grainTableEntries;
  private readonly long[] _grainOffsets; // virtual grain index -> byte offset in file (0 = unallocated)
  // Per-GD-index byte offset of the grain table on disk (0 = GT not yet allocated).
  private readonly long[] _gtByteOffsets;
  private readonly long _gdByteOffset;
  private readonly long[] _redundantGtByteOffsets;
  private readonly long _redundantGdByteOffset;
  private long _position;

  private VmdkStream(Stream backing, long diskSize, int grainSizeBytes, int grainTableEntries,
                     long[] grainOffsets, long[] gtByteOffsets, long gdByteOffset,
                     long[] redundantGtByteOffsets, long redundantGdByteOffset, bool leaveOpen) {
    _backing = backing;
    _diskSize = diskSize;
    _grainSizeBytes = grainSizeBytes;
    _grainTableEntries = grainTableEntries;
    _grainOffsets = grainOffsets;
    _gtByteOffsets = gtByteOffsets;
    _gdByteOffset = gdByteOffset;
    _redundantGtByteOffsets = redundantGtByteOffsets;
    _redundantGdByteOffset = redundantGdByteOffset;
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
  public override long Length => _diskSize;

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
    if (_position >= _diskSize) return 0;
    var remaining = (int)Math.Min(count, _diskSize - _position);
    var totalRead = 0;

    while (remaining > 0) {
      var grainIdx = (int)(_position / _grainSizeBytes);
      var grainOff = (int)(_position % _grainSizeBytes);
      var toRead = Math.Min(remaining, _grainSizeBytes - grainOff);

      if (grainIdx >= _grainOffsets.Length || _grainOffsets[grainIdx] == 0) {
        // Unallocated grain — return zeros
        Array.Clear(buffer, offset, toRead);
      } else {
        _backing.Position = _grainOffsets[grainIdx] + grainOff;
        var n = _backing.Read(buffer, offset, toRead);
        if (n < toRead) {
          // Backing ended early — zero the rest
          Array.Clear(buffer, offset + n, toRead - n);
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
    if (_position + count > _diskSize)
      throw new InvalidOperationException(
        $"Write would exceed virtual disk size ({_diskSize} bytes). " +
        $"Position={_position}, Count={count}.");

    var remaining = count;
    while (remaining > 0) {
      var grainIdx = (int)(_position / _grainSizeBytes);
      var grainOff = (int)(_position % _grainSizeBytes);
      var toWrite = Math.Min(remaining, _grainSizeBytes - grainOff);

      if (grainIdx >= _grainOffsets.Length)
        throw new InvalidOperationException($"Grain index {grainIdx} out of range.");
      if (_grainOffsets[grainIdx] == 0)
        AllocateGrain(grainIdx);

      _backing.Position = _grainOffsets[grainIdx] + grainOff;
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
      SeekOrigin.End => _diskSize + offset,
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
    if (value != _diskSize)
      throw new NotSupportedException(
        $"Cannot change the length of a VMDK virtual disk stream " +
        $"(current={_diskSize}, requested={value}).");
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
  /// Tries to open a <see cref="VmdkStream"/> for a sparse VMDK.
  /// Returns <c>null</c> if the stream is not a valid sparse VMDK.
  /// </summary>
  public static VmdkStream? TryOpen(Stream stream) {
    try {
      if (stream.Length < 512) return null;

      stream.Position = 0;
      Span<byte> magic = stackalloc byte[4];
      stream.ReadExactly(magic);
      if (!magic.SequenceEqual(SparseMagic))
        return null;

      // Parse sparse header — SparseExtentHeader is byte-packed (no alignment),
      // so fields sit at 12 (capacity), 20 (grainSize), 44 (numGTEsPerGT), 56 (gdOffset).
      stream.Position = 12;
      Span<byte> hdr = stackalloc byte[52]; // read bytes 12..63
      stream.ReadExactly(hdr);

      var capacity = (long)BinaryPrimitives.ReadUInt64LittleEndian(hdr);          // offset 12
      var grainSizeSectors = (long)BinaryPrimitives.ReadUInt64LittleEndian(hdr[8..]); // offset 20
      // descriptor offset/size at 28, 36 — skip
      var numGTEsPerGT = (int)BinaryPrimitives.ReadUInt32LittleEndian(hdr[32..]); // offset 44
      var rgdOffsetSectors = (long)BinaryPrimitives.ReadUInt64LittleEndian(hdr[36..]); // offset 48
      var gdOffsetSectors = (long)BinaryPrimitives.ReadUInt64LittleEndian(hdr[44..]); // offset 56

      // This stream edits grains in place, so it only takes a plain hosted sparse
      // extent: compressed / stream-optimized grains (flags bits 16-17, or the
      // grain directory deferred to the footer) and delta disks, whose unallocated
      // grains belong to a parent, are read through VmdkVirtualDisk instead.
      stream.Position = 8;
      Span<byte> flagBytes = stackalloc byte[4];
      stream.ReadExactly(flagBytes);
      var flags = BinaryPrimitives.ReadUInt32LittleEndian(flagBytes);
      if ((flags & 0x0003_0000u) != 0 || gdOffsetSectors == -1 || HasParent(stream, hdr))
        return null;
      var zeroedGte = (flags & 0x0000_0004u) != 0;

      if (numGTEsPerGT <= 0) numGTEsPerGT = 512;
      var grainSizeBytes = (int)(grainSizeSectors * 512);
      if (grainSizeBytes <= 0) return null;

      var diskSize = capacity * 512;
      var totalGrains = (int)((diskSize + grainSizeBytes - 1) / grainSizeBytes);
      var numGdEntries = (int)((capacity + (long)numGTEsPerGT * grainSizeSectors - 1) /
                               ((long)numGTEsPerGT * grainSizeSectors));

      var gdByteOffset = gdOffsetSectors * 512;
      if (gdByteOffset <= 0 || gdByteOffset + numGdEntries * 4L > stream.Length)
        return null;

      // Read grain directory
      var gdBuf = new byte[numGdEntries * 4];
      stream.Position = gdByteOffset;
      stream.ReadExactly(gdBuf);

      // Build per-grain offset array
      var grainOffsets = new long[totalGrains];
      for (var gd = 0; gd < numGdEntries; gd++) {
        var gtSectorOffset = BinaryPrimitives.ReadUInt32LittleEndian(gdBuf.AsSpan(gd * 4));
        if (gtSectorOffset == 0) continue;

        var gtByteOffset = (long)gtSectorOffset * 512;
        var entriesToRead = Math.Min(numGTEsPerGT, totalGrains - gd * numGTEsPerGT);
        if (entriesToRead <= 0) continue;

        var gtBuf = new byte[entriesToRead * 4];
        stream.Position = gtByteOffset;
        stream.ReadExactly(gtBuf);

        for (var gte = 0; gte < entriesToRead; gte++) {
          var grainIdx = gd * numGTEsPerGT + gte;
          if (grainIdx >= totalGrains) break;
          var grainSector = BinaryPrimitives.ReadUInt32LittleEndian(gtBuf.AsSpan(gte * 4));
          // With the zeroed-GTE flag, entry 1 marks a grain that reads as zeros.
          grainOffsets[grainIdx] = grainSector == 0 || (zeroedGte && grainSector == 1) ? 0 : (long)grainSector * 512;
        }
      }

      // Cache the per-GD-index byte offsets of grain tables so on-write
      // allocation can update the right GT entry without re-reading the GD.
      var gtByteOffsets = new long[numGdEntries];
      for (var gd = 0; gd < numGdEntries; gd++) {
        var gtSectorOffset = BinaryPrimitives.ReadUInt32LittleEndian(gdBuf.AsSpan(gd * 4));
        gtByteOffsets[gd] = gtSectorOffset == 0 ? 0 : (long)gtSectorOffset * 512;
      }

      var redundantGdByteOffset = (flags & 2u) != 0 && rgdOffsetSectors != 0
        ? checked(rgdOffsetSectors * 512)
        : 0;
      long[] redundantGtByteOffsets = [];
      if (redundantGdByteOffset != 0) {
        if (redundantGdByteOffset < 0 || redundantGdByteOffset > stream.Length - gdBuf.Length)
          return null;
        stream.Position = redundantGdByteOffset;
        stream.ReadExactly(gdBuf);
        redundantGtByteOffsets = new long[numGdEntries];
        for (var gd = 0; gd < numGdEntries; gd++) {
          var tableOffset = (long)BinaryPrimitives.ReadUInt32LittleEndian(gdBuf.AsSpan(gd * 4)) * 512;
          var tableBytes = Math.Min(numGTEsPerGT, totalGrains - gd * numGTEsPerGT) * 4L;
          if (tableOffset != 0 && tableOffset > stream.Length - tableBytes)
            return null;
          redundantGtByteOffsets[gd] = tableOffset;
        }
      }

      stream.Position = 0;
      return new VmdkStream(stream, diskSize, grainSizeBytes, numGTEsPerGT,
                            grainOffsets, gtByteOffsets, gdByteOffset,
                            redundantGtByteOffsets, redundantGdByteOffset, leaveOpen: true);
    } catch {
      stream.Position = 0;
      return null;
    }
  }

  /// <summary>True when the embedded descriptor names a parent (a delta disk).</summary>
  private static bool HasParent(Stream stream, ReadOnlySpan<byte> hdr) {
    var descriptorSector = (long)BinaryPrimitives.ReadUInt64LittleEndian(hdr[16..]); // offset 28
    var descriptorSectors = (long)BinaryPrimitives.ReadUInt64LittleEndian(hdr[24..]); // offset 36
    if (descriptorSector <= 0 || descriptorSectors is <= 0 or > 2048) return false;
    var offset = descriptorSector * 512;
    var length = (int)descriptorSectors * 512;
    if (offset + length > stream.Length) return false;
    var bytes = new byte[length];
    stream.Position = offset;
    stream.ReadExactly(bytes);
    var text = System.Text.Encoding.ASCII.GetString(bytes);
    var at = text.IndexOf("parentCID=", StringComparison.Ordinal);
    if (at < 0) return false;
    var value = text[(at + 10)..].Split('\n')[0].Trim();
    return !value.StartsWith("ffffffff", StringComparison.OrdinalIgnoreCase);
  }

  // ── Sparse grain allocation ──────────────────────────────────────────

  /// <summary>
  /// Allocates a fresh grain (and the owning grain table, when none exists yet)
  /// at the sector-aligned end of the backing stream, writes zero-filled
  /// placeholders, and updates the grain directory + grain table entries on
  /// disk so a subsequent read sees the new region as allocated zeros.
  /// </summary>
  private void AllocateGrain(int grainIdx) {
    var gdIdx = grainIdx / _grainTableEntries;
    var gteIdx = grainIdx % _grainTableEntries;
    if (gdIdx >= _gtByteOffsets.Length)
      throw new InvalidOperationException($"Grain {grainIdx} maps to GD index {gdIdx} outside the grain directory.");

    // Both directory/table copies must expose every newly allocated grain.
    EnsureGrainTable(gdIdx, _gtByteOffsets, _gdByteOffset);
    if (_redundantGdByteOffset != 0)
      EnsureGrainTable(gdIdx, _redundantGtByteOffsets, _redundantGdByteOffset);

    // Step 2: allocate the grain itself at the next sector-aligned EOF.
    var grainOffset = AlignUp(_backing.Length, 512);
    if (grainOffset > _backing.Length) PadZeros(_backing.Length, grainOffset - _backing.Length);
    _backing.Position = grainOffset;
    _backing.Write(new byte[_grainSizeBytes], 0, _grainSizeBytes);

    // Update GT entry on disk.
    Span<byte> gtEntry = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(gtEntry, checked((uint)(grainOffset / 512)));
    _backing.Position = _gtByteOffsets[gdIdx] + gteIdx * 4L;
    _backing.Write(gtEntry);
    if (_redundantGdByteOffset != 0) {
      _backing.Position = _redundantGtByteOffsets[gdIdx] + gteIdx * 4L;
      _backing.Write(gtEntry);
    }

    _grainOffsets[grainIdx] = grainOffset;
  }

  private void EnsureGrainTable(int gdIdx, long[] tableOffsets, long directoryOffset) {
    if (tableOffsets[gdIdx] != 0)
      return;

    var tableOffset = AlignUp(_backing.Length, 512);
    if (tableOffset > _backing.Length)
      PadZeros(_backing.Length, tableOffset - _backing.Length);
    _backing.Position = tableOffset;
    _backing.Write(new byte[_grainTableEntries * 4]);

    Span<byte> entry = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(entry, checked((uint)(tableOffset / 512)));
    _backing.Position = directoryOffset + gdIdx * 4L;
    _backing.Write(entry);
    tableOffsets[gdIdx] = tableOffset;
  }

  private void PadZeros(long startOffset, long byteCount) {
    _backing.Position = startOffset;
    var zeros = new byte[Math.Min(byteCount, 4096)];
    var remaining = byteCount;
    while (remaining > 0) {
      var chunk = (int)Math.Min(remaining, zeros.Length);
      _backing.Write(zeros, 0, chunk);
      remaining -= chunk;
    }
  }

  private static long AlignUp(long value, int alignment)
    => (value + alignment - 1) / alignment * alignment;
}
