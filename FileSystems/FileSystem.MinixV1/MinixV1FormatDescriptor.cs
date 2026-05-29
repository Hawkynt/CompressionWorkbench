#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.MinixV1;

/// <summary>
/// Read-only descriptor for the original Minix v1 filesystem (1987,
/// Tanenbaum). 1024-byte blocks, 16-bit zone numbers, 32-byte inodes
/// (7 direct + 1 indirect + 1 double-indirect), magic 0x137F (14-byte
/// names) or 0x138F (30-byte names — Coherent variant). Predecessor to
/// Linux's ext filesystem family.
/// </summary>
public sealed class MinixV1FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveDefragmentable {
  public string Id => "MinixV1";
  public string DisplayName => "Minix V1 FS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".minix1";
  public IReadOnlyList<string> Extensions => [".minix1"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // V1 magic at superblock+16 == file offset 1040
    new([0x7F, 0x13], Offset: 1040, Confidence: 0.85),  // 0x137F: 14-char names
    new([0x8F, 0x13], Offset: 1040, Confidence: 0.85),  // 0x138F: 30-char names
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Minix v1 filesystem image (1987) — read-only.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new MinixV1Reader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new MinixV1Reader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Defragment(Stream archive)
    => throw new NotSupportedException("MinixV1 read-only — defragmentation requires a writer.");

  public void Defragment(Stream archive, DefragOptions options)
    => throw new NotSupportedException("MinixV1 read-only — defragmentation requires a writer.");
}
