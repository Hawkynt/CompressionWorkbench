using Compression.Core.Checksums;
using Compression.Core.Deflate;
using Compression.Core.Streams;

namespace FileFormat.Gzip;

/// <summary>
/// Stream for reading and writing GZIP format data (RFC 1952).
/// </summary>
public sealed class GzipStream : CompressionStream {
  private readonly Crc32 _crc = new();
  private uint _originalSize;
  private GzipHeader? _header;
  private bool _headerWritten;
  private bool _headerRead;

  // Decompression state
  private DeflateDecompressor? _decompressor;

  // Compression state
  private DeflateCompressor? _compressor;
  private readonly DeflateCompressionLevel _compressionLevel;
  private MemoryStream? _deflateBuffer;

  /// <summary>
  /// Initializes a new <see cref="GzipStream"/> for decompression.
  /// </summary>
  /// <param name="stream">The stream containing GZIP data.</param>
  /// <param name="mode">The stream mode (Compress or Decompress).</param>
  /// <param name="leaveOpen">Whether to leave the inner stream open.</param>
  public GzipStream(Stream stream, CompressionStreamMode mode, bool leaveOpen = false)
    : this(stream, mode, DeflateCompressionLevel.Default, leaveOpen) {
  }

  /// <summary>
  /// Initializes a new <see cref="GzipStream"/> with a specific compression level.
  /// </summary>
  /// <param name="stream">The stream to read from or write to.</param>
  /// <param name="mode">The stream mode.</param>
  /// <param name="compressionLevel">The Deflate compression level (only used in Compress mode).</param>
  /// <param name="leaveOpen">Whether to leave the inner stream open.</param>
  public GzipStream(Stream stream, CompressionStreamMode mode, DeflateCompressionLevel compressionLevel, bool leaveOpen = false)
    : base(stream, mode, leaveOpen) {
    this._compressionLevel = compressionLevel;

    if (mode == CompressionStreamMode.Compress) {
      this._header = new GzipHeader();
      this._deflateBuffer = new MemoryStream();
      this._compressor = new DeflateCompressor(this._deflateBuffer, compressionLevel);
    }
  }

  /// <summary>
  /// Gets or sets the GZIP header. Set before writing to customize the header.
  /// </summary>
  public GzipHeader Header {
    get => this._header ??= new GzipHeader();
    set => this._header = value;
  }

  /// <summary>
  /// Gets the CRC-32 value of the uncompressed data.
  /// </summary>
  public uint Crc32Value => this._crc.Value;

  /// <summary>
  /// Gets the original (uncompressed) size mod 2^32.
  /// </summary>
  public uint OriginalSize => this._originalSize;

  private bool _trailerVerified;
  private bool _allMembersDone;

  /// <inheritdoc />
  protected override int DecompressBlock(byte[] buffer, int offset, int count) {
    if (this._allMembersDone)
      return 0;

    if (!this._headerRead) {
      // Check if the stream has any data at all
      if (InnerStream.Position >= InnerStream.Length)
        return 0;

      this._header = GzipHeader.Read(InnerStream);
      this._decompressor = new DeflateDecompressor(InnerStream);
      this._headerRead = true;
      this._trailerVerified = false;
    }

    var bytesRead = this._decompressor!.Decompress(buffer, offset, count);

    if (bytesRead > 0) {
      this._crc.Update(buffer.AsSpan(offset, bytesRead));
      this._originalSize += (uint)bytesRead;
    }
    else if (!this._trailerVerified) {
      // Deflate stream ended — read and verify trailer
      ReadAndVerifyTrailer();
      this._trailerVerified = true;

      // RFC 1952: check for another concatenated gzip member
      if (HasNextMember()) {
        // Reset state for next member
        this._crc.Reset();
        this._originalSize = 0;
        this._headerRead = false;
        this._decompressor = null;
        // Recurse to start reading the next member immediately
        return DecompressBlock(buffer, offset, count);
      }

      this._allMembersDone = true;
    }

    return bytesRead;
  }

