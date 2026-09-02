using System.Buffers.Binary;

namespace FileFormat.Dms;

/// <summary>
/// Creates an Amiga DMS (Disk Masher System) archive.
/// </summary>
public sealed class DmsWriter : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private bool _disposed;
  private bool _headerWritten;
  private long _headerPosition;
  private int _trackCount;
  private uint _totalPacked;
  private uint _totalUnpacked;
  private ushort _firstTrack = ushort.MaxValue;
  private ushort _lastTrack;

  /// <summary>
  /// Initializes a new <see cref="DmsWriter"/>.
  /// </summary>
  /// <param name="stream">The stream to write the DMS archive to.</param>
  /// <param name="leaveOpen">Whether to leave the stream open when this writer is disposed.</param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is null.</exception>
  /// <exception cref="ArgumentException">Thrown when <paramref name="stream"/> is not seekable.</exception>
  public DmsWriter(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    if (!stream.CanSeek)
      throw new ArgumentException("Stream must be seekable.", nameof(stream));
    this._leaveOpen = leaveOpen;
  }

  // ── Public API ───────────────────────────────────────────────────────────

  /// <summary>
  /// Writes the 56-byte file header. Must be called before writing any tracks.
  /// The header will be updated on dispose with correct track ranges and sizes.
  /// </summary>
  /// <param name="header">The header to write. Magic, PackedSize, UnpackedSize, From, and To are auto-filled on dispose.</param>
  public void WriteHeader(DmsHeader header) {
    ArgumentNullException.ThrowIfNull(header);

    this._headerWritten = true;
    this._headerPosition = this._stream.Position;

    Span<byte> buf = stackalloc byte[DmsConstants.FileHeaderSize];
    buf.Clear();

    // Magic.
    BinaryPrimitives.WriteUInt32BigEndian(buf, DmsConstants.MagicValue);
    // Bytes 4-7: header CRC — filled after writing all other fields.

    // Fields at offsets 8..55.
    BinaryPrimitives.WriteUInt16BigEndian(buf[8..], header.CreatorVersion);
    BinaryPrimitives.WriteUInt16BigEndian(buf[10..], header.NeededVersion);
    BinaryPrimitives.WriteUInt16BigEndian(buf[12..], header.DiskType);
    BinaryPrimitives.WriteUInt16BigEndian(buf[14..], header.CompressionMode);
    BinaryPrimitives.WriteUInt32BigEndian(buf[16..], header.InfoFlags);
    // From/To at 20-23 — will be patched on dispose.
    BinaryPrimitives.WriteUInt16BigEndian(buf[20..], header.From);
    BinaryPrimitives.WriteUInt16BigEndian(buf[22..], header.To);
    // PackedSize/UnpackedSize at 24-31 — will be patched on dispose.
    BinaryPrimitives.WriteUInt32BigEndian(buf[24..], header.PackedSize);
    BinaryPrimitives.WriteUInt32BigEndian(buf[28..], header.UnpackedSize);
    BinaryPrimitives.WriteUInt16BigEndian(buf[32..], header.LowTrack);
    BinaryPrimitives.WriteUInt16BigEndian(buf[34..], header.HighTrack);
    BinaryPrimitives.WriteUInt16BigEndian(buf[36..], header.CpuType);
    BinaryPrimitives.WriteUInt16BigEndian(buf[38..], header.CpuSpeed);
    BinaryPrimitives.WriteUInt16BigEndian(buf[40..], header.CreatedDay);
    BinaryPrimitives.WriteUInt16BigEndian(buf[42..], header.CreatedMinute);
    BinaryPrimitives.WriteUInt16BigEndian(buf[44..], header.CreatedTick);

    // Compute and store CRC of bytes 8..55.
    var crc = DmsReader.ComputeCrc16(buf[8..]);
    BinaryPrimitives.WriteUInt16BigEndian(buf[4..], crc);

    this._stream.Write(buf);
  }

  /// <summary>
  /// Compresses and writes one track to the archive.
  /// </summary>
  /// <param name="trackNumber">The track number (0-based).</param>
  /// <param name="data">The uncompressed track data.</param>
  /// <param name="compressionMode">Compression mode: 0 (None), 1 (Simple RLE), or 2 (Quick LZ77).</param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="data"/> is null.</exception>
  /// <exception cref="NotSupportedException">Thrown for unsupported compression modes.</exception>
  public void WriteTrack(int trackNumber, byte[] data, int compressionMode = 0) {
    ArgumentNullException.ThrowIfNull(data);

    // Auto-write header if not written yet.
    if (!this._headerWritten)
      WriteHeader(new DmsHeader());

    var compressed = compressionMode switch {
      DmsConstants.ModeNone      => data,
      DmsConstants.ModeSimpleRle => CompressRle(data),
      DmsConstants.ModeQuick     => CompressQuick(data),
      _ => throw new NotSupportedException($"DMS compression mode {compressionMode} is not supported for writing."),
    };

    // If compression didn't help, fall back to store.
    var actualMode = (byte)compressionMode;
    if (compressionMode != DmsConstants.ModeNone && compressed.Length >= data.Length) {
      compressed = data;
      actualMode = DmsConstants.ModeNone;
    }

    var uncompressedCrc = DmsReader.ComputeCrc16(data);
    var compressedCrc   = DmsReader.ComputeCrc16(compressed);

    // Write track header (20 bytes, big-endian).
    Span<byte> hdr = stackalloc byte[DmsConstants.TrackHeaderSize];
    hdr.Clear();

    BinaryPrimitives.WriteUInt16BigEndian(hdr, DmsConstants.TrackSignature);
    BinaryPrimitives.WriteUInt16BigEndian(hdr[2..], (ushort)trackNumber);
    // bytes 4-5: reserved (0).
    BinaryPrimitives.WriteUInt16BigEndian(hdr[6..], (ushort)compressed.Length);
    BinaryPrimitives.WriteUInt16BigEndian(hdr[8..], (ushort)data.Length);
    hdr[10] = actualMode;
    hdr[11] = 0; // flags
    BinaryPrimitives.WriteUInt16BigEndian(hdr[12..], compressedCrc);
    // bytes 14-15: padding for compressed CRC field (keep 0).
    BinaryPrimitives.WriteUInt16BigEndian(hdr[16..], uncompressedCrc);
    // bytes 18-19: padding for uncompressed CRC field (keep 0).

    this._stream.Write(hdr);
    this._stream.Write(compressed);

    // Track bookkeeping.
    var tn = (ushort)trackNumber;
    if (tn < this._firstTrack) this._firstTrack = tn;
    if (tn > this._lastTrack) this._lastTrack = tn;
    this._totalPacked += (uint)compressed.Length;
    this._totalUnpacked += (uint)data.Length;
    this._trackCount++;
  }

  /// <summary>
  /// Convenience method: splits a disk image into tracks and writes all of them.
  /// </summary>
  /// <param name="diskImage">The complete disk image.</param>
  /// <param name="compressionMode">Compression mode to use for each track.</param>
  /// <param name="trackSize">The size of each track in bytes. Defaults to 11264 (one cylinder).</param>
  public void WriteDisk(byte[] diskImage, int compressionMode = 0, int trackSize = DmsConstants.CylinderSize) {
    ArgumentNullException.ThrowIfNull(diskImage);

    var trackCount = (diskImage.Length + trackSize - 1) / trackSize;

    WriteHeader(new DmsHeader {
      CompressionMode = (ushort)compressionMode,
      LowTrack        = 0,
      HighTrack       = (ushort)(trackCount - 1),
    });

    for (var i = 0; i < trackCount; i++) {
      var offset = i * trackSize;
      var length = Math.Min(trackSize, diskImage.Length - offset);
      var trackData = new byte[length];
      Array.Copy(diskImage, offset, trackData, 0, length);
      WriteTrack(i, trackData, compressionMode);
    }
  }

  // ── Compression: Mode 1 (Simple RLE) ────────────────────────────────────

  private static byte[] CompressRle(byte[] data) {
    using var ms = new MemoryStream();

    var i = 0;
    while (i < data.Length) {
      var b = data[i];

      // Count run length.
      var runLen = 1;
      while (i + runLen < data.Length && data[i + runLen] == b && runLen < 255)
        runLen++;

      if (b == DmsConstants.RleEscape) {
        if (runLen == 1) {
          // Literal 0x90 → 0x90 0x00.
          ms.WriteByte(DmsConstants.RleEscape);
          ms.WriteByte(0x00);
        } else {
          // Run of 0x90 → 0x90 0x90 count.
          ms.WriteByte(DmsConstants.RleEscape);
          ms.WriteByte(DmsConstants.RleEscape);
          ms.WriteByte((byte)runLen);
        }
        i += runLen;
      } else if (runLen >= 3) {
        // Encode as RLE run: 0x90 byte count.
        ms.WriteByte(DmsConstants.RleEscape);
        ms.WriteByte(b);
        ms.WriteByte((byte)runLen);
        i += runLen;
      } else {
        // Literal byte(s).
        ms.WriteByte(b);
        i++;
      }
    }

    return ms.ToArray();
  }

  // ── Compression: Mode 2 (Quick — LZ77) ──────────────────────────────────

  private static byte[] CompressQuick(byte[] data) {
    using var ms = new MemoryStream();

    // Simple hash chain for match finding (window = 4095 to fit 12-bit offset).
    const int windowSize = 4095;
    const int maxLength  = 17; // 4-bit length + 2 = max 17.
    const int minMatch   = 3;  // Only emit match if length >= 3.

    var i = 0;
    while (i < data.Length) {
      var bestLen    = 0;
      var bestOffset = 0;

      // Search for matches in the sliding window.
      var searchStart = Math.Max(0, i - windowSize);
      for (var j = searchStart; j < i; j++) {
        var len = 0;
        while (len < maxLength && i + len < data.Length && data[j + len] == data[i + len])
          len++;
        if (len > bestLen) {
          bestLen    = len;
          bestOffset = i - j;
        }
      }

      if (bestLen >= minMatch) {
        // Emit match token: high nibble = length-2, low 12 bits = offset.
        var token = ((bestLen - 2) << 12) | (bestOffset & 0x0FFF);
        ms.WriteByte((byte)(token >> 8));
        ms.WriteByte((byte)(token & 0xFF));
        i += bestLen;
      } else {
        // Emit literal: offset=0 token followed by literal byte.
        ms.WriteByte(0x00);
        ms.WriteByte(0x00);
        ms.WriteByte(data[i]);
        i++;
      }
    }

    return ms.ToArray();
  }

  // ── Finalization ─────────────────────────────────────────────────────────

  private void PatchHeader() {
    if (this._trackCount == 0)
      return;

    var currentPos = this._stream.Position;
    this._stream.Position = this._headerPosition;

    // Re-read the header to patch it.
    Span<byte> buf = stackalloc byte[DmsConstants.FileHeaderSize];
    ReadFully(this._stream, buf);

    // Patch From/To.
    BinaryPrimitives.WriteUInt16BigEndian(buf[20..], this._firstTrack);
    BinaryPrimitives.WriteUInt16BigEndian(buf[22..], this._lastTrack);

    // Patch packed/unpacked sizes.
    BinaryPrimitives.WriteUInt32BigEndian(buf[24..], this._totalPacked);
    BinaryPrimitives.WriteUInt32BigEndian(buf[28..], this._totalUnpacked);

    // Recompute header CRC.
    var crc = DmsReader.ComputeCrc16(buf[8..]);
    BinaryPrimitives.WriteUInt16BigEndian(buf[4..], crc);

    // Write back.
    this._stream.Position = this._headerPosition;
    this._stream.Write(buf);

    this._stream.Position = currentPos;
  }

  private static void ReadFully(Stream stream, Span<byte> buffer) {
    var offset = 0;
    while (offset < buffer.Length) {
      var read = stream.Read(buffer[offset..]);
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of stream while patching DMS header.");
      offset += read;
    }
  }

  // ── IDisposable ──────────────────────────────────────────────────────────

  /// <inheritdoc />
  public void Dispose() {
    if (!this._disposed) {
      this._disposed = true;
      PatchHeader();
      this._stream.Flush();
      if (!this._leaveOpen)
        this._stream.Dispose();
    }
  }
}
