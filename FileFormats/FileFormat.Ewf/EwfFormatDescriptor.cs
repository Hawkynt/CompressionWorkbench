#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Ewf;

/// <summary>
/// Pseudo-archive descriptor for EnCase Expert Witness Format (EWF) forensic
/// images (.e01/.ewf/.l01). Surfaces each parsed section as a separate entry
/// along with a <c>metadata.ini</c> summarising acquisition parameters pulled
/// from the <c>header</c>/<c>header2</c>/<c>hash</c>/<c>digest</c> sections.
/// Full sector decompression + segment chaining across multi-file sets is
/// deferred to a later phase — forensic tooling (libewf, EnCase) can decode
/// the per-section data directly.
/// </summary>
public sealed class EwfFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Ewf";
  public string DisplayName => "EnCase EWF (E01)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".e01";
  public IReadOnlyList<string> Extensions => [".e01", ".ewf", ".l01"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x45, 0x56, 0x46, 0x09, 0x0D, 0x0A, 0xFF, 0x00], Offset: 0, Confidence: 0.95), // "EVF\t\r\n\xFF\x00"
    new([0x4C, 0x56, 0x46, 0x09, 0x0D, 0x0A, 0xFF, 0x00], Offset: 0, Confidence: 0.95), // "LVF\t\r\n\xFF\x00"
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "EnCase Expert Witness Format forensic image; surfaces section descriptors " +
    "(header, volume, sectors, table, hash, digest, done/next) as entries.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Data.LongLength, e.Data.LongLength, "stored", false, false, null
    )).ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  private static List<(string Name, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var img = EwfReader.Read(ms.GetBuffer().AsSpan(0, (int)ms.Length));

    var result = new List<(string, byte[])> {
      ("metadata.ini", BuildMetadata(img)),
    };

    // Section file names: `section_{index:D2}_{type}.bin`. The index keeps the
    // walk order stable even when multiple sections share a type (e.g. table/table2).
    for (var i = 0; i < img.Sections.Count; i++) {
      var s = img.Sections[i];
      var safeType = SafeNameSegment(s.Type);
      result.Add(($"section_{i:D2}_{safeType}.bin", s.Payload));
    }
    return result;
  }

  private static string SafeNameSegment(string raw) {
    var sb = new StringBuilder(raw.Length);
    foreach (var c in raw) {
      if (char.IsLetterOrDigit(c) || c == '_' || c == '-') sb.Append(c);
      else sb.Append('_');
    }
    return sb.Length == 0 ? "unknown" : sb.ToString();
  }

  private static byte[] BuildMetadata(EwfReader.EwfImage img) {
    var sb = new StringBuilder();
    sb.AppendLine("[ewf]");
    sb.Append("signature = ").AppendLine(img.IsLogical ? "LVF (logical)" : "EVF (physical)");
    sb.Append(CultureInfo.InvariantCulture, $"segment_number = {img.SegmentNumber}\n");
    sb.Append(CultureInfo.InvariantCulture, $"file_size = {img.TotalFileSize}\n");
    sb.Append(CultureInfo.InvariantCulture, $"section_count = {img.Sections.Count}\n");

    var headerSection = img.Sections.FirstOrDefault(s => s.Type is "header" or "header2");
    if (headerSection is not null) {
      var parsed = ParseAcquisitionHeader(headerSection.Payload);
      if (parsed.Count > 0) {
        sb.AppendLine();
        sb.AppendLine("[acquisition]");
        foreach (var kv in parsed)
          sb.Append(CultureInfo.InvariantCulture, $"{kv.Key} = {kv.Value}\n");
      }
    }

    var hashSection = img.Sections.FirstOrDefault(s => s.Type == "hash");
    if (hashSection is not null && hashSection.Payload.Length >= 16) {
      sb.AppendLine();
      sb.AppendLine("[hash]");
      sb.Append("md5 = ").AppendLine(Convert.ToHexString(hashSection.Payload.AsSpan(0, 16)));
    }

    var digestSection = img.Sections.FirstOrDefault(s => s.Type == "digest");
    if (digestSection is not null && digestSection.Payload.Length >= 36) {
      sb.AppendLine();
      sb.AppendLine("[digest]");
      sb.Append("md5 = ").AppendLine(Convert.ToHexString(digestSection.Payload.AsSpan(0, 16)));
      sb.Append("sha1 = ").AppendLine(Convert.ToHexString(digestSection.Payload.AsSpan(16, 20)));
    }

    sb.AppendLine();
    sb.AppendLine("[sections]");
    for (var i = 0; i < img.Sections.Count; i++) {
      var s = img.Sections[i];
      sb.Append(CultureInfo.InvariantCulture,
        $"section_{i:D2} = type={s.Type} offset={s.DescriptorOffset} size={s.SectionSize} next=0x{s.NextSectionOffset:X} checksum=0x{s.Checksum:X8}\n");
    }
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static Dictionary<string, string> ParseAcquisitionHeader(byte[] payload) {
    // header/header2 payloads are typically zlib-compressed UTF-8/UTF-16 text
    // organised as tab-separated rows (category, key..., value...). Surface
    // the raw printable text when we can't decompress — leaves forensic tools
    // something to work with without us re-implementing zlib here.
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    if (payload.Length == 0) return result;

    try {
      // header2 is UTF-16LE with BOM (0xFF 0xFE); header is ASCII/UTF-8.
      var text = payload.Length >= 2 && payload[0] == 0xFF && payload[1] == 0xFE
        ? Encoding.Unicode.GetString(payload, 2, payload.Length - 2)
        : Encoding.UTF8.GetString(payload);

      // Strip non-printable / control chars except tab and newline for safety.
      var sb = new StringBuilder(text.Length);
      foreach (var c in text) {
        if (c == '\t' || c == '\n' || c == '\r' || (c >= 0x20 && c < 0x7F) || c > 0xA0) sb.Append(c);
      }
      var printable = sb.ToString();

      var lines = printable.Split('\n', StringSplitOptions.RemoveEmptyEntries);
      if (lines.Length >= 2) {
        var keys = lines[1].Split('\t');
        if (lines.Length >= 3) {
          var values = lines[2].Split('\t');
          for (var i = 0; i < Math.Min(keys.Length, values.Length); i++) {
            var k = keys[i].Trim().Trim('\r');
            var v = values[i].Trim().Trim('\r');
            if (k.Length > 0) result[k] = v;
          }
        }
      }
    } catch {
      // Swallow — payload isn't a header2 text block (may be raw / compressed).
    }
    return result;
  }
}
