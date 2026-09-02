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
    var gnuStringTable = BuildGnuStringTable(entries);
    var needsStringTable = gnuStringTable.Length > 0;

    if (needsStringTable) {
      var tableData = Encoding.ASCII.GetBytes(gnuStringTable);
      WriteEntryHeader(this._stream, ArConstants.GnuStringTableName, DateTimeOffset.UnixEpoch,
        0, 0, 0, tableData.Length);
      this._stream.Write(tableData);
      if (tableData.Length % 2 != 0)
        this._stream.WriteByte(ArConstants.PaddingByte);
    }

    // Compute per-entry name fields.
    // For GNU long names, track their offset in the string table.
    var tableOffset = 0;
    foreach (var entry in entries) {
      string nameField;
      var storedName = MemberName(entry.Name);
      if (storedName.Length > ArConstants.MaxInlineNameLength) {
        // GNU long name: "/offset"
        nameField = $"/{tableOffset}";
        // Advance by the length of the entry in the string table: name + "/\n"
        tableOffset += Encoding.ASCII.GetByteCount(storedName) + 2;
      } else {
        // Inline name: terminated by '/'
        nameField = storedName + "/";
      }

      WriteEntryHeader(this._stream, nameField, entry.ModifiedTime,
        entry.OwnerId, entry.GroupId, entry.FileMode, entry.Data.Length);
      this._stream.Write(entry.Data);
      if (entry.Data.Length % 2 != 0)
        this._stream.WriteByte(ArConstants.PaddingByte);
    }

    this._stream.Flush();
  }

  /// <summary>
  /// Describes a single streaming ar member: its metadata plus a pre-known
  /// payload size and an on-demand source stream. Used by
  /// <see cref="WriteStreaming"/> so multi-GB members never materialize in RAM.
  /// </summary>
  /// <param name="Name">The member name.</param>
  /// <param name="Size">The member's logical byte size.</param>
  /// <param name="OpenData">Factory that opens the member's payload stream.</param>
  /// <param name="ModifiedTime">The member's modification time.</param>
  /// <param name="OwnerId">The numeric owner (user) ID.</param>
  /// <param name="GroupId">The numeric group ID.</param>
  /// <param name="FileMode">The file permission mode (octal).</param>
  public readonly record struct StreamingMember(
    string Name,
    long Size,
    Func<Stream> OpenData,
    DateTimeOffset ModifiedTime,
    int OwnerId = 0,
    int GroupId = 0,
    int FileMode = 0x81A4);

  /// <summary>
  /// Writes all <paramref name="members"/> to the stream as a complete ar
  /// archive, streaming each member's payload from its
  /// <see cref="StreamingMember.OpenData"/> factory in bounded 64 KB chunks
  /// rather than buffering it into RAM. The ar header encodes each member's
  /// size before its payload, so the pre-known <see cref="StreamingMember.Size"/>
  /// drives the header; the GNU string table for overlong names is built from
  /// the names alone in a first pass.
  /// </summary>
  /// <remarks>
  /// Produces byte-identical output to <see cref="Write"/> for the same
  /// names/sizes/metadata/payloads. Peak memory is the 64 KB copy buffer
  /// regardless of member size.
  /// </remarks>
  /// <param name="members">The members to write.</param>
  public void WriteStreaming(IReadOnlyList<StreamingMember> members) {
    ArgumentNullException.ThrowIfNull(members);

    // Write global magic.
    this._stream.Write(ArConstants.GlobalMagic);

    // Build GNU string table for names that exceed the inline limit. Names
    // only — no payload is read in this pass.
    var sb = new StringBuilder();
    foreach (var m in members)
      if (m.Name.Length > ArConstants.MaxInlineNameLength)
        sb.Append(m.Name).Append("/\n");
    var gnuStringTable = sb.ToString();

    if (gnuStringTable.Length > 0) {
      var tableData = Encoding.ASCII.GetBytes(gnuStringTable);
      WriteEntryHeader(this._stream, ArConstants.GnuStringTableName, DateTimeOffset.UnixEpoch,
        0, 0, 0, tableData.Length);
      this._stream.Write(tableData);
      if (tableData.Length % 2 != 0)
        this._stream.WriteByte(ArConstants.PaddingByte);
    }

    var buffer = new byte[64 * 1024];
    var tableOffset = 0;
    foreach (var m in members) {
      string nameField;
      var storedName = MemberName(m.Name);
      if (storedName.Length > ArConstants.MaxInlineNameLength) {
        nameField = $"/{tableOffset}";
        tableOffset += Encoding.ASCII.GetByteCount(storedName) + 2;
      } else {
        nameField = storedName + "/";
      }

      WriteEntryHeader(this._stream, nameField, m.ModifiedTime,
        m.OwnerId, m.GroupId, m.FileMode, m.Size);

      if (m.Size > 0) {
        using var src = m.OpenData();
        var remaining = m.Size;
        while (remaining > 0) {
          var toRead = (int)Math.Min(buffer.Length, remaining);
          var read = src.Read(buffer, 0, toRead);
          if (read <= 0)
            throw new EndOfStreamException(
              $"AR streaming member '{m.Name}': source ended {remaining} bytes short of the declared size {m.Size}.");
          this._stream.Write(buffer, 0, read);
          remaining -= read;
        }
      }
      if (m.Size % 2 != 0)
        this._stream.WriteByte(ArConstants.PaddingByte);
    }

    this._stream.Flush();
  }

  // ── Helpers ──────────────────────────────────────────────────────────────

  /// <summary>
  /// The name an ar member can actually carry: the leaf, with any directories
  /// dropped.
  /// </summary>
  /// <remarks>
  /// A member name is terminated by a slash, so a name that contains one cannot
  /// be told from a name that ends there — writing <c>nested/D.TXT</c> inline
  /// produces a member every reader calls <c>nested</c>, holding the wrong
  /// file's bytes under a name nobody asked for. The long-name table terminates
  /// its entries the same way and is no better. GNU ar keeps the leaf, and so
  /// does this.
  /// </remarks>
  /// <param name="name">The name as given.</param>
  /// <returns>The name to store.</returns>
  private static string MemberName(string name) {
    var cut = name.LastIndexOfAny(['/', '\\']);
    return cut < 0 ? name : name[(cut + 1)..];
  }

  /// <summary>
  /// Builds the GNU string table content for entries whose names exceed
  /// <see cref="ArConstants.MaxInlineNameLength"/> characters.
  /// Each entry in the table is "name/\n".
  /// </summary>
  private static string BuildGnuStringTable(IReadOnlyList<ArEntry> entries) {
    var sb = new StringBuilder();
    foreach (var entry in entries) {
      var storedName = MemberName(entry.Name);
      if (storedName.Length > ArConstants.MaxInlineNameLength)
        sb.Append(storedName).Append("/\n");
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
    var valueBytes = Encoding.ASCII.GetBytes(value);
    var copyLen = Math.Min(valueBytes.Length, length);
    valueBytes.AsSpan(0, copyLen).CopyTo(header.Slice(offset, copyLen));
  }

  // ── IDisposable ──────────────────────────────────────────────────────────

  /// <inheritdoc />
  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
  public void Dispose() {
    if (!this._disposed) {
      this._disposed = true;
      if (!this._leaveOpen)
        this._stream.Dispose();
    }
  }
}
