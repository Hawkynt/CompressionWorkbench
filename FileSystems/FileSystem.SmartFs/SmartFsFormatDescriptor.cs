#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.SmartFs;

/// <summary>
/// Read-only descriptor for SmartFS — the wear-levelled raw-flash
/// filesystem in Apache NuttX RTOS. Recognises the "SMRT" format
/// signature near the start of the format sector (NuttX
/// CONFIG_SMARTFS_FORMAT_SIG). Sector-chain traversal + directory
/// enumeration are out of scope; this descriptor surfaces the parsed
/// format sector as metadata plus the raw image.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/apache/nuttx/tree/master/fs/smartfs</c> — reference implementation (Apache NuttX)</description></item>
///   <item><description>Apache NuttX "SmartFS" documentation and SmartFS Design Document (NuttX project wiki)</description></item>
/// </list>
/// </summary>
public sealed class SmartFsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveDefragmentable {
  public string Id => "SmartFs";
  public string DisplayName => "SmartFS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".smartfs";
  public IReadOnlyList<string> Extensions => [".smartfs", ".smart"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // SMRT signature commonly appears at offset 10 (after 5-byte per-sector
    // header + 5-byte format sector prefix). We declare two offsets so the
    // FormatDetector recognises both common NuttX configurations.
    new("SMRT"u8.ToArray(), Offset: 10, Confidence: 0.85),
    new("SMRT"u8.ToArray(), Offset: 8,  Confidence: 0.80),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "SmartFS wear-levelled raw-flash filesystem (Apache NuttX) — format sector surface only. " +
    "WORM write deferred — full SmartFS emission requires the per-sector logical-physical mapping " +
    "table (CRC-protected 5-byte header per sector), wear-level sequence counters, directory " +
    "sector chains with variable-length name entries, and free-sector allocator; no Windows/WSL " +
    "validator exists outside the NuttX target, so an emitted image cannot be proved correct.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new SmartFsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new SmartFsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Defragment(Stream archive)
    => throw new NotSupportedException("SmartFs read-only — defragmentation requires a writer.");

  public void Defragment(Stream archive, DefragOptions options)
    => throw new NotSupportedException("SmartFs read-only — defragmentation requires a writer.");
}
