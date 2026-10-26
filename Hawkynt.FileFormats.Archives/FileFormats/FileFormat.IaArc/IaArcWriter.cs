using System.Globalization;
using System.Text;

namespace FileFormat.IaArc;

/// <summary>
/// Writes uncompressed version-1 Internet Archive ARC files and appends records to existing ones.
/// </summary>
public static class IaArcWriter {

  /// <summary>Field values for a record this writer emits.</summary>
  public sealed record Record(string Url, byte[] Body, DateTime ArchiveDateUtc,
    string ContentType = "application/octet-stream", string IpAddress = "0.0.0.0");

  /// <summary>Writes a new version-1 ARC: the filedesc record, then one URL record per entry.</summary>
  public static void Write(Stream output, string arcFileName, DateTime createdUtc, IEnumerable<Record> records) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(records);
    var versionBlock = Encoding.ASCII.GetBytes("1 0 CompressionWorkbench\nURL IP-address Archive-date Content-type Archive-length\n");
    WriteRecord(output, new Record("filedesc://" + arcFileName, versionBlock, createdUtc, "text/plain"));
    foreach (var r in records)
      WriteRecord(output, r);
  }

  /// <summary>Writes one record: header line, body, LF separator.</summary>
  public static void WriteRecord(Stream output, Record r) {
    if (string.IsNullOrEmpty(r.Url) || r.Url.Contains('\n') || r.Url.Contains('\r'))
      throw new ArgumentException($"Invalid ARC record URL '{r.Url}'.");
    if (r.IpAddress.Contains(' ') || r.ContentType.Contains(' '))
      throw new ArgumentException("ARC IP address and content type must not contain spaces.");
    var line = string.Create(CultureInfo.InvariantCulture,
      $"{r.Url} {r.IpAddress} {r.ArchiveDateUtc:yyyyMMddHHmmss} {r.ContentType} {r.Body.LongLength}\n");
    output.Write(Encoding.Latin1.GetBytes(line));
    output.Write(r.Body);
    output.WriteByte((byte)'\n');
  }
}
