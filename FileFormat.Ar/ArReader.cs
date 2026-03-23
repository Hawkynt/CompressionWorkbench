using System.Text;

namespace FileFormat.Ar;

/// <summary>
/// Reads entries from a Unix ar archive.
/// </summary>
public sealed class ArReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<ArEntry> _entries = [];
  private bool _disposed;

  /// <summary>Gets the entries present in the archive.</summary>
  public IReadOnlyList<ArEntry> Entries => this._entries;

  /// <summary>
  /// Initializes a new <see cref="ArReader"/> and parses the archive.
  /// </summary>
  /// <param name="stream">A stream containing the ar archive data.</param>
  /// <param name="leaveOpen">
  /// Whether to leave <paramref name="stream"/> open when this reader is disposed.
  /// </param>
  /// <exception cref="ArgumentNullException">
  /// Thrown when <paramref name="stream"/> is null.
  /// </exception>
  /// <exception cref="InvalidDataException">
  /// Thrown when the stream does not start with the ar global magic.
  /// </exception>
  public ArReader(Stream stream, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(stream);
    this._stream = stream;
    this._leaveOpen = leaveOpen;
    Parse();
  }

  // ── Parsing ──────────────────────────────────────────────────────────────

  private void Parse() {
    // Read and validate global header.
    Span<byte> magic = stackalloc byte[ArConstants.GlobalHeaderSize];
    this._stream.ReadExactly(magic);
    if (!magic.SequenceEqual(ArConstants.GlobalMagic))
      ThrowInvalidMagic();

    // GNU extended filename string table (loaded on first encounter).
    string? gnuStringTable = null;

    // Process entry headers until EOF.
    Span<byte> headerBuf = stackalloc byte[ArConstants.EntryHeaderSize];
    while (true) {
      var firstByte = this._stream.ReadByte();
      if (firstByte < 0)
        break; // clean EOF

      // We consumed one byte; read the remaining 59 bytes of the entry header.
      headerBuf[0] = (byte)firstByte;
      this._stream.ReadExactly(headerBuf[1..]);

      // Validate entry magic: last 2 bytes must be "`\n".
      if (headerBuf[58] != ArConstants.EntryMagic[0] || headerBuf[59] != ArConstants.EntryMagic[1])
        ThrowInvalidEntryMagic();

      // Parse ASCII fields.
      var rawName    = ReadField(headerBuf,  0, 16);
      var rawMtime   = ReadField(headerBuf, 16, 12);
      var rawUid     = ReadField(headerBuf, 28,  6);
      var rawGid     = ReadField(headerBuf, 34,  6);
      var rawMode    = ReadField(headerBuf, 40,  8);
      var rawSize    = ReadField(headerBuf, 48, 10);

      if (!long.TryParse(rawSize, out var dataSize) || dataSize < 0)
        ThrowInvalidEntryMagic();

      long.TryParse(rawMtime, out var unixTime);
      int.TryParse(rawUid, out var uid);
      int.TryParse(rawGid, out var gid);

      // File mode is stored as octal.
      var fileMode = 0;
      if (!string.IsNullOrEmpty(rawMode))
        fileMode = Convert.ToInt32(rawMode, 8);

      // Read entry data.
      var data = new byte[dataSize];
      this._stream.ReadExactly(data);

      // Skip padding byte when data size is odd.
      if (dataSize % 2 != 0)
        this._stream.ReadByte();

      // Handle GNU string table ("//") — store it but do not add as a user entry.
      if (rawName == ArConstants.GnuStringTableName) {
        gnuStringTable = Encoding.ASCII.GetString(data);
        continue;
      }

      // Resolve the filename.
      var name = ResolveEntryName(rawName, gnuStringTable);

      this._entries.Add(new ArEntry {
        Name         = name,
        ModifiedTime = DateTimeOffset.FromUnixTimeSeconds(unixTime),
        OwnerId      = uid,
        GroupId      = gid,
        FileMode     = fileMode,
        Data         = data,
      });
    }
  }

  // ── Name resolution ──────────────────────────────────────────────────────

  private static string ResolveEntryName(string rawName, string? gnuStringTable) {
    // GNU long filename: "/offset"
    if (rawName.Length > 1 && rawName[0] == ArConstants.GnuLongNamePrefix &&
        char.IsAsciiDigit(rawName[1])) {
      if (gnuStringTable == null)
        return rawName; // malformed but tolerate

      if (int.TryParse(rawName[1..], out var offset) && offset < gnuStringTable.Length) {
        // The name in the string table is terminated by "/\n".
        var end = gnuStringTable.IndexOf("/\n", offset, StringComparison.Ordinal);
        return end >= 0
          ? gnuStringTable[offset..end]
          : gnuStringTable[offset..].TrimEnd('\n', '/');
      }

      return rawName;
    }

    // Normal name: strip trailing '/' and spaces.
    return rawName.TrimEnd('/', ' ');
  }

  // ── Field helpers ────────────────────────────────────────────────────────

  private static string ReadField(ReadOnlySpan<byte> header, int offset, int length) =>
    Encoding.ASCII.GetString(header.Slice(offset, length)).TrimEnd(' ');

  // ── Error helpers ────────────────────────────────────────────────────────

  private static void ThrowInvalidMagic() =>
    throw new InvalidDataException("Stream does not contain a valid ar archive (bad global magic).");

  private static void ThrowInvalidEntryMagic() =>
    throw new InvalidDataException("Invalid ar entry header (bad entry magic or malformed size).");

  // ── IDisposable ──────────────────────────────────────────────────────────

  /// <inheritdoc />
  public void Dispose() {
    if (!this._disposed) {
      this._disposed = true;
      if (!this._leaveOpen)
        this._stream.Dispose();
    }
  }
}
