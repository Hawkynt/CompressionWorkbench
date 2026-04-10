using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Big;

/// <summary>
/// Reads entries from an EA Games BIG archive (BIGF or BIG4 variant).
/// </summary>
public sealed class BigReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly bool _littleEndianDirectory; // BIG4 uses LE offsets/sizes
  private bool _disposed;

  /// <summary>Gets all entries in the archive.</summary>
  public IReadOnlyList<BigEntry> Entries { get; }

  /// <summary>Gets whether this archive uses the BIG4 (little-endian) variant.</summary>
  public bool IsBig4 { get; }

  /// <summary>
  /// Initializes a new <see cref="BigReader"/> from a stream.
  /// </summary>
  /// <param name="stream">The stream containing the BIG archive.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  /// <exception cref="InvalidDataException">Thrown when the magic bytes are not "BIGF" or "BIG4".</exception>
  public BigReader(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;

    if (stream.Length < 16)
      throw new InvalidDataException("Stream is too small to be a valid BIG archive.");

    // Read 16-byte header: 4-byte magic + 3× uint32
    Span<byte> header = stackalloc byte[16];
    ReadExact(header);

    var magic = Encoding.ASCII.GetString(header[..4]);
    if (magic == "BIG4") {
      this.IsBig4 = true;
      this._littleEndianDirectory = true;
    } else if (magic == "BIGF") {
      this.IsBig4 = false;
      this._littleEndianDirectory = false;
    } else {
      throw new InvalidDataException($"Invalid BIG magic: expected 'BIGF' or 'BIG4', got '{magic}'");
    }

    // Header fields are always big-endian
    var numFiles = (int)BinaryPrimitives.ReadUInt32BigEndian(header[8..12]);

    if (numFiles < 0)
      throw new InvalidDataException($"Invalid file count: {numFiles}");

    this.Entries = ReadDirectory(numFiles);
  }

  /// <summary>
  /// Extracts the raw data for a given entry.
  /// </summary>
  /// <param name="entry">The entry to extract.</param>
  /// <returns>The entry's raw bytes.</returns>
  public byte[] Extract(BigEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);

    if (entry.Size == 0)
      return [];

    this._stream.Position = entry.DataOffset;
    var data = new byte[entry.Size];
    ReadExact(data);
    return data;
  }

  private List<BigEntry> ReadDirectory(int count) {
    var entries = new List<BigEntry>(count);
    Span<byte> dword = stackalloc byte[4];

    for (var i = 0; i < count; i++) {
      // Each directory entry: uint32 offset + uint32 size + null-terminated path
      ReadExact(dword);
      var offset = this._littleEndianDirectory
        ? BinaryPrimitives.ReadUInt32LittleEndian(dword)
        : BinaryPrimitives.ReadUInt32BigEndian(dword);

      ReadExact(dword);
      var size = this._littleEndianDirectory
        ? BinaryPrimitives.ReadUInt32LittleEndian(dword)
        : BinaryPrimitives.ReadUInt32BigEndian(dword);

      var path = ReadNullTerminatedString();

      entries.Add(new BigEntry {
        Path = path,
        Size = (int)size,
        DataOffset = offset,
      });
    }

    return entries;
  }

  private string ReadNullTerminatedString() {
    var bytes = new List<byte>(64);
    Span<byte> b = stackalloc byte[1];
    while (true) {
      var read = this._stream.Read(b);
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of stream while reading path string.");
      if (b[0] == 0)
        break;
      bytes.Add(b[0]);
    }
    return Encoding.UTF8.GetString(bytes.ToArray());
  }

  private void ReadExact(Span<byte> buffer) {
    var totalRead = 0;
    while (totalRead < buffer.Length) {
      var read = this._stream.Read(buffer[totalRead..]);
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of BIG archive stream.");
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
