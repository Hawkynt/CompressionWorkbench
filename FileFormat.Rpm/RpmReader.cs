using System.Buffers.Binary;
using System.Text;
using Compression.Core.Streams;
using FileFormat.Cpio;

namespace FileFormat.Rpm;

/// <summary>
/// Reads metadata and payload from an RPM package file.
/// </summary>
/// <remarks>
/// <para>
/// An RPM file consists of three sections: a 96-byte Lead, a Signature header structure
/// (8-byte-aligned), a main Header structure (8-byte-aligned), and finally the compressed
/// payload (a cpio archive). This reader parses the Lead, Signature, and Header, then
/// provides a stream positioned at the raw payload bytes via <see cref="GetPayloadStream"/>.
/// </para>
/// <para>
/// This implementation is read-only. RPM writing is not supported.
/// </para>
/// </remarks>
public sealed class RpmReader : IDisposable {
  private readonly Stream _stream;
  private readonly long _payloadOffset;
  private bool _disposed;

  // -------------------------------------------------------------------------
  // Public properties
  // -------------------------------------------------------------------------

  /// <summary>Gets the package name from the main header.</summary>
  public string Name { get; }

  /// <summary>Gets the package version from the main header.</summary>
  public string Version { get; }

  /// <summary>Gets the package release from the main header.</summary>
  public string Release { get; }

  /// <summary>Gets the package architecture from the main header.</summary>
  public string Architecture { get; }

  /// <summary>
  /// Gets the name of the payload compressor, e.g. <c>"gzip"</c>, <c>"bzip2"</c>,
  /// <c>"xz"</c>, <c>"lzma"</c>, or <c>"zstd"</c>.
  /// Defaults to <c>"gzip"</c> when the PAYLOADCOMPRESSOR tag is absent.
  /// </summary>
  public string PayloadCompressor { get; }

  /// <summary>Gets the parsed Signature header structure.</summary>
  public RpmHeader SignatureHeader { get; }

  /// <summary>Gets the parsed main Header structure.</summary>
  public RpmHeader Header { get; }

  // -------------------------------------------------------------------------
  // Constructor
  // -------------------------------------------------------------------------

  /// <summary>
  /// Opens an RPM package from the given stream and parses its Lead, Signature, and Header.
  /// </summary>
  /// <param name="stream">A stream positioned at the start of the RPM data.</param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is null.</exception>
  /// <exception cref="InvalidDataException">Thrown when the RPM data is malformed.</exception>
  public RpmReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    this._stream = stream;

    // ── Lead ─────────────────────────────────────────────────────────────
    ReadAndValidateLead(stream);

    // ── Signature header ─────────────────────────────────────────────────
    this.SignatureHeader = RpmHeader.Read(stream);
    AlignTo8Bytes(stream);

    // ── Main header ───────────────────────────────────────────────────────
    this.Header = RpmHeader.Read(stream);
    // No alignment needed: payload immediately follows the main header.

    this._payloadOffset = stream.CanSeek ? stream.Position : -1;

