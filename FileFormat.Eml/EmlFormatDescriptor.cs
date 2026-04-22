#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Eml;

/// <summary>
/// Descriptor for single-message RFC 822 / MIME files.  Each message is exposed
/// as a set of archive entries:
/// <list type="bullet">
///   <item><description><c>FULL.eml</c> — the original file verbatim.</description></item>
///   <item><description><c>metadata.ini</c> — flattened headers (From/To/Subject/Date/Message-ID).</description></item>
///   <item><description><c>part_NN_*.ext</c> — each MIME part with its transfer-encoding decoded.</description></item>
///   <item><description><c>attachments/&lt;name&gt;</c> — parts marked as attachments.</description></item>
/// </list>
/// </summary>
public sealed class EmlFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {
  public string Id => "Eml";
  public string DisplayName => "EML (RFC 822 message)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".eml";
  public IReadOnlyList<string> Extensions => [".eml"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // There is no reliable magic for RFC 822: messages start with whatever header
  // the sender put first.  Detection is extension-only.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "RFC 822 / MIME email message with per-part + attachment extraction.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false, LastModified: null,
      Kind: e.Kind)).ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) {
    foreach (var e in BuildEntries(input)) {
      if (e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase)) {
        output.Write(e.Data);
        return;
      }
    }
    throw new FileNotFoundException($"Entry not found: {entryName}");
  }

  // ── Entry builder ────────────────────────────────────────────────────────

  private static IReadOnlyList<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();
    var root = EmlParser.Parse(blob);

    var entries = new List<(string Name, string Kind, byte[] Data)> {
      ("FULL.eml", "Track", blob),
      ("metadata.ini", "Tag", BuildMetadata(root)),
    };

    var partIndex = 0;
    WalkParts(root, entries, ref partIndex);
    return entries;
  }

  private static void WalkParts(EmlParser.Part part, List<(string, string, byte[])> entries, ref int index) {
    if (part.SubParts != null) {
      // Composite part — descend.
      foreach (var sub in part.SubParts)
        WalkParts(sub, entries, ref index);
      return;
    }

    // Leaf part with real content.
    var name = part.FileName;
    var mime = part.MimeType ?? "application/octet-stream";
    var ext = ChooseExtension(mime, name);

    string entryName;
    if (part.IsAttachment && !string.IsNullOrEmpty(name))
      entryName = "attachments/" + SanitizeFileName(name);
    else
      entryName = $"part_{index:D2}_{SanitizeMimeSlug(mime)}{ext}";

    entries.Add((entryName, part.IsAttachment ? "Payload" : "Track", part.DecodedBody));
    index++;
  }

  private static byte[] BuildMetadata(EmlParser.Part root) {
    var sb = new StringBuilder();
    sb.AppendLine("[message]");
    foreach (var key in new[] { "From", "To", "Cc", "Subject", "Date", "Message-ID" }) {
      var v = root.GetHeader(key);
      if (v != null) sb.Append(key).Append(" = ").AppendLine(v);
    }
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static string ChooseExtension(string mime, string? fileName) {
    if (!string.IsNullOrEmpty(fileName)) {
      var e = Path.GetExtension(fileName);
      if (!string.IsNullOrEmpty(e)) return e;
    }
    return mime switch {
      "text/plain" => ".txt",
      "text/html" => ".html",
      "application/pdf" => ".pdf",
      "image/jpeg" => ".jpg",
      "image/png" => ".png",
      "image/gif" => ".gif",
      "application/zip" => ".zip",
      "application/json" => ".json",
      _ => ".bin",
    };
  }

  private static string SanitizeMimeSlug(string mime) =>
    mime.Replace('/', '_').Replace('+', '_').Replace('.', '_');

  private static string SanitizeFileName(string name) {
    var sb = new StringBuilder(name.Length);
    foreach (var c in name) {
      if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-')
        sb.Append(c);
      else
        sb.Append('_');
    }
    return sb.ToString();
  }
}
