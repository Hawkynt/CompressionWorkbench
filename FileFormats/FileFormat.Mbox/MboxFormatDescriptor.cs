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
public sealed class MboxFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract, IArchiveCreatable, IArchiveModifiable {
  public string Id => "Mbox";
  public string DisplayName => "mbox (Unix mailbox)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
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
  public string Description =>
    "Unix mbox mailbox: flat stream of RFC 822 messages separated by \"From \" lines. " +
    "True in-place R/W: Add appends a new \"From \" separator + message at EOF (every " +
    "pre-existing byte byte-identical); Remove tombstones a record with an X-Status: D " +
    "marker + zero-wiped body, preserving record length so byte offsets of every other " +
    "message stay stable.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var messages = Load(stream);
    var result = new List<ArchiveEntryInfo>(messages.Count);
    for (var i = 0; i < messages.Count; i++) {
      var m = messages[i];
      // Tombstoned (deleted) messages carry the canonical "X-Status: D" + our
      // "X-Cwb-Tombstone: 1" markers — skip them so callers see the live set
      // after an in-place Remove. The bytes remain on disk; only the listing
      // surface omits them.
      if (IsTombstone(m)) continue;
      var name = EntryName(m, i);
      DateTime? lastMod = m.Date != null && DateTime.TryParse(m.Date, null,
        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
        out var dt) ? dt : null;
      result.Add(new ArchiveEntryInfo(i, name, m.EmlBytes.Length, m.EmlBytes.Length,
        "stored", false, false, lastMod, Kind: "Track"));
    }
    return result;
  }

  private static bool IsTombstone(MboxMessage m) {
    if (m.EmlBytes.Length < 32) return false;
    // Headers area runs until the first blank line; bound the scan at 1 KiB.
    var n = Math.Min(m.EmlBytes.Length, 1024);
    var headers = System.Text.Encoding.Latin1.GetString(m.EmlBytes, 0, n);
    return headers.Contains("X-Cwb-Tombstone: 1", StringComparison.Ordinal);
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var messages = Load(stream);
    for (var i = 0; i < messages.Count; i++) {
      var m = messages[i];
      if (IsTombstone(m)) continue;
      var name = EntryName(m, i);
      if (files != null && files.Length > 0 && !MatchesFilter(name, files)) continue;
      WriteFile(outputDir, name, m.EmlBytes);
    }
  }

  /// <summary>
  /// Opens a single mbox message as a bounded read-only stream. The
  /// reader splits the mailbox into RFC 822 messages; the matched
  /// message's bytes are wrapped in a
  /// <see cref="Compression.Registry.Streaming.BoundedEntryStream"/> sized
  /// to its logical length.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var messages = Load(archive);
    for (var i = 0; i < messages.Count; i++) {
      var name = EntryName(messages[i], i);
      if (!string.Equals(name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = messages[i].EmlBytes;
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
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

  /// <summary>
  /// Creates a fresh mbox mailbox at <paramref name="output"/> by appending each
  /// input file as a complete RFC 822 message. Each input is expected to be an
  /// <c>.eml</c> payload (headers + blank line + body); the writer wraps every
  /// message with a "From " envelope separator and byte-stuffs body lines that
  /// start with <c>From </c> per RFC 4155.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    using var w = new MboxWriter(output, leaveOpen: true);
    foreach (var (_, data) in FilesOnly(inputs))
      w.AddMessage(data);
  }

  /// <summary>
  /// Appends every input as a new mbox message in place. The mailbox bytes
  /// before <see cref="Stream.Length"/> stay byte-identical; only a new
  /// <c>From&#32;</c> separator + the input's bytes are written at EOF.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      MboxInPlaceModifier.Append(archive, input.ReadContent());
    }
  }

  /// <summary>
  /// Tombstones the named messages in place. The match is by entry name —
  /// see <see cref="EntryName"/> — so callers should pass the names returned
  /// by <see cref="List"/>. The byte offsets of every non-targeted message
  /// are unchanged after this call.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    if (entryNames.Length == 0) return;

    archive.Position = 0;
    var messages = Load(archive);
    var nameToIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < messages.Count; i++)
      nameToIndex[EntryName(messages[i], i)] = i;

    var hits = new List<int>();
    foreach (var entryName in entryNames) {
      if (nameToIndex.TryGetValue(entryName, out var idx)) hits.Add(idx);
    }
    hits.Sort();
    for (var i = hits.Count - 1; i >= 0; i--)
      MboxInPlaceModifier.TombstoneAt(archive, hits[i]);
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
