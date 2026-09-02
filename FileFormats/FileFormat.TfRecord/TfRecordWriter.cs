namespace FileFormat.TfRecord;

/// <summary>
/// Writes records to a TensorFlow TFRecord file.
/// </summary>
public sealed class TfRecordWriter : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private bool _disposed;

  /// <summary>
  /// Initializes a new <see cref="TfRecordWriter"/>.
  /// </summary>
  /// <param name="stream">The output stream to write TFRecord framing to.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  public TfRecordWriter(Stream stream, bool leaveOpen = false) {
    this._stream    = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
  }

  /// <summary>
  /// Appends a record. Records are written immediately — no buffering — since each one is
  /// self-contained framing.
  /// </summary>
  /// <param name="data">The record payload bytes.</param>
  public void AddRecord(ReadOnlySpan<byte> data) {
    ObjectDisposedException.ThrowIf(this._disposed, this);

    Span<byte> lengthBuf = stackalloc byte[TfRecordConstants.LengthFieldSize];
    BitConverter.TryWriteBytes(lengthBuf, (ulong)data.Length);

    Span<byte> crcBuf = stackalloc byte[TfRecordConstants.LengthCrcSize];

    // length
    this._stream.Write(lengthBuf);

    // length-CRC = mask(crc32c(length-bytes))
    BitConverter.TryWriteBytes(crcBuf, Crc32C.Mask(Crc32C.Compute(lengthBuf)));
    this._stream.Write(crcBuf);

    // data
    if (data.Length > 0)
      this._stream.Write(data);

    // data-CRC = mask(crc32c(data-bytes))
    BitConverter.TryWriteBytes(crcBuf, Crc32C.Mask(Crc32C.Compute(data)));
    this._stream.Write(crcBuf);
  }

  /// <summary>Convenience overload for byte arrays.</summary>
  public void AddRecord(byte[] data) {
    ArgumentNullException.ThrowIfNull(data);
    this.AddRecord((ReadOnlySpan<byte>)data);
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
