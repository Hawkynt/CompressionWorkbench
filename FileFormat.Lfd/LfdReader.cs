using System.Text;

namespace FileFormat.Lfd;

/// <summary>
/// Reads resources from a LucasArts X-Wing / TIE Fighter LFD bundle.
/// </summary>
/// <remarks>
/// The format has no global header. We walk type/name/size headers from offset 0 instead of
/// trusting the conventional first-entry RMAP — the RMAP is just an index for fast iteration,
/// not load-bearing. This keeps reads correct even on files with a missing or malformed RMAP.
/// </remarks>
public sealed class LfdReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private bool _disposed;

  /// <summary>Gets all resource entries discovered in the bundle (including the RMAP, if present).</summary>
  public IReadOnlyList<LfdEntry> Entries { get; }

  /// <summary>
  /// Initializes a new <see cref="LfdReader"/> from a stream.
  /// </summary>
  /// <param name="stream">The stream containing the LFD bundle.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  public LfdReader(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;

    var length = stream.Length;
    if (length < LfdConstants.HeaderSize)
      throw new InvalidDataException("Stream is too small to contain a single LFD resource header.");

    stream.Position = 0;
    this.Entries = ReadAllEntries(length);
  }

  /// <summary>
  /// Extracts the raw payload bytes for a given entry.
  /// </summary>
  public byte[] Extract(LfdEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);

    if (entry.Size == 0)
      return [];

    if (entry.Size > int.MaxValue)
      throw new InvalidDataException($"Entry '{entry.DisplayName}' is too large to extract: {entry.Size} bytes.");

    this._stream.Position = entry.Offset;
    var buffer = new byte[entry.Size];
    ReadExact(buffer);
    return buffer;
  }

  private List<LfdEntry> ReadAllEntries(long streamLength) {
    var entries = new List<LfdEntry>();
    Span<byte> header = stackalloc byte[LfdConstants.HeaderSize];

    while (this._stream.Position + LfdConstants.HeaderSize <= streamLength) {
      var headerOffset = this._stream.Position;
      ReadExact(header);

      var type = ParseField(header[..LfdConstants.TypeFieldSize]);
      var name = ParseField(header.Slice(LfdConstants.TypeFieldSize, LfdConstants.NameFieldSize));
      var size = BitConverter.ToUInt32(header[(LfdConstants.TypeFieldSize + LfdConstants.NameFieldSize)..]);

      var payloadOffset = headerOffset + LfdConstants.HeaderSize;
      if (payloadOffset + size > streamLength)
        throw new InvalidDataException(
          $"LFD entry '{type}.{name}' at offset {headerOffset} declares size {size} which exceeds remaining stream length.");

      entries.Add(new LfdEntry {
        Type        = type,
        Name        = name,
        DisplayName = type + "." + name,
        Offset      = payloadOffset,
        Size        = size,
      });

      this._stream.Position = payloadOffset + size;
    }

    if (entries.Count == 0)
      throw new InvalidDataException("LFD stream contained no parseable resource entries.");

    return entries;
  }

  private static string ParseField(ReadOnlySpan<byte> field) {
    var length = field.IndexOf((byte)0);
    if (length < 0)
      length = field.Length;
    return Encoding.ASCII.GetString(field[..length]);
  }

  private void ReadExact(Span<byte> buffer) {
    var totalRead = 0;
    while (totalRead < buffer.Length) {
      var read = this._stream.Read(buffer[totalRead..]);
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of LFD stream.");
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
