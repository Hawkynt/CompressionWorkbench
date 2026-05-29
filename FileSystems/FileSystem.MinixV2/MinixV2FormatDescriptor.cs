#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.MinixV2;

/// <summary>
/// Read-only descriptor for Minix v2 filesystem (1991). v2 extended
/// the original layout with 64-byte inodes, 32-bit zone numbers, and
/// triple-indirect blocks for large-file support. Magic 0x2468
/// (14-byte names) or 0x2478 (30-byte names — extended variant).
/// </summary>
public sealed class MinixV2FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveDefragmentable {
  public string Id => "MinixV2";
  public string DisplayName => "Minix V2 FS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".minix2";
  public IReadOnlyList<string> Extensions => [".minix2"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // V2 magic at superblock+16 == file offset 1040
    new([0x68, 0x24], Offset: 1040, Confidence: 0.85),  // 0x2468: 14-char names
    new([0x78, 0x24], Offset: 1040, Confidence: 0.85),  // 0x2478: 30-char names
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Minix v2 filesystem image (1991) — read-only.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new MinixV2Reader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new MinixV2Reader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Defragment(Stream archive)
    => throw new NotSupportedException("MinixV2 read-only — defragmentation requires a writer.");

  public void Defragment(Stream archive, DefragOptions options)
    => throw new NotSupportedException("MinixV2 read-only — defragmentation requires a writer.");
}
