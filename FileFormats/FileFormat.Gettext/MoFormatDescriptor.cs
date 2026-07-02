#pragma warning disable CS1591
using System.Text;
using Compression.Registry;

namespace FileFormat.Gettext;

/// <summary>
/// Exposes a gettext .mo binary catalog as an archive of per-message text files.
/// Entry zero with an empty msgid is the catalog metadata header.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://www.gnu.org/software/gettext/manual/html_node/MO-Files.html</c> — GNU gettext manual — binary MO file layout</description></item>
///   <item><description><c>https://www.gnu.org/software/gettext/</c> — GNU gettext project</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Gettext</c> — Wikipedia</description></item>
/// </list>
/// </summary>
public sealed class MoFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  public string Id => "Mo";
  public string DisplayName => "MO (gettext binary catalog)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".mo";
  public IReadOnlyList<string> Extensions => [".mo"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0xDE, 0x12, 0x04, 0x95], Confidence: 0.95),
    new([0x95, 0x04, 0x12, 0xDE], Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Compiled gettext message catalog; each message extractable as text. R-only: in-place R/W " +
    "is not honestly available because the 28-byte MO header records numStrings + " +
    "origTableOffset + transTableOffset, with both string descriptor tables packed BEFORE the " +
    "key/value pools. Adding or removing a message means extending (or collapsing) the descriptor " +
    "tables in place, which shifts every byte of the pools and invalidates the descriptor " +
    "(length, offset) pairs already written. That's a full rebuild, not an in-place mutation, so " +
    "promoting to CanModify would mis-advertise the surface.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = Read(stream);
    return GettextEntryHelper.ToArchiveEntries(entries);
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) =>
    GettextEntryHelper.Extract(Read(stream), outputDir, files);

  /// <summary>
  /// WORM creation: emits an MO catalog where each input becomes one entry. The
  /// archive name (sans path + trailing <c>.txt</c>) is used as the msgid; the
  /// input bytes (decoded as UTF-8) become the msgstr. An empty msgid signals
  /// the gettext metadata header and is placed first per the spec.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var entries = new List<CatalogEntry>(inputs.Count);
    var idx = 0;
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      var (context, msgid) = ParseInputName(input.ArchiveName);
      var msgstr = Encoding.UTF8.GetString(input.ReadContent());
      entries.Add(new CatalogEntry(
        Index: idx++,
        Context: context,
        MsgId: msgid,
        MsgIdPlural: null,
        MsgStr: msgstr,
        MsgStrPlural: null));
    }
    MoWriter.Write(output, entries);
  }

  /// <summary>
  /// Reverses the reader's <c>EntryName</c> sanitisation back into (context, msgid).
  /// Format: <c>NNNN_(ctx__)?LABEL.txt</c> where <c>HEADER</c> represents the empty
  /// msgid. Unparseable names fall back to the leaf as the literal msgid.
  /// </summary>
  public static (string? Context, string MsgId) ParseInputName(string archiveName) {
    var leaf = Path.GetFileName(archiveName);
    if (leaf.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
      leaf = leaf[..^4];

    // Strip optional NNNN_ index prefix.
    var underscore = leaf.IndexOf('_');
    if (underscore > 0 && underscore <= 6) {
      var prefix = leaf[..underscore];
      if (prefix.All(c => c is >= '0' and <= '9'))
        leaf = leaf[(underscore + 1)..];
    }

    // Context separator is double-underscore "__".
    string? context = null;
    var sep = leaf.IndexOf("__", StringComparison.Ordinal);
    if (sep > 0) {
      context = leaf[..sep];
      leaf = leaf[(sep + 2)..];
    }

    return (context, leaf == "HEADER" ? "" : leaf);
  }

  private static List<CatalogEntry> Read(Stream s) {
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return new MoReader().Read(ms.ToArray());
  }
}

internal static class GettextEntryHelper {
  public static List<ArchiveEntryInfo> ToArchiveEntries(List<CatalogEntry> entries) {
    var list = new List<ArchiveEntryInfo>(entries.Count);
    foreach (var e in entries) {
      var name = EntryName(e);
      var size = Encoding.UTF8.GetByteCount(e.MsgStr);
      list.Add(new ArchiveEntryInfo(
        Index: e.Index,
        Name: name,
        OriginalSize: size,
        CompressedSize: size,
        Method: "stored",
        IsDirectory: false,
        IsEncrypted: false,
        LastModified: null));
    }
    return list;
  }

  public static void Extract(List<CatalogEntry> entries, string outputDir, string[]? files) {
    foreach (var e in entries) {
      var name = EntryName(e);
      if (files != null && files.Length > 0 && !FormatHelpers.MatchesFilter(name, files))
        continue;
      FormatHelpers.WriteFile(outputDir, name, Encoding.UTF8.GetBytes(e.MsgStr));
    }
  }

  private static string EntryName(CatalogEntry e) {
    var label = string.IsNullOrEmpty(e.MsgId) ? "HEADER" : Sanitize(e.MsgId);
    var prefix = e.Context != null ? $"{Sanitize(e.Context)}__" : "";
    return $"{e.Index:D4}_{prefix}{label}.txt";
  }

  private static string Sanitize(string s) {
    var sb = new StringBuilder(Math.Min(s.Length, 60));
    foreach (var c in s) {
      if (sb.Length >= 60) break;
      if (char.IsLetterOrDigit(c) || c is '-' or '_') sb.Append(c);
      else if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
    }
    return sb.Length > 0 ? sb.ToString().TrimEnd('_') : "entry";
  }
}
