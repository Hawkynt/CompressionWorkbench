using System.Text;
using Compression.Core.Checksums;
using Compression.Core.Dictionary.Lzx;

namespace FileFormat.Lzx;

/// <summary>
/// Creates an Amiga LZX archive.
/// </summary>
public sealed class LzxAmigaWriter : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private bool _disposed;

  /// <summary>
  /// Initializes a new <see cref="LzxAmigaWriter"/>.
  /// </summary>
  /// <param name="stream">The stream to write the archive to.</param>
  /// <param name="leaveOpen">Whether to leave the stream open when this writer is disposed.</param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is null.</exception>
  public LzxAmigaWriter(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;

    // Write the 3-byte magic signature
    this._stream.Write(LzxAmigaConstants.Magic);
  }

  /// <summary>
  /// Adds a file to the archive using Store method (no compression).
  /// </summary>
  /// <param name="name">The filename to store in the archive.</param>
  /// <param name="data">The file data.</param>
  /// <param name="lastModified">
  /// The last-modified timestamp. Defaults to <see cref="DateTime.UtcNow"/> if not specified.
  /// </param>
  /// <exception cref="ArgumentNullException">
  /// Thrown when <paramref name="name"/> or <paramref name="data"/> is null.
  /// </exception>
  /// <exception cref="ObjectDisposedException">Thrown when the writer has been disposed.</exception>
  public void AddFile(string name, byte[] data, DateTime? lastModified = null) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    ObjectDisposedException.ThrowIf(this._disposed, this);

    var dataCrc = Crc32.Compute(data);
    var timestamp = lastModified ?? DateTime.UtcNow;

    this.WriteEntryHeader(
      name,
      originalSize: (uint)data.Length,
      compressedSize: (uint)data.Length,
      method: LzxAmigaConstants.MethodStored,
      flags: 0,
      dataCrc: dataCrc,
      timestamp: timestamp
    );

    this._stream.Write(data, 0, data.Length);
  }

  /// <summary>
  /// Adds a file to the archive using LZX compression.
  /// </summary>
  /// <param name="name">The filename to store in the archive.</param>
  /// <param name="data">The file data.</param>
  /// <param name="lastModified">
  /// The last-modified timestamp. Defaults to <see cref="DateTime.UtcNow"/> if not specified.
  /// </param>
  /// <exception cref="ArgumentNullException">
  /// Thrown when <paramref name="name"/> or <paramref name="data"/> is null.
  /// </exception>
  /// <exception cref="ObjectDisposedException">Thrown when the writer has been disposed.</exception>
  public void AddFileLzx(string name, byte[] data, DateTime? lastModified = null) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    ObjectDisposedException.ThrowIf(this._disposed, this);

    var dataCrc = Crc32.Compute(data);
    var timestamp = lastModified ?? DateTime.UtcNow;

    // Compress using the LZX algorithm
    var compressor = new LzxCompressor(LzxAmigaConstants.DefaultWindowBits);
    var compressed = compressor.Compress(data);

    // Fall back to stored if compression doesn't help
    if (compressed.Length >= data.Length) {
      this.WriteEntryHeader(
        name,
        originalSize: (uint)data.Length,
        compressedSize: (uint)data.Length,
        method: LzxAmigaConstants.MethodStored,
        flags: 0,
        dataCrc: dataCrc,
        timestamp: timestamp
      );
      this._stream.Write(data, 0, data.Length);
      return;
    }

    this.WriteEntryHeader(
      name,
      originalSize: (uint)data.Length,
      compressedSize: (uint)compressed.Length,
      method: LzxAmigaConstants.MethodLzx,
      flags: 0,
      dataCrc: dataCrc,
      timestamp: timestamp
    );

    this._stream.Write(compressed, 0, compressed.Length);
  }

  /// <inheritdoc />
  public void Dispose() {
    if (this._disposed)
      return;
    this._disposed = true;
    if (!this._leaveOpen)
      this._stream.Dispose();
  }

  // ── Header writing ─────────────────────────────────────────────────────

  private void WriteEntryHeader(
    string name,
    uint originalSize,
    uint compressedSize,
    byte method,
    byte flags,
    uint dataCrc,
    DateTime timestamp) {
    var filenameBytes = Encoding.Latin1.GetBytes(name);
    if (filenameBytes.Length > LzxAmigaConstants.MaxFilenameLength)
      filenameBytes = filenameBytes[..LzxAmigaConstants.MaxFilenameLength];

    var header = new byte[LzxAmigaConstants.FixedHeaderSize + filenameBytes.Length];

    // Attributes (2 bytes LE) — default: read + write
    var attributes = (ushort)(LzxAmigaConstants.AttrRead | LzxAmigaConstants.AttrWrite);
    header[LzxAmigaConstants.OffsetAttributes] = (byte)(attributes & 0xFF);
    header[LzxAmigaConstants.OffsetAttributes + 1] = (byte)((attributes >> 8) & 0xFF);

    // Uncompressed size (4 bytes LE)
    BitConverter.TryWriteBytes(header.AsSpan(LzxAmigaConstants.OffsetUncompressedSize), originalSize);

    // Compressed size (4 bytes LE)
    BitConverter.TryWriteBytes(header.AsSpan(LzxAmigaConstants.OffsetCompressedSize), compressedSize);

    // Machine type
    header[LzxAmigaConstants.OffsetMachineType] = LzxAmigaConstants.MachinePc;

    // Compression method
    header[LzxAmigaConstants.OffsetMethod] = method;

    // Flags
    header[LzxAmigaConstants.OffsetFlags] = flags;

    // Comment length (0)
    header[LzxAmigaConstants.OffsetCommentLength] = 0;

    // Extract version
    header[LzxAmigaConstants.OffsetExtractVersion] = 0;

    // Pad
    header[LzxAmigaConstants.OffsetPad] = 0;

    // Date (4 bytes LE — packed Amiga date)
    var packedDate = LzxAmigaReader.EncodeAmigaDate(timestamp);
    BitConverter.TryWriteBytes(header.AsSpan(LzxAmigaConstants.OffsetDate), packedDate);

    // Data CRC-32 (4 bytes LE)
    BitConverter.TryWriteBytes(header.AsSpan(LzxAmigaConstants.OffsetDataCrc), dataCrc);

    // Filename length
    header[LzxAmigaConstants.OffsetFilenameLength] = (byte)filenameBytes.Length;

    // Filename
    filenameBytes.CopyTo(header.AsSpan(LzxAmigaConstants.FixedHeaderSize));

    // Compute header CRC-32 over the entire header (with the CRC field zeroed)
    // Zero out the header CRC field before computing
    header[LzxAmigaConstants.OffsetHeaderCrc] = 0;
    header[LzxAmigaConstants.OffsetHeaderCrc + 1] = 0;
    header[LzxAmigaConstants.OffsetHeaderCrc + 2] = 0;
    header[LzxAmigaConstants.OffsetHeaderCrc + 3] = 0;
    var headerCrc = Crc32.Compute(header);
    BitConverter.TryWriteBytes(header.AsSpan(LzxAmigaConstants.OffsetHeaderCrc), headerCrc);

    this._stream.Write(header, 0, header.Length);
  }
}
