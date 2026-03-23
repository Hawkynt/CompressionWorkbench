using System.Buffers.Binary;

namespace FileFormat.Dms;

/// <summary>
/// Reads and extracts tracks from an Amiga DMS (Disk Masher System) archive.
/// </summary>
public sealed class DmsReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<DmsTrack> _tracks = [];
  private bool _disposed;

  /// <summary>Gets the file header.</summary>
  public DmsHeader Header { get; private set; } = null!;

  /// <summary>Gets the track entries present in the archive.</summary>
  public IReadOnlyList<DmsTrack> Entries => this._tracks;

  /// <summary>
  /// Initializes a new <see cref="DmsReader"/> and parses the archive.
  /// </summary>
  /// <param name="stream">A seekable stream containing a DMS archive.</param>
  /// <param name="leaveOpen">Whether to leave the stream open when this reader is disposed.</param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is null.</exception>
  /// <exception cref="ArgumentException">Thrown when <paramref name="stream"/> is not seekable.</exception>
  /// <exception cref="InvalidDataException">Thrown when the stream does not contain a valid DMS archive.</exception>
  public DmsReader(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    if (!stream.CanSeek)
      throw new ArgumentException("Stream must be seekable.", nameof(stream));
    this._leaveOpen = leaveOpen;

    ReadArchive();
  }

  // ── Parsing ──────────────────────────────────────────────────────────────

  private void ReadArchive() {
    this._stream.Position = 0;

    if (this._stream.Length < DmsConstants.FileHeaderSize)
      ThrowInvalid("Stream is too short to be a DMS archive.");

    Span<byte> headerBuf = stackalloc byte[DmsConstants.FileHeaderSize];
    ReadFully(this._stream, headerBuf);

    // Verify magic.
    var magic = BinaryPrimitives.ReadUInt32BigEndian(headerBuf);
    if (magic != DmsConstants.MagicValue)
      ThrowInvalid($"Invalid DMS magic: expected 0x{DmsConstants.MagicValue:X8}, got 0x{magic:X8}.");

    // Verify header CRC (CRC-16 of bytes 8..55).
    var storedCrc = BinaryPrimitives.ReadUInt16BigEndian(headerBuf[4..]);
    var computedCrc = ComputeCrc16(headerBuf[8..]);
    if (storedCrc != computedCrc)
      ThrowInvalid($"DMS header CRC mismatch: expected 0x{storedCrc:X4}, computed 0x{computedCrc:X4}.");

    this.Header = new DmsHeader {
      Magic           = magic,
      CreatorVersion  = BinaryPrimitives.ReadUInt16BigEndian(headerBuf[8..]),
      NeededVersion   = BinaryPrimitives.ReadUInt16BigEndian(headerBuf[10..]),
      DiskType        = BinaryPrimitives.ReadUInt16BigEndian(headerBuf[12..]),
      CompressionMode = BinaryPrimitives.ReadUInt16BigEndian(headerBuf[14..]),
      InfoFlags       = BinaryPrimitives.ReadUInt32BigEndian(headerBuf[16..]),
      From            = BinaryPrimitives.ReadUInt16BigEndian(headerBuf[20..]),
      To              = BinaryPrimitives.ReadUInt16BigEndian(headerBuf[22..]),
      PackedSize      = BinaryPrimitives.ReadUInt32BigEndian(headerBuf[24..]),
      UnpackedSize    = BinaryPrimitives.ReadUInt32BigEndian(headerBuf[28..]),
      LowTrack        = BinaryPrimitives.ReadUInt16BigEndian(headerBuf[32..]),
      HighTrack       = BinaryPrimitives.ReadUInt16BigEndian(headerBuf[34..]),
      CpuType         = BinaryPrimitives.ReadUInt16BigEndian(headerBuf[36..]),
      CpuSpeed        = BinaryPrimitives.ReadUInt16BigEndian(headerBuf[38..]),
      CreatedDay      = BinaryPrimitives.ReadUInt16BigEndian(headerBuf[40..]),
      CreatedMinute   = BinaryPrimitives.ReadUInt16BigEndian(headerBuf[42..]),
      CreatedTick     = BinaryPrimitives.ReadUInt16BigEndian(headerBuf[44..]),
    };

    // Read track headers sequentially.
    while (this._stream.Position + DmsConstants.TrackHeaderSize <= this._stream.Length) {
      var track = TryReadTrack();
      if (track == null)
        break;
      this._tracks.Add(track);
    }
  }

  private DmsTrack? TryReadTrack() {
    Span<byte> buf = stackalloc byte[DmsConstants.TrackHeaderSize];
    var read = this._stream.Read(buf);
    if (read < DmsConstants.TrackHeaderSize)
      return null;

    var sig = BinaryPrimitives.ReadUInt16BigEndian(buf);
    if (sig != DmsConstants.TrackSignature)
      return null;

    var trackNumber     = BinaryPrimitives.ReadUInt16BigEndian(buf[2..]);
    // bytes 4-5: reserved
    var compressedSize  = BinaryPrimitives.ReadUInt16BigEndian(buf[6..]);
    var uncompressedSize = BinaryPrimitives.ReadUInt16BigEndian(buf[8..]);
    var   mode            = buf[10];
    var   flags           = buf[11];
    var compressedCrc   = BinaryPrimitives.ReadUInt16BigEndian(buf[12..]);
    // Note: bytes 12-15 store compressed CRC as 4 bytes in some implementations,
    // but the CRC-16 occupies the first 2 bytes. bytes 14-15 may be padding.
    var uncompressedCrc = BinaryPrimitives.ReadUInt16BigEndian(buf[16..]);

    var dataOffset = this._stream.Position;

    // Skip past the compressed data.
    this._stream.Seek(compressedSize, SeekOrigin.Current);

    return new DmsTrack {
      TrackNumber      = trackNumber,
      CompressedSize   = compressedSize,
      UncompressedSize = uncompressedSize,
      CompressionMode  = mode,
      Flags            = flags,
      CompressedCrc    = compressedCrc,
      UncompressedCrc  = uncompressedCrc,
      DataOffset       = dataOffset,
    };
  }

  // ── Extraction ───────────────────────────────────────────────────────────

  /// <summary>
  /// Extracts and decompresses the data for the given track.
  /// </summary>
  /// <param name="track">The track to extract.</param>
  /// <returns>The decompressed track data.</returns>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="track"/> is null.</exception>
  /// <exception cref="NotSupportedException">Thrown when the track uses an unsupported compression mode.</exception>
  /// <exception cref="InvalidDataException">Thrown when CRC verification fails.</exception>
  public byte[] Extract(DmsTrack track) {
    ArgumentNullException.ThrowIfNull(track);

    this._stream.Position = track.DataOffset;
    var compressed = new byte[track.CompressedSize];
    ReadFully(this._stream, compressed);

    // Verify compressed data CRC.
    var compCrc = ComputeCrc16(compressed);
    if (compCrc != track.CompressedCrc)
      ThrowInvalid($"CRC mismatch for compressed data on track {track.TrackNumber}: " +
                   $"expected 0x{track.CompressedCrc:X4}, computed 0x{compCrc:X4}.");

    var data = track.CompressionMode switch {
      DmsConstants.ModeNone      => compressed,
      DmsConstants.ModeSimpleRle => DecompressRle(compressed, track.UncompressedSize),
      DmsConstants.ModeQuick     => DecompressQuick(compressed, track.UncompressedSize),
      DmsConstants.ModeMedium    => throw new NotSupportedException("DMS Medium (mode 3) decompression is not supported."),
      DmsConstants.ModeDeep      => throw new NotSupportedException("DMS Deep (mode 4) decompression is not supported."),
      DmsConstants.ModeHeavy1    => throw new NotSupportedException("DMS Heavy1 (mode 5) decompression is not supported."),
      DmsConstants.ModeHeavy2    => throw new NotSupportedException("DMS Heavy2 (mode 6) decompression is not supported."),
      _                          => throw new NotSupportedException($"Unknown DMS compression mode: {track.CompressionMode}."),
    };

    // Verify decompressed data CRC.
    var dataCrc = ComputeCrc16(data);
    if (dataCrc != track.UncompressedCrc)
      ThrowInvalid($"CRC mismatch for decompressed data on track {track.TrackNumber}: " +
                   $"expected 0x{track.UncompressedCrc:X4}, computed 0x{dataCrc:X4}.");

    return data;
  }

  /// <summary>
  /// Extracts the entire disk image by concatenating all decompressed tracks in order.
  /// </summary>
  /// <returns>The full disk image as a byte array.</returns>
  public byte[] ExtractDisk() {
    using var ms = new MemoryStream();
    foreach (var track in this._tracks) {
      var data = Extract(track);
      ms.Write(data);
    }
    return ms.ToArray();
  }

  // ── Decompression: Mode 1 (Simple RLE) ──────────────────────────────────

  private static byte[] DecompressRle(byte[] compressed, int uncompressedSize) {
    var output = new byte[uncompressedSize];
    var srcPos = 0;
    var dstPos = 0;

    while (srcPos < compressed.Length && dstPos < uncompressedSize) {
      var b = compressed[srcPos++];
      if (b != DmsConstants.RleEscape) {
        output[dstPos++] = b;
      } else {
        if (srcPos >= compressed.Length)
          break;

        var next = compressed[srcPos++];
        if (next == 0x00) {
          // Literal 0x90.
          output[dstPos++] = DmsConstants.RleEscape;
        } else {
          // Run: next = repeat byte, then count byte follows.
          if (srcPos >= compressed.Length)
            break;
          var count = compressed[srcPos++];
          int runLength = count;
          for (var i = 0; i < runLength && dstPos < uncompressedSize; i++)
            output[dstPos++] = next;
        }
      }
    }

    return output;
  }

  // ── Decompression: Mode 2 (Quick — LZ77) ────────────────────────────────

  private static byte[] DecompressQuick(byte[] compressed, int uncompressedSize) {
    var output = new byte[uncompressedSize];
    var srcPos = 0;
    var dstPos = 0;

    while (srcPos < compressed.Length && dstPos < uncompressedSize) {
      if (srcPos + 1 >= compressed.Length)
        break;

      // Read 2-byte token (big-endian).
      var token = (compressed[srcPos] << 8) | compressed[srcPos + 1];
      srcPos += 2;

      var offset = token & 0x0FFF;  // Low 12 bits = offset.
      var length = (token >> 12) & 0x0F; // High nibble = length - 2.

      if (offset == 0) {
        // Literal byte: length field is ignored, next byte is literal.
        if (srcPos >= compressed.Length)
          break;
        output[dstPos++] = compressed[srcPos++];
      } else {
        // Back-reference: copy length+2 bytes from offset back in output.
        var matchLen = length + 2;
        var matchPos = dstPos - offset;
        if (matchPos < 0)
          ThrowInvalid($"Quick decompression: invalid back-reference offset {offset} at position {dstPos}.");

        for (var i = 0; i < matchLen && dstPos < uncompressedSize; i++)
          output[dstPos++] = output[matchPos + i];
      }
    }

    return output;
  }

  // ── CRC-16/CCITT (forward, non-reflected, init=0) ──────────────────────

  private static readonly ushort[] Crc16Table = BuildCrc16Table();

  private static ushort[] BuildCrc16Table() {
    const ushort poly = DmsConstants.CrcPolynomial;
    var table = new ushort[256];
    for (var i = 0; i < 256; i++) {
      var crc = (ushort)(i << 8);
      for (var j = 0; j < 8; j++)
        crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ poly) : (ushort)(crc << 1);
      table[i] = crc;
    }
    return table;
  }

  internal static ushort ComputeCrc16(ReadOnlySpan<byte> data) {
    ushort crc = 0;
    foreach (var b in data)
      crc = (ushort)((crc << 8) ^ Crc16Table[(byte)(crc >> 8) ^ b]);
    return crc;
  }

  // ── Helpers ──────────────────────────────────────────────────────────────

  private static void ReadFully(Stream stream, Span<byte> buffer) {
    var offset = 0;
    while (offset < buffer.Length) {
      var read = stream.Read(buffer[offset..]);
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of DMS archive data.");
      offset += read;
    }
  }

  private static void ThrowInvalid(string message) =>
    throw new InvalidDataException(message);

  /// <inheritdoc />
  public void Dispose() {
    if (!this._disposed) {
      this._disposed = true;
      if (!this._leaveOpen)
        this._stream.Dispose();
    }
  }
}
