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
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://www.rfc-editor.org/rfc/rfc5322</c> — RFC 5322 — Internet Message Format (successor of RFC 822)</description></item>
///   <item><description><c>https://www.rfc-editor.org/rfc/rfc2045</c> — RFC 2045 — MIME part one: message body formats</description></item>
///   <item><description><c>https://www.rfc-editor.org/rfc/rfc2046</c> — RFC 2046 — MIME part two: media types incl. multipart boundaries</description></item>
/// </list>
/// </summary>
public sealed class EmlFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract, IArchiveCreatable, IArchiveModifiable {
  public string Id => "Eml";
  public string DisplayName => "EML (RFC 822 message)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify |
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
  public string Description =>
    "RFC 822 / MIME email message with per-part + attachment extraction. In-place R/W " +
    "appends/removes attachments inside a multipart body by splicing between boundary " +
    "delimiters — every surviving byte stays at its original offset.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false, LastModified: null,
      Kind: e.Kind)).ToList();

  /// <summary>
  /// Opens a single entry as a bounded read-only stream. Each entry's
  /// decoded byte buffer is produced by <see cref="BuildEntries"/> and
  /// wrapped in a
  /// <see cref="Compression.Registry.Streaming.BoundedEntryStream"/> sized
  /// to its logical length.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    foreach (var e in BuildEntries(archive)) {
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new MemoryStream(e.Data, writable: false), e.Data.Length, leaveOpen: false);
    }
    return new Compression.Registry.Streaming.BoundedEntryStream(
      new MemoryStream(System.Array.Empty<byte>(), writable: false), 0, leaveOpen: false);
  }

  /// <summary>Native in-memory single-entry extraction routed through the bounded <see cref="OpenEntry"/>.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }

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

  /// <summary>
  /// WORM creation: emits a MIME multipart/mixed message where each input is one
  /// base64-encoded attachment. With a single input the writer emits a
  /// single-part envelope instead. <c>From</c>/<c>To</c>/<c>Subject</c> can be
  /// overridden via <see cref="FormatCreateOptions.FormatSpecific"/> keys of the
  /// same name; the message envelope otherwise uses a deterministic minimal
  /// template so round-trips of the same input list are byte-identical.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var parts = new List<(string Name, byte[] Data, string? MimeType)>();
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      var leaf = Path.GetFileName(i.ArchiveName);
      // Skip reader-emitted synthetic entries so a list-then-create round-trip
      // doesn't smuggle reader state into the message.
      if (string.Equals(leaf, "metadata.ini", StringComparison.OrdinalIgnoreCase)) continue;
      if (string.Equals(leaf, "FULL.eml", StringComparison.OrdinalIgnoreCase)) continue;
      parts.Add((leaf, i.ReadContent(), GuessMimeType(leaf)));
    }

    Dictionary<string, string>? hdrs = null;
    if (options?.FormatSpecific != null) {
      foreach (var key in new[] { "From", "To", "Subject", "Date", "Message-ID" }) {
        var v = options.GetOption(key, "");
        if (!string.IsNullOrEmpty(v)) {
          hdrs ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
          hdrs[key] = v;
        }
      }
    }

    EmlWriter.Write(output, parts, hdrs);
  }

  private static string? GuessMimeType(string name) {
    var ext = Path.GetExtension(name).ToLowerInvariant();
    return ext switch {
      ".txt" => "text/plain; charset=utf-8",
      ".html" or ".htm" => "text/html; charset=utf-8",
      ".pdf" => "application/pdf",
      ".jpg" or ".jpeg" => "image/jpeg",
      ".png" => "image/png",
      ".gif" => "image/gif",
      ".zip" => "application/zip",
      ".json" => "application/json",
      _ => null,
    };
  }

  // ── IArchiveModifiable ───────────────────────────────────────────────────

  /// <summary>
  /// Appends each input as a fresh <c>Content-Disposition: attachment</c>
  /// MIME part immediately before the closing <c>--&lt;boundary&gt;--</c>
  /// delimiter. The message must already be a multipart body — single-part
  /// messages can't be promoted in place without rewriting the top-level
  /// Content-Type header. Routed through <see cref="EmlInPlaceModifier.AddAttachment"/>.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      var content = input.ReadContent();
      var fileName = Path.GetFileName(input.ArchiveName);
      EmlInPlaceModifier.AddAttachment(archive, fileName, content);
    }
  }

  /// <summary>
  /// Removes attachments by filename. Entry names are matched against the
  /// reader's <c>attachments/&lt;filename&gt;</c> exposure — passing either
  /// the prefixed form or the bare filename works. Routed through
  /// <see cref="EmlInPlaceModifier.RemoveAttachment"/>.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    foreach (var name in entryNames) {
      var bare = name;
      if (bare.StartsWith("attachments/", StringComparison.OrdinalIgnoreCase))
        bare = bare["attachments/".Length..];
      EmlInPlaceModifier.RemoveAttachment(archive, bare);
    }
  }

  // ── Entry builder ────────────────────────────────────────────────────────

  private static IReadOnlyList<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();
    var root = EmlParser.Parse(blob);

    var entries = new List<(string Name, string Kind, byte[] Data)> {
      ("FULL.eml", "Container", blob),
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
