using System.Buffers.Binary;
using Compression.Core.Dictionary.Lzma;

namespace FileFormat.Lrzip;

/// <summary>
/// Reads a Long Range Zip (lrzip) container. Only the LZMA subtype is decompressed;
/// other method codes are surfaced via <see cref="NotSupportedException"/> so callers
/// can still read header metadata (version, expanded size, hash) without erroring out.
/// </summary>
public sealed class LrzipReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly long _bodyOffset;
  private bool _disposed;

  /// <summary>Container major version from the header.</summary>
  public byte MajorVersion { get; }

  /// <summary>Container minor version from the header (0x06 = lrzip 0.6).</summary>
  public byte MinorVersion { get; }

  /// <summary>Original uncompressed payload size, as recorded in the header.</summary>
  public ulong ExpandedSize { get; }

  /// <summary>Compression method byte (1 = LZMA, see <see cref="LrzipConstants"/>).</summary>
  public byte Method { get; }

  /// <summary>Flag byte (bit 0 = encrypted; we do not support encryption).</summary>
  public byte Flags { get; }

  /// <summary>Hash type identifier (typically 0 = MD5 of uncompressed data).</summary>
  public byte HashType { get; }

  /// <summary>Stored hash (typically MD5 of uncompressed data) — preserved verbatim, not validated.</summary>
  public byte[] Hash { get; }

  /// <summary>
  /// Initializes a new <see cref="LrzipReader"/> from a stream and parses the 38-byte header.
  /// </summary>
  /// <param name="stream">The stream containing the lrzip container.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  public LrzipReader(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;

    if (stream.Length < LrzipConstants.HeaderSize)
      throw new InvalidDataException("Stream is too small to be a valid lrzip container.");

    Span<byte> header = stackalloc byte[LrzipConstants.HeaderSize];
    ReadExact(header);

    if (!header[..4].SequenceEqual(LrzipConstants.Magic))
      throw new InvalidDataException(
        $"Invalid lrzip magic: expected '{LrzipConstants.MagicString}', got '0x{header[0]:X2}{header[1]:X2}{header[2]:X2}{header[3]:X2}'.");

    this.MajorVersion = header[4];
    this.MinorVersion = header[5];
    this.ExpandedSize = BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(6, 8));
    this.Method = header[14];
    this.Flags = header[15];
    this.HashType = header[16];
    // header[17..22] — 5 reserved bytes, ignored
    this.Hash = header.Slice(22, 16).ToArray();

    // Encryption support is intentionally absent
    if ((this.Flags & 0x01) != 0)
      throw new NotSupportedException("lrzip encrypted containers are not supported.");

    this._bodyOffset = LrzipConstants.HeaderSize;
  }

  /// <summary>
  /// Decompresses the body and returns the full uncompressed payload.
  /// </summary>
  /// <exception cref="NotSupportedException">Thrown for any method other than LZMA.</exception>
  public byte[] Extract() {
    switch (this.Method) {
      case LrzipConstants.MethodNone: return ExtractStored();
      case LrzipConstants.MethodLzma: return ExtractLzma();
      case LrzipConstants.MethodLzo:
        throw new NotSupportedException("lrzip method LZO is not yet supported.");
      case LrzipConstants.MethodBzip2:
        throw new NotSupportedException("lrzip method BZIP2 is not yet supported.");
      case LrzipConstants.MethodGzip:
        throw new NotSupportedException("lrzip method GZIP is not yet supported.");
      case LrzipConstants.MethodZpaq:
        throw new NotSupportedException("lrzip method ZPAQ is not yet supported.");
      default:
        throw new NotSupportedException($"lrzip method {this.Method} not yet supported.");
    }
  }

  private byte[] ExtractStored() {
    this._stream.Position = this._bodyOffset;
    // Trust ExpandedSize; clamp against actual stream length to avoid huge allocations on bad input
    var available = this._stream.Length - this._bodyOffset;
    var size = (long)this.ExpandedSize;
    if (size < 0 || size > available)
      size = available;
    var data = new byte[size];
    ReadExact(data);
    return data;
  }

  private byte[] ExtractLzma() {
    this._stream.Position = this._bodyOffset;

    Span<byte> preamble = stackalloc byte[LrzipConstants.LzmaPreambleSize];
    ReadExact(preamble);

    // Build the standard 5-byte LZMA properties block from props byte + 4 LE dict size bytes
    var properties = preamble.ToArray();

    // The LZMA stream that follows is "raw" (no end-of-stream marker required) and is
    // bounded by ExpandedSize, which we pass to the decoder so it stops at the right point.
    var decoder = new LzmaDecoder(this._stream, properties, (long)this.ExpandedSize);
    using var output = new MemoryStream();
    decoder.Decode(output);
    return output.ToArray();
  }

  private void ReadExact(Span<byte> buffer) {
    var totalRead = 0;
    while (totalRead < buffer.Length) {
      var read = this._stream.Read(buffer[totalRead..]);
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of lrzip stream.");
      totalRead += read;
    }
  }

  /// <inheritdoc />
  public void Dispose() {
    if (this._disposed)
      return;
    this._disposed = true;
    if (!this._leaveOpen)
      this._stream.Dispose();
  }
}
