#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.IaArc;

/// <summary>
/// Internet Archive ARC — the crawl container that preceded WARC (ISO 28500). Each entry is one
/// captured URL; its bytes are the record body exactly as archived (HTTP response headers included).
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://archive.org/web/researcher/ArcFileFormat.php</c> — the Internet Archive's ARC 1.0 / 2.0 specification</description></item>
///   <item><description><c>https://github.com/webrecorder/warcio</c> — warcio, the reader used as oracle</description></item>
/// </list>
/// </summary>
public sealed class IaArcFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable {

  public string Id => "IaArc";
  public string DisplayName => "Internet Archive ARC";
  public FormatCategory Category => FormatCategory.Archive;
  // R/W: removal copies the surviving records byte for byte; additions append version-1 records
  // (a version-2 file refuses additions — its result-code/checksum/location fields cannot be invented).
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".arc";
  // ".arc" belongs to SEA ARC; crawl files are recognised by their filedesc:// record instead.
  public IReadOnlyList<string> Extensions => [];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("filedesc://"u8.ToArray(), Confidence: 0.9)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("store", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Internet Archive ARC crawl file (versions 1 and 2), the predecessor of WARC";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new IaArcReader(stream);
    var names = EntryNames(r.Records);
    return r.Records.Select((rec, i) => new ArchiveEntryInfo(i, names[i], rec.BodyLength, rec.BodyLength,
      rec.ContentType, false, false, ParseDate(rec.ArchiveDate))).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new IaArcReader(stream);
    var names = EntryNames(r.Records);
    for (var i = 0; i < r.Records.Count; ++i) {
      if (files != null && !MatchesFilter(names[i], files)) continue;
      WriteFile(outputDir, names[i], r.ReadBody(r.Records[i]));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(inputs);
    var created = inputs.Select(StampOf).DefaultIfEmpty(DateTime.UtcNow).Max();
    IaArcWriter.Write(output, options.GetString("filename") ?? "archive.arc", created,
      inputs.Where(i => !i.IsDirectory).Select(i => new IaArcWriter.Record(i.ArchiveName, i.ReadContent(), StampOf(i))));
  }

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    var additions = inputs.Where(i => !i.IsDirectory).ToList();
    if (additions.Count == 0)
      return;
    var r = new IaArcReader(archive);
    if (r.Version != 1)
      throw new NotSupportedException("ARC: records can only be added to version-1 files.");
    var replaced = new HashSet<string>(additions.Select(a => a.ArchiveName), StringComparer.Ordinal);
    var names = EntryNames(r.Records);
    if (r.Records.Where((_, i) => replaced.Contains(names[i]) || replaced.Contains(r.Records[i].Url)).Any())
      this.Remove(archive, [.. replaced]);

    archive.Seek(0, SeekOrigin.End);
    if (archive.Length > 0) {
      archive.Position = archive.Length - 1;
      var last = archive.ReadByte();
      if (last != '\n')
        archive.WriteByte((byte)'\n');
    }
    foreach (var input in additions)
      IaArcWriter.WriteRecord(archive, new IaArcWriter.Record(input.ArchiveName, input.ReadContent(), StampOf(input)));
    archive.Flush();
  }

  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    var drop = new HashSet<string>(entryNames, StringComparer.Ordinal);
    var r = new IaArcReader(archive);
    var names = EntryNames(r.Records);
    var keep = new List<(long Start, long End)>();
    var firstRecord = r.Records.Count > 0 ? r.Records[0].RecordOffset : archive.Length;
    keep.Add((0, firstRecord));
    for (var i = 0; i < r.Records.Count; ++i) {
      if (drop.Contains(names[i]) || drop.Contains(r.Records[i].Url)) continue;
      var end = i + 1 < r.Records.Count ? r.Records[i + 1].RecordOffset : archive.Length;
      keep.Add((r.Records[i].RecordOffset, end));
    }
    if (keep.Count == r.Records.Count + 1)
      return;

    using var rebuilt = new MemoryStream();
    var buffer = new byte[81920];
    foreach (var (start, end) in keep) {
      archive.Position = start;
      var remaining = end - start;
      while (remaining > 0) {
        var n = archive.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
        if (n <= 0) throw new EndOfStreamException();
        rebuilt.Write(buffer, 0, n);
        remaining -= n;
      }
    }
    archive.Position = 0;
    archive.SetLength(0);
    rebuilt.Position = 0;
    rebuilt.CopyTo(archive);
    archive.Flush();
  }

  /// <summary>
  /// File-system-safe, unique entry names derived from each record's URL: scheme, query and fragment
  /// dropped, unsafe characters replaced; duplicates (the same URL captured twice) get "~2", "~3", ….
  /// </summary>
  internal static string[] EntryNames(IReadOnlyList<IaArcRecord> records) {
    var result = new string[records.Count];
    var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < records.Count; ++i) {
      var name = Sanitize(records[i].Url);
      if (name.Length == 0)
        name = $"record-{i:D4}";
      if (seen.TryGetValue(name, out var count)) {
        seen[name] = ++count;
        name = $"{name}~{count}";
      } else
        seen[name] = 1;
      result[i] = name;
    }
    return result;
  }

  private static string Sanitize(string url) {
    var s = url;
    var scheme = s.IndexOf("://", StringComparison.Ordinal);
    if (scheme >= 0) s = s[(scheme + 3)..];
    var cut = s.IndexOfAny(['?', '#']);
    if (cut >= 0) s = s[..cut];
    var sb = new StringBuilder(s.Length);
    foreach (var c in s)
      sb.Append(c is '/' or '.' or '-' or '_' || char.IsAsciiLetterOrDigit(c) ? c : '_');
    var segments = sb.ToString().Split('/', StringSplitOptions.RemoveEmptyEntries)
      .Select(seg => seg.Trim('.'))
      .Where(seg => seg.Length > 0);
    return string.Join('/', segments);
  }

  private static DateTime? ParseDate(string s)
    => DateTime.TryParseExact(s, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
      DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;

  private static DateTime StampOf(ArchiveInputInfo i)
    => i.Metadata?.LastWriteTimeUtc?.UtcDateTime
       ?? (i.InMemoryContent == null && File.Exists(i.FullPath) ? File.GetLastWriteTimeUtc(i.FullPath) : DateTime.UtcNow);
}
