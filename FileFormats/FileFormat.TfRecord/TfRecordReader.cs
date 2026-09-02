namespace FileFormat.TfRecord;

/// <summary>
/// Reads records from a TensorFlow TFRecord file.
/// </summary>
public sealed class TfRecordReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private bool _disposed;

  /// <summary>Gets all records discovered in the file (corrupt records are flagged but still listed).</summary>
  public IReadOnlyList<TfRecordEntry> Entries { get; }

  /// <summary>
  /// Initializes a new <see cref="TfRecordReader"/> from a stream.
  /// </summary>
  /// <param name="stream">The seekable stream containing TFRecord data.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  public TfRecordReader(Stream stream, bool leaveOpen = false) {
    this._stream    = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
    this.Entries    = ScanRecords();
  }

  /// <summary>
  /// Extracts the raw payload bytes for a given entry.
  /// </summary>
  /// <param name="entry">The entry whose payload should be read.</param>
  /// <returns>The record's data bytes (Size bytes).</returns>
  public byte[] Extract(TfRecordEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);

    if (entry.Size == 0)
      return [];

    this._stream.Position = entry.Offset;
    var buf = new byte[entry.Size];
    ReadExact(buf);
    return buf;
  }

  private List<TfRecordEntry> ScanRecords() {
    var entries        = new List<TfRecordEntry>();
    var index          = 0;
    var anyValidParsed = false;

    this._stream.Position = 0;
    Span<byte> lengthBuf    = stackalloc byte[TfRecordConstants.LengthFieldSize];
    Span<byte> lengthCrcBuf = stackalloc byte[TfRecordConstants.LengthCrcSize];
    Span<byte> dataCrcBuf   = stackalloc byte[TfRecordConstants.DataCrcSize];

    while (this._stream.Position < this._stream.Length) {
      var recordStart = this._stream.Position;

      // Truncated length prefix at EOF: stop without throwing — caller decides what "complete" means.
      var remaining = this._stream.Length - recordStart;
      if (remaining < TfRecordConstants.LengthFieldSize + TfRecordConstants.LengthCrcSize)
        break;

      ReadExact(lengthBuf);
      ReadExact(lengthCrcBuf);

      var length          = BitConverter.ToInt64(lengthBuf);
      var storedLengthCrc = BitConverter.ToUInt32(lengthCrcBuf);
      var expectedLenCrc  = Crc32C.Mask(Crc32C.Compute(lengthBuf));
      var lengthCorrupt   = storedLengthCrc != expectedLenCrc;

      // First record's length-CRC must match for the file to even be a TFRecord.
      // After that, we tolerate corruption (flag and continue) so partial files remain inspectable.
      if (!anyValidParsed && lengthCorrupt)
        throw new InvalidDataException("TFRecord length-CRC mismatch on first record — not a valid TFRecord file.");

      // A length we can't trust (corrupt CRC) or that exceeds the remaining stream — stop scanning.
      // Without a verified length we can't safely advance to the next record.
      if (lengthCorrupt || length < 0 ||
          length > this._stream.Length - this._stream.Position - TfRecordConstants.DataCrcSize) {
        entries.Add(new TfRecordEntry {
          Name      = SynthesizeName(index++),
          Offset    = this._stream.Position,
          Size      = 0,
          IsCorrupt = true,
        });
        break;
      }

      var dataOffset = this._stream.Position;

      // Read data + data-CRC; verify CRC but don't seek-skip — we need data bytes for CRC anyway,
      // and pooling/streaming for very large records is out of scope for this format reader.
      var data = new byte[length];
      ReadExact(data);
      ReadExact(dataCrcBuf);

      var storedDataCrc = BitConverter.ToUInt32(dataCrcBuf);
      var expectedDataCrc = Crc32C.Mask(Crc32C.Compute(data));
      var dataCorrupt = storedDataCrc != expectedDataCrc;

      entries.Add(new TfRecordEntry {
        Name      = SynthesizeName(index++),
        Offset    = dataOffset,
        Size      = length,
        IsCorrupt = dataCorrupt,
      });

      anyValidParsed = true;
    }

    return entries;
  }

  private static string SynthesizeName(int index)
    => $"record_{index:D5}.bin";

  private void ReadExact(Span<byte> buffer) {
    var totalRead = 0;
    while (totalRead < buffer.Length) {
      var read = this._stream.Read(buffer[totalRead..]);
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of TFRecord stream.");
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
