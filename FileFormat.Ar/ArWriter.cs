using System.Text;

namespace FileFormat.Ar;

/// <summary>
/// Writes a Unix ar archive to a stream.
/// </summary>
public sealed class ArWriter : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private bool _disposed;

  /// <summary>
  /// Initializes a new <see cref="ArWriter"/>.
  /// </summary>
  /// <param name="stream">The stream to write the archive to.</param>
  /// <param name="leaveOpen">Whether to leave the stream open when this writer is disposed.</param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is null.</exception>
  public ArWriter(Stream stream, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(stream);
    this._stream = stream;
    this._leaveOpen = leaveOpen;
  }

  /// <summary>
  /// Writes all <paramref name="entries"/> to the stream as a complete ar archive,
  /// using the GNU extended filename format for names longer than
  /// <see cref="ArConstants.MaxInlineNameLength"/> characters.
  /// </summary>
  /// <param name="entries">The entries to write.</param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="entries"/> is null.</exception>
  public void Write(IReadOnlyList<ArEntry> entries) {
    ArgumentNullException.ThrowIfNull(entries);

    // Write global magic.
    this._stream.Write(ArConstants.GlobalMagic);

    // Build GNU string table for names that exceed the inline limit.
    string gnuStringTable = BuildGnuStringTable(entries);
    bool needsStringTable = gnuStringTable.Length > 0;

    if (needsStringTable) {
      byte[] tableData = Encoding.ASCII.GetBytes(gnuStringTable);
      WriteEntryHeader(this._stream, ArConstants.GnuStringTableName, DateTimeOffset.UnixEpoch,
        0, 0, 0, tableData.Length);
      this._stream.Write(tableData);
      if (tableData.Length % 2 != 0)
        this._stream.WriteByte(ArConstants.PaddingByte);
    }

    // Compute per-entry name fields.
    // For GNU long names, track their offset in the string table.
    int tableOffset = 0;
    foreach (var entry in entries) {
      string nameField;
      if (entry.Name.Length > ArConstants.MaxInlineNameLength) {
        // GNU long name: "/offset"
        nameField = $"/{tableOffset}";
        // Advance by the length of the entry in the string table: name + "/\n"
        tableOffset += Encoding.ASCII.GetByteCount(entry.Name) + 2;
      } else {
        // Inline name: terminated by '/'
        nameField = entry.Name + "/";
      }

      WriteEntryHeader(this._stream, nameField, entry.ModifiedTime,
        entry.OwnerId, entry.GroupId, entry.FileMode, entry.Data.Length);
      this._stream.Write(entry.Data);
      if (entry.Data.Length % 2 != 0)
        this._stream.WriteByte(ArConstants.PaddingByte);
    }

    this._stream.Flush();
  }

  // ── Helpers ──────────────────────────────────────────────────────────────

  /// <summary>
  /// Builds the GNU string table content for entries whose names exceed
  /// <see cref="ArConstants.MaxInlineNameLength"/> characters.
  /// Each entry in the table is "name/\n".
  /// </summary>
  private static string BuildGnuStringTable(IReadOnlyList<ArEntry> entries) {
    var sb = new StringBuilder();
    foreach (var entry in entries) {
      if (entry.Name.Length > ArConstants.MaxInlineNameLength)
        sb.Append(entry.Name).Append("/\n");
    }
    return sb.ToString();
  }

  private static void WriteEntryHeader(
    Stream stream,
    string nameField,
    DateTimeOffset modifiedTime,
    int ownerId,
    int groupId,
    int fileMode,
    long dataSize) {
    Span<byte> header = stackalloc byte[ArConstants.EntryHeaderSize];
    header.Clear();

    WriteAsciiField(header,  0, 16, nameField);
    WriteAsciiField(header, 16, 12, modifiedTime.ToUnixTimeSeconds().ToString());
    WriteAsciiField(header, 28,  6, ownerId.ToString());
    WriteAsciiField(header, 34,  6, groupId.ToString());
    WriteAsciiField(header, 40,  8, Convert.ToString(fileMode, 8));
    WriteAsciiField(header, 48, 10, dataSize.ToString());

    // Entry magic: "`\n"
    header[58] = ArConstants.EntryMagic[0];
    header[59] = ArConstants.EntryMagic[1];

    stream.Write(header);
  }

  private static void WriteAsciiField(Span<byte> header, int offset, int length, string value) {
    // Fill with spaces first (right-padding).
    header.Slice(offset, length).Fill((byte)' ');

    // Write value bytes, truncated to field width.
    byte[] valueBytes = Encoding.ASCII.GetBytes(value);
    int copyLen = Math.Min(valueBytes.Length, length);
    valueBytes.AsSpan(0, copyLen).CopyTo(header.Slice(offset, copyLen));
  }

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
