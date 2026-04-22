#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Mbox;

/// <summary>
/// Descriptor for the Unix mbox mailbox format.  Each RFC 822 message in the
/// mailbox is surfaced as a separate <c>.eml</c> entry; the message body is
/// preserved verbatim (including any "&gt;From " byte-stuffed lines).
/// </summary>
public sealed class MboxFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {
  public string Id => "Mbox";
  public string DisplayName => "mbox (Unix mailbox)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".mbox";
  public IReadOnlyList<string> Extensions => [".mbox", ".mbx"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // "From " at offset 0 is a weak marker — plain text files can legitimately
  // start that way — so keep confidence low and rely on extension as the firm hit.
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new("From "u8.ToArray(), Confidence: 0.70)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Unix mbox mailbox: stream of RFC 822 messages separated by \"From \" lines.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var messages = Load(stream);
    var result = new List<ArchiveEntryInfo>(messages.Count);
    for (var i = 0; i < messages.Count; i++) {
      var m = messages[i];
      var name = EntryName(m, i);
      DateTime? lastMod = m.Date != null && DateTime.TryParse(m.Date, null,
        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
        out var dt) ? dt : null;
      result.Add(new ArchiveEntryInfo(i, name, m.EmlBytes.Length, m.EmlBytes.Length,
        "stored", false, false, lastMod, Kind: "Track"));
    }
    return result;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var messages = Load(stream);
    for (var i = 0; i < messages.Count; i++) {
      var m = messages[i];
      var name = EntryName(m, i);
      if (files != null && files.Length > 0 && !MatchesFilter(name, files)) continue;
      WriteFile(outputDir, name, m.EmlBytes);
    }
  }

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) {
    var messages = Load(input);
    for (var i = 0; i < messages.Count; i++) {
      var name = EntryName(messages[i], i);
      if (name.Equals(entryName, StringComparison.OrdinalIgnoreCase)) {
        output.Write(messages[i].EmlBytes);
        return;
      }
    }
    throw new FileNotFoundException($"Entry not found: {entryName}");
  }

  // ── Helpers ──────────────────────────────────────────────────────────────

  private static IReadOnlyList<MboxMessage> Load(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return MboxReader.ReadAll(ms.GetBuffer().AsSpan(0, (int)ms.Length));
  }

  private static string EntryName(MboxMessage m, int index) {
    var slug = SubjectSlug(m.Subject);
    return string.IsNullOrEmpty(slug)
      ? $"message_{index:D2}.eml"
      : $"message_{index:D2}_{slug}.eml";
  }

  private static string SubjectSlug(string? subject) {
    if (string.IsNullOrWhiteSpace(subject)) return string.Empty;
    var sb = new StringBuilder(subject.Length);
    foreach (var c in subject) {
      if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9')
        sb.Append(c);
      else if (c is ' ' or '-' or '_' or '.')
        sb.Append('_');
      if (sb.Length >= 40) break;
    }
    return sb.ToString().Trim('_');
  }
}
