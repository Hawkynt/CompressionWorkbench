#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Qcow2;

/// <summary>
/// Provides seekable read/write access to the virtual disk content of an
/// uncompressed QCOW2 v2/v3 image. Translates virtual offsets through the
/// L1 and L2 tables. Reads from unallocated clusters return zeros.
/// Writes to unallocated clusters allocate new clusters at EOF and update
/// L2 entries and refcounts.
/// </summary>
public sealed class Qcow2Stream : Stream {
  private static readonly byte[] Magic = [0x51, 0x46, 0x49, 0xFB];

  private readonly Stream _backing;
  private readonly bool _leaveOpen;
  private readonly long _virtualSize;
  private readonly int _clusterBits;
  private readonly int _clusterSize;
  private readonly int _l2Entries;
  private readonly long _l1TableOffset;
  private readonly int _l1Size;
  private readonly long _refcountTableOffset;

  // Cached L2 entry offsets: clusterIdx -> host offset (0 = unallocated, negative = compressed)
  private readonly long[] _clusterMap;
  private long _position;

  private Qcow2Stream(Stream backing, long virtualSize, int clusterBits,
                       long l1TableOffset, int l1Size, long refcountTableOffset,
                       long[] clusterMap, bool leaveOpen) {
    _backing = backing;
    _virtualSize = virtualSize;
    _clusterBits = clusterBits;
    _clusterSize = 1 << clusterBits;
    _l2Entries = _clusterSize / 8;
    _l1TableOffset = l1TableOffset;
    _l1Size = l1Size;
    _refcountTableOffset = refcountTableOffset;
    _clusterMap = clusterMap;
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
      var clusterIdx = (int)(_position >> _clusterBits);
      var clusterOff = (int)(_position & (_clusterSize - 1));
      var toRead = Math.Min(remaining, _clusterSize - clusterOff);

      if (clusterIdx >= _clusterMap.Length || _clusterMap[clusterIdx] <= 0) {
        // Unallocated or compressed — return zeros for unallocated
        Array.Clear(buffer, offset, toRead);
      } else {
        _backing.Position = _clusterMap[clusterIdx] + clusterOff;
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
      var clusterIdx = (int)(_position >> _clusterBits);
      var clusterOff = (int)(_position & (_clusterSize - 1));
      var toWrite = Math.Min(remaining, _clusterSize - clusterOff);

      if (clusterIdx < _clusterMap.Length && _clusterMap[clusterIdx] > 0) {
        // Already allocated — write in place
        _backing.Position = _clusterMap[clusterIdx] + clusterOff;
        _backing.Write(buffer, offset, toWrite);
      } else if (clusterIdx < _clusterMap.Length) {
        // Unallocated — allocate at EOF
        var newOffset = AllocateClusterAtEof();
        _clusterMap[clusterIdx] = newOffset;
        UpdateL2Entry(clusterIdx, newOffset);

        // If partial cluster write, zero-fill the rest
        if (clusterOff > 0 || toWrite < _clusterSize) {
          var zero = new byte[_clusterSize];
          _backing.Position = newOffset;
          _backing.Write(zero, 0, _clusterSize);
        }

        _backing.Position = newOffset + clusterOff;
        _backing.Write(buffer, offset, toWrite);
      } else {
        throw new InvalidOperationException($"Cluster index {clusterIdx} out of range.");
      }

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
        $"Cannot change the length of a QCOW2 virtual disk stream " +
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

  // ── Allocation helpers ────────────────────────────────────────────

  private long AllocateClusterAtEof() {
    var eof = _backing.Length;
    // Align to cluster boundary
    var aligned = ((eof + _clusterSize - 1) / _clusterSize) * _clusterSize;
    _backing.SetLength(aligned + _clusterSize);
    return aligned;
  }

  private void UpdateL2Entry(int clusterIdx, long hostOffset) {
    var l1Idx = clusterIdx / _l2Entries;
    var l2Idx = clusterIdx % _l2Entries;

    if (l1Idx >= _l1Size) return;

    // Read L1 entry to find L2 table offset
    _backing.Position = _l1TableOffset + l1Idx * 8L;
    Span<byte> l1Buf = stackalloc byte[8];
    _backing.ReadExactly(l1Buf);
    var l1Entry = BinaryPrimitives.ReadUInt64BigEndian(l1Buf);
    var l2TableOffset = (long)(l1Entry & 0x00FFFFFFFFFFFE00UL);

    if (l2TableOffset == 0) return; // L2 table not allocated — skip update

    // Write L2 entry
    _backing.Position = l2TableOffset + l2Idx * 8L;
    Span<byte> l2Buf = stackalloc byte[8];
    BinaryPrimitives.WriteUInt64BigEndian(l2Buf, (ulong)hostOffset);
    _backing.Write(l2Buf);
  }

  // ── Static factory ────────────────────────────────────────────────

  /// <summary>
  /// Tries to open a <see cref="Qcow2Stream"/> for an uncompressed QCOW2 image.
  /// Returns <c>null</c> if the stream is not a valid QCOW2 or uses unsupported features.
  /// </summary>
  public static Qcow2Stream? TryOpen(Stream stream) {
    try {
      if (stream.Length < 72) return null;

      stream.Position = 0;
      Span<byte> hdr = stackalloc byte[72];
      stream.ReadExactly(hdr);

      if (!hdr[..4].SequenceEqual(Magic)) return null;

      var version = BinaryPrimitives.ReadUInt32BigEndian(hdr[4..]);
      if (version is not (2 or 3)) return null;

      var clusterBits = (int)BinaryPrimitives.ReadUInt32BigEndian(hdr[20..]);
      if (clusterBits < 9 || clusterBits > 21) return null;

      var virtualSize = (long)BinaryPrimitives.ReadUInt64BigEndian(hdr[24..]);
      var cryptMethod = BinaryPrimitives.ReadUInt32BigEndian(hdr[32..]);
      if (cryptMethod != 0) return null; // encrypted — can't handle

      var l1Size = (int)BinaryPrimitives.ReadUInt32BigEndian(hdr[36..]);
      var l1TableOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(hdr[40..]);
      var refcountTableOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(hdr[48..]);

      var clusterSize = 1 << clusterBits;
      var l2Entries = clusterSize / 8;
      var totalClusters = (int)((virtualSize + clusterSize - 1) / clusterSize);

      // Build cluster map
      var clusterMap = new long[totalClusters];

      var l1ReadBuf = new byte[8];
      for (var l1Idx = 0; l1Idx < l1Size; l1Idx++) {
        var l1EntryOff = l1TableOffset + l1Idx * 8L;
        if (l1EntryOff + 8 > stream.Length) break;

        stream.Position = l1EntryOff;
        stream.ReadExactly(l1ReadBuf);
        var l1Entry = BinaryPrimitives.ReadUInt64BigEndian(l1ReadBuf);
        var l2TableOff = (long)(l1Entry & 0x00FFFFFFFFFFFE00UL);
        if (l2TableOff == 0) continue;

        // Read the entire L2 table
        var l2Count = Math.Min(l2Entries, totalClusters - l1Idx * l2Entries);
        if (l2Count <= 0) continue;

        var l2Buf = new byte[l2Count * 8];
        stream.Position = l2TableOff;
        stream.ReadExactly(l2Buf);

        for (var l2Idx = 0; l2Idx < l2Count; l2Idx++) {
          var clusterIdx = l1Idx * l2Entries + l2Idx;
          if (clusterIdx >= totalClusters) break;

          var l2Entry = BinaryPrimitives.ReadUInt64BigEndian(l2Buf.AsSpan(l2Idx * 8));
          if (l2Entry == 0) continue;

          var isCompressed = (l2Entry & (1UL << 62)) != 0;
          if (isCompressed) {
            clusterMap[clusterIdx] = -1; // mark as compressed — read returns zeros
          } else {
            var hostOffset = (long)(l2Entry & 0x00FFFFFFFFFFFE00UL);
            if (hostOffset > 0)
              clusterMap[clusterIdx] = hostOffset;
          }
        }
      }

      stream.Position = 0;
      return new Qcow2Stream(stream, virtualSize, clusterBits,
                              l1TableOffset, l1Size, refcountTableOffset,
                              clusterMap, leaveOpen: true);
    } catch {
      stream.Position = 0;
      return null;
    }
  }
}
