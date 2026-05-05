#pragma warning disable CS1591
using System.Text;
using Compression.Registry;

namespace FileFormat.Gettext;

/// <summary>
/// Exposes a gettext .mo binary catalog as an archive of per-message text files.
/// Entry zero with an empty msgid is the catalog metadata header.
/// </summary>
public sealed class MoFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Mo";
  public string DisplayName => "MO (gettext binary catalog)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
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
  public string Description => "Compiled gettext message catalog; each message extractable as text.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = Read(stream);
    return GettextEntryHelper.ToArchiveEntries(entries);
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) =>
    GettextEntryHelper.Extract(Read(stream), outputDir, files);

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
