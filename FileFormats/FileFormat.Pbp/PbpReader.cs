namespace FileFormat.Pbp;

/// <summary>
/// Reads sections from a PSP PBP archive (EBOOT.PBP and similar multi-section files).
/// </summary>
public sealed class PbpReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private bool _disposed;

  /// <summary>Gets the PBP version field from the header.</summary>
  public uint Version { get; }

  /// <summary>Gets the non-empty entries discovered in the archive, in section order.</summary>
  public IReadOnlyList<PbpEntry> Entries { get; }

  /// <summary>
  /// Initializes a new <see cref="PbpReader"/> from a stream.
  /// </summary>
  /// <param name="stream">The stream containing the PBP archive.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  public PbpReader(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;

    if (stream.Length < PbpConstants.HeaderSize)
      throw new InvalidDataException("Stream is too small to be a valid PBP archive.");

    Span<byte> header = stackalloc byte[PbpConstants.HeaderSize];
    ReadExact(header);

    if (!header[..4].SequenceEqual(PbpConstants.Magic))
      throw new InvalidDataException("Invalid PBP magic.");

    this.Version = BitConverter.ToUInt32(header[4..8]);

    var offsets = new long[PbpConstants.SectionCount];
    for (var i = 0; i < PbpConstants.SectionCount; ++i) {
      var off = BitConverter.ToUInt32(header.Slice(8 + i * 4, 4));
      if (off != 0 && off < PbpConstants.HeaderSize)
        throw new InvalidDataException($"Section offset {off} is inside the PBP header.");
      offsets[i] = off;
    }

    var fileLength = stream.Length;
    var entries = new List<PbpEntry>();

    for (var i = 0; i < PbpConstants.SectionCount; ++i) {
      var offset = offsets[i];
      if (offset == 0)
        continue;

      var nextOffset = fileLength;
      for (var j = i + 1; j < PbpConstants.SectionCount; ++j) {
        if (offsets[j] != 0) {
          nextOffset = offsets[j];
          break;
        }
      }

      var size = nextOffset - offset;
      if (size < 0)
        throw new InvalidDataException($"Section '{PbpConstants.SectionNames[i]}' has negative size.");
      if (offset + size > fileLength)
        throw new InvalidDataException($"Section '{PbpConstants.SectionNames[i]}' extends past end of stream.");

      if (size == 0)
        continue;

      entries.Add(new PbpEntry {
        Name = PbpConstants.SectionNames[i],
        Offset = offset,
        Size = size,
      });
    }

    this.Entries = entries;
  }

  /// <summary>
  /// Extracts the raw bytes for a given entry.
  /// </summary>
  /// <param name="entry">The entry to extract.</param>
  /// <returns>The section payload.</returns>
  public byte[] Extract(PbpEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);

    if (entry.Size == 0)
      return [];
    if (entry.Size > int.MaxValue)
      throw new InvalidDataException($"Section '{entry.Name}' is too large to extract into a single byte array.");

    this._stream.Position = entry.Offset;
    var buffer = new byte[entry.Size];
    ReadExact(buffer);
    return buffer;
  }

  private void ReadExact(Span<byte> buffer) {
    var totalRead = 0;
    while (totalRead < buffer.Length) {
      var read = this._stream.Read(buffer[totalRead..]);
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of PBP stream.");
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
