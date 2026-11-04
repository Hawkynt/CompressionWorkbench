#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.StuffItX;

/// <summary>
/// StuffIt X (.sitx) archive (Aladdin Systems / Smith Micro), read-only.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/MacPaw/XADMaster</c> — XADMaster (The Unarchiver), the reader this one is converted from</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/StuffIt</c> — Wikipedia — covers StuffIt X</description></item>
/// </list>
/// </summary>
public sealed class StuffItXFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "StuffItX";
  public string DisplayName => "StuffIt X";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".sitx";
  public IReadOnlyList<string> Extensions => [".sitx"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("StuffIt!"u8.ToArray(), Confidence: 0.90)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("sitx", "StuffIt X")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "StuffIt X archive (Aladdin/Smith Micro), read-only: files, folders and resource forks (as .rsrc) " +
    "from Brimstone, Cyanide, Iron and stored streams, each checked against its CRC-32. Darkhorse, " +
    "Deflate, Blend, method 7, preprocessed and encrypted streams are listed but not extracted.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new StuffItXReader(stream, leaveOpen: true);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FullPath, e.OriginalSize,
      e.CompressedSize, e.Method, e.IsDirectory, false, e.LastModified)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new StuffItXReader(stream, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.FullPath, files)) continue;
      WriteFile(outputDir, e.FullPath, r.Extract(e));
    }
  }
}
