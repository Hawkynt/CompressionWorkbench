using System.Text;

namespace FileFormat.Wad;

/// <summary>
/// Reads entries from an id Software WAD archive (Doom/Heretic/Hexen).
/// </summary>
public sealed class WadReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private bool _disposed;

  /// <summary>Gets whether the WAD is an Internal WAD (IWAD).</summary>
  public bool IsIwad { get; }

  /// <summary>Gets whether the WAD is a Patch WAD (PWAD).</summary>
  public bool IsPwad { get; }

  /// <summary>Gets all lump entries in the WAD.</summary>
  public IReadOnlyList<WadEntry> Entries { get; }

  /// <summary>
  /// Initializes a new <see cref="WadReader"/> from a stream.
  /// </summary>
  /// <param name="stream">The stream containing the WAD archive.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  public WadReader(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;

    // Read 12-byte header
    Span<byte> header = stackalloc byte[WadConstants.HeaderSize];
    ReadExact(header);

    var magic = Encoding.ASCII.GetString(header[..4]);
    this.IsIwad = magic == WadConstants.MagicIwadString;
    this.IsPwad = magic == WadConstants.MagicPwadString;

    if (!this.IsIwad && !this.IsPwad)
      throw new InvalidDataException($"Invalid WAD magic: {magic}");

    var lumpCount = BitConverter.ToInt32(header[4..8]);
    var directoryOffset = BitConverter.ToInt32(header[8..12]);

    if (lumpCount < 0)
      throw new InvalidDataException($"Invalid lump count: {lumpCount}");

    // Seek to and read the directory
    this._stream.Position = directoryOffset;
    this.Entries = ReadDirectory(lumpCount);
  }

  /// <summary>
  /// Extracts the data for a given lump entry.
  /// </summary>
  /// <param name="entry">The lump entry to extract.</param>
  /// <returns>The raw lump data.</returns>
  public byte[] Extract(WadEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);

    if (entry.Size == 0)
      return [];

    this._stream.Position = entry.DataOffset;
    var data = new byte[entry.Size];
    ReadExact(data);
    return data;
  }

  private List<WadEntry> ReadDirectory(int count) {
    var entries = new List<WadEntry>(count);
    Span<byte> buf = stackalloc byte[WadConstants.DirectoryEntrySize];

    for (var i = 0; i < count; ++i) {
      ReadExact(buf);

      var dataOffset = BitConverter.ToInt32(buf[..4]);
      var dataSize = BitConverter.ToInt32(buf[4..8]);
      var name = ParseLumpName(buf[8..16]);

      entries.Add(new WadEntry {
        Name = name,
        Size = dataSize,
        DataOffset = dataOffset,
      });
    }

    return entries;
  }

  private static string ParseLumpName(ReadOnlySpan<byte> nameBytes) {
    // Find null terminator
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
        throw new EndOfStreamException("Unexpected end of WAD stream.");
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
