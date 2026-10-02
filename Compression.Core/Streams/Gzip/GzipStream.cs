using System.Buffers.Binary;
using Compression.Core.Checksums;
using Compression.Core.Deflate;
using Compression.Core.Streams;

namespace FileFormat.Gzip;

/// <summary>
/// Stream for reading and writing GZIP format data (RFC 1952).
/// </summary>
/// <remarks>
/// <para>Decompression reads every member of a multi-member file in turn (RFC 1952 §2.2),
/// checking each member's CRC-32 and size; bytes after the last member that do not start
/// another one are left unread. The inner stream need not be seekable.</para>
/// <para>Compression streams straight into the inner stream; nothing is buffered beyond the
/// Deflate window.</para>
/// </remarks>
public sealed class GzipStream : CompressionStream {
  private const int TrailerSize = 8;

  private readonly Crc32 _crc = new();
  private uint _originalSize;
  private GzipHeader? _header;

  // Compression state
  private readonly DeflateCompressionLevel _compressionLevel;
  private DeflateCompressor? _compressor;

  // Decompression state
  private readonly Stream _source;
  private readonly PushbackStream? _pushback;
  private DeflateDecompressor? _decompressor;
  private bool _allMembersDone;

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
    this._source = stream;

    if (mode == CompressionStreamMode.Compress)
      this._header = new GzipHeader();
    else if (!stream.CanSeek)
      this._source = this._pushback = new(stream);
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

  /// <summary>Number of members whose header has been read so far while decompressing (RFC 1952 §2.2).</summary>
  public int MembersRead { get; private set; }

  /// <inheritdoc />
  protected override int DecompressBlock(byte[] buffer, int offset, int count)
    => this.DecompressBlock(buffer.AsSpan(offset, count));

  /// <inheritdoc />
  protected override int DecompressBlock(Span<byte> buffer) {
    while (!this._allMembersDone && !buffer.IsEmpty) {
      if (this._decompressor is null && !this.StartMember()) {
        this._allMembersDone = true;
        break;
      }

      var bytesRead = this._decompressor!.Decompress(buffer);
      if (bytesRead > 0) {
        this._crc.Update(buffer[..bytesRead]);
        this._originalSize += (uint)bytesRead;
        return bytesRead;
      }

      this.FinishMember();
      if (!this.NextMemberFollows())
        this._allMembersDone = true;
    }

    return 0;
  }

  // Reads the next member's header. Only the first member may be missing entirely: an empty
  // input decompresses to nothing.
  private bool StartMember() {
    if (this.MembersRead == 0) {
      Span<byte> first = stackalloc byte[1];
      if (this._source.Read(first) == 0)
        return false;
      this.PutBack(first);
    }

    this._header = GzipHeader.Read(this._source);
    ++this.MembersRead;
    this._crc.Reset();
    this._originalSize = 0;
    this._decompressor = new(this._source);
    return true;
  }

  private void FinishMember() {
    var decompressor = this._decompressor!;
    this._decompressor = null;

    Span<byte> trailer = stackalloc byte[TrailerSize];
    if (decompressor.ReadRemainder(trailer) != TrailerSize)
      throw new InvalidDataException("GZIP trailer is missing or truncated.");

    var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(trailer);
    var expectedSize = BinaryPrimitives.ReadUInt32LittleEndian(trailer[4..]);
    if (expectedCrc != this._crc.Value)
      throw new InvalidDataException($"GZIP CRC-32 mismatch: expected 0x{expectedCrc:X8}, computed 0x{this._crc.Value:X8}.");
    if (expectedSize != this._originalSize)
      throw new InvalidDataException($"GZIP size mismatch: expected {expectedSize}, computed {this._originalSize}.");

    // On a stream that cannot seek, the decoder's read-ahead is the start of whatever follows.
    var readAhead = decompressor.UnconsumedBytes;
    if (readAhead > 0) {
      var bytes = new byte[readAhead];
      var n = decompressor.ReadRemainder(bytes);
      this.PutBack(bytes.AsSpan(0, n));
    }
  }

  private bool NextMemberFollows() {
    Span<byte> magic = stackalloc byte[2];
    var n = this._source.ReadAtLeast(magic, magic.Length, throwOnEndOfStream: false);
    this.PutBack(magic[..n]);
    return n == 2 && magic[0] == GzipConstants.Magic1 && magic[1] == GzipConstants.Magic2;
  }

  private void PutBack(ReadOnlySpan<byte> bytes) {
    if (bytes.IsEmpty)
      return;

    if (this._pushback is not null)
      this._pushback.Unread(bytes);
    else
      this._source.Seek(-bytes.Length, SeekOrigin.Current);
  }

  /// <inheritdoc />
  protected override void CompressBlock(byte[] buffer, int offset, int count)
    => this.CompressBlock(buffer.AsSpan(offset, count));

  /// <inheritdoc />
  protected override void CompressBlock(ReadOnlySpan<byte> buffer) {
    this.EnsureCompressor();
    this._crc.Update(buffer);
    this._originalSize += (uint)buffer.Length;
    this._compressor!.Write(buffer);
  }

  /// <inheritdoc />
  protected override void FinishCompression() {
    this.EnsureCompressor();
    this._compressor!.Finish();

    // Trailer: CRC32 + ISIZE (both little-endian)
    Span<byte> trailer = stackalloc byte[TrailerSize];
    BinaryPrimitives.WriteUInt32LittleEndian(trailer, this._crc.Value);
    BinaryPrimitives.WriteUInt32LittleEndian(trailer[4..], this._originalSize);
    this.InnerStream.Write(trailer);
    this.InnerStream.Flush();
  }

  private void EnsureCompressor() {
    if (this._compressor is not null)
      return;

    this.Header.Write(this.InnerStream);
    this._compressor = new(this.InnerStream, this._compressionLevel);
  }
}