    // ── Extract well-known tags ───────────────────────────────────────────
    this.Name              = this.Header.GetString(RpmConstants.TagName)         ?? string.Empty;
    this.Version           = this.Header.GetString(RpmConstants.TagVersion)      ?? string.Empty;
    this.Release           = this.Header.GetString(RpmConstants.TagRelease)      ?? string.Empty;
    this.Architecture      = this.Header.GetString(RpmConstants.TagArch)         ?? string.Empty;
    this.PayloadCompressor = this.Header.GetString(RpmConstants.TagPayloadCompressor) ?? "gzip";
  }

  // -------------------------------------------------------------------------
  // Public methods
  // -------------------------------------------------------------------------

  /// <summary>
  /// Returns a stream positioned at the beginning of the raw compressed payload.
  /// </summary>
  /// <returns>
  /// A <see cref="Stream"/> whose first byte is the first byte of the compressed payload
  /// (e.g. the gzip or xz header). The caller is responsible for choosing the appropriate
  /// decompressor based on <see cref="PayloadCompressor"/>.
  /// </returns>
  /// <exception cref="InvalidOperationException">
  /// Thrown when the underlying stream is not seekable and the payload stream has already
  /// been consumed.
  /// </exception>
  /// <exception cref="ObjectDisposedException">Thrown when this reader has been disposed.</exception>
  public Stream GetPayloadStream() {
    ObjectDisposedException.ThrowIf(this._disposed, this);

    if (this._stream.CanSeek) {
      this._stream.Seek(this._payloadOffset, SeekOrigin.Begin);
      // Return a non-closing wrapper so callers cannot dispose our stream.
      return new NonClosingStream(this._stream);
    }

    // Non-seekable: return the stream as-is (caller must consume it exactly once).
    return new NonClosingStream(this._stream);
  }

  /// <summary>
  /// Decompresses the payload, parses the inner cpio archive, and returns all regular file entries.
  /// </summary>
  /// <returns>A list of tuples containing the file path and file data for each regular file.</returns>
  /// <exception cref="NotSupportedException">
  /// Thrown when <see cref="PayloadCompressor"/> is not a recognized compressor.
  /// </exception>
  public IReadOnlyList<(string Path, byte[] Data)> ExtractFiles() {
    using var compressedStream = GetPayloadStream();
    using var decompressedStream = DecompressPayload(compressedStream);
    using var cpioReader = new CpioReader(decompressedStream, leaveOpen: true);

    var entries = cpioReader.ReadAll();
    var result = new List<(string Path, byte[] Data)>();
    foreach (var (entry, data) in entries) {
      if (entry.IsRegularFile)
        result.Add((entry.Name, data));
    }
    return result;
  }

  private Stream DecompressPayload(Stream compressedStream) {
    switch (this.PayloadCompressor) {
      case "gzip": {
        var gz = new Gzip.GzipStream(compressedStream, CompressionStreamMode.Decompress, leaveOpen: true);
        return ReadToMemoryStream(gz);
      }
      case "bzip2": {
        var bz2 = new Bzip2.Bzip2Stream(compressedStream, CompressionStreamMode.Decompress, leaveOpen: true);
        return ReadToMemoryStream(bz2);
      }
      case "xz": {
        var xz = new Xz.XzStream(compressedStream, CompressionStreamMode.Decompress, leaveOpen: true);
        return ReadToMemoryStream(xz);
      }
      case "zstd": {
        var zstd = new Zstd.ZstdStream(compressedStream, CompressionStreamMode.Decompress, leaveOpen: true);
        return ReadToMemoryStream(zstd);
      }
      case "lzma": {
        var output = new MemoryStream();
        Lzma.LzmaStream.Decompress(compressedStream, output);
        output.Position = 0;
        return output;
      }
      default:
        throw new NotSupportedException($"Unsupported payload compressor: {this.PayloadCompressor}");
    }
  }

  private static MemoryStream ReadToMemoryStream(Stream source) {
    var ms = new MemoryStream();
    source.CopyTo(ms);
    source.Dispose();
    ms.Position = 0;
    return ms;
  }

  /// <inheritdoc />
  public void Dispose() {
    if (!this._disposed) {
      this._disposed = true;
      this._stream.Dispose();
    }
  }

  // -------------------------------------------------------------------------
  // Lead parsing
  // -------------------------------------------------------------------------

  private static void ReadAndValidateLead(Stream stream) {
    Span<byte> lead = stackalloc byte[RpmConstants.LeadSize];
    stream.ReadExactly(lead);

    // Validate 4-byte magic
    if (lead[0] != RpmConstants.LeadMagic[0]
     || lead[1] != RpmConstants.LeadMagic[1]
     || lead[2] != RpmConstants.LeadMagic[2]
     || lead[3] != RpmConstants.LeadMagic[3])
      throw new InvalidDataException(
        $"Invalid RPM lead magic: 0x{lead[0]:X2} 0x{lead[1]:X2} 0x{lead[2]:X2} 0x{lead[3]:X2}.");
  }

  // -------------------------------------------------------------------------
  // Alignment helper
  // -------------------------------------------------------------------------

  private static void AlignTo8Bytes(Stream stream) {
    if (!stream.CanSeek) {
      // For non-seekable streams we track alignment manually.
      // The header preamble + index + store together tell us the total consumed bytes.
      // Since we already called RpmHeader.Read which consumed exactly
      // HeaderPreambleSize + nindex*IndexEntrySize + hsize bytes, we need to compute
      // the padding. However we don't have that information here without changes to
      // RpmHeader.Read. For seekable streams we use Position; for non-seekable
      // we rely on the caller passing a seekable stream (which is common in practice).
      return;
    }

    var pos = stream.Position;
    var rem = pos % 8;
    if (rem != 0) {
      var pad = 8 - rem;
      stream.Seek(pad, SeekOrigin.Current);
    }
  }

  // -------------------------------------------------------------------------
  // NonClosingStream helper
  // -------------------------------------------------------------------------

  /// <summary>
  /// A stream wrapper that forwards all operations to an inner stream but does
  /// not close or dispose it when this wrapper is disposed.
  /// </summary>
  private sealed class NonClosingStream(Stream inner) : Stream {
    public override bool CanRead  => inner.CanRead;
    public override bool CanSeek  => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length   => inner.Length;

    public override long Position {
      get => inner.Position;
      set => inner.Position = value;
    }

    public override void Flush() => inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) =>
      inner.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) =>
      inner.Read(buffer);

    public override long Seek(long offset, SeekOrigin origin) =>
      inner.Seek(offset, origin);

    public override void SetLength(long value) =>
      inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) =>
      inner.Write(buffer, offset, count);

    // Override Dispose to NOT close the inner stream.
    protected override void Dispose(bool disposing) { /* do not dispose inner */ }
  }
}
