using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Slf;

/// <summary>
/// Reads entries from a Sir-Tech SLF archive (Jagged Alliance 2 resource library).
/// </summary>
/// <remarks>
/// SLF has no magic bytes. Detection is extension-only; the reader instead validates plausibility
/// of the entry table and rejects garbage with <see cref="InvalidDataException"/>.
/// </remarks>
public sealed class SlfReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private bool _disposed;

  /// <summary>Gets the friendly library name embedded in the SLF header (may be empty).</summary>
  public string LibName { get; }

  /// <summary>Gets the virtual path prefix that JA2 mounts entries under (e.g. <c>"BinaryData\\"</c>); may be empty.</summary>
  public string LibPath { get; }

  /// <summary>Gets the active (non-tombstoned) entries in the archive.</summary>
  public IReadOnlyList<SlfEntry> Entries { get; }

  /// <summary>
  /// Initializes a new <see cref="SlfReader"/> from a stream.
  /// </summary>
  /// <param name="stream">The stream containing the SLF archive.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  public SlfReader(Stream stream, bool leaveOpen = false) {
    this._stream    = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;

    if (stream.Length < SlfConstants.HeaderSize)
      throw new InvalidDataException("Stream is too small to contain an SLF header.");

    Span<byte> header = stackalloc byte[SlfConstants.HeaderSize];
    this._stream.Position = 0;
    ReadExact(header);

    this.LibName = ReadFixedAscii(header[..SlfConstants.NameFieldSize]);
    this.LibPath = ReadFixedAscii(header.Slice(SlfConstants.NameFieldSize, SlfConstants.NameFieldSize));

    var fieldsBase     = SlfConstants.NameFieldSize * 2;
    var numberOfEntries = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(fieldsBase, 4));
    var usedEntries     = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(fieldsBase + 4, 4));

    if (numberOfEntries < 0 || numberOfEntries > SlfConstants.MaxPlausibleEntries)
      throw new InvalidDataException($"Implausible SLF entry count: {numberOfEntries}.");

    if (usedEntries < 0 || usedEntries > numberOfEntries)
      throw new InvalidDataException($"Invalid SLF used-entry count: {usedEntries} of {numberOfEntries}.");

    var tableBytes = (long)numberOfEntries * SlfConstants.EntrySize;
    if (SlfConstants.HeaderSize + tableBytes > stream.Length)
      throw new InvalidDataException("SLF entry table extends past end of stream.");

    this.Entries = ReadEntries(numberOfEntries);
  }

  /// <summary>
  /// Extracts the raw payload for an entry.
  /// </summary>
  /// <param name="entry">The entry to extract.</param>
  /// <returns>The entry's bytes (length = <see cref="SlfEntry.Size"/>).</returns>
  public byte[] Extract(SlfEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);

    if (entry.Size == 0)
      return [];

    if (entry.Size > int.MaxValue)
      throw new InvalidDataException($"Entry {entry.Name} is too large for a single allocation: {entry.Size} bytes.");

    this._stream.Position = entry.Offset;
    var buffer = new byte[entry.Size];
    ReadExact(buffer);
    return buffer;
  }

  private List<SlfEntry> ReadEntries(int count) {
    var streamLength = this._stream.Length;
    var result = new List<SlfEntry>(count);
    Span<byte> buf = stackalloc byte[SlfConstants.EntrySize];

    this._stream.Position = SlfConstants.HeaderSize;
    for (var i = 0; i < count; ++i) {
      ReadExact(buf);

      var name   = ReadFixedAscii(buf[..SlfConstants.NameFieldSize]);
      var offset = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(SlfConstants.NameFieldSize, 4));
      var size   = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(SlfConstants.NameFieldSize + 4, 4));
      var state  = buf[SlfConstants.NameFieldSize + 8];
      // 3 bytes reserved at NameFieldSize + 9
      // FileTime is a 64-bit signed Windows FILETIME (100-ns ticks since 1601) — read as Int64 so DateTime.FromFileTimeUtc accepts it.
      var fileTime = BinaryPrimitives.ReadInt64LittleEndian(buf.Slice(SlfConstants.NameFieldSize + 12, 8));
      // 2 + 2 bytes reserved tail

      // 0xFF marks a JA2-deleted slot whose payload may be stale or zero — caller asked us to hide these.
      if (state == SlfConstants.StateDeleted)
        continue;

      if (offset > streamLength || size > streamLength || (long)offset + size > streamLength)
        throw new InvalidDataException($"SLF entry '{name}' has out-of-bounds offset/size ({offset}/{size}) for stream length {streamLength}.");

      DateTime lastModified;
      try {
        lastModified = fileTime > 0 ? DateTime.FromFileTimeUtc(fileTime) : DateTime.MinValue;
      } catch (ArgumentOutOfRangeException) {
        // Tolerant read: we don't fail extraction on a junk timestamp.
        lastModified = DateTime.MinValue;
      }

      result.Add(new SlfEntry {
        Name         = name,
        Offset       = offset,
        Size         = size,
        LastModified = lastModified,
      });
    }

    return result;
  }

  private static string ReadFixedAscii(ReadOnlySpan<byte> field) {
    var nullIndex = field.IndexOf((byte)0);
    if (nullIndex < 0) nullIndex = field.Length;
    return Encoding.ASCII.GetString(field[..nullIndex]);
  }

  private void ReadExact(Span<byte> buffer) {
    var totalRead = 0;
    while (totalRead < buffer.Length) {
      var read = this._stream.Read(buffer[totalRead..]);
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of SLF stream.");
      totalRead += read;
    }
  }

  /// <inheritdoc />
  public void Dispose() {
    if (this._disposed) return;
    this._disposed = true;
    if (!this._leaveOpen)
      this._stream.Dispose();
  }
}
