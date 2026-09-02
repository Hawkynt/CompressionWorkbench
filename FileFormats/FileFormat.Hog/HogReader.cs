using System.Text;

namespace FileFormat.Hog;

/// <summary>
/// Reads entries from a Descent I/II HOG archive.
/// </summary>
public sealed class HogReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private bool _disposed;

  /// <summary>The HOG magic bytes at offset 0.</summary>
  public static ReadOnlySpan<byte> Magic => "DHF"u8;

  /// <summary>Gets all file entries in the HOG archive.</summary>
  public IReadOnlyList<HogEntry> Entries { get; }

  /// <summary>
  /// Initializes a new <see cref="HogReader"/> from a stream.
  /// </summary>
  /// <param name="stream">The stream containing the HOG archive.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  public HogReader(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;

    // Validate magic
    Span<byte> magic = stackalloc byte[3];
    ReadExact(magic);
    if (!magic.SequenceEqual(Magic))
      throw new InvalidDataException(
        $"Invalid HOG magic: expected \"DHF\", got \"{Encoding.ASCII.GetString(magic)}\".");

    this.Entries = ReadEntries();
  }

  /// <summary>
  /// Extracts the data for a given entry.
  /// </summary>
  /// <param name="entry">The entry to extract.</param>
  /// <returns>The raw file data.</returns>
  public byte[] Extract(HogEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);

    if (entry.Size == 0)
      return [];

    this._stream.Position = entry.DataOffset;
    var data = new byte[entry.Size];
    ReadExact(data);
    return data;
  }

  private List<HogEntry> ReadEntries() {
    var entries = new List<HogEntry>();

    // Each record: 13-byte null-padded filename + 4-byte LE uint32 size + inline data
    Span<byte> header = stackalloc byte[17]; // 13 + 4

    while (true) {
      // Try to read the 17-byte entry header; stop at EOF
      var totalRead = 0;
      while (totalRead < header.Length) {
        var read = this._stream.Read(header[totalRead..]);
        if (read == 0) {
          if (totalRead == 0)
            return entries; // clean EOF between entries
          throw new EndOfStreamException("Unexpected end of HOG stream in entry header.");
        }
        totalRead += read;
      }

      var name = ParseName(header[..13]);
      var size = (int)BitConverter.ToUInt32(header[13..17]);
      var dataOffset = this._stream.Position;

      entries.Add(new HogEntry { Name = name, Size = size, DataOffset = dataOffset });

      // Skip past the inline data to the next entry header
      this._stream.Position = dataOffset + size;
    }
  }

  private static string ParseName(ReadOnlySpan<byte> nameBytes) {
    var length = nameBytes.IndexOf((byte)0);
    if (length < 0)
      length = nameBytes.Length;
    return Encoding.ASCII.GetString(nameBytes[..length]);
  }

  private void ReadExact(Span<byte> buffer) {
    var totalRead = 0;
    while (totalRead < buffer.Length) {
      var read = this._stream.Read(buffer[totalRead..]);
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of HOG stream.");
      totalRead += read;
    }
  }

  /// <inheritdoc />
  public void Dispose() {
    if (!this._disposed) {
      this._disposed = true;
      if (!this._leaveOpen)
        this._stream.Dispose();
    }
  }
}
