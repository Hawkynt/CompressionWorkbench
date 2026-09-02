using System.Buffers.Binary;
using System.Globalization;

namespace FileFormat.Mix;

/// <summary>
/// Reads entries from a Westwood TD/RA1 MIX archive (no encryption, no checksum).
/// </summary>
/// <remarks>
/// TD/RA1 MIX has no magic bytes — the file begins directly with a UInt16 file count.
/// Detection relies on the <c>.mix</c> extension; the constructor performs sanity checks
/// (plausible file count, body size matches reachable file size) and throws
/// <see cref="InvalidDataException"/> when the input is implausible as a MIX file.
/// </remarks>
public sealed class MixReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly long _bodyStart;
  private bool _disposed;

  /// <summary>Gets the total body size declared in the header (sum of all file payload sizes).</summary>
  public long BodySize { get; }

  /// <summary>Gets all entries in the MIX archive, in directory order (sorted ascending by Westwood ID).</summary>
  public IReadOnlyList<MixEntry> Entries { get; }

  /// <summary>
  /// Initializes a new <see cref="MixReader"/> from a stream.
  /// </summary>
  /// <param name="stream">The stream containing the TD/RA1 MIX archive.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  public MixReader(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;

    if (!stream.CanRead || !stream.CanSeek)
      throw new ArgumentException("Stream must be readable and seekable.", nameof(stream));

    if (stream.Length < MixConstants.HeaderSize)
      throw new InvalidDataException("Stream is too small to be a valid TD/RA1 MIX archive.");

    Span<byte> header = stackalloc byte[MixConstants.HeaderSize];
    ReadExact(header);

    var fileCount = BinaryPrimitives.ReadUInt16LittleEndian(header[..2]);
    var bodySize = BinaryPrimitives.ReadUInt32LittleEndian(header[2..6]);

    var directorySize = (long)fileCount * MixConstants.DirectoryEntrySize;
    var minRequired = MixConstants.HeaderSize + directorySize + (long)bodySize;
    if (minRequired > stream.Length)
      throw new InvalidDataException(
        $"Declared MIX content ({minRequired} bytes: header + {fileCount} dir entries + {bodySize} body) exceeds stream length ({stream.Length}). Likely not a TD/RA1 MIX file.");

    this.BodySize = bodySize;
    this.Entries = ReadDirectory(fileCount);
    this._bodyStart = MixConstants.HeaderSize + directorySize;

    foreach (var e in this.Entries) {
      if (e.Offset < 0 || e.Size < 0 || e.Offset + e.Size > bodySize)
        throw new InvalidDataException(
          $"MIX entry 0x{e.Id:X8} (offset={e.Offset}, size={e.Size}) extends past body size {bodySize}.");
    }
  }

  /// <summary>
  /// Extracts the raw bytes for a given entry.
  /// </summary>
  /// <param name="entry">The entry to extract.</param>
  /// <returns>The raw entry payload.</returns>
  public byte[] Extract(MixEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);

    if (entry.Size == 0)
      return [];

    if (entry.Size > int.MaxValue)
      throw new InvalidDataException($"MIX entry 0x{entry.Id:X8} size {entry.Size} exceeds Int32.MaxValue.");

    this._stream.Position = this._bodyStart + entry.Offset;
    var data = new byte[entry.Size];
    ReadExact(data);
    return data;
  }

  private List<MixEntry> ReadDirectory(int count) {
    var entries = new List<MixEntry>(count);
    Span<byte> buf = stackalloc byte[MixConstants.DirectoryEntrySize];

    for (var i = 0; i < count; ++i) {
      ReadExact(buf);
      var id = BinaryPrimitives.ReadUInt32LittleEndian(buf[0..4]);
      var offset = BinaryPrimitives.ReadUInt32LittleEndian(buf[4..8]);
      var size = BinaryPrimitives.ReadUInt32LittleEndian(buf[8..12]);

      entries.Add(new MixEntry {
        Id = id,
        Name = id.ToString("X8", CultureInfo.InvariantCulture) + ".bin",
        Offset = offset,
        Size = size,
      });
    }

    return entries;
  }

  private void ReadExact(Span<byte> buffer) {
    var totalRead = 0;
    while (totalRead < buffer.Length) {
      var read = this._stream.Read(buffer[totalRead..]);
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of MIX stream.");
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
