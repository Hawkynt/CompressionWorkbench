using System.Text;

namespace FileFormat.Gob;

/// <summary>
/// Reads entries from a Lucasarts GOB v2 archive (Jedi Knight, Outlaws).
/// </summary>
public sealed class GobReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private bool _disposed;

  /// <summary>Gets the GOB version field as written in the header (typically 0x14 or 0x20).</summary>
  public uint Version { get; }

  /// <summary>Gets all entries in the archive, in directory order.</summary>
  public IReadOnlyList<GobEntry> Entries { get; }

  /// <summary>
  /// Initializes a new <see cref="GobReader"/> from a stream.
  /// </summary>
  /// <param name="stream">The stream containing the GOB archive.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  public GobReader(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;

    if (stream.Length < GobConstants.HeaderSize)
      throw new InvalidDataException("Stream is too small to be a valid GOB archive.");

    Span<byte> header = stackalloc byte[GobConstants.HeaderSize];
    ReadExact(header);

    // Magic must be exactly "GOB " (with trailing space). Reject 0x00 padding here —
    // GOB v1 uses a different magic and we don't support it in this wave.
    if (!header[..4].SequenceEqual(GobConstants.Magic))
      throw new InvalidDataException("Invalid GOB magic — expected 'GOB ' (with trailing space).");

    this.Version = BitConverter.ToUInt32(header[4..8]);
    var dirOffset = BitConverter.ToUInt32(header[8..12]);

    if (dirOffset > stream.Length - 4)
      throw new InvalidDataException($"GOB directory offset {dirOffset} is out of bounds.");

    this._stream.Position = dirOffset;

    Span<byte> countBuf = stackalloc byte[4];
    ReadExact(countBuf);
    var count = BitConverter.ToUInt32(countBuf);

    if (count > int.MaxValue || (long)count * GobConstants.DirectoryEntrySize > stream.Length)
      throw new InvalidDataException($"GOB entry count {count} is implausibly large.");

    this.Entries = ReadDirectory((int)count);
  }

  /// <summary>
  /// Extracts the raw bytes for a given entry.
  /// </summary>
  public byte[] Extract(GobEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Size == 0)
      return [];

    this._stream.Position = entry.Offset;
    var data = new byte[entry.Size];
    ReadExact(data);
    return data;
  }

  private List<GobEntry> ReadDirectory(int count) {
    var entries = new List<GobEntry>(count);
    Span<byte> buf = stackalloc byte[GobConstants.DirectoryEntrySize];

    for (var i = 0; i < count; ++i) {
      ReadExact(buf);
      var offset = BitConverter.ToUInt32(buf[0..4]);
      var size = BitConverter.ToUInt32(buf[4..8]);
      var name = ParseName(buf[8..(8 + GobConstants.NameFieldSize)]);

      entries.Add(new GobEntry {
        Name = name,
        Offset = offset,
        Size = size,
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
        throw new EndOfStreamException("Unexpected end of GOB stream.");
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
