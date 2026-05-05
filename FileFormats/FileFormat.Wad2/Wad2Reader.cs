using System.Text;

namespace FileFormat.Wad2;

/// <summary>
/// Reads entries from a Quake/Half-Life WAD2 or WAD3 texture archive.
/// </summary>
public sealed class Wad2Reader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private bool _disposed;

  /// <summary>Gets whether the archive uses WAD3 magic (Half-Life).</summary>
  public bool IsWad3 { get; }

  /// <summary>Gets all entries in the WAD archive.</summary>
  public IReadOnlyList<Wad2Entry> Entries { get; }

  /// <summary>
  /// Initializes a new <see cref="Wad2Reader"/> from a stream.
  /// </summary>
  /// <param name="stream">The stream containing the WAD2/WAD3 archive.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  public Wad2Reader(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;

    // Validate minimum header size
    if (stream.Length < Wad2Constants.HeaderSize)
      throw new InvalidDataException("Stream is too small to be a valid WAD2/WAD3 archive.");

    // Read 12-byte header: magic(4) + numEntries(4) + dirOffset(4)
    Span<byte> header = stackalloc byte[Wad2Constants.HeaderSize];
    ReadExact(header);

    var magic = Encoding.ASCII.GetString(header[..4]);
    this.IsWad3 = magic == Wad2Constants.MagicWad3String;

    if (magic != Wad2Constants.MagicWad2String && magic != Wad2Constants.MagicWad3String)
      throw new InvalidDataException($"Invalid WAD2/WAD3 magic: {magic}");

    var numEntries = (int)BitConverter.ToUInt32(header[4..8]);
    var dirOffset = (int)BitConverter.ToUInt32(header[8..12]);

    if (numEntries < 0)
      throw new InvalidDataException($"Invalid entry count: {numEntries}");

    this._stream.Position = dirOffset;
    this.Entries = ReadDirectory(numEntries);
  }

  /// <summary>
  /// Extracts the raw data for a given entry.
  /// </summary>
  /// <param name="entry">The entry to extract.</param>
  /// <returns>The raw entry data (diskSize bytes).</returns>
  public byte[] Extract(Wad2Entry entry) {
    ArgumentNullException.ThrowIfNull(entry);

    if (entry.CompressedSize == 0)
      return [];

    this._stream.Position = entry.DataOffset;
    var data = new byte[entry.CompressedSize];
    ReadExact(data);
    return data;
  }

  private List<Wad2Entry> ReadDirectory(int count) {
    var entries = new List<Wad2Entry>(count);
    Span<byte> buf = stackalloc byte[Wad2Constants.DirectoryEntrySize];

    for (var i = 0; i < count; ++i) {
      ReadExact(buf);

      var dataOffset  = (int)BitConverter.ToUInt32(buf[0..4]);
      var diskSize    = (int)BitConverter.ToUInt32(buf[4..8]);
      var size        = (int)BitConverter.ToUInt32(buf[8..12]);
      var type        = buf[12];
      var compression = buf[13];
      // buf[14..16] — 2 bytes padding, ignored
      var name = ParseName(buf[16..32]);

      entries.Add(new Wad2Entry {
        Name           = name,
        Size           = size,
        CompressedSize = diskSize,
        Type           = type,
        Compression    = compression,
        DataOffset     = dataOffset,
      });
    }

    return entries;
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
        throw new EndOfStreamException("Unexpected end of WAD2/WAD3 stream.");
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
