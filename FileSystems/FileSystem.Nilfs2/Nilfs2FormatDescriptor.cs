#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Nilfs2;

/// <summary>
/// Read-only descriptor for NILFS2 — the New Implementation of a
/// Log-structured File System (Linux mainline since 2.6.30). NILFS2 has
/// continuous-snapshot semantics: every commit produces a new checkpoint.
/// Magic 0x3434 sits at superblock+6 (file offset 1030). Full DAT-tree
/// + segment-log replay is out of scope here; this descriptor surfaces
/// the parsed superblock as structured metadata plus the raw image.
/// </summary>
public sealed class Nilfs2FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveDefragmentable {
  public string Id => "Nilfs2";
  public string DisplayName => "NILFS2";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".nilfs2";
  public IReadOnlyList<string> Extensions => [".nilfs2", ".nilfs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // NILFS_SUPER_MAGIC == 0x3434, little-endian at superblock+6 == file offset 1030.
    new([0x34, 0x34], Offset: 1030, Confidence: 0.85),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "NILFS2 continuous-snapshot log-structured filesystem — superblock surface only.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new Nilfs2Reader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new Nilfs2Reader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Defragment(Stream archive)
    => throw new NotSupportedException("Nilfs2 read-only — defragmentation requires a writer.");

  public void Defragment(Stream archive, DefragOptions options)
    => throw new NotSupportedException("Nilfs2 read-only — defragmentation requires a writer.");
}