  private bool HasNextMember() {
    // After the trailer, check if another gzip member follows (magic 1F 8B)
    var unconsumed = this._decompressor?.UnconsumedBytes ?? 0;
    // The trailer has already been consumed by ReadAndVerifyTrailer,
    // but the decompressor may have been rewound there. Check current position.

    if (!InnerStream.CanSeek) {
      // For non-seekable streams, try to peek two bytes
      var b1 = InnerStream.ReadByte();
      if (b1 < 0) return false;
      var b2 = InnerStream.ReadByte();
      if (b2 < 0) return false;
      if (b1 == GzipConstants.Magic1 && b2 == GzipConstants.Magic2) {
        // Push back by seeking (non-seekable can't do this — just return true
        // and let the header reader re-read these bytes)
        // Actually we can't push back on non-seekable, so we need to handle this.
        // For simplicity, if non-seekable and magic matches, seek back.
        return false; // non-seekable multi-member not supported
      }
      return false;
    }

    var pos = InnerStream.Position;
    if (pos + 2 > InnerStream.Length)
      return false;

    var m1 = InnerStream.ReadByte();
    var m2 = InnerStream.ReadByte();

    if (m1 == GzipConstants.Magic1 && m2 == GzipConstants.Magic2) {
      // Rewind so GzipHeader.Read can consume the full header
      InnerStream.Seek(-2, SeekOrigin.Current);
      return true;
    }

    // Not a gzip header — rewind
    InnerStream.Position = pos;
    return false;
  }

  /// <inheritdoc />
  protected override void CompressBlock(byte[] buffer, int offset, int count) {
    if (!this._headerWritten) {
      Header.Write(InnerStream);
      this._headerWritten = true;
    }

    this._crc.Update(buffer.AsSpan(offset, count));
    this._originalSize += (uint)count;

    this._compressor!.Write(buffer.AsSpan(offset, count));

    // Flush any buffered Deflate data to the inner stream
    FlushDeflateBuffer();
  }

  /// <inheritdoc />
  protected override void FinishCompression() {
    if (!this._headerWritten) {
      Header.Write(InnerStream);
      this._headerWritten = true;
    }

    this._compressor!.Finish();
    FlushDeflateBuffer();

    // Write trailer: CRC32 + ISIZE (both little-endian)
    var writer = new BinaryWriter(InnerStream, System.Text.Encoding.UTF8, leaveOpen: true);
    writer.Write(this._crc.Value);
    writer.Write(this._originalSize);
    writer.Flush();
  }

  private void FlushDeflateBuffer() {
    if (this._deflateBuffer!.Position > 0) {
      var data = this._deflateBuffer.ToArray();
      InnerStream.Write(data);
      this._deflateBuffer.SetLength(0);
      this._deflateBuffer.Position = 0;
    }
  }

  private void ReadAndVerifyTrailer() {
    // The Deflate decompressor's BitBuffer may have read bytes past the deflate
    // stream end. Rewind the inner stream so we can read the 8-byte gzip trailer.
    var unconsumed = this._decompressor!.UnconsumedBytes;
    if (unconsumed > 0 && InnerStream.CanSeek)
      InnerStream.Seek(-unconsumed, SeekOrigin.Current);

    var reader = new BinaryReader(InnerStream, System.Text.Encoding.UTF8, leaveOpen: true);

    uint expectedCrc;
    uint expectedSize;

    try {
      expectedCrc = reader.ReadUInt32();
      expectedSize = reader.ReadUInt32();
    }
    catch (EndOfStreamException) {
      throw new InvalidDataException("GZIP trailer is missing or truncated.");
    }

    if (expectedCrc != this._crc.Value)
      throw new InvalidDataException($"GZIP CRC-32 mismatch: expected 0x{expectedCrc:X8}, computed 0x{this._crc.Value:X8}.");

    if (expectedSize != this._originalSize)
      throw new InvalidDataException($"GZIP size mismatch: expected {expectedSize}, computed {this._originalSize}.");
  }
}
