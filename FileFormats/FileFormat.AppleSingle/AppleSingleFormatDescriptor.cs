#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.AppleSingle;

/// <summary>
/// Pseudo-archive descriptor for AppleSingle (RFC 1740) container files. Each
/// entry id (data fork, resource fork, Finder info, dates, real name, …) is
/// surfaced as a separate archive entry plus a metadata.ini summary.
/// </summary>
public sealed class AppleSingleFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "AppleSingle";
  public string DisplayName => "AppleSingle";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".as";
  public IReadOnlyList<string> Extensions => [".as", ".applesingle"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x00, 0x05, 0x16, 0x00], Confidence: 0.90),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "AppleSingle (RFC 1740) container — bundles data fork, resource fork, " +
    "Finder info, and Mac metadata for transport across non-HFS filesystems.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Data.LongLength, e.Data.LongLength, "stored",
      false, false, null)).ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  private static IEnumerable<(string Name, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var container = AppleSingleReader.Read(ms.GetBuffer().AsSpan(0, (int)ms.Length));

    yield return ("metadata.ini", BuildMetadata(container));
    foreach (var e in container.Entries)
      yield return (e.Name, e.Data);
  }

  private static byte[] BuildMetadata(AppleSingleReader.Container c) {
    var sb = new StringBuilder();
    sb.AppendLine("[applesingle]");
    sb.Append(CultureInfo.InvariantCulture, $"format = {(c.IsDouble ? "AppleDouble" : "AppleSingle")}\n");
    sb.Append(CultureInfo.InvariantCulture, $"version = 0x{c.Version:X8}\n");
    sb.Append(CultureInfo.InvariantCulture, $"entry_count = {c.Entries.Count}\n");
    foreach (var e in c.Entries)
      sb.Append(CultureInfo.InvariantCulture, $"entry_{e.EntryId:D2} = {AppleSingleReader.EntryDescription(e.EntryId)} ({e.Data.Length} bytes)\n");
    var realName = c.Entries.FirstOrDefault(e => e.EntryId == 3);
    if (realName != null)
      sb.Append(CultureInfo.InvariantCulture, $"real_name = {AppleSingleReader.DecodeRealName(realName.Data)}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
