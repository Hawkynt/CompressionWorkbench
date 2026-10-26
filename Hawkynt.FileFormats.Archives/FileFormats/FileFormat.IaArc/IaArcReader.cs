using System.Text;

namespace FileFormat.IaArc;

/// <summary>One record of an Internet Archive ARC file.</summary>
/// <param name="RecordOffset">Offset of the record's header line.</param>
/// <param name="HeaderLine">The record's header line, without its line terminator.</param>
/// <param name="Url">The URL (may contain spaces; the remaining fields are taken from the right).</param>
/// <param name="IpAddress">IP address field.</param>
/// <param name="ArchiveDate">Archive date, 14 digits (YYYYMMDDhhmmss).</param>
/// <param name="ContentType">Content type field.</param>
/// <param name="BodyOffset">Offset of the body (the captured response, HTTP headers included).</param>
/// <param name="BodyLength">Body length (the line's last field).</param>
public sealed record IaArcRecord(long RecordOffset, string HeaderLine, string Url, string IpAddress, string ArchiveDate,
  string ContentType, long BodyOffset, long BodyLength);

/// <summary>
/// Reads an uncompressed Internet Archive ARC crawl file (versions 1 and 2). An <c>.arc.gz</c> is a
/// sequence of gzip members, one per record; it decompresses to exactly this stream.
/// </summary>
/// <remarks>
/// Layout per <c>https://archive.org/web/researcher/ArcFileFormat.php</c>: a <c>filedesc://</c> record whose
/// body is the version block (<c>"1 0 originator\nURL IP-address Archive-date Content-type Archive-length\n"</c>
/// for version 1, the ten-field definition line for version 2), then URL records. Each record is one
/// LF-terminated header line whose last field is the body length, the body, and an LF separator.
/// Version 1 lines have 5 fields (URL IP date type length), version 2 lines 10
/// (URL IP date type result-code checksum location offset filename length). Readers seen in the wild
/// (warcio, Heritrix) tolerate a missing separator and extra blank lines; so does this one.
/// </remarks>
public sealed class IaArcReader {

  /// <summary>The magic every ARC file starts with.</summary>
  public static ReadOnlySpan<byte> Magic => "filedesc://"u8;

  private const int MaxHeaderLine = 64 * 1024;

  private readonly Stream _stream;

  public IaArcReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    this._stream = stream;
    stream.Position = 0;
    var length = stream.Length;

    var first = ReadLine(stream) ?? throw new InvalidDataException("Empty ARC file.");
    if (!first.StartsWith("filedesc://", StringComparison.Ordinal))
      throw new InvalidDataException("Not an ARC file (missing filedesc:// record).");
    this.FileDescription = ParseHeader(0, first, 5, stream.Position, length);
    var block = new byte[this.FileDescription.BodyLength];
    stream.ReadExactly(block);
    this.VersionBlock = block;
    var text = Encoding.Latin1.GetString(block);
    this.Version = text.Length > 0 && text[0] == '2' ? 2 : text.Length > 0 && text[0] == '1' ? 1
      : throw new InvalidDataException("ARC version block names neither version 1 nor 2.");
    var fields = this.Version == 1 ? 5 : 10;
    if (fields != 5)
      this.FileDescription = ParseHeader(0, first, fields, this.FileDescription.BodyOffset, length);

    var records = new List<IaArcRecord>();
    while (true) {
      string? line;
      long start;
      do {
        start = stream.Position;
        line = ReadLine(stream);
      } while (line is { Length: 0 });
      if (line == null)
        break;
      var record = ParseHeader(start, line, fields, stream.Position, length);
      records.Add(record);
      stream.Position = record.BodyOffset + record.BodyLength;
    }
    this.Records = records;
  }

  /// <summary>The <c>filedesc://</c> record.</summary>
  public IaArcRecord FileDescription { get; }

  /// <summary>The filedesc record's body: the version block plus any metadata that follows it.</summary>
  public byte[] VersionBlock { get; }

  /// <summary>ARC version, 1 or 2.</summary>
  public int Version { get; }

  /// <summary>The URL records, in file order.</summary>
  public IReadOnlyList<IaArcRecord> Records { get; }

  public byte[] ReadBody(IaArcRecord record) {
    ArgumentNullException.ThrowIfNull(record);
    var data = new byte[record.BodyLength];
    this._stream.Position = record.BodyOffset;
    this._stream.ReadExactly(data);
    return data;
  }

  private static IaArcRecord ParseHeader(long recordOffset, string line, int fieldCount, long bodyOffset, long fileLength) {
    // Split from the right: only the URL may contain spaces.
    var parts = new string[fieldCount];
    var end = line.Length;
    for (var i = fieldCount - 1; i >= 1; --i) {
      var space = end > 0 ? line.LastIndexOf(' ', end - 1) : -1;
      if (space <= 0)
        throw new InvalidDataException($"ARC record line has fewer than {fieldCount} fields: '{Truncate(line)}'.");
      parts[i] = line[(space + 1)..end];
      end = space;
    }
    parts[0] = line[..end];
    if (!long.TryParse(parts[^1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var bodyLength))
      throw new InvalidDataException($"ARC record length '{Truncate(parts[^1])}' is not a number.");
    if (bodyLength > fileLength - bodyOffset)
      throw new InvalidDataException($"ARC record '{Truncate(parts[0])}' overruns the file.");
    return new IaArcRecord(recordOffset, line, parts[0], parts[1], parts[2], parts[3], bodyOffset, bodyLength);
  }

  private static string Truncate(string s) => s.Length <= 80 ? s : s[..80] + "…";

  /// <summary>Reads one LF-terminated line (a trailing CR is dropped), or null at end of stream.</summary>
  private static string? ReadLine(Stream s) {
    var bytes = new List<byte>();
    while (true) {
      var b = s.ReadByte();
      if (b < 0)
        return bytes.Count == 0 ? null : Encoding.Latin1.GetString(bytes.ToArray());
      if (b == '\n')
        break;
      if (bytes.Count >= MaxHeaderLine)
        throw new InvalidDataException("ARC header line too long.");
      bytes.Add((byte)b);
    }
    if (bytes.Count > 0 && bytes[^1] == '\r')
      bytes.RemoveAt(bytes.Count - 1);
    return Encoding.Latin1.GetString(bytes.ToArray());
  }
}
