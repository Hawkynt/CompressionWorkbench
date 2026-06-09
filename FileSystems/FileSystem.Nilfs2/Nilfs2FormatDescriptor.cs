#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Nilfs2;

/// <summary>
/// NILFS2 descriptor (continuous-snapshot log-structured filesystem, Linux mainline
/// since 2.6.30). Magic 0x3434 sits at superblock+6 (file offset 1030).
///
/// <para><b>Honest scope.</b> NILFS2's full DAT-tree + IFile/CPFile/SUFile +
/// segment-log replay is multi-week work. The writer here ships single-checkpoint
/// WORM via a spec-compliant superblock plus a writer-private compact directory
/// at offset 2048 — sufficient for self-round-trip through this descriptor's
/// reader. External NILFS2 tools see a valid signature but a deep mount will
/// reject the image. Snapshot semantics are deliberately out of scope.</para>
/// </summary>
public sealed class Nilfs2FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
    IArchiveCreatable, IArchiveDefragmentable {
  public string Id => "Nilfs2";
  public string DisplayName => "NILFS2";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
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
  public string Description => "NILFS2 continuous-snapshot log-structured filesystem — WORM writer emits spec-compliant superblock + private compact directory (single-checkpoint, no DAT/segment log).";

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

  /// <summary>
  /// Emits a self-contained NILFS2 image (valid superblock + private directory)
  /// over <paramref name="output"/>. Round-trips through this descriptor's
  /// reader; kernel mount is out of scope (would require the full DAT tree +
  /// segment-log replay pipeline).
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var writer = new Nilfs2Writer();
    foreach (var (name, data) in FilesOnly(inputs))
      writer.AddFile(name, data);
    writer.WriteTo(output);
  }

  public void Defragment(Stream archive)
    => throw new NotSupportedException("Nilfs2 WORM writer is single-pass — defragmentation is N/A.");

  public void Defragment(Stream archive, DefragOptions options)
    => throw new NotSupportedException("Nilfs2 WORM writer is single-pass — defragmentation is N/A.");
}
